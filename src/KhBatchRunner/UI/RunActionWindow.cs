using System.IO;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Modal dialog shown when a Multi Model Check/Action button is clicked. Choices:
///   1. Model set        — which saved set of models to run against.
///   2. When finished    — leave the opened files open, close them, or close Revit.
///   3. Destination folder + project log name — where the tracking and detail logs land.
/// Open mode is no longer a choice: it's fixed by the action's <see cref="ActionCategory"/> —
/// a Check opens each model detached (Read-only), an Action stays attached and syncs — and
/// every model opens with all worksets closed. Built in code (no designer file).
/// </summary>
public sealed class RunActionWindow : Form
{
    private readonly DimmableCheckedListBox _presetsList;
    /// <summary>The sets this dialog was handed, kept so the list can be rebuilt when the
    /// account snapshot arrives and changes their order.</summary>
    private readonly IReadOnlyList<ModelPreset> _allPresets = Array.Empty<ModelPreset>();
    private readonly ComboBox? _syncCombo;
    private readonly ComboBox _completionCombo;
    private readonly TextBox _folderBox;
    private readonly TextBox _projectLogBox;
    private readonly Button _okButton;
    private readonly Button _browseButton;
    private readonly CheckBox? _activeModelCheck;
    private readonly Label _note;
    private readonly string _batchNote;
    private readonly string _activeNote;

    public IReadOnlyList<ModelPreset> SelectedPresets { get; private set; } = Array.Empty<ModelPreset>();
    public string? DestinationFolder { get; private set; }
    public string? ProjectLogName { get; private set; }
    public BatchCompletion SelectedCompletion { get; private set; } = BatchCompletion.CloseDocuments;
    public SyncMode SelectedSyncMode { get; private set; } = SyncMode.Sync;

    /// <summary>Whether the run may split across parallel Revit instances. Always false in
    /// "run on active model" mode — there is one document and it is already open here. The size is
    /// derived by the caller from the resolved model count
    /// (<see cref="Schedule.InstanceBudget.AutoGroupSize"/>).</summary>
    public bool SplitEnabled { get; private set; }

    /// <summary>True when the user chose to run against the model open in Revit rather than a
    /// model set — no logs, results shown on screen. Set only when <see cref="ActiveModelName"/>
    /// was non-null (a runnable active document exists).</summary>
    public bool RunOnActiveModel { get; private set; }

    public RunActionWindow(IReadOnlyList<ModelPreset> presets, string actionName, ActionCategory category,
        string? activeModelName = null)
    {
        Text = $"Run: {actionName}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        // Scale the whole dialog (positions, sizes, fonts together) with Revit's DPI so
        // labels never grow into their fields on high-DPI displays.
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        // Width is fixed (also drives the note's wrap width); the height is fitted to the
        // actual content at the end of the constructor, after the rows are built.
        ClientSize = new System.Drawing.Size(560, 320);
        MinimumSize = new System.Drawing.Size(520, 0);

        // --- Model sets (one or more; combined into a single run) ---
        // DimmableCheckedListBox, not CheckedListBox: a plain one cannot be owner-drawn, so the
        // account greying was invisible here until 1.5.1.2. See that class.
        _presetsList = new DimmableCheckedListBox { CheckOnClick = true, IntegralHeight = false, Height = 130, Dock = DockStyle.Top };
        // Labels, dims and orders by the account snapshot — and ensures one exists. This window has
        // no Forma client of its own, which used to mean it simply read whatever another window had
        // cached, and showed nothing at all when nothing had. See ModelSetAccess.EnsureSnapshot.
        _allPresets = presets;
        ModelSetAccess.AttachGreying(_presetsList, i => (i as PresetItem)?.Preset,
            onSnapshotReady: RefreshPresets);
        RefreshPresets();
        if (_presetsList.Items.Count == 1)
            _presetsList.SetItemChecked(0, true);
        CheckListBehavior.EnableRangeCheck(_presetsList);

        // --- Model treatment (write actions only — a Check never persists, so no combo) ---
        if (category == ActionCategory.Action)
        {
            _syncCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _syncCombo.Items.Add(new SyncItem(SyncMode.Sync, "Sync changes to central"));
            _syncCombo.Items.Add(new SyncItem(SyncMode.NoSync, "Don't sync (dry run)"));
            _syncCombo.SelectedIndex = 1; // Dry run (default) — the irreversible option is opt-in.
        }

        // --- When finished (post-run disposition) ---
        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseDocuments, "Close files"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.LeaveOpen, "Leave files open"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseRevit, "Close Revit when complete"));
        _completionCombo.SelectedIndex = 2; // Close Revit when complete (default) — see BatchRunWindow
        Label splitNote = FormLayout.NoteLabel(this, FormLayout.SplitNote);

        // --- Destination folder (holds both the Log\ tracking file and the detail CSV) ---
        _folderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        _browseButton = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        _browseButton.Click += (_, _) => BrowseForFolder();

        // --- Project log name (used to name both log files: <ProjectLog>_<Action>.csv) ---
        _projectLogBox = new TextBox { Text = DefaultProjectLogName(actionName) };

        // --- OK / Cancel ---
        _okButton = new Button { Text = "Run", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        _okButton.Click += (_, _) => Commit();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };

        // --- Run on active model (skips the whole batch: no logs, results shown on screen) ---
        if (activeModelName != null)
        {
            _activeModelCheck = new CheckBox
            {
                Text = $"Run on active model only — “{activeModelName}” (no logs, results shown on screen)",
                AutoSize = true,
                Margin = new Padding(3, 2, 3, 2)
            };
            _activeModelCheck.CheckedChanged += (_, _) => OnActiveModelToggled();
        }

        // --- Category note (what this run will actually do to the model) ---
        _batchNote = category == ActionCategory.Action
            ? "This action writes to the model: each model opens with all worksets closed, then " +
              "syncs to central and relinquishes (cloud/workshared) or saves (local file) — or, " +
              "as a dry run, discards its changes and relinquishes everything."
            : "Read-only — each model opens with all worksets closed and nothing is written back.";
        // The default disposition now ends the session, and shows no summary dialog — say so.
        // Batch note only: in active-model mode the disposition is moot and this note is swapped out.
        _batchNote += " When finished, Revit closes by default and the run summary goes to the log.";
        _activeNote = category == ActionCategory.Action
            ? "Runs on the model open in Revit right now. Changes are made but NOT saved or synced — " +
              "review them, then save/sync or press Ctrl+Z to undo."
            : "Runs the check on the model open in Revit right now. Nothing is written — no CSV, no Excel.";
        Label note = FormLayout.NoteLabel(this, _batchNote);
        _note = note;

        // Responsive single-column layout: labels auto-size, fields stretch — no overlap at DPI.
        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, new Label { Text = "Model set(s):", AutoSize = true, Margin = new Padding(3, 4, 3, 2) });
        FormLayout.AddAutoRow(root, _presetsList);
        if (_activeModelCheck != null)
            FormLayout.AddAutoRow(root, _activeModelCheck);
        FormLayout.AddAutoRow(root, note);
        if (_syncCombo != null)
            FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Model treatment:", _syncCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("When finished:", _completionCombo));
        FormLayout.AddAutoRow(root, splitNote);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Destination folder:", _folderBox, _browseButton));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Project log name:", _projectLogBox));
        var buttons = FormLayout.FlowRow(_okButton, cancel);
        buttons.Anchor = AnchorStyles.Right;
        FormLayout.AddAutoRow(root, buttons);

        Controls.Add(root);
        AcceptButton = _okButton;
        CancelButton = cancel;

        // Follow Revit's light/dark theme, then dim the note so it reads as secondary text.
        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);

        // Removing the Model-treatment / Worksets rows left dead space at the old fixed height —
        // shrink to the layout's preferred height so the dialog fits its content snugly.
        ClientSize = new System.Drawing.Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width), root.PreferredSize.Height);
        MinimumSize = Size; // resizable, but never smaller than the fitted content (all controls shown)

        // With no saved model sets, running against the open model is the only useful thing to
        // do — pre-check it so the dialog is still actionable (toggling disables the batch fields).
        if (_activeModelCheck != null && presets.Count == 0)
            _activeModelCheck.Checked = true;

        splitNote.ForeColor = Theme.Dim(splitNote);
    }

    /// <summary>Enable/disable the batch-only fields and swap the note when "run on active model"
    /// is toggled — in active mode the model set, sync, completion, folder and log are all moot.</summary>
    private void OnActiveModelToggled()
    {
        bool active = _activeModelCheck!.Checked;
        _presetsList.Enabled = !active;
        if (_syncCombo != null) _syncCombo.Enabled = !active;
        _completionCombo.Enabled = !active;
        _folderBox.Enabled = !active;
        _browseButton.Enabled = !active;
        _projectLogBox.Enabled = !active;
        _note.Text = active ? _activeNote : _batchNote;
        _okButton.Text = active ? "Run on active" : "Run";
    }

    private static string DefaultProjectLogName(string actionName)
    {
        string safe = string.Join("_", actionName.Split(Path.GetInvalidFileNameChars()));
        return safe;
    }

    private void BrowseForFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(_folderBox.Text)
                ? _folderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _folderBox.Text = dlg.SelectedPath;
    }

    private void Commit()
    {
        // Active-model mode ignores every batch field — no preset/folder/log to validate, and
        // no LastDestinationFolder to remember (nothing is written).
        if (_activeModelCheck?.Checked == true)
        {
            RunOnActiveModel = true;
            return;
        }

        var chosen = _presetsList.CheckedItems.Cast<PresetItem>().Select(i => i.Preset).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "Check at least one model set.", "Run",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        if (string.IsNullOrWhiteSpace(_folderBox.Text))
        {
            MessageBox.Show(this, "Choose a destination folder.", "Run",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        if (string.IsNullOrWhiteSpace(_projectLogBox.Text))
        {
            MessageBox.Show(this, "Enter a project log name.", "Run",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        SelectedPresets = chosen;
        DestinationFolder = _folderBox.Text.Trim();
        Settings.UserSettings.LastDestinationFolder = DestinationFolder;
        ProjectLogName = _projectLogBox.Text.Trim();
        SelectedCompletion = (_completionCombo.SelectedItem as CompletionItem)?.Completion
                             ?? BatchCompletion.CloseDocuments;
        SelectedSyncMode = (_syncCombo?.SelectedItem as SyncItem)?.Mode ?? SyncMode.Sync;
        // Active-model mode has exactly one document, already open in this Revit — nothing to split.
        SplitEnabled = _activeModelCheck?.Checked != true &&
                       FormLayout.SplitAllowed((_completionCombo.SelectedItem as CompletionItem)?.Completion);
    }

    /// <summary>Combo item that shows the preset name + model count but keeps the object.</summary>
    /// <summary>Repopulates the set list, preserving ticks. Re-run when the account snapshot lands,
    /// because the ORDER depends on the verdict and is fixed at populate time.</summary>
    private void RefreshPresets()
    {
        var ticked = new HashSet<string>(
            _presetsList.CheckedItems.Cast<PresetItem>().Select(i => ModelSetAccess.SetKey(i.Preset)),
            StringComparer.OrdinalIgnoreCase);

        _presetsList.BeginUpdate();
        _presetsList.Items.Clear();
        foreach (ModelPreset p in ModelSetAccess.Ordered(ModelSetAccess.Visible(_allPresets)))
        {
            int i = _presetsList.Items.Add(new PresetItem(p));
            if (ticked.Contains(ModelSetAccess.SetKey(p))) _presetsList.SetItemChecked(i, true);
        }
        _presetsList.EndUpdate();
    }

    private sealed class PresetItem
    {
        public ModelPreset Preset { get; }
        public PresetItem(ModelPreset preset) => Preset = preset;
        public override string ToString() =>
            ModelSetAccess.Label(Preset, $"{Preset.Name}  ({Preset.Models.Count} models)");
    }

    /// <summary>Combo item mapping a friendly label to a <see cref="SyncMode"/>.</summary>
    private sealed class SyncItem
    {
        public SyncMode Mode { get; }
        private readonly string _label;
        public SyncItem(SyncMode mode, string label) { Mode = mode; _label = label; }
        public override string ToString() => _label;
    }

    /// <summary>Combo item mapping a friendly label to a <see cref="BatchCompletion"/>.</summary>
    private sealed class CompletionItem
    {
        public BatchCompletion Completion { get; }
        private readonly string _label;
        public CompletionItem(BatchCompletion completion, string label) { Completion = completion; _label = label; }
        public override string ToString() => _label;
    }
}
