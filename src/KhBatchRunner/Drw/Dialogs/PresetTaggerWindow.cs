using System.Drawing;
using System.Windows.Forms;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.UI;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>What the Preset Tagger dialog lists, read from the model by the command. Plain strings,
/// so the window builds without Revit (the window gates construct it outside Revit).</summary>
internal sealed class PresetTaggerDialogData
{
    /// <summary>Categories the model has that the tool can tag: key ("OST_Doors") and name ("Doors").</summary>
    public List<(string Key, string Label)> Categories { get; set; } = new();
    /// <summary>"Family : Type" tag types by category key.</summary>
    public Dictionary<string, List<string>> TagTypes { get; set; } = new();
    public string ActiveView { get; set; } = "";
    public int SelectedViews { get; set; }
}

/// <summary>
/// Preset Tagger: a list of rules (one per category, or more), the editor for the selected rule
/// below it, which views, and a dry run.
/// </summary>
internal sealed class PresetTaggerWindow : DrwToolWindow
{
    private readonly PresetTaggerDialogData _data;
    private readonly List<TagRule> _rules = new();
    private readonly ListBox _list;
    private readonly ComboBox _category, _tagType, _position, _mode;
    private readonly NumericUpDown _distance, _minLength;
    private readonly CheckBox _enabled, _leader, _horizontal, _plans, _ceilings, _sections, _elevations, _dryRun;
    private readonly TextBox _typeMark, _family;
    private readonly Control _editor;
    private readonly ScopePicker _scope;
    private readonly DrwPresetBar<PresetTaggerPreset> _bar;
    private bool _loading;

    private static readonly (string Text, TagPosition Value)[] Positions =
    {
        ("On the element", TagPosition.Center), ("Above", TagPosition.Above), ("Below", TagPosition.Below),
        ("Left", TagPosition.Left), ("Right", TagPosition.Right)
    };

    private static readonly string[] Modes = { "The view (above, below, left, right)", "The element (exterior or interior side)" };
    private static readonly (string Text, TagSide Value)[] Sides =
    {
        ("Exterior side (front, for a loose family)", TagSide.Exterior), ("Interior side (back, for a loose family)", TagSide.Interior)
    };

    public PresetTaggerPreset Result { get; private set; } = new();

    public PresetTaggerWindow(PresetTaggerDialogData data, PresetTaggerPreset initial)
    {
        Text = "Preset Tagger";
        _data = data;

        _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, MinimumSize = new Size(0, 130), Height = 150 };
        var add = DrwControls.Button("Add rule", 90, AddRule);
        var remove = DrwControls.Button("Remove", 90, RemoveRule);

        _category = DrwControls.Combo(Array.Empty<string>());
        _tagType = DrwControls.Combo(Array.Empty<string>());
        _position = DrwControls.Combo(Positions.Select(p => p.Text));
        _mode = DrwControls.Combo(Modes);
        _distance = DrwControls.Number(0, 0, 100, 1, 0.5);
        _enabled = DrwControls.Check("Use this rule", true);
        _leader = DrwControls.Check("Leader", false);
        _horizontal = DrwControls.Check("Horizontal text", true);
        _typeMark = new TextBox { Width = 160 };
        _family = new TextBox { Width = 160 };
        _minLength = DrwControls.Number(0, 0, 100000, 0, 100);

        _editor = DrwControls.Group("Selected rule",
            FormLayout.LabeledRow("Category:", _category),
            FormLayout.LabeledRow("Tag type:", _tagType),
            FormLayout.LabeledRow("Position by:", _mode),
            FormLayout.LabeledRow("Position:", _position),
            FormLayout.LabeledRow("Distance, mm on paper:", _distance),
            FormLayout.FlowRow(_enabled, _leader, _horizontal),
            FormLayout.LabeledRow("Only Type Mark containing:", _typeMark),
            FormLayout.LabeledRow("Only family containing:", _family),
            FormLayout.LabeledRow("Lines shorter than, mm (skip):", _minLength));

        _plans = DrwControls.Check("Plans", true);
        _ceilings = DrwControls.Check("Ceiling plans", false);
        _sections = DrwControls.Check("Sections", false);
        _elevations = DrwControls.Check("Elevations", false);
        // Four boxes on one row: the usual 12 px gap pushed "Elevations" 5 px past the row at the
        // window's minimum width (layout gate).
        foreach (CheckBox c in new[] { _plans, _ceilings, _sections, _elevations })
            c.Margin = new Padding(3, 4, 6, 4);
        _scope = new ScopePicker(this, data.ActiveView, data.SelectedViews, "All views whose name contains:",
            FormLayout.FlowRow(new Label { Text = "Covering:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
                _plans, _ceilings, _sections, _elevations));
        _dryRun = DrwControls.Check("Only report what would be tagged (change nothing)", false);

        var listPanel = new Panel { Dock = DockStyle.Top, Height = 150, MinimumSize = new Size(0, 130) };
        listPanel.Controls.Add(_list);
        var page = DrwControls.Page(
            DrwControls.Group("Rules",
                listPanel,
                FormLayout.FlowRow(add, remove),
                FormLayout.NoteLabel(this, "Each rule tags one category with one tag type. An element already tagged in " +
                    "the view is skipped, so running again adds nothing. If the chosen spot overlaps other annotation, " +
                    "the tag slides up to 12 mm further out, then the opposite side, the other sides and the centre " +
                    "are tried; with none clear the element is left " +
                    "untagged and listed as Skipped. By the element, a tag stays on its side (an exterior tag never " +
                    "moves to the interior). Distances are on paper, so they suit every view scale.")),
            _editor,
            _scope.Group,
            DrwControls.Group("Options", _dryRun));

        _list.SelectedIndexChanged += (_, _) => { if (!_loading) ShowRule(); };
        _category.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            FillTagTypes(CategoryKey(), "");
            Store();
        };
        _mode.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            FillPositions(_mode.SelectedIndex == 1, TagPosition.Above, TagSide.Exterior);
            Store();
        };
        foreach (ComboBox c in new[] { _tagType, _position })
            c.SelectedIndexChanged += (_, _) => Store();
        foreach (NumericUpDown n in new[] { _distance, _minLength }) n.ValueChanged += (_, _) => Store();
        foreach (CheckBox c in new[] { _enabled, _leader, _horizontal }) c.CheckedChanged += (_, _) => Store();
        foreach (TextBox t in new[] { _typeMark, _family }) t.TextChanged += (_, _) => Store();

        _bar = new DrwPresetBar<PresetTaggerPreset>(this, PresetTaggerStore.Store, Capture, Apply, initial.Name);
        Apply(initial);
        Build(_bar.Row, page, "Tag", new Size(700, 820), new Size(600, 520));
    }

    // ---------------------------------------------------------------------------------------------

    private string LabelOf(string key) =>
        _data.Categories.FirstOrDefault(c => c.Key == key).Label is { Length: > 0 } l ? l : key + " (not in this model)";

    private string Line(TagRule r) =>
        $"{(r.Enabled ? "" : "(off) ")}{LabelOf(r.Category)} — {(r.TagType.Length > 0 ? r.TagType : "no tag type")} — " +
        ((r.PositionMode == TagPositionMode.Element
                ? (r.Side == TagSide.Exterior ? "exterior side" : "interior side")
                : Positions.FirstOrDefault(p => p.Value == r.Position).Text ?? "On the element") +
              (r.DistanceMm > 0 ? $", {r.DistanceMm:0.#} mm" : ""));

    /// <summary>The category list: the model's categories, plus the rule's own when the model lacks it.</summary>
    private void FillCategories(string key)
    {
        _category.Items.Clear();
        foreach ((string _, string label) in _data.Categories) _category.Items.Add(label);
        if (!_data.Categories.Any(c => c.Key == key)) _category.Items.Add(LabelOf(key));
        _category.SelectedItem = LabelOf(key);
        Widen(_category);
    }

    private string CategoryKey()
    {
        string label = DrwControls.Text(_category);
        string? key = _data.Categories.FirstOrDefault(c => c.Label == label).Key;
        if (key != null) return key;
        const string missing = " (not in this model)";
        return label.EndsWith(missing, StringComparison.Ordinal) ? label.Substring(0, label.Length - missing.Length) : label;
    }

    /// <summary>The tag types for a category. A stored type this model lacks is kept in the list as
    /// it is, so opening a preset never quietly swaps it for another.</summary>
    private void FillTagTypes(string key, string stored)
    {
        _tagType.Items.Clear();
        List<string> types = _data.TagTypes.TryGetValue(key, out List<string>? t) ? t : new List<string>();
        foreach (string s in types) _tagType.Items.Add(s);
        if (stored.Length > 0 && !types.Contains(stored)) _tagType.Items.Add(stored);
        if (stored.Length > 0) _tagType.SelectedItem = stored;
        else if (_tagType.Items.Count > 0) _tagType.SelectedIndex = 0;
        Widen(_tagType);
    }

    private static void Widen(ComboBox c) => KhBatchRunner.UI.FormLayout.FitList(c);

    /// <summary>The position list for the mode: view directions, or the element's two sides.</summary>
    private void FillPositions(bool element, TagPosition position, TagSide side)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            _position.Items.Clear();
            if (element)
            {
                foreach ((string text, TagSide _) in Sides) _position.Items.Add(text);
                _position.SelectedIndex = Math.Max(0, Array.FindIndex(Sides, s => s.Value == side));
            }
            else
            {
                foreach ((string text, TagPosition _) in Positions) _position.Items.Add(text);
                _position.SelectedIndex = Math.Max(0, Array.FindIndex(Positions, p => p.Value == position));
            }
        }
        finally { _loading = was; }
    }

    private void ShowRule()
    {
        int i = _list.SelectedIndex;
        _editor.Enabled = i >= 0 && i < _rules.Count;
        if (!_editor.Enabled) return;
        TagRule r = _rules[i];
        _loading = true;
        try
        {
            FillCategories(r.Category);
            FillTagTypes(r.Category, r.TagType);
            _mode.SelectedIndex = r.PositionMode == TagPositionMode.Element ? 1 : 0;
            FillPositions(r.PositionMode == TagPositionMode.Element, r.Position, r.Side);
            _distance.Value = DrwControls.Clamp(_distance, r.DistanceMm);
            _enabled.Checked = r.Enabled;
            _leader.Checked = r.Leader;
            _horizontal.Checked = r.Horizontal;
            _typeMark.Text = r.TypeMarkContains;
            _family.Text = r.FamilyContains;
            _minLength.Value = DrwControls.Clamp(_minLength, r.MinLengthMm);
        }
        finally { _loading = false; }
    }

    /// <summary>The editor's values into the selected rule, and its line in the list.</summary>
    private void Store()
    {
        int i = _list.SelectedIndex;
        if (_loading || i < 0 || i >= _rules.Count) return;
        TagRule r = _rules[i];
        r.Category = CategoryKey();
        r.TagType = DrwControls.Text(_tagType);
        r.PositionMode = _mode.SelectedIndex == 1 ? TagPositionMode.Element : TagPositionMode.View;
        int pos = _position.SelectedIndex;
        if (r.PositionMode == TagPositionMode.Element)
            r.Side = pos >= 0 && pos < Sides.Length ? Sides[pos].Value : TagSide.Exterior;
        else
            r.Position = pos >= 0 && pos < Positions.Length ? Positions[pos].Value : TagPosition.Center;
        r.DistanceMm = (double)_distance.Value;
        r.Enabled = _enabled.Checked;
        r.Leader = _leader.Checked;
        r.Horizontal = _horizontal.Checked;
        r.TypeMarkContains = _typeMark.Text.Trim();
        r.FamilyContains = _family.Text.Trim();
        r.MinLengthMm = (double)_minLength.Value;
        _loading = true;
        try { _list.Items[i] = Line(r); }
        finally { _loading = false; }
    }

    private void AddRule()
    {
        string key = _data.Categories.FirstOrDefault(c => !_rules.Any(r => r.Category == c.Key)).Key
                     ?? _data.Categories.FirstOrDefault().Key ?? "OST_Doors";
        string type = _data.TagTypes.TryGetValue(key, out List<string>? t) && t.Count > 0 ? t[0] : "";
        _rules.Add(new TagRule { Category = key, TagType = type });
        _list.Items.Add(Line(_rules[_rules.Count - 1]));
        _list.SelectedIndex = _rules.Count - 1;
    }

    private void RemoveRule()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _rules.Count) return;
        _rules.RemoveAt(i);
        _list.Items.RemoveAt(i);
        _list.SelectedIndex = Math.Min(i, _rules.Count - 1);
        ShowRule();
    }

    // ---------------------------------------------------------------------------------------------

    private PresetTaggerPreset Capture() => new()
    {
        Name = _bar?.SelectedName is { Length: > 0 } n ? n : "Default",
        Rules = _rules.Select(r => r.Copy()).ToList(),
        Plans = _plans.Checked,
        CeilingPlans = _ceilings.Checked,
        Sections = _sections.Checked,
        Elevations = _elevations.Checked,
        Scope = _scope.Scope,
        ViewNameContains = _scope.Contains,
        DryRun = _dryRun.Checked
    };

    private void Apply(PresetTaggerPreset p)
    {
        _rules.Clear();
        _list.Items.Clear();
        foreach (TagRule r in p.Rules ?? new List<TagRule>())
        {
            TagRule copy = r.Copy();
            // A new preset's rules name no tag type: offer the model's first, as the dialog shows it.
            if (copy.TagType.Length == 0 && _data.TagTypes.TryGetValue(copy.Category, out List<string>? t) && t.Count > 0)
                copy.TagType = t[0];
            _rules.Add(copy);
            _list.Items.Add(Line(copy));
        }
        _plans.Checked = p.Plans;
        _ceilings.Checked = p.CeilingPlans;
        _sections.Checked = p.Sections;
        _elevations.Checked = p.Elevations;
        _scope.Set(p.Scope, p.ViewNameContains);
        _dryRun.Checked = p.DryRun;
        _list.SelectedIndex = _rules.Count > 0 ? 0 : -1;
        ShowRule();
    }

    protected override bool TryAccept(out string? why)
    {
        Result = Capture();
        List<TagRule> on = Result.Rules.Where(r => r.Enabled).ToList();
        why = on.Count == 0 ? "Add a rule, or tick \"Use this rule\" on one."
            : on.FirstOrDefault(r => r.TagType.Length == 0) is TagRule r0
                ? $"Choose a tag type for {LabelOf(r0.Category)} (this model has none loaded; load a tag family first)."
            : Result.Scope == DrwViewScope.AllPlans && !Result.Plans && !Result.CeilingPlans && !Result.Sections && !Result.Elevations
                ? "Tick at least one kind of view for the name filter."
            : null;
        return why == null;
    }
}
