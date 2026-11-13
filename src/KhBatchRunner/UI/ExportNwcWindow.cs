using System.Globalization;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Creates and edits the named Export-NWC option sets that Batch Run and Schedule Run pick from,
/// and runs one immediately over a saved model set.
///
/// <para><b>Laid out like the team's existing NWC panel</b> — the same options in the same
/// four-column grid, the same Entire Model / 3D Views choice, the same naming row — because that
/// is the arrangement people already read, and a batch tool that asks the same questions in a
/// different order is a tool you have to re-learn.</para>
///
/// <para>Smaller than <see cref="CutModelWindow"/> despite the option count: Cut model has to
/// browse Forma for a publish target, and this writes to a plain folder. What they share is the
/// shape — a named set is the unit, because an action that carries its own configuration cannot be
/// built by <see cref="Actions.ActionRegistry"/> and has to hand that configuration to the job.</para>
/// </summary>
public sealed class ExportNwcWindow : Form
{
    private readonly ComboBox _setsCombo;
    private readonly InputSetActions<ExportNwcInputSet> _sets;
    // Name of the currently-loaded options set (null for unsaved inputs) — rename-on-save semantics.
    private string? _loadedSetName;
    private readonly TextBox _outputBox;

    // Bird's grid, read left to right, top to bottom.
    private readonly CheckBox _convertElementProperties;
    private readonly CheckBox _exportLinks;
    private readonly CheckBox _exportRoomAsAttribute;
    private readonly CheckBox _findMissingMaterials;
    private readonly CheckBox _divideFileIntoLevels;
    private readonly CheckBox _exportParts;
    private readonly CheckBox _exportRoomGeometry;
    private readonly CheckBox _exportElementIds;
    private readonly CheckBox _internalCoordinates;
    private readonly CheckBox _exportUrls;
    private readonly CheckBox _convertLinkedCad;
    private readonly CheckBox _convertLights;
    private readonly TextBox _facetingBox;
    private readonly ComboBox _parametersCombo;

    private readonly RadioButton _entireModelRadio;
    private readonly RadioButton _viewsRadio;
    private readonly TextBox _viewContainsBox;

    private readonly TextBox _prefixBox;
    private readonly TextBox _suffixBox;
    private readonly CheckBox _nameModelCheck;
    private readonly CheckBox _nameViewCheck;

    private readonly ListBox _modelSetsList;
    private readonly TextBox _destBox;
    private readonly TextBox _projectLogBox;
    private readonly CheckBox? _activeModelCheck;
    private readonly Label _status;

    /// <summary>The options to run with, set only when the user chose Run. Null on Close.</summary>
    public ExportNwcInputSet? RunSpec { get; private set; }

    /// <summary>The model set to run over, when <see cref="RunSpec"/> is set and
    /// <see cref="RunOnActiveModel"/> is false.</summary>
    public string SelectedModelSetName { get; private set; } = "";

    /// <summary>True when the user chose to export the model open in Revit rather than a model set.
    /// Set only when the window was given an active model name.</summary>
    public bool RunOnActiveModel { get; private set; }

    /// <param name="activeModelName">Title of a runnable project model open in Revit, or null when
    /// there is none — the "run on active model" checkbox is offered only with one.</param>
    public ExportNwcWindow(string? activeModelName = null)
    {
        Text = "Export NWC — options and run";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(900, 700);
        MinimumSize = new Size(760, 620);

        CheckBox Chk(string text, bool on = false) =>
            new() { Text = text, AutoSize = true, Checked = on, Margin = new Padding(3, 4, 12, 4) };

        var root = FormLayout.SingleColumn();

        Label note = FormLayout.NoteLabel(this,
            "Writes Navisworks .nwc files. Choose the whole model, or every 3D view whose title " +
            "contains the text you give (case sensitive; leave it blank for all 3D views) — a " +
            "model can then produce several files, one per view, each with its own log row. A " +
            "model matching nothing exports as a whole model instead and says so. Leave the output " +
            "folder empty to write beside the run's own logs.");

        // Editable, like the "Input set:" combo of Cut model and its siblings: the name typed here
        // is the name Save set saves under. This used to be a fixed list plus a separate "Set name"
        // box, where saving overwrote without asking and deleting did not confirm — see
        // InputSetActions for the rules every saved-set row now shares.
        _setsCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _setsCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_setsCombo.SelectedItem is SetOption opt) ApplyInputs(opt.Set, loaded: true);
        };
        _sets = new InputSetActions<ExportNwcInputSet>(this, _setsCombo,
            ExportNwcSetStore.LoadAll, ExportNwcSetStore.Exists, ExportNwcSetStore.Save, ExportNwcSetStore.Delete,
            s => s.Name, s => s.Clone(), (s, name) => s.Name = name,
            s => new SetOption(s), o => (o as SetOption)?.Set, SetStatus,
            noun: "options set", boxLabel: "Saved options set");

        _outputBox = new TextBox();
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        browse.Click += (_, _) => PickFolder(_outputBox);

        // --- the option grid, four columns like the panel it mirrors ---
        _convertElementProperties = Chk("Convert Element Properties", true);
        _exportLinks = Chk("Export Links");
        _exportRoomAsAttribute = Chk("Export Room As Attribute", true);
        _findMissingMaterials = Chk("Find Missing Materials", true);

        _divideFileIntoLevels = Chk("Divide File Into Levels", true);
        _exportParts = Chk("Export Parts", true);
        _exportRoomGeometry = Chk("Export Room Geometry");
        _exportElementIds = Chk("Export Element Ids", true);

        _internalCoordinates = Chk("Export Internal Coordinates (uncheck for shared)");
        _exportUrls = Chk("Export URLs", true);
        _convertLinkedCad = Chk("Export Linked CAD");
        _convertLights = Chk("Convert Lights");

        _facetingBox = new TextBox { Text = "1", Width = 60 };
        _parametersCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        _parametersCombo.Items.Add(new NamedItem(NwcParameterMode.All.ToString(), "All parameters"));
        _parametersCombo.Items.Add(new NamedItem(NwcParameterMode.Elements.ToString(), "Element parameters only"));
        _parametersCombo.Items.Add(new NamedItem(NwcParameterMode.None.ToString(), "No parameters"));
        _parametersCombo.SelectedIndex = 0;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 4)
        };
        // AutoSize columns, NOT Percent 25. Four equal quarters of the dialog gave each caption about
        // 206px, and "Convert Element Properties" needs 211 while "Export Internal Coordinates
        // (uncheck for shared)" needs 350 — so both shipped truncated, and every caption in the grid
        // truncated once the dialog was dragged to its minimum. A Percent column clips an AutoSize
        // child; only an AutoSize column widens to fit it. The four-column shape is unchanged.
        for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (Control c in new Control[]
                 {
                     _convertElementProperties, _exportLinks, _exportRoomAsAttribute, _findMissingMaterials,
                     _divideFileIntoLevels, _exportParts, _exportRoomGeometry, _exportElementIds,
                     _internalCoordinates, _exportUrls, _convertLinkedCad, _convertLights,
                 })
            grid.Controls.Add(c);

        // --- scope: entire model, or views whose title contains … ---
        _entireModelRadio = new RadioButton { Text = "Export entire model", AutoSize = true, Checked = true };
        _viewsRadio = new RadioButton { Text = "Export 3D views — titles contain:", AutoSize = true };
        _viewContainsBox = new TextBox { Width = 200, Enabled = false };
        _viewsRadio.CheckedChanged += (_, _) => _viewContainsBox.Enabled = _viewsRadio.Checked;
        var scopeRow = FormLayout.FlowRow(
            _entireModelRadio, _viewsRadio, _viewContainsBox,
            new Label { Text = "(case sensitive; blank = all 3D views)", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });

        // --- naming convention ---
        _prefixBox = new TextBox { Width = 120 };
        _suffixBox = new TextBox { Width = 120 };
        _nameModelCheck = Chk("Model name", true);
        _nameViewCheck = Chk("View name");
        var nameRow = FormLayout.FlowRow(
            new Label { Text = "Prefix:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, _prefixBox,
            _nameModelCheck, _nameViewCheck,
            new Label { Text = "Suffix:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, _suffixBox);

        _modelSetsList = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.One };
        // Labels and dims from the account snapshot, and ENSURES one exists rather than hoping
        // another window took it first — see ModelSetAccess.EnsureSnapshot.
        ModelSetAccess.AttachGreying(_modelSetsList, i => (i as SetItem)?.Preset,
            onSnapshotReady: RefreshModelSets);
        RefreshModelSets();

        _destBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        _projectLogBox = new TextBox { Text = "Export NWC" };
        var destBrowse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        destBrowse.Click += (_, _) => PickFolder(_destBox);

        var saveBtn = new Button { Text = "Save set", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        saveBtn.Click += (_, _) => SaveSet();
        var dupBtn = new Button { Text = "Duplicate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        dupBtn.Click += (_, _) => DuplicateSet();
        var deleteBtn = new Button { Text = "Delete", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        deleteBtn.Click += (_, _) => DeleteSet();

        var runBtn = new Button
        {
            Text = "Run", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 0)
        };
        runBtn.Click += (_, _) => Commit();
        var cancelBtn = new Button
        {
            Text = "Cancel", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel
        };

        // --- Run on active model (one export of what is open now: no model set, no CSV log) ---
        if (activeModelName != null)
        {
            var activeCheck = new CheckBox
            {
                Text = $"Run on active model only — “{activeModelName}” (exports what is open now; no CSV log)",
                AutoSize = true,
                Margin = new Padding(3, 2, 3, 2)
            };
            activeCheck.CheckedChanged += (_, _) =>
            {
                bool active = activeCheck.Checked;
                _modelSetsList.Enabled = !active;
                _projectLogBox.Enabled = !active;
                runBtn.Text = active ? "Run on active" : "Run";
            };
            _activeModelCheck = activeCheck;
        }

        _status = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Saved options set:", _setsCombo, saveBtn, dupBtn, deleteBtn));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Output folder:", _outputBox, browse));
        FormLayout.AddAutoRow(root, grid);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Faceting factor:", _facetingBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Parameters:", _parametersCombo));
        FormLayout.AddAutoRow(root, scopeRow);
        FormLayout.AddAutoRow(root, new Label { Text = "File naming:", AutoSize = true });
        FormLayout.AddAutoRow(root, nameRow);
        FormLayout.AddAutoRow(root, new Label { Text = "Model set to run over:", AutoSize = true });
        FormLayout.AddFillRow(root, _modelSetsList, 100);
        if (_activeModelCheck != null)
            FormLayout.AddAutoRow(root, _activeModelCheck);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Export CSV log to:", _destBox, destBrowse));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Project log name:", _projectLogBox));
        FormLayout.AddAutoRow(root, _status);
        FormLayout.AddAutoRow(root, FormLayout.FlowRow(runBtn, cancelBtn));

        Controls.Add(root);
        CancelButton = cancelBtn;
        Theme.Apply(this);
        FormLayout.AlignLabels(this);

        // A floor under the model-set list, so the rows below it cannot squeeze it into a line.
        _modelSetsList.MinimumSize = new Size(0, 110);

        // Fit the dialog to what it actually contains, then refuse to go below it — the idiom nine
        // other windows already use (CutModelWindow and friends). Without it this dialog opened at a
        // hardcoded 900x700 that its own rows had outgrown: FormLayout's table does not scroll, so
        // the percent row holding the model-set list was starved toward zero and the rows after it
        // overflowed on top of each other. That is why "Model set to run over:" appeared sliced in
        // half underneath the "Export CSV log to:" row.
        ClientSize = new Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width), root.PreferredSize.Height);
        MinimumSize = Size;

        // Populate the saved sets and restore the last committed inputs (which also selects the
        // matching set if the last run came from one) — the same restore the other set windows do.
        _sets.Reload(selectName: null);
        ExportNwcInputSet? lastUsed = ExportNwcSetStore.TryLoadLastUsed();
        if (lastUsed != null)
        {
            ApplyInputs(lastUsed, loaded: false);
            if (!string.IsNullOrEmpty(lastUsed.Name))
                _sets.SelectByName(lastUsed.Name); // fires ApplyInputs(loaded: true) if it still exists
        }

        // With no saved model sets, exporting the open model is the only thing this dialog can do.
        if (_activeModelCheck != null && _modelSetsList.Items.Count == 0)
            _activeModelCheck.Checked = true;
    }

    /// <summary>Fills every control from the given inputs. <paramref name="loaded"/> marks it as
    /// the currently-loaded NAMED set (rename-on-save semantics); false for last-used restores.</summary>
    private void ApplyInputs(ExportNwcInputSet s, bool loaded)
    {
        _loadedSetName = loaded && !string.IsNullOrEmpty(s.Name) ? s.Name : null;

        if (!string.IsNullOrWhiteSpace(s.DestinationFolder)) _destBox.Text = s.DestinationFolder;
        if (!string.IsNullOrWhiteSpace(s.ProjectLogName)) _projectLogBox.Text = s.ProjectLogName;
        _outputBox.Text = s.OutputFolder;

        _convertElementProperties.Checked = s.ConvertElementProperties;
        _exportLinks.Checked = s.ExportLinks;
        _exportRoomAsAttribute.Checked = s.ExportRoomAsAttribute;
        _findMissingMaterials.Checked = s.FindMissingMaterials;
        _divideFileIntoLevels.Checked = s.DivideFileIntoLevels;
        _exportParts.Checked = s.ExportParts;
        _exportRoomGeometry.Checked = s.ExportRoomGeometry;
        _exportElementIds.Checked = s.ExportElementIds;
        _internalCoordinates.Checked = s.UseInternalCoordinates;
        _exportUrls.Checked = s.ExportUrls;
        _convertLinkedCad.Checked = s.ConvertLinkedCADFormats;
        _convertLights.Checked = s.ConvertLights;
        _facetingBox.Text = s.FacetingFactor.ToString(CultureInfo.InvariantCulture);
        SelectNamed(_parametersCombo, s.Parameters.ToString());

        _entireModelRadio.Checked = s.ExportEntireModel;
        _viewsRadio.Checked = !s.ExportEntireModel;
        _viewContainsBox.Text = s.ViewTitleContains;
        _viewContainsBox.Enabled = _viewsRadio.Checked;

        _prefixBox.Text = s.NamePrefix;
        _suffixBox.Text = s.NameSuffix;
        _nameModelCheck.Checked = s.NameIncludesModel;
        _nameViewCheck.Checked = s.NameIncludesView;
    }

    private static void SelectNamed(ComboBox combo, string value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (((NamedItem)combo.Items[i]).Value.Equals(value, StringComparison.OrdinalIgnoreCase))
            { combo.SelectedIndex = i; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private ExportNwcInputSet Current(string name)
    {
        // A faceting factor that will not parse falls back to Revit's own default rather than
        // failing the dialog — it is a quality dial, not a decision.
        if (!double.TryParse(_facetingBox.Text.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double faceting) || faceting <= 0)
            faceting = 1.0;

        var mode = NwcParameterMode.All;
        if (_parametersCombo.SelectedItem is NamedItem ni)
            Enum.TryParse(ni.Value, out mode);

        return new ExportNwcInputSet
        {
            Name = name,
            OutputFolder = _outputBox.Text.Trim(),
            ExportEntireModel = _entireModelRadio.Checked,
            ViewTitleContains = _viewContainsBox.Text.Trim(),
            NamePrefix = _prefixBox.Text.Trim(),
            NameSuffix = _suffixBox.Text.Trim(),
            NameIncludesModel = _nameModelCheck.Checked,
            NameIncludesView = _nameViewCheck.Checked,
            ConvertElementProperties = _convertElementProperties.Checked,
            ExportLinks = _exportLinks.Checked,
            ExportRoomAsAttribute = _exportRoomAsAttribute.Checked,
            FindMissingMaterials = _findMissingMaterials.Checked,
            DivideFileIntoLevels = _divideFileIntoLevels.Checked,
            ExportParts = _exportParts.Checked,
            ExportRoomGeometry = _exportRoomGeometry.Checked,
            ExportElementIds = _exportElementIds.Checked,
            ExportUrls = _exportUrls.Checked,
            UseInternalCoordinates = _internalCoordinates.Checked,
            ConvertLinkedCADFormats = _convertLinkedCad.Checked,
            ConvertLights = _convertLights.Checked,
            FacetingFactor = faceting,
            Parameters = mode,
            DestinationFolder = _destBox.Text.Trim(),
            ProjectLogName = string.IsNullOrWhiteSpace(_projectLogBox.Text) ? "Export NWC" : _projectLogBox.Text.Trim()
        };
    }

    private void SaveSet() => _sets.Save(ref _loadedSetName, Current, missing: null);

    /// <summary>Forks the selected set to a new "&lt;name&gt; (copy)" and loads the copy — see
    /// <see cref="InputSetActions{T}.Duplicate"/>.</summary>
    private void DuplicateSet() => _sets.Duplicate();

    private void DeleteSet() => _sets.Delete(ref _loadedSetName);

    private void Commit()
    {
        ExportNwcInputSet set = Current(_loadedSetName ?? "");
        bool active = _activeModelCheck?.Checked == true;
        var chosen = _modelSetsList.SelectedItem as SetItem;
        if (!active && chosen is null)
        {
            SetStatus("Select the model set to run over.");
            return;
        }
        if (!set.HasOutput)
        {
            SetStatus("Set an output folder, or a CSV log folder to write the .nwc files beside.");
            return;
        }

        ExportNwcSetStore.SaveLastUsed(set);
        if (!string.IsNullOrWhiteSpace(set.DestinationFolder))
            Settings.UserSettings.LastDestinationFolder = set.DestinationFolder;
        RunSpec = set;
        RunOnActiveModel = active;
        SelectedModelSetName = chosen?.Preset.Name ?? "";
        DialogResult = DialogResult.OK;
        Close();
    }

    private void PickFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = target.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>A dropdown entry whose stored value differs from its label — same shape as the one
    /// every other window here keeps privately.</summary>
    private sealed class NamedItem
    {
        public string Value { get; }
        private readonly string _label;
        public NamedItem(string value, string label) { Value = value; _label = label; }
        public override string ToString() => _label;
    }

    private sealed class SetOption
    {
        public ExportNwcInputSet Set { get; }
        public SetOption(ExportNwcInputSet set) => Set = set;
        public override string ToString() => Set.Name;
    }

    /// <summary>Repopulates the model-set list, preserving the selection. Called on first build and
    /// again when the account snapshot lands, because the ORDER depends on the verdict.</summary>
    private void RefreshModelSets()
    {
        string? keep = (_modelSetsList.SelectedItem as SetItem)?.Preset.Name;
        _modelSetsList.BeginUpdate();
        _modelSetsList.Items.Clear();
        foreach (ModelPreset p in ModelSetAccess.VisibleSets())
        {
            int i = _modelSetsList.Items.Add(new SetItem(p));
            if (keep != null && p.Name == keep) _modelSetsList.SelectedIndex = i;
        }
        _modelSetsList.EndUpdate();
    }

    private sealed class SetItem
    {
        public ModelPreset Preset { get; }
        public SetItem(ModelPreset preset) => Preset = preset;
        public override string ToString() => ModelSetAccess.Label(Preset,
            Preset.IsFolderBacked ? $"{Preset.Name}  [folder(s)]" : $"{Preset.Name}  ({Preset.Models.Count})");
    }
}
