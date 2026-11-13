using System.Globalization;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Creates and edits the named "Export IFC + Data" option sets that Batch Run and Schedule Run pick
/// from, and runs one immediately over a saved model set. Same shape as <see cref="ExportNwcWindow"/>
/// — a named set is the unit, because the action carries its own configuration into the job.
/// </summary>
public sealed class ExportIfcWindow : Form
{
    private readonly ComboBox _setsCombo;
    private readonly InputSetActions<ExportIfcInputSet> _sets;
    // Name of the currently-loaded options set (null for unsaved inputs) — rename-on-save semantics.
    private string? _loadedSetName;
    private readonly TextBox _outputBox;
    private readonly TextBox _viewContainsBox;
    private readonly ComboBox _schemaCombo;
    private readonly CheckBox _sharedCoordinates;
    private readonly CheckBox _commonPsets;
    private readonly CheckBox _revitPsets;
    private readonly CheckBox _baseQuantities;
    private readonly CheckBox _elementsCsv;
    private readonly CheckBox _roomsCsv;
    private readonly CheckBox _convertToFrag;
    private readonly TextBox _extraParamsBox;
    private readonly TextBox _keepDaysBox;

    private readonly DimmableCheckedListBox _modelSetsList;
    private readonly TextBox _destBox;
    private readonly TextBox _projectLogBox;
    private readonly CheckBox? _activeModelCheck;
    private readonly Label _status;

    /// <summary>The options to run with, set only when the user chose Run. Null on Close.</summary>
    public ExportIfcInputSet? RunSpec { get; private set; }

    /// <summary>The model sets to run over (one or more, combined into a single run), when
    /// <see cref="RunSpec"/> is set and <see cref="RunOnActiveModel"/> is false.</summary>
    public IReadOnlyList<string> SelectedModelSetNames { get; private set; } = Array.Empty<string>();

    /// <summary>True when the user chose to export the model open in Revit rather than a model set.
    /// Set only when the window was given an active model name.</summary>
    public bool RunOnActiveModel { get; private set; }

    /// <param name="activeModelName">Title of a runnable project model open in Revit, or null when
    /// there is none — the "run on active model" checkbox is offered only with one.</param>
    public ExportIfcWindow(string? activeModelName = null)
    {
        Text = "Export IFC + Data — options and run";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(860, 640);
        MinimumSize = new Size(720, 560);

        CheckBox Chk(string text, bool on = false) =>
            new() { Text = text, AutoSize = true, Checked = on, Margin = new Padding(3, 4, 12, 4) };
        Label Inline(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

        var root = FormLayout.SingleColumn();

        Label note = FormLayout.NoteLabel(this,
            "Writes, for each model, an IFC of one 3D view (the first whose title contains the text " +
            "below; blank = whole model), elements.csv and rooms.csv, into <output>\\runs\\<run time>\\. " +
            "When the run ends the IFC is converted for the BIM report and <output>\\latest\\ is " +
            "refreshed; on a scheduled run that happens after Revit has closed. Nothing is saved back " +
            "to the model.");

        // Editable, like the "Input set:" combo of Cut model and its siblings: the name typed here
        // is the name Save set saves under. This used to be a fixed list plus a separate "Set name"
        // box, where saving overwrote without asking and deleting did not confirm — see
        // InputSetActions for the rules every saved-set row now shares.
        _setsCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _setsCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_setsCombo.SelectedItem is SetOption opt) ApplyInputs(opt.Set, loaded: true);
        };
        _sets = new InputSetActions<ExportIfcInputSet>(this, _setsCombo,
            ExportIfcSetStore.LoadAll, ExportIfcSetStore.Exists, ExportIfcSetStore.Save, ExportIfcSetStore.Delete,
            s => s.Name, s => s.Clone(), (s, name) => s.Name = name,
            s => new SetOption(s), o => (o as SetOption)?.Set, SetStatus,
            noun: "options set", boxLabel: "Saved options set");

        _outputBox = new TextBox();
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        browse.Click += (_, _) => PickFolder(_outputBox);

        _viewContainsBox = new TextBox { Width = 240 };
        var viewRow = FormLayout.FlowRow(_viewContainsBox, Inline("(case sensitive; blank = whole model)"));

        _schemaCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        _schemaCombo.Items.Add(new NamedItem(IfcSchemaChoice.Ifc4ReferenceView.ToString(), "IFC4 Reference View (recommended)"));
        _schemaCombo.Items.Add(new NamedItem(IfcSchemaChoice.Ifc2x3CoordinationView2.ToString(), "IFC2x3 Coordination View 2.0"));
        _schemaCombo.Items.Add(new NamedItem(IfcSchemaChoice.Ifc4DesignTransferView.ToString(), "IFC4 Design Transfer View"));
        _schemaCombo.SelectedIndex = 0;

        _sharedCoordinates = Chk("Shared coordinates", true);
        _commonPsets = Chk("IFC common property sets", true);
        _revitPsets = Chk("Revit property sets");
        _baseQuantities = Chk("Base quantities");
        _elementsCsv = Chk("Write elements.csv", true);
        _roomsCsv = Chk("Write rooms.csv (GFA)", true);
        _convertToFrag = Chk("Convert for the BIM report", true);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 4)
        };
        // AutoSize columns: a Percent column clips an AutoSize caption (see ExportNwcWindow).
        for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (Control c in new Control[]
                 {
                     _sharedCoordinates, _commonPsets, _revitPsets, _baseQuantities,
                     _elementsCsv, _roomsCsv, _convertToFrag,
                 })
            grid.Controls.Add(c);

        _extraParamsBox = new TextBox();
        _keepDaysBox = new TextBox { Text = "14", Width = 60 };
        var keepRow = FormLayout.FlowRow(_keepDaysBox, Inline("days (0 = keep every run)"));

        // A tick list, not a single-select list: one or more sets, combined into one run — the same
        // control and the same rule as the Check/Action buttons (RunActionWindow).
        _modelSetsList = new DimmableCheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        ModelSetAccess.AttachGreying(_modelSetsList, i => (i as SetItem)?.Preset,
            onSnapshotReady: RefreshModelSets);
        RefreshModelSets();
        if (_modelSetsList.Items.Count == 1)
            _modelSetsList.SetItemChecked(0, true);
        CheckListBehavior.EnableRangeCheck(_modelSetsList);

        _destBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        _projectLogBox = new TextBox { Text = "Export IFC + Data" };
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
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("3D view title contains:", viewRow));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("IFC version:", _schemaCombo));
        FormLayout.AddAutoRow(root, grid);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Extra parameters (comma separated):", _extraParamsBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Keep runs for:", keepRow));
        FormLayout.AddAutoRow(root, new Label { Text = "Model set(s) to run over:", AutoSize = true });
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

        _modelSetsList.MinimumSize = new Size(0, 110);

        // Fit the dialog to its contents, then refuse to go below it (see ExportNwcWindow).
        ClientSize = new Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width), root.PreferredSize.Height);
        MinimumSize = Size;

        // Populate the saved sets and restore the last committed inputs (which also selects the
        // matching set if the last run came from one) — the same restore the other set windows do.
        _sets.Reload(selectName: null);
        ExportIfcInputSet? lastUsed = ExportIfcSetStore.TryLoadLastUsed();
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
    private void ApplyInputs(ExportIfcInputSet s, bool loaded)
    {
        _loadedSetName = loaded && !string.IsNullOrEmpty(s.Name) ? s.Name : null;

        if (!string.IsNullOrWhiteSpace(s.DestinationFolder)) _destBox.Text = s.DestinationFolder;
        if (!string.IsNullOrWhiteSpace(s.ProjectLogName)) _projectLogBox.Text = s.ProjectLogName;
        _outputBox.Text = s.OutputFolder;
        _viewContainsBox.Text = s.ViewTitleContains;
        for (int i = 0; i < _schemaCombo.Items.Count; i++)
            if (((NamedItem)_schemaCombo.Items[i]).Value == s.Schema.ToString()) _schemaCombo.SelectedIndex = i;
        _sharedCoordinates.Checked = s.UseSharedCoordinates;
        _commonPsets.Checked = s.ExportCommonPropertySets;
        _revitPsets.Checked = s.ExportRevitPropertySets;
        _baseQuantities.Checked = s.ExportBaseQuantities;
        _elementsCsv.Checked = s.WriteElementsCsv;
        _roomsCsv.Checked = s.WriteRoomsCsv;
        _convertToFrag.Checked = s.ConvertToFrag;
        _extraParamsBox.Text = string.Join(", ", s.ExtraParameters ?? new List<string>());
        _keepDaysBox.Text = s.KeepRunsDays.ToString(CultureInfo.InvariantCulture);
    }

    private ExportIfcInputSet Current(string name)
    {
        var schema = IfcSchemaChoice.Ifc4ReferenceView;
        if (_schemaCombo.SelectedItem is NamedItem ni) Enum.TryParse(ni.Value, out schema);

        // A retention that will not parse falls back to the default rather than failing the dialog.
        if (!int.TryParse(_keepDaysBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int keep) || keep < 0)
            keep = 14;

        return new ExportIfcInputSet
        {
            Name = name,
            OutputFolder = _outputBox.Text.Trim(),
            ViewTitleContains = _viewContainsBox.Text.Trim(),
            Schema = schema,
            UseSharedCoordinates = _sharedCoordinates.Checked,
            ExportCommonPropertySets = _commonPsets.Checked,
            ExportRevitPropertySets = _revitPsets.Checked,
            ExportBaseQuantities = _baseQuantities.Checked,
            WriteElementsCsv = _elementsCsv.Checked,
            WriteRoomsCsv = _roomsCsv.Checked,
            ConvertToFrag = _convertToFrag.Checked,
            ExtraParameters = _extraParamsBox.Text.Split(',')
                .Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList(),
            KeepRunsDays = keep,
            DestinationFolder = _destBox.Text.Trim(),
            ProjectLogName = string.IsNullOrWhiteSpace(_projectLogBox.Text) ? "Export IFC + Data" : _projectLogBox.Text.Trim()
        };
    }

    private void SaveSet() => _sets.Save(ref _loadedSetName, Current, missing: null);

    /// <summary>Forks the selected set to a new "&lt;name&gt; (copy)" and loads the copy — see
    /// <see cref="InputSetActions{T}.Duplicate"/>.</summary>
    private void DuplicateSet() => _sets.Duplicate();

    private void DeleteSet() => _sets.Delete(ref _loadedSetName);

    private void Commit()
    {
        ExportIfcInputSet set = Current(_loadedSetName ?? "");
        bool active = _activeModelCheck?.Checked == true;
        List<string> chosen = _modelSetsList.CheckedItems.Cast<SetItem>().Select(i => i.Preset.Name).ToList();
        if (!active && chosen.Count == 0)
        {
            SetStatus("Tick at least one model set to run over.");
            return;
        }
        if (!set.HasOutput)
        {
            SetStatus("Set an output folder, or a CSV log folder to write the exports beside.");
            return;
        }

        ExportIfcSetStore.SaveLastUsed(set);
        if (!string.IsNullOrWhiteSpace(set.DestinationFolder))
            Settings.UserSettings.LastDestinationFolder = set.DestinationFolder;
        RunSpec = set;
        RunOnActiveModel = active;
        SelectedModelSetNames = active ? Array.Empty<string>() : chosen;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void PickFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = target.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    private void SetStatus(string text) => _status.Text = text;

    private sealed class NamedItem
    {
        public string Value { get; }
        private readonly string _label;
        public NamedItem(string value, string label) { Value = value; _label = label; }
        public override string ToString() => _label;
    }

    private sealed class SetOption
    {
        public ExportIfcInputSet Set { get; }
        public SetOption(ExportIfcInputSet set) => Set = set;
        public override string ToString() => Set.Name;
    }

    private void RefreshModelSets()
    {
        var ticked = new HashSet<string>(
            _modelSetsList.CheckedItems.Cast<SetItem>().Select(i => ModelSetAccess.SetKey(i.Preset)),
            StringComparer.OrdinalIgnoreCase);
        _modelSetsList.BeginUpdate();
        _modelSetsList.Items.Clear();
        foreach (ModelPreset p in ModelSetAccess.VisibleSets())
        {
            int i = _modelSetsList.Items.Add(new SetItem(p));
            if (ticked.Contains(ModelSetAccess.SetKey(p))) _modelSetsList.SetItemChecked(i, true);
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
