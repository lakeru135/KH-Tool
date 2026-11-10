using System.IO;
using KhBatchRunner.Forma;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Dialog for Forma Batch Action → Internal Relink: pick the HOST model set (the models whose links get
/// re-pointed), the Forma folders to scan for the NEW-prefix target models, and one OLD → NEW site
/// code pair that remaps each link name (leading token before the first separator; e.g. "OLD-ST-BLDA"
/// → "SITE1A-ST-BLDA"). All inputs save/load as named <see cref="RelinkInputSet"/>s (self-contained —
/// the target folders are snapshotted in), with the last committed inputs auto-restored. A sibling
/// of <see cref="CutModelWindow"/>.
/// </summary>
public sealed class RelinkModelWindow : Form
{
    private readonly DataManagementClient _client;
    private readonly ComboBox _setCombo;
    private readonly ComboBox _presetCombo;
    private readonly ListBox _foldersList;
    private readonly CheckBox _subfoldersCheck;
    private readonly ComboBox _sepCombo;
    private readonly TextBox _oldCodeBox;
    private readonly TextBox _newCodeBox;
    private readonly CheckBox _exactNameCheck;
    private readonly TextBox _folderBox;
    private readonly TextBox _projectLogBox;
    private readonly ComboBox _completionCombo;
    private readonly Button _okButton;

    // Name of the currently-loaded input set (null for unsaved inputs) — rename-on-save semantics.
    private string? _loadedSetName;

    private readonly InputSetActions<RelinkInputSet> _sets;

    /// <summary>The chosen host model set (may itself be a folder-backed set, resolved at run time).</summary>
    public ModelPreset? SelectedPreset { get; private set; }

    /// <summary>Everything the dialog collected, committed.</summary>
    public RelinkInputSet? Inputs { get; private set; }

    public BatchCompletion SelectedCompletion { get; private set; } = BatchCompletion.CloseDocuments;

    /// <summary>
    /// Whether this run may be split across several fresh Revit instances. False for "Leave files
    /// open" (a group's Revit has to close itself) — see <see cref="FormLayout.SplitAllowed"/>.
    /// The tool sizes the groups; the caller only says yes or no.
    /// </summary>
    public bool SplitEnabled { get; private set; }

    public RelinkModelWindow(IReadOnlyList<ModelPreset> presets, DataManagementClient client)
    {
        _client = client;

        Text = "Internal Relink — Forma Batch Action";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(620, 460);
        MinimumSize = new System.Drawing.Size(580, 0);

        // --- Input set (save/load every field below as a named set) ---
        _setCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _setCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_setCombo.SelectedItem is SetItem item) ApplyInputs(item.Set, loaded: true);
        };
        _sets = new InputSetActions<RelinkInputSet>(this, _setCombo,
            RelinkSetStore.LoadAll, RelinkSetStore.Exists, RelinkSetStore.Save, RelinkSetStore.Delete,
            s => s.Name, s => s.Clone(), (s, name) => s.Name = name,
            s => new SetItem(s), o => (o as SetItem)?.Set, Warn);
        var saveSet = new Button { Text = "Save set", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        saveSet.Click += (_, _) => SaveInputSet();
        var dupSet = new Button { Text = "Duplicate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        dupSet.Click += (_, _) => DuplicateInputSet();
        var deleteSet = new Button { Text = "Delete", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(70, 0) };
        deleteSet.Click += (_, _) => DeleteInputSet();

        // Hosts come from a saved model set (which may itself be a folder-backed set — see
        // Edit Model Set → "Save folder as set", resolved to its current models at run time).
        _presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        // Ordered, labelled and dimmed by the signed-in-account check, and rebuilt when that answer
        // lands — the whole treatment, in the one call that carries it (ModelSetAccess.BindSetCombo).
        // This was a bare foreach until 1.5.1.2, which is why a set this account cannot open looked
        // exactly like every other set here while Edit Model Set was flagging it.
        ModelSetAccess.BindSetCombo(_presetCombo, presets,
            p => new PresetItem(p), i => (i as PresetItem)?.Preset);

        // MinimumSize (not just Height) so LabeledRow's Dock.Fill can't collapse it in the auto-size row.
        // MultiExtended so Shift/Ctrl range-select works and Remove can drop several folders at once.
        _foldersList = new ListBox
        {
            Height = 92, IntegralHeight = false, MinimumSize = new System.Drawing.Size(0, 92),
            SelectionMode = SelectionMode.MultiExtended
        };
        var addFolder = new Button { Text = "Add folder…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(100, 0) };
        addFolder.Click += (_, _) => AddTargetFolder();
        var removeFolder = new Button { Text = "Remove", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        removeFolder.Click += (_, _) =>
        {
            foreach (object item in _foldersList.SelectedItems.Cast<object>().ToList())
                _foldersList.Items.Remove(item);
        };
        _subfoldersCheck = new CheckBox { Text = "Include subfolders when scanning", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 3, 4) };

        _sepCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _sepCombo.Items.AddRange(new object[] { "-  (dash)", "_  (underscore)", ".  (dot)" });
        _sepCombo.SelectedIndex = 0;
        _oldCodeBox = new TextBox();
        _newCodeBox = new TextBox();
        _exactNameCheck = new CheckBox
        {
            Text = "Also relink links by exact name (bridge / shared models with no site code)",
            AutoSize = true, Checked = true, Margin = new Padding(3, 4, 3, 4)
        };

        _folderBox = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        browse.Click += (_, _) => BrowseForFolder();

        _projectLogBox = new TextBox { Text = "Internal Relink" };

        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseDocuments, "Close files"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.LeaveOpen, "Leave files open"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseRevit, "Close Revit when complete"));
        _completionCombo.SelectedIndex = 2; // Close Revit when complete (default) — see BatchRunWindow

        _okButton = new Button { Text = "Run", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        _okButton.Click += (_, _) => Commit();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };

        Label note = FormLayout.NoteLabel(this,
            "Re-points each host model's Revit links to the matching models found in the target " +
            "folders. The link name's leading token is swapped: old code → new code (e.g. " +
            "\"OLD-ST-BLDA\" → \"SITE1A-ST-BLDA\"). The SAME old and new code is fine — links are then " +
            "re-pointed to the same-named copies found in the target folders. With \"exact name\" " +
            "ticked, links whose leading token is not the old code (bridge/shared models) are " +
            "re-pointed to a target model with the identical name. A link already pointing at the " +
            "found model, or with no (or several) matching targets, is left untouched and reported. " +
            "Each host is synced back to its cloud central. Use \"Close files\": a host left open " +
            "keeps its links loaded, which blocks relinking the next host if it is one of those links.");

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Input set:", _setCombo, saveSet, dupSet, deleteSet));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Host model set:", _presetCombo));
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Target folders:", _foldersList));
        FormLayout.AddAutoRow(root, FlowRight(addFolder, removeFolder));
        FormLayout.AddAutoRow(root, _subfoldersCheck);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Old site code:", _oldCodeBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("New site code:", _newCodeBox));
        FormLayout.AddAutoRow(root, _exactNameCheck);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Separator:", _sepCombo));
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

        ReloadSetCombo(selectName: null);
        RelinkInputSet? lastUsed = RelinkSetStore.TryLoadLastUsed();
        if (lastUsed != null)
        {
            ApplyInputs(lastUsed, loaded: false);
            if (!string.IsNullOrEmpty(lastUsed.Name))
                SelectSetByName(lastUsed.Name);
        }
    }

    private static FlowLayoutPanel FlowRight(params Control[] controls)
    {
        FlowLayoutPanel row = FormLayout.FlowRow(controls);
        row.Anchor = AnchorStyles.Left;
        return row;
    }

    // ---- input sets ------------------------------------------------------------

    private void ReloadSetCombo(string? selectName) => _sets.Reload(selectName);

    private void SelectSetByName(string name) => _sets.SelectByName(name);

    private void ApplyInputs(RelinkInputSet s, bool loaded)
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

        _foldersList.Items.Clear();
        foreach (FolderRef f in s.TargetFolders)
            _foldersList.Items.Add(f);

        _subfoldersCheck.Checked = s.IncludeSubfolders;
        _sepCombo.SelectedIndex = s.Separator switch { "_" => 1, "." => 2, _ => 0 };
        _oldCodeBox.Text = s.OldCode;
        _newCodeBox.Text = s.NewCode;
        _exactNameCheck.Checked = s.MatchByExactName;
        if (!string.IsNullOrWhiteSpace(s.DestinationFolder)) _folderBox.Text = s.DestinationFolder;
        if (!string.IsNullOrWhiteSpace(s.ProjectLogName)) _projectLogBox.Text = s.ProjectLogName;
    }

    private RelinkInputSet BuildSnapshot(string name) => new()
    {
        Name = name,
        ModelSetName = (_presetCombo.SelectedItem as PresetItem)?.Preset.Name ?? "",
        OldCode = _oldCodeBox.Text.Trim(),
        NewCode = _newCodeBox.Text.Trim(),
        Separator = _sepCombo.SelectedIndex switch { 1 => "_", 2 => ".", _ => "-" },
        MatchByExactName = _exactNameCheck.Checked,
        TargetFolders = _foldersList.Items.Cast<FolderRef>().ToList(),
        IncludeSubfolders = _subfoldersCheck.Checked,
        DestinationFolder = _folderBox.Text.Trim(),
        ProjectLogName = _projectLogBox.Text.Trim()
    };

    private void SaveInputSet() => _sets.Save(ref _loadedSetName, BuildSnapshot,
        () => _foldersList.Items.Count == 0 ? "Add at least one target folder before saving the set." : null);

    /// <summary>Forks the selected set to a new "&lt;name&gt; (copy)" and loads the copy — see
    /// <see cref="InputSetActions{T}.Duplicate"/>.</summary>
    private void DuplicateInputSet() => _sets.Duplicate();

    private void DeleteInputSet() => _sets.Delete(ref _loadedSetName);

    private void AddTargetFolder()
    {
        // Multi-pick: the crawler window stays open across "Add folder" clicks, so adding several
        // folders doesn't re-load the (slow) Forma tree each time; "Done" hands the list back.
        using var picker = new FormaFolderPickerWindow(
            _client, "Internal Relink — target folders",
            "Select a Forma folder and click \"Add folder\" — repeat for more; \"Done\" when finished:",
            multiPick: true);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.PickedFolders.Count == 0) return;

        foreach (FolderRef folder in picker.PickedFolders)
        {
            // De-dup by folder URN so the same folder isn't scanned twice.
            if (_foldersList.Items.Cast<FolderRef>().Any(f => f.FolderUrn == folder.FolderUrn)) continue;
            _foldersList.Items.Add(folder);
        }
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
            Warn("Select a host model set.");
            return;
        }
        if (_foldersList.Items.Count == 0)
        {
            Warn("Add at least one target folder to scan for the new-prefix models.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_oldCodeBox.Text) || string.IsNullOrWhiteSpace(_newCodeBox.Text))
        {
            Warn("Enter both the old and the new site code (e.g. OLD → SITE1A).");
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
        RelinkSetStore.SaveLastUsed(Inputs);
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
        public override string ToString() => ModelSetAccess.Label(Preset, Preset.IsFolderBacked
            ? $"{Preset.Name}  [folder]"
            : $"{Preset.Name}  ({Preset.Models.Count} models)");
    }

    private sealed class SetItem
    {
        public RelinkInputSet Set { get; }
        public SetItem(RelinkInputSet set) => Set = set;
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
