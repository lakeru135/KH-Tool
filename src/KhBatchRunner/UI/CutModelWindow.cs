using System.IO;
using KhBatchRunner.Forma;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Dialog for Forma Batch Action → Cut model: pick a model set, the TARGET Forma folder to publish
/// into, and ONE site code that drives BOTH the site-location match on the temp copy and the
/// publish name prefix (Rename mode: "XXX-AR" + "BETA2A" → "BETA2A-AR", mirroring the location
/// name "BETA2A-AR"), plus the option to delete the non-matching locations. Sources open
/// detached — every edit happens on a local temp copy; the source model is never modified.
/// All inputs are savable/loadable as named <see cref="CutModelInputSet"/>s and the last
/// committed inputs are auto-restored when the dialog opens. Built in code, themed to Revit
/// (a sibling of <see cref="InitiateWindow"/>).
/// </summary>
public sealed class CutModelWindow : Form
{
    private readonly DataManagementClient _client;
    private readonly ComboBox _setCombo;
    private readonly ComboBox _presetCombo;
    private readonly TextBox _targetBox;
    private readonly ComboBox _sepCombo;
    private readonly TextBox _siteCodeBox;
    private readonly TextBox _suffixBox;
    private readonly CheckBox _deleteOthersCheck;
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
    // file name (same rule as model/script/Initiate sets).
    private string? _loadedSetName;

    public ModelPreset? SelectedPreset { get; private set; }

    /// <summary>Everything the dialog collected, committed — target, site code, naming and log
    /// fields. The command resolves the publish target via <see cref="InitiateInputSet.ToPublishTarget"/>.</summary>
    public CutModelInputSet? Inputs { get; private set; }

    public BatchCompletion SelectedCompletion { get; private set; } = BatchCompletion.CloseDocuments;

    /// <summary>
    /// Whether this run may be split across several fresh Revit instances. False for "Leave files
    /// open" (a group's Revit has to close itself) — see <see cref="FormLayout.SplitAllowed"/>.
    /// The tool sizes the groups; the caller only says yes or no.
    /// </summary>
    public bool SplitEnabled { get; private set; }

    public CutModelWindow(IReadOnlyList<ModelPreset> presets, DataManagementClient client)
    {
        _client = client;

        Text = "Cut Model — Forma Batch Action";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(600, 380);
        MinimumSize = new System.Drawing.Size(560, 0);

        // --- Input set (save/load every field below as a named set) ---
        _setCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _setCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_setCombo.SelectedItem is SetItem item) ApplyInputs(item.Set, loaded: true);
        };
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
        _siteCodeBox = new TextBox();
        _suffixBox = new TextBox();
        _deleteOthersCheck = new CheckBox
        {
            Text = "Delete the other (non-matching) site locations from the cut copy",
            AutoSize = true,
            Margin = new Padding(3, 4, 3, 4)
        };

        _folderBox = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        browse.Click += (_, _) => BrowseForFolder();

        _projectLogBox = new TextBox { Text = "Cut model" };

        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseDocuments, "Close files"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.LeaveOpen, "Leave files open"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseRevit, "Close Revit when complete"));
        _completionCombo.SelectedIndex = 2; // Close Revit when complete (default) — see BatchRunWindow

        _okButton = new Button { Text = "Run", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        _okButton.Click += (_, _) => Commit();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };

        Label note = FormLayout.NoteLabel(this,
            "Cuts each model for one site: saves a local temp copy (the source is never " +
            "modified), activates the site location matching the site code (e.g. \"BETA2A\" " +
            "matches \"BETA2A-AR\"), then publishes it as a NEW cloud model named with the same " +
            "site code as prefix. A model with no matching location is logged as an error and " +
            "not published.");

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Input set:", _setCombo, saveSet, dupSet, deleteSet));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Model set:", _presetCombo));
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Target Forma folder:", _targetBox, pick));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Site code:", _siteCodeBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Separator:", _sepCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Suffix:", _suffixBox));
        FormLayout.AddAutoRow(root, _deleteOthersCheck);
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
        CutModelInputSet? lastUsed = CutModelSetStore.TryLoadLastUsed();
        if (lastUsed != null)
        {
            ApplyInputs(lastUsed, loaded: false);
            if (!string.IsNullOrEmpty(lastUsed.Name))
                SelectSetByName(lastUsed.Name); // fires ApplyInputs(loaded:true) if it still exists
        }
    }

    // ---- input sets ------------------------------------------------------------

    private void ReloadSetCombo(string? selectName)
    {
        _setCombo.Items.Clear();
        foreach (CutModelInputSet set in CutModelSetStore.LoadAll())
            _setCombo.Items.Add(new SetItem(set));
        if (selectName != null) SelectSetByName(selectName);
    }

    private void SelectSetByName(string name)
    {
        for (int i = 0; i < _setCombo.Items.Count; i++)
        {
            if (((SetItem)_setCombo.Items[i]).Set.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _setCombo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Fills every control from the given inputs. <paramref name="loaded"/> marks it as
    /// the currently-loaded NAMED set (rename-on-save semantics); false for last-used restores.</summary>
    private void ApplyInputs(CutModelInputSet s, bool loaded)
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
        _siteCodeBox.Text = s.Prefix;
        _suffixBox.Text = s.Suffix;
        _deleteOthersCheck.Checked = s.DeleteOtherLocations;
        if (!string.IsNullOrWhiteSpace(s.DestinationFolder)) _folderBox.Text = s.DestinationFolder;
        if (!string.IsNullOrWhiteSpace(s.ProjectLogName)) _projectLogBox.Text = s.ProjectLogName;
    }

    /// <summary>Snapshot of every control as a <see cref="CutModelInputSet"/> — PrefixMode is
    /// always Rename (the site code replaces the source name's leading token).</summary>
    private CutModelInputSet BuildSnapshot(string name) => new()
    {
        Name = name,
        ModelSetName = (_presetCombo.SelectedItem as PresetItem)?.Preset.Name ?? "",
        HubId = _hubId,
        ProjectId = _projectId,
        FolderUrn = _folderUrn,
        FolderPath = _folderPath,
        Separator = _sepCombo.SelectedIndex switch { 1 => "_", 2 => ".", _ => "-" },
        PrefixMode = PublishPrefixMode.Rename,
        Prefix = _siteCodeBox.Text.Trim(),
        Suffix = _suffixBox.Text.Trim(),
        DeleteOtherLocations = _deleteOthersCheck.Checked,
        DestinationFolder = _folderBox.Text.Trim(),
        ProjectLogName = _projectLogBox.Text.Trim()
    };

    private void SaveInputSet()
    {
        string name = _setCombo.Text.Trim();
        if (name.Length == 0)
        {
            Warn("Type a name for the input set (in the Input set box), then Save set.");
            return;
        }
        if (string.IsNullOrEmpty(_folderUrn))
        {
            Warn("Pick a target Forma folder before saving the set.");
            return;
        }
        bool renaming = _loadedSetName != null &&
                        !name.Equals(_loadedSetName, StringComparison.OrdinalIgnoreCase);
        if (CutModelSetStore.Exists(name) &&
            (_loadedSetName == null || renaming))
        {
            if (MessageBox.Show(this, $"An input set named '{name}' already exists. Overwrite it?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }

        CutModelSetStore.Save(BuildSnapshot(name));
        // Same rename rule as model/script/Initiate sets: saving a loaded set under a new name
        // deletes the old file (a rename, not a silent duplicate).
        if (renaming) CutModelSetStore.Delete(_loadedSetName!);
        _loadedSetName = name;
        ReloadSetCombo(selectName: name);
    }

    /// <summary>
    /// Forks the selected set to a new "&lt;name&gt; (copy)" and loads the copy.
    ///
    /// <para>Until 1.4.7 there was no way to do this. The combo is editable, so loading a set and
    /// typing a new name LOOKS like a fork — but <see cref="SaveInputSet"/> reads that as a rename
    /// and deletes the original file, so the obvious gesture destroyed the set being copied
    /// from.</para>
    /// </summary>
    private void DuplicateInputSet()
    {
        if (_setCombo.SelectedItem is not SetItem item)
        {
            Warn("Select a saved input set to duplicate.");
            return;
        }
        CutModelInputSet copy = item.Set.Clone();
        copy.Name = SetDuplication.UniqueName(item.Set.Name, CutModelSetStore.LoadAll().Select(s => s.Name));
        try
        {
            CutModelSetStore.Save(copy);
        }
        catch (Exception ex)
        {
            Warn("Could not duplicate the input set: " + ex.Message);
            return;
        }
        // Selecting the new row raises SelectedIndexChanged -> ApplyInputs(loaded: true), which is
        // what re-points _loadedSetName at the COPY. Leave it on the original and the very next
        // "Save set" takes the rename branch and deletes the set just copied from.
        ReloadSetCombo(selectName: copy.Name);
    }

    private void DeleteInputSet()
    {
        if (_setCombo.SelectedItem is not SetItem item)
        {
            Warn("Select a saved input set to delete.");
            return;
        }
        if (MessageBox.Show(this, $"Delete input set '{item.Set.Name}'?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        CutModelSetStore.Delete(item.Set.Name);
        if (_loadedSetName != null && _loadedSetName.Equals(item.Set.Name, StringComparison.OrdinalIgnoreCase))
            _loadedSetName = null;
        ReloadSetCombo(selectName: null);
        _setCombo.Text = "";
    }

    private void PickTargetFolder()
    {
        using var picker = new FormaFolderPickerWindow(
            _client, "Cut model — target folder", "Select the destination Forma folder to publish the cut copies into:");
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
        if (string.IsNullOrWhiteSpace(_siteCodeBox.Text))
        {
            Warn("Enter a site code — it selects the site location and becomes the name prefix.");
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
        CutModelSetStore.SaveLastUsed(Inputs);
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
        public CutModelInputSet Set { get; }
        public SetItem(CutModelInputSet set) => Set = set;
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
