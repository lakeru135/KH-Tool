using System.Drawing;
using System.IO;
using System.Windows.Forms;
using KhBatchRunner.Forma;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Interop;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;

namespace KhBatchRunner.UI;

/// <summary>
/// "Schedule Interop" window — a <see cref="ScheduleRunWindow"/> sibling dedicated to just the
/// Batch Interop (AIT Model Checker) action, the same relationship <see cref="BatchInteropWindow"/>
/// has to <see cref="BatchRunWindow"/>. Top: a table of existing schedules, filtered to jobs whose
/// ONLY action is Batch Interop (mixed multi-action jobs that happen to include it are managed
/// from the general Schedule Run window instead — kept out of this table to avoid this window
/// silently dropping their other actions on Save). Below: model sets + ad-hoc models (Forma or
/// local), the checkset, a post-run disposition, destination, and recurrence. "Run now" executes
/// immediately; "Save schedule" persists the job and (re)creates its Windows Task Scheduler task.
/// No sync choice: Batch Interop is a Check — read-only, nothing is ever written back.
/// </summary>
public sealed class ScheduleInteropWindow : Form
{
    public enum ScheduleOutcome { None, RunNow, Create }

    private readonly ScheduleJobListPanel _jobs;
    private readonly ScheduleTimingPanel _timing;
    private readonly ModelSourcePanel _models;
    private readonly ComboBox _checksetSetCombo;
    private readonly TextBox _checksetBox;
    private readonly CheckBox _examinesLinksCheck;
    private readonly ComboBox _completionCombo;
    private readonly TextBox _folderBox;
    private readonly TextBox _projectLogBox;
    private readonly StatusLine _status;

    private readonly DataManagementClient _client;
    private readonly string _revitVersionNumber;
    private string? _editingId;

    public ScheduleOutcome Outcome { get; private set; } = ScheduleOutcome.None;
    public ScheduledJob? Job { get; private set; }

    public ScheduleInteropWindow(DataManagementClient client, string revitVersionNumber)
    {
        _client = client;
        _revitVersionNumber = revitVersionNumber;

        Text = "Schedule Interop";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        // The top row contains Name + Recurrence + Date + Time. At the old 760px width the Time
        // label and picker were clipped completely (confirmed by the standalone render harness),
        // making a schedule impossible to read or edit. Match the general Schedule Run width.
        ClientSize = new Size(880, 760);
        MinimumSize = new Size(896, 660);

        Button Mk(string text, int minW, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(minW, 0) };
            b.Click += (_, _) => onClick();
            return b;
        }
        Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) };

        // --- Schedule table (Interop-only jobs) ---
        _jobs = new ScheduleJobListPanel(
            IsInteropOnly, extraColumns: null, extraValues: null,
            onRowChosen: job => LoadJobIntoForm(job, editingExisting: true),
            setStatus: SetStatus);
        _jobs.NewRequested += NewSchedule;
        _jobs.DuplicateRequested += copy => LoadJobIntoForm(copy, editingExisting: false);

        // --- Name / recurrence / date / time / weekly days — see ScheduleTimingPanel ---
        _timing = new ScheduleTimingPanel();

        // --- Model sets / ad-hoc (no actions+scripts column — this window is Interop-only) ---
        // --- Model sets / ad-hoc — see ModelSourcePanel ---
        _models = new ModelSourcePanel(client, SetStatus, enableRangeCheck: false);

        // --- Checkset (saved sets + a .xml path or hosted URL) ---
        _checksetSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _checksetSetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_checksetSetCombo.SelectedItem is not Interop.ChecksetSet set) return;
            _checksetBox.Text = set.Path;
            // The flag belongs to the checkset, so picking a saved one brings its answer with it.
            _examinesLinksCheck.Checked = set.ExaminesLinks;
        };
        var saveChecksetSet = Mk("Save set", 80, SaveChecksetSet);
        var dupChecksetSet = Mk("Duplicate", 80, DuplicateChecksetSet);
        var deleteChecksetSet = Mk("Delete", 70, DeleteChecksetSet);
        _checksetBox = new TextBox();
        var checksetBrowse = Mk("Browse…", 80, BrowseForCheckset);
        _examinesLinksCheck = new CheckBox
        {
            Text = "This checkset examines the links themselves",
            AutoSize = true
        };
        Label linksNote = FormLayout.NoteLabel(this,
            "Leave clear and the run skips worksets a previous run proved hold nothing but links — " +
            "the checker never looks inside a linked model, and loading them is most of the open " +
            "time. Tick it if any check asks about the links as elements (pinned, named, on the " +
            "right workset), which needs every workset open.");

        // --- When finished (post-run disposition) ---
        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new NamedItem(BatchCompletion.CloseDocuments.ToString(), "Close files"));
        _completionCombo.Items.Add(new NamedItem(BatchCompletion.LeaveOpen.ToString(), "Leave files open"));
        _completionCombo.Items.Add(new NamedItem(BatchCompletion.CloseRevit.ToString(), "Close Revit when complete"));
        // A scheduled (headless) run always closes Revit; "Run now" honours whatever's picked.
        _completionCombo.SelectedIndex = 2;
        Label serialNote = FormLayout.NoteLabel(this, FormLayout.SplitNote);

        Label note = FormLayout.NoteLabel(this,
            "Read-only — runs the Interoperability Tools Model Checker checkset against each " +
            "model (Forma or local); nothing is synced or saved. Each model's HTML + Excel report " +
            "lands in an InteropReports_<time> folder inside the destination folder.");

        // --- Destination / log ---
        _folderBox = new TextBox();
        var browseBtn = Mk("Browse…", 80, BrowseFolder);
        _projectLogBox = new TextBox();

        // --- Action buttons (Run/Save on the left, Close on the right) ---
        var runNowBtn = Mk("Run now", 100, () => Commit(ScheduleOutcome.RunNow));
        var saveBtn = Mk("Save schedule", 130, () => Commit(ScheduleOutcome.Create));
        var closeBtn = new Button
        {
            Text = "Close", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 0), DialogResult = DialogResult.Cancel
        };
        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 0)
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.Controls.Add(FormLayout.FlowRow(runNowBtn, saveBtn), 0, 0);
        // In a FlowRow like its neighbours, so Close shares their 2 px top margin and sits level.
        actionRow.Controls.Add(FormLayout.FlowRow(closeBtn), 1, 0);

        var root = FormLayout.SingleColumn();
        FormLayout.AddFillRow(root, _jobs, 40);
        FormLayout.AddAutoRow(root, _timing);
        FormLayout.AddFillRow(root, _models, 60);
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Saved checkset:", _checksetSetCombo, saveChecksetSet, dupChecksetSet, deleteChecksetSet));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Checkset (.xml/URL):", _checksetBox, checksetBrowse));
        FormLayout.AddAutoRow(root, _examinesLinksCheck);
        FormLayout.AddAutoRow(root, linksNote);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("When finished:", _completionCombo));
        FormLayout.AddAutoRow(root, serialNote);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Destination folder:", _folderBox, browseBtn));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Project log name:", _projectLogBox));
        FormLayout.AddAutoRow(root, actionRow);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(root);
        Controls.Add(_status);
        CancelButton = closeBtn;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);
        serialNote.ForeColor = Theme.Dim(serialNote);
        linksNote.ForeColor = Theme.Dim(linksNote);
        // Fit to what the dialog actually holds, then refuse to be dragged below it — the idiom
        // CutModelWindow and eight others already use. A hardcoded ClientSize with a hand-guessed
        // minimum WIDER than it meant the dialog opened already over-full, and FormLayout's table
        // does not scroll: the percent rows were starved and the rows after them overlapped.
        // +_status.Height because the status strip docks outside root.
        ClientSize = new Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width),
            root.PreferredSize.Height + _status.Height);
        MinimumSize = Size;
        _jobs.RefreshGrid();

        // Populate saved checksets and restore the last-used one + destination folder.
        ReloadChecksetCombo(selectName: null);
        Interop.ChecksetSet? lastCheckset = Interop.ChecksetSetStore.TryLoadLastUsed();
        if (lastCheckset != null)
        {
            _checksetBox.Text = lastCheckset.Path;
            if (!string.IsNullOrEmpty(lastCheckset.Name)) SelectChecksetByName(lastCheckset.Name);
        }
        if (string.IsNullOrWhiteSpace(_folderBox.Text))
            _folderBox.Text = Settings.UserSettings.DefaultDestinationFolder;
    }

    // ---- saved checksets -------------------------------------------------------

    private void ReloadChecksetCombo(string? selectName)
    {
        _checksetSetCombo.Items.Clear();
        foreach (Interop.ChecksetSet set in Interop.ChecksetSetStore.LoadAll())
            _checksetSetCombo.Items.Add(set);
        if (selectName != null) SelectChecksetByName(selectName);
    }

    private void SelectChecksetByName(string name)
    {
        for (int i = 0; i < _checksetSetCombo.Items.Count; i++)
            if (((Interop.ChecksetSet)_checksetSetCombo.Items[i]).Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { _checksetSetCombo.SelectedIndex = i; return; }
    }

    private void SaveChecksetSet()
    {
        string name = _checksetSetCombo.Text.Trim();
        string path = _checksetBox.Text.Trim();
        if (name.Length == 0) { SetStatus("Type a name for the checkset (in the Saved checkset box), then Save set."); return; }
        if (path.Length == 0) { SetStatus("Choose a checkset (.xml file or hosted URL) before saving the set."); return; }
        if (Interop.ChecksetSetStore.Exists(name) &&
            MessageBox.Show(this, $"A checkset named '{name}' already exists. Overwrite it?", "Schedule Interop",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        Interop.ChecksetSetStore.Save(new Interop.ChecksetSet
        {
            Name = name, Path = path, ExaminesLinks = _examinesLinksCheck.Checked
        });
        ReloadChecksetCombo(selectName: name);
    }

    /// <summary>
    /// Forks the selected checkset entry to a new "&lt;name&gt; (copy)" and selects the copy — the
    /// starting point for the same checkset answered differently on
    /// <see cref="Interop.ChecksetSet.ExaminesLinks"/>, or for one hosted URL saved per project.
    /// </summary>
    private void DuplicateChecksetSet()
    {
        if (_checksetSetCombo.SelectedItem is not Interop.ChecksetSet set)
        { SetStatus("Select a saved checkset to duplicate."); return; }
        Interop.ChecksetSet copy = set.Clone();
        copy.Name = SetDuplication.UniqueName(
            set.Name, Interop.ChecksetSetStore.LoadAll().Select(c => c.Name));
        try
        {
            Interop.ChecksetSetStore.Save(copy);
        }
        catch (Exception ex)
        {
            SetStatus("Could not duplicate the checkset: " + ex.Message.Split('\n')[0]);
            return;
        }
        ReloadChecksetCombo(selectName: copy.Name);
    }

    private void DeleteChecksetSet()
    {
        if (_checksetSetCombo.SelectedItem is not Interop.ChecksetSet set)
        { SetStatus("Select a saved checkset to delete."); return; }
        if (MessageBox.Show(this, $"Delete checkset '{set.Name}'?", "Schedule Interop",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        Interop.ChecksetSetStore.Delete(set.Name);
        ReloadChecksetCombo(selectName: null);
        _checksetSetCombo.Text = "";
    }


    // ---- schedule table --------------------------------------------------------

    /// <summary>Only jobs whose sole action is Batch Interop — see the class doc for why mixed
    /// multi-action jobs are excluded from this table.</summary>
    private static bool IsInteropOnly(ScheduledJob j) =>
        j.ActionNames.Count == 1 && j.ActionNames[0] == BatchInteropAction.DisplayName;






    /// <summary>Internal so the round-trip gate can drive it — see TryBuildJob.</summary>
    internal void LoadJobIntoForm(ScheduledJob job, bool editingExisting)
    {
        _editingId = editingExisting ? job.Id : null;
        _timing.Load(job);

        _models.Load(job);

        _checksetBox.Text = job.InteropChecksetPath ?? "";
        _examinesLinksCheck.Checked = job.InteropChecksetExaminesLinks;
        SelectComboValue(_completionCombo, job.Completion.ToString());
        _folderBox.Text = job.DestinationFolder;
        _projectLogBox.Text = job.ProjectLogName;

        SetStatus(editingExisting ? $"Editing '{job.Name}'. Change fields and Save schedule." :
            $"Duplicated as '{job.Name}'. Adjust and Save schedule to create it.");
    }

    private static void SelectComboValue(ComboBox combo, string value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (((NamedItem)combo.Items[i]).Value == value) { combo.SelectedIndex = i; return; }
    }

    private void NewSchedule()
    {
        _jobs.Grid.ClearSelection();
        _editingId = null;
        _timing.Clear();
        _models.Clear();
        _checksetBox.Text = "";
        _examinesLinksCheck.Checked = false;
        _completionCombo.SelectedIndex = 2; // Close Revit — the natural default for a headless schedule
        _folderBox.Text = "";
        _projectLogBox.Text = "";

        SetStatus("New schedule — fill in the fields and Save schedule.");
    }



    // ---- ad-hoc models ---------------------------------------------------------



    /// <summary>Add models to the ad-hoc list, skipping any already present (local path or cloud GUID).</summary>




    // ---- checkset / folder / commit --------------------------------------------

    private void BrowseForCheckset()
    {
        string aitResources = Path.Combine(ModelCheckerInvoker.AitDirectory(_revitVersionNumber), "Resources");
        using var dlg = new OpenFileDialog
        {
            Filter = "Model Checker checkset (*.xml)|*.xml|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(aitResources)
                ? aitResources
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _checksetBox.Text = dlg.FileName;
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog();
        if (Directory.Exists(_folderBox.Text)) dlg.SelectedPath = _folderBox.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dlg.SelectedPath;
    }


    /// <summary>Reads the form into a <see cref="ScheduledJob"/>, or reports the first problem and
    /// returns false. Writes nothing — see <see cref="ScheduleCutWindow.TryBuildJob"/> for why the
    /// mapping was split from the persistence in 1.4.0. Note that this window's "remember the last
    /// checkset" write stayed behind in <see cref="Commit"/>: reading the form must not have side
    /// effects, or the round-trip gate would rewrite the user's stored checkset on every build.
    /// </summary>
    internal bool TryBuildJob(out ScheduledJob? job)
    {
        job = null;
        if (string.IsNullOrWhiteSpace(_timing.ScheduleName))
        {
            SetStatus("Enter a schedule name.");
            return false;
        }
        List<string> modelSets = _models.CheckedNames;
        if (!_models.HasAnySource)
        {
            SetStatus("Check at least one model set or add an ad-hoc model.");
            return false;
        }
        string checkset = _checksetBox.Text.Trim();
        bool isUrl = checkset.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (checkset.Length == 0 || (!isUrl && !File.Exists(checkset)))
        {
            SetStatus(checkset.Length == 0
                ? "Choose a checkset (.xml file or hosted URL)."
                : $"Checkset not found: {checkset}");
            return false;
        }
        var completion = (BatchCompletion)Enum.Parse(typeof(BatchCompletion),
            ((NamedItem)_completionCombo.SelectedItem!).Value);
        ScheduleRecurrence recurrence = _timing.Recurrence;
        List<DayOfWeek> weeklyDays = _timing.WeeklyDays;
        string time = _timing.Time;
        DateTime? startDate = _timing.StartDate;

        job = new ScheduledJob
        {
            Id = _editingId ?? Guid.NewGuid().ToString("N"),
            Name = _timing.ScheduleName.Trim(),
            ModelSetNames = modelSets,
            AdHocModels = _models.AdHocModels,
            RunItems = new List<RunItem> { new(BatchInteropAction.DisplayName, RunItemKind.Action) },
            InteropChecksetPath = checkset,
            InteropChecksetExaminesLinks = _examinesLinksCheck.Checked,
            // Check never persists — NoSync is documentation only, same as BatchInteropCommand.
            Sync = SyncMode.NoSync,
            Completion = completion,
            // Split when the disposition allows it, same rule as every other schedule window. The
            // shape is decided at RUN time (GroupSizeAuto) from the resolved model count and the
            // machine, at the heavier Interop weight — see InstanceBudget.WeightFor. A job saved
            // here can still refuse to split when it fires, if that PC's AIT year is unverified
            // (Interop.InteropSplitPolicy).
            GroupSize = SplitAllowed() ? ScheduledJob.GroupSizeAuto : 0,
            DestinationFolder = _folderBox.Text.Trim(),
            ProjectLogName = _projectLogBox.Text.Trim(),
            Time = time,
            Recurrence = recurrence,
            StartDate = startDate,
            WeeklyDays = weeklyDays
        };
        return true;
    }

    private void Commit(ScheduleOutcome outcome)
    {
        if (!TryBuildJob(out ScheduledJob? built)) return;
        Job = built!;

        Interop.ChecksetSetStore.SaveLastUsed(new Interop.ChecksetSet
        {
            Name = (_checksetSetCombo.SelectedItem as Interop.ChecksetSet)?.Name ?? "",
            Path = Job.InteropChecksetPath ?? "",
            ExaminesLinks = Job.InteropChecksetExaminesLinks
        });

        // "Save schedule" saves IN PLACE — window stays open (grid refreshes, status reports the
        // result) and the user closes it; same fix as ScheduleRunWindow.
        if (outcome == ScheduleOutcome.Create)
        {
            (bool ok, string msg) = ScheduleSaver.SaveAndRegister(Job);
            if (ok)
            {
                _editingId = Job.Id;
                _jobs.RefreshGrid();
            }
            SetStatus(ok ? msg : "SAVE FAILED: " + msg);
            return;
        }

        Outcome = outcome;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetStatus(string text)
    {
        // Reachable while the constructor is still running: ModelSourcePanel takes this as a
        // callback before the status strip below it exists. The panel no longer calls back
        // synchronously (see ModelSetAccess.BeginRefresh), so this is the second lock on a door
        // that cost four windows a NullReferenceException in 1.5.0 — a dropped status line is a
        // nuisance, a throw here is a dialog that will not open at all.
        if (_status is null) return;
        _status.SetMessage(text);
    }

    /// <summary>Whether the chosen disposition permits a parallel split — see
    /// <see cref="FormLayout.SplitAllowed"/>.</summary>
    private bool SplitAllowed() =>
        FormLayout.SplitAllowed(((NamedItem?)_completionCombo.SelectedItem)?.Value);

    /// <summary>Combo item pairing a stored value with a display label.</summary>


}
