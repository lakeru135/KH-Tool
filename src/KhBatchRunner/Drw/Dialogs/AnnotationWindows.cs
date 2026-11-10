using System.Windows.Forms;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.UI;
using NumberSystemDisplayRule = Autodesk.Revit.DB.NumberSystemDisplayRule;
using NumberSystemJustifyOption = Autodesk.Revit.DB.NumberSystemJustifyOption;
using StairsNumberSystemReferenceOption = Autodesk.Revit.DB.Architecture.StairsNumberSystemReferenceOption;
using TagOrientation = Autodesk.Revit.DB.TagOrientation;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>The "which views" block both annotation dialogs share.</summary>
internal sealed class ScopePicker
{
    private readonly RadioButton _active, _selected, _all;
    private readonly TextBox _contains;
    private readonly Control[] _extra;

    public GroupBox Group { get; }

    /// <param name="allText">The name-filter option's words ("All floor plans whose name contains:").</param>
    /// <param name="extraRows">Rows that belong to the name filter (e.g. which kinds of view it covers):
    /// indented under it, and enabled only while it is chosen.</param>
    public ScopePicker(Form host, string activeViewName, int selectedViewCount,
        string allText = "All floor plans whose name contains:", params Control[] extraRows)
    {
        _active = new RadioButton { Text = $"Active view ({activeViewName})", AutoSize = true, Checked = true };
        _selected = new RadioButton { Text = $"Views selected in the Project Browser ({selectedViewCount})", AutoSize = true };
        _all = new RadioButton { Text = allText, AutoSize = true };
        _contains = new TextBox { Width = 160 };   // 220 ran past the group at the minimum width
        _extra = extraRows;
        foreach (Control row in _extra) row.Margin = new Padding(22, row.Margin.Top, row.Margin.Right, row.Margin.Bottom);
        // `_all` sits in a FlowRow with its text box, so WinForms does not group it with the other two
        // (radio buttons exclude each other only within one parent): clicking it used to leave "Active
        // view" ticked as well — and Scope read that first, ignoring the name filter. One-of-three by hand.
        foreach (RadioButton rb in new[] { _active, _selected, _all })
            rb.CheckedChanged += (s, _) =>
            {
                if (((RadioButton)s!).Checked)
                    foreach (RadioButton other in new[] { _active, _selected, _all })
                        if (other != s) other.Checked = false;
                EnableFilter();
            };
        EnableFilter();
        Group = DrwControls.Group("Views",
            new Control[] { _active, _selected, FormLayout.FlowRow(_all, _contains) }
                .Concat(extraRows)
                .Append(FormLayout.NoteLabel(host, "Batch Run always uses the last option, with the text saved in the preset."))
                .ToArray());
    }

    private void EnableFilter()
    {
        _contains.Enabled = _all.Checked;
        foreach (Control row in _extra) row.Enabled = _all.Checked;
    }

    public DrwViewScope Scope =>
        _active.Checked ? DrwViewScope.ActiveView : _selected.Checked ? DrwViewScope.SelectedViews : DrwViewScope.AllPlans;

    public string Contains => _contains.Text.Trim();

    public void Set(DrwViewScope scope, string contains)
    {
        _active.Checked = scope == DrwViewScope.ActiveView;
        _selected.Checked = scope == DrwViewScope.SelectedViews;
        _all.Checked = scope == DrwViewScope.AllPlans;
        _contains.Text = contains;
        EnableFilter();
    }
}

/// <summary>Shared frame: preset bar, a page of groups, and Run / Cancel.</summary>
internal abstract class DrwToolWindow : Form
{
    // Minimum 600 wide: at 560 the preset list shrank to 133 px beside its four buttons.
    protected void Build(Control presetRow, Control page, string runText) =>
        Build(presetRow, page, runText, new Size(620, 600), new Size(600, 480));

    protected void Build(Control presetRow, Control page, string runText, Size size, Size minimum)
    {
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = size;
        MinimumSize = minimum;

        var run = DrwControls.Button(runText, 110, OnRun);
        var cancel = DrwControls.Button("Cancel", 90);
        cancel.DialogResult = DialogResult.Cancel;
        // A modeless window (Door & Window) is not closed by a DialogResult; close it (the user,
        // 2026-10-22: Cancel did nothing).
        cancel.Click += (_, _) => { if (!Modal) Close(); };
        _cancel = cancel;
        var bottom = FormLayout.FlowRow(run, cancel);
        bottom.Anchor = AnchorStyles.Right;

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, presetRow);
        FormLayout.AddFillRow(root, page, 100);
        FormLayout.AddAutoRow(root, bottom);
        Controls.Add(root);
        AcceptButton = run;
        CancelButton = cancel;
        // Scoped, as EnlargeWindow does: aligned across the whole form, the page's longest label set
        // the preset row's label column too, and the preset combo was crushed to ~30 px beside its
        // four buttons (0.2.6 Datum Dimension, "Every-datum string, mm in from the datum end:").
        FormLayout.AlignLabels(this, presetRow, page);
        DrwControls.FitNotes(this);
        Theme.Apply(this);
    }

    private Button? _cancel;

    /// <summary>The Cancel button, pressed (the self-test).</summary>
    internal void PressCancel() => _cancel?.PerformClick();

    protected abstract bool TryAccept(out string? why);

    /// <summary>The run button: a dialog closes with OK; a window that stays open (Enlarge Door
    /// &amp; Window) runs its work and stays.</summary>
    protected virtual void OnRun()
    {
        if (TryAccept(out string? why)) DialogResult = DialogResult.OK; else MessageBox.Show(this, why, Text);
    }
}

internal sealed class DatumDimensionWindow : DrwToolWindow
{
    private readonly ComboBox _dimType, _side;
    private readonly CheckBox _every, _overall, _spacingFromType, _extra, _skip, _grids, _levels, _plans, _sections, _elevations;
    private readonly NumericUpDown _everyOffset, _overallOffset;
    private readonly Label _snap;
    private readonly IReadOnlyDictionary<string, double?> _snaps;
    private readonly ScopePicker _scope;
    private readonly DrwPresetBar<DatumDimensionPreset> _bar;

    // The Side combo's rows, in order, each tied to the value it saves.
    private static readonly (string Text, GridSide Value)[] Sides =
    {
        ("Most bubbles (tie: both ends)", GridSide.MostBubbles),
        ("Both ends", GridSide.Both),
        ("Top / right", GridSide.TopRight),
        ("Bottom / left", GridSide.BottomLeft)
    };

    public DatumDimensionPreset Result { get; private set; } = new();

    /// <param name="snapDistancesMm">Each dimension type's Dimension Line Snap Distance in paper mm,
    /// by the label the list shows (null = the type has none).</param>
    public DatumDimensionWindow(List<string> dimensionTypes, IReadOnlyDictionary<string, double?> snapDistancesMm,
        string activeView, int selectedViews, DatumDimensionPreset initial)
    {
        Text = "Datum Dimension";
        _snaps = snapDistancesMm;
        _dimType = DrwControls.Combo(dimensionTypes);
        _grids = DrwControls.Check("Grids", true);
        _levels = DrwControls.Check("Levels (sections / elevations)", true);
        _every = DrwControls.Check("One string through every grid / level", true);
        _overall = DrwControls.Check("One overall string, first to last", true);
        _extra = DrwControls.Check("Add one more every-grid / every-level string, to edit by hand", false);
        _everyOffset = DrwControls.Number(18, 0, 200, 1, 1);
        _overallOffset = DrwControls.Number(10, 0, 200, 1, 1);
        _spacingFromType = DrwControls.Check("Space the strings by the type's Dimension Line Snap Distance", false);
        _snap = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        _side = DrwControls.Combo(Sides.Select(s => s.Text));
        _skip = DrwControls.Check("Skip views already dimensioned (grids and levels judged apart)", true);
        _plans = DrwControls.Check("Plans", true);
        _sections = DrwControls.Check("Sections", false);
        _elevations = DrwControls.Check("Elevations", false);
        _scope = new ScopePicker(this, activeView, selectedViews, "All views whose name contains:",
            FormLayout.FlowRow(new Label { Text = "Covering:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
                _plans, _sections, _elevations));
        _dimType.SelectedIndexChanged += (_, _) => UpdateSpacing();
        _spacingFromType.CheckedChanged += (_, _) => UpdateSpacing();

        var page = DrwControls.Page(
            DrwControls.Group("Dimensions",
                FormLayout.FlowRow(new Label { Text = "Dimension:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
                    _grids, _levels),
                FormLayout.LabeledRow("Dimension type:", _dimType),
                _overall, _every, _extra,
                FormLayout.LabeledRow("Datum end:", _side),
                FormLayout.LabeledRow("Overall string, mm from the end:", _overallOffset),
                FormLayout.LabeledRow("Every-datum string, mm from the end:", _everyOffset, _snap),
                _spacingFromType,
                FormLayout.NoteLabel(this, "From the datum end inward: overall, then every datum, then the extra " +
                    "string, one gap apart (the difference of the two mm values, or the type's snap distance). " +
                    "Millimetres on paper; \"top\" and \"right\" as the view shows them. Arc grids, and a grid only " +
                    "nearly parallel to the others, are left out with a row saying so."),
                _skip),
            _scope.Group);

        _bar = new DrwPresetBar<DatumDimensionPreset>(this, DatumDimensionStore.Store, Capture, Apply, initial.Name);
        Apply(initial);
        Build(_bar.Row, page, "Dimension", new Size(660, 640), new Size(600, 500));
        _snap.ForeColor = Theme.Dim(_snap);
    }

    /// <summary>The every-grid field is the type's business while "space by type" is ticked; the
    /// label beside it says what the chosen type's snap distance is.</summary>
    private void UpdateSpacing()
    {
        double? mm = _snaps.TryGetValue(DrwControls.Text(_dimType), out double? v) ? v : null;
        _snap.Text = mm is > 0 ? $"(this type: {mm:0.0} mm)" : "(this type has no snap distance)";
        _everyOffset.Enabled = !_spacingFromType.Checked || !(mm > 0);
    }

    private DatumDimensionPreset Capture() => new()
    {
        Name = _bar?.SelectedName is { Length: > 0 } n ? n : "Default",
        DimensionTypeName = DrwControls.Text(_dimType),
        EveryGrid = _every.Checked,
        Overall = _overall.Checked,
        EveryGridOffsetMm = (double)_everyOffset.Value,
        OverallOffsetMm = (double)_overallOffset.Value,
        SpacingFromType = _spacingFromType.Checked,
        ExtraString = _extra.Checked,
        Side = _side.SelectedIndex >= 0 && _side.SelectedIndex < Sides.Length ? Sides[_side.SelectedIndex].Value : GridSide.MostBubbles,
        Grids = _grids.Checked,
        Levels = _levels.Checked,
        Plans = _plans.Checked,
        Sections = _sections.Checked,
        Elevations = _elevations.Checked,
        Scope = _scope.Scope,
        ViewNameContains = _scope.Contains,
        SkipDimensioned = _skip.Checked
    };

    private void Apply(DatumDimensionPreset p)
    {
        DrwControls.Select(_dimType, p.DimensionTypeName);
        _every.Checked = p.EveryGrid;
        _overall.Checked = p.Overall;
        _everyOffset.Value = DrwControls.Clamp(_everyOffset, p.EveryGridOffsetMm);
        _overallOffset.Value = DrwControls.Clamp(_overallOffset, p.OverallOffsetMm);
        _spacingFromType.Checked = p.SpacingFromType;
        _extra.Checked = p.ExtraString;
        _side.SelectedIndex = Math.Max(0, Array.FindIndex(Sides, s => s.Value == p.Side));
        _grids.Checked = p.Grids;
        _levels.Checked = p.Levels;
        _plans.Checked = p.Plans;
        _sections.Checked = p.Sections;
        _elevations.Checked = p.Elevations;
        _scope.Set(p.Scope, p.ViewNameContains);
        _skip.Checked = p.SkipDimensioned;
        UpdateSpacing();
    }

    protected override bool TryAccept(out string? why)
    {
        Result = Capture();
        why = !Result.EveryGrid && !Result.Overall && !Result.ExtraString ? "Tick at least one of the dimension strings."
            : !Result.Grids && !Result.Levels ? "Tick Grids, Levels, or both."
            : Result.Scope == DrwViewScope.AllPlans && !Result.Plans && !Result.Sections && !Result.Elevations
                ? "Tick at least one kind of view (Plans, Sections, Elevations) for the name filter."
            : null;
        return why == null;
    }
}

/// <summary>
/// Match Datums: which views take the active view's grid and level ends. Only views parallel to the
/// source are listed; nothing is ticked except views selected in the Project Browser.
/// </summary>
internal sealed class MatchDatumsWindow : Form
{
    /// <summary>One view the list can offer.</summary>
    public sealed class Choice
    {
        public Choice(long id, string name, string kind, long scopeBox)
        {
            Id = id; Name = name; Kind = kind; ScopeBox = scopeBox;
        }

        public long Id { get; }
        public string Name { get; }
        public string Kind { get; }
        /// <summary>The scope box its crop follows; -1 for none.</summary>
        public long ScopeBox { get; }
        public override string ToString() => $"{Name}   ({Kind})";
    }

    private readonly List<Choice> _all;
    private readonly HashSet<long> _ticked;
    private readonly CheckedListBox _list;
    private readonly TextBox _filter;
    private readonly Label _count;
    private bool _filling;

    public IReadOnlyList<long> SelectedIds => _all.Where(c => _ticked.Contains(c.Id)).Select(c => c.Id).ToList();

    public MatchDatumsWindow(string sourceName, string sourceKind, long sourceScopeBox, string sourceScopeBoxName,
        List<Choice> choices, IEnumerable<long> preselected, int sourceScale = 0)
    {
        Text = "Match Datums";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(620, 600);
        MinimumSize = new Size(520, 460);
        _all = choices;
        _ticked = new HashSet<long>(preselected.Where(id => choices.Any(c => c.Id == id)));

        _filter = new TextBox { Width = 240 };
        _filter.TextChanged += (_, _) => Fill();
        _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        _list.ItemCheck += (_, e) =>
        {
            if (_filling) return;
            long id = ((Choice)_list.Items[e.Index]).Id;
            if (e.NewValue == CheckState.Checked) _ticked.Add(id); else _ticked.Remove(id);
            BeginInvokeCount();
        };
        _count = new Label { AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

        var sameBox = DrwControls.Button("Same Scope Box", 110, () => TickWhere(c => sourceScopeBox >= 0 && c.ScopeBox == sourceScopeBox));
        sameBox.Enabled = sourceScopeBox >= 0;
        var all = DrwControls.Button("All", 70, () => TickWhere(_ => true));
        var none = DrwControls.Button("None", 70, () => { foreach (Choice c in Visible()) _ticked.Remove(c.Id); Fill(); });

        string datums = sourceKind == "Plan" ? "grid" : "grid and level";
        var header = FormLayout.NoteLabel(this,
            $"In the views ticked below, every {datums} end is made 2D and placed exactly where \"{sourceName}\" " +
            $"({sourceKind}) shows it — even beyond a view's crop or scope box, where the crop hides it (its row says so). " +
            "Bubbles and elbows are copied too. " +
            $"Only views parallel to it{(sourceScale > 0 ? $" and at its scale (1:{sourceScale})" : "")} are listed. " +
            (sourceScopeBox >= 0 ? $"Its crop follows Scope Box '{sourceScopeBoxName}'. " : "") +
            "Section markers cannot be matched: the Revit API has no per-view access to their ends — adjust them by " +
            "hand. One Undo reverses the whole run.");

        var run = DrwControls.Button("Match", 110, () =>
        {
            if (_ticked.Count > 0) DialogResult = DialogResult.OK;
            else MessageBox.Show(this, "Tick at least one view.", Text);
        });
        var cancel = DrwControls.Button("Cancel", 90);
        cancel.DialogResult = DialogResult.Cancel;
        var bottom = FormLayout.FlowRow(run, cancel);
        bottom.Anchor = AnchorStyles.Right;

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, header);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Name contains:", _filter));
        FormLayout.AddAutoRow(root, FormLayout.FlowRow(sameBox, all, none, _count));
        FormLayout.AddFillRow(root, _list, 100);
        FormLayout.AddAutoRow(root, bottom);
        Controls.Add(root);
        AcceptButton = run;
        CancelButton = cancel;
        Fill();
        DrwControls.FitNotes(this);
        Theme.Apply(this);
    }

    private IEnumerable<Choice> Visible()
    {
        string f = _filter.Text.Trim();
        return _all.Where(c => f.Length == 0 || c.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>Ticks the visible views that pass <paramref name="test"/>; the name filter narrows every helper.</summary>
    private void TickWhere(Func<Choice, bool> test)
    {
        foreach (Choice c in Visible().Where(test)) _ticked.Add(c.Id);
        Fill();
    }

    private void Fill()
    {
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (Choice c in Visible())
            _list.Items.Add(c, _ticked.Contains(c.Id));
        _list.EndUpdate();
        _filling = false;
        UpdateCount();
    }

    // ItemCheck fires before the tick changes; count once it has.
    private void BeginInvokeCount()
    {
        if (IsHandleCreated) BeginInvoke(new Action(UpdateCount)); else UpdateCount();
    }

    private void UpdateCount() =>
        _count.Text = $"{_ticked.Count} of {_all.Count} ticked" + (_list.Items.Count < _all.Count ? $", {_list.Items.Count} shown" : "");
}

internal sealed class StairAnnotationWindow : DrwToolWindow
{
    private readonly CheckBox _path, _numbers, _skip, _sectionsToo, _dims, _dimOverall, _dimSnap;
    private readonly ComboBox _pathType, _tagType, _display, _reference, _justify, _orientation, _dimType;
    private readonly NumericUpDown _referenceOffset, _justifyOffset, _numberSize, _dimOffset, _dimSpacing;
    private readonly Label _dimSnapValue;
    private readonly IReadOnlyDictionary<string, double?> _dimSnaps;
    private readonly TextBox _runLabel;
    private readonly ScopePicker _scope;
    private readonly DrwPresetBar<StairAnnotationPreset> _bar;

    // The palette's own words ("Right Quarter", "Start and End"), each tied to the API value it sets.
    private static readonly string[] TagTypes = { "Riser", "Tread" };
    private static readonly (string Text, NumberSystemDisplayRule Value)[] Displays =
    {
        ("All", NumberSystemDisplayRule.All), ("Even", NumberSystemDisplayRule.Even),
        ("Odd", NumberSystemDisplayRule.Odd), ("Start and End", NumberSystemDisplayRule.StartAndEnd)
    };
    private static readonly (string Text, StairsNumberSystemReferenceOption Value)[] References =
    {
        ("Center", StairsNumberSystemReferenceOption.Center), ("Left", StairsNumberSystemReferenceOption.Left),
        ("Right", StairsNumberSystemReferenceOption.Right), ("Left Quarter", StairsNumberSystemReferenceOption.LeftQuarter),
        ("Right Quarter", StairsNumberSystemReferenceOption.RightQuarter)
    };
    private static readonly (string Text, NumberSystemJustifyOption Value)[] Justifies =
    {
        ("Center", NumberSystemJustifyOption.Center), ("Front", NumberSystemJustifyOption.Front),
        ("Back", NumberSystemJustifyOption.Back)
    };
    private static readonly (string Text, TagOrientation Value)[] Orientations =
    {
        ("Horizontal", TagOrientation.Horizontal), ("Vertical", TagOrientation.Vertical)
    };

    public StairAnnotationPreset Result { get; private set; } = new();

    public StairAnnotationWindow(List<string> pathTypes, List<string> dimTypes, IReadOnlyDictionary<string, double?> snapDistancesMm,
        string activeView, int selectedViews,
        StairAnnotationPreset initial)
    {
        Text = "Stair Annotation";
        _path = DrwControls.Check("Place stair path", true);
        _pathType = DrwControls.Combo(pathTypes);
        _numbers = DrwControls.Check("Place tread numbers", true);
        _tagType = DrwControls.Combo(TagTypes);
        _display = DrwControls.Combo(Displays.Select(x => x.Text));
        _reference = DrwControls.Combo(References.Select(x => x.Text));
        _referenceOffset = DrwControls.Number(0, -1000, 1000, 2, 0.5);
        _justify = DrwControls.Combo(Justifies.Select(x => x.Text));
        _justifyOffset = DrwControls.Number(0, -1000, 1000, 2, 0.5);
        _orientation = DrwControls.Combo(Orientations.Select(x => x.Text));
        _numberSize = DrwControls.Number(0, 0, 100, 2, 0.1);
        _dims = DrwControls.Check("Place dimensions (plans; straight runs: single, U or L)", true);
        _dimType = DrwControls.Combo(dimTypes);
        _dimOffset = DrwControls.Number(10, 0, 100, 1, 1);
        _dimSpacing = DrwControls.Number(8, 1, 50, 1, 1);
        _dimSnaps = snapDistancesMm;
        _dimSnap = DrwControls.Check("Space the strings by the type's Dimension Line Snap Distance", true);
        _dimSnapValue = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        _dimSnap.CheckedChanged += (_, _) => UpdateDimSpacing();
        _dimOverall = DrwControls.Check("Overall length and width strings", true);
        _runLabel = new TextBox { Width = 160 };
        _dims.CheckedChanged += (_, _) => EnableDims();
        _dimType.SelectedIndexChanged += (_, _) => UpdateDimSpacing();
        _skip = DrwControls.Check("Leave stairs that already have a path, numbers or dimensions as they are", true);
        _skip.Enabled = false;   // always on: duplicates are never wanted
        _sectionsToo = DrwControls.Check("Sections too (tread numbers only)", false);
        _scope = new ScopePicker(this, activeView, selectedViews, "All floor plans whose name contains:", _sectionsToo);
        _path.CheckedChanged += (_, _) => _pathType.Enabled = _path.Checked;
        _numbers.CheckedChanged += (_, _) => EnableNumbers();

        var page = DrwControls.Page(
            DrwControls.Group("Stair path",
                _path, FormLayout.LabeledRow("Path type:", _pathType)),
            DrwControls.Group("Tread numbers",
                _numbers,
                FormLayout.LabeledRow("Tag Type:", _tagType),
                FormLayout.LabeledRow("Display Rule:", _display),
                FormLayout.LabeledRow("Reference:", _reference),
                FormLayout.LabeledRow("Offset from Reference (mm):", _referenceOffset),
                FormLayout.LabeledRow("Justify:", _justify),
                FormLayout.LabeledRow("Justify Offset (mm):", _justifyOffset),
                FormLayout.LabeledRow("Orientation:", _orientation),
                FormLayout.LabeledRow("Number Size (mm):", _numberSize),
                FormLayout.NoteLabel(this, "The Graphics settings of Revit's Properties palette, written on every " +
                    "new number (tread numbers have no type). Number Size 0 keeps Revit's size. Numbers are placed " +
                    "in the plan of the stair's base level only, so a stair seen from the level above is not " +
                    "numbered twice. Paths go on every plan showing the stair. In a section only numbers are " +
                    "placed, on every stair it shows.")),
            DrwControls.Group("Dimensions",
                _dims,
                FormLayout.LabeledRow("Dimension type:", _dimType),
                FormLayout.LabeledRow("First string from the stair (mm on paper):", _dimOffset),
                _dimSnap,
                FormLayout.LabeledRow("Between strings (mm on paper):", _dimSpacing, _dimSnapValue),
                FormLayout.LabeledRow("Run prefix:", _runLabel),
                _dimOverall,
                FormLayout.NoteLabel(this, "Each run is dimensioned along its outer side, with the run segment prefixed " +
                    "({N} = treads, {D} = tread depth in metres, so \"10T @ 0.275 = \" before Revit's 2750). The widths go " +
                    "on the opposite side (for a U, beyond the end the first run climbs to), overall strings outside. " +
                    "Plans of the stair's base level only; winder and curved stairs are skipped for now. Each row's " +
                    "Note says which line each run end is attached to.")),
            _scope.Group,
            _skip);

        _bar = new DrwPresetBar<StairAnnotationPreset>(this, StairAnnotationStore.Store, Capture, Apply, initial.Name);
        Apply(initial);
        Build(_bar.Row, page, "Annotate");
        _dimSnapValue.ForeColor = Theme.Dim(_dimSnapValue);
    }

    private void EnableNumbers()
    {
        foreach (Control c in new Control[] { _tagType, _display, _reference, _referenceOffset, _justify, _justifyOffset, _orientation, _numberSize })
            c.Enabled = _numbers.Checked;
    }

    private void EnableDims()
    {
        foreach (Control c in new Control[] { _dimType, _dimOffset, _dimSnap, _runLabel, _dimOverall })
            c.Enabled = _dims.Checked;
        UpdateDimSpacing();
    }

    /// <summary>The mm spacing is only asked for when the type's snap distance is not used (or it has none).</summary>
    private void UpdateDimSpacing()
    {
        double? mm = _dimSnaps.TryGetValue(DrwControls.Text(_dimType), out double? v) ? v : null;
        _dimSnapValue.Text = !_dimSnap.Checked ? "" : mm is > 0 ? $"(type's snap: {mm:0.0} mm)" : "(type has no snap: this is used)";
        _dimSpacing.Enabled = _dims.Checked && (!_dimSnap.Checked || !(mm > 0));
    }

    private static T Pick<T>((string Text, T Value)[] options, ComboBox combo, T fallback) =>
        options.Where(o => o.Text == DrwControls.Text(combo)).Select(o => o.Value).DefaultIfEmpty(fallback).First();

    private static void SelectOption<T>((string Text, T Value)[] options, ComboBox combo, T value) =>
        DrwControls.Select(combo, options.Where(o => Equals(o.Value, value)).Select(o => o.Text).FirstOrDefault() ?? "");

    private StairAnnotationPreset Capture() => new()
    {
        Name = _bar?.SelectedName is { Length: > 0 } n ? n : "Default",
        PlacePath = _path.Checked,
        PathTypeName = DrwControls.Text(_pathType),
        PlaceNumbers = _numbers.Checked,
        TagType = DrwControls.Text(_tagType),
        DisplayRule = Pick(Displays, _display, NumberSystemDisplayRule.All),
        Reference = Pick(References, _reference, StairsNumberSystemReferenceOption.Center),
        ReferenceOffsetMm = (double)_referenceOffset.Value,
        Justify = Pick(Justifies, _justify, NumberSystemJustifyOption.Center),
        JustifyOffsetMm = (double)_justifyOffset.Value,
        Orientation = Pick(Orientations, _orientation, TagOrientation.Horizontal),
        NumberSizeMm = (double)_numberSize.Value,
        PlaceDimensions = _dims.Checked,
        DimensionTypeName = DrwControls.Text(_dimType),
        DimOffsetMm = (double)_dimOffset.Value,
        DimSpacingMm = (double)_dimSpacing.Value,
        SpacingFromType = _dimSnap.Checked,
        DimOverall = _dimOverall.Checked,
        RunLabel = _runLabel.Text,
        Scope = _scope.Scope,
        ViewNameContains = _scope.Contains,
        Sections = _sectionsToo.Checked,
        SkipExisting = true
    };

    private void Apply(StairAnnotationPreset p)
    {
        _path.Checked = p.PlacePath;
        DrwControls.Select(_pathType, p.PathTypeName);
        _numbers.Checked = p.PlaceNumbers;
        DrwControls.Select(_tagType, p.TagType);
        SelectOption(Displays, _display, p.DisplayRule);
        SelectOption(References, _reference, p.Reference);
        _referenceOffset.Value = DrwControls.Clamp(_referenceOffset, p.ReferenceOffsetMm);
        SelectOption(Justifies, _justify, p.Justify);
        _justifyOffset.Value = DrwControls.Clamp(_justifyOffset, p.JustifyOffsetMm);
        SelectOption(Orientations, _orientation, p.Orientation);
        _numberSize.Value = DrwControls.Clamp(_numberSize, p.NumberSizeMm);
        _dims.Checked = p.PlaceDimensions;
        DrwControls.Select(_dimType, p.DimensionTypeName);
        _dimOffset.Value = DrwControls.Clamp(_dimOffset, p.DimOffsetMm);
        _dimSpacing.Value = DrwControls.Clamp(_dimSpacing, p.DimSpacingMm);
        _dimSnap.Checked = p.SpacingFromType;
        _dimOverall.Checked = p.DimOverall;
        _runLabel.Text = p.RunLabel;
        EnableDims();
        _sectionsToo.Checked = p.Sections;
        _scope.Set(p.Scope, p.ViewNameContains);
        _pathType.Enabled = _path.Checked;
        EnableNumbers();
    }

    protected override bool TryAccept(out string? why)
    {
        Result = Capture();
        why = !Result.PlacePath && !Result.PlaceNumbers && !Result.PlaceDimensions
            ? "Tick the stair path, the tread numbers or the dimensions." : null;
        return why == null;
    }
}

/// <summary>Single Font: the font, which groups it reaches (with how many of each this model has),
/// renaming, and a dry run.</summary>
internal sealed class SingleFontWindow : DrwToolWindow
{
    private readonly ComboBox _font;
    private readonly CheckBox _text, _dims, _modelText, _schedules, _tags, _titleBlocks, _heads, _otherAnno, _modelFams, _rename, _dryRun;
    private readonly DrwPresetBar<SingleFontPreset> _bar;

    public SingleFontPreset Result { get; private set; } = new();

    /// <param name="fonts">Installed fonts, for the list; any name can still be typed.</param>
    /// <param name="counts">How many of each group the model has, by <see cref="SingleFontPreset"/> property name.</param>
    /// <param name="familyDocument">Run from the Family Editor: the groups name the open family and
    /// the families nested in it, rather than a project and its loaded families.</param>
    public SingleFontWindow(List<string> fonts, IReadOnlyDictionary<string, int> counts, SingleFontPreset initial,
        bool familyDocument = false)
    {
        Text = "Single Font";
        _font = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 280, AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems };
        foreach (string f in fonts) _font.Items.Add(f);
        CheckBox Tick(string text, string key) =>
            DrwControls.Check(counts.TryGetValue(key, out int n) ? $"{text} ({n})" : text, true);
        _text = Tick(familyDocument ? "Text and label types" : "Text types (text, legends, schedule text)", "TextTypes");
        _dims = Tick("Dimension and spot types", "DimensionTypes");
        _modelText = Tick("Model text types", "ModelTextTypes");
        _schedules = Tick("Schedule cell and column font overrides (schedules)", "ScheduleFonts");
        _tags = Tick("Tag families", "TagFamilies");
        _titleBlocks = Tick("Title blocks", "TitleBlocks");
        _heads = Tick("Grid / level / section heads, view titles", "HeadFamilies");
        _otherAnno = Tick("Other annotation families (generic annotations, symbols)", "OtherAnnotationFamilies");
        _modelFams = Tick("Model and detail families that contain text (slow)", "ModelFamilies");
        _rename = DrwControls.Check("Rename types to the new font (\"2.5mm Arial\" → \"2.5mm Tahoma\")", true);
        _dryRun = DrwControls.Check("Only report what would change (change nothing)", false);

        var page = DrwControls.Page(
            DrwControls.Group("Font", FormLayout.LabeledRow("Font:", _font),
                FormLayout.NoteLabel(this, "Any installed font, or type a name for a preset used on another PC.")),
            DrwControls.Group(familyDocument ? "This family's types" : "Project types", _text, _dims, _modelText, _schedules),
            DrwControls.Group(familyDocument
                    ? "Nested families (each opened, changed and reloaded into this family only if it changed)"
                    : "Loaded families (each opened, changed and reloaded only if it changed)",
                _tags, _titleBlocks, _heads, _otherAnno, _modelFams,
                FormLayout.NoteLabel(this, "Nested annotation families inside them are changed too. In-place and " +
                    "non-editable families are skipped and named.")),
            DrwControls.Group("Options", _rename, _dryRun));

        _bar = new DrwPresetBar<SingleFontPreset>(this, SingleFontStore.Store, Capture, Apply, initial.Name);
        Apply(initial);
        Build(_bar.Row, page, "Apply");
    }

    private SingleFontPreset Capture() => new()
    {
        Name = _bar?.SelectedName is { Length: > 0 } n ? n : "Default",
        FontName = _font.Text.Trim(),
        TextTypes = _text.Checked, DimensionTypes = _dims.Checked, ModelTextTypes = _modelText.Checked,
        ScheduleFonts = _schedules.Checked, TagFamilies = _tags.Checked, TitleBlocks = _titleBlocks.Checked,
        HeadFamilies = _heads.Checked, OtherAnnotationFamilies = _otherAnno.Checked, ModelFamilies = _modelFams.Checked,
        RenameTypes = _rename.Checked, DryRun = _dryRun.Checked
    };

    private void Apply(SingleFontPreset p)
    {
        _font.Text = p.FontName;
        _text.Checked = p.TextTypes; _dims.Checked = p.DimensionTypes; _modelText.Checked = p.ModelTextTypes;
        _schedules.Checked = p.ScheduleFonts; _tags.Checked = p.TagFamilies; _titleBlocks.Checked = p.TitleBlocks;
        _heads.Checked = p.HeadFamilies; _otherAnno.Checked = p.OtherAnnotationFamilies; _modelFams.Checked = p.ModelFamilies;
        _rename.Checked = p.RenameTypes; _dryRun.Checked = p.DryRun;
    }

    protected override bool TryAccept(out string? why)
    {
        Result = Capture();
        bool any = Result.TextTypes || Result.DimensionTypes || Result.ModelTextTypes || Result.ScheduleFonts || Result.TagFamilies ||
                   Result.TitleBlocks || Result.HeadFamilies || Result.OtherAnnotationFamilies || Result.ModelFamilies;
        why = Result.FontName.Length == 0 ? "Choose or type a font." : !any ? "Tick at least one group." : null;
        return why == null;
    }
}
