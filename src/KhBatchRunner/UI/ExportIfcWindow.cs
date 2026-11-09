using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
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
    private readonly TextBox _nameBox;
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

    private readonly ListBox _modelSetsList;
    private readonly TextBox _destBox;
    private readonly Label _status;

    /// <summary>The options to run with, set only when the user chose Run. Null on Close.</summary>
    public ExportIfcInputSet? RunSpec { get; private set; }

    /// <summary>The model set to run over, when <see cref="RunSpec"/> is set.</summary>
    public string SelectedModelSetName { get; private set; } = "";

    public ExportIfcWindow()
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

        _setsCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _setsCombo.SelectedIndexChanged += (_, _) => LoadSelectedSet();

        _nameBox = new TextBox();
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

        _modelSetsList = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.One };
        ModelSetAccess.AttachGreying(_modelSetsList, i => (i as SetItem)?.Preset,
            onSnapshotReady: RefreshModelSets);
        RefreshModelSets();

        _destBox = new TextBox
        {
            Text = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KhBatchRunner")
        };
        var destBrowse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        destBrowse.Click += (_, _) => PickFolder(_destBox);

        var saveBtn = new Button { Text = "Save set", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        saveBtn.Click += (_, _) => SaveSet();
        var dupBtn = new Button { Text = "Duplicate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        dupBtn.Click += (_, _) => DuplicateSet();
        var deleteBtn = new Button { Text = "Delete set", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        deleteBtn.Click += (_, _) => DeleteSet();

        var runBtn = new Button
        {
            Text = "Run", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 0)
        };
        runBtn.Click += (_, _) => Commit();
        var cancelBtn = new Button
        {
            Text = "Close", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel
        };

        _status = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Saved options set:", _setsCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Set name:", _nameBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Output folder:", _outputBox, browse));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("3D view title contains:", viewRow));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("IFC version:", _schemaCombo));
        FormLayout.AddAutoRow(root, grid);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Extra parameters (comma separated):", _extraParamsBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Keep runs for:", keepRow));
        FormLayout.AddAutoRow(root, FormLayout.FlowRow(saveBtn, dupBtn, deleteBtn));
        FormLayout.AddAutoRow(root, new Label { Text = "Model set to run over:", AutoSize = true });
        FormLayout.AddFillRow(root, _modelSetsList, 100);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Export CSV log to:", _destBox, destBrowse));
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

        ReloadSets(ExportIfcSetStore.TryLoadLastUsed()?.Name);
    }

    private void ReloadSets(string? select)
    {
        _setsCombo.Items.Clear();
        foreach (ExportIfcInputSet s in ExportIfcSetStore.LoadAll())
            _setsCombo.Items.Add(new SetOption(s));

        if (_setsCombo.Items.Count == 0) return;
        for (int i = 0; i < _setsCombo.Items.Count && select != null; i++)
            if (((SetOption)_setsCombo.Items[i]).Set.Name.Equals(select, StringComparison.OrdinalIgnoreCase))
            { _setsCombo.SelectedIndex = i; return; }
        _setsCombo.SelectedIndex = 0;
    }

    private void LoadSelectedSet()
    {
        if (_setsCombo.SelectedItem is not SetOption opt) return;
        ExportIfcInputSet s = opt.Set;

        _nameBox.Text = s.Name;
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

    private ExportIfcInputSet Current()
    {
        var schema = IfcSchemaChoice.Ifc4ReferenceView;
        if (_schemaCombo.SelectedItem is NamedItem ni) Enum.TryParse(ni.Value, out schema);

        // A retention that will not parse falls back to the default rather than failing the dialog.
        if (!int.TryParse(_keepDaysBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int keep) || keep < 0)
            keep = 14;

        return new ExportIfcInputSet
        {
            Name = _nameBox.Text.Trim(),
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
            ProjectLogName = "Export IFC + Data"
        };
    }

    private void SaveSet()
    {
        ExportIfcInputSet set = Current();
        if (string.IsNullOrWhiteSpace(set.Name))
        {
            SetStatus("Give the set a name before saving.");
            return;
        }
        ExportIfcSetStore.Save(set);
        ReloadSets(set.Name);
        SetStatus($"Saved '{set.Name}'.");
    }

    private void DuplicateSet()
    {
        if (_setsCombo.SelectedItem is not SetOption opt)
        {
            SetStatus("Select a saved set to duplicate.");
            return;
        }
        ExportIfcInputSet copy = opt.Set.Clone();
        copy.Name = SetDuplication.UniqueName(opt.Set.Name, ExportIfcSetStore.LoadAll().Select(s => s.Name));
        try
        {
            ExportIfcSetStore.Save(copy);
        }
        catch (Exception ex)
        {
            SetStatus("Could not duplicate set: " + ex.Message.Split('\n')[0]);
            return;
        }
        ReloadSets(copy.Name);
        SetStatus($"Duplicated '{opt.Set.Name}' as '{copy.Name}'.");
    }

    private void DeleteSet()
    {
        if (_setsCombo.SelectedItem is not SetOption opt)
        {
            SetStatus("Select a saved set to delete.");
            return;
        }
        ExportIfcSetStore.Delete(opt.Set.Name);
        ReloadSets(null);
        SetStatus($"Deleted '{opt.Set.Name}'.");
    }

    private void Commit()
    {
        ExportIfcInputSet set = Current();
        if (_modelSetsList.SelectedItem is not SetItem chosen)
        {
            SetStatus("Select the model set to run over.");
            return;
        }
        if (!set.HasOutput)
        {
            SetStatus("Set an output folder, or a CSV log folder to write the exports beside.");
            return;
        }

        ExportIfcSetStore.SaveLastUsed(set);
        RunSpec = set;
        SelectedModelSetName = chosen.Preset.Name;
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
