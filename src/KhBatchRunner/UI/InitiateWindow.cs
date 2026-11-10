using System.IO;
using KhBatchRunner.Forma;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Dialog for Batch Initiate to Forma: pick a model set (Forma + local models both supported), the
/// TARGET Forma folder to publish into, an optional name prefix/suffix, and where to write the run
/// logs. Open mode is fixed by the action (sources open detached — never locking a source
/// central), so there's no open-mode choice here; the note spells out that this creates new cloud
/// models. All inputs are savable/loadable as named <see cref="InitiateInputSet"/>s (the "Input
/// set" row — these also seed Initiate inside Batch Run / Schedule Run), and the last committed
/// inputs are auto-restored when the dialog opens. Built in code, themed to Revit.
/// </summary>
public sealed class InitiateWindow : Form
{
    private readonly DataManagementClient _client;
    private readonly ComboBox _setCombo;
    private readonly ComboBox _presetCombo;
    private readonly TextBox _targetBox;
    private readonly ComboBox _sepCombo;
    private readonly ComboBox _prefixModeCombo;
    private readonly TextBox _prefixBox;
    private readonly TextBox _suffixBox;
    private readonly TextBox _folderBox;
    private readonly TextBox _projectLogBox;
    private readonly ComboBox _completionCombo;
    private readonly Button _okButton;

    // Target folder identity — from the Forma picker or a loaded input set (raw ids; the GUIDs
    // are derived on use by PublishTarget.FromIdentifiers).
    private string _hubId = "";
    private string _projectId = "";
    private string _folderUrn = "";
    private string _folderPath = "";

    // Name of the currently-loaded input set (null for unsaved inputs). If Save changes the
    // name, the old file is deleted so it's a rename, not a duplicate — sets are keyed by
    // file name (same rule as model/script sets).
    private string? _loadedSetName;

    private readonly InputSetActions<InitiateInputSet> _sets;

    public ModelPreset? SelectedPreset { get; private set; }

    /// <summary>Everything the dialog collected, committed — target, naming and log fields.
    /// The command resolves the publish target via <see cref="InitiateInputSet.ToPublishTarget"/>.</summary>
    public InitiateInputSet? Inputs { get; private set; }

    public BatchCompletion SelectedCompletion { get; private set; } = BatchCompletion.CloseDocuments;

    /// <summary>
    /// Whether this run may be split across several fresh Revit instances. False for "Leave files
    /// open" (a group's Revit has to close itself) — see <see cref="FormLayout.SplitAllowed"/>.
    /// The tool sizes the groups; the caller only says yes or no.
    /// </summary>
    public bool SplitEnabled { get; private set; }

    public InitiateWindow(IReadOnlyList<ModelPreset> presets, DataManagementClient client)
    {
        _client = client;

        Text = "Batch Initiate to Forma";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(600, 360);
        MinimumSize = new System.Drawing.Size(560, 0);

        // --- Input set (save/load every field below as a named set) ---
        _setCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _setCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_setCombo.SelectedItem is SetItem item) ApplyInputs(item.Set, loaded: true);
        };
        _sets = new InputSetActions<InitiateInputSet>(this, _setCombo,
            InitiateSetStore.LoadAll, InitiateSetStore.Exists, InitiateSetStore.Save, InitiateSetStore.Delete,
            s => s.Name, s => s.Clone(), (s, name) => s.Name = name,
            s => new SetItem(s), o => (o as SetItem)?.Set, Warn);
        var saveSet = new Button { Text = "Save set", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        saveSet.Click += (_, _) => SaveInputSet();
        var dupSet = new Button { Text = "Duplicate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        dupSet.Click += (_, _) => DuplicateInputSet();
        var deleteSet = new Button { Text = "Delete", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(70, 0) };
        deleteSet.Click += (_, _) => DeleteInputSet();

        _presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        // Ordered, labelled and dimmed by the signed-in-account check, and rebuilt when that answer
        // lands — the whole treatment, in the one call that carries it (ModelSetAccess.BindSetCombo).
        // This was a bare foreach until 1.5.1.2, which is why a set this account cannot open looked
        // exactly like every other set here while Edit Model Set was flagging it.
        ModelSetAccess.BindSetCombo(_presetCombo, presets,
            p => new PresetItem(p), i => (i as PresetItem)?.Preset);

        _targetBox = new TextBox { ReadOnly = true, Text = "(none selected)" };
        var pick = new Button { Text = "Pick Forma folder…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(120, 0) };
        pick.Click += (_, _) => PickTargetFolder();

        _sepCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _sepCombo.Items.AddRange(new object[] { "-  (dash)", "_  (underscore)", ".  (dot)" });
        _sepCombo.SelectedIndex = 0;
        _prefixModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _prefixModeCombo.Items.AddRange(new object[] { "Add prefix", "Rename prefix (replace leading token)" });
        // Default to "Rename prefix" (index 1). A committed run's choice is auto-restored from the
        // last-used set (ApplyInputs) and overrides this, so this default only applies on a fresh
        // dialog with no saved last-used.
        _prefixModeCombo.SelectedIndex = 1;
        _prefixBox = new TextBox();
        _suffixBox = new TextBox();

        _folderBox = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        browse.Click += (_, _) => BrowseForFolder();

        _projectLogBox = new TextBox { Text = "Initiate to Forma" };

        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseDocuments, "Close files"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.LeaveOpen, "Leave files open"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseRevit, "Close Revit when complete"));
        _completionCombo.SelectedIndex = 2; // Close Revit when complete (default) — see BatchRunWindow

        _okButton = new Button { Text = "Run", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        _okButton.Click += (_, _) => Commit();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };

        Label note = FormLayout.NoteLabel(this,
            "Publishes each model in the set as a NEW cloud model in the target Forma folder. " +
            "Sources open detached (a workshared source central is never locked). A name " +
            "collision in the target folder is logged as an error for that model.");

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Input set:", _setCombo, saveSet, dupSet, deleteSet));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Model set:", _presetCombo));
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Target Forma folder:", _targetBox, pick));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Separator:", _sepCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Prefix mode:", _prefixModeCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Prefix:", _prefixBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Suffix:", _suffixBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("When finished:", _completionCombo));
        FormLayout.AddAutoRow(root, FormLayout.NoteLabel(this, FormLayout.SplitNote));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Log destination folder:", _folderBox, browse));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Project log name:", _projectLogBox));
        var buttons = FormLayout.FlowRow(_okButton, cancel);
        buttons.Anchor = AnchorStyles.Right;
        FormLayout.AddAutoRow(root, buttons);

        Controls.Add(root);
        AcceptButton = _okButton;
        CancelButton = cancel;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);
        ClientSize = new System.Drawing.Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width), root.PreferredSize.Height);
        MinimumSize = Size; // resizable, but never smaller than the fitted content (all controls shown)

        // Populate the saved sets and restore the last committed inputs (which also selects
        // the matching set if the last commit came from one).
        ReloadSetCombo(selectName: null);
        InitiateInputSet? lastUsed = InitiateSetStore.TryLoadLastUsed();
        if (lastUsed != null)
        {
            ApplyInputs(lastUsed, loaded: false);
            if (!string.IsNullOrEmpty(lastUsed.Name))
                SelectSetByName(lastUsed.Name); // fires ApplyInputs(loaded:true) if it still exists
        }
    }

    // ---- input sets ------------------------------------------------------------

    private void ReloadSetCombo(string? selectName) => _sets.Reload(selectName);

    private void SelectSetByName(string name) => _sets.SelectByName(name);

    /// <summary>Fills every control from the given inputs. <paramref name="loaded"/> marks it as
    /// the currently-loaded NAMED set (rename-on-save semantics); false for last-used restores.</summary>
    private void ApplyInputs(InitiateInputSet s, bool loaded)
    {
        _loadedSetName = loaded && !string.IsNullOrEmpty(s.Name) ? s.Name : null;

        if (!string.IsNullOrEmpty(s.ModelSetName))
        {
            for (int i = 0; i < _presetCombo.Items.Count; i++)
                if (((PresetItem)_presetCombo.Items[i]).Preset.Name.Equals(s.ModelSetName, StringComparison.OrdinalIgnoreCase))
                {
                    _presetCombo.SelectedIndex = i;
                    break;
                }
        }

        _hubId = s.HubId;
        _projectId = s.ProjectId;
        _folderUrn = s.FolderUrn;
        _folderPath = s.FolderPath;
        _targetBox.Text = s.HasTarget ? s.FolderPath : "(none selected)";

        _sepCombo.SelectedIndex = s.Separator switch { "_" => 1, "." => 2, _ => 0 };
        _prefixModeCombo.SelectedIndex = s.PrefixMode == PublishPrefixMode.Rename ? 1 : 0;
        _prefixBox.Text = s.Prefix;
        _suffixBox.Text = s.Suffix;
        if (!string.IsNullOrWhiteSpace(s.DestinationFolder)) _folderBox.Text = s.DestinationFolder;
        if (!string.IsNullOrWhiteSpace(s.ProjectLogName)) _projectLogBox.Text = s.ProjectLogName;
    }

    /// <summary>Snapshot of every control as an <see cref="InitiateInputSet"/>.</summary>
    private InitiateInputSet BuildSnapshot(string name) => new()
    {
        Name = name,
        ModelSetName = (_presetCombo.SelectedItem as PresetItem)?.Preset.Name ?? "",
        HubId = _hubId,
        ProjectId = _projectId,
        FolderUrn = _folderUrn,
        FolderPath = _folderPath,
        Separator = _sepCombo.SelectedIndex switch { 1 => "_", 2 => ".", _ => "-" },
        PrefixMode = _prefixModeCombo.SelectedIndex == 1 ? PublishPrefixMode.Rename : PublishPrefixMode.Add,
        Prefix = _prefixBox.Text.Trim(),
        Suffix = _suffixBox.Text.Trim(),
        DestinationFolder = _folderBox.Text.Trim(),
        ProjectLogName = _projectLogBox.Text.Trim()
    };

    private void SaveInputSet() => _sets.Save(ref _loadedSetName, BuildSnapshot,
        () => string.IsNullOrEmpty(_folderUrn) ? "Pick a target Forma folder before saving the set." : null);

    /// <summary>Forks the selected set to a new "&lt;name&gt; (copy)" and loads the copy — see
    /// <see cref="InputSetActions{T}.Duplicate"/>.</summary>
    private void DuplicateInputSet() => _sets.Duplicate();

    private void DeleteInputSet() => _sets.Delete(ref _loadedSetName);

    private void PickTargetFolder()
    {
        using var picker = new FormaFolderPickerWindow(
            _client, "Initiate to Forma — target folder", "Select the destination Forma folder to publish into:");
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedFolder is null) return;
        _hubId = picker.SelectedFolder.HubId;
        _projectId = picker.SelectedFolder.ProjectId;
        _folderUrn = picker.SelectedFolder.Id;
        _folderPath = picker.SelectedFolderPath;
        _targetBox.Text = picker.SelectedFolderPath;
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
        if (_presetCombo.SelectedItem is not PresetItem item)
        {
            Warn("Select a model set.");
            return;
        }
        if (string.IsNullOrEmpty(_folderUrn))
        {
            Warn("Pick a target Forma folder to publish into.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_folderBox.Text))
        {
            Warn("Choose a log destination folder.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_projectLogBox.Text))
        {
            Warn("Enter a project log name.");
            return;
        }

        SelectedPreset = item.Preset;
        Inputs = BuildSnapshot(_loadedSetName ?? "");
        SelectedCompletion = (_completionCombo.SelectedItem as CompletionItem)?.Completion
                             ?? BatchCompletion.CloseDocuments;
        SplitEnabled = FormLayout.SplitAllowed((_completionCombo.SelectedItem as CompletionItem)?.Completion);
        // Auto-restore these inputs the next time the dialog opens.
        InitiateSetStore.SaveLastUsed(Inputs);
    }

    private void Warn(string text)
    {
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        DialogResult = DialogResult.None;
    }

    private sealed class PresetItem
    {
        public ModelPreset Preset { get; }
        public PresetItem(ModelPreset preset) => Preset = preset;
        public override string ToString() =>
            ModelSetAccess.Label(Preset, $"{Preset.Name}  ({Preset.Models.Count} models)");
    }

    private sealed class SetItem
    {
        public InitiateInputSet Set { get; }
        public SetItem(InitiateInputSet set) => Set = set;
        public override string ToString() => Set.Name;
    }

    private sealed class CompletionItem
    {
        public BatchCompletion Completion { get; }
        private readonly string _label;
        public CompletionItem(BatchCompletion completion, string label) { Completion = completion; _label = label; }
        public override string ToString() => _label;
    }
}
