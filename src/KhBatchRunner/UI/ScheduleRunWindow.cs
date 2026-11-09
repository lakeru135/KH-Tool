using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using KhBatchRunner.Forma;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;
using KhBatchRunner.Scripts;

namespace KhBatchRunner.UI;

/// <summary>
/// "Schedule Run" window. Top: a table of existing schedules (Name / Schedule / Next run /
/// Last run) — click a row to load it below for editing; New/Duplicate/Delete act on the
/// selection. Below: the job editor — name, one or more model sets plus ad-hoc local models,
/// one or more built-in actions and/or script sets, a post-run disposition, destination,
/// and recurrence (with day-of-week picks for Weekly). "Run now" executes immediately;
/// "Save schedule" persists the job and (re)creates its Windows Task Scheduler task.
/// </summary>
public sealed class ScheduleRunWindow : Form
{
    public enum ScheduleOutcome { None, RunNow, Create }

    private readonly ScheduleJobListPanel _jobs;
    private readonly ScheduleTimingPanel _timing;
    private readonly ModelSourcePanel _models;
    private readonly CheckedListBox _actionsScriptsList;
    private readonly ComboBox _initiateSetCombo;
    private readonly ComboBox _cutModelSetCombo;
    private readonly ComboBox _relinkSetCombo;
    private readonly ComboBox _correctWorksetSetCombo;
    private readonly ComboBox _exportNwcSetCombo;
    private readonly ComboBox _exportIfcSetCombo;
    private readonly ComboBox _fileLogSetCombo;
    private readonly ComboBox _dataExtractSetCombo;
    private readonly ComboBox _syncCombo;
    private readonly ComboBox _completionCombo;
    private readonly TextBox _folderBox;
    private readonly TextBox _projectLogBox;
    private readonly Label _countLabel;
    private readonly StatusLine _status;

    private readonly DataManagementClient _client;
    private string? _editingId;

    public ScheduleOutcome Outcome { get; private set; } = ScheduleOutcome.None;
    public ScheduledJob? Job { get; private set; }

    public ScheduleRunWindow(DataManagementClient client)
    {
        _client = client;

        Text = "Schedule Run";
        StartPosition = FormStartPosition.CenterScreen;
        // Scale the whole dialog with Revit's DPI (positions, sizes, fonts together).
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(880, 820);
        // Barely below the natural size, because this dialog cannot survive being squeezed: the
        // fixed rows (schedule grid, the name/recurrence line, eight labelled option rows, the
        // button bar) claim their height first, so the three list columns are what absorbs the
        // shortfall. At the old 820x720 the ad-hoc list and the actions list had collapsed to
        // nothing and "Time:" was clipped off the right edge — a dialog you could drag into being
        // unusable. A render at MinimumSize is what showed it.
        MinimumSize = new Size(896, 800);

        Button Mk(string text, int minW, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(minW, 0) };
            b.Click += (_, _) => onClick();
            return b;
        }
        Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) };

        var open = new Diagnostics.OpenTimer("Schedule Run");

        // --- Schedule table ---
        _jobs = new ScheduleJobListPanel(
            _ => true, extraColumns: null, extraValues: null,
            onRowChosen: job => LoadJobIntoForm(job, editingExisting: true),
            setStatus: SetStatus);
        _jobs.NewRequested += NewSchedule;
        _jobs.DuplicateRequested += copy => LoadJobIntoForm(copy, editingExisting: false);

        // --- Name / recurrence / date / time / weekly days — see ScheduleTimingPanel ---
        _timing = new ScheduleTimingPanel();

        // --- Model sets / ad-hoc / actions+scripts ---
        open.Mark("schedule grid");
        // --- Model sets / ad-hoc — see ModelSourcePanel. 54.4/45.6 reproduces the 37/31 split
        // these two columns had when they sat in this window's own three-column table.
        _models = new ModelSourcePanel(client, SetStatus,
            onChanged: UpdateSelectionUi, enableRangeCheck: true, modelSetsPercent: 54.4f);
        open.Mark("model sets");
        // This column is narrower than the other two — let the row wrap to a second line
        // instead of clipping the last button when all three don't fit on one (Dock=Fill so
        // WrapContents has an actual width to wrap against; without it the panel just grows
        // to fit its children in one row and overflows the column instead).

        // HorizontalScrollbar because these entries carry a trailing explanation — "Initiate to Forma
        // (publishes new cloud models)" — and the column is a third of the dialog, so the useful
        // half of the label was cut off with no way to read it.
        _actionsScriptsList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
            HorizontalScrollbar = true
        };
        // Via HostServices so this window works in a host that has no Revit action registry to
        // enumerate — the standalone controller supplies the same list over its token broker.
        foreach (ActionInfo a in HostServices.ListActions())
            _actionsScriptsList.Items.Add(new ActionOrScriptItem(a.Name, isScriptSet: false,
                worksets: a.Worksets));
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Actions.ActionNames.InitiateToForma, isScriptSet: false, isInitiate: true,
            worksets: Actions.SyntheticActionPostures.InitiateToForma));
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Actions.ActionNames.CutModel, isScriptSet: false, isCutModel: true,
            worksets: Actions.SyntheticActionPostures.CutModel));
        // Internal Relink + Correct Workset (2.0.0: moved here from the retired separate schedule
        // window). Both write to the model; each plans its own worksets.
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Actions.ActionNames.InternalRelink, isScriptSet: false, isRelink: true,
            worksets: WorksetPosture.PlansOwn));
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Actions.ActionNames.CorrectWorkset, isScriptSet: false, isCorrectWorkset: true,
            worksets: WorksetPosture.PlansOwn));
        // Export NWC — carries its own view name and output folder, so it is offered the same way.
        // The three synthetic items read their posture statically because they are not in the
        // registry and their real constructors need input sets — see ExportNwcAction.DeclaredWorksets.
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Actions.ActionNames.ExportNwc, isScriptSet: false, isExportNwc: true,
            worksets: Actions.SyntheticActionPostures.ExportNwc));
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Actions.ActionNames.ExportIfcData, isScriptSet: false, isExportIfc: true,
            worksets: Actions.SyntheticActionPostures.ExportIfcData));
        // Get File Log is a REST-only step (no model opened): it pulls the file log for the folders
        // of the "Get File Log set" chosen below. Add it as a checkable item like the others.
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Schedule.FileLogStep.DisplayName, isScriptSet: false, isFileLog: true));
        // Get Data Extract is the second REST-only step: it downloads the newest finished run of an
        // Forma Data Connector extract named by the "Get Data Extract set" chosen below.
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Schedule.DataExtractStep.DisplayName, isScriptSet: false, isDataExtract: true));
        foreach (ScriptSet s in ScriptSetStore.LoadAll())
            _actionsScriptsList.Items.Add(new ActionOrScriptItem(s.Name, isScriptSet: true, refId: s.Id));
        PinRestOnlyFirst();
        // Reorder buttons — checklist order is run order (interleaved actions + script sets).
        var actionButtons = FormLayout.FlowRow(
            Mk("Move ▲", 70, () => MoveActionScript(-1)),
            Mk("Move ▼", 70, () => MoveActionScript(1)));

        // Seeds the Initiate pass's publish target (only used when the Initiate item is checked).
        _initiateSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (InitiateInputSet s in InitiateSetStore.LoadAll())
            _initiateSetCombo.Items.Add(new InitiateSetItem(s));
        if (_initiateSetCombo.Items.Count > 0) _initiateSetCombo.SelectedIndex = 0;

        // Seeds the Cut model pass's publish target + site code (only used when Cut model is checked).
        _cutModelSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (CutModelInputSet s in CutModelSetStore.LoadAll())
            _cutModelSetCombo.Items.Add(new CutModelSetItem(s));
        if (_cutModelSetCombo.Items.Count > 0) _cutModelSetCombo.SelectedIndex = 0;

        // Seeds the Internal Relink pass (old -> new code + target folders).
        _relinkSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (RelinkInputSet s in RelinkSetStore.LoadAll())
            _relinkSetCombo.Items.Add(new RelinkSetItem(s));
        if (_relinkSetCombo.Items.Count > 0) _relinkSetCombo.SelectedIndex = 0;

        // Seeds the Correct Workset pass (rules + Check/Fix mode).
        _correctWorksetSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (CorrectWorksetInputSet s in CorrectWorksetSetStore.LoadAll())
            _correctWorksetSetCombo.Items.Add(new CorrectWorksetSetItem(s));
        if (_correctWorksetSetCombo.Items.Count > 0) _correctWorksetSetCombo.SelectedIndex = 0;

        // Seeds the Export NWC pass's options (only used when Export NWC is checked).
        _exportNwcSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (ExportNwcInputSet s in ExportNwcSetStore.LoadAll())
            _exportNwcSetCombo.Items.Add(new ExportNwcSetItem(s));
        if (_exportNwcSetCombo.Items.Count > 0) _exportNwcSetCombo.SelectedIndex = 0;

        _exportIfcSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (ExportIfcInputSet s in ExportIfcSetStore.LoadAll())
            _exportIfcSetCombo.Items.Add(new ExportIfcSetItem(s));
        if (_exportIfcSetCombo.Items.Count > 0) _exportIfcSetCombo.SelectedIndex = 0;

        // Seeds the Get File Log step: WHICH FOLDERS as well as which files (only used when that
        // item is checked). The folders live in the set because a schedule that inferred them from
        // the job's model sets could pull nothing and never say so.
        _fileLogSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (FileLogInputSet s in FileLogSetStore.LoadAll())
            _fileLogSetCombo.Items.Add(new FileLogSetItem(s));
        if (_fileLogSetCombo.Items.Count > 0) _fileLogSetCombo.SelectedIndex = 0;

        // Seeds the Get Data Extract step: WHICH account and extract (only used when that item is
        // checked). Same rule as the file log — the target lives in the set so a schedule cannot
        // quietly download the wrong account's data.
        _dataExtractSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (DataExtractInputSet s in DataExtractSetStore.LoadAll())
            _dataExtractSetCombo.Items.Add(new DataExtractSetItem(s));
        if (_dataExtractSetCombo.Items.Count > 0) _dataExtractSetCombo.SelectedIndex = 0;

        // A floor under each column and under the row that holds them, so the two percent rows in
        // this dialog cannot absorb every shortfall and squeeze the lists into uselessness. Without
        // it the ad-hoc and actions columns rendered ONE ROW HIGH (measured: 28px, and 1px at the
        // dialog's own minimum size) while the dialog still looked like it worked. BatchRunWindow has
        // carried exactly this pair of floors, for exactly this reason, since it was written.
        _actionsScriptsList.MinimumSize = new Size(0, 130);

        var lists = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            // The row must hold a column WHOLE: its heading (~20px), the 130px list floor above, and
            // its button row — which for the ad-hoc column wraps onto two lines (~76px). 190 was
            // enough before the floors existed and is not now; too small a row here just moves the
            // squeeze from the list to the buttons underneath it.
            Margin = new Padding(0), MinimumSize = new Size(0, 240)
        };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lists.Controls.Add(_models, 0, 0);
        lists.Controls.Add(FormLayout.TitledColumn("Actions + script sets", _actionsScriptsList, actionButtons), 1, 0);

        // --- Model treatment (whether write actions/scripts persist their changes) ---
        _syncCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _syncCombo.Items.Add(new NamedItem(SyncMode.Sync.ToString(), "Sync changes to central"));
        _syncCombo.Items.Add(new NamedItem(SyncMode.NoSync.ToString(), "Don't sync (dry run)"));
        _syncCombo.SelectedIndex = 1; // Dry run (default) — the irreversible option is opt-in.
                                      // A saved job's own setting still wins; see LoadJob.

        // --- When finished (post-run disposition) ---
        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new NamedItem(BatchCompletion.CloseDocuments.ToString(), "Close files"));
        _completionCombo.Items.Add(new NamedItem(BatchCompletion.LeaveOpen.ToString(), "Leave files open"));
        _completionCombo.Items.Add(new NamedItem(BatchCompletion.CloseRevit.ToString(), "Close Revit when complete"));
        // A scheduled (headless) run always closes Revit; "Run now" honours whatever's picked.
        _completionCombo.SelectedIndex = 2;
        _completionCombo.SelectedIndexChanged += (_, _) => OnSplitConditionChanged();

        Label splitNote = FormLayout.NoteLabel(this, FormLayout.SplitNote);

        // Open mode is fixed by each action's category now (no picker) — just describe it.
        Label note = FormLayout.NoteLabel(this,
            "Checks always run Read-only; model treatment applies to actions/scripts that modify the model. All worksets closed.");

        // --- Destination / log ---
        _folderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        var browseBtn = Mk("Browse…", 80, BrowseFolder);
        _projectLogBox = new TextBox();

        // Live "N selected" count under the lists. A NoteLabel, not a plain AutoSize one: with a
        // split preview and the folder-backed caveat appended, SelectionSummary.Describe produces a
        // sentence far wider than this dialog, and a plain label just loses the right-hand end of it.
        _countLabel = FormLayout.NoteLabel(this, "");
        _countLabel.Anchor = AnchorStyles.Left;
        _countLabel.Margin = new Padding(3, 2, 3, 4);

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

        // Two flexible areas (schedule table + the three lists) share the leftover height;
        // everything else auto-sizes so nothing overlaps at high DPI.
        var root = FormLayout.SingleColumn();
        // 40/60, not the 45/55 it was until 1.5.1.3. The two percent rows split whatever the auto
        // rows leave over, and every option row added to this dialog shrinks that leftover for both
        // of them — "Get Data Extract set:" was the one that made the share too small for the lists
        // row's own 240px floor, so the lists panel painted over the "Selected: …" line beneath it
        // at the dialog's minimum size. Shifting five points from the schedule grid (which degrades
        // gracefully: one fewer visible row) to the lists (which do not: the floor is there because
        // they had rendered one row high) buys the margin back. The layout gate is what measures it.
        FormLayout.AddFillRow(root, _jobs, 40);
        FormLayout.AddAutoRow(root, _timing);
        FormLayout.AddFillRow(root, lists, 60);
        FormLayout.AddAutoRow(root, _countLabel);
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Initiate input set:", _initiateSetCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Cut model input set:", _cutModelSetCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Internal Relink set:", _relinkSetCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Correct Workset set:", _correctWorksetSetCombo));
        // Export NWC and Export IFC + Data share ONE row. A separate row for IFC is exactly the kind
        // of addition the 40/60 note above warns about: it starved the lists below their 240px floor
        // at the dialog's minimum size, and no percent split cleared both sizes in the layout gate.
        var exportRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, RowCount = 1, Margin = new Padding(0)
        };
        exportRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        exportRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        exportRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _exportNwcSetCombo.Dock = DockStyle.Fill;
        _exportIfcSetCombo.Dock = DockStyle.Fill;
        exportRow.Controls.Add(_exportNwcSetCombo, 0, 0);
        exportRow.Controls.Add(new Label
        {
            Text = "IFC + Data:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(9, 6, 6, 3)
        }, 1, 0);
        exportRow.Controls.Add(_exportIfcSetCombo, 2, 0);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Export NWC options:", exportRow));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Get File Log set:", _fileLogSetCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Get Data Extract set:", _dataExtractSetCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Model treatment:", _syncCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("When finished:", _completionCombo));
        FormLayout.AddAutoRow(root, splitNote);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Destination folder:", _folderBox, browseBtn));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Project log name:", _projectLogBox));
        FormLayout.AddAutoRow(root, actionRow);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(root);
        Controls.Add(_status);
        CancelButton = closeBtn;

        // Shift+click range checking on both check-lists; keep the count + the Initiate/Cut combos'
        // enabled state in sync (ItemCheck fires before the state updates, so defer with BeginInvoke).
        CheckListBehavior.EnableRangeCheck(_actionsScriptsList);
        // Guard on IsHandleCreated: PreloadDraft → LoadJobIntoForm checks items BEFORE ShowDialog
        // (no handle yet); LoadJobIntoForm calls UpdateSelectionUi directly, so nothing is missed.
        _models.ModelSets.ItemCheck += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)UpdateSelectionUi); };
        _actionsScriptsList.ItemCheck += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)UpdateSelectionUi); };

        open.Mark("layout");
        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);

        // Fit to what the dialog actually holds, then refuse to be dragged below it — the idiom
        // CutModelWindow and eight others already use. The hardcoded 880x820 with a hand-guessed
        // 896x800 minimum is what let the lists be starved: a minimum WIDER than the natural size
        // meant the dialog opened already over-full, and FormLayout's table does not scroll.
        // +_status.Height because the status strip docks outside root.
        ClientSize = new Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width),
            root.PreferredSize.Height + _status.Height);

        MinimumSize = Size;
        open.Mark("theme");
        _jobs.RefreshGrid();
        open.Mark("RefreshGrid");
        OnSplitConditionChanged();
        UpdateSelectionUi();
        open.Done();
    }

    /// <summary>Live selected-count label; the Initiate/Cut model/Export NWC input-set combos are enabled only when
    /// their synthetic item is checked (grayed out otherwise).</summary>
    private void UpdateSelectionUi()
    {
        // Reachable from control events raised while the constructor is still wiring things up,
        // and the label is built after those controls.
        if (_countLabel is null) return;

        var checkedActions = _actionsScriptsList.CheckedItems.Cast<ActionOrScriptItem>().ToList();

        _countLabel.Text = SelectionSummary.Describe(
            _models.ModelSets.CheckedItems.Cast<ModelSetItem>().Select(i => i.Preset).ToList(),
            _models.AdHocModels,
            _actionsScriptsList.CheckedItems.Count,
            SplitAllowed()) + OrderingHint(checkedActions);
        CheckListBehavior.SetEnabledOrBlank(_initiateSetCombo, checkedActions.Any(a => a.IsInitiate));
        CheckListBehavior.SetEnabledOrBlank(_cutModelSetCombo, checkedActions.Any(a => a.IsCutModel));
        CheckListBehavior.SetEnabledOrBlank(_relinkSetCombo, checkedActions.Any(a => a.IsRelink));
        CheckListBehavior.SetEnabledOrBlank(_correctWorksetSetCombo, checkedActions.Any(a => a.IsCorrectWorkset));
        CheckListBehavior.SetEnabledOrBlank(_exportNwcSetCombo, checkedActions.Any(a => a.IsExportNwc));
        CheckListBehavior.SetEnabledOrBlank(_exportIfcSetCombo, checkedActions.Any(a => a.IsExportIfc));

        CheckListBehavior.SetEnabledOrBlank(_fileLogSetCombo, checkedActions.Any(a => a.IsFileLog));
        CheckListBehavior.SetEnabledOrBlank(_dataExtractSetCombo, checkedActions.Any(a => a.IsDataExtract));
    }

    /// <summary>
    /// What the selection will actually do that the checklist does not already say — appended to
    /// the selection summary rather than pushed through <see cref="SetStatus"/>, because it
    /// describes a standing property of the selection, not the outcome of the last click.
    ///
    /// <para>Both hints are ADVISORY. Neither blocks a save and neither reorders anything: the run
    /// order is visible in the tracking CSV's <c>"{i} of {n}"</c> Task cells, so silently rewriting
    /// it would make the log disagree with the saved job.</para>
    /// </summary>
    private string OrderingHint(List<ActionOrScriptItem> checkedActions)
    {
        if (checkedActions.Count == 0) return "";

        // (1) A REST-only job resolves no model set at all — ScheduledRunner short-circuits before
        // ModelPresetCombiner runs — so ticked sets are not "also processed", they are discarded.
        if (checkedActions.All(a => a.IsRestOnly))
            return _models.HasAnySource
                ? "  —  opens no model: the ticked model sets will not be used."
                : "  —  opens no model, so no model set is needed.";

        // (2) A wide-posture pass cannot join a document already open with closed worksets, so the
        // engine closes and reopens it. Placed first, every later narrow pass joins ITS document
        // instead and the model is opened once. See ModelDocumentCursor / BatchEngine.CanJoinOpenDocument.
        List<ActionOrScriptItem> modelItems = checkedActions.Where(a => !a.IsRestOnly).ToList();
        int firstWide = modelItems.FindIndex(a => a.OpensWide);
        if (firstWide > 0)
            return $"  —  '{modelItems[firstWide].Name}' opens each model on its own worksets; " +
                   "move it above the others to open each model once instead of twice.";

        return "";
    }

    /// <summary>Weekly-day checkboxes are interactive only when Recurrence is Weekly; the start-date
    /// picker only when Recurrence is Once (Daily/Weekly derive their day from the time/weekdays).</summary>

    // ---- schedule table --------------------------------------------------------




    /// <summary>Preloads a not-yet-saved draft job (e.g. handed off from Batch Run) as a new schedule.</summary>
    public void PreloadDraft(ScheduledJob draft) => LoadJobIntoForm(draft, editingExisting: false);



    /// <summary>Internal so the round-trip gate can drive it — see TryBuildJob.</summary>
    internal void LoadJobIntoForm(ScheduledJob job, bool editingExisting)
    {
        _editingId = editingExisting ? job.Id : null;
        _timing.Load(job);

        _models.Load(job);

        // Reorder the checklist so the job's items appear FIRST in their saved order (checked),
        // then any remaining items (unchecked). Visible order == run order, so a saved
        // action → script → action arrangement round-trips and the Move buttons stick.
        ApplyRunItemOrder(job.RunItems);

        // Re-select the Initiate / Cut model input sets the job was seeded from (by name), if they
        // still exist. The job carries its own snapshot regardless, so a since-deleted set doesn't break it.
        if (job.InitiatePublish != null)
            SelectInitiateSet(job.InitiatePublish.Name);
        if (job.CutModelPublish != null)
            SelectCutModelSet(job.CutModelPublish.Name);
        if (job.RelinkSpec != null)
            SelectByName(_relinkSetCombo, job.RelinkSpec.Name, o => ((RelinkSetItem)o).Set.Name);
        if (job.CorrectWorksetSpec != null)
            SelectByName(_correctWorksetSetCombo, job.CorrectWorksetSpec.Name, o => ((CorrectWorksetSetItem)o).Set.Name);
        if (job.ExportNwcSpec != null)
            SelectExportNwcSet(job.ExportNwcSpec.Name);
        if (job.ExportIfcSpec != null)
            SelectByName(_exportIfcSetCombo, job.ExportIfcSpec.Name, o => ((ExportIfcSetItem)o).Set.Name);

        if (job.FileLogInputs != null)
            SelectFileLogSet(job.FileLogInputs.Name);
        if (job.DataExtractInputs != null)
            SelectDataExtractSet(job.DataExtractInputs.Name);

        SelectComboValue(_syncCombo, job.Sync.ToString());
        SelectComboValue(_completionCombo, job.Completion.ToString());
        OnSplitConditionChanged();
        _folderBox.Text = job.DestinationFolder;
        _projectLogBox.Text = job.ProjectLogName;

        // Keep the date picker enabled only for Once, etc. (recurrence was just set above).
        // Reflect the loaded job's checks in the count + the Initiate/Cut combos' enabled state.
        UpdateSelectionUi();

        SetStatus(editingExisting ? $"Editing '{job.Name}'. Change fields and Save schedule." :
            $"Duplicated as '{job.Name}'. Adjust and Save schedule to create it.");
    }

    /// <summary>Whether the chosen disposition permits a parallel split — see
    /// <see cref="FormLayout.SplitAllowed"/>. <c>ScheduledRunner</c> refuses to split a
    /// "Leave files open" job too, so the two ends agree.</summary>
    private bool SplitAllowed() =>
        FormLayout.SplitAllowed(((NamedItem?)_completionCombo.SelectedItem)?.Value);

    /// <summary>The summary line states the resulting instance count, so it has to follow the
    /// disposition — otherwise it would still promise a split that "Leave files open" just ruled
    /// out.</summary>
    private void OnSplitConditionChanged() => UpdateSelectionUi();

    private static void SelectComboValue(ComboBox combo, string value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (((NamedItem)combo.Items[i]).Value == value) { combo.SelectedIndex = i; return; }
    }

    private void SelectInitiateSet(string name)
    {
        for (int i = 0; i < _initiateSetCombo.Items.Count; i++)
            if (((InitiateSetItem)_initiateSetCombo.Items[i]).Set.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { _initiateSetCombo.SelectedIndex = i; return; }
    }

    private void SelectExportNwcSet(string name)
    {
        for (int i = 0; i < _exportNwcSetCombo.Items.Count; i++)
            if (((ExportNwcSetItem)_exportNwcSetCombo.Items[i]).Set.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { _exportNwcSetCombo.SelectedIndex = i; return; }
    }

    private void SelectFileLogSet(string name)
    {
        for (int i = 0; i < _fileLogSetCombo.Items.Count; i++)
            if (((FileLogSetItem)_fileLogSetCombo.Items[i]).Set.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { _fileLogSetCombo.SelectedIndex = i; return; }
    }

    private void SelectDataExtractSet(string name)
    {
        for (int i = 0; i < _dataExtractSetCombo.Items.Count; i++)
            if (((DataExtractSetItem)_dataExtractSetCombo.Items[i]).Set.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { _dataExtractSetCombo.SelectedIndex = i; return; }
    }

    private static void SelectByName(ComboBox combo, string name, Func<object, string> nameOf)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (nameOf(combo.Items[i]).Equals(name, StringComparison.OrdinalIgnoreCase))
            { combo.SelectedIndex = i; return; }
    }

    private void SelectCutModelSet(string name)
    {
        for (int i = 0; i < _cutModelSetCombo.Items.Count; i++)
            if (((CutModelSetItem)_cutModelSetCombo.Items[i]).Set.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { _cutModelSetCombo.SelectedIndex = i; return; }
    }

    /// <summary>Reorders the actions/scripts checklist so <paramref name="runItems"/> come first in
    /// their saved order (checked), then the remaining registry items (unchecked) — so the visible
    /// order matches run order for a loaded/duplicated job.</summary>
    // The same alias ActionRegistry.Create applies when the schedule runs, so what the window ticks
    // and what the run executes cannot disagree. Covers DRW's Grid -> Datum Dimension rename and the 1.x Initiate name.
    private static string CurrentActionName(string saved) => Actions.ActionNames.Canonical(saved);

    private void ApplyRunItemOrder(IReadOnlyList<RunItem> runItems)
    {
        var current = _actionsScriptsList.Items.Cast<ActionOrScriptItem>().ToList();
        var ordered = new List<ActionOrScriptItem>();
        var toCheck = new HashSet<ActionOrScriptItem>();
        foreach (RunItem ri in runItems)
        {
            // Script sets match by stable id first (so a renamed set stays checked/ordered), then by
            // name; actions (no id) always match by name — by their CURRENT name, so an action
            // saved under a tool's old name (DRW's "Grid Dimension — X") still shows ticked.
            string name = ri.Kind == RunItemKind.ScriptSet ? ri.Name : CurrentActionName(ri.Name);
            ActionOrScriptItem? match = current.FirstOrDefault(c =>
                c.IsScriptSet == (ri.Kind == RunItemKind.ScriptSet) &&
                (!string.IsNullOrWhiteSpace(ri.RefId) && !string.IsNullOrWhiteSpace(c.RefId)
                    ? c.RefId == ri.RefId
                    : c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
            if (match != null && !ordered.Contains(match)) { ordered.Add(match); toCheck.Add(match); }
        }
        foreach (ActionOrScriptItem c in current)
            if (!ordered.Contains(c)) ordered.Add(c);

        _actionsScriptsList.Items.Clear();
        foreach (ActionOrScriptItem c in ordered)
        {
            int idx = _actionsScriptsList.Items.Add(c);
            _actionsScriptsList.SetItemChecked(idx, toCheck.Contains(c));
        }
        // A saved job may list the step anywhere — jobs saved before it was pinned, and any job
        // whose RunItems were written by hand. It still RUNS first, so it must still SHOW first.
        PinRestOnlyFirst();
    }

    /// <summary>
    /// Moves the REST-only items (Get File Log, then Get Data Extract) to the top of the checklist,
    /// keeping their checked state.
    ///
    /// <para>The checklist's order IS the run order for everything else, but not for these steps:
    /// the runner hoists them to the front of the whole run whatever position they hold
    /// (<c>ScheduledRunner.BeginHeadless</c>, <c>TryRunInteractive</c> and
    /// <c>ModalJobHandler.Execute</c> all run them before the model-major loop starts). A list that
    /// let one sit third while it always ran first was showing an order the run does not have, so
    /// the honest fix is to make the position match rather than to caption the discrepancy.</para>
    ///
    /// <para>File log before data extract, matching <c>RestOnlySteps.Run</c> — the two are
    /// independent, but a list that disagreed with the log's row order would send a reader looking
    /// for a cause that is not there.</para>
    /// </summary>
    private void PinRestOnlyFirst()
    {
        var pinned = new List<(ActionOrScriptItem Item, bool Checked)>();
        for (int i = _actionsScriptsList.Items.Count - 1; i >= 0; i--)
        {
            if (_actionsScriptsList.Items[i] is not ActionOrScriptItem { IsRestOnly: true } item) continue;
            pinned.Add((item, _actionsScriptsList.GetItemChecked(i)));
            _actionsScriptsList.Items.RemoveAt(i);
        }
        foreach ((ActionOrScriptItem item, bool wasChecked) in
                 pinned.OrderByDescending(p => p.Item.IsFileLog))
        {
            _actionsScriptsList.Items.Insert(0, item);
            _actionsScriptsList.SetItemChecked(0, wasChecked);
        }
    }

    /// <summary>Reorder the selected action/script set up (-1) or down (+1); list order is run order.</summary>
    private void MoveActionScript(int delta)
    {
        int i = _actionsScriptsList.SelectedIndex;
        if (i < 0) { SetStatus("Select an action or script set to reorder."); return; }
        int j = i + delta;
        if (j < 0 || j >= _actionsScriptsList.Items.Count) return;

        // Both directions, because either one would break the same promise — see PinRestOnlyFirst.
        ActionOrScriptItem? blocking =
            _actionsScriptsList.Items[i] as ActionOrScriptItem is { IsRestOnly: true } moved ? moved
            : delta < 0 && _actionsScriptsList.Items[j] as ActionOrScriptItem is { IsRestOnly: true } over ? over
            : null;
        if (blocking != null)
        {
            SetStatus($"'{blocking.Name}' always runs first — it opens no model, so the run pulls " +
                      "it before any model is opened. It cannot be reordered.");
            return;
        }

        object item = _actionsScriptsList.Items[i];
        bool wasChecked = _actionsScriptsList.GetItemChecked(i);
        _actionsScriptsList.Items.RemoveAt(i);
        _actionsScriptsList.Items.Insert(j, item);
        _actionsScriptsList.SetItemChecked(j, wasChecked);
        _actionsScriptsList.SelectedIndex = j;
    }

    private void NewSchedule()
    {
        _jobs.Grid.ClearSelection();
        _editingId = null;
        _timing.Clear();
        _models.Clear();
        for (int i = 0; i < _actionsScriptsList.Items.Count; i++) _actionsScriptsList.SetItemChecked(i, false);
        if (_initiateSetCombo.Items.Count > 0) _initiateSetCombo.SelectedIndex = 0;
        if (_cutModelSetCombo.Items.Count > 0) _cutModelSetCombo.SelectedIndex = 0;
        if (_relinkSetCombo.Items.Count > 0) _relinkSetCombo.SelectedIndex = 0;
        if (_correctWorksetSetCombo.Items.Count > 0) _correctWorksetSetCombo.SelectedIndex = 0;
        if (_exportNwcSetCombo.Items.Count > 0) _exportNwcSetCombo.SelectedIndex = 0;
        if (_exportIfcSetCombo.Items.Count > 0) _exportIfcSetCombo.SelectedIndex = 0;
        if (_fileLogSetCombo.Items.Count > 0) _fileLogSetCombo.SelectedIndex = 0;
        if (_dataExtractSetCombo.Items.Count > 0) _dataExtractSetCombo.SelectedIndex = 0;
        _syncCombo.SelectedIndex = 0; // Sync (default)
        _completionCombo.SelectedIndex = 2; // Close Revit — the natural default for a headless schedule
        OnSplitConditionChanged();
        _folderBox.Text = Settings.UserSettings.DefaultDestinationFolder;
        _projectLogBox.Text = "";

        UpdateSelectionUi();
        SetStatus("New schedule — fill in the fields and Save schedule.");
    }



    // ---- ad-hoc models ---------------------------------------------------------



    /// <summary>Add models to the ad-hoc list, skipping any already present (local path or cloud GUID).</summary>




    // ---- folder / commit --------------------------------------------------------

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog();
        if (Directory.Exists(_folderBox.Text)) dlg.SelectedPath = _folderBox.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dlg.SelectedPath;
    }


    /// <summary>
    /// Reads the form into a <see cref="ScheduledJob"/>, or reports the first problem and returns
    /// false. Writes nothing, prompts for nothing — see <see cref="ScheduleCutWindow.TryBuildJob"/>
    /// for why the mapping was split from the persistence in 1.4.0.
    ///
    /// <para>The two publish CONFIRMATIONS stayed behind in <see cref="Commit"/>. They were modal
    /// message boxes in the middle of the mapping, which is both the wrong place for them — reading
    /// a form should not be able to block on a human — and the reason the round-trip gate could not
    /// otherwise run this window unattended. Nothing is persisted before the confirmation either
    /// way, so what the user sees is unchanged.</para>
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
        List<string> modelSetIds = _models.CheckedIds;
        var actionsAndScripts = _actionsScriptsList.CheckedItems.Cast<ActionOrScriptItem>().ToList();
        if (actionsAndScripts.Count == 0)
        {
            SetStatus("Check at least one action or script set.");
            return false;
        }

        // Checklist order == run order (interleaved actions + script sets). Script items carry
        // their set's stable id so a later rename doesn't orphan the schedule. Built here rather
        // than in the initializer below because the model-source check needs to read it first.
        List<RunItem> runItems = actionsAndScripts
            .Select(a => new RunItem(a.Name,
                a.IsScriptSet ? RunItemKind.ScriptSet : RunItemKind.Action,
                a.IsScriptSet ? a.RefId : null))
            .ToList();

        // A REST-only job (Get File Log / Get Data Extract and nothing else) opens no model, and the
        // runner discards any model set it carries — ScheduledRunner short-circuits on the same
        // RestOnlySteps.IsOnly before a preset is ever combined. Demanding a model set here
        // therefore made the ONLY way to save such a job to tick a set that would then be discarded.
        if (!Schedule.RestOnlySteps.IsOnly(runItems) && !_models.HasAnySource)
        {
            SetStatus("Check at least one model set or add an ad-hoc model.");
            return false;
        }
        InitiateInputSet? initiateSpec = null;
        if (actionsAndScripts.Any(a => a.IsInitiate))
        {
            if (_initiateSetCombo.SelectedItem is not InitiateSetItem si)
            {
                SetStatus("Select an Initiate input set (create one first in Forma Batch Action → Initiate to Forma).");
                return false;
            }
            initiateSpec = si.Set.Clone();
        }

        ExportNwcInputSet? exportNwcSpec = null;
        if (actionsAndScripts.Any(a => a.IsExportNwc))
        {
            if (_exportNwcSetCombo.SelectedItem is not ExportNwcSetItem es)
            {
                SetStatus("Select an Export NWC options set (create one first in " +
                          "Multi Model Action → Export NWC).");
                return false;
            }
            // Snapshotted onto the job, never re-read from the store at run time — a scheduled run
            // must be self-contained, the same rule Initiate and Cut model follow.
            exportNwcSpec = es.Set.Clone();
        }

        ExportIfcInputSet? exportIfcSpec = null;
        if (actionsAndScripts.Any(a => a.IsExportIfc))
        {
            if (_exportIfcSetCombo.SelectedItem is not ExportIfcSetItem ei)
            {
                SetStatus("Select an Export IFC + Data options set (create one first in " +
                          "NWD Helper → Export IFC + Data).");
                return false;
            }
            exportIfcSpec = ei.Set.Clone();   // snapshotted, like Export NWC
        }

        // Null unless the step is actually in the job — a set on a job that never pulls a file log
        // is a setting nobody can see the effect of.
        FileLogInputSet? fileLogInputs = null;
        if (actionsAndScripts.Any(a => a.IsFileLog))
        {
            if (_fileLogSetCombo.SelectedItem is not FileLogSetItem fs)
            {
                SetStatus("Select a Get File Log set (create one first in Forma Batch Action → " +
                          "Get File Log: add folders, then Save).");
                return false;
            }
            // Snapshotted onto the job, never re-read from the store at run time — a scheduled run
            // must be self-contained, the same rule Initiate, Cut model and Export NWC follow.
            fileLogInputs = fs.Set.Clone();
        }

        // Same rule again: null unless the step is in the job, and snapshotted rather than re-read
        // at run time so an overnight download cannot change because someone edited the set at 5pm.
        DataExtractInputSet? dataExtractInputs = null;
        if (actionsAndScripts.Any(a => a.IsDataExtract))
        {
            if (_dataExtractSetCombo.SelectedItem is not DataExtractSetItem ds)
            {
                SetStatus("Select a Get Data Extract set (create one first in Forma Batch Action → " +
                          "Get Data Extract: choose an account and an extract, then Save).");
                return false;
            }
            dataExtractInputs = ds.Set.Clone();
        }

        CutModelInputSet? cutModelSpec = null;
        if (actionsAndScripts.Any(a => a.IsCutModel))
        {
            if (_cutModelSetCombo.SelectedItem is not CutModelSetItem cs)
            {
                SetStatus("Select a Cut model input set (create one first in Forma Batch Action → Cut model).");
                return false;
            }
            cutModelSpec = cs.Set.Clone();
        }

        RelinkInputSet? relinkSpec = null;
        if (actionsAndScripts.Any(a => a.IsRelink))
        {
            if (_relinkSetCombo.SelectedItem is not RelinkSetItem rs)
            {
                SetStatus("Select an Internal Relink set (create one first in Forma Batch Action -> Internal Relink).");
                return false;
            }
            relinkSpec = rs.Set.Clone();
        }

        CorrectWorksetInputSet? correctWorksetSpec = null;
        if (actionsAndScripts.Any(a => a.IsCorrectWorkset))
        {
            if (_correctWorksetSetCombo.SelectedItem is not CorrectWorksetSetItem ws)
            {
                SetStatus("Select a Correct Workset set (create one first in Model Batch Action -> Correct Workset).");
                return false;
            }
            correctWorksetSpec = ws.Set.Clone();
        }

        var completion = (BatchCompletion)Enum.Parse(typeof(BatchCompletion),
            ((NamedItem)_completionCombo.SelectedItem!).Value);
        var syncMode = (SyncMode)Enum.Parse(typeof(SyncMode), ((NamedItem)_syncCombo.SelectedItem!).Value);
        // Relink and a Fix-mode Correct Workset only mean something when their changes reach the
        // central; the retired dedicated window forced Sync for them, so refuse a dry run that would
        // quietly discard their work rather than save it.
        if (syncMode == SyncMode.NoSync && (relinkSpec != null || correctWorksetSpec?.Fix == true))
        {
            SetStatus("Internal Relink / Correct Workset (Fix) need Model treatment = Sync - " +
                      "a dry run would discard every change they make.");
            return false;
        }
        ScheduleRecurrence recurrence = _timing.Recurrence;
        List<DayOfWeek> weeklyDays = _timing.WeeklyDays;
        string time = _timing.Time;
        DateTime? startDate = _timing.StartDate;

        job = new ScheduledJob
        {
            Id = _editingId ?? Guid.NewGuid().ToString("N"),
            Name = _timing.ScheduleName.Trim(),
            ModelSetNames = modelSets,
            ModelSetIds = modelSetIds,
            AdHocModels = _models.AdHocModels,
            RunItems = runItems,
            InitiatePublish = initiateSpec,
            CutModelPublish = cutModelSpec,
            RelinkSpec = relinkSpec,
            CorrectWorksetSpec = correctWorksetSpec,
            ExportNwcSpec = exportNwcSpec,
            ExportIfcSpec = exportIfcSpec,
            FileLogInputs = fileLogInputs,
            DataExtractInputs = dataExtractInputs,
            Completion = completion,
            Sync = syncMode,
            DestinationFolder = _folderBox.Text.Trim(),
            ProjectLogName = _projectLogBox.Text.Trim(),
            Time = time,
            Recurrence = recurrence,
            // A Once job fires on a fixed calendar date (date part only); Daily/Weekly ignore it.
            StartDate = startDate,
            WeeklyDays = weeklyDays,
            GroupSize = SplitAllowed() ? ScheduledJob.GroupSizeAuto : 0
        };
        return true;
    }

    private void Commit(ScheduleOutcome outcome)
    {
        if (!TryBuildJob(out ScheduledJob? built)) return;

        // Initiate and Cut model publish NEW cloud models, and a headless scheduled run never
        // prompts — so the destination is confirmed once, here at save/run time (covering both
        // "Save schedule" and this window's "Run now"). Ordered Initiate-then-Cut, as before.
        if (built!.InitiatePublish is { } initiate &&
            !ConfirmPublish("Schedule — Initiate to Forma", initiate.FolderPath))
        {
            SetStatus("Initiate not confirmed — schedule not saved.");
            return;
        }
        if (built.CutModelPublish is { } cut &&
            !ConfirmPublish("Schedule — Cut model", cut.FolderPath))
        {
            SetStatus("Cut model not confirmed — schedule not saved.");
            return;
        }

        Job = built;
        Settings.UserSettings.LastDestinationFolder = Job.DestinationFolder;

        // "Save schedule" saves IN PLACE — the window stays open (grid refreshes, status reports
        // success/failure) and the user closes it when done; it used to close itself on save,
        // losing the context to fix a failed save or add the next schedule.
        if (outcome == ScheduleOutcome.Create)
        {
            (bool ok, string msg) = ScheduleSaver.SaveAndRegister(Job);
            if (ok)
            {
                _editingId = Job.Id; // further Saves update this schedule instead of duplicating it
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

    /// <summary>Confirms a publishing pass (Initiate / Cut model), which creates NEW cloud models
    /// unattended every time the schedule fires. Returns true when the user says Yes.</summary>
    private static bool ConfirmPublish(string title, string folderPath) =>
        HostServices.Confirm(title,
            "This job publishes NEW cloud models when it runs.\n\n" +
            $"Target folder: {folderPath}\n\n" +
            "Each model in the job is published as a new cloud model in that folder, " +
            "unattended, every time the schedule fires. Continue?");

    /// <summary>Combo item pairing a stored value with a display label.</summary>


    private sealed class InitiateSetItem
    {
        public InitiateInputSet Set { get; }
        public InitiateSetItem(InitiateInputSet set) => Set = set;
        public override string ToString() => $"{Set.Name}  →  {Set.FolderPath}";
    }

    private sealed class ExportIfcSetItem
    {
        public ExportIfcInputSet Set { get; }
        public ExportIfcSetItem(ExportIfcInputSet set) => Set = set;
        public override string ToString() =>
            $"{Set.Name}  →  {Set.ScopeLabel}  →  {Set.OutputLabel}";
    }

    private sealed class ExportNwcSetItem
    {
        public ExportNwcInputSet Set { get; }
        public ExportNwcSetItem(ExportNwcInputSet set) => Set = set;
        public override string ToString() =>
            $"{Set.Name}  →  {Set.ScopeLabel}  →  {Set.OutputLabel}";
    }

    private sealed class FileLogSetItem
    {
        public FileLogInputSet Set { get; }
        public FileLogSetItem(FileLogInputSet set) => Set = set;
        // The folder COUNT is in the label on purpose: this step writes one report per folder, so
        // "3 folder(s)" is the shape of the output, not a detail.
        public override string ToString() =>
            $"{Set.Name}  →  {Set.FoldersLabel}  →  {Set.ScopeLabel}";
    }

    private sealed class DataExtractSetItem
    {
        public DataExtractInputSet Set { get; }
        public DataExtractSetItem(DataExtractInputSet set) => Set = set;
        // The ACCOUNT and extract are in the label on purpose: a schedule pointed at the wrong
        // account downloads real data that simply belongs to someone else's project.
        public override string ToString() =>
            $"{Set.Name}  →  {Set.TargetLabel}  →  {Set.ScopeLabel}";
    }

    private sealed class RelinkSetItem
    {
        public RelinkInputSet Set { get; }
        public RelinkSetItem(RelinkInputSet set) => Set = set;
        public override string ToString() => $"{Set.Name}  ({Set.OldCode} -> {Set.NewCode})";
    }

    private sealed class CorrectWorksetSetItem
    {
        public CorrectWorksetInputSet Set { get; }
        public CorrectWorksetSetItem(CorrectWorksetInputSet set) => Set = set;
        public override string ToString() => $"{Set.Name}  ({(Set.Fix ? "Fix" : "Check")})";
    }

    private sealed class CutModelSetItem
    {
        public CutModelInputSet Set { get; }
        public CutModelSetItem(CutModelInputSet set) => Set = set;
        public override string ToString() => $"{Set.Name}  ({Set.Prefix})  →  {Set.FolderPath}";
    }

    private sealed class ActionOrScriptItem
    {
        public string Name { get; }
        public bool IsScriptSet { get; }
        public bool IsInitiate { get; }
        public bool IsCutModel { get; }
        public bool IsExportNwc { get; }
        public bool IsExportIfc { get; }
        public bool IsRelink { get; }
        public bool IsCorrectWorkset { get; }
        public bool IsFileLog { get; }
        public bool IsDataExtract { get; }

        /// <summary>True for a step that opens no model — the ones the runner hoists to the front
        /// and the ones a job may consist of without naming a model set. Asked as one question so a
        /// third such step cannot be added to the list and forgotten by the waiver, the pin, the
        /// move guard and the ordering hint, which all read this.</summary>
        public bool IsRestOnly => IsFileLog || IsDataExtract;

        /// <summary>Stable id of the backing script set (script-set items only); null for actions.</summary>
        public string? RefId { get; }

        /// <summary>How this item opens a model, or NULL when nobody told us. Null is not a
        /// posture: the advisory stays silent rather than guessing. Script sets are null because
        /// their pass needs the ACTIVE document, which forces a fresh open of its own regardless of
        /// worksets — a "this opens twice" hint about them would be noise.</summary>
        public WorksetPosture? Worksets { get; }

        /// <summary>True when this item makes the engine open the model on its own terms, so a
        /// model already open with closed worksets has to be closed and reopened for it.</summary>
        public bool OpensWide =>
            Worksets is WorksetPosture.OpensAll or WorksetPosture.PlansOwn;

        public ActionOrScriptItem(string name, bool isScriptSet, bool isInitiate = false,
            bool isCutModel = false, bool isFileLog = false, string? refId = null,
            bool isExportNwc = false, bool isDataExtract = false, WorksetPosture? worksets = null,
            bool isRelink = false, bool isCorrectWorkset = false, bool isExportIfc = false)
        {
            IsRelink = isRelink; IsCorrectWorkset = isCorrectWorkset; IsExportIfc = isExportIfc;
            Name = name; IsScriptSet = isScriptSet; IsInitiate = isInitiate; IsCutModel = isCutModel;
            IsFileLog = isFileLog; RefId = refId; IsExportNwc = isExportNwc; Worksets = worksets;
            IsDataExtract = isDataExtract;
        }
        public override string ToString() =>
            IsScriptSet ? $"[Script Set] {Name}"
            : IsInitiate || IsCutModel ? $"{Name}  (publishes new cloud models)"
            : IsFileLog ? $"{Name}  (runs first — Forma file log, no model opened)"
            : IsDataExtract ? $"{Name}  (runs first — downloads Forma's extract, no model opened)"
            : IsExportNwc ? $"{Name}  (writes .nwc files)"
            : IsExportIfc ? $"{Name}  (writes .ifc + report data)"
            : IsRelink ? $"{Name}  (relinks and syncs)"
            : IsCorrectWorkset ? $"{Name}  (checks or fixes worksets)"
            : Name;
    }

}
