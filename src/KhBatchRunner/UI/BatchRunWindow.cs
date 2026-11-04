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
/// "Batch Run" (DYN multi player) window: mix one or more saved model sets with ad-hoc
/// local models, check one or more built-in actions and/or DYN script sets (both execute —
/// script sets run via the Dynamo runner), and either run immediately or hand the same
/// selection off to Schedule Run to save as a recurring job.
/// </summary>
public sealed class BatchRunWindow : Form
{
    public enum BatchRunOutcome { None, RunNow, SaveAsSchedule }

    private readonly DimmableCheckedListBox _modelSetsList;
    private readonly CheckBox _accessFilter = null!;
    private readonly ListBox _adHocList;
    private readonly CheckedListBox _actionsScriptsList;
    private readonly ComboBox _initiateSetCombo;
    private readonly ComboBox _cutModelSetCombo;
    private readonly ComboBox _exportNwcSetCombo;
    private readonly ComboBox _syncCombo;
    private readonly ComboBox _completionCombo;
    private readonly TextBox _folderBox;
    private readonly TextBox _projectLogBox;
    private readonly Label _countLabel;
    private readonly StatusLine _status;

    private readonly List<CloudModel> _adHocModels = new();
    private readonly DataManagementClient _client;

    public BatchRunOutcome Outcome { get; private set; } = BatchRunOutcome.None;

    public IReadOnlyList<string> SelectedModelSetNames { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<CloudModel> SelectedAdHocModels { get; private set; } = Array.Empty<CloudModel>();

    /// <summary>The checked actions + script sets in CHECKLIST order (list order is run order) —
    /// both the run-now path and the save-as-schedule handoff use this so actions and script sets
    /// stay interleaved exactly as arranged.</summary>
    public IReadOnlyList<(string Name, bool IsScriptSet)> SelectedRunItems { get; private set; } =
        Array.Empty<(string, bool)>();
    public BatchCompletion SelectedCompletion { get; private set; } = BatchCompletion.CloseDocuments;
    public SyncMode SelectedSyncMode { get; private set; } = SyncMode.Sync;

    /// <summary>The publish snapshot when "Initiate to Forma" is checked, else null (seeded from the chosen saved Initiate input set).</summary>
    public InitiateInputSet? SelectedInitiateSpec { get; private set; }

    /// <summary>The publish + site snapshot when "Cut model" is checked, else null (seeded from the chosen saved Cut model input set).</summary>
    public CutModelInputSet? SelectedCutModelSpec { get; private set; }

    /// <summary>The Export NWC options the run should use, or null when the run does not export.</summary>
    public ExportNwcInputSet? SelectedExportNwcSpec { get; private set; }

    public string DestinationFolder { get; private set; } = "";
    public string ProjectLogName { get; private set; } = "";

    /// <summary>
    /// Whether the run may split across parallel Revit instances — i.e. the chosen disposition
    /// closes. The SIZE of each group is not asked for: the caller derives it once the model list
    /// is resolved, via <see cref="Schedule.InstanceBudget.AutoGroupSize"/>, because it depends on
    /// the model count and the machine rather than on anything visible in this dialog.
    /// </summary>
    public bool SplitEnabled { get; private set; }

    public BatchRunWindow(DataManagementClient client)
    {
        _client = client;

        Text = "Batch Run";
        StartPosition = FormStartPosition.CenterScreen;
        // Scale the whole dialog with Revit's DPI (positions, sizes, fonts together).
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        // Tall enough for the three list columns to be usable. Everything below them is a fixed
        // row — two notes, five labelled option rows, the button bar, the status strip — and the
        // lists are the ONLY row that flexes, so they absorb every shortfall. At 590 they had
        // collapsed to about two visible items each, with the "Add local / Add Forma" buttons sitting
        // on top of the ad-hoc list. The minimum is close to the default for the same reason.
        ClientSize = new Size(860, 790);
        MinimumSize = new Size(840, 760);

        Button Mk(string text, int minW, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(minW, 0) };
            b.Click += (_, _) => onClick();
            return b;
        }

        var open = new Diagnostics.OpenTimer("Batch Run");

        // --- Model sets ---
        // DimmableCheckedListBox, not CheckedListBox: a plain one cannot be owner-drawn, so the
        // account greying was invisible here until 1.5.1.2. See that class.
        _modelSetsList = new DimmableCheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        ModelSetAccess.AttachGreying(_modelSetsList, i => (i as ModelSetItem)?.Preset);
        RebuildModelSets();
        // Built and shown first, relabelled when the (REST-bound) account check lands — see
        // ModelSetAccess.BeginRefresh. Batch Run is opened constantly; it must not wait on Forma.
        _accessFilter = ModelSetAccess.FilterCheckBox(RebuildModelSets);
        ModelSetAccess.BeginRefresh(this, client, RebuildModelSets);
        open.Mark("model sets");

        // --- Ad-hoc models (local + Forma) ---
        _adHocList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
        var adHocButtons = FormLayout.FlowRow(
            Mk("Add local…", 80, AddAdHocModels),
            Mk("Add Forma…", 80, AddAdHocFromForma),
            Mk("Remove", 60, RemoveSelectedAdHoc));
        // This column is narrower than the other two — let the row wrap to a second line
        // instead of clipping the last button when all three don't fit on one (Dock=Fill so
        // WrapContents has an actual width to wrap against; without it the panel just grows
        // to fit its children in one row and overflows the column instead).
        adHocButtons.Dock = DockStyle.Fill;
        adHocButtons.WrapContents = true;

        // --- Actions + script sets (reorderable — list order is run order) ---
        _actionsScriptsList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        foreach (IBatchAction a in ActionRegistry.All())
            _actionsScriptsList.Items.Add(new ActionOrScriptItem(a.Name, isScriptSet: false));
#if !EDITION_DRW
        // DRW Helper's Batch Run offers only DRW's own tools (ActionRegistry scopes those), so the
        // batch runner's synthetic publish/export items and Dynamo script sets stay off its list.
        // Initiate to Forma isn't in ActionRegistry (it needs a publish target) — offer it as a
        // synthetic item; when checked, the "Initiate input set" combo below seeds its target.
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Actions.InitiateToFormaAction.DisplayName, isScriptSet: false, isInitiate: true));
        // Cut model (Forma Batch Action) — also needs a per-run target + site input, so it's a synthetic
        // item seeded by the "Cut model input set" combo below, mirroring Initiate.
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Actions.CutModelAction.DisplayName, isScriptSet: false, isCutModel: true));
        // Export NWC — needs an output folder and a view name, so it is a synthetic item seeded by
        // the "Export NWC options" combo below, exactly like Cut model.
        _actionsScriptsList.Items.Add(new ActionOrScriptItem(
            Actions.ExportNwcAction.DisplayName, isScriptSet: false, isExportNwc: true));
        foreach (ScriptSet s in ScriptSetStore.LoadAll())
            _actionsScriptsList.Items.Add(new ActionOrScriptItem(s.Name, isScriptSet: true));
#endif
        open.Mark("actions + script sets");
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

        // Seeds the Export NWC pass's view name + output folder (only used when it is checked).
        _exportNwcSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (ExportNwcInputSet s in ExportNwcSetStore.LoadAll())
            _exportNwcSetCombo.Items.Add(new ExportNwcSetItem(s));
        if (_exportNwcSetCombo.Items.Count > 0) _exportNwcSetCombo.SelectedIndex = 0;
        open.Mark("input sets");

        // A floor under each column, so the row cannot be squeezed into uselessness even if the
        // rows below it grow (a longer note, a taller font, a higher DPI). Six-ish visible items.
        foreach (Control list in new Control[] { _modelSetsList, _adHocList, _actionsScriptsList })
            list.MinimumSize = new Size(0, 130);

        // Three side-by-side columns that share the width and grow with the window.
        var lists = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            // 240, not 190: the row has to hold a column WHOLE — its heading (~20px), the 130px list
            // floor above, and its button row, which for the ad-hoc column wraps onto two lines
            // (~76px). At 190 the floor was honoured by pushing the list ON TOP of its own buttons
            // instead, which is the same defect one layer down.
            Margin = new Padding(0), MinimumSize = new Size(0, 240)
        };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lists.Controls.Add(FormLayout.TitledColumn("Model sets", _modelSetsList, _accessFilter), 0, 0);
        lists.Controls.Add(FormLayout.TitledColumn("Ad-hoc models (local + Forma)", _adHocList, adHocButtons), 1, 0);
        lists.Controls.Add(FormLayout.TitledColumn("Actions + script sets", _actionsScriptsList, actionButtons), 2, 0);

        // --- Model treatment (whether write actions/scripts persist their changes) ---
        _syncCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _syncCombo.Items.Add(new NamedItem(SyncMode.Sync.ToString(), "Sync changes to central"));
        _syncCombo.Items.Add(new NamedItem(SyncMode.NoSync.ToString(), "Don't sync (dry run)"));
        // Dry run is the DEFAULT: the safe option should be the one you get by not thinking about
        // it. Syncing a batch across a set of live central models is the irreversible choice, so it
        // is the one you have to pick deliberately.
        _syncCombo.SelectedIndex = 1;

        // --- When finished (post-run disposition) ---
        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new NamedItem(BatchCompletion.CloseDocuments.ToString(), "Close files"));
        _completionCombo.Items.Add(new NamedItem(BatchCompletion.LeaveOpen.ToString(), "Leave files open"));
        _completionCombo.Items.Add(new NamedItem(BatchCompletion.CloseRevit.ToString(), "Close Revit when complete"));
        // Close Revit when complete (default), matching every Schedule window. A run ends with a
        // fresh, unwedged process next time, and the summary is written to the log rather than
        // shown in a dialog nobody would be there for (see BatchRunnerWindow.WriteSummaryRows).
        _completionCombo.SelectedIndex = 2;
        _completionCombo.SelectedIndexChanged += (_, _) => UpdateSelectionUi();

        // Open mode is fixed by each action's category now (no picker) — just describe it.
        Label note = FormLayout.NoteLabel(this,
            "Checks always run Read-only; model treatment applies to actions/scripts that modify the model. All worksets closed.");

        // "Close Revit when complete" is now the default, which is a destructive-looking ending
        // (any other model open in this Revit goes with it) and shows no summary dialog — so say so.
        Label completionNote = FormLayout.NoteLabel(this,
            "Closing Revit is the default: this Revit exits when the run finishes and the succeeded/failed " +
            "summary is written to the log instead of shown. Parallel groups need a closing option.");

        Label splitNote = FormLayout.NoteLabel(this, FormLayout.SplitNote);

        // --- Destination / log ---
        _folderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        var browseBtn = Mk("Browse…", 80, BrowseFolder);
        _projectLogBox = new TextBox();

        // Live "N selected" count under the lists.
        _countLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 2, 3, 4) };

        // --- Action buttons (Run/Save on the left, Close on the right) ---
        var runNowBtn = Mk("Run now", 100, () => Commit(BatchRunOutcome.RunNow));
        var saveAsScheduleBtn = Mk("Save as schedule…", 150, () => Commit(BatchRunOutcome.SaveAsSchedule));
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
        actionRow.Controls.Add(FormLayout.FlowRow(runNowBtn, saveAsScheduleBtn), 0, 0);
        // In a FlowRow like its neighbours, so Close shares their 2 px top margin and sits level.
        actionRow.Controls.Add(FormLayout.FlowRow(closeBtn), 1, 0);

        var root = FormLayout.SingleColumn();
        FormLayout.AddFillRow(root, lists, 100);
        FormLayout.AddAutoRow(root, _countLabel);
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Initiate input set:", _initiateSetCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Cut model input set:", _cutModelSetCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Export NWC options:", _exportNwcSetCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Model treatment:", _syncCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("When finished:", _completionCombo));
        FormLayout.AddAutoRow(root, completionNote);
        FormLayout.AddAutoRow(root, splitNote);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Destination folder:", _folderBox, browseBtn));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Project log name:", _projectLogBox));
        FormLayout.AddAutoRow(root, actionRow);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(root);
        Controls.Add(_status);
        CancelButton = closeBtn;

        // Shift+click range checking on both check-lists, and keep the count + the Initiate/Cut
        // combos' enabled state in sync as items are checked (ItemCheck fires before the state
        // updates, so defer with BeginInvoke).
        CheckListBehavior.EnableRangeCheck(_modelSetsList);
        CheckListBehavior.EnableRangeCheck(_actionsScriptsList);
        _modelSetsList.ItemCheck += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)UpdateSelectionUi); };
        _actionsScriptsList.ItemCheck += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)UpdateSelectionUi); };

        open.Mark("layout");
        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);
        completionNote.ForeColor = Theme.Dim(completionNote);
        splitNote.ForeColor = Theme.Dim(splitNote);

        // Fit to what the dialog actually holds, then refuse to be dragged below it — the idiom
        // CutModelWindow and eight others already use. +_status.Height because the status strip
        // docks outside root.
        ClientSize = new Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width),
            root.PreferredSize.Height + _status.Height);
        MinimumSize = Size;

        UpdateSelectionUi();
        SetStatus("Check the model sets and actions/script sets to include and a destination, then Run now or Save as schedule.");
        open.Mark("theme + finalise");
        open.Done();
    }

    /// <summary>Live selected-count label; the Initiate/Cut input-set combos are enabled only when
    /// their synthetic item is checked (grayed out otherwise).</summary>
    /// <summary>
    /// Repopulates the model-set checklist from the store, preserving ticks — on first build, when
    /// the access filter is toggled, and when the account snapshot arrives. Only a set proven
    /// inaccessible is ever left out; see <see cref="ModelSetAccess"/>.
    /// </summary>
    private void RebuildModelSets()
    {
        var ticked = new HashSet<string>(
            _modelSetsList.CheckedItems.Cast<ModelSetItem>().Select(i => ModelSetAccess.SetKey(i.Preset)),
            StringComparer.OrdinalIgnoreCase);

        _modelSetsList.BeginUpdate();
        _modelSetsList.Items.Clear();
        foreach (ModelPreset p in ModelSetAccess.VisibleSets())
        {
            int idx = _modelSetsList.Items.Add(new ModelSetItem(p));
            if (ticked.Contains(ModelSetAccess.SetKey(p))) _modelSetsList.SetItemChecked(idx, true);
        }
        _modelSetsList.EndUpdate();
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        // Reachable from control events raised while the constructor is still wiring things up,
        // and the label is built after those controls.
        if (_countLabel is null) return;

        _countLabel.Text = SelectionSummary.Describe(
            _modelSetsList.CheckedItems.Cast<ModelSetItem>().Select(i => i.Preset).ToList(),
            _adHocModels,
            _actionsScriptsList.CheckedItems.Count,
            SplitAllowed());

        var checkedActions = _actionsScriptsList.CheckedItems.Cast<ActionOrScriptItem>().ToList();
        CheckListBehavior.SetEnabledOrBlank(_initiateSetCombo, checkedActions.Any(a => a.IsInitiate));
        CheckListBehavior.SetEnabledOrBlank(_cutModelSetCombo, checkedActions.Any(a => a.IsCutModel));
        CheckListBehavior.SetEnabledOrBlank(_exportNwcSetCombo, checkedActions.Any(a => a.IsExportNwc));
    }

    private void AddAdHocModels()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add local Revit models", Filter = "Revit models (*.rvt)|*.rvt|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int added = MergeAdHoc(dlg.FileNames.Select(p =>
            new CloudModel { DisplayName = Path.GetFileName(p), LocalPath = p }));
        SetStatus(added == 0 ? "Nothing new to add." : $"Added {added} local model(s).");
    }

    private void AddAdHocFromForma()
    {
        using var picker = new FormaPickerWindow(_client);
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        int added = MergeAdHoc(picker.SelectedModels);
        SetStatus(added == 0 ? "Nothing new to add." : $"Added {added} Forma model(s).");
    }

    /// <summary>Add models to the ad-hoc list, skipping any already present (local path or cloud GUID).</summary>
    private int MergeAdHoc(IEnumerable<CloudModel> models)
    {
        var existing = _adHocModels.Select(AdHocKey).ToHashSet();
        int added = 0;
        foreach (CloudModel m in models)
            if (existing.Add(AdHocKey(m))) { _adHocModels.Add(m); added++; }
        if (added > 0) RefreshAdHocList();
        return added;
    }

    private static string AdHocKey(CloudModel m) =>
        m.IsLocal ? "local:" + (m.LocalPath ?? "").ToLowerInvariant() : "cloud:" + m.ModelGuid;

    private void RemoveSelectedAdHoc()
    {
        var keys = _adHocList.SelectedItems.Cast<AdHocItem>().Select(i => AdHocKey(i.Model)).ToHashSet();
        _adHocModels.RemoveAll(m => keys.Contains(AdHocKey(m)));
        RefreshAdHocList();
    }

    private void RefreshAdHocList()
    {
        _adHocList.Items.Clear();
        foreach (CloudModel m in _adHocModels)
            _adHocList.Items.Add(new AdHocItem(m));
        // Ad-hoc models count toward the total, so adding or removing one changes the model and
        // instance counts the summary reports.
        UpdateSelectionUi();
    }

    /// <summary>Reorder the selected action/script set up (-1) or down (+1); list order is run order.</summary>
    private void MoveActionScript(int delta)
    {
        int i = _actionsScriptsList.SelectedIndex;
        if (i < 0) { SetStatus("Select an action or script set to reorder."); return; }
        int j = i + delta;
        if (j < 0 || j >= _actionsScriptsList.Items.Count) return;

        object item = _actionsScriptsList.Items[i];
        bool wasChecked = _actionsScriptsList.GetItemChecked(i);
        _actionsScriptsList.Items.RemoveAt(i);
        _actionsScriptsList.Items.Insert(j, item);
        _actionsScriptsList.SetItemChecked(j, wasChecked);
        _actionsScriptsList.SelectedIndex = j;
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog();
        if (Directory.Exists(_folderBox.Text)) dlg.SelectedPath = _folderBox.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dlg.SelectedPath;
    }

    private void Commit(BatchRunOutcome outcome)
    {
        var modelSets = _modelSetsList.CheckedItems.Cast<ModelSetItem>().Select(i => i.Preset.Name).ToList();
        if (modelSets.Count == 0 && _adHocModels.Count == 0)
        {
            SetStatus("Check at least one model set or add an ad-hoc model.");
            return;
        }
        var actionsAndScripts = _actionsScriptsList.CheckedItems.Cast<ActionOrScriptItem>().ToList();
        if (actionsAndScripts.Count == 0)
        {
            SetStatus("Check at least one action or script set.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_folderBox.Text))
        {
            SetStatus("Choose a destination folder.");
            return;
        }

        InitiateInputSet? initiateSpec = null;
        if (actionsAndScripts.Any(a => a.IsInitiate))
        {
            if (_initiateSetCombo.SelectedItem is not InitiateSetItem si)
            {
                SetStatus("Select an Initiate input set (create one first in Forma Batch Action → Initiate to Forma).");
                return;
            }
            initiateSpec = si.Set.Clone();
        }

        CutModelInputSet? cutModelSpec = null;
        if (actionsAndScripts.Any(a => a.IsCutModel))
        {
            if (_cutModelSetCombo.SelectedItem is not CutModelSetItem cs)
            {
                SetStatus("Select a Cut model input set (create one first in Forma Batch Action → Cut model).");
                return;
            }
            cutModelSpec = cs.Set.Clone();
        }

        ExportNwcInputSet? exportNwcSpec = null;
        if (actionsAndScripts.Any(a => a.IsExportNwc))
        {
            if (_exportNwcSetCombo.SelectedItem is not ExportNwcSetItem es)
            {
                SetStatus("Select an Export NWC options set (create one first in Multi Model Action → Export NWC).");
                return;
            }
            exportNwcSpec = es.Set.Clone();
        }

        SelectedInitiateSpec = initiateSpec;
        SelectedCutModelSpec = cutModelSpec;
        SelectedExportNwcSpec = exportNwcSpec;
        SelectedModelSetNames = modelSets;
        SelectedAdHocModels = new List<CloudModel>(_adHocModels);
        SelectedRunItems = actionsAndScripts.Select(a => (a.Name, a.IsScriptSet)).ToList();
        SelectedCompletion = (BatchCompletion)Enum.Parse(typeof(BatchCompletion),
            ((NamedItem)_completionCombo.SelectedItem!).Value);
        SelectedSyncMode = (SyncMode)Enum.Parse(typeof(SyncMode),
            ((NamedItem)_syncCombo.SelectedItem!).Value);
        DestinationFolder = _folderBox.Text.Trim();
        Settings.UserSettings.LastDestinationFolder = DestinationFolder;
        ProjectLogName = _projectLogBox.Text.Trim();
        SplitEnabled = SplitAllowed();

        Outcome = outcome;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Whether the chosen disposition permits a parallel split — see
    /// <see cref="FormLayout.SplitAllowed"/>. Changing it refreshes the summary, so the line can
    /// never promise a split that "Leave files open" has just ruled out.</summary>
    private bool SplitAllowed() =>
        FormLayout.SplitAllowed(((NamedItem?)_completionCombo.SelectedItem)?.Value);

    private void SetStatus(string text) { _status.SetMessage(text); }

    private sealed class NamedItem
    {
        public string Value { get; }
        private readonly string _label;
        public NamedItem(string value, string label) { Value = value; _label = label; }
        public override string ToString() => _label;
    }

    private sealed class ModelSetItem
    {
        public ModelPreset Preset { get; }
        public ModelSetItem(ModelPreset preset) => Preset = preset;
        public override string ToString() =>
            ModelSetAccess.Label(Preset, $"{Preset.Name}  ({Preset.Models.Count} models)");
    }

    private sealed class ActionOrScriptItem
    {
        public string Name { get; }
        public bool IsScriptSet { get; }
        public bool IsInitiate { get; }
        public bool IsCutModel { get; }
        public bool IsExportNwc { get; }
        public ActionOrScriptItem(string name, bool isScriptSet, bool isInitiate = false,
            bool isCutModel = false, bool isExportNwc = false)
        {
            Name = name; IsScriptSet = isScriptSet; IsInitiate = isInitiate; IsCutModel = isCutModel;
            IsExportNwc = isExportNwc;
        }
        public override string ToString() =>
            IsScriptSet ? $"[Script Set] {Name}"
            : IsInitiate || IsCutModel ? $"{Name}  (publishes new cloud models)"
            : IsExportNwc ? $"{Name}  (writes .nwc files)"
            : Name;
    }

    private sealed class InitiateSetItem
    {
        public InitiateInputSet Set { get; }
        public InitiateSetItem(InitiateInputSet set) => Set = set;
        public override string ToString() => $"{Set.Name}  →  {Set.FolderPath}";
    }

    private sealed class ExportNwcSetItem
    {
        public ExportNwcInputSet Set { get; }
        public ExportNwcSetItem(ExportNwcInputSet set) => Set = set;
        public override string ToString() =>
            $"{Set.Name}  →  {Set.ScopeLabel}  →  {Set.OutputLabel}";
    }

    private sealed class CutModelSetItem
    {
        public CutModelInputSet Set { get; }
        public CutModelSetItem(CutModelInputSet set) => Set = set;
        public override string ToString() => $"{Set.Name}  ({Set.Prefix})  →  {Set.FolderPath}";
    }

    private sealed class AdHocItem
    {
        public CloudModel Model { get; }
        public AdHocItem(CloudModel model) => Model = model;
        public override string ToString() => Model.IsLocal ? $"{Model.DisplayName}  (local)" : $"{Model.DisplayName}  (Forma)";
    }
}
