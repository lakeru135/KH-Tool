using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

/// <summary>Where a rule puts its tag, as the view shows it, measured from the element's point.</summary>
public enum TagPosition { Center, Above, Below, Left, Right }

/// <summary>What a rule's position is measured by: the view's directions, or the element's own
/// sides (a door's exterior side stays exterior however the view is turned).</summary>
public enum TagPositionMode { View, Element }

/// <summary>An element's side in Element mode. For a wall, or a door or window in one, Exterior is
/// the wall's exterior; for any other family it is the front (its facing side).</summary>
public enum TagSide { Exterior, Interior }

/// <summary>What a wall rule tags: the wall itself, or the material of its finish face.</summary>
public enum TagWhat { Element, Material }

/// <summary>Which finish faces a wall material rule tags.</summary>
public enum MaterialFaces { Exterior, Interior, Both }

/// <summary>One category of a Preset Tagger preset: which tag type, where, and which elements.</summary>
public sealed class TagRule
{
    public bool Enabled { get; set; } = true;
    /// <summary>The element category, as a BuiltInCategory name ("OST_Doors"); resolved at run time
    /// so a category missing in some Revit year never breaks the build.</summary>
    public string Category { get; set; } = "OST_Doors";
    /// <summary>"Family : Type" of the tag. A model without it fails this rule only — no other type
    /// is ever used in its place.</summary>
    public string TagType { get; set; } = "";
    public TagPosition Position { get; set; } = TagPosition.Center;
    /// <summary>Paper millimetres from the element's point to the tag's, at the view's scale.</summary>
    public double DistanceMm { get; set; }
    public bool Leader { get; set; }
    /// <summary>Horizontal text; otherwise vertical.</summary>
    public bool Horizontal { get; set; } = true;
    /// <summary>Only elements whose type's Type Mark contains this (blank = every element).</summary>
    public string TypeMarkContains { get; set; } = "";
    /// <summary>Only elements whose family name contains this (blank = every element).</summary>
    public string FamilyContains { get; set; } = "";
    /// <summary>Only elements whose type name contains this (blank = every element).</summary>
    public string TypeNameContains { get; set; } = "";
    /// <summary>Line-based elements (walls, beams…) shorter than this many model mm are left out.</summary>
    public double MinLengthMm { get; set; }

    /// <summary>View: <see cref="Position"/> is above/below/left/right as the view shows it.
    /// Element: <see cref="Side"/> is the element's exterior or interior side, and the distance is
    /// measured from the wall face (or the element's edge) to the tag's near edge.</summary>
    public TagPositionMode PositionMode { get; set; } = TagPositionMode.View;
    public TagSide Side { get; set; } = TagSide.Exterior;

    /// <summary>Walls only: tag the wall, or the material of its outermost finish layer.</summary>
    public TagWhat What { get; set; } = TagWhat.Element;
    public MaterialFaces Faces { get; set; } = MaterialFaces.Both;
    /// <summary>Material tag types, "Family : Type", by where the tag sits beside the face: a family
    /// with one type per direction (the Thai triangle tag) points its tip at the face this way.
    /// One type for every direction is fine too. Blank = that direction is not tagged.</summary>
    public string MaterialTypeAbove { get; set; } = "";
    public string MaterialTypeBelow { get; set; } = "";
    public string MaterialTypeLeft { get; set; } = "";
    public string MaterialTypeRight { get; set; } = "";
    /// <summary>Material tags in elevations: one type, upright (the Thai tag's triangle points up at
    /// the surface). Blank = elevations are not tagged.</summary>
    public string MaterialTypeElevation { get; set; } = "";

    /// <summary>Material tags: one pair per piece of wall between door and window openings,
    /// instead of one per wall. Pieces shorter than <see cref="MinPieceMm"/> (model mm) are left out.</summary>
    public bool MaterialPerPiece { get; set; }
    public double MinPieceMm { get; set; } = 300;
    /// <summary>Material tags: only faces that are seen — a face another wall or a column is against
    /// is skipped where it is covered, and cut into pieces where one meets it.</summary>
    public bool MaterialSeenOnly { get; set; }

    public bool TagsMaterial() => What == TagWhat.Material && Category == "OST_Walls";

    public TagRule Copy() => (TagRule)MemberwiseClone();
}

/// <summary>A Preset Tagger preset: one rule per category, and which views it covers.</summary>
public sealed class PresetTaggerPreset : IDrwPreset
{
    public string Name { get; set; } = "Default";
    public List<TagRule> Rules { get; set; } = new()
    {
        new TagRule { Category = "OST_Doors" },
        new TagRule { Category = "OST_Windows" },
        new TagRule { Category = "OST_Rooms" }
    };
    /// <summary>Which kinds of view the name-filter scope (and so Batch Run) covers.</summary>
    public bool Plans { get; set; } = true;
    public bool CeilingPlans { get; set; }
    public bool Sections { get; set; }
    public bool Elevations { get; set; }
    public DrwViewScope Scope { get; set; } = DrwViewScope.ActiveView;
    public string ViewNameContains { get; set; } = "";
    /// <summary>Report what would be tagged; change nothing.</summary>
    public bool DryRun { get; set; }
    /// <summary>Before tagging, re-seat wall material tags that read "?" into their finish layer.
    /// The one case where this tool moves a tag it did not make: only tags that read "?".</summary>
    public bool RepairMaterialTags { get; set; }
}

public static class PresetTaggerStore
{
    public static readonly DrwPresetStore<PresetTaggerPreset> Store = new("PresetTaggerPresets");
}

/// <summary>
/// Preset Tagger: tags the elements of each rule's category in each view, with the rule's own tag
/// type, at a paper-millimetre distance scaled by the view.
///
/// <para>An element that already carries a tag in the view is skipped, so a rerun adds nothing and
/// never moves a tag someone placed by hand. When a preset has two rules for one category (a door
/// number tag and a fire-rating tag), each rule only counts tags of its own tag family.</para>
///
/// <para>Overlaps: every tag is created at its preferred spot, the view is regenerated once, and each
/// tag's real size is read. Then, tag by tag, the preferred spot and the alternates (opposite side,
/// the two other sides, centre) are tried against the annotation already in the view and the tags
/// accepted so far. The first clear spot wins; with none clear the tag is deleted and the element is
/// Skipped. A second regenerate reads every kept tag back before its row says Created.</para>
/// </summary>
internal static partial class PresetTagger
{
    public static readonly IReadOnlyList<string> Columns = new[] { "Kind", "View", "Element", "Category", "Tag type", "Status", "Note" };

    /// <summary>Element category → its tag category, by BuiltInCategory name. Names this Revit
    /// does not know are dropped when resolved.</summary>
    private static readonly (string Element, string Tag)[] TagCategoryNames =
    {
        ("OST_Doors", "OST_DoorTags"), ("OST_Windows", "OST_WindowTags"), ("OST_Rooms", "OST_RoomTags"),
        ("OST_MEPSpaces", "OST_MEPSpaceTags"), ("OST_Walls", "OST_WallTags"), ("OST_Floors", "OST_FloorTags"),
        ("OST_Ceilings", "OST_CeilingTags"), ("OST_Roofs", "OST_RoofTags"), ("OST_Furniture", "OST_FurnitureTags"),
        ("OST_FurnitureSystems", "OST_FurnitureSystemTags"), ("OST_Casework", "OST_CaseworkTags"),
        ("OST_GenericModel", "OST_GenericModelTags"), ("OST_PlumbingFixtures", "OST_PlumbingFixtureTags"),
        ("OST_SpecialityEquipment", "OST_SpecialityEquipmentTags"), ("OST_LightingFixtures", "OST_LightingFixtureTags"),
        ("OST_ElectricalFixtures", "OST_ElectricalFixtureTags"), ("OST_ElectricalEquipment", "OST_ElectricalEquipmentTags"),
        ("OST_MechanicalEquipment", "OST_MechanicalEquipmentTags"), ("OST_DuctTerminal", "OST_DuctTerminalTags"),
        ("OST_Sprinklers", "OST_SprinklerTags"), ("OST_StructuralColumns", "OST_StructuralColumnTags"),
        ("OST_StructuralFraming", "OST_StructuralFramingTags"), ("OST_StructuralFoundation", "OST_StructuralFoundationTags"),
        ("OST_Stairs", "OST_StairsTags"), ("OST_StairsRailing", "OST_StairsRailingTags"),
        ("OST_CurtainWallPanels", "OST_CurtainWallPanelTags"), ("OST_Parking", "OST_ParkingTags"),
        ("OST_Planting", "OST_PlantingTags"), ("OST_Site", "OST_SiteTags"), ("OST_Parts", "OST_PartTags")
    };

    /// <summary>Rooms and spaces: tagged with their own tag kinds, in plans only, never by a
    /// multi-category tag.</summary>
    private static bool IsSpatial(BuiltInCategory c) => c is BuiltInCategory.OST_Rooms or BuiltInCategory.OST_MEPSpaces;

    private static BuiltInCategory? Parse(string name) =>
        Enum.TryParse(name, out BuiltInCategory c) ? c : null;

    /// <summary>The tag category of an element category, or null when this tool cannot tag it.</summary>
    public static BuiltInCategory? TagCategory(BuiltInCategory element)
    {
        string name = element.ToString();
        foreach ((string e, string t) in TagCategoryNames)
            if (e == name) return Parse(t);
        return null;
    }

    /// <summary>The element categories this tool can tag that this model knows, with their names.</summary>
    public static List<(string Key, string Label)> Categories(Document doc)
    {
        var list = new List<(string, string)>();
        foreach ((string e, string t) in TagCategoryNames)
        {
            if (Parse(e) is not BuiltInCategory ec || Parse(t) is null) continue;
            Category? cat = null;
            try { cat = Category.GetCategory(doc, ec); } catch { /* not in this model */ }
            if (cat != null) list.Add((e, cat.Name));
        }
        return list.OrderBy(c => c.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The tag types a rule for <paramref name="element"/> may use: that category's tags,
    /// and multi-category tags for anything but rooms and spaces.</summary>
    public static List<FamilySymbol> TagTypes(Document doc, BuiltInCategory element)
    {
        if (TagCategory(element) is not BuiltInCategory tagCat) return new List<FamilySymbol>();
        var ids = new List<BuiltInCategory> { tagCat };
        if (!IsSpatial(element)) ids.Add(BuiltInCategory.OST_MultiCategoryTags);
        return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => s.Category != null && ids.Any(c => s.Category.Id.Value == (long)c))
            .OrderBy(s => DrwLookup.TypeLabel(s), StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Tag types by category key, as the dialog lists them.</summary>
    public static Dictionary<string, List<string>> TagTypeLabels(Document doc, IEnumerable<string> categoryKeys)
    {
        var map = new Dictionary<string, List<string>>();
        foreach (string key in categoryKeys)
            map[key] = Parse(key) is BuiltInCategory c ? DrwLookup.Labels(TagTypes(doc, c)) : new List<string>();
        return map;
    }

    /// <summary>The model's material tag types, as a wall material rule may use them.</summary>
    public static List<FamilySymbol> MaterialTagTypes(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => s.Category != null && s.Category.Id.Value == (long)BuiltInCategory.OST_MaterialTags)
            .OrderBy(s => DrwLookup.TypeLabel(s), StringComparer.OrdinalIgnoreCase).ToList();

    private static readonly TagPosition[] Sides = { TagPosition.Above, TagPosition.Below, TagPosition.Left, TagPosition.Right };

    private static string MaterialType(TagRule rule, TagPosition side) => side switch
    {
        TagPosition.Above => rule.MaterialTypeAbove,
        TagPosition.Below => rule.MaterialTypeBelow,
        TagPosition.Left => rule.MaterialTypeLeft,
        _ => rule.MaterialTypeRight
    } ?? "";

    /// <summary>The rule's material tag types, for the results' Tag type column.</summary>
    private static string MaterialLabel(TagRule rule) =>
        string.Join(" / ", Sides.Select(s => MaterialType(rule, s)).Append(rule.MaterialTypeElevation ?? "").Where(s => s.Length > 0).Distinct());

    private static void ResolveMaterial(Document doc, Resolved r)
    {
        if (!r.Rule.TagsMaterial())
        {
            r.Problem = "Material tags are offered for walls only.";
            return;
        }
        List<FamilySymbol> types = MaterialTagTypes(doc);
        var missing = new List<string>();
        foreach (TagPosition side in Sides)
        {
            string label = MaterialType(r.Rule, side).Trim();
            FamilySymbol? sym = label.Length == 0 ? null : types.FirstOrDefault(t => DrwLookup.TypeLabel(t) == label);
            if (label.Length > 0 && sym == null) missing.Add(label);
            r.Materials[side] = sym;
        }
        string elevation = (r.Rule.MaterialTypeElevation ?? "").Trim();
        r.Elevation = elevation.Length == 0 ? null : types.FirstOrDefault(t => DrwLookup.TypeLabel(t) == elevation);
        if (elevation.Length > 0 && r.Elevation == null) missing.Add(elevation);
        if (missing.Count > 0)
            r.Problem = $"The material tag type '{missing.Distinct().First()}' is not in this model, so this rule tagged nothing " +
                        "(no other type is used in its place).";
        else if (r.Materials.Values.All(s => s == null) && r.Elevation == null)
            r.Problem = "The rule names no material tag type.";
        r.Tag = r.Materials.Values.FirstOrDefault(s => s != null) ?? r.Elevation;
    }

    /// <summary>A view this tool tags in.</summary>
    public static bool IsTagView(View v) =>
        !v.IsTemplate && (DrwScope.IsPlan(v) || v.ViewType is ViewType.CeilingPlan or ViewType.Section or ViewType.Elevation);

    private static string KindOf(View v) => v.ViewType switch
    {
        ViewType.CeilingPlan => "Ceiling plan",
        ViewType.Section => "Section",
        ViewType.Elevation => "Elevation",
        _ => "Plan"
    };

    // =============================================================================================

    /// <summary>A rule checked against this model: its category and tag type, or why it cannot run.</summary>
    private sealed class Resolved
    {
        public TagRule Rule = null!;
        public BuiltInCategory Category;
        public string CategoryName = "";
        public FamilySymbol? Tag;
        public string? Problem;
        /// <summary>Another enabled rule has the same category: "already tagged" then means a tag
        /// of this rule's own tag family.</summary>
        public bool Shared;
        /// <summary>Material rules: the tag type for each side a tag sits on (null = not set).</summary>
        public Dictionary<TagPosition, FamilySymbol?> Materials = new();
        /// <summary>Material rules: the tag type for elevations (null = not set).</summary>
        public FamilySymbol? Elevation;
        public bool Material => Rule.TagsMaterial();
    }

    /// <summary>A tag made this run, waiting for its place to be settled.</summary>
    private sealed class Pending
    {
        public Resolved Rule = null!;
        public Element Element = null!;
        public Element Tag = null!;
        public XYZ Anchor = null!;
        public XYZ First = null!;
        /// <summary>Element mode: the outward direction of the chosen side (null = by the view).</summary>
        public XYZ? Out;
        /// <summary>Element mode: model feet from the anchor to the face along <see cref="Out"/>.</summary>
        public double Edge;
        /// <summary>Further anchors along a wall, tried in order when the first is blocked.</summary>
        public List<XYZ> Alternates = new();
        /// <summary>A material tag: its point must stay in the finish layer, so only the anchors vary.</summary>
        public bool Fixed;
        /// <summary>A material tag: the material its finish layer has, to check what Revit read.</summary>
        public ElementId? LayerMaterial;
        /// <summary>A note set when the tag was planned (a fallback taken).</summary>
        public string Note = "";
        /// <summary>The tag type made (a material rule picks one per side).</summary>
        public FamilySymbol? Symbol;
        /// <summary>By the view: the side to try first.</summary>
        public TagPosition Position;
        /// <summary>A material tag: which face, for the results.</summary>
        public string Face = "";
        /// <summary>A material tag: its wall and piece. The two faces of one piece share it — they
        /// settle together.</summary>
        public string Group = "";
        /// <summary>A material tag: which face of its wall. Tags back to back across a wall never
        /// count each other as in the way, paired or not.</summary>
        public TagSide? Side;
    }

    /// <summary>A tag already in the view: its family, whether it is a material tag, where it sits.</summary>
    private sealed class TagInfo
    {
        public string Family = "";
        public bool Material;
        public XYZ? Head;
    }

    /// <summary>A rectangle in the view's own coordinates (along RightDirection / UpDirection), feet.</summary>
    private readonly struct Box
    {
        public readonly double MinX, MinY, MaxX, MaxY;
        public Box(double minX, double minY, double maxX, double maxY) { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public Box Shift(double dx, double dy) => new(MinX + dx, MinY + dy, MaxX + dx, MaxY + dy);

        public bool Overlaps(Box o, double tolerance) =>
            MinX < o.MaxX - tolerance && o.MinX < MaxX - tolerance && MinY < o.MaxY - tolerance && o.MinY < MaxY - tolerance;

        /// <summary>The smallest and largest of the corners measured along (ux, uy).</summary>
        public double Low(double ux, double uy) => Math.Min(MinX * ux, MaxX * ux) + Math.Min(MinY * uy, MaxY * uy);
        public double High(double ux, double uy) => Math.Max(MinX * ux, MaxX * ux) + Math.Max(MinY * uy, MaxY * uy);
    }

    /// <param name="materialTags">Filled with the material tags made, so the command can select them
    /// for the person to touch (a new material tag reads "?" until then).</param>
    public static List<IReadOnlyDictionary<string, string>> Run(Document doc, IReadOnlyList<View> views, PresetTaggerPreset preset,
        List<ElementId>? materialTags = null)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        List<Resolved> rules = Resolve(doc, preset, rows);
        if (rules.All(r => r.Problem != null) && !preset.RepairMaterialTags) return rows;

        foreach (View view in views)
        {
            try { TagView(doc, view, rules.Where(r => r.Problem == null).ToList(), preset.DryRun, preset.RepairMaterialTags, rows, materialTags); }
            catch (Exception ex)
            {
                rows.Add(DrwRows.Row(Columns, KindOf(view), view.Name, "", "", "", "Failed", ex.Message));
            }
        }
        return rows;
    }

    private static List<Resolved> Resolve(Document doc, PresetTaggerPreset preset, List<IReadOnlyDictionary<string, string>> rows)
    {
        var list = new List<Resolved>();
        List<TagRule> enabled = preset.Rules.Where(r => r.Enabled).ToList();
        foreach (TagRule rule in enabled)
        {
            var r = new Resolved { Rule = rule, CategoryName = rule.Category };
            if (Parse(rule.Category) is not BuiltInCategory cat || TagCategory(cat) is null)
                r.Problem = $"Preset Tagger cannot tag the category '{rule.Category}'.";
            else
            {
                r.Category = cat;
                try { r.CategoryName = Category.GetCategory(doc, cat)?.Name ?? rule.Category; } catch { /* keep the key */ }
                if (rule.What == TagWhat.Material)
                {
                    ResolveMaterial(doc, r);
                    r.Shared = enabled.Count(o => o.Category == rule.Category && o.TagsMaterial()) > 1;
                    if (r.Problem != null)
                        rows.Add(DrwRows.Row(Columns, "", "", "", r.CategoryName, MaterialLabel(rule), "Failed", r.Problem));
                    list.Add(r);
                    continue;
                }
                List<FamilySymbol> types = TagTypes(doc, cat);
                r.Tag = types.FirstOrDefault(t => DrwLookup.TypeLabel(t) == rule.TagType);
                if (string.IsNullOrWhiteSpace(rule.TagType))
                    r.Problem = "The rule names no tag type.";
                else if (r.Tag == null)
                    r.Problem = $"The tag type '{rule.TagType}' is not in this model, so this category was not tagged " +
                                "(no other type is used in its place).";
            }
            r.Shared = enabled.Count(o => o.Category == rule.Category && !o.TagsMaterial()) > 1;
            if (r.Problem != null)
                rows.Add(DrwRows.Row(Columns, "", "", "", r.CategoryName, rule.TagType, "Failed", r.Problem));
            list.Add(r);
        }
        if (enabled.Count == 0 && !preset.RepairMaterialTags)
            rows.Add(DrwRows.Row(Columns, "", "", "", "", "", "Failed", "The preset has no ticked rule."));
        return list;
    }

    /// <summary>Revit's API cannot link a new material tag to the material under it (tried every
    /// way on Revit 2026: points, leaders, heights, references) — only a tag placed or touched in
    /// Revit reads. The tool puts it in the right place; the person touches it.</summary>
    public const string UntouchedNote =
        "Shows '?' until touched in Revit: Revit's API cannot link a new material tag to its material. Select it and nudge it once.";

    private const string CoarseNote =
        "The view is Coarse, which shows no wall layers, so a material tag would read '?'. Set the view to Medium or Fine.";
}
