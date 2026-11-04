using System.IO;
using System.Windows.Forms;
using KhBatchRunner.Forma;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Dialog for Model Batch Action → Correct Workset: define category rules (categories → target workset
/// name + rename-search keyword), the RVT-link rule (each link alone in
/// "&lt;keyword&gt;_&lt;model name&gt;"), pick Check/Fix mode and the hosts (model set / every
/// model in a Forma folder / the active model). All inputs save/load as named
/// <see cref="CorrectWorksetInputSet"/>s with last-used recall. A sibling of
/// <see cref="RelinkModelWindow"/>.
/// </summary>
public sealed class CorrectWorksetWindow : Form
{
    private readonly DataManagementClient _client;
    private readonly ComboBox _setCombo;
    private readonly RadioButton _hostsSetRadio;
    private readonly RadioButton _hostsActiveRadio;
    private readonly ComboBox _presetCombo;
    private readonly DataGridView _grid;
    private readonly List<CorrectWorksetRule> _rules = new();
    private readonly CheckBox _linkRuleCheck;
    private readonly TextBox _linkKeywordBox;
    private readonly CheckBox _cadRuleCheck;
    private readonly TextBox _cadWorksetBox;
    private readonly ComboBox _modeCombo;
    private readonly ComboBox _completionCombo;
    private readonly TextBox _folderBox;
    private readonly TextBox _projectLogBox;
    private readonly Button _okButton;

    private string? _loadedSetName;

    /// <summary>The chosen host model set (may be a folder-backed set); null when the active model is used.</summary>
    public ModelPreset? SelectedPreset { get; private set; }

    public CorrectWorksetInputSet? Inputs { get; private set; }

    public BatchCompletion SelectedCompletion { get; private set; } = BatchCompletion.CloseDocuments;

    /// <summary>
    /// Whether this run may be split across several fresh Revit instances. False for "Leave files
    /// open" (a group's Revit has to close itself) — see <see cref="FormLayout.SplitAllowed"/>.
    /// The tool sizes the groups; the caller only says yes or no.
    /// </summary>
    public bool SplitEnabled { get; private set; }

    public CorrectWorksetWindow(IReadOnlyList<ModelPreset> presets, DataManagementClient client)
    {
        _client = client;

        Text = "Correct Workset — Model Batch Action";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(680, 560);
        MinimumSize = new System.Drawing.Size(620, 0);

        // --- Input set ---
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

        // --- Hosts: a saved model set (which may be a folder-backed set) or the active model. ---
        _hostsSetRadio = new RadioButton { Text = "Model set:", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 3, 4) };
        _hostsActiveRadio = new RadioButton { Text = "Active model (open in Revit now)", AutoSize = true, Margin = new Padding(12, 4, 3, 4) };
        _hostsSetRadio.CheckedChanged += (_, _) => UpdateHostSourceEnabled();
        _hostsActiveRadio.CheckedChanged += (_, _) => UpdateHostSourceEnabled();

        _presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        // Ordered, labelled and dimmed by the signed-in-account check, and rebuilt when that answer
        // lands — the whole treatment, in the one call that carries it (ModelSetAccess.BindSetCombo).
        // This was a bare foreach until 1.5.1.2, which is why a set this account cannot open looked
        // exactly like every other set here while Edit Model Set was flagging it.
        ModelSetAccess.BindSetCombo(_presetCombo, presets,
            p => new PresetItem(p), i => (i as PresetItem)?.Preset);

        // --- Rules grid ---
        _grid = new DataGridView
        {
            Height = 130, MinimumSize = new System.Drawing.Size(0, 130), Dock = DockStyle.Fill,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Categories", ReadOnly = true, FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Target workset", FillWeight = 32 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Find & rename (keyword)", FillWeight = 28 });
        _grid.CellEndEdit += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rules.Count) return;
            string value = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString() ?? "";
            if (e.ColumnIndex == 1) _rules[e.RowIndex].TargetWorkset = value.Trim();
            if (e.ColumnIndex == 2) _rules[e.RowIndex].Keyword = value.Trim();
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) EditRowCategories(e.RowIndex); };

        var addRule = new Button { Text = "Add rule", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        addRule.Click += (_, _) =>
        {
            _rules.Add(new CorrectWorksetRule());
            RefreshGrid(select: _rules.Count - 1);
            EditRowCategories(_rules.Count - 1);
        };
        var pickCats = new Button { Text = "Pick categories…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(120, 0) };
        pickCats.Click += (_, _) => { if (_grid.CurrentRow != null) EditRowCategories(_grid.CurrentRow.Index); };
        var removeRule = new Button { Text = "Remove rule", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(95, 0) };
        removeRule.Click += (_, _) =>
        {
            if (_grid.CurrentRow is null || _grid.CurrentRow.Index >= _rules.Count) return;
            _rules.RemoveAt(_grid.CurrentRow.Index);
            RefreshGrid();
        };

        // --- Link rule + CAD rule + mode ---
        _linkRuleCheck = new CheckBox { Text = "Correct RVT link worksets — each link alone in:", AutoSize = true, Checked = true, Margin = new Padding(3, 6, 3, 4) };
        _linkKeywordBox = new TextBox { Text = "LINK", Width = 120 };
        _linkRuleCheck.CheckedChanged += (_, _) => _linkKeywordBox.Enabled = _linkRuleCheck.Checked;
        var linkHint = new Label { Text = "_<model name>", AutoSize = true, Margin = new Padding(0, 6, 3, 3) };

        _cadRuleCheck = new CheckBox { Text = "Combine linked CAD into one workset:", AutoSize = true, Margin = new Padding(3, 6, 3, 4) };
        _cadWorksetBox = new TextBox { Text = "*LINKED CAD", Width = 160, Enabled = false };
        _cadRuleCheck.CheckedChanged += (_, _) => _cadWorksetBox.Enabled = _cadRuleCheck.Checked;

        _modeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _modeCombo.Items.Add("Check only — report what would change, change nothing");
        _modeCombo.Items.Add("Fix — rename (with approval) / create / move");
        _modeCombo.SelectedIndex = 0;

        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseDocuments, "Close files"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.LeaveOpen, "Leave files open"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseRevit, "Close Revit when complete"));
        _completionCombo.SelectedIndex = 2; // Close Revit when complete (default) — see BatchRunWindow

        _folderBox = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        browse.Click += (_, _) => BrowseForFolder();
        _projectLogBox = new TextBox { Text = "Correct Workset" };

        _okButton = new Button { Text = "Run", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        _okButton.Click += (_, _) => Commit();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };

        Label note = FormLayout.NoteLabel(this,
            "Per model: each rule moves its categories' elements onto the target workset — an " +
            "existing workset whose name contains the rename keyword is RENAMED to the target " +
            "(you approve each rename per model during the run), otherwise the workset is created. " +
            "Link rule: a workset holding exactly one link is renamed to <keyword>_<model name> " +
            "(with your approval); extra links move to their own worksets (created as needed); any " +
            "other elements left in a link's workset follow your category rules or move to WIP. Run " +
            "Check first, review the CSV, then Fix.");

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Input set:", _setCombo, saveSet, dupSet, deleteSet));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Hosts from:", FlowLeft(_hostsSetRadio, _hostsActiveRadio)));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Host model set:", _presetCombo));
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Rules:", _grid));
        FormLayout.AddAutoRow(root, FlowLeft(addRule, pickCats, removeRule));
        FormLayout.AddAutoRow(root, FlowLeft(_linkRuleCheck, _linkKeywordBox, linkHint));
        FormLayout.AddAutoRow(root, FlowLeft(_cadRuleCheck, _cadWorksetBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Mode:", _modeCombo));
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
        linkHint.ForeColor = Theme.Dim(linkHint);
        ClientSize = new System.Drawing.Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width), root.PreferredSize.Height);
        MinimumSize = Size; // resizable, but never smaller than the fitted content (all controls shown)

        ReloadSetCombo(selectName: null);
        CorrectWorksetInputSet? lastUsed = CorrectWorksetSetStore.TryLoadLastUsed();
        if (lastUsed != null)
        {
            ApplyInputs(lastUsed, loaded: false);
            if (!string.IsNullOrEmpty(lastUsed.Name))
                SelectSetByName(lastUsed.Name);
        }
        UpdateHostSourceEnabled();
        RefreshGrid();
    }

    private static FlowLayoutPanel FlowLeft(params Control[] controls)
    {
        FlowLayoutPanel row = FormLayout.FlowRow(controls);
        row.Anchor = AnchorStyles.Left;
        return row;
    }

    private void UpdateHostSourceEnabled()
    {
        bool active = _hostsActiveRadio.Checked;
        _presetCombo.Enabled = _hostsSetRadio.Checked;
        // Active model: no batch open/close, no CSV — completion/log fields don't apply.
        _completionCombo.Enabled = !active;
        _folderBox.Enabled = !active;
        _projectLogBox.Enabled = !active;
    }

    // ---- rules grid ------------------------------------------------------------

    private void RefreshGrid(int? select = null)
    {
        _grid.Rows.Clear();
        foreach (CorrectWorksetRule rule in _rules)
            _grid.Rows.Add(CategoriesSummary(rule), rule.TargetWorkset, rule.Keyword);
        if (select is int i && i >= 0 && i < _grid.Rows.Count)
            _grid.CurrentCell = _grid.Rows[i].Cells[1];
    }

    private void EditRowCategories(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rules.Count) return;
        using var picker = new CategoryPickerDialog(_rules[rowIndex].Categories);
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _rules[rowIndex].Categories = picker.SelectedCategoryNames;
        _grid.Rows[rowIndex].Cells[0].Value = CategoriesSummary(_rules[rowIndex]);
    }

    private static string CategoriesSummary(CorrectWorksetRule rule)
    {
        if (rule.Categories.Count == 0) return "(pick categories…)";
        var labels = rule.Categories.Take(3).Select(LabelFor).ToList();
        string text = string.Join(", ", labels);
        return rule.Categories.Count > 3 ? $"{text} (+{rule.Categories.Count - 3})" : text;
    }

    private static string LabelFor(string enumName)
    {
        try
        {
            if (Enum.TryParse(enumName, out Autodesk.Revit.DB.BuiltInCategory bic))
                return Autodesk.Revit.DB.LabelUtils.GetLabelFor(bic);
        }
        catch { /* fall through to the raw name */ }
        return enumName;
    }

    // ---- input sets ------------------------------------------------------------

    private void ReloadSetCombo(string? selectName)
    {
        _setCombo.Items.Clear();
        foreach (CorrectWorksetInputSet set in CorrectWorksetSetStore.LoadAll())
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

    private void ApplyInputs(CorrectWorksetInputSet s, bool loaded)
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

        // A legacy folder-mode set falls back to the model-set radio (folder hosts are now a
        // folder-backed model set instead).
        _hostsActiveRadio.Checked = s.RunOnActiveModel;
        _hostsSetRadio.Checked = !s.RunOnActiveModel;
        UpdateHostSourceEnabled();

        _rules.Clear();
        _rules.AddRange(s.Rules.Select(r => r.Clone()));
        RefreshGrid();

        _linkRuleCheck.Checked = s.ApplyLinkRule;
        _linkKeywordBox.Text = s.LinkKeyword;
        _linkKeywordBox.Enabled = s.ApplyLinkRule;
        _cadRuleCheck.Checked = s.ApplyCadRule;
        _cadWorksetBox.Text = string.IsNullOrWhiteSpace(s.CadWorksetName) ? "*LINKED CAD" : s.CadWorksetName;
        _cadWorksetBox.Enabled = s.ApplyCadRule;
        _modeCombo.SelectedIndex = s.Fix ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(s.DestinationFolder)) _folderBox.Text = s.DestinationFolder;
        if (!string.IsNullOrWhiteSpace(s.ProjectLogName)) _projectLogBox.Text = s.ProjectLogName;
    }

    private CorrectWorksetInputSet BuildSnapshot(string name) => new()
    {
        Name = name,
        ModelSetName = (_presetCombo.SelectedItem as PresetItem)?.Preset.Name ?? "",
        RunOnActiveModel = _hostsActiveRadio.Checked,
        Rules = _rules.Select(r => r.Clone()).ToList(),
        ApplyLinkRule = _linkRuleCheck.Checked,
        LinkKeyword = _linkKeywordBox.Text.Trim(),
        ApplyCadRule = _cadRuleCheck.Checked,
        CadWorksetName = _cadWorksetBox.Text.Trim(),
        Fix = _modeCombo.SelectedIndex == 1,
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
        bool renaming = _loadedSetName != null &&
                        !name.Equals(_loadedSetName, StringComparison.OrdinalIgnoreCase);
        if (CorrectWorksetSetStore.Exists(name) && (_loadedSetName == null || renaming))
        {
            if (MessageBox.Show(this, $"An input set named '{name}' already exists. Overwrite it?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }

        CorrectWorksetSetStore.Save(BuildSnapshot(name));
        if (renaming) CorrectWorksetSetStore.Delete(_loadedSetName!);
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
        CorrectWorksetInputSet copy = item.Set.Clone();
        copy.Name = SetDuplication.UniqueName(item.Set.Name, CorrectWorksetSetStore.LoadAll().Select(s => s.Name));
        try
        {
            CorrectWorksetSetStore.Save(copy);
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
        CorrectWorksetSetStore.Delete(item.Set.Name);
        if (_loadedSetName != null && _loadedSetName.Equals(item.Set.Name, StringComparison.OrdinalIgnoreCase))
            _loadedSetName = null;
        ReloadSetCombo(selectName: null);
        _setCombo.Text = "";
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
        _grid.EndEdit(); // flush an in-progress cell edit into _rules

        PresetItem? item = _presetCombo.SelectedItem as PresetItem;
        if (_hostsSetRadio.Checked && item is null)
        {
            Warn("Select a host model set.");
            return;
        }

        var usableRules = _rules.Where(r => r.Categories.Count > 0 && r.TargetWorkset.Trim().Length > 0).ToList();
        bool linkRule = _linkRuleCheck.Checked && _linkKeywordBox.Text.Trim().Length > 0;
        bool cadRule = _cadRuleCheck.Checked && _cadWorksetBox.Text.Trim().Length > 0;
        if (usableRules.Count == 0 && !linkRule && !cadRule)
        {
            Warn("Add at least one rule (categories + target workset), or enable the RVT link / linked-CAD rule.");
            return;
        }
        if (_rules.Any(r => r.Categories.Count == 0 || r.TargetWorkset.Trim().Length == 0))
        {
            Warn("Every rule needs categories AND a target workset name (remove incomplete rows).");
            return;
        }

        if (!_hostsActiveRadio.Checked)
        {
            if (string.IsNullOrWhiteSpace(_folderBox.Text)) { Warn("Choose a log destination folder."); return; }
            if (string.IsNullOrWhiteSpace(_projectLogBox.Text)) { Warn("Enter a project log name."); return; }
        }

        SelectedPreset = _hostsSetRadio.Checked ? item!.Preset : null;
        Inputs = BuildSnapshot(_loadedSetName ?? "");
        SelectedCompletion = (_completionCombo.SelectedItem as CompletionItem)?.Completion
                             ?? BatchCompletion.CloseDocuments;
        // "Run on the active model" is one already-open document — nothing to split.
        SplitEnabled = !_hostsActiveRadio.Checked &&
                       FormLayout.SplitAllowed((_completionCombo.SelectedItem as CompletionItem)?.Completion);
        CorrectWorksetSetStore.SaveLastUsed(Inputs);
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
        public CorrectWorksetInputSet Set { get; }
        public SetItem(CorrectWorksetInputSet set) => Set = set;
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
