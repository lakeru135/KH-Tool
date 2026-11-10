using System.Windows.Forms;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.Drw.DoorWindow;
using KhBatchRunner.UI;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>One item as the Items tab lists it.</summary>
internal sealed class DwItemRow
{
    public string Mark { get; set; } = "";
    public string Category { get; set; } = "";
    public string Types { get; set; } = "";
    public int Qty { get; set; }
    public string Size { get; set; } = "";
    public string Warnings { get; set; } = "";
    /// <summary>For the sheet preview, model mm: width, height + sill, width in plan (wall either
    /// side included; 0 = not measured yet — <see cref="IDoorWindowApi.Measure"/>).</summary>
    public double WidthMm { get; set; }
    public double HeightSillMm { get; set; }
    public double PlanWidthMm { get; set; }
    /// <summary>What the elevation draws, model mm (0 = not measured yet): its width, and its height
    /// with the sill. A frame or a default-size family can draw past the nominal size.</summary>
    public double ElevationWidthMm { get; set; }
    public double ElevationHeightMm { get; set; }
    /// <summary>Its size is an instance parameter: drawn as an outline at the placed size.</summary>
    public bool InstanceSized { get; set; }
    /// <summary>Its cell's width set by hand in a legend, paper mm (0: none), remembered by the build.</summary>
    public double AdjustedWidthMm { get; set; }
    /// <summary>Its dimensions moved by hand in a legend, remembered by the build.</summary>
    public int MovedDims { get; set; }
    public bool IsDoor => Category == "Door";
    /// <summary>As <see cref="DwItem.Key"/>: category + Type Mark.</summary>
    public string Key => (IsDoor ? "D|" : "W|") + (Mark.StartsWith("(") ? "" : Mark);
}

/// <summary>
/// What the window asks of the model, each only when first needed (the user, 2026-10-21: the first
/// press was slow on a large model). Runs where the Revit API may be used.
/// </summary>
internal interface IDoorWindowApi
{
    /// <summary>Doors and windows, as the Items tab lists them.</summary>
    List<DwItemRow> Items(DoorWindowPreset p);
    DwTitleBlockData? TitleBlock(string name);
    /// <summary>What each placed type draws, model mm, by <see cref="DwItemRow.Key"/>: plan width,
    /// elevation width, elevation height with the sill; and the text fit for wrapping.</summary>
    DwMeasured Measure(DoorWindowPreset p);
    /// <summary>Forgets the set's hand edits (cell widths, moved dimensions) and rebuilds it; says how it went.</summary>
    string ResetWidths(DoorWindowPreset p);
    /// <summary>The sheet parameters the Sheets tab offers (the browser organization's first).</summary>
    List<DwSheetParameterChoice> SheetParameters();
    /// <summary>The set's schedule, read; null when it has none yet.</summary>
    DwScheduleData? Schedule(DoorWindowPreset p);
    /// <summary>The set's schedule made from the starter if missing (or put back in shape), opened
    /// as the active view, and read. Says in <paramref name="did"/> what it made or changed.</summary>
    DwScheduleData? EnsureAndOpen(DoorWindowPreset p, List<string> did);
    /// <summary>Builds the legends; shows the results table; says how it went.</summary>
    string Build(DoorWindowPreset p);
    string ApplyNow();
    /// <summary>False when the set is not built in this model yet (then it is saved with the build).</summary>
    bool SetLive(DoorWindowPreset p, bool live);
    /// <summary>Saves a row's "Value from" on the set's schedule (field parameter id → choice) and
    /// reads the schedule again.</summary>
    DwScheduleData? SetValueFrom(DoorWindowPreset p, long fieldId, string choice);
    /// <summary>Adds or removes the schedule's Size (Width x Height) and Count fields; reads it again
    /// and says what changed.</summary>
    (DwScheduleData? Schedule, List<string> Did) SetSizeQty(DoorWindowPreset p, bool size, bool qty);
}

/// <summary>What <see cref="IDoorWindowApi.Measure"/> answers.</summary>
internal sealed class DwMeasured
{
    public Dictionary<string, (double Plan, double ElevationWidth, double ElevationHeight)> Extents { get; } = new(StringComparer.Ordinal);
    /// <summary>How much wider Revit draws the table font than GDI measures it.</summary>
    public double TextFit { get; set; } = 1;
}

/// <summary>The window's link to the model. The window stays open beside Revit, so its requests
/// run later, when Revit is free (<c>DoorWindowSession</c>'s external event), and answer on the
/// window's thread.</summary>
internal interface IDoorWindowModel
{
    void Run<T>(Func<IDoorWindowApi, T> work, Action<T> done);
}

/// <summary>What the Enlarge Door &amp; Window window shows first, read from the model by the
/// command. Plain data, so the window builds without Revit (the window gates construct it outside
/// Revit; with no <see cref="Model"/> it shows only what is here).</summary>
internal sealed class DoorWindowDialogData
{
    public List<DwItemRow> Items { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> DimensionTypes { get; set; } = new();
    /// <summary>How the project shows lengths, e.g. "metric (Millimeters)".</summary>
    public string ProjectUnits { get; set; } = "metric (Millimeters)";
    /// <summary>A 900 x 2100 door written in the project's units, for the sample cell.</summary>
    public string SampleSize { get; set; } = "900 x 2100";
    /// <summary>The model's title blocks: names, and (the gates' fixtures) their lines already read.</summary>
    public List<DwTitleBlockData> TitleBlocks { get; set; } = new();
    /// <summary>The opening set's schedule, as the command made/opened and read it.</summary>
    public DwScheduleData? Schedule { get; set; }
    /// <summary>The gates' fixtures: a schedule per category, used when there is no model.</summary>
    public List<DwScheduleData> Schedules { get; set; } = new();
    public IDoorWindowModel? Model { get; set; }
}

/// <summary>
/// Enlarge Door &amp; Window: doors or windows, the set's own schedule (its shown fields are the
/// table rows; it is open beside this window for editing, and the window follows its edits), the
/// layout of the legends and their sheets. Stays open: Build legends runs and the window stays.
/// </summary>
internal sealed class DoorWindowWindow : DrwToolWindow
{
    private const string None = "(none)";
    private const string Reading = "reading…";
    private readonly DoorWindowDialogData _data;
    private readonly IDoorWindowModel? _model;
    private readonly string _sampleSize;
    private DwScheduleData? _schedule;
    private readonly Dictionary<string, DwTitleBlockData?> _titleBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _titleBlocksAsked = new(StringComparer.OrdinalIgnoreCase);
    private bool _planWidthsAsked;
    /// <summary>How much wider Revit draws the table font than GDI measures it (measured with the extents).</summary>
    private double _textFit = 1;
    private List<DwSheetParameterChoice>? _sheetChoices;
    private bool _sheetChoicesAsked;
    /// <summary>Something the window shows changed in the model; the timer asks for a fresh read.</summary>
    private bool _stale;
    private int _busy;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private readonly RadioButton _doors, _windows;
    private readonly CheckBox _unmarked, _live, _sizeRow, _qtyRow, _elevationFirst;
    private readonly ComboBox _description, _dimType, _titleBlock;
    private readonly TextBox _sizeLabel, _qtyLabel, _blank, _legendName, _elevTitle, _planTitle, _sheetNumber, _sheetName, _floorLabel;
    private readonly Label _scheduleName, _scheduleNote, _status, _sheetSummary, _cellReal, _elevReal, _planReal;
    private readonly DataGridView _rowsPreview, _items;
    private readonly DataGridViewComboBoxColumn _shows;
    private readonly NumericUpDown _scale, _cellWidth, _elevBox, _planBox, _header, _rowLine, _labelWidth, _inset, _wallStub,
        _columns, _rowsPerLegend, _floorGap, _dimOffset, _margin, _sheetShown;
    private readonly Button _resetWidths;
    private readonly DataGridView _sheetParams;
    private readonly DataGridViewComboBoxColumn _sheetParamName;
    private readonly TabControl _tabs;
    private readonly TabPage _sheetsPage;
    private readonly DoorWindowSheetPreview _sheetPreview = new();
    private const string NoTitleBlock = "(none: legends only)";
    private readonly DrwPresetBar<DoorWindowPreset> _bar;
    private readonly DoorWindowDiagram _diagram = new();
    private DoorWindowDiagram.Part? _activePart;
    private bool _applying;

    public DoorWindowPreset Result { get; private set; } = new();

    public DoorWindowWindow(DoorWindowDialogData data, DoorWindowPreset initial)
    {
        Text = "Enlarge Door & Window";
        _sampleSize = data.SampleSize;
        _data = data;
        _model = data.Model;
        _schedule = data.Schedule;
        _status = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 4, 3, 2) };

        // ---------------- Items: doors or windows
        _doors = new RadioButton { Text = "Doors", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 12, 4) };
        _windows = new RadioButton { Text = "Windows", AutoSize = true, Margin = new Padding(3, 4, 12, 4) };
        _unmarked = DrwControls.Check("Include types with no Type Mark", false);
        _live = DrwControls.Check("Live: keep these legends current as doors/windows change", true);
        var apply = DrwControls.Button("Apply now", 100, ApplyNow);
        apply.Enabled = _model != null;
        _items = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false
        };
        foreach ((string name, int weight) in new[] { ("Mark", 10), ("Size", 18), ("Qty", 8), ("Cell", 12), ("Types", 30), ("Warnings", 26) })
            _items.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, FillWeight = weight });
        var itemsPage = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(itemsPage, FormLayout.FlowRow(new Label { Text = "Schedule:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, _doors, _windows, _unmarked));
        FormLayout.AddAutoRow(itemsPage, FormLayout.NoteLabel(this,
            "Doors and windows are separate sets, each with its own schedule, legends and sheets: save one preset for each. " +
            "One item per Type Mark of the placed types (types sharing a mark are merged)." +
            (data.Warnings.Count > 0 ? "\n" + string.Join("\n", data.Warnings) : "")));
        FormLayout.AddFillRow(itemsPage, _items, 100);
        FormLayout.AddAutoRow(itemsPage, FormLayout.FlowRow(_live, apply));
        FormLayout.AddAutoRow(itemsPage, FormLayout.NoteLabel(this,
            "Live writes changed values inside your edit (one Undo takes back both); a change the cells cannot take — a new " +
            "Type Mark, a new size, a field added to the schedule — rebuilds the set a moment later. Apply now brings every " +
            "door & window set in this model up to date, live or not (after a Sync with Central, for one)."));

        // ---------------- Fields: the set's own schedule
        _scheduleName = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 6, 12, 3), Font = new Font(Font, FontStyle.Bold) };
        var open = DrwControls.Button("Open schedule", 110, OpenSchedule);
        open.Enabled = _model != null;
        _scheduleNote = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 4, 3, 4), MaximumSize = new Size(700, 0) };
        _description = DrwControls.Combo(new[] { None });
        _sizeRow = DrwControls.Check("Size row (Width x Height field), headed", true);
        _sizeLabel = new TextBox { Width = 120 };
        _qtyRow = DrwControls.Check("Qty row (Count field), headed", true);
        _qtyLabel = new TextBox { Width = 120 };
        _rowsPreview = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, EditMode = DataGridViewEditMode.EditOnEnter,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false
        };
        _rowsPreview.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = "Label", FillWeight = 25, ReadOnly = true });
        _rowsPreview.Columns.Add(new DataGridViewTextBoxColumn { Name = "From", HeaderText = "From", FillWeight = 18, ReadOnly = true });
        _shows = new DataGridViewComboBoxColumn
        {
            Name = "ValueFrom", HeaderText = "Value from", FillWeight = 24, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        };
        _rowsPreview.Columns.Add(_shows);
        _rowsPreview.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "First item's value", FillWeight = 37, ReadOnly = true });
        _rowsPreview.DataError += (_, e) => e.ThrowException = false;
        // A pick takes effect at once, not when the cell is left.
        _rowsPreview.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_rowsPreview.IsCurrentCellDirty) _rowsPreview.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _rowsPreview.CellValueChanged += (_, e) =>
        {
            if (_fillingRows || e.RowIndex < 0 || e.ColumnIndex != _shows.Index || _rowsPreview.Rows[e.RowIndex].Cells[e.ColumnIndex] is not DataGridViewComboBoxCell c
                || _rowsPreview.Rows[e.RowIndex].Tag is not DwRowDef def) return;
            string choice = DwValueFrom.Parse(c.Value?.ToString() ?? "");
            // After the grid's own event (rebuilding rows inside it upsets the grid); a window not yet
            // shown has no handle to queue on, and no edit in progress either.
            if (IsHandleCreated) BeginInvoke(new Action(() => ValueFromPicked(def, choice))); else ValueFromPicked(def, choice);
        };
        var fieldsPage = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(fieldsPage, FormLayout.FlowRow(new Label { Text = "Schedule:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, _scheduleName, open));
        FormLayout.AddAutoRow(fieldsPage, _scheduleNote);
        FormLayout.AddAutoRow(fieldsPage, FormLayout.LabeledRow("Description (beside the mark):", _description));
        FormLayout.AddAutoRow(fieldsPage, FormLayout.FlowRow(_sizeRow, _sizeLabel, _qtyRow, _qtyLabel));
        FormLayout.AddAutoRow(fieldsPage, FormLayout.NoteLabel(this,
            "The rows are the shown fields of this set's own schedule, in its order, labelled by their column headings. It is " +
            "open in Revit beside this window: add, remove, reorder and rename fields there (Properties > Fields / Formatting) " +
            "and this list follows. Formulas, combined parameters and filters work. Size and Qty are fields too: ticking one adds " +
            "Revit's combined Width x Height or Count field to the schedule (with the heading beside it), unticking removes it; " +
            "format and move them in the schedule like any other. A material field (Frame Material…) " +
            "can show something other than the material's name under 'Value from': its Description (the default), any " +
            "other property of the material, or a parameter of the door type. That choice is saved in the model with the " +
            "schedule, so everyone's legends agree."));
        FormLayout.AddFillRow(fieldsPage, _rowsPreview, 100);

        // ---------------- Layout
        _scale = DrwControls.Number(50, 1, 1000, 0, 5);
        _cellWidth = DrwControls.Number(70, 20, 500, 0, 5);
        _elevBox = DrwControls.Number(70, 10, 500, 0, 5);
        _planBox = DrwControls.Number(30, 10, 500, 0, 5);
        _header = DrwControls.Number(10, 4, 50, 1, 1);
        _rowLine = DrwControls.Number(7.5, 2, 30, 1, 0.5);
        _labelWidth = DrwControls.Number(20, 5, 200, 1, 1);
        _inset = DrwControls.Number(2.5, 0, 20, 1, 0.5);
        _wallStub = DrwControls.Number(15, 0, 100, 1, 1);
        // The distance each cell size is in the model, beside it (the user, 2026-10-22: to see it
        // against their usual door and window sizes).
        _cellReal = RealLabel();
        _elevReal = RealLabel();
        _planReal = RealLabel();
        _resetWidths = DrwControls.Button("Reset hand edits (0)", 220, ResetWidths);
        _resetWidths.Enabled = false;
        _columns = DrwControls.Number(4, 1, 20, 0, 1);
        _rowsPerLegend = DrwControls.Number(2, 1, 20, 0, 1);
        _floorGap = DrwControls.Number(5, 0, 50, 1, 1);
        _dimOffset = DrwControls.Number(5, 1, 30, 1, 1);
        _floorLabel = new TextBox { Width = 80 };
        _elevationFirst = DrwControls.Check("Elevation above plan (plan first when off)", true);
        _elevTitle = new TextBox { Width = 200 };
        _planTitle = new TextBox { Width = 200 };
        _dimType = DrwControls.Combo(data.DimensionTypes, withNone: true);
        _blank = new TextBox { Width = 80 };
        _legendName = new TextBox { Width = 240 };
        var layoutPage = DrwControls.Page(
            DrwControls.Group("Cell (paper mm)",
                FormLayout.LabeledRow("Legend scale 1:", _scale),
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.CellWidth, "Cell width:"), _cellWidth, _cellReal),
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.ElevationBox, "Elevation box height:"), _elevBox, _elevReal),
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.PlanBox, "Plan box height:"), _planBox, _planReal),
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.Header, "Header strip height:"), _header),
                FormLayout.LabeledRow("Elevation box title:", _elevTitle),
                FormLayout.LabeledRow("Plan box title:", _planTitle),
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.WallStub, "Wall either side in plan:"), _wallStub),
                _elevationFirst,
                FormLayout.NoteLabel(this, "The numbers match the sample cell; click a number there to jump to its field. " +
                    "Cell width and elevation box are minimums: an item that would not fit at this scale spans more cells, and its row of cells grows to fit it. " +
                    "Hand edits in a legend are remembered: a cell's width changed by hand (its frame's Cell Width) keeps its width and moves the cells after it along, " +
                    "and a dimension line or text moved by hand stays where it was put."),
                _resetWidths),
            DrwControls.Group("Table (paper mm)",
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.LineHeight, "Line height:"), _rowLine),
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.LabelWidth, "Label column width:"), _labelWidth),
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.Gap, "Gap before the value:"), _inset),
                FormLayout.LabeledRow("Empty value shows:", _blank),
                FormLayout.NoteLabel(this, "A value wraps at its cell's width (what is left past the label column and the gap), and its row grows a line for each line. " +
                    "Header text is 3 mm, table text 2 mm (TH Sarabun New).")),
            DrwControls.Group("Elevation",
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.FloorGap, "Floor line above the box bottom:"), _floorGap),
                FormLayout.LabeledRow("Floor line text (blank: none):", _floorLabel),
                FormLayout.LabeledRow(Numbered(DoorWindowDiagram.Part.DimOffset, "Dimensions stand off by:"), _dimOffset),
                FormLayout.LabeledRow("Dimension type:", _dimType)),
            DrwControls.Group("Legends",
                FormLayout.LabeledRow("Cells across:", _columns),
                FormLayout.LabeledRow("Rows of cells down:", _rowsPerLegend),
                FormLayout.LabeledRow("Legend name ({n} = number):", _legendName),
                FormLayout.NoteLabel(this, "Building again rebuilds the same legends (by this preset's name), so viewports on sheets stay. " +
                    "Header labels you dragged keep their place in their cell.")));

        // The sample cell beside the Layout fields, as Enlarge's sample sections sit beside theirs:
        // the field being edited is drawn in red, so it is plain which part of the cell it moves.
        foreach ((NumericUpDown n, DoorWindowDiagram.Part part) in new[]
                 {
                     (_cellWidth, DoorWindowDiagram.Part.CellWidth), (_elevBox, DoorWindowDiagram.Part.ElevationBox),
                     (_planBox, DoorWindowDiagram.Part.PlanBox), (_header, DoorWindowDiagram.Part.Header),
                     (_rowLine, DoorWindowDiagram.Part.LineHeight), (_labelWidth, DoorWindowDiagram.Part.LabelWidth),
                     (_inset, DoorWindowDiagram.Part.Gap), (_wallStub, DoorWindowDiagram.Part.WallStub),
                     (_floorGap, DoorWindowDiagram.Part.FloorGap), (_dimOffset, DoorWindowDiagram.Part.DimOffset)
                 })
        {
            n.Enter += (_, _) => { _activePart = part; UpdateDiagram(); };
            n.ValueChanged += (_, _) => { _activePart = part; UpdateDiagram(); };
            _partFields[part] = n;
        }
        foreach (NumericUpDown n in new[] { _scale, _columns, _rowsPerLegend })
            n.Enter += (_, _) => { _activePart = null; UpdateDiagram(); };
        _scale.ValueChanged += (_, _) => UpdateDiagram();
        _floorLabel.TextChanged += (_, _) => UpdateDiagram();
        // A number clicked in the sample cell: its field, scrolled into view.
        _diagram.PartClicked += part =>
        {
            if (!_partFields.TryGetValue(part, out NumericUpDown? n)) return;
            for (Control? c = n.Parent; c != null; c = c.Parent)
                if (c is TabPage tab) { _tabs.SelectedTab = tab; break; }
            n.Focus();
            n.Select(0, n.Text.Length);
            _activePart = part;
            UpdateDiagram();
        };
        _elevationFirst.CheckedChanged += (_, _) => UpdateDiagram();
        _elevTitle.TextChanged += (_, _) => UpdateDiagram();
        _planTitle.TextChanged += (_, _) => UpdateDiagram();
        var sample = new GroupBox { Text = "Sample cell", Dock = DockStyle.Fill, Padding = new Padding(6) };
        _diagram.Dock = DockStyle.Fill;
        sample.Controls.Add(_diagram);
        var layoutTab = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layoutTab.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layoutTab.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 350));
        layoutTab.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layoutPage.Dock = DockStyle.Fill;
        layoutTab.Controls.Add(layoutPage, 0, 0);
        layoutTab.Controls.Add(sample, 1, 0);

        // ---------------- Sheets
        _titleBlock = DrwControls.Combo(new[] { NoTitleBlock }.Concat(data.TitleBlocks.Select(t => t.Name)));
        _sheetNumber = new TextBox { Width = 120 };
        _sheetName = new TextBox { Width = 260 };
        _margin = DrwControls.Number(5, 0, 50, 1, 1);
        _sheetShown = DrwControls.Number(1, 1, 99, 0, 1);
        _sheetParams = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true, EditMode = DataGridViewEditMode.EditOnEnter,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false, MinimumSize = new Size(0, 110)
        };
        _sheetParamName = new DataGridViewComboBoxColumn
        {
            Name = "Parameter", HeaderText = "Parameter", FillWeight = 50, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        };
        _sheetParams.Columns.Add(_sheetParamName);
        _sheetParams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", FillWeight = 50 });
        _sheetParams.DataError += (_, e) => e.ThrowException = false;
        _sheetParams.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_sheetParams.IsCurrentCellDirty && _sheetParams.CurrentCell is DataGridViewComboBoxCell) _sheetParams.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _sheetParams.CellValueChanged += (_, e) =>
        {
            // A parameter picked: its value starts as the one most sheets use.
            if (_applying || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            DataGridViewRow r = _sheetParams.Rows[e.RowIndex];
            string name = ParamName(r.Cells[0].Value?.ToString());
            if ((r.Cells[1].Value?.ToString() ?? "").Length == 0 && _sheetChoices?.FirstOrDefault(c => c.Name == name) is { Values.Count: > 0 } c)
                r.Cells[1].Value = c.Values[0];
        };
        // A width cap, so the label's preferred height counts its wrapped lines and the Preview group
        // grows to show them (uncapped, the second line was cut off at the group's bottom).
        _sheetSummary = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 6, 3, 3), MaximumSize = new Size(380, 0) };
        var sheetFields = DrwControls.Page(
            DrwControls.Group("Sheets",
                FormLayout.LabeledRow("Title block:", _titleBlock),
                FormLayout.LabeledRow("First sheet number:", _sheetNumber),
                FormLayout.LabeledRow("Sheet name:", _sheetName),
                FormLayout.LabeledRow("Margin inside the frame (mm):", _margin),
                FormLayout.NoteLabel(this, "One sheet per legend. Each legend holds what fits inside the title block's frame " +
                    "(red dashes, found from its lines) with the margin kept clear; the rest goes on the next sheet, numbered on " +
                    "from the first (A-12, A-13...). Cells across and rows of cells down then follow the frame, not the Layout tab. " +
                    "With no title block, only legends are made.")),
            DrwControls.Group("Sheet parameters",
                _sheetParams,
                FormLayout.NoteLabel(this, "Written on the set's sheets at every build. Those marked * are what your sheet browser organization " +
                    "groups by, so the sheets land in the right folder; any other sheet parameter can be added.")),
            DrwControls.Group("Preview",
                FormLayout.LabeledRow("Show sheet:", _sheetShown),
                _sheetSummary));
        var sheetSample = new GroupBox { Text = "Sheet", Dock = DockStyle.Fill, Padding = new Padding(6) };
        _sheetPreview.Dock = DockStyle.Fill;
        sheetSample.Controls.Add(_sheetPreview);
        var sheetsTab = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        sheetsTab.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430));
        sheetsTab.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sheetsTab.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sheetFields.Dock = DockStyle.Fill;
        sheetsTab.Controls.Add(sheetFields, 0, 0);
        sheetsTab.Controls.Add(sheetSample, 1, 0);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _sheetsPage = new TabPage("Sheets") { Padding = new Padding(4) };
        foreach ((string title, Control page) in new (string, Control)[] { ("Items", itemsPage), ("Fields", fieldsPage), ("Layout", layoutTab), ("Sheets", sheetsTab) })
        {
            TabPage tp = title == "Sheets" ? _sheetsPage : new TabPage(title) { Padding = new Padding(4) };
            page.Dock = DockStyle.Fill;
            tp.Controls.Add(page);
            _tabs.TabPages.Add(tp);
        }

        // A status line under the tabs: what the last request did.
        var body = FormLayout.SingleColumn();
        FormLayout.AddFillRow(body, _tabs, 100);
        FormLayout.AddAutoRow(body, _status);

        _bar = new DrwPresetBar<DoorWindowPreset>(this, DoorWindowStores.Store, Capture, Apply, initial.Name);
        Apply(initial);
        _activePart = null;   // loading the preset raised every field's ValueChanged
        UpdateDiagram();

        _doors.CheckedChanged += (_, _) => { if (_doors.Checked) CategoryChanged(); };
        _windows.CheckedChanged += (_, _) => { if (_windows.Checked) CategoryChanged(); };
        _description.SelectedIndexChanged += (_, _) => UpdateRows();
        _unmarked.CheckedChanged += (_, _) => { UpdateItems(); UpdateRows(); };
        foreach (CheckBox c in new[] { _sizeRow, _qtyRow }) c.CheckedChanged += (_, _) => SizeQtyChanged();
        foreach (TextBox t in new[] { _sizeLabel, _qtyLabel }) t.TextChanged += (_, _) => UpdateRows();
        _live.CheckedChanged += (_, _) => LiveChanged();
        // Title blocks and plan widths are read when the Sheets tab first shows them.
        _titleBlock.SelectedIndexChanged += (_, _) => UpdateSheets();
        _margin.ValueChanged += (_, _) => UpdateSheets();
        _sheetShown.ValueChanged += (_, _) => UpdateSheets();
        _tabs.SelectedIndexChanged += (_, _) => UpdateSheets();
        _timer.Tick += (_, _) => { if (_stale && _busy == 0) Refresh(open: false); };
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
        Build(_bar.Row, body, "Build legends", new Size(1060, 760), new Size(900, 600));
        // Each tab its own label column: aligned across all four, the Layout tab's longest label set
        // the Sheets tab's too, and the title block combo was crushed to 77 px (UI layout, 2026-10-22).
        FormLayout.AlignLabels(this, itemsPage, fieldsPage, layoutPage, sheetFields);
    }

    private bool IsDoor => _doors.Checked;

    private void UpdateDiagram()
    {
        DoorWindowPreset p = Capture();
        _diagram.Set(p, _activePart, _sampleSize);
        _cellReal.Text = $"= {DoorWindowDiagram.Real(p.CellWidthMm, p.Scale)}";
        _elevReal.Text = $"= {DoorWindowDiagram.Real(p.ElevationBoxMm, p.Scale)}";
        _planReal.Text = $"= {DoorWindowDiagram.Real(p.PlanBoxMm, p.Scale)}";
    }

    private readonly Dictionary<DoorWindowDiagram.Part, NumericUpDown> _partFields = new();

    private static Label RealLabel() => new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(6, 6, 3, 3) };

    /// <summary>A Layout field's label with its number in the sample cell: "1. Cell width:".</summary>
    private static string Numbered(DoorWindowDiagram.Part part, string label) => $"{(int)part + 1}. {label}";

    /// <summary>The Layout tab's distance labels, "= 3.50 m" at the scale in the row above (the self-test reads them).</summary>
    internal List<string> RealDistances => new() { _cellReal.Text, _elevReal.Text, _planReal.Text };

    /// <summary>Clicks setting <paramref name="part"/>'s key line in the sample cell (the self-test).</summary>
    internal void ClickPart(DoorWindowDiagram.Part part)
    {
        // Shown and painted first: where the key lines are is only known once drawn.
        for (Control? c = _diagram.Parent; c != null; c = c.Parent)
            if (c is TabPage tab) { _tabs.SelectedTab = tab; break; }
        _diagram.Refresh();
        _diagram.ClickAt(_diagram.KeyPoint(part));
    }

    /// <summary>The sample cell (the self-test clicks it).</summary>
    internal DoorWindowDiagram Diagram => _diagram;

    /// <summary>The Layout field that has the focus (the self-test).</summary>
    internal DoorWindowDiagram.Part? FocusedPart => _partFields.Where(kv => kv.Value.ContainsFocus).Select(kv => (DoorWindowDiagram.Part?)kv.Key).FirstOrDefault();

    /// <summary>Size or Qty ticked: with the set's schedule in the model, its field is added or
    /// removed there and the schedule read again; without one, only the preview follows.</summary>
    private void SizeQtyChanged()
    {
        if (_applying) return;
        if (_schedule == null || _model == null) { UpdateItems(); UpdateRows(); return; }
        DoorWindowPreset p = Capture();
        Ask("Changing the schedule's Size / Qty fields…", api => api.SetSizeQty(p, p.SizeRow, p.QtyRow), r =>
        {
            Say(r.Did.Count > 0 ? $"Schedule: {string.Join("; ", r.Did)}." : "");
            SetSchedule(r.Schedule);
        });
    }

    private void Say(string text) => _status.Text = text;

    // ------------------------------------------------------------------ requests to the model

    /// <summary>Runs <paramref name="work"/> where the Revit API may be used; <paramref name="done"/>
    /// back here. With no model (the gates) nothing runs.</summary>
    private void Ask<T>(string doing, Func<IDoorWindowApi, T> work, Action<T> done)
    {
        if (_model == null) return;
        _busy++;
        if (doing.Length > 0) Say(doing);
        _model.Run(work, r =>
        {
            _busy = Math.Max(0, _busy - 1);
            if (!IsDisposed) done(r);
        });
    }

    /// <summary>The model changed something the window shows (<c>DoorWindowSession</c>).</summary>
    internal void MarkStale() => _stale = true;

    /// <summary>A fresh read of the set's schedule and the items; <paramref name="open"/> also makes
    /// the set's starter if missing and opens it.</summary>
    private void Refresh(bool open)
    {
        _stale = false;
        DoorWindowPreset p = Capture();
        Ask(open ? "Opening the set's schedule…" : "", api =>
            {
                var did = new List<string>();
                DwScheduleData? s = open ? api.EnsureAndOpen(p, did) : api.Schedule(p);
                return (Schedule: s, Items: api.Items(p), Did: did);
            },
            r =>
            {
                _data.Items = r.Items;
                _planWidthsAsked = false;
                SetSchedule(r.Schedule);
                UpdateItems();
                if (r.Did.Count > 0) Say("Schedule: " + string.Join("; ", r.Did) + ".");
                else if (open) Say($"'{r.Schedule?.Name}' is open in Revit: edit its fields there.");
            });
    }

    /// <summary>The set's schedule as just read (the self-test calls it to stand in for a re-read).</summary>
    internal void SetSchedule(DwScheduleData? s)
    {
        _schedule = s;
        _scheduleName.Text = s?.Name ?? "(made when you build)";
        // The Size and Qty ticks say what the schedule has.
        if (s != null)
        {
            bool was = _applying;
            _applying = true;
            try
            {
                _sizeRow.Checked = s.SizeColumn >= 0;
                _qtyRow.Checked = s.CountColumn >= 0;
            }
            finally { _applying = was; }
        }
        string keep = DrwControls.Text(_description);
        _description.BeginUpdate();
        _description.Items.Clear();
        _description.Items.Add(None);
        foreach (string h in s?.Headings ?? new List<string>()) _description.Items.Add(h);
        _description.EndUpdate();
        DrwControls.Select(_description, keep.Length > 0 ? keep : None);
        if (_description.SelectedIndex < 0) DrwControls.Select(_description, s?.Headings.FirstOrDefault(h => h.Equals("Description", StringComparison.OrdinalIgnoreCase)) ?? None);
        _scheduleNote.Text = s == null
            ? "This set's schedule is made from the starter when you open it or build."
            : $"{s.ByMark.Count} Type Mark(s), {s.Headings.Count} shown field(s) besides Type Mark" +
              (s.MaterialColumns.Count > 0 ? $", {s.MaterialColumns.Count} of them material(s)" : "") + "." +
              (s.Problems.Count > 0 ? " " + string.Join(" ", s.Problems) + " (put right when you build)" : "");
        UpdateRows();
    }

    private DwTitleBlockData? TitleBlockData(string name)
    {
        if (_titleBlocks.TryGetValue(name, out DwTitleBlockData? t)) return t;
        DwTitleBlockData? known = _data.TitleBlocks.FirstOrDefault(x => x.Name == name);
        if (known is { Lines.Count: > 0 } || _model == null) return _titleBlocks[name] = known;
        if (_titleBlocksAsked.Add(name))
            Ask("Reading the title block…", api => api.TitleBlock(name), r =>
            {
                _titleBlocks[name] = r ?? known;
                Say("");
                UpdateSheets();
            });
        return null;
    }

    private void ReadPlanWidths()
    {
        if (_planWidthsAsked || _model == null) return;
        _planWidthsAsked = true;
        DoorWindowPreset p = Capture();
        Ask("Measuring the doors and windows…", api => api.Measure(p), measured =>
        {
            _textFit = measured.TextFit;
            foreach (DwItemRow i in _data.Items)
                if (measured.Extents.TryGetValue(i.Key, out var e))
                {
                    i.PlanWidthMm = e.Plan;
                    i.ElevationWidthMm = e.ElevationWidth;
                    i.ElevationHeightMm = e.ElevationHeight;
                }
            Say("");
            UpdateSheets();
        });
    }

    // ------------------------------------------------------------------ items and fields

    private List<DwItemRow> Included(DoorWindowPreset p) =>
        _data.Items.Where(i => i.IsDoor == (p.Category == DwCategory.Door) && (p.IncludeUnmarked || !i.Mark.StartsWith("("))).ToList();

    private void CategoryChanged()
    {
        if (_applying) return;
        // A legend name left at the other category's default follows the category.
        string other = IsDoor ? "Window Schedule {n}" : "Door Schedule {n}";
        if (_legendName.Text.Trim() == other || _legendName.Text.Trim() == "Door & Window Schedule {n}")
            _legendName.Text = IsDoor ? "Door Schedule {n}" : "Window Schedule {n}";
        SetSchedule(_model == null ? _data.Schedules.FirstOrDefault(s => s.IsDoor == IsDoor) : null);
        UpdateItems();
        Refresh(open: true);
    }

    private void OpenSchedule() => Refresh(open: true);

    private void UpdateRows()
    {
        DoorWindowPreset p = Capture();
        DwScheduleData? s = _schedule;
        DwItemRow? first = Included(p).FirstOrDefault();
        // Setting a material cell's value raises CellValueChanged, which refills the rows: without
        // this the grid refilled itself for ever.
        _fillingRows = true;
        try { FillRows(p, s, first); }
        finally { _fillingRows = false; }
        UpdateSheets();
    }

    private bool _fillingRows;

    private void FillRows(DoorWindowPreset p, DwScheduleData? s, DwItemRow? first)
    {
        _rowsPreview.Rows.Clear();
        foreach (DwRowDef r in DoorWindowItems.Rows(p, s))
        {
            bool schedule = s != null && r.Column >= 0;
            bool material = schedule && r.Kind == DwRowKind.Cell && s!.MaterialColumns.Contains(r.Column);
            string from = schedule && s!.ValueFrom.TryGetValue(r.Column, out string? f) ? f : DwValueFrom.Cell;
            string value = first == null ? "" : RowValue(first, r, p, s);
            // Not the cell: say what the schedule itself shows, so the two are never confused.
            if (schedule && first != null && from != DwValueFrom.Cell)
                value += $"   (schedule shows: {(s!.Cell(first.Mark, r.Column) is { Length: > 0 } cell ? cell : "-")})";
            string source = r.Kind switch
            {
                DwRowKind.Size => schedule ? "Schedule: Width x Height" : "Size (this tool)",
                DwRowKind.Qty => schedule ? "Schedule: Count" : "Qty (this tool)",
                _ => material ? "Schedule: material" : "Schedule"
            };
            int i = _rowsPreview.Rows.Add(r.Label, source,
                null, value);
            DataGridViewRow row = _rowsPreview.Rows[i];
            row.Tag = r;
            // Only a material row may take its value from elsewhere (the user, 2026-10-21: on a
            // plain row, Type:/Instance: only confused — add the parameter to the schedule instead).
            if (material)
            {
                var combo = (DataGridViewComboBoxCell)row.Cells[_shows.Index];
                foreach (string choice in Choices(s!).Append(DwValueFrom.Show(from)).Distinct()) combo.Items.Add(choice);
                combo.Value = DwValueFrom.Show(from);
            }
            else row.Cells[_shows.Index] = new DataGridViewTextBoxCell { Value = schedule ? DwValueFrom.Show(DwValueFrom.Cell) : "" };
            row.Cells[_shows.Index].ReadOnly = !material;
        }
    }

    /// <summary>What a material row may take its value from: its cell (the material's name), any
    /// property of the material, or a parameter of the drawn type (a text "Frame Spec" and the like).</summary>
    private static IEnumerable<string> Choices(DwScheduleData s)
    {
        yield return DwValueFrom.Show(DwValueFrom.Cell);
        foreach (string prop in s.MaterialProperties) yield return DwValueFrom.Show(DwValueFrom.MaterialPrefix + prop);
        foreach (string name in s.TypeParameters) yield return DwValueFrom.Show(DwValueFrom.TypePrefix + name);
    }

    /// <summary>A "Value from" picked: saved on the schedule in the model (everyone's legends follow
    /// it), then read back. Without a model (the gates) it is only shown.</summary>
    private void ValueFromPicked(DwRowDef def, string choice)
    {
        if (_schedule == null || def.Column < 0) return;
        if (_model == null)
        {
            _schedule.ValueFrom[def.Column] = choice;
            UpdateRows();
            return;
        }
        long fieldId = _schedule.FieldIds[def.Column];
        DoorWindowPreset p = Capture();
        Ask("Saving where the row's value comes from…", api => api.SetValueFrom(p, fieldId, choice), s =>
        {
            Say($"'{def.Label}' now shows {DwValueFrom.Show(choice)} (saved in the model).");
            SetSchedule(s);
        });
    }

    private void UpdateItems()
    {
        DoorWindowPreset p = Capture();
        _items.Rows.Clear();
        foreach (DwItemRow r in Included(p))
            _items.Rows.Add(r.Mark, r.Size, r.Qty, string.Join("; ", new[]
            {
                r.AdjustedWidthMm > 0 ? $"{r.AdjustedWidthMm:0} mm (set by hand)" : "",
                r.MovedDims > 0 ? $"{r.MovedDims} dimension(s) moved" : ""
            }.Where(x => x.Length > 0)), r.Types, r.Warnings);
        int adjusted = Included(p).Sum(r => (r.AdjustedWidthMm > 0 ? 1 : 0) + r.MovedDims);
        _resetWidths.Text = $"Reset hand edits ({adjusted})";
        _resetWidths.Enabled = _model != null && adjusted > 0;
    }

    private static string RowValue(DwItemRow i, DwRowDef r, DoorWindowPreset p, DwScheduleData? s)
    {
        string v = r.Kind switch
        {
            DwRowKind.Size when s != null && r.Column >= 0 => i.Size == "?" ? "" : DoorWindowItems.SizeCell(s.Cell(i.Mark, r.Column), i.InstanceSized, i.Size),
            DwRowKind.Qty when s != null && r.Column >= 0 => s.Cell(i.Mark, r.Column),
            DwRowKind.Size => i.Size,
            DwRowKind.Qty => i.Qty.ToString(),
            _ => s == null ? "" : DoorWindowItems.CellValue(s, i.Mark, r, p)
        };
        return string.IsNullOrWhiteSpace(v) ? p.Blank : v;
    }

    private void ApplyNow() => Ask("Bringing every door & window set up to date…", api => api.ApplyNow(), Say);

    private void LiveChanged()
    {
        if (_applying || _model == null) return;
        bool on = _live.Checked;
        DoorWindowPreset p = Capture();
        Ask("", api => api.SetLive(p, on), done => Say(done
            ? $"Live is {(on ? "on" : "off")} for '{p.Name}' in this model."
            : $"Live {(on ? "on" : "off")}: saved with the legends when you build."));
    }

    /// <summary>Build legends: runs beside Revit and the window stays open.</summary>
    protected override void OnRun()
    {
        if (_model == null) { base.OnRun(); return; }
        if (!TryAccept(out string? why)) { MessageBox.Show(this, why, Text); return; }
        DoorWindowPreset p = Result;
        DoorWindowStores.Store.SaveLastUsed(p);
        Ask("Building the legends…", api => api.Build(p), said =>
        {
            Say(said);
            Refresh(open: false);
        });
    }

    // ------------------------------------------------------------------ sheets

    /// <summary>
    /// Packs the items for the chosen title block exactly as the builder will (the same
    /// <see cref="DoorWindowLayout.Pack"/>, spans from width and plan width, tables from the
    /// schedule's cells) and shows one sheet of it. Only once the Sheets tab has shown: the title
    /// block and the plan widths are read from the model then, not when the window opens.
    /// </summary>
    private void UpdateSheets()
    {
        if (_tabs == null) return;
        _sheetsSeen |= _tabs.SelectedTab == _sheetsPage;
        if (!_sheetsSeen) return;
        DoorWindowPreset p = Capture();
        DwTitleBlockData? tb = p.TitleBlockName.Length > 0 ? TitleBlockData(p.TitleBlockName) : null;
        if (p.TitleBlockName.Length > 0 && tb == null) { _sheetSummary.Text = Reading; return; }
        _columns.Enabled = _rowsPerLegend.Enabled = tb == null;
        ReadPlanWidths();
        ReadSheetChoices();
        List<DwItemRow> included = Included(p);
        DwScheduleData? s = _schedule;
        List<DwRowDef> defs = DoorWindowItems.Rows(p, s);
        int columns = p.Columns;
        double maxHeight = 0;
        if (tb != null) (columns, maxHeight) = DwSheets.Grid(tb.Frame, p);
        double k = Math.Max(1, p.Scale);
        var needs = included.Select(i =>
        {
            double need = DoorWindowLayout.NeedWidthMm(i.WidthMm / k, i.PlanWidthMm / k, i.ElevationWidthMm / k, p);
            double width = DoorWindowLayout.CellWidthMm(need, i.AdjustedWidthMm > 0 ? i.AdjustedWidthMm : null, p, columns, out _);
            double valueMm = DoorWindowLayout.ValueWidthMm(width, p);
            return new DwNeed(
                Math.Max(1, (int)Math.Round(width / Math.Max(1, p.CellWidthMm))),
                DoorWindowLayout.ElevationNeedMm(Math.Max(i.HeightSillMm, i.ElevationHeightMm) / k, p),
                defs.Sum(r => DwWrap.Lines(RowValue(i, r, p, s), p.RowFont, p.RowTextMm, valueMm, _textFit)) * p.RowLineMm,
                width);
        }).ToList();
        DwPack pack = DoorWindowLayout.Pack(needs, p, columns, maxHeight);
        _sheetShown.Maximum = Math.Max(1, pack.Sheets);
        int sheet = (int)_sheetShown.Value - 1;
        _sheetSummary.Text = included.Count == 0 ? "Nothing to schedule."
            : DoorWindowLayout.Summary(included.Count, pack.ItemsPerSheet()) + (tb != null ? $", {columns} cells across" : " (legends only)");
        _sheetPreview.Set(tb, p, pack, included.Select(i => i.Mark).ToList(), sheet);
    }

    /// <summary>A parameter as the grid lists it: "* Name" for one the browser organization groups by.</summary>
    private static string ParamShown(DwSheetParameterChoice c) => (c.Organization ? "* " : "") + c.Name;

    private static string ParamName(string? shown) => (shown ?? "").StartsWith("* ") ? shown!.Substring(2) : shown ?? "";

    /// <summary>The grid's rows from the preset, its choices from the model's sheet parameters
    /// (once read): the organization's listed first, and a preset with none starts with them.</summary>
    private void FillSheetParameters(List<DwSheetParameter> rows)
    {
        bool was = _applying;
        _applying = true;
        try
        {
            _sheetParamName.Items.Clear();
            var choices = _sheetChoices ?? new List<DwSheetParameterChoice>();
            foreach (DwSheetParameterChoice c in choices) _sheetParamName.Items.Add(ParamShown(c));
            // A name the model lacks is still listed, so the preset's row shows (the build reports it).
            foreach (DwSheetParameter r in rows.Where(r => choices.All(c => c.Name != r.Name))) _sheetParamName.Items.Add(r.Name);
            _sheetParams.Rows.Clear();
            var show = rows.Count > 0 ? rows
                : choices.Where(c => c.Organization).Select(c => new DwSheetParameter { Name = c.Name, Value = c.Values.FirstOrDefault() ?? "" }).ToList();
            foreach (DwSheetParameter r in show)
                _sheetParams.Rows.Add(choices.FirstOrDefault(c => c.Name == r.Name) is { } c ? ParamShown(c) : r.Name, r.Value);
        }
        finally { _applying = was; }
    }

    private void ReadSheetChoices()
    {
        if (_sheetChoicesAsked || _model == null) return;
        _sheetChoicesAsked = true;
        Ask("", api => api.SheetParameters(), choices =>
        {
            List<DwSheetParameter> now = Capture().SheetParameters;
            _sheetChoices = choices;
            FillSheetParameters(now);
        });
    }

    /// <summary>The Sheets tab's parameter rows as "name = value" (the self-test reads them).</summary>
    internal List<string> SheetParameterRows => Capture().SheetParameters.Select(x => x.Name + " = " + x.Value).ToList();

    /// <summary>Stands in for the model's sheet parameters (the self-test).</summary>
    internal void SetSheetChoices(List<DwSheetParameterChoice> choices)
    {
        _sheetChoicesAsked = true;
        List<DwSheetParameter> now = Capture().SheetParameters;
        _sheetChoices = choices;
        FillSheetParameters(now);
    }

    private void ResetWidths()
    {
        DoorWindowPreset p = Capture();
        Ask("Resetting the hand edits…", api => api.ResetWidths(p), said =>
        {
            Say(said);
            Refresh(open: false);
        });
    }

    /// <summary>Brings the Sheets tab forward (the self-test, which reads its summary).</summary>
    internal void ShowSheets()
    {
        // A window not yet on screen raises no SelectedIndexChanged, so say it here.
        _tabs.SelectedTab = _sheetsPage;
        _sheetsSeen = true;
        UpdateSheets();
    }

    /// <summary>The Sheets tab has been shown once: from then on it follows every change.</summary>
    private bool _sheetsSeen;

    /// <summary>The Sheets tab's summary line (the self-test reads it).</summary>
    internal string SheetSummaryText => _sheetSummary.Text;

    /// <summary>The Fields tab's rows as label | from | shows | value lines (the self-test reads them).</summary>
    internal List<string> RowsPreview => _rowsPreview.Rows.Cast<DataGridViewRow>()
        .Select(r => string.Join(" | ", r.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString() ?? ""))).ToList();

    /// <summary>A row's "Value from" choices as listed (the self-test reads them).</summary>
    internal List<string> ValueFromChoices(string heading) =>
        _rowsPreview.Rows.Cast<DataGridViewRow>().First(r => (r.Cells[0].Value?.ToString() ?? "") == heading).Cells[_shows.Index] is DataGridViewComboBoxCell c
            ? c.Items.Cast<object>().Select(o => o.ToString() ?? "").ToList() : new List<string>();

    /// <summary>Picks a row's "Value from", as the user would (the self-test).</summary>
    internal void PickValueFrom(string heading, string shown)
    {
        DataGridViewRow row = _rowsPreview.Rows.Cast<DataGridViewRow>().First(r => (r.Cells[0].Value?.ToString() ?? "") == heading);
        row.Cells[_shows.Index].Value = shown;
    }

    /// <summary>The summary label shows whole inside its group (the self-test reads it).</summary>
    internal bool SheetSummaryFits()
    {
        GroupBox? g = null;
        for (Control? c = _sheetSummary.Parent; c != null && g == null; c = c.Parent) g = c as GroupBox;
        if (g == null) return false;
        Rectangle r = g.RectangleToClient(_sheetSummary.RectangleToScreen(_sheetSummary.ClientRectangle));
        return r.Bottom <= g.ClientSize.Height && r.Right <= g.ClientSize.Width && _sheetSummary.PreferredSize.Height <= _sheetSummary.Height;
    }

    /// <summary>Marks a part as being edited (the self-test's screenshot of the highlight).</summary>
    internal void ShowPart(DoorWindowDiagram.Part part)
    {
        _activePart = part;
        UpdateDiagram();
    }

    // ------------------------------------------------------------------ preset

    private DoorWindowPreset Capture()
    {
        string description = _description != null ? DrwControls.Text(_description) : "";
        return new DoorWindowPreset
        {
            Name = _bar?.SelectedName is { Length: > 0 } n ? n : "Default",
            Category = _windows.Checked ? DwCategory.Window : DwCategory.Door,
            IncludeUnmarked = _unmarked.Checked,
            Live = _live.Checked,
            ScheduleName = _schedule?.Name ?? "",
            DescriptionHeading = description == None ? "" : description,
            SizeRow = _sizeRow.Checked,
            SizeLabel = _sizeLabel.Text.Trim(),
            QtyRow = _qtyRow.Checked,
            QtyLabel = _qtyLabel.Text.Trim(),
            ElevationTitle = _elevTitle.Text.Trim().Length > 0 ? _elevTitle.Text.Trim() : "Elevation View",
            PlanTitle = _planTitle.Text.Trim().Length > 0 ? _planTitle.Text.Trim() : "Plan View",
            Blank = _blank.Text,
            Scale = (int)_scale.Value,
            CellWidthMm = (double)_cellWidth.Value,
            ElevationBoxMm = (double)_elevBox.Value,
            PlanBoxMm = (double)_planBox.Value,
            HeaderMm = (double)_header.Value,
            RowLineMm = (double)_rowLine.Value,
            LabelWidthMm = (double)_labelWidth.Value,
            TextInsetMm = (double)_inset.Value,
            WallStubMm = (double)_wallStub.Value,
            Columns = (int)_columns.Value,
            RowsPerLegend = (int)_rowsPerLegend.Value,
            FloorGapMm = (double)_floorGap.Value,
            DimOffsetMm = (double)_dimOffset.Value,
            FloorLabel = _floorLabel?.Text.Trim() ?? "FFL.",
            ElevationFirst = _elevationFirst.Checked,
            DimensionTypeName = DrwControls.Text(_dimType) is var t && t != DrwLookup.None ? t : "",
            LegendName = _legendName.Text.Trim().Length > 0 ? _legendName.Text.Trim() : (_windows.Checked ? "Window Schedule {n}" : "Door Schedule {n}"),
            TitleBlockName = _titleBlock != null && DrwControls.Text(_titleBlock) is var tb && tb != NoTitleBlock ? tb : "",
            SheetNumber = _sheetNumber != null && _sheetNumber.Text.Trim().Length > 0 ? _sheetNumber.Text.Trim() : "A-12",
            SheetName = _sheetName?.Text.Trim() ?? "",
            SheetMarginMm = _margin != null ? (double)_margin.Value : 5,
            SheetParameters = _sheetParams == null ? new List<DwSheetParameter>() : _sheetParams.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
                .Select(r => new DwSheetParameter { Name = ParamName(r.Cells[0].Value?.ToString()), Value = r.Cells[1].Value?.ToString() ?? "" })
                .Where(x => x.Name.Length > 0).ToList()
        };
    }

    private void Apply(DoorWindowPreset p)
    {
        _applying = true;
        try
        {
            (p.Category == DwCategory.Window ? _windows : _doors).Checked = true;
            _unmarked.Checked = p.IncludeUnmarked;
            _live.Checked = p.Live;
            _sizeRow.Checked = p.SizeRow;
            _sizeLabel.Text = p.SizeLabel ?? "";
            _qtyRow.Checked = p.QtyRow;
            _qtyLabel.Text = p.QtyLabel ?? "";
            _elevTitle.Text = p.ElevationTitle ?? "Elevation View";
            _planTitle.Text = p.PlanTitle ?? "Plan View";
            _blank.Text = p.Blank ?? "-";
            _scale.Value = DrwControls.Clamp(_scale, p.Scale);
            _cellWidth.Value = DrwControls.Clamp(_cellWidth, p.CellWidthMm);
            _elevBox.Value = DrwControls.Clamp(_elevBox, p.ElevationBoxMm);
            _planBox.Value = DrwControls.Clamp(_planBox, p.PlanBoxMm);
            _header.Value = DrwControls.Clamp(_header, p.HeaderMm);
            _rowLine.Value = DrwControls.Clamp(_rowLine, p.RowLineMm);
            _labelWidth.Value = DrwControls.Clamp(_labelWidth, p.LabelWidthMm);
            _inset.Value = DrwControls.Clamp(_inset, p.TextInsetMm);
            _wallStub.Value = DrwControls.Clamp(_wallStub, p.WallStubMm);
            _columns.Value = DrwControls.Clamp(_columns, p.Columns);
            _rowsPerLegend.Value = DrwControls.Clamp(_rowsPerLegend, p.RowsPerLegend);
            _floorGap.Value = DrwControls.Clamp(_floorGap, p.FloorGapMm);
            _dimOffset.Value = DrwControls.Clamp(_dimOffset, p.DimOffsetMm);
            _floorLabel.Text = p.FloorLabel ?? "";
            _elevationFirst.Checked = p.ElevationFirst;
            DrwControls.Select(_dimType, p.DimensionTypeName ?? "");
            _legendName.Text = p.LegendName ?? "";
            _titleBlock.SelectedItem = p.TitleBlockName is { Length: > 0 } t && _titleBlock.Items.Contains(t) ? t : NoTitleBlock;
            _sheetNumber.Text = p.SheetNumber ?? "A-12";
            _sheetName.Text = p.SheetName ?? "";
            _margin.Value = DrwControls.Clamp(_margin, p.SheetMarginMm);
            FillSheetParameters(p.SheetParameters);
            _description.Items.Clear();
            _description.Items.Add(None);
            if (p.DescriptionHeading.Length > 0) _description.Items.Add(p.DescriptionHeading);
            DrwControls.Select(_description, p.DescriptionHeading.Length > 0 ? p.DescriptionHeading : None);
            if (_model == null) _schedule = _data.Schedules.FirstOrDefault(s => s.IsDoor == (p.Category == DwCategory.Door)) ?? _schedule;
            SetSchedule(_schedule);
            UpdateItems();
        }
        finally { _applying = false; }
        // Another preset is another set: its own schedule, made if missing and opened.
        if (_bar != null) Refresh(open: true);
    }

    protected override bool TryAccept(out string? why)
    {
        Result = Capture();
        why = !Result.LegendName.Contains("{n}") ? "The legend name needs {n} so each legend gets its own number." : null;
        return why == null;
    }
}
