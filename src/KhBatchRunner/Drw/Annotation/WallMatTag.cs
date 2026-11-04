using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

/// <summary>A Wall Mat Tag preset: the material tag types per direction, which faces, and which walls
/// and views.</summary>
public sealed class WallMatTagPreset : IDrwPreset
{
    public string Name { get; set; } = "Default";
    public MaterialFaces Faces { get; set; } = MaterialFaces.Both;
    /// <summary>Material tag types, "Family : Type", by where the tag sits beside the face: a family
    /// with one type per direction (the Thai triangle tag) points its tip at the face this way. One
    /// type for every direction is fine too. Blank = that direction is not tagged. By default the
    /// family that comes with DRW Helper, loaded into the model when it lacks it.</summary>
    public string TypeAbove { get; set; } = BundledFamilies.WallMatType(TagPosition.Above);
    public string TypeBelow { get; set; } = BundledFamilies.WallMatType(TagPosition.Below);
    public string TypeLeft { get; set; } = BundledFamilies.WallMatType(TagPosition.Left);
    public string TypeRight { get; set; } = BundledFamilies.WallMatType(TagPosition.Right);
    /// <summary>The type for elevations, upright: the triangle points up at the surface.</summary>
    public string TypeElevation { get; set; } = BundledFamilies.WallMatElevationType();
    /// <summary>One tag pair per piece of wall between door and window openings.</summary>
    public bool PerPiece { get; set; }
    /// <summary>Pieces shorter than this (model mm) are left out.</summary>
    public double MinPieceMm { get; set; } = 300;
    /// <summary>Only faces that are seen: skip a face another wall or a column is against (a finish
    /// drawn as its own wall), and cut pieces where walls and columns meet the face.</summary>
    public bool SeenFacesOnly { get; set; } = true;
    /// <summary>Only walls whose type's Type Mark contains this (blank = every wall).</summary>
    public string TypeMarkContains { get; set; } = "";
    /// <summary>Only walls whose type name contains this (blank = every wall).</summary>
    public string TypeNameContains { get; set; } = "";
    /// <summary>Walls shorter than this many model mm are left out.</summary>
    public double MinLengthMm { get; set; }
    /// <summary>Before tagging, move material tags whose point cannot read a material (off the wall,
    /// past its end, in an opening, on a layer line) back into the layer.</summary>
    public bool RepairMaterialTags { get; set; } = true;
    /// <summary>Which kinds of view the name-filter scope (and so Batch Run) covers.</summary>
    public bool Plans { get; set; } = true;
    public bool Sections { get; set; }
    public bool Elevations { get; set; }
    public DrwViewScope Scope { get; set; } = DrwViewScope.ActiveView;
    public string ViewNameContains { get; set; } = "";
    /// <summary>Report what would be tagged; change nothing.</summary>
    public bool DryRun { get; set; }
}

public static class WallMatTagStore
{
    public static readonly DrwPresetStore<WallMatTagPreset> Store = new("WallMatTagPresets");
}

/// <summary>
/// Wall Mat Tag: a material tag on each finish face of every wall in the views, its point 5 mm in
/// from the finish face (inside the outermost layer), of the type whose triangle sits on the side the view shows the
/// face from — only on faces that are seen, cut into pieces by openings and by walls meeting them. It is Preset Tagger's engine with one material rule: the same overlap search, rerun
/// check and "?" repair, and the same result columns.
/// </summary>
internal static class WallMatTag
{
    public static IReadOnlyList<string> Columns => PresetTagger.Columns;

    /// <summary>The Preset Tagger preset the engine runs: one material rule for walls.</summary>
    internal static PresetTaggerPreset ToEngine(WallMatTagPreset p) => new()
    {
        Name = p.Name,
        // Repair only (no type chosen): no rule, so nothing is reported as missing a type.
        Rules = new[] { p.TypeAbove, p.TypeBelow, p.TypeLeft, p.TypeRight, p.TypeElevation }.All(string.IsNullOrWhiteSpace) ? new List<TagRule>() : new List<TagRule>
        {
            new()
            {
                Category = "OST_Walls", What = TagWhat.Material, Faces = p.Faces,
                MaterialTypeAbove = p.TypeAbove, MaterialTypeBelow = p.TypeBelow,
                MaterialTypeLeft = p.TypeLeft, MaterialTypeRight = p.TypeRight, MaterialTypeElevation = p.TypeElevation,
                MaterialPerPiece = p.PerPiece, MinPieceMm = p.MinPieceMm, MaterialSeenOnly = p.SeenFacesOnly,
                TypeMarkContains = p.TypeMarkContains, TypeNameContains = p.TypeNameContains, MinLengthMm = p.MinLengthMm
            }
        },
        Plans = p.Plans, Sections = p.Sections, Elevations = p.Elevations, Scope = p.Scope, ViewNameContains = p.ViewNameContains,
        DryRun = p.DryRun, RepairMaterialTags = p.RepairMaterialTags
    };

    /// <param name="materialTags">Filled with the tags made, so the command can select them for the
    /// person to touch (a new material tag reads "?" until then).</param>
    /// <remarks>A type of a family that comes with DRW Helper is loaded first when the model lacks it
    /// (a dry run loads it too, then undoes that with everything else).</remarks>
    public static List<IReadOnlyDictionary<string, string>> Run(Document doc, IReadOnlyList<View> views, WallMatTagPreset preset,
        List<ElementId>? materialTags = null)
    {
        string[] types = { preset.TypeAbove, preset.TypeBelow, preset.TypeLeft, preset.TypeRight, preset.TypeElevation };
        if (BundledFamilies.Missing(doc, types).Count == 0) return PresetTagger.Run(doc, views, ToEngine(preset), materialTags);
        using var group = new TransactionGroup(doc, "Wall Mat Tag");
        group.Start();
        List<string> loaded = BundledFamilies.LoadMissing(doc, types);
        List<IReadOnlyDictionary<string, string>> rows = PresetTagger.Run(doc, views, ToEngine(preset), materialTags);
        if (preset.DryRun) group.RollBack();
        else group.Assimilate();
        foreach (string f in loaded)
            rows.Insert(0, DrwRows.Row(Columns, "", "", f, "Material Tags", f, preset.DryRun ? "Would load" : "Loaded",
                "This family comes with DRW Helper and was not in the model."));
        return rows;
    }

    /// <summary>The views a run covers: plans and sections (the walls they cut) and elevations (the
    /// surfaces they look at).</summary>
    public static List<View> Views(Document doc, Autodesk.Revit.UI.UIDocument? uiDoc, WallMatTagPreset p) =>
        DrwScope.ResolveTagViews(doc, uiDoc, p.Scope, p.ViewNameContains, p.Plans, ceilingPlans: false, p.Sections, p.Elevations)
            .Where(v => DrwScope.IsPlan(v) || v.ViewType is ViewType.Section or ViewType.Elevation).ToList();
}
