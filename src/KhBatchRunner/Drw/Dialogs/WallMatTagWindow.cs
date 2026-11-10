using System.Windows.Forms;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.UI;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>What the Wall Mat Tag dialog lists, read from the model by the command. Plain strings,
/// so the window builds without Revit (the window gates construct it outside Revit).</summary>
internal sealed class WallMatTagDialogData
{
    /// <summary>"Family : Type" of the model's material tags.</summary>
    public List<string> MaterialTagTypes { get; set; } = new();
    public string ActiveView { get; set; } = "";
    public int SelectedViews { get; set; }
}

/// <summary>
/// Wall Mat Tag: the material tag type for each side a tag sits on, which faces, one pair per wall or
/// per piece between openings, which walls and views, the "?" repair and a dry run.
/// </summary>
internal sealed class WallMatTagWindow : DrwToolWindow
{
    private readonly ComboBox _faces, _above, _below, _left, _right, _elevation;
    private readonly CheckBox _perPiece, _seenOnly, _plans, _sections, _elevations, _repair, _dryRun;
    private readonly NumericUpDown _minPiece, _minLength;
    private readonly TextBox _typeMark, _typeName;
    private readonly ScopePicker _scope;
    private readonly DrwPresetBar<WallMatTagPreset> _bar;

    private static readonly (string Text, MaterialFaces Value)[] FaceChoices =
    {
        ("Both faces", MaterialFaces.Both), ("Exterior face", MaterialFaces.Exterior), ("Interior face", MaterialFaces.Interior)
    };

    public WallMatTagPreset Result { get; private set; } = new();

    public WallMatTagWindow(WallMatTagDialogData data, WallMatTagPreset initial)
    {
        Text = "Wall Mat Tag";

        _faces = DrwControls.Combo(FaceChoices.Select(f => f.Text));
        _above = DrwControls.Combo(data.MaterialTagTypes, withNone: true);
        _below = DrwControls.Combo(data.MaterialTagTypes, withNone: true);
        _left = DrwControls.Combo(data.MaterialTagTypes, withNone: true);
        _right = DrwControls.Combo(data.MaterialTagTypes, withNone: true);
        _elevation = DrwControls.Combo(data.MaterialTagTypes, withNone: true);
        var types = DrwControls.Group("Material tag",
            FormLayout.LabeledRow("Faces:", _faces),
            FormLayout.LabeledRow("Type that sits above the face:", _above),
            FormLayout.LabeledRow("Type that sits below the face:", _below),
            FormLayout.LabeledRow("Type that sits left of the face:", _left),
            FormLayout.LabeledRow("Type that sits right of the face:", _right),
            FormLayout.LabeledRow("Type in elevations (triangle points up):", _elevation),
            FormLayout.NoteLabel(this, "For a tag family with one type per direction (the triangle points at the face): " +
                "the type for the side the face is seen from is used. One type in every box is fine too. " +
                "KH_Anno_Wall Mat tag comes with DRW Helper and is loaded into the model the first time it is used. " +
                "Plans and sections tag only the walls they cut: the point goes 5 mm in from the finish face (a thinner " +
                "finish layer: its middle), with no leader; the two faces' tags line up, and on a wall off the view's axes " +
                "the tag turns with the wall. A section tags the walls it cuts on the cut, up the wall, and the walls seen beyond " +
                "it as an elevation does. Elevations tag each surface they look " +
                "at, upright. A face split or painted in surfaces gets one tag per surface. " +
                "Revit's API cannot link a new material tag to its material, so new tags show '?' until you touch " +
                "them in Revit; they are left selected for that. Coarse views show no layers and are skipped."));

        _perPiece = DrwControls.Check("One tag pair per wall piece between openings and meeting walls", false);
        _minPiece = DrwControls.Number(300, 0, 100000, 0, 50);
        var pieces = DrwControls.Group("Pieces",
            _perPiece,
            FormLayout.LabeledRow("Pieces shorter than, mm (skip):", _minPiece));

        _typeMark = new TextBox { Width = 160 };
        _typeName = new TextBox { Width = 160 };
        _minLength = DrwControls.Number(0, 0, 100000, 0, 100);
        _seenOnly = DrwControls.Check("Seen faces only: skip a face with a wall or column against it", true);
        var walls = DrwControls.Group("Walls",
            _seenOnly,
            FormLayout.NoteLabel(this, "A finish drawn as its own wall: the faces where the two walls touch are not tagged, the " +
                "finish wall's outer face is. Walls and columns meeting a face also cut it into pieces."),
            FormLayout.LabeledRow("Only Type Mark containing:", _typeMark),
            FormLayout.LabeledRow("Only wall type name containing:", _typeName),
            FormLayout.LabeledRow("Walls shorter than, mm (skip):", _minLength));

        _plans = DrwControls.Check("Plans", true);
        _sections = DrwControls.Check("Sections", false);
        _elevations = DrwControls.Check("Elevations", false);
        _scope = new ScopePicker(this, data.ActiveView, data.SelectedViews, "All views whose name contains:",
            FormLayout.FlowRow(new Label { Text = "Covering:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, _plans, _sections, _elevations));
        _repair = DrwControls.Check("Repair material tags whose point is off the wall or on a layer line ('?')", true);
        _dryRun = DrwControls.Check("Only report what would be tagged (change nothing)", false);

        var page = DrwControls.Page(types, pieces, walls, _scope.Group, DrwControls.Group("Options", _repair, _dryRun));
        _perPiece.CheckedChanged += (_, _) => _minPiece.Enabled = _perPiece.Checked;

        _bar = new DrwPresetBar<WallMatTagPreset>(this, WallMatTagStore.Store, Capture, Apply, initial.Name);
        Apply(initial);
        Build(_bar.Row, page, "Tag", new Size(680, 760), new Size(600, 520));
    }

    private static void Pick(ComboBox c, string value)
    {
        if (value.Length > 0 && !c.Items.Contains(value)) c.Items.Add(value);   // kept as stored, never swapped
        c.SelectedItem = value.Length > 0 ? value : DrwLookup.None;
    }

    private static string Picked(ComboBox c) => DrwControls.Text(c) is var t && t != DrwLookup.None ? t : "";

    private WallMatTagPreset Capture() => new()
    {
        Name = _bar?.SelectedName is { Length: > 0 } n ? n : "Default",
        Faces = _faces.SelectedIndex >= 0 && _faces.SelectedIndex < FaceChoices.Length ? FaceChoices[_faces.SelectedIndex].Value : MaterialFaces.Both,
        TypeAbove = Picked(_above),
        TypeBelow = Picked(_below),
        TypeLeft = Picked(_left),
        TypeRight = Picked(_right),
        TypeElevation = Picked(_elevation),
        PerPiece = _perPiece.Checked,
        SeenFacesOnly = _seenOnly.Checked,
        MinPieceMm = (double)_minPiece.Value,
        TypeMarkContains = _typeMark.Text.Trim(),
        TypeNameContains = _typeName.Text.Trim(),
        MinLengthMm = (double)_minLength.Value,
        RepairMaterialTags = _repair.Checked,
        Plans = _plans.Checked,
        Sections = _sections.Checked,
        Elevations = _elevations.Checked,
        Scope = _scope.Scope,
        ViewNameContains = _scope.Contains,
        DryRun = _dryRun.Checked
    };

    private void Apply(WallMatTagPreset p)
    {
        _faces.SelectedIndex = Math.Max(0, Array.FindIndex(FaceChoices, f => f.Value == p.Faces));
        Pick(_above, p.TypeAbove ?? "");
        Pick(_below, p.TypeBelow ?? "");
        Pick(_left, p.TypeLeft ?? "");
        Pick(_right, p.TypeRight ?? "");
        Pick(_elevation, p.TypeElevation ?? "");
        _perPiece.Checked = p.PerPiece;
        _seenOnly.Checked = p.SeenFacesOnly;
        _minPiece.Value = DrwControls.Clamp(_minPiece, p.MinPieceMm);
        _minPiece.Enabled = p.PerPiece;
        _typeMark.Text = p.TypeMarkContains ?? "";
        _typeName.Text = p.TypeNameContains ?? "";
        _minLength.Value = DrwControls.Clamp(_minLength, p.MinLengthMm);
        _repair.Checked = p.RepairMaterialTags;
        _plans.Checked = p.Plans;
        _sections.Checked = p.Sections;
        _elevations.Checked = p.Elevations;
        _scope.Set(p.Scope, p.ViewNameContains);
        _dryRun.Checked = p.DryRun;
    }

    protected override bool TryAccept(out string? why)
    {
        Result = Capture();
        bool anyType = new[] { Result.TypeAbove, Result.TypeBelow, Result.TypeLeft, Result.TypeRight, Result.TypeElevation }.Any(t => t.Length > 0);
        why = !anyType && !Result.RepairMaterialTags
                ? "Choose at least one material tag type (load a material tag family first), or tick the repair."
            : Result.Scope == DrwViewScope.AllPlans && !Result.Plans && !Result.Sections && !Result.Elevations
                ? "Tick at least one kind of view for the name filter."
            : null;
        return why == null;
    }
}
