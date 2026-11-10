using System.Windows.Forms;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.Drw.Enlarge;
using KhBatchRunner.UI;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>What the Enlarge dialog lists, read from the model before it opens.</summary>
internal sealed class EnlargeDialogData
{
    public bool IsStair { get; init; }
    public List<string> PlanTypes { get; init; } = new();
    public List<string> SectionTypes { get; init; } = new();
    public List<string> ElevationTypes { get; init; } = new();
    public List<string> PlanTemplates { get; init; } = new();
    public List<string> SectionTemplates { get; init; } = new();
    public List<string> ElevationTemplates { get; init; } = new();
    public List<string> Targets { get; init; } = new();
    /// <summary>Ticked when the dialog opens: the rooms/stairs selected (or picked) in the model.</summary>
    public HashSet<int> Preselected { get; init; } = new();
    /// <summary>How many of <see cref="Preselected"/> came from the model selection or a pick.</summary>
    public int FromSelection { get; init; }
    /// <summary>The project's view parameters, for the Parameters tab's dropdown.</summary>
    public List<ParamChoice> ViewParameters { get; init; } = new();
    /// <summary>Templates that control View Scale, keyed "Plan|name" / "Section|name" /
    /// "Elevation|name", with the scale they impose.</summary>
    public Dictionary<string, int> TemplateScales { get; init; } = new();
}

/// <summary>One dropdown entry: what it shows, and the name/GUID a preset row stores.</summary>
internal sealed record ParamChoice(string Label, string Name, string Guid);

/// <summary>
/// Enlarge Room / Enlarge Stair: pick the rooms or stairs, then how their views are made. Four
/// tabs — Targets, Views, Naming &amp; templates, Parameters — with a preset bar above them.
/// </summary>
internal sealed class EnlargeWindow : Form
{
    private readonly EnlargeDialogData _data;

    // targets
    private readonly TextBox _filter;
    private readonly CheckedListBox _targets;
    private readonly List<int> _shown = new();
    private readonly HashSet<int> _checked;
    private readonly TextBox _batchFilter;

    // plan
    private readonly CheckBox _createPlan;
    private readonly ComboBox _planType;
    private readonly NumericUpDown _planMargin, _scale;

    // vertical views
    private readonly CheckBox _createVertical, _north, _east, _south, _west, _orient, _innerLoops;
    private readonly RadioButton _section, _elevation, _byBoundary, _byCenter;
    private readonly ComboBox _sectionType, _elevationType;
    private readonly NumericUpDown _sectionOffset, _sectionCentreOffset, _sectionCrop, _minLength, _inset;
    private readonly RadioButton _fromEdge, _fromCentre;
    private readonly SectionPlacementDiagram _diagram;
    private readonly NumericUpDown _sectionBelow, _sectionAbove, _beforeFirst, _afterLast;
    private readonly ComboBox _topFrom;
    private readonly SectionHeightDiagram _heightDiagram;
    private readonly Label? _templateNote;

    // naming & templates
    private readonly TextBox _prefix, _planPrefix, _sectionPrefix, _elevPrefix;
    private readonly ComboBox _roomLabel, _delimiter;
    private readonly CheckBox _overrideMarks, _skipExisting;
    private readonly ComboBox _planTemplate, _sectionTemplate, _elevTemplate;

    // parameters
    private readonly DataGridView _params;
    private readonly DataGridViewComboBoxColumn _paramColumn;
    private readonly Dictionary<string, ParamChoice> _choices = new(StringComparer.Ordinal);

    private readonly DrwPresetBar<EnlargePreset> _bar;

    // Set while the list is rebuilt: adding an item already ticked raises ItemCheck, and that
    // must not be read as the user ticking it (nor run before the window has a handle).
    private bool _refilling;

    public EnlargePreset Result { get; private set; } = new();
    public IReadOnlyList<int> SelectedTargets => _checked.OrderBy(i => i).ToList();

    /// <summary>
    /// True when the dialog closed so the user can pick rooms/stairs in the model. The command
    /// picks, then reopens the dialog with <see cref="Snapshot"/> and the ticks carried over.
    /// </summary>
    public bool PickRequested { get; private set; }

    /// <summary>What the dialog held when it closed (for reopening after a pick).</summary>
    public EnlargePreset Snapshot { get; private set; } = new();

    public EnlargeWindow(EnlargeDialogData data, EnlargePreset initial, DrwPresetStore<EnlargePreset> store)
    {
        _data = data;
        _checked = new HashSet<int>(data.Preselected);
        string what = data.IsStair ? "stairs" : "rooms";
        Text = data.IsStair ? "Enlarge Stair" : "Enlarge Room";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(760, 640);
        // 720: at 640 the "Measure the top from" list beside the sample picture shrank to 54 px
        // (self-test layout check, 0.2.7).
        MinimumSize = new Size(720, 560);

        // ---------------- Targets tab
        _filter = new TextBox();
        _filter.TextChanged += (_, _) => RefillTargets();
        _targets = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, Dock = DockStyle.Fill };
        _targets.ItemCheck += (_, e) =>
        {
            if (_refilling) return;
            int idx = _shown[e.Index];
            if (e.NewValue == CheckState.Checked) _checked.Add(idx); else _checked.Remove(idx);
            // ItemCheck fires BEFORE the tick lands; the count is refreshed once it has. Before the
            // window has a handle there is no message loop to defer to — BeginInvoke would throw,
            // which is what stopped the dialog opening when rooms were selected first.
            if (IsHandleCreated) BeginInvoke(new Action(UpdateStatus)); else UpdateStatus();
        };
        var checkRow = FormLayout.FlowRow(
            DrwControls.Button("Check shown", 0, () => SetShown(true)),
            DrwControls.Button("Uncheck shown", 0, () => SetShown(false)),
            DrwControls.Button("Uncheck all", 0, () => { _checked.Clear(); RefillTargets(); }),
            DrwControls.Button($"Pick {what} in model…", 0, () =>
            {
                _params.EndEdit();
                Snapshot = Capture();
                PickRequested = true;
                DialogResult = DialogResult.Retry;
            }));
        _batchFilter = new TextBox();

        var targetsPage = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(targetsPage, FormLayout.LabeledRow("Filter:", _filter));
        FormLayout.AddFillRow(targetsPage, _targets, 100);
        FormLayout.AddAutoRow(targetsPage, checkRow);
        FormLayout.AddAutoRow(targetsPage, FormLayout.LabeledRow("Batch Run takes " + what + " whose name contains:", _batchFilter));
        FormLayout.AddAutoRow(targetsPage, FormLayout.NoteLabel(this,
            "The ticks are for this run only. Batch Run has nobody to tick anything, so it takes all the " +
            what + " matching the text above (blank = all) with the preset as saved."));

        // ---------------- Views tab
        _createPlan = DrwControls.Check("Create enlarged plan" + (data.IsStair ? "s (one per level the stair crosses)" : ""), true);
        _planType = DrwControls.Combo(data.PlanTypes);
        _planMargin = DrwControls.Number(0.5, 0, 50, 2, 0.1);
        _scale = DrwControls.Number(50, 1, 5000, 0, 5);

        _createVertical = DrwControls.Check(data.IsStair ? "Create sections" : "Create sections or elevations", true);
        _section = new RadioButton { Text = "Section", AutoSize = true, Checked = true };
        _elevation = new RadioButton { Text = "Elevation", AutoSize = true };
        _north = DrwControls.Check("N", true);
        _east = DrwControls.Check("E", true);
        _south = DrwControls.Check("S", false);
        _west = DrwControls.Check("W", false);
        _orient = DrwControls.Check("Turn N/E/S/W to the longest boundary edge", false);
        _sectionType = DrwControls.Combo(data.SectionTypes);
        _fromEdge = new RadioButton { Text = "From the edge, inward", AutoSize = true, Checked = true };
        _fromCentre = new RadioButton { Text = "From the centre", AutoSize = true };
        _sectionOffset = DrwControls.Number(0.5, 0, 50, 2, 0.1);
        _sectionCentreOffset = DrwControls.Number(0, -50, 50, 2, 0.1);
        _sectionCrop = DrwControls.Number(0.3, 0, 20, 2, 0.1);
        _diagram = new SectionPlacementDiagram { TargetWord = data.IsStair ? "stair" : "room" };
        _sectionBelow = DrwControls.Number(0.3, 0, 50, 2, 0.1);
        _sectionAbove = DrwControls.Number(0.3, 0, 50, 2, 0.1);
        _topFrom = DrwControls.Combo(new[] { data.IsStair ? "Last step" : "Top of room", "Next level above" });
        _heightDiagram = new SectionHeightDiagram { TargetWord = data.IsStair ? "stair" : "room" };
        _beforeFirst = DrwControls.Number(0, 0, 50, 2, 0.1);
        _afterLast = DrwControls.Number(0, 0, 50, 2, 0.1);
        _elevationType = DrwControls.Combo(data.ElevationTypes);
        _byBoundary = new RadioButton { Text = "By each boundary edge", AutoSize = true, Checked = true };
        _byCenter = new RadioButton { Text = "By centre, 4 sides (ticked sides)", AutoSize = true };
        _minLength = DrwControls.Number(1.0, 0, 100, 2, 0.1);
        _innerLoops = DrwControls.Check("Include inner loops (columns, holes)", false);
        _inset = DrwControls.Number(0.5, 0.05, 20, 2, 0.1);

        foreach (Control c in new Control[] { _createPlan, _createVertical, _section, _elevation, _byBoundary, _byCenter, _fromEdge, _fromCentre })
            switch (c)
            {
                case CheckBox cb: cb.CheckedChanged += (_, _) => UpdateEnabled(); break;
                case RadioButton rb: rb.CheckedChanged += (_, _) => UpdateEnabled(); break;
            }
        foreach (NumericUpDown n in new[] { _sectionOffset, _sectionCentreOffset, _sectionCrop, _sectionBelow, _sectionAbove })
            n.ValueChanged += (_, _) => UpdateDiagram();
        _topFrom.SelectedIndexChanged += (_, _) => UpdateDiagram();

        var planGroup = DrwControls.Group("Enlarged plan",
            _createPlan,
            FormLayout.LabeledRow("Plan type:", _planType),
            FormLayout.LabeledRow("Crop margin (m):", _planMargin),
            FormLayout.LabeledRow("Scale  1 :", _scale),
            FormLayout.NoteLabel(this, "Each view is cropped to the " + (data.IsStair ? "stair" : "room") +
                " plus the margin."));
        var sideRow = FormLayout.FlowRow(new Label { Text = "Looking:", AutoSize = true, Margin = new Padding(3, 6, 6, 3) },
            _north, _east, _south, _west);
        string target = data.IsStair ? "stair" : "room";
        // Stairs only ever get sections, so the stair dialog has no Section/Elevation choice.
        var vertRows = new List<Control> { _createVertical };
        if (!data.IsStair) vertRows.Add(FormLayout.FlowRow(_section, _elevation));
        vertRows.Add(sideRow);
        vertRows.Add(_orient);
        vertRows.Add(FormLayout.NoteLabel(this, "N is up the plan (+Y). Each ticked side gets a section looking " +
            "that way across the " + target + (data.IsStair ? "." : "; a centre elevation marker shows the ticked sides.")));
        var vertGroup = DrwControls.Group(data.IsStair ? "Sections" : "Sections / elevations", vertRows.ToArray());

        // Section: fields on the right, a live sample on the left — the layout of Revit's View Range.
        GroupBox sectionGroup = SampleGroup("Section", "Sample section placement", _diagram, out TableLayoutPanel sectionFields,
            FormLayout.LabeledRow("Section type:", _sectionType),
            // One per line (same parent, so they still exclude each other): side by side they ran past
            // the fields column beside the sample picture at the minimum width.
            _fromEdge, _fromCentre,
            FormLayout.LabeledRow("Line inset from edge (m):", _sectionOffset),
            FormLayout.LabeledRow("Offset from centre (m):", _sectionCentreOffset),
            FieldNote("From the edge: the line stands the inset inside the side behind it, looking across. " +
                      "From the centre: + moves the line back from the centre, − forward."));

        // Crop and height: the side and far crop, and a pad below and above with the top measured from
        // the target or the level above. Sections AND elevations use them (elevations since 0.2.7).
        GroupBox heightGroup = SampleGroup(data.IsStair ? "Section crop and height" : "Crop and height (sections and elevations)",
            "Sample height", _heightDiagram, out TableLayoutPanel heightFields,
            FormLayout.LabeledRow("Side and far crop (m):", _sectionCrop),
            FormLayout.LabeledRow("Below the bottom (m):", _sectionBelow),
            FormLayout.LabeledRow("Measure the top from:", _topFrom),
            FormLayout.LabeledRow("Above it (m):", _sectionAbove),
            FieldNote("The side and far crop reaches that far past the " + (data.IsStair ? "stair" : "outline") +
                      " and past what the view looks at. Next level above: the first level above the " +
                      (data.IsStair ? "stair's top level (a stair ending at L2 under a Roof level uses Roof)"
                                    : "room's level") +
                      ". If there is none, the " + (data.IsStair ? "last step" : "top of the room") + " is used."));

        // Stairs: room in front of the first step and past the last one, for plans and sections alike.
        var boundaryGroup = DrwControls.Group("Stair boundary",
            FormLayout.LabeledRow("Beyond the first step (m):", _beforeFirst),
            FormLayout.LabeledRow("Beyond the last step (m):", _afterLast),
            FormLayout.NoteLabel(this, "Stretches the stair's outline in front of the first riser and past the " +
                "last riser, keeping the run's width. Plans and sections both use it; 0 = the stair itself."));
        var elevGroup = DrwControls.Group("Elevation",
            FormLayout.LabeledRow("Elevation type:", _elevationType),
            FormLayout.FlowRow(_byBoundary, _byCenter),
            FormLayout.LabeledRow("Minimum edge length (m):", _minLength),
            _innerLoops,
            FormLayout.LabeledRow("Marker inset from edge (m):", _inset),
            FormLayout.NoteLabel(this, "Elevations take their crop and height from \"Crop and height\" above."));
        var viewsPage = data.IsStair
            ? DrwControls.Page(planGroup, boundaryGroup, vertGroup, sectionGroup, heightGroup)
            : DrwControls.Page(planGroup, vertGroup, sectionGroup, heightGroup, elevGroup);

        // ---------------- Naming & templates tab
        _prefix = new TextBox { Text = "Enlarge" };
        _planPrefix = new TextBox { Text = "Plan" };
        _sectionPrefix = new TextBox { Text = "Section" };
        _elevPrefix = new TextBox { Text = "Elevation" };
        _roomLabel = DrwControls.Combo(new[] { "Name", "Number", "Number Name" });
        _delimiter = DrwControls.Combo(new[] { "-", ".", "_" });
        _overrideMarks = DrwControls.Check("Renumber multistory stair members' Mark (ST-1 → ST-1-1, ST-1-2…)", true);
        _skipExisting = DrwControls.Check("Skip a view whose name already exists (safe to re-run)", true);
        _planTemplate = DrwControls.Combo(data.PlanTemplates, withNone: true);
        _sectionTemplate = DrwControls.Combo(data.SectionTemplates, withNone: true);
        _elevTemplate = DrwControls.Combo(data.ElevationTemplates, withNone: true);

        var nameRows = new List<Control>
        {
            FormLayout.LabeledRow("Prefix (every view):", _prefix),
            FormLayout.LabeledRow("Plan prefix:", _planPrefix),
            FormLayout.LabeledRow("Section prefix:", _sectionPrefix),
            FormLayout.LabeledRow("Elevation prefix:", _elevPrefix)
        };
        if (data.IsStair)
        {
            nameRows.Add(FormLayout.LabeledRow("Member mark delimiter:", _delimiter));
            nameRows.Add(_overrideMarks);
            nameRows.Add(FormLayout.NoteLabel(this,
                "Views are named from the stair's Mark, plans end with their level: " +
                "\"Enlarge Plan - ST-1 - Level 2\". A stair with no Mark is ST-1, ST-2… after the highest in the model."));
        }
        else
        {
            nameRows.Add(FormLayout.LabeledRow("Room label:", _roomLabel));
            nameRows.Add(FormLayout.NoteLabel(this,
                "e.g. \"Enlarge Plan - Kitchen\", \"Enlarge Section - Kitchen - N\". A room with no name is RM-1, RM-2…"));
        }
        nameRows.Add(_skipExisting);
        // Says when a chosen template imposes its own scale, so the Scale field will not apply.
        _templateNote = FormLayout.NoteLabel(this, "");
        var templateRows = new List<Control>
        {
            FormLayout.LabeledRow("Plan:", _planTemplate),
            FormLayout.LabeledRow("Section:", _sectionTemplate)
        };
        if (!data.IsStair) templateRows.Add(FormLayout.LabeledRow("Elevation:", _elevTemplate));   // stairs: sections only
        templateRows.Add(_templateNote);
        foreach (ComboBox c in new[] { _planTemplate, _sectionTemplate, _elevTemplate })
            c.SelectedIndexChanged += (_, _) => UpdateTemplateNote();
        _scale.ValueChanged += (_, _) => UpdateTemplateNote();
        var namingPage = DrwControls.Page(
            DrwControls.Group("Naming", nameRows.ToArray()),
            DrwControls.Group("View templates", templateRows.ToArray()));

        // ---------------- Parameters tab
        _params = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersWidth = 28
        };
        foreach (ParamChoice c in data.ViewParameters) _choices[c.Label] = c;
        _paramColumn = new DataGridViewComboBoxColumn
        {
            Name = "Name", HeaderText = "Parameter", FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton, FillWeight = 60
        };
        _paramColumn.Items.AddRange(_choices.Keys.Cast<object>().ToArray());
        _params.Columns.Add(_paramColumn);
        _params.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", FillWeight = 40 });
        // A value no longer in the list must never throw a dialog at the user; it is re-added on load.
        _params.DataError += (_, e) => e.ThrowException = false;
        // Each parameter once: as a row's dropdown opens, it lists only what no OTHER row has taken
        // (plus its own current value). A cell can carry its own list, apart from the column's.
        _params.CellBeginEdit += (_, e) =>
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0) return;
            if (_params.Rows[e.RowIndex].Cells[0] is not DataGridViewComboBoxCell cell) return;
            string own = cell.Value?.ToString() ?? "";
            var taken = new HashSet<string>(_params.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Index != e.RowIndex && !r.IsNewRow)
                .Select(r => r.Cells[0].Value?.ToString() ?? "")
                .Where(v => v.Length > 0 && v != own), StringComparer.Ordinal);
            cell.Items.Clear();
            foreach (string label in _choices.Keys.Where(k => !taken.Contains(k))) cell.Items.Add(label);
        };
        var paramsPage = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(paramsPage, FormLayout.NoteLabel(this,
            "Written to every view created. The list is this project's parameters that apply to views " +
            "(project and shared) — nothing is created. When two parameters share a name, each is shown " +
            "with its kind, type and GUID so you pick the right one; the choice is saved by GUID, not name. " +
            "Values: text, numbers in project units, or yes/no (1/0) for Yes/No parameters." +
            (data.ViewParameters.Count == 0 ? "\n\nThis project has no parameters bound to views." : "")));
        FormLayout.AddFillRow(paramsPage, _params, 100);

        // ---------------- assemble
        var tabs = new TabControl { Dock = DockStyle.Fill };
        foreach ((string title, Control page) in new (string, Control)[]
                 {
                     (data.IsStair ? "Stairs" : "Rooms", targetsPage), ("Views", viewsPage),
                     ("Naming & templates", namingPage), ("Parameters", paramsPage)
                 })
        {
            var tp = new TabPage(title) { Padding = new Padding(4) };
            page.Dock = DockStyle.Fill;
            tp.Controls.Add(page);
            tabs.TabPages.Add(tp);
        }

        _status = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        var run = DrwControls.Button("Create views", 110, Accept);
        var cancel = DrwControls.Button("Cancel", 90);
        cancel.DialogResult = DialogResult.Cancel;
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(run, 1, 0);
        bottom.Controls.Add(cancel, 2, 0);

        _bar = new DrwPresetBar<EnlargePreset>(this, store, Capture, Apply, initial.Name);

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, _bar.Row);
        FormLayout.AddFillRow(root, tabs, 100);
        FormLayout.AddAutoRow(root, bottom);
        Controls.Add(root);
        AcceptButton = run;
        CancelButton = cancel;

        Apply(initial);
        RefillTargets();
        FormLayout.AlignLabels(this, _bar.Row, targetsPage, planGroup, boundaryGroup, vertGroup, sectionFields,
            heightFields, elevGroup, namingPage);
        DrwControls.FitNotes(this);
        Theme.Apply(this);
        _templateNote.ForeColor = Theme.Dim(_templateNote);
        UpdateTemplateNote();
    }

    private readonly Label _status;

    // ------------------------------------------------------------------ targets

    private void RefillTargets()
    {
        string f = _filter.Text.Trim();
        _targets.BeginUpdate();
        _refilling = true;
        try
        {
            _targets.Items.Clear();
            _shown.Clear();
            for (int i = 0; i < _data.Targets.Count; i++)
            {
                if (f.Length > 0 && _data.Targets[i].IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _shown.Add(i);
                _targets.Items.Add(_data.Targets[i], _checked.Contains(i));
            }
        }
        finally
        {
            _refilling = false;
            _targets.EndUpdate();
        }
        UpdateStatus();
    }

    private void SetShown(bool on)
    {
        foreach (int i in _shown) if (on) _checked.Add(i); else _checked.Remove(i);
        RefillTargets();
    }

    private void UpdateStatus() =>
        _status.Text = $"{_checked.Count} of {_data.Targets.Count} {(_data.IsStair ? "stairs" : "rooms")} ticked" +
                       (_data.FromSelection > 0 ? $"  ({_data.FromSelection} from your selection)" : "");

    private void UpdateEnabled()
    {
        _planType.Enabled = _planMargin.Enabled = _createPlan.Checked;
        bool v = _createVertical.Checked;
        _section.Enabled = _elevation.Enabled = _orient.Enabled = v;
        bool sec = v && (_data.IsStair || _section.Checked), elev = v && !_data.IsStair && _elevation.Checked;
        _sectionType.Enabled = _fromEdge.Enabled = _fromCentre.Enabled = sec;
        // Crop and height serve sections and elevations alike.
        _sectionCrop.Enabled = _sectionBelow.Enabled = _sectionAbove.Enabled = _topFrom.Enabled = sec || elev;
        _sectionOffset.Enabled = sec && _fromEdge.Checked;
        _sectionCentreOffset.Enabled = sec && _fromCentre.Checked;
        _elevationType.Enabled = _byBoundary.Enabled = _byCenter.Enabled = elev;
        _minLength.Enabled = _innerLoops.Enabled = _inset.Enabled = elev && _byBoundary.Checked;
        bool sides = sec || (elev && _byCenter.Checked);
        _north.Enabled = _east.Enabled = _south.Enabled = _west.Enabled = sides;
        UpdateDiagram();
    }

    private void UpdateDiagram()
    {
        _diagram.Set(
            _fromCentre.Checked ? SectionPlacement.FromCentre : SectionPlacement.FromEdge,
            (double)_sectionOffset.Value, (double)_sectionCentreOffset.Value, (double)_sectionCrop.Value);
        _heightDiagram.Set(
            _topFrom.SelectedIndex == 1 ? SectionTopFrom.NextLevel : SectionTopFrom.TargetTop,
            (double)_sectionBelow.Value, (double)_sectionAbove.Value);
    }

    /// <summary>
    /// A group laid out like Revit's View Range dialog: a "Sample …" picture on the left, the
    /// fields on the right, and a button under the fields that hides or shows the picture.
    /// </summary>
    private static GroupBox SampleGroup(string title, string sampleTitle, DrwSampleDiagram diagram,
        out TableLayoutPanel fields, params Control[] rows)
    {
        fields = FormLayout.SingleColumn();
        fields.AutoSize = true;
        fields.Padding = new Padding(0);
        foreach (Control row in rows) FormLayout.AddAutoRow(fields, row);

        var sample = new GroupBox
        {
            Text = sampleTitle, Padding = new Padding(6),
            Size = new Size(diagram.Width + 14, diagram.Height + 26), Margin = new Padding(0, 0, 8, 0)
        };
        diagram.Dock = DockStyle.Fill;
        sample.Controls.Add(diagram);
        Button toggle = DrwControls.Button("Hide sample <<", 0);
        toggle.Click += (_, _) =>
        {
            sample.Visible = !sample.Visible;
            toggle.Text = sample.Visible ? "Hide sample <<" : "Show sample >>";
        };
        FormLayout.AddAutoRow(fields, toggle);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.Controls.Add(sample, 0, 0);
        body.Controls.Add(fields, 1, 0);
        return DrwControls.Group(title, body);
    }

    /// <summary>A wrapping note sized for the fields column beside a sample picture.</summary>
    private static Label FieldNote(string text) => new()
    {
        Text = text, AutoSize = true, UseMnemonic = false,
        MaximumSize = new Size(360, 0), Margin = new Padding(3, 4, 3, 4)
    };

    // ------------------------------------------------------------------ preset <-> controls

    private EnlargePreset Capture()
    {
        var p = new EnlargePreset
        {
            Name = _bar?.SelectedName is { Length: > 0 } n ? n : "Default",
            CreatePlan = _createPlan.Checked,
            PlanTypeName = DrwControls.Text(_planType),
            PlanMarginM = (double)_planMargin.Value,
            Scale = (int)_scale.Value,
            CreateVerticalViews = _createVertical.Checked,
            ViewKind = _data.IsStair || _section.Checked ? EnlargeViewKind.Section : EnlargeViewKind.Elevation,
            North = _north.Checked, East = _east.Checked, South = _south.Checked, West = _west.Checked,
            OrientToLongestEdge = _orient.Checked,
            SectionTypeName = DrwControls.Text(_sectionType),
            SectionPlacement = _fromCentre.Checked ? SectionPlacement.FromCentre : SectionPlacement.FromEdge,
            SectionOffsetM = (double)_sectionOffset.Value,
            SectionCentreOffsetM = (double)_sectionCentreOffset.Value,
            SectionBelowM = (double)_sectionBelow.Value,
            SectionAboveM = (double)_sectionAbove.Value,
            SectionTopFrom = _topFrom.SelectedIndex == 1 ? SectionTopFrom.NextLevel : SectionTopFrom.TargetTop,
            StairBeforeFirstStepM = (double)_beforeFirst.Value,
            StairAfterLastStepM = (double)_afterLast.Value,
            SectionCropOffsetM = (double)_sectionCrop.Value,
            ElevationTypeName = DrwControls.Text(_elevationType),
            ElevationMode = _byCenter.Checked ? ElevationMode.ByCenter4 : ElevationMode.ByBoundary,
            MinBoundaryLengthM = (double)_minLength.Value,
            IncludeInnerLoops = _innerLoops.Checked,
            ElevationInsetM = (double)_inset.Value,
            Prefix = _prefix.Text.Trim(),
            PlanPrefix = _planPrefix.Text.Trim(),
            SectionPrefix = _sectionPrefix.Text.Trim(),
            ElevationPrefix = _elevPrefix.Text.Trim(),
            RoomLabel = _roomLabel.SelectedIndex switch { 1 => RoomLabel.Number, 2 => RoomLabel.NumberName, _ => RoomLabel.Name },
            MarkDelimiter = DrwControls.Text(_delimiter),
            OverrideMultistoryMarks = _overrideMarks.Checked,
            PlanTemplate = TemplateText(_planTemplate),
            SectionTemplate = TemplateText(_sectionTemplate),
            ElevationTemplate = TemplateText(_elevTemplate),
            SkipExisting = _skipExisting.Checked,
            BatchFilter = _batchFilter.Text.Trim()
        };
        // First row per parameter wins: a repeat from an older preset is dropped here rather than
        // written twice. Keyed by GUID for a shared parameter, else by name.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow r in _params.Rows)
        {
            if (r.IsNewRow) continue;
            string label = r.Cells[0].Value?.ToString() ?? "";
            if (!_choices.TryGetValue(label, out ParamChoice? c)) continue;
            if (!seen.Add(c.Guid.Length > 0 ? "guid:" + c.Guid : "name:" + c.Name)) continue;
            p.Parameters.Add(new ParamValue { Name = c.Name, Guid = c.Guid, Value = r.Cells[1].Value?.ToString() ?? "" });
        }
        return p;
    }

    /// <summary>
    /// The dropdown entry a saved row means: by GUID when it has one, else the one project
    /// parameter of that name, else the only entry of that name. A row this model cannot match
    /// becomes "Name  (not in this model)" — kept, so saving the preset does not lose it.
    /// </summary>
    private string LabelFor(ParamValue pv)
    {
        ParamChoice? hit = pv.Guid.Length > 0
            ? _choices.Values.FirstOrDefault(c => string.Equals(c.Guid, pv.Guid, StringComparison.OrdinalIgnoreCase))
            : _choices.Values.Where(c => string.Equals(c.Name, pv.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Guid.Length > 0 ? 1 : 0).FirstOrDefault();
        if (hit != null) return hit.Label;

        string label = $"{pv.Name}  (not in this model)";
        if (!_choices.ContainsKey(label))
        {
            _choices[label] = new ParamChoice(label, pv.Name, pv.Guid);
            _paramColumn.Items.Add(label);
        }
        return label;
    }

    private static string TemplateText(ComboBox c) => DrwControls.Text(c) == DrwLookup.None ? "" : DrwControls.Text(c);

    /// <summary>
    /// A template that controls View Scale wins over the Scale field (that is what a template is
    /// for). Say so while the choice is being made, rather than only in the results afterwards.
    /// </summary>
    private void UpdateTemplateNote()
    {
        if (_templateNote == null) return;   // events fire while the controls are still being built
        int scale = (int)_scale.Value;
        var clashes = new List<string>();
        void Check(string kind, ComboBox combo)
        {
            string name = TemplateText(combo);
            if (name.Length > 0 && _data.TemplateScales.TryGetValue($"{kind}|{name}", out int s) && s != scale)
                clashes.Add($"the {kind.ToLowerInvariant()} template '{name}' sets 1:{s}");
        }
        Check("Plan", _planTemplate);
        Check("Section", _sectionTemplate);
        if (!_data.IsStair) Check("Elevation", _elevTemplate);
        _templateNote.Text = clashes.Count == 0 ? "" :
            $"The Scale field (1:{scale}) will not apply where a template controls the scale: " +
            string.Join("; ", clashes) + ". The template's scale is used, and the results say so.";
    }

    private void Apply(EnlargePreset p)
    {
        _createPlan.Checked = p.CreatePlan;
        DrwControls.Select(_planType, p.PlanTypeName);
        _planMargin.Value = DrwControls.Clamp(_planMargin, p.PlanMarginM);
        _scale.Value = DrwControls.Clamp(_scale, p.Scale);
        _createVertical.Checked = p.CreateVerticalViews;
        _section.Checked = p.ViewKind == EnlargeViewKind.Section;
        _elevation.Checked = p.ViewKind == EnlargeViewKind.Elevation;
        if (_data.IsStair) { _section.Checked = true; _elevation.Checked = false; }   // stairs: sections only
        _north.Checked = p.North; _east.Checked = p.East; _south.Checked = p.South; _west.Checked = p.West;
        _orient.Checked = p.OrientToLongestEdge;
        DrwControls.Select(_sectionType, p.SectionTypeName);
        _fromCentre.Checked = p.SectionPlacement == SectionPlacement.FromCentre;
        _fromEdge.Checked = !_fromCentre.Checked;
        _sectionOffset.Value = DrwControls.Clamp(_sectionOffset, p.SectionOffsetM);
        _sectionCentreOffset.Value = DrwControls.Clamp(_sectionCentreOffset, p.SectionCentreOffsetM);
        _sectionBelow.Value = DrwControls.Clamp(_sectionBelow, p.SectionBelowM);
        _sectionAbove.Value = DrwControls.Clamp(_sectionAbove, p.SectionAboveM);
        _topFrom.SelectedIndex = p.SectionTopFrom == SectionTopFrom.NextLevel ? 1 : 0;
        _beforeFirst.Value = DrwControls.Clamp(_beforeFirst, p.StairBeforeFirstStepM);
        _afterLast.Value = DrwControls.Clamp(_afterLast, p.StairAfterLastStepM);
        _sectionCrop.Value = DrwControls.Clamp(_sectionCrop, p.SectionCropOffsetM);
        DrwControls.Select(_elevationType, p.ElevationTypeName);
        _byBoundary.Checked = p.ElevationMode == ElevationMode.ByBoundary;
        _byCenter.Checked = p.ElevationMode == ElevationMode.ByCenter4;
        _minLength.Value = DrwControls.Clamp(_minLength, p.MinBoundaryLengthM);
        _innerLoops.Checked = p.IncludeInnerLoops;
        _inset.Value = DrwControls.Clamp(_inset, p.ElevationInsetM);
        _prefix.Text = p.Prefix;
        _planPrefix.Text = p.PlanPrefix;
        _sectionPrefix.Text = p.SectionPrefix;
        _elevPrefix.Text = p.ElevationPrefix;
        _roomLabel.SelectedIndex = p.RoomLabel switch { RoomLabel.Number => 1, RoomLabel.NumberName => 2, _ => 0 };
        DrwControls.Select(_delimiter, p.MarkDelimiter);
        _overrideMarks.Checked = p.OverrideMultistoryMarks;
        DrwControls.Select(_planTemplate, string.IsNullOrEmpty(p.PlanTemplate) ? DrwLookup.None : p.PlanTemplate);
        DrwControls.Select(_sectionTemplate, string.IsNullOrEmpty(p.SectionTemplate) ? DrwLookup.None : p.SectionTemplate);
        DrwControls.Select(_elevTemplate, string.IsNullOrEmpty(p.ElevationTemplate) ? DrwLookup.None : p.ElevationTemplate);
        _skipExisting.Checked = p.SkipExisting;
        _batchFilter.Text = p.BatchFilter;
        _params.Rows.Clear();
        foreach (ParamValue pv in p.Parameters) _params.Rows.Add(LabelFor(pv), pv.Value);
        UpdateEnabled();
    }

    private void Accept()
    {
        _params.EndEdit();
        EnlargePreset p = Capture();
        if (_checked.Count == 0)
        {
            MessageBox.Show(this, $"Tick at least one {(_data.IsStair ? "stair" : "room")}.", Text);
            return;
        }
        if (!p.CreatePlan && !p.CreateVerticalViews)
        {
            MessageBox.Show(this, "Nothing to create: tick the plan, the sections/elevations, or both.", Text);
            return;
        }
        bool needsSides = p.CreateVerticalViews &&
                          (p.ViewKind == EnlargeViewKind.Section || p.ElevationMode == ElevationMode.ByCenter4);
        if (needsSides && !(p.North || p.East || p.South || p.West))
        {
            MessageBox.Show(this, "Tick at least one of N / E / S / W.", Text);
            return;
        }
        Result = p;
        DialogResult = DialogResult.OK;
    }
}
