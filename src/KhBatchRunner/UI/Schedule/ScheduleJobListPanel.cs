using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using KhBatchRunner.Schedule;

// Folder UI\Schedule\ groups the shared schedule panels; the NAMESPACE stays KhBatchRunner.UI
// deliberately. A nested UI.Schedule namespace shadows KhBatchRunner.Schedule for every file in
// UI, so ScheduledJob silently stops resolving - which is exactly what it did when tried.
namespace KhBatchRunner.UI;

/// <summary>
/// The table of existing schedules that sits at the top of every Schedule* window, with its
/// New / Duplicate / Delete buttons.
///
/// <para><b>Why this is shared (1.4.0).</b> It existed four times — ScheduleRunWindow,
/// ScheduleInteropWindow and two since-retired windows — and the copies had DRIFTED.
/// <c>SelectedJob</c>, <c>DuplicateSelected</c> and <c>DeleteSelectedSchedule</c> were still
/// byte-identical in all four; <c>RefreshGrid</c> was not. Two of them learned to keep the user's
/// column sort and selected row across a rebind. Interop and Cut never did, so on those two every
/// save silently threw you back to file order and lost your place. Nobody decided that: the fix
/// landed in two copies and stopped.</para>
///
/// <para>That is the whole argument for this file. The behaviour below is the one the two better
/// copies had, so collapsing them FIXES Interop and Cut — the single intended behaviour change in
/// an otherwise structural release.</para>
/// </summary>
internal sealed class ScheduleJobListPanel : TableLayoutPanel
{
    private readonly DataGridView _grid;
    private readonly Func<ScheduledJob, bool> _filter;
    private readonly Func<ScheduledJob, string[]>? _extraValues;
    private readonly Action<ScheduledJob> _onRowChosen;
    private readonly Action<string> _setStatus;

    /// <summary>
    /// The selected jobs as they stood the instant before the current click landed on the "On"
    /// column, or null when the click was anywhere else. See the CellMouseDown handler for why
    /// this cannot simply be read back in CellClick.
    /// </summary>
    private IReadOnlyList<ScheduledJob>? _selectionBeforeClick;

    /// <param name="filter">Which saved jobs this window is willing to manage. A window that listed
    /// jobs it cannot represent would silently drop their other actions on save.</param>
    /// <param name="extraColumns">Columns inserted between "Name" and "Schedule" — only the retired action-picker window
    /// uses them, for Action and Input set.</param>
    /// <param name="extraValues">Their values, in the same order.</param>
    /// <param name="onRowChosen">Load this job into the form. The window owns that; the list does not
    /// know what a form is.</param>
    /// <param name="setStatus">Where "Pick a schedule to delete." goes.</param>
    public ScheduleJobListPanel(
        Func<ScheduledJob, bool> filter,
        (string Key, string Header)[]? extraColumns,
        Func<ScheduledJob, string[]>? extraValues,
        Action<ScheduledJob> onRowChosen,
        Action<string> setStatus)
    {
        _filter = filter;
        _extraValues = extraValues;
        _onRowChosen = onRowChosen;
        _setStatus = setStatus;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            // 1.4.7: Enable / Disable / Delete act on the whole selection. Ctrl/Shift-click builds
            // one, which is why LoadRowIntoForm below now checks the modifier keys.
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        // Armed or paused, first because it is the thing you scan the list for. The grid stays
        // ReadOnly and the tick is applied from CellClick instead: that keeps DataGridView's edit
        // mode (and its commit/cancel lifecycle) out of a path that has to talk to schtasks, and
        // puts the toggle in ONE method the buttons call too.
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = EnabledColumn,
            HeaderText = "On",
            FillWeight = 22
        });
        _grid.Columns.Add("Name", "Name");
        foreach ((string key, string header) in extraColumns ?? Array.Empty<(string, string)>())
            _grid.Columns.Add(key, header);
        _grid.Columns.Add("Schedule", "Schedule");
        _grid.Columns.Add("NextRun", "Next run");
        _grid.Columns.Add("LastRun", "Last run");
        // What Windows recorded for the task itself — see ScheduleDisplay.LastResult.
        _grid.Columns.Add("LastResult", "Last result");

        // Header-click sorting. Only two windows set this before 1.4.0; the other two could not be
        // sorted at all, which is the same drift as the lost sort below.
        foreach (DataGridViewColumn c in _grid.Columns)
            c.SortMode = DataGridViewColumnSortMode.Automatic;

        // The selection AS IT WAS before this click, captured because the click is about to destroy
        // it. DataGridView updates the selection during OnMouseDown, and CellClick fires afterwards
        // — so a plain click on one row of a multi-row selection has already collapsed that
        // selection to the single clicked row by the time the handler below runs. Reading
        // SelectedJobs() there would always see exactly one row and the tick would silently act on
        // one schedule while the user was looking at five selected.
        _grid.CellMouseDown += (_, e) =>
        {
            _selectionBeforeClick = e.RowIndex >= 0 && e.ColumnIndex >= 0 &&
                                    _grid.Columns[e.ColumnIndex].Name == EnabledColumn
                ? SelectedJobs()
                : null;
        };

        _grid.CellClick += (_, e) =>
        {
            IReadOnlyList<ScheduledJob>? before = _selectionBeforeClick;
            _selectionBeforeClick = null;

            if (e.RowIndex < 0) return;
            if (e.ColumnIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == EnabledColumn)
            {
                // Ctrl/Shift on the tick column BUILDS a selection rather than toggling. Without
                // this the "On" column was the one column you could not Ctrl-click to extend a
                // selection with — it toggled the job instead, which is both surprising and, on a
                // schedule, consequential.
                if ((Control.ModifierKeys & (Keys.Control | Keys.Shift)) != 0) return;

                if (_grid.Rows[e.RowIndex].Tag is not ScheduledJob clicked) return;

                // Act on the whole selection when the clicked row was part of it — matching the
                // Enable/Disable buttons — and on the clicked row alone otherwise. The target state
                // is the clicked row's NEW state, applied to all of them, so a mixed selection ends
                // up agreeing with the tick the user just set rather than each row flipping its own
                // way. RefreshGrid restores a multi-row selection by id, so it survives.
                bool clickedWasSelected = before != null && before.Any(j => j.Id == clicked.Id);
                ApplyEnabled(clickedWasSelected ? before! : new[] { clicked }, !clicked.Enabled);
                return;
            }
            // Loading the editor while a selection is being EXTENDED would fight the user for it —
            // and the row they Ctrl-clicked is not the one they want to edit.
            if ((Control.ModifierKeys & (Keys.Control | Keys.Shift)) != 0) return;
            LoadRowIntoForm(e.RowIndex);
        };

        Buttons = FormLayout.FlowRow(
            Mk("New schedule", 110, () => NewRequested?.Invoke()),
            Mk("Duplicate", 100, DuplicateSelected),
            Mk("Enable", 80, () => ApplyEnabled(SelectedJobs(), true)),
            Mk("Disable", 80, () => ApplyEnabled(SelectedJobs(), false)),
            Mk("Delete schedule", 120, DeleteSelectedSchedule));

        Dock = DockStyle.Fill;
        ColumnCount = 1;
        RowCount = 2;
        Margin = new Padding(0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(_grid, 0, 0);
        Controls.Add(Buttons, 0, 1);
    }

    /// <summary>Raised by "New schedule". The window clears its own fields — the list has no idea
    /// what they are.</summary>
    public event Action? NewRequested;

    /// <summary>The button row, exposed so a window can keep its existing layout arrangement.</summary>
    public FlowLayoutPanel Buttons { get; }

    /// <summary>The grid itself, for a window that needs to size or place it specifically.</summary>
    public DataGridView Grid => _grid;

    /// <summary>Name of the armed/paused checkbox column.</summary>
    private const string EnabledColumn = "Enabled";

    public ScheduledJob? SelectedJob() =>
        _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as ScheduledJob : null;

    /// <summary>
    /// Every selected job, in the order shown. Enable / Disable / Delete work over this;
    /// Duplicate and row-load deliberately stay on <see cref="SelectedJob"/> — duplicating five
    /// schedules at once has no meaning, and there is one editor to load into.
    /// </summary>
    public IReadOnlyList<ScheduledJob> SelectedJobs() =>
        _grid.SelectedRows.Cast<DataGridViewRow>()
            .OrderBy(r => r.Index)
            .Select(r => r.Tag as ScheduledJob)
            .Where(j => j != null)
            .Select(j => j!)
            .ToList();

    private static Button Mk(string text, int minW, Action onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(minW, 0)
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// Rebinds the table from disk, KEEPING the user's column sort and selected row.
    ///
    /// <para>Both of those are the point. Before 1.4.0 two of the four windows reset to file order
    /// and cleared the selection on every save, delete or duplicate — so editing a schedule threw
    /// you back to the top of an unsorted list, in exactly the windows where you were most likely
    /// to be working through several.</para>
    /// </summary>
    public void RefreshGrid()
    {
        string? sortCol = _grid.SortedColumn?.Name;
        ListSortDirection dir = _grid.SortOrder == SortOrder.Descending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        // A SET of ids, not one: a bulk action rebinds the grid, and restoring a single row would
        // collapse the very selection it just acted on.
        var selectedIds = SelectedJobs().Select(j => j.Id).ToHashSet(StringComparer.Ordinal);

        _grid.Rows.Clear();
        Dictionary<string, int> taskResults = TaskSchedulerGateway.LastRunResults();
        foreach (ScheduledJob j in ScheduleStore.LoadAll().Where(_filter))
        {
            var cells = new List<object?> { j.Enabled, j.Name };
            if (_extraValues != null) cells.AddRange(_extraValues(j));
            cells.Add(ScheduleDisplay.DescribeSchedule(j));
            cells.Add(ScheduleDisplay.DescribeNextRun(j));
            cells.Add(j.LastRunAt.HasValue ? ScheduleDisplay.Local(j.LastRunAt.Value) : ScheduleDisplay.NoNextRun);
            cells.Add(ScheduleDisplay.LastResult(taskResults, j.TaskName));

            _grid.Rows.Add(cells.ToArray());
            _grid.Rows[_grid.Rows.Count - 1].Tag = j;
        }

        DataGridViewColumn? col = sortCol != null ? _grid.Columns[sortCol] : _grid.Columns["Name"];
        if (col != null && col.SortMode != DataGridViewColumnSortMode.NotSortable && _grid.Rows.Count > 0)
            _grid.Sort(col, sortCol != null ? dir : ListSortDirection.Ascending);

        if (selectedIds.Count > 0)
        {
            _grid.ClearSelection();
            foreach (DataGridViewRow row in _grid.Rows)
                if (row.Tag is ScheduledJob j && selectedIds.Contains(j.Id)) row.Selected = true;
        }
    }

    /// <summary>
    /// Arms or pauses every given schedule, and reports what Windows actually did.
    ///
    /// <para>The stored flag is written ONLY for a job whose task Windows accepted the change for,
    /// so the tick in this grid can never claim a state Task Scheduler does not hold. A job whose
    /// task was never registered fails here and is named — worth seeing, rather than showing a tick
    /// that means nothing.</para>
    /// </summary>
    private void ApplyEnabled(IReadOnlyList<ScheduledJob> jobs, bool enabled)
    {
        string verb = enabled ? "enabled" : "disabled";
        if (jobs.Count == 0)
        {
            _setStatus($"Pick one or more schedules to {(enabled ? "enable" : "disable")}.");
            return;
        }

        int done = 0;
        string? firstProblem = null;
        foreach (ScheduledJob job in jobs)
        {
            (bool ok, string msg) = TaskSchedulerGateway.SetEnabled(job, enabled);
            if (ok)
            {
                job.Enabled = enabled;
                ScheduleStore.Save(job);
                done++;
            }
            else
            {
                firstProblem ??= $"'{job.Name}': {FirstLine(msg)}";
            }
        }

        RefreshGrid();
        _setStatus(firstProblem == null
            ? $"{Count(done)} {verb}."
            : $"{done} of {jobs.Count} {verb}. {firstProblem}");
    }

    /// <summary>schtasks reports several lines; the status label has room for the first.</summary>
    private static string FirstLine(string message) =>
        message.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim()
        ?? message;

    private static string Count(int n) => n == 1 ? "1 schedule" : $"{n} schedules";

    private void LoadRowIntoForm(int rowIndex)
    {
        if (_grid.Rows[rowIndex].Tag is not ScheduledJob job) return;
        // A plain click has already made this the only selected row; forcing it again would undo
        // a Ctrl-click the handler above has decided to let through.
        _onRowChosen(job);
    }

    private void DuplicateSelected()
    {
        ScheduledJob? job = SelectedJob();
        if (job == null) { _setStatus("Pick a schedule to duplicate."); return; }
        DuplicateRequested?.Invoke(job.Duplicate());
    }

    /// <summary>Raised with a fresh copy to load as a NEW schedule (never editing the original).</summary>
    public event Action<ScheduledJob>? DuplicateRequested;

    /// <summary>
    /// Deletes every selected schedule, task first then stored job — and asks first.
    ///
    /// <para>There was no confirmation at all before 1.4.7, when only one row could ever be
    /// selected. With Ctrl-click there is nothing between a slipped modifier and several schedules
    /// gone, and a schedule has no undo: the definition, its models and its timing are simply no
    /// longer on the machine.</para>
    /// </summary>
    private void DeleteSelectedSchedule()
    {
        IReadOnlyList<ScheduledJob> jobs = SelectedJobs();
        if (jobs.Count == 0) { _setStatus("Pick a schedule to delete."); return; }

        string what = jobs.Count == 1
            ? $"Delete schedule '{jobs[0].Name}'?"
            : $"Delete {jobs.Count} schedules?";
        if (MessageBox.Show(this, what + " This cannot be undone.", "Delete schedule",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        int done = 0;
        string? firstProblem = null;
        foreach (ScheduledJob job in jobs)
        {
            (bool ok, string msg) = TaskSchedulerGateway.Delete(job);
            // The stored job goes either way: leaving it behind after its task is gone is how a row
            // appears in this list that nothing will ever run.
            ScheduleStore.Delete(job.Id);
            if (ok) done++;
            else firstProblem ??= $"'{job.Name}': {FirstLine(msg)}";
        }

        RefreshGrid();
        _setStatus(firstProblem == null
            ? $"Deleted {Count(done)}."
            : $"Deleted {done} of {jobs.Count}. {firstProblem}");
    }
}
