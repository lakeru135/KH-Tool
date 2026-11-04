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
internal static class PresetTagger
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

    private static void TagView(Document doc, View view, List<Resolved> rules, bool dryRun, bool repair,
        List<IReadOnlyDictionary<string, string>> rows, List<ElementId>? materialTags)
    {
        string kind = KindOf(view);
        double paperMm = DrwUnits.FromMillimeters(1) * Math.Max(1, view.Scale);
        double tolerance = 0.2 * paperMm;
        XYZ right = view.RightDirection, up = view.UpDirection;
        bool plan = view is ViewPlan;
        bool coarse = view.DetailLevel == ViewDetailLevel.Coarse;

        var pending = new List<Pending>();
        var viewRows = new List<(Pending? Item, IReadOnlyDictionary<string, string> Row)>();

        IReadOnlyDictionary<string, string> Row(Element e, Resolved r, string status, string note, string face = "", FamilySymbol? type = null) =>
            DrwRows.Row(Columns, kind, view.Name, face.Length > 0 ? $"{Label(e)} — {face}" : Label(e), r.CategoryName,
                type != null ? DrwLookup.TypeLabel(type) : r.Material ? MaterialLabel(r.Rule) : r.Rule.TagType, status, note);
        IReadOnlyDictionary<string, string> RowOf(Pending p, string status, string note) =>
            Row(p.Element, p.Rule, status, note, p.Face, p.Symbol);

        using Transaction t = DrwWarningSwallower.Start(doc, "Preset Tagger");

        // 0. Wall material tags that read "?", put back into their finish layer.
        bool moved = false;
        if (repair && view.ViewType != ViewType.Elevation)
            foreach ((string element, string type, string status, string note) in Repair(doc, view, coarse, paperMm, out moved))
                viewRows.Add((null, DrwRows.Row(Columns, kind, view.Name, element, "Material Tags", type, status, note)));

        Dictionary<ElementId, List<TagInfo>> tagged = ExistingTags(doc, view);

        void Remember(Element e, Element tag, bool material, XYZ? head)
        {
            if (!tagged.TryGetValue(e.Id, out List<TagInfo>? infos)) tagged[e.Id] = infos = new List<TagInfo>();
            infos.Add(new TagInfo { Family = (doc.GetElement(tag.GetTypeId()) as ElementType)?.FamilyName ?? "", Material = material, Head = head });
        }

        // Walls and columns read for which faces are seen, once per view.
        var solids = new Dictionary<ElementId, List<Solid>>();

        // 1. Every tag at its preferred spot.
        foreach (Resolved r in rules)
        {
            if (IsSpatial(r.Category) && !plan) continue;   // rooms and spaces are tagged in plans
            if (r.Material)
            {
                // A section uses both: the walls it cuts are tagged on the cut, the walls seen beyond
                // it as an elevation sees them.
                if (view.ViewType == ViewType.Elevation) PlanElevation(r);
                else
                {
                    PlanMaterial(r);
                    if (view.ViewType == ViewType.Section && r.Elevation != null) PlanElevation(r);
                }
                continue;
            }
            string family = r.Tag!.FamilyName;
            foreach (Element e in Candidates(doc, view, r).ToList())
            {
                if (tagged.TryGetValue(e.Id, out List<TagInfo>? infos) && infos.Any(i => !i.Material && (!r.Shared || i.Family == family)))
                {
                    viewRows.Add((null, Row(e, r, "Skipped", "Already tagged in this view.")));
                    continue;
                }
                XYZ? anchor = Anchor(e, view);
                if (anchor == null)
                {
                    viewRows.Add((null, Row(e, r, "Skipped", "No point to tag from (unplaced or not enclosed).")));
                    continue;
                }
                var p = new Pending { Rule = r, Element = e, Anchor = anchor, Symbol = r.Tag, Position = r.Rule.Position };
                if (r.Rule.PositionMode == TagPositionMode.Element)
                {
                    if (IsSpatial(r.Category))
                    {
                        p.Position = TagPosition.Center;
                        p.Note = "Rooms and spaces have no exterior side; placed on the room, by the view.";
                    }
                    else PlanSide(view, p, r.Rule.Side);
                }
                p.First = p.Out != null
                    ? anchor + p.Out * (p.Edge + r.Rule.DistanceMm * paperMm)
                    : anchor + Offset(p.Position, r.Rule.DistanceMm * paperMm, right, up);
                try
                {
                    Element? tag = Create(doc, view, e, r, p.First);
                    if (tag == null)
                    {
                        viewRows.Add((null, Row(e, r, "Failed", "Revit did not create the tag.")));
                        continue;
                    }
                    p.Tag = tag;
                    pending.Add(p);
                    viewRows.Add((p, Row(e, r, "Created", "")));
                    Remember(e, tag, false, null);
                }
                catch (Exception ex)
                {
                    viewRows.Add((null, Row(e, r, "Failed", ex.Message)));
                }
            }
        }

        // A wall material rule: one tag per finish face, its point 5 mm in from the finish face
        // (never on a layer boundary, never with a leader — both read "?"), and the type whose
        // triangle sits on the side of the face the view shows.
        void PlanMaterial(Resolved r)
        {
            bool perPiece = r.Rule.MaterialPerPiece;
            // A section with an elevation type: the walls it does not cut are the elevation's.
            bool beyondIsElevation = view.ViewType == ViewType.Section && r.Elevation != null;
            double minPiece = DrwUnits.FromMillimeters(Math.Max(0, r.Rule.MinPieceMm));
            // Every seen face of every wall first, so tags can line up across walls.
            var work = new List<FaceWork>();
            foreach (Element e in Candidates(doc, view, r).ToList())
            {
                if (e is not Wall w) continue;
                if (beyondIsElevation && !CutBySection(view, w)) continue;
                foreach (TagSide side in FacesOf(r.Rule.Faces))
                {
                    string faceName = side == TagSide.Exterior ? "exterior face" : "interior face";
                    if (coarse)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", CoarseNote, faceName)));
                        continue;
                    }
                    if (FaceOf(doc, view, w, side, out WallFace face, solids) is string why)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", why, faceName)));
                        continue;
                    }
                    (double ux, double uy) = Flat(face.Out, right, up);
                    if (Math.Sqrt(ux * ux + uy * uy) < 0.3)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", "This face is seen face-on in this view, so it shows no layers to tag.", faceName)));
                        continue;
                    }
                    TagPosition sits = Math.Abs(ux) >= Math.Abs(uy)
                        ? (ux > 0 ? TagPosition.Right : TagPosition.Left)
                        : (uy > 0 ? TagPosition.Above : TagPosition.Below);
                    FamilySymbol? sym = r.Materials.TryGetValue(sits, out FamilySymbol? s) ? s : null;
                    if (sym == null)
                    {
                        viewRows.Add((null, Row(e, r, "Failed", $"No material tag type is set for '{SitsName(sits)}', the side this face needs here.", faceName)));
                        continue;
                    }
                    // Only faces that are seen: another wall or a column against the face covers it
                    // (wholly: a finish drawn as its own wall) or cuts it into pieces (a wall meeting it).
                    if (r.Rule.MaterialSeenOnly)
                    {
                        Covers(doc, view, w, face, solids);
                        if (face.NotSeen(DrwUnits.FromMillimeters(SeenGapMm)))
                        {
                            Element? by = doc.GetElement(face.Covers.OrderByDescending(c => c.B - c.A).First().By);
                            viewRows.Add((null, Row(e, r, "Skipped",
                                $"Not a seen face: {(by != null ? Label(by) : "another element")} is against it (a finish drawn as its own " +
                                "wall, or a wall built up in layers), so it is not tagged.", faceName, sym)));
                            continue;
                        }
                    }
                    var pieces = perPiece ? face.Pieces(minPiece) : face.Whole(minPiece);
                    if (pieces.Count == 0)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped",
                            $"No piece of the face between openings and walls is at least {Math.Max(0, r.Rule.MinPieceMm):0} mm long.", faceName, sym)));
                        continue;
                    }
                    work.Add(new FaceWork { Wall = w, Side = side, Face = face, Sits = sits, Symbol = sym, Name = faceName, Pieces = pieces, Turn = Turn(sits, ux, uy) });
                }
            }

            Dictionary<(FaceWork, int), (string Group, List<XYZ> Spots)> paired = PairAcross(work, !perPiece);

            foreach (FaceWork fw in work)
            {
                Wall w = fw.Wall;
                WallFace face = fw.Face;
                FamilySymbol sym = fw.Symbol;
                for (int k = 0; k < fw.Pieces.Count; k++)
                {
                    (double a, double b) = fw.Pieces[k];
                    string pieceName = fw.Pieces.Count > 1 ? $"{fw.Name}, piece {k + 1} of {fw.Pieces.Count}" : fw.Name;
                    // Already tagged: a material tag on this face (and, by piece, within this piece).
                    if (tagged.TryGetValue(w.Id, out List<TagInfo>? infos) && infos.Any(i => i.Material && i.Head != null &&
                            SideOf(doc, view, w, i.Head) == fw.Side &&
                            (!perPiece || face.Along(i.Head) is var at && at >= a - 1e-6 && at <= b + 1e-6)))
                    {
                        viewRows.Add((null, Row(w, r, "Skipped",
                            perPiece ? "This piece of the face already has a material tag in this view."
                                     : "This face already has a material tag in this view.", pieceName, sym)));
                        continue;
                    }
                    List<XYZ> spots;
                    string group;
                    if (paired.TryGetValue((fw, k), out var pair))
                    {
                        spots = pair.Spots;
                        group = pair.Group;
                    }
                    else
                    {
                        spots = face.Distances(a, b, !perPiece).Select(face.At).ToList();
                        group = $"{w.Id.Value}:{fw.Side}:{k}";
                    }
                    if (spots.Count == 0)
                    {
                        viewRows.Add((null, Row(w, r, "Skipped", "Every spot along the wall is in an opening or against another wall or column.", pieceName, sym)));
                        continue;
                    }
                    var p = new Pending
                    {
                        Rule = r, Element = w, Anchor = spots[0], First = spots[0], Alternates = spots.Skip(1).ToList(),
                        Fixed = true, Out = face.Out, LayerMaterial = face.MaterialAt((a + b) / 2), Position = fw.Sits, Face = pieceName, Symbol = sym,
                        Group = group, Side = fw.Side
                    };
                    try
                    {
                        if (!sym.IsActive) sym.Activate();
                        // A wall off the view's axes: the tag turns with it, so its triangle still points straight at the face.
                        IndependentTag? tag = IndependentTag.Create(doc, sym.Id, view.Id, new Reference(w), false,
                            fw.Turn == 0 ? TagOrientation.Horizontal : TagOrientation.AnyModelDirection, p.Anchor);
                        if (tag == null)
                        {
                            viewRows.Add((null, Row(w, r, "Failed", "Revit did not create the tag.", pieceName, sym)));
                            continue;
                        }
                        if (fw.Turn != 0) tag.RotationAngle = fw.Turn;
                        p.Tag = tag;
                        pending.Add(p);
                        viewRows.Add((p, RowOf(p, "Created", "")));
                        Remember(w, tag, true, p.Anchor);
                    }
                    catch (Exception ex)
                    {
                        viewRows.Add((null, Row(w, r, "Failed", ex.Message, pieceName, sym)));
                    }
                }
            }
        }

        // A wall material rule in an elevation: one tag on each surface of every face the elevation
        // looks at (each surface of a split or painted face its own), upright, of the elevation type
        // (the triangle points up at it), on a spot clear of openings and of anything in front.
        void PlanElevation(Resolved r)
        {
            FamilySymbol? sym = r.Elevation;
            XYZ toViewer = view.ViewDirection;
            double mm = DrwUnits.FromMillimeters(1);
            List<(Element E, BoundingBoxXYZ Box)>? blockers = null;
            foreach (Element e in Candidates(doc, view, r).ToList())
            {
                if (e is not Wall w) continue;
                if (view.ViewType == ViewType.Section && CutBySection(view, w)) continue;   // tagged on the cut
                foreach (TagSide side in FacesOf(r.Rule.Faces))
                {
                    string faceName = side == TagSide.Exterior ? "exterior face" : "interior face";
                    if (SideFace(w, side) is not PlanarFace pf)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", "The wall's face could not be read (only straight walls are material-tagged for now).", faceName)));
                        continue;
                    }
                    double facing = pf.FaceNormal.DotProduct(toViewer);
                    if (facing <= 0.05) continue;   // faces away: the elevation sees the wall's other face
                    if (facing < 0.5)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", "This face is seen at a glancing angle in this elevation, so it is not tagged.", faceName)));
                        continue;
                    }
                    if (sym == null)
                    {
                        viewRows.Add((null, Row(e, r, "Failed", "No material tag type is set for elevations.", faceName)));
                        continue;
                    }
                    blockers ??= Blockers(doc, view);
                    List<(List<Face> Faces, ElementId Material)> surfaces = Surfaces(doc, w, pf);
                    for (int k = 0; k < surfaces.Count; k++)
                    {
                        (List<Face> faces, ElementId material) = surfaces[k];
                        string pieceName = surfaces.Count > 1 ? $"{faceName}, surface {k + 1} of {surfaces.Count}" : faceName;
                        if (tagged.TryGetValue(w.Id, out List<TagInfo>? infos) &&
                            infos.Any(i => i.Material && i.Head != null && faces.Any(f => OnSurface(f, i.Head, 100 * mm))))
                        {
                            viewRows.Add((null, Row(w, r, "Skipped", "This surface already has a material tag in this view.", pieceName, sym)));
                            continue;
                        }
                        List<XYZ> spots = SurfaceSpots(faces).Where(p => InCrop(view, p)).ToList();
                        if (spots.Count == 0)
                        {
                            viewRows.Add((null, Row(w, r, "Skipped", "No spot on this surface is clear of its edges and openings inside the crop.", pieceName, sym)));
                            continue;
                        }
                        Element? hiddenBy = null;
                        var clear = new List<XYZ>();
                        foreach (XYZ p in spots)
                        {
                            if (Hidden(p, toViewer, view, blockers, solids, out Element? by)) hiddenBy ??= by;
                            else clear.Add(p);
                            if (clear.Count >= 16) break;
                        }
                        if (clear.Count == 0)
                        {
                            viewRows.Add((null, Row(w, r, "Skipped",
                                $"Hidden in this elevation behind {(hiddenBy != null ? Label(hiddenBy) : "another element")}, so it is not tagged.", pieceName, sym)));
                            continue;
                        }
                        var p0 = new Pending
                        {
                            Rule = r, Element = w, Anchor = clear[0], First = clear[0], Alternates = clear.Skip(1).ToList(),
                            Fixed = true, Out = pf.FaceNormal, LayerMaterial = material, Position = TagPosition.Below, Face = pieceName, Symbol = sym
                        };
                        try
                        {
                            if (!sym.IsActive) sym.Activate();
                            IndependentTag? tag = IndependentTag.Create(doc, sym.Id, view.Id, new Reference(w), false, TagOrientation.Horizontal, p0.Anchor);
                            if (tag == null)
                            {
                                viewRows.Add((null, Row(w, r, "Failed", "Revit did not create the tag.", pieceName, sym)));
                                continue;
                            }
                            p0.Tag = tag;
                            pending.Add(p0);
                            viewRows.Add((p0, RowOf(p0, "Created", "")));
                            Remember(w, tag, true, p0.Anchor);
                        }
                        catch (Exception ex)
                        {
                            viewRows.Add((null, Row(w, r, "Failed", ex.Message, pieceName, sym)));
                        }
                    }
                }
            }
        }

        if (pending.Count == 0)
        {
            Finish(viewRows.Select(v => (v.Item, v.Row)).ToList());
            return;
        }

        // 2. Measure, then settle each tag on the first spot clear of everything already there.
        doc.Regenerate();
        var mine = new HashSet<ElementId>(pending.Select(p => p.Tag.Id));
        List<Box> obstacles = Obstacles(doc, view, mine);
        var existing = new List<Box>(obstacles);
        var notes = new Dictionary<Pending, string>();
        var dropped = new HashSet<Pending>();
        // Which material tag each accepted box belongs to (by its index in obstacles): tags back to
        // back across a wall never count each other.
        var owner = new Dictionary<int, Pending>();
        bool Blocked(Box at, Pending? p) =>
            obstacles.Where((o, i) => p == null || !owner.TryGetValue(i, out Pending? q) || !OppositeFaces(p, q))
                .Any(o => o.Overlaps(at, tolerance));
        void Accept(Box at, Pending? p)
        {
            if (p?.Side != null) owner[obstacles.Count] = p;
            obstacles.Add(at);
        }
        Box? Rel(Pending p)
        {
            if (BoxOf(p.Tag, view) is not Box b) return null;
            (double fx, double fy) = Flat(p.First, right, up);
            return b.Shift(-fx, -fy);
        }

        // Both faces of a piece: the first spot along the wall where both are clear, so they line up.
        var settled = new HashSet<Pending>();
        foreach (var pair in pending.Where(p => p.Fixed && p.Group.Length > 0).GroupBy(p => p.Group).Where(g => g.Count() == 2))
        {
            Pending p1 = pair.First(), p2 = pair.Last();
            if (Rel(p1) is not Box r1 || Rel(p2) is not Box r2) continue;
            var a1 = new List<XYZ> { p1.Anchor }; a1.AddRange(p1.Alternates);
            var a2 = new List<XYZ> { p2.Anchor }; a2.AddRange(p2.Alternates);
            for (int i = 0; i < Math.Min(a1.Count, a2.Count); i++)
            {
                (double x1, double y1) = Flat(a1[i], right, up);
                (double x2, double y2) = Flat(a2[i], right, up);
                Box b1 = r1.Shift(x1, y1), b2 = r2.Shift(x2, y2);
                if (Blocked(b1, p1) || Blocked(b2, p2)) continue;
                Accept(b1, p1);
                Accept(b2, p2);
                if (!a1[i].IsAlmostEqualTo(p1.First)) Move(doc, p1, a1[i]);
                if (!a2[i].IsAlmostEqualTo(p2.First)) Move(doc, p2, a2[i]);
                if (i > 0)
                    notes[p1] = notes[p2] = "Moved along the wall, with the other face's tag: the first spot overlaps other annotation.";
                settled.Add(p1);
                settled.Add(p2);
                break;
            }
        }

        foreach (Pending p in pending)
        {
            if (settled.Contains(p)) continue;
            Box? measured = BoxOf(p.Tag, view);
            if (measured is not Box box)
            {
                notes[p] = "Its size could not be read, so overlaps were not checked.";
                continue;
            }
            (double fx, double fy) = Flat(p.First, right, up);
            // The tag's box relative to its head point, so it can be tried at any spot.
            Box rel = box.Shift(-fx, -fy);
            double d = p.Rule.Rule.DistanceMm * paperMm;
            XYZ? head = null;
            if (p.Out == null)
            {
                (double ax, double ay) = Flat(p.Anchor, right, up);
                foreach (TagPosition pos in Order(p.Position))
                {
                    (double ux, double uy) = Unit(pos);
                    double start = pos == p.Position ? d : Math.Max(d, -rel.Low(ux, uy) + GapMm * paperMm);
                    if (Clear(ux, uy, start, rel, ax, ay, obstacles, paperMm, tolerance) is not (double dx, double dy, double along)) continue;
                    head = p.Anchor + right * dx + up * dy;
                    if (pos != p.Position)
                        notes[p] = $"Moved {Word(pos)}: the preferred spot overlaps other annotation.";
                    else if (along > start + tolerance)
                        notes[p] = $"Moved {(along - start) / paperMm:0.#} mm further out: the preferred spot overlaps other annotation.";
                    Accept(rel.Shift(ax + dx, ay + dy), null);
                    break;
                }
            }
            else
            {
                // The element's own side only — an exterior tag never jumps to the interior.
                (double nx, double ny) = Flat(p.Out, right, up);
                double len = Math.Sqrt(nx * nx + ny * ny);
                double ux = nx / len, uy = ny / len;
                var anchors = new List<XYZ> { p.Anchor };
                anchors.AddRange(p.Alternates);
                for (int i = 0; i < anchors.Count && head == null; i++)
                {
                    (double ax, double ay) = Flat(anchors[i], right, up);
                    if (p.Fixed)
                    {
                        Box at = rel.Shift(ax, ay);
                        if (Blocked(at, p)) continue;
                        head = anchors[i];
                        Accept(at, p);
                    }
                    else
                    {
                        double start = p.Edge * len - rel.Low(ux, uy) + d;
                        if (Clear(ux, uy, start, rel, ax, ay, obstacles, paperMm, tolerance) is not (double dx, double dy, double along)) continue;
                        head = anchors[i] + right * dx + up * dy;
                        if (along > start + tolerance)
                            notes[p] = $"Moved {(along - start) / paperMm:0.#} mm further out: the first spot overlaps other annotation.";
                        Accept(rel.Shift(ax + dx, ay + dy), null);
                    }
                    if (i > 0) notes[p] = "Moved along the wall: the first spot overlaps other annotation.";
                }
            }
            if (head == null)
            {
                doc.Delete(p.Tag.Id);
                dropped.Add(p);
                continue;
            }
            if (!head.IsAlmostEqualTo(p.First)) Move(doc, p, head);
        }

        // 3. Read every kept tag back — that it exists, and where Revit actually drew it. A tag with
        // a leader is not checked: its box takes in the leader line. A material tag must also read
        // its material, not "?".
        doc.Regenerate();
        var drawn = new Dictionary<Pending, Box>();
        foreach (Pending p in pending)
            if (!dropped.Contains(p) && !p.Rule.Rule.Leader && p.Tag.IsValidObject && BoxOf(p.Tag, view) is Box b) drawn[p] = b;
        var final = new List<(Pending? Item, IReadOnlyDictionary<string, string> Row)>();
        foreach ((Pending? p, IReadOnlyDictionary<string, string> row) in viewRows)
        {
            if (p == null) { final.Add((null, row)); continue; }
            if (dropped.Contains(p))
            {
                final.Add((p, RowOf(p, "Skipped", p.Out != null
                    ? $"Every spot on its {(p.Fixed ? "face" : SideWord(p))} overlaps other annotation; no tag placed."
                    : "Every position overlaps other annotation; no tag placed.")));
                continue;
            }
            if (!p.Tag.IsValidObject || doc.GetElement(p.Tag.Id) == null)
            {
                final.Add((p, RowOf(p, "Failed", "Revit removed the tag.")));
                continue;
            }
            if (p.Fixed && MaterialHint(doc, p.LayerMaterial) is string bad)
            {
                final.Add((p, RowOf(p, "Warning", bad)));
                continue;
            }
            if (drawn.TryGetValue(p, out Box drawnBox) &&
                (existing.Any(o => o.Overlaps(drawnBox, tolerance)) ||
                 drawn.Any(d => d.Key != p && !OppositeFaces(p, d.Key) && d.Value.Overlaps(drawnBox, tolerance))))
            {
                final.Add((p, RowOf(p, "Warning", "Tagged, but as Revit drew it the tag overlaps other annotation.")));
                continue;
            }
            string note = string.Join(" ", new[] { p.Note, notes.TryGetValue(p, out string? n) ? n : "", p.Fixed ? UntouchedNote : "" }
                .Where(s => s.Length > 0));
            final.Add((p, RowOf(p, "Created", note)));
        }
        Finish(final);

        void Finish(List<(Pending? Item, IReadOnlyDictionary<string, string> Row)> list)
        {
            bool changed = pending.Count > 0 || moved;
            if (dryRun || !changed)
            {
                t.RollBack();
                rows.AddRange(list.Select(f =>
                    f.Item != null && f.Row["Status"] == "Created" ? With(f.Row, "Would create")
                    : f.Row["Status"] == "Repaired" ? With(f.Row, "Would repair")
                    : f.Row));
                return;
            }
            TransactionStatus status = t.Commit();
            foreach ((Pending? p, IReadOnlyDictionary<string, string> row) in list)
            {
                // A commit is not proof of the edit: the tag must still be there afterwards.
                if (p != null && row["Status"] is "Created" or "Warning" && (status != TransactionStatus.Committed || doc.GetElement(p.Tag.Id) == null))
                    rows.Add(With(row, "Failed", status != TransactionStatus.Committed ? $"The change was not kept ({status})." : "Revit removed the tag."));
                else if (p == null && row["Status"] == "Repaired" && status != TransactionStatus.Committed)
                    rows.Add(With(row, "Failed", $"The change was not kept ({status})."));
                else
                {
                    rows.Add(row);
                    if (p is { Fixed: true } && row["Status"] is "Created" or "Warning") materialTags?.Add(p.Tag.Id);
                }
            }
        }
    }

    /// <summary>Material tags back to back: on the two faces of one wall, or of a wall and the
    /// finish wall against it — each behind the other's face, pointing away. Their ink is on
    /// opposite sides of the walls; only their families' boxes overlap.</summary>
    private static bool OppositeFaces(Pending p, Pending q) =>
        p.Side != null && q.Side != null && p.Out != null && q.Out != null && p.Out.DotProduct(q.Out) < -0.9 &&
        (q.First - p.First).DotProduct(p.Out) < 0 && (p.First - q.First).DotProduct(q.Out) < 0;

    private static IEnumerable<TagSide> FacesOf(MaterialFaces f) => f switch
    {
        MaterialFaces.Exterior => new[] { TagSide.Exterior },
        MaterialFaces.Interior => new[] { TagSide.Interior },
        _ => new[] { TagSide.Exterior, TagSide.Interior }
    };

    private static string SitsName(TagPosition p) => p switch
    {
        TagPosition.Above => "Sits above",
        TagPosition.Below => "Sits below",
        TagPosition.Left => "Sits left",
        _ => "Sits right"
    };

    private static string SideWord(Pending p)
    {
        bool wall = p.Element is Wall || (p.Element is FamilyInstance fi && fi.Host is Wall);
        return p.Rule.Rule.Side == TagSide.Exterior ? (wall ? "exterior side" : "front") : (wall ? "interior side" : "back");
    }

    /// <summary>
    /// Element mode: the outward direction of the element's chosen side and how far its face is
    /// from the anchor. A wall, or a door or window in one, uses that wall's face (its exterior is
    /// the wall's exterior, however the door is flipped); any other family uses its facing side. An
    /// element with neither, or whose side faces the view, is placed above it by the view instead.
    /// </summary>
    private static void PlanSide(View view, Pending p, TagSide side)
    {
        Wall? wall = p.Element as Wall ?? (p.Element as FamilyInstance)?.Host as Wall;
        XYZ? n = null;
        double edge = 0;
        if (wall != null && SideFace(wall, side) is PlanarFace pf)
        {
            n = Level(pf.FaceNormal);
            edge = Math.Max(0, (pf.Origin - p.Anchor).DotProduct(n));
            if (p.Element is Wall && wall.Location is LocationCurve { Curve: Line line })
                p.Alternates = new List<XYZ> { line.Evaluate(0.25, true), line.Evaluate(0.75, true) };
        }
        else if (p.Element is FamilyInstance fi && fi.FacingOrientation is XYZ facing && !facing.IsZeroLength())
        {
            n = side == TagSide.Exterior ? facing.Normalize() : facing.Normalize().Negate();
            if (fi.get_BoundingBox(view) is BoundingBoxXYZ bb)
                foreach (double x in new[] { bb.Min.X, bb.Max.X })
                foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                    edge = Math.Max(edge, (bb.Transform.OfPoint(new XYZ(x, y, z)) - p.Anchor).DotProduct(n));
        }
        if (n == null)
        {
            p.Position = TagPosition.Above;
            p.Note = "It has no exterior or interior side, so the tag was placed above it, by the view.";
            return;
        }
        (double ux, double uy) = Flat(n, view.RightDirection, view.UpDirection);
        if (Math.Sqrt(ux * ux + uy * uy) < 0.3)
        {
            p.Position = TagPosition.Above;
            p.Note = "Its side faces the view, so the tag was placed above it, by the view.";
            p.Alternates.Clear();
            return;
        }
        p.Out = n;
        p.Edge = edge;
    }

    private static XYZ Level(XYZ v)
    {
        var flat = new XYZ(v.X, v.Y, 0);
        return flat.IsZeroLength() ? v : flat.Normalize();
    }

    /// <summary>The wall's planar side face on that side, or null (a curved wall).</summary>
    private static PlanarFace? SideFace(Wall w, TagSide side)
    {
        try
        {
            foreach (Reference r in HostObjectUtils.GetSideFaces(w, side == TagSide.Exterior ? ShellLayerType.Exterior : ShellLayerType.Interior))
                if (w.GetGeometryObjectFromReference(r) is PlanarFace pf && Math.Abs(pf.FaceNormal.Z) < 0.1) return pf;
        }
        catch { /* no faces to read */ }
        return null;
    }

    /// <summary>How far in from the finish face a material tag's point goes, model mm; a layer
    /// thinner than twice this gets its middle instead, so the point stays inside it.</summary>
    public const double FacePointMm = 5;

    /// <summary>From a finish face into its layer of <paramref name="width"/>: where the tag's point goes.</summary>
    private static double FacePoint(double width) => Math.Min(DrwUnits.FromMillimeters(FacePointMm), width / 2);

    /// <summary>One finish face of a straight wall: the line its tag points run along, 5 mm in from
    /// the face. In a plan that line runs along the wall; in a section it runs up the wall where the
    /// section cuts it.</summary>
    private sealed class WallFace
    {
        /// <summary>Where the tag line starts, on the wall's location line (a plan: its start; a
        /// section: where the section crosses it, at the wall's bottom).</summary>
        public XYZ Origin = null!;
        /// <summary>Which way the tag line runs: along the wall (a plan) or up it (a section).</summary>
        public XYZ Dir = null!;
        public double Length;
        /// <summary>The wall's direction along its location line.</summary>
        public XYZ WallDir = null!;
        /// <summary>A section: its plane, which every tag point is kept on (null in a plan).</summary>
        public XYZ? CutOrigin, CutNormal;
        /// <summary>The face's outward normal, level.</summary>
        public XYZ Out = null!;
        /// <summary>A point on the face.</summary>
        public XYZ Plane = null!;
        /// <summary>From the location line along <see cref="Out"/> to the tag points, inside the finish layer.</summary>
        public double Inset;
        /// <summary>The same for a painted surface: just inside it, so the tag reads the paint.</summary>
        public double PaintInset;
        public ElementId Material = ElementId.InvalidElementId;
        /// <summary>Door and window openings, as distances along the line.</summary>
        public List<(double A, double B)> Openings = new();
        /// <summary>Stretches of this face another wall or a column is against, as distances along
        /// the line, merged; <c>By</c> is the element covering the most of each.</summary>
        public List<(double A, double B, ElementId By)> Covers = new();
        /// <summary>The surfaces along the line when the face is split or painted: each stretch of one
        /// material (empty = one surface, the layer's own material).</summary>
        public List<(double A, double B, ElementId Material, bool Painted)> Runs = new();

        public bool Section => CutNormal != null;

        /// <summary>The point <paramref name="s"/> along the line, <paramref name="off"/> out from the
        /// location line; in a section, slid along the wall onto the section plane.</summary>
        public XYZ OnLine(double s, double off)
        {
            XYZ c = Origin + Dir * s + Out * off;
            if (CutNormal != null && CutOrigin != null)
            {
                double d = WallDir.DotProduct(CutNormal);
                if (Math.Abs(d) > 1e-9) c += WallDir * ((CutOrigin - c).DotProduct(CutNormal) / d);
            }
            return c;
        }

        public XYZ At(double s) => OnLine(s, RunAt(s) is { Painted: true } ? PaintInset : Inset);

        private (double A, double B, ElementId Material, bool Painted)? RunAt(double s)
        {
            foreach (var r in Runs)
                if (s >= r.A && s <= r.B) return r;
            return null;
        }

        /// <summary>The material a tag at <paramref name="s"/> should read.</summary>
        public ElementId MaterialAt(double s) => RunAt(s)?.Material ?? Material;

        public bool InOpening(double s) => Openings.Any(o => s > o.A && s < o.B);

        /// <summary>In an opening, or against another wall or column.</summary>
        public bool InCut(double s) => InOpening(s) || Covers.Any(c => s > c.A && s < c.B);

        /// <summary>Not a face anyone sees: covered from end to end, but for openings (a finish wall
        /// stops at the door through both) and stretches shorter than <paramref name="gap"/>.</summary>
        public bool NotSeen(double gap)
        {
            if (Covers.Count == 0) return false;
            double from = 0;
            foreach ((double a, double b) in Openings.Concat(Covers.Select(c => (c.A, c.B))).OrderBy(x => x.A))
            {
                if (a - from >= gap) return false;
                from = Math.Max(from, b);
            }
            return Length - from < gap;
        }

        /// <summary>How far along the line a point is.</summary>
        public double Along(XYZ p) => (p - Origin).DotProduct(Dir);

        /// <summary>The pieces of the face between openings and the walls or columns against it, at
        /// least <paramref name="minLength"/> long; each surface of a split face its own piece.</summary>
        public List<(double A, double B)> Pieces(double minLength)
        {
            var cuts = Openings.Concat(Covers.Select(c => (c.A, c.B)))
                .Select(o => (A: Math.Max(0, o.A), B: Math.Min(Length, o.B))).Where(o => o.B > o.A)
                .OrderBy(o => o.A).ToList();
            var pieces = new List<(double A, double B)>();
            double from = 0;
            foreach ((double a, double b) in cuts)
            {
                if (a > from) pieces.Add((from, a));
                from = Math.Max(from, b);
            }
            if (Length > from) pieces.Add((from, Length));
            return BySurface(pieces, minLength);
        }

        /// <summary>The whole face as one piece — or, split or painted in stretches, one per surface
        /// (each surface gets its own tag), those at least <paramref name="minLength"/> long.</summary>
        public List<(double A, double B)> Whole(double minLength) =>
            Runs.Count > 1 ? BySurface(new List<(double A, double B)> { (0, Length) }, minLength) : new List<(double A, double B)> { (0, Length) };

        private List<(double A, double B)> BySurface(List<(double A, double B)> pieces, double minLength)
        {
            var split = new List<(double A, double B)>();
            List<double> bounds = Runs.Skip(1).Select(r => r.A).ToList();
            foreach ((double a, double b) in pieces)
            {
                double from = a;
                foreach (double x in bounds.Where(x => x > a && x < b))
                {
                    split.Add((from, x));
                    from = x;
                }
                split.Add((from, b));
            }
            return split.Where(p => p.B - p.A >= Math.Max(minLength, 1e-6)).ToList();
        }

        private static readonly double[] PieceSpots = { 0.5, 1 / 3.0, 2 / 3.0, 0.25, 0.75 };
        private static readonly double[] WallSpots = { 0.5, 1 / 3.0, 2 / 3.0, 0.25, 0.75, 0.4, 0.6, 0.15, 0.85 };

        /// <summary>Distances along the line to tag at within [a, b], best first: the middle, then out
        /// towards the ends (more of them for a whole wall); none in an opening or against another
        /// wall or column.</summary>
        public List<double> Distances(double a, double b, bool wholeWall) =>
            (wholeWall ? WallSpots : PieceSpots).Select(f => a + f * (b - a)).Where(s => !InCut(s)).ToList();
    }

    /// <summary>The furthest apart two faces can be and still be one wall build-up (a finish wall,
    /// core and finish wall), model mm.</summary>
    private const double MaxBuildUpMm = 600;

    /// <summary>Faces back to back: pointing opposite ways, each behind the other, at most
    /// <see cref="MaxBuildUpMm"/> apart — the two faces of a wall, or of a wall and the finish wall
    /// against it. Two walls facing each other across a room point towards each other instead.</summary>
    private static bool BackToBack(WallFace f, WallFace g)
    {
        if (f.Out.DotProduct(g.Out) > -0.99) return false;
        double d1 = (g.Plane - f.Plane).DotProduct(f.Out), d2 = (f.Plane - g.Plane).DotProduct(g.Out);
        double max = DrwUnits.FromMillimeters(MaxBuildUpMm);
        return d1 < 0 && d2 < 0 && -d1 <= max && -d2 <= max;
    }

    /// <summary>
    /// Lines tags up across a wall without adding any: each piece of a face is paired with at most
    /// one piece back to back with it (the other face of the wall, or of the wall build-up), the
    /// pairs sharing the most wall first, each overlapping by at least half the shorter piece; ties
    /// go to the closest middles, so a face across three equal rooms keeps its middle. A pair gets
    /// one group and the same spots, measured across, so step 2 settles them together. A piece left
    /// over keeps its own spots.
    /// </summary>
    private static Dictionary<(FaceWork, int), (string Group, List<XYZ> Spots)> PairAcross(List<FaceWork> work, bool wholeWall)
    {
        double mm = DrwUnits.FromMillimeters(1);
        var cands = new List<(FaceWork F, int I, FaceWork G, int J, double Lo, double Hi, double Overlap, double Apart)>();
        for (int x = 0; x < work.Count; x++)
            for (int y = x + 1; y < work.Count; y++)
            {
                FaceWork f = work[x], g = work[y];
                if (!BackToBack(f.Face, g.Face)) continue;
                for (int i = 0; i < f.Pieces.Count; i++)
                    for (int j = 0; j < g.Pieces.Count; j++)
                    {
                        (double a, double b) = f.Pieces[i];
                        double g0 = f.Face.Along(g.Face.At(g.Pieces[j].A)), g1 = f.Face.Along(g.Face.At(g.Pieces[j].B));
                        double lo = Math.Min(g0, g1), hi = Math.Max(g0, g1);
                        double overlap = Math.Min(b, hi) - Math.Max(a, lo);
                        if (overlap <= mm || overlap < 0.5 * Math.Min(b - a, hi - lo)) continue;
                        cands.Add((f, i, g, j, Math.Max(a, lo), Math.Min(b, hi), overlap, Math.Abs((a + b) / 2 - (lo + hi) / 2)));
                    }
            }
        var paired = new Dictionary<(FaceWork, int), (string, List<XYZ>)>();
        while (true)
        {
            cands.RemoveAll(x => paired.ContainsKey((x.F, x.I)) || paired.ContainsKey((x.G, x.J)));
            if (cands.Count == 0) break;
            // The most shared wall first; within 20% of it, the closest middles (a face across
            // three rooms lines up with the middle one, though the end rooms run a little longer).
            double most = cands.Max(x => x.Overlap);
            var c = cands.Where(x => x.Overlap >= 0.8 * most).OrderBy(x => x.Apart).First();
            cands.Remove(c);
            List<double> shared = c.F.Face.Distances(c.Lo, c.Hi, wholeWall)
                .Where(s => !c.G.Face.InCut(c.G.Face.Along(c.F.Face.At(s)))).ToList();
            if (shared.Count == 0) continue;
            string group = $"pair:{c.F.Wall.Id.Value}:{c.F.Side}:{c.I}";
            paired[(c.F, c.I)] = (group, shared.Select(c.F.Face.At).ToList());
            paired[(c.G, c.J)] = (group, shared.Select(s => c.G.Face.At(c.G.Face.Along(c.F.Face.At(s)))).ToList());
        }
        return paired;
    }

    /// <summary>One face to tag in <c>PlanMaterial</c>: its wall, side, type and pieces.</summary>
    private sealed class FaceWork
    {
        public Wall Wall = null!;
        public TagSide Side;
        public WallFace Face = null!;
        public TagPosition Sits;
        public FamilySymbol Symbol = null!;
        public string Name = "";
        public List<(double A, double B)> Pieces = new();
        /// <summary>How far the tag turns (radians, anticlockwise in the view) to point straight at
        /// the face; 0 for a wall along the view's axes.</summary>
        public double Turn;
    }

    /// <summary>A wall turned less than this off the view's axes keeps its tag upright, degrees.</summary>
    private const double TurnMinDeg = 0.5;

    /// <summary>The turn (radians, anticlockwise in the view, within ±45°) from the side a tag type
    /// sits on to the face's outward direction (<paramref name="ux"/>, <paramref name="uy"/> across the
    /// view), so the tag's triangle points straight at a face off the view's axes; 0 within
    /// <see cref="TurnMinDeg"/>.</summary>
    internal static double Turn(TagPosition sits, double ux, double uy)
    {
        double basis = sits switch
        {
            TagPosition.Right => 0,
            TagPosition.Above => Math.PI / 2,
            TagPosition.Left => Math.PI,
            _ => -Math.PI / 2
        };
        double d = Math.Atan2(uy, ux) - basis;
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d <= -Math.PI) d += 2 * Math.PI;
        return Math.Abs(d) < TurnMinDeg * Math.PI / 180 ? 0 : d;
    }

    /// <summary>How far out from a face another element still counts as against it, model mm: a
    /// finish wall drawn with a small gap.</summary>
    public const double TouchMm = 10;

    /// <summary>A stretch of face shorter than this, model mm, between two covered stretches (or a
    /// covered stretch and the end) counts as covered too: too short to be a face anyone tags.</summary>
    private const double SeenGapMm = 300;

    /// <summary>
    /// Fills <see cref="WallFace.Covers"/>: where the walls and columns in the view are against this
    /// face. Lines along the face 1, 5 and 10 mm out, at the height the view shows (the cut plane in
    /// a plan, mid-height in a section), are run through each neighbour's solids — the solids as
    /// Revit joined them, so a wall butting in reaches through this wall's finish to be found.
    /// </summary>
    private static void Covers(Document doc, View view, Wall w, WallFace face, Dictionary<ElementId, List<Solid>> cache)
    {
        face.Covers.Clear();
        if (w.get_BoundingBox(null) is not BoundingBoxXYZ wb) return;
        double mm = DrwUnits.FromMillimeters(1);
        double z = ShownZ(doc, view, w, w.Location is LocationCurve lc0 ? lc0.Curve.GetEndPoint(0).Z : 0);

        double grow = 20 * mm;
        List<Element> near;
        try
        {
            near = new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(face.Section
                    // A section also counts the floors against the face: one piece per storey.
                    ? new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Floors }
                    : new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns }))
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(wb.Min - new XYZ(grow, grow, grow), wb.Max + new XYZ(grow, grow, grow))))
                .WhereElementIsNotElementType().Where(x => x.Id != w.Id).ToList();
        }
        catch { return; }
        if (near.Count == 0) return;

        double length = face.Length, pad = 10 * mm;
        var hits = new List<(double A, double B, ElementId By)>();
        foreach (double offMm in new[] { 1.0, 5.0, TouchMm })
        {
            XYZ Probe(double s)
            {
                XYZ p = face.OnLine(s, (face.Plane - face.Origin).DotProduct(face.Out) + offMm * mm);
                return face.Section ? p : new XYZ(p.X, p.Y, z);
            }
            Line probe;
            try { probe = Line.CreateBound(Probe(-pad), Probe(length + pad)); }
            catch { continue; }
            foreach (Element n in near)
                foreach (Solid solid in SolidsOf(n, cache))
                {
                    try
                    {
                        SolidCurveIntersection sci = solid.IntersectWithCurve(probe,
                            new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside });
                        for (int i = 0; i < sci.SegmentCount; i++)
                        {
                            Curve c = sci.GetCurveSegment(i);
                            double s0 = face.Along(c.GetEndPoint(0)), s1 = face.Along(c.GetEndPoint(1));
                            hits.Add((Math.Min(s0, s1), Math.Max(s0, s1), n.Id));
                        }
                    }
                    catch { /* a solid that cannot be intersected: not against the face */ }
                }
        }
        if (hits.Count == 0) return;

        // Merge, closing gaps too short to tag (and short ends), clipped to the face.
        double gap = SeenGapMm * mm;
        var merged = new List<(double A, double B, Dictionary<ElementId, double> By)>();
        foreach ((double a, double b, ElementId by) in hits.OrderBy(h => h.A))
        {
            double lo = Math.Max(0, a), hi = Math.Min(length, b);
            if (hi <= lo) continue;
            if (merged.Count > 0 && lo - merged[^1].B < gap)
            {
                var last = merged[^1];
                last.By[by] = (last.By.TryGetValue(by, out double had) ? had : 0) + (hi - lo);
                merged[^1] = (last.A, Math.Max(last.B, hi), last.By);
            }
            else merged.Add((lo, hi, new Dictionary<ElementId, double> { [by] = hi - lo }));
        }
        for (int i = 0; i < merged.Count; i++)
        {
            (double a, double b, Dictionary<ElementId, double> by) = merged[i];
            if (a < gap) a = 0;
            if (length - b < gap) b = length;
            face.Covers.Add((a, b, by.OrderByDescending(kv => kv.Value).First().Key));
        }
    }

    /// <summary>The solids of a wall or column, through family instances, read once per run.</summary>
    private static List<Solid> SolidsOf(Element e, Dictionary<ElementId, List<Solid>> cache)
    {
        if (cache.TryGetValue(e.Id, out List<Solid>? found)) return found;
        var list = new List<Solid>();
        void Walk(GeometryElement? g)
        {
            if (g == null) return;
            foreach (GeometryObject o in g)
                if (o is Solid s && s.Volume > 1e-9) list.Add(s);
                else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
        }
        try { Walk(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })); }
        catch { /* no geometry to read */ }
        cache[e.Id] = list;
        return list;
    }

    private const string NotCutPlanNote =
        "Not cut by this plan's cut plane (the wall is above or below it), so the plan shows no layers to tag.";
    private const string NotCutSectionNote = "Not cut by this section (the wall ends before the section line), so it is not tagged.";

    /// <summary>The finish face of <paramref name="w"/> on <paramref name="side"/>, or why it has none
    /// to tag in this view: a plan tags along the wall at its cut plane, a section up the wall where
    /// the section cuts it; a wall the view does not cut has none.</summary>
    private static string? FaceOf(Document doc, View view, Wall w, TagSide side, out WallFace face,
        Dictionary<ElementId, List<Solid>>? cache = null)
    {
        face = new WallFace();
        if (w.Location is not LocationCurve { Curve: Line line }) return "Only straight walls are material-tagged for now.";
        List<CompoundStructureLayer> layers = w.WallType?.GetCompoundStructure()?.GetLayers().Where(l => l.Width > 1e-6).ToList()
                                              ?? new List<CompoundStructureLayer>();
        if (layers.Count == 0) return "This wall type has no layers (a curtain or stacked wall), so there is no finish to tag.";
        if (SideFace(w, side) is not PlanarFace pf) return "The wall's face could not be read.";
        if (w.get_BoundingBox(null) is not BoundingBoxXYZ wb) return "The wall's size could not be read.";
        CompoundStructureLayer layer = side == TagSide.Exterior ? layers[0] : layers[layers.Count - 1];
        bool plan = view is ViewPlan;
        double mm = DrwUnits.FromMillimeters(1), pad = 50 * mm;
        face.Out = Level(pf.FaceNormal);
        face.Plane = pf.Origin;
        face.Material = layer.MaterialId;
        face.WallDir = line.Direction;
        double surface = (pf.Origin - line.GetEndPoint(0)).DotProduct(face.Out);
        face.Inset = surface - FacePoint(layer.Width);
        face.PaintInset = surface - Math.Min(0.5 * mm, layer.Width / 2);
        double? cut = plan ? CutZ(doc, (ViewPlan)view) : null;
        if (plan)
        {
            if (cut is double cz && (cz <= wb.Min.Z || cz >= wb.Max.Z)) return NotCutPlanNote;
            face.Origin = line.GetEndPoint(0);
            face.Dir = line.Direction;
            face.Length = line.Length;
            try
            {
                foreach (ElementId id in w.FindInserts(true, false, true, true))
                {
                    if (doc.GetElement(id)?.get_BoundingBox(null) is not BoundingBoxXYZ bb) continue;
                    double lo = double.MaxValue, hi = double.MinValue;
                    foreach (double x in new[] { bb.Min.X, bb.Max.X })
                    foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                    {
                        double s = face.Along(bb.Transform.OfPoint(new XYZ(x, y, bb.Min.Z)));
                        lo = Math.Min(lo, s); hi = Math.Max(hi, s);
                    }
                    face.Openings.Add((lo - pad, hi + pad));
                }
            }
            catch { /* no inserts to read */ }
        }
        else
        {
            // A section: where this face's tag line crosses the section plane, up the wall from there.
            XYZ n = view.ViewDirection, o = CutPlane(view);
            double across = line.Direction.DotProduct(n);
            if (Math.Abs(across) < 0.17) return "The wall runs along this section, so the section shows no cut through it.";
            XYZ p0 = line.GetEndPoint(0) + face.Out * face.Inset;
            double t = (o - p0).DotProduct(n) / across;
            if (t < 0 || t > line.Length) return NotCutSectionNote;
            XYZ at = line.GetEndPoint(0) + line.Direction * t;
            if (view.CropBoxActive && view.CropBox is BoundingBoxXYZ crop)
            {
                XYZ local = crop.Transform.Inverse.OfPoint(at);
                if (local.X < crop.Min.X || local.X > crop.Max.X) return "Cut by the section line, but outside the section's crop, so it is not tagged.";
            }
            face.Origin = new XYZ(at.X, at.Y, wb.Min.Z);
            face.Dir = XYZ.BasisZ;
            face.Length = wb.Max.Z - wb.Min.Z;
            face.CutOrigin = o;
            face.CutNormal = n;
            // Only the height the crop shows.
            if (view.CropBoxActive && view.CropBox is BoundingBoxXYZ c2)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (double x in new[] { c2.Min.X, c2.Max.X })
                foreach (double y in new[] { c2.Min.Y, c2.Max.Y })
                {
                    double z = c2.Transform.OfPoint(new XYZ(x, y, 0)).Z;
                    lo = Math.Min(lo, z); hi = Math.Max(hi, z);
                }
                if (hi <= wb.Min.Z || lo >= wb.Max.Z) return "Cut by the section line, but above or below the section's crop, so it is not tagged.";
                if (lo > wb.Min.Z) face.Openings.Add((-pad, lo - wb.Min.Z));
                if (hi < wb.Max.Z) face.Openings.Add((hi - wb.Min.Z, face.Length + pad));
            }
        }

        // Where the wall itself is not, along its tag line (at the plan's cut height): a door or
        // window — also one in a wall this one is joined to, a finish drawn as its own wall, which is
        // not its insert — and a tag there reads nothing. A wall with nothing there is not cut.
        List<Solid> own = SolidsOf(w, cache ?? new Dictionary<ElementId, List<Solid>>());
        if (own.Count > 0)
        {
            double z = ShownZ(doc, view, w, line.GetEndPoint(0).Z), length = face.Length;
            WallFace f = face;
            XYZ P(double s) => plan ? Flat(f.At(s), z) : f.At(s);
            var inside = new List<(double A, double B)>();
            bool read = true;
            try
            {
                Line probe = Line.CreateBound(P(0), P(length));
                foreach (Solid solid in own)
                {
                    SolidCurveIntersection sci = solid.IntersectWithCurve(probe,
                        new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside });
                    for (int i = 0; i < sci.SegmentCount; i++)
                    {
                        Curve c = sci.GetCurveSegment(i);
                        double s0 = face.Along(c.GetEndPoint(0)), s1 = face.Along(c.GetEndPoint(1));
                        inside.Add((Math.Min(s0, s1), Math.Max(s0, s1)));
                    }
                }
            }
            catch { inside.Clear(); read = false; }
            if (read && inside.Count == 0) return plan ? NotCutPlanNote : NotCutSectionNote;
            if (inside.Count > 0)
            {
                double from = 0;
                foreach ((double a, double b) in inside.OrderBy(x => x.A))
                {
                    if (a - from > mm) face.Openings.Add((from - pad, a + pad));
                    from = Math.Max(from, b);
                }
                if (length - from > mm) face.Openings.Add((from - pad, length + pad));
            }
        }

        SurfaceRuns(doc, view, w, pf, face, surface, plan ? ShownZ(doc, view, w, line.GetEndPoint(0).Z) : null);
        return null;
    }

    /// <summary>A split or painted face: the stretches of one material along the tag line, read on
    /// the surface every 10 mm (each a surface that gets its own tag). Nothing for a plain face.</summary>
    private static void SurfaceRuns(Document doc, View view, Wall w, PlanarFace pf, WallFace face, double surface, double? z)
    {
        face.Runs.Clear();
        bool painted(Face f)
        {
            try { return doc.IsPainted(w.Id, f); }
            catch { return false; }
        }
        ElementId materialOf(Face f)
        {
            try { if (doc.IsPainted(w.Id, f)) return doc.GetPaintedMaterial(w.Id, f); }
            catch { /* not paintable */ }
            return face.Material;
        }
        bool split;
        try { split = pf.HasRegions; }
        catch { split = false; }
        if (!split)
        {
            if (painted(pf)) face.Runs.Add((0, face.Length, materialOf(pf), true));
            return;
        }
        List<Face> regions;
        try { regions = pf.GetRegions().Cast<Face>().ToList(); }
        catch { return; }
        var info = regions.Select(r => (Face: r, Material: materialOf(r), Painted: painted(r))).ToList();
        double mm = DrwUnits.FromMillimeters(1), step = 10 * mm;
        int count = Math.Max(2, (int)Math.Ceiling(face.Length / step) + 1);
        var runs = new List<(double A, double B, ElementId Material, bool Painted)>();
        for (int i = 0; i < count; i++)
        {
            double s = Math.Min(face.Length, i * step);
            XYZ q = face.OnLine(s, surface);
            if (z is double zz) q = Flat(q, zz);
            ElementId m = face.Material;
            bool p = false;
            foreach (var r in info)
            {
                IntersectionResult? ir = null;
                try { ir = r.Face.Project(q); }
                catch { /* off the region */ }
                if (ir == null || ir.Distance > 0.5 * mm) continue;
                m = r.Material;
                p = r.Painted;
                break;
            }
            if (runs.Count > 0 && runs[^1].Material == m && runs[^1].Painted == p) runs[^1] = (runs[^1].A, s, m, p);
            else
            {
                if (runs.Count > 0)
                {
                    double mid = (runs[^1].B + s) / 2;
                    runs[^1] = (runs[^1].A, mid, runs[^1].Material, runs[^1].Painted);
                    runs.Add((mid, s, m, p));
                }
                else runs.Add((0, s, m, p));
            }
        }
        if (runs.Count == 0) return;
        runs[^1] = (runs[^1].A, face.Length, runs[^1].Material, runs[^1].Painted);
        if (runs.Count > 1 || runs[0].Painted) face.Runs.AddRange(runs);
    }

    private static XYZ Flat(XYZ p, double z) => new(p.X, p.Y, z);

    /// <summary>A wall a section's cut plane passes through (its body, not only its location line).</summary>
    private static bool CutBySection(View view, Wall w)
    {
        if (w.Location is not LocationCurve { Curve: Curve c }) return false;
        XYZ o = CutPlane(view), n = view.ViewDirection;
        double a = (c.GetEndPoint(0) - o).DotProduct(n), b = (c.GetEndPoint(1) - o).DotProduct(n);
        double half = w.Width / 2;
        return Math.Min(a, b) <= half && Math.Max(a, b) >= -half;
    }

    /// <summary>A point on a section's cut plane, or an elevation's plane: the face of its view box
    /// nearest the viewer. (<c>View.Origin</c> is a corner of the far clip plane, not on the cut.)</summary>
    internal static XYZ CutPlane(View view)
    {
        try
        {
            if (view.CropBox is BoundingBoxXYZ box)
            {
                double x = (box.Min.X + box.Max.X) / 2, y = (box.Min.Y + box.Max.Y) / 2;
                XYZ a = box.Transform.OfPoint(new XYZ(x, y, box.Min.Z)), b = box.Transform.OfPoint(new XYZ(x, y, box.Max.Z));
                return a.DotProduct(view.ViewDirection) > b.DotProduct(view.ViewDirection) ? a : b;
            }
        }
        catch { /* no view box */ }
        return view.Origin;
    }

    /// <summary>A plan's cut height, model feet (null when it cannot be read).</summary>
    private static double? CutZ(Document doc, ViewPlan vp)
    {
        try
        {
            PlanViewRange vr = vp.GetViewRange();
            if (doc.GetElement(vr.GetLevelId(PlanViewPlane.CutPlane)) is Level cl) return cl.ProjectElevation + vr.GetOffset(PlanViewPlane.CutPlane);
            if (vp.GenLevel != null) return vp.GenLevel.ProjectElevation + vr.GetOffset(PlanViewPlane.CutPlane);
        }
        catch { /* no view range */ }
        return null;
    }

    /// <summary>The height a view shows a wall at: the cut plane in a plan, the given height
    /// otherwise; kept inside the wall.</summary>
    private static double ShownZ(Document doc, View view, Wall w, double z)
    {
        if (view is ViewPlan vp && CutZ(doc, vp) is double cut) z = cut;
        double mm = DrwUnits.FromMillimeters(1);
        return w.get_BoundingBox(null) is BoundingBoxXYZ wb ? Math.Max(wb.Min.Z + mm, Math.Min(wb.Max.Z - mm, z)) : z;
    }

    /// <summary>Which face of the wall a point sits on (or is nearest to, inside the wall).</summary>
    private static TagSide? SideOf(Document doc, View view, Wall w, XYZ point)
    {
        PlanarFace? ext = SideFace(w, TagSide.Exterior), inn = SideFace(w, TagSide.Interior);
        if (ext == null || inn == null) return null;
        double e = (point - ext.Origin).DotProduct(Level(ext.FaceNormal));
        double i = (point - inn.Origin).DotProduct(Level(inn.FaceNormal));
        return e >= i ? TagSide.Exterior : TagSide.Interior;
    }

    /// <summary>Why a material tag on this finish would read "?" even once touched, or null. The API
    /// cannot read a material tag's text, so the finish material itself is what is checked.</summary>
    private static string? MaterialHint(Document doc, ElementId? layerMaterial)
    {
        if (layerMaterial == null || layerMaterial == ElementId.InvalidElementId)
            return "The finish layer has no material (By Category), so the tag will read '?'. Give the layer a material.";
        Element? m = doc.GetElement(layerMaterial);
        bool Empty(BuiltInParameter p) => string.IsNullOrWhiteSpace(m?.get_Parameter(p)?.AsString());
        return Empty(BuiltInParameter.ALL_MODEL_DESCRIPTION) && Empty(BuiltInParameter.ALL_MODEL_MARK)
            ? $"The finish material '{m?.Name}' has no Description and no Mark, so the tag will read '?' even once touched. " +
              "Fill in the parameter the tag shows."
            : null;
    }

    /// <summary>The point a material tag reads its material at: its free leader's end, or its head.</summary>
    private static XYZ ReadPoint(IndependentTag tag)
    {
        try
        {
            if (tag.HasLeader && tag.LeaderEndCondition == LeaderEndCondition.Free &&
                tag.GetTaggedReferences().FirstOrDefault() is Reference r)
                return tag.GetLeaderEnd(r);
        }
        catch { /* fall back to the head */ }
        return tag.TagHeadPosition;
    }

    /// <summary>
    /// Wall material tags in the view whose point cannot read a material — off the wall, past its
    /// ends, in a door or window opening, or on the line between two layers — the reasons a tag reads
    /// "?". Each point is moved into the layer on its side (5 mm in from a finish face, else the
    /// layer's middle), keeping its height and its
    /// place along the wall. The API cannot read a tag's text, so a tag whose point is inside a layer
    /// is taken as reading and left alone. A tag placed by hand reads again once moved back (checked
    /// on Revit 2026); one made by a tool still needs a touch in Revit.
    /// </summary>
    private static List<(string Element, string Type, string Status, string Note)> Repair(Document doc, View view, bool coarse, double paperMm,
        out bool moved)
    {
        moved = false;
        var list = new List<(string, string, string, string)>();
        double tol = DrwUnits.FromMillimeters(0.5);
        List<IndependentTag> tags = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
            .Where(t => t.Category?.Id.Value == (long)BuiltInCategory.OST_MaterialTags).ToList();
        foreach (IndependentTag tag in tags)
        {
            Wall? w = tag.GetTaggedLocalElementIds().Select(doc.GetElement).OfType<Wall>().FirstOrDefault();
            if (w == null) continue;   // only wall material tags
            if (w.Location is not LocationCurve { Curve: Line line } ||
                SideFace(w, TagSide.Exterior) is not PlanarFace ext ||
                w.WallType?.GetCompoundStructure()?.GetLayers().Where(l => l.Width > 1e-6).ToList() is not { Count: > 0 } layers)
                continue;   // curved or layerless: nothing to measure against
            string type = doc.GetElement(tag.GetTypeId()) is Element tt ? DrwLookup.TypeLabel(tt) : "";
            string label = $"{Label(w)} — material tag [{tag.Id.Value}]";
            XYZ n = Level(ext.FaceNormal), start = line.GetEndPoint(0);
            XYZ at = ReadPoint(tag);
            double length = line.Length;
            double s = (at - start).DotProduct(line.Direction);
            // Depth from the exterior face, inward; layer k spans [edges[k], edges[k+1]].
            double depth = (ext.Origin - at).DotProduct(n);
            var edges = new List<double> { 0 };
            foreach (CompoundStructureLayer l in layers) edges.Add(edges[edges.Count - 1] + l.Width);
            double total = edges[edges.Count - 1];

            string? why = null;
            int layer = -1;
            if (depth < tol) { why = $"{-depth / DrwUnits.FromMillimeters(1):0} mm outside the exterior face"; layer = 0; }
            else if (depth > total - tol) { why = $"{(depth - total) / DrwUnits.FromMillimeters(1):0} mm outside the interior face"; layer = layers.Count - 1; }
            else
            {
                for (int k = 1; k < edges.Count - 1 && why == null; k++)
                    if (Math.Abs(depth - edges[k]) < tol)
                    {
                        why = "on the line between two layers";
                        // With a leader: the layer on the head's side of that line. Without one (the
                        // point is the head): the layer nearer the face, the finish a tag there means.
                        double headDepth = (ext.Origin - tag.TagHeadPosition).DotProduct(n);
                        bool outward = Math.Abs(headDepth - depth) > tol ? headDepth < edges[k] : edges[k] < total / 2;
                        layer = outward ? k - 1 : k;
                    }
                if (why == null)
                    layer = Enumerable.Range(0, layers.Count).First(k => depth >= edges[k] && depth <= edges[k + 1]);
            }
            if (s < -tol || s > length + tol)
                why = (why == null ? "" : why + ", and ") + "past the end of the wall";
            // Openings: a point in a door or window reads nothing either.
            WallFace? f = view is ViewPlan && FaceOf(doc, view, w, TagSide.Exterior, out WallFace face) == null ? face : null;
            double clamped = Math.Max(0.05 * length, Math.Min(0.95 * length, s));
            if (f != null && f.InOpening(clamped))
            {
                why = (why == null ? "" : why + ", and ") + "in a door or window opening";
                (double a, double b) = f.Openings.First(o => clamped > o.A && clamped < o.B);
                clamped = clamped - a < b - clamped && a > 0 ? a : b < length ? b : a;
            }
            if (why == null) continue;   // inside a layer: taken as reading
            if (coarse)
            {
                list.Add((label, type, "Warning", $"Its point is {why}. " + CoarseNote));
                continue;
            }
            double mid = layer == 0 ? FacePoint(layers[0].Width)
                : layer == layers.Count - 1 ? total - FacePoint(layers[layer].Width)
                : (edges[layer] + edges[layer + 1]) / 2;
            XYZ flat = start + line.Direction * clamped + n * ((ext.Origin - start).DotProduct(n) - mid);
            var target = new XYZ(flat.X, flat.Y, at.Z);
            try
            {
                Reference? reference = tag.GetTaggedReferences().FirstOrDefault();
                if (tag.HasLeader && reference != null)
                {
                    if (tag.LeaderEndCondition != LeaderEndCondition.Free) tag.LeaderEndCondition = LeaderEndCondition.Free;
                    tag.SetLeaderEnd(reference, target);
                }
                else tag.TagHeadPosition = target;
                moved = true;
                Element? material = doc.GetElement(layers[layer].MaterialId);
                list.Add((label, type, "Repaired",
                    $"Its point was {why}; moved into the '{material?.Name ?? "unnamed"}' layer. " +
                    "A tag placed by hand reads again; one made by a tool still needs a touch in Revit."));
            }
            catch (Exception ex)
            {
                list.Add((label, type, "Failed", ex.Message));
            }
        }
        return list;
    }

    // ---- Elevations ---------------------------------------------------------------------------------

    /// <summary>The surfaces of a wall face an elevation shows: each region of a split face, or the
    /// whole face; touching regions of one material are one surface.</summary>
    private static List<(List<Face> Faces, ElementId Material)> Surfaces(Document doc, Wall w, PlanarFace pf)
    {
        List<Face> regions = new() { pf };
        try { if (pf.HasRegions) regions = pf.GetRegions().Cast<Face>().ToList(); }
        catch { /* one surface */ }
        ElementId MaterialOf(Face f)
        {
            try { if (doc.IsPainted(w.Id, f)) return doc.GetPaintedMaterial(w.Id, f); }
            catch { /* not paintable */ }
            return f.MaterialElementId;
        }
        var mats = regions.Select(MaterialOf).ToList();
        double tiny = DrwUnits.FromMillimeters(0.5);
        bool Touch(Face a, Face b)
        {
            foreach (EdgeArray loop in a.EdgeLoops)
                foreach (Edge edge in loop)
                {
                    XYZ mid = edge.Evaluate(0.5);
                    try { if (b.Project(mid) is IntersectionResult ir && ir.Distance < tiny) return true; }
                    catch { /* off it */ }
                }
            return false;
        }
        int[] group = Enumerable.Range(0, regions.Count).ToArray();
        int Root(int i) => group[i] == i ? i : group[i] = Root(group[i]);
        for (int i = 0; i < regions.Count; i++)
            for (int j = i + 1; j < regions.Count; j++)
                if (mats[i] == mats[j] && Root(i) != Root(j) && Touch(regions[i], regions[j])) group[Root(j)] = Root(i);
        return Enumerable.Range(0, regions.Count).GroupBy(Root)
            .Select(g => (g.Select(i => regions[i]).ToList(), mats[g.Key])).ToList();
    }

    /// <summary>A point lies on <paramref name="f"/> (within <paramref name="within"/> of it, inside its edges).</summary>
    private static bool OnSurface(Face f, XYZ p, double within)
    {
        try { return f.Project(p) is IntersectionResult ir && ir.Distance < within && f.IsInside(ir.UVPoint); }
        catch { return false; }
    }

    /// <summary>Spots on a surface for its tag, best first: a 9 × 9 grid over each region, kept
    /// only at least 50 mm inside its edges (so out of window and door holes), nearest the middle
    /// of them all first.</summary>
    private static List<XYZ> SurfaceSpots(List<Face> faces)
    {
        double edge = DrwUnits.FromMillimeters(50);
        var spots = new List<XYZ>();
        foreach (Face f in faces)
        {
            BoundingBoxUV bb = f.GetBoundingBox();
            for (int i = 1; i <= 9; i++)
                for (int j = 1; j <= 9; j++)
                {
                    var uv = new UV(bb.Min.U + (bb.Max.U - bb.Min.U) * i / 10, bb.Min.V + (bb.Max.V - bb.Min.V) * j / 10);
                    try
                    {
                        if (!f.IsInside(uv)) continue;
                        if (!new[] { new UV(edge, 0), new UV(-edge, 0), new UV(0, edge), new UV(0, -edge) }.All(d => f.IsInside(uv + d))) continue;
                        spots.Add(f.Evaluate(uv));
                    }
                    catch { /* off the face */ }
                }
        }
        if (spots.Count == 0) return spots;
        XYZ mid = spots.Aggregate(XYZ.Zero, (a, p) => a + p) / spots.Count;
        return spots.OrderBy(p => p.DistanceTo(mid)).ToList();
    }

    /// <summary>Inside the view's crop (always, when the crop is off).</summary>
    private static bool InCrop(View view, XYZ p)
    {
        if (!view.CropBoxActive || view.CropBox is not BoundingBoxXYZ crop) return true;
        XYZ local = crop.Transform.Inverse.OfPoint(p);
        return local.X >= crop.Min.X && local.X <= crop.Max.X && local.Y >= crop.Min.Y && local.Y <= crop.Max.Y;
    }

    /// <summary>What an elevation can hide a wall behind: walls, floors, roofs, ceilings, columns and
    /// beams in the view.</summary>
    private static List<(Element E, BoundingBoxXYZ Box)> Blockers(Document doc, View view)
    {
        try
        {
            return new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(new[]
                {
                    BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings,
                    BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming
                }))
                .WhereElementIsNotElementType().Select(e => (e, e.get_BoundingBox(null))).Where(x => x.Item2 != null)
                .Select(x => (x.e, x.Item2!)).ToList();
        }
        catch { return new List<(Element, BoundingBoxXYZ)>(); }
    }

    /// <summary>Whether <paramref name="p"/> is hidden as the elevation looks at it: something
    /// between it and the elevation's plane (<paramref name="by"/>), or the spot is in front of the
    /// plane, cut away.</summary>
    private static bool Hidden(XYZ p, XYZ toViewer, View view, List<(Element E, BoundingBoxXYZ Box)> blockers,
        Dictionary<ElementId, List<Solid>> cache, out Element? by)
    {
        by = null;
        double mm = DrwUnits.FromMillimeters(1);
        double len = (CutPlane(view) - p).DotProduct(toViewer);
        if (len < 0) return true;
        if (len < 2 * mm) return false;
        XYZ a = p + toViewer * mm, b = p + toViewer * len;
        var lo = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        var hi = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        Line ray;
        try { ray = Line.CreateBound(a, b); }
        catch { return false; }
        foreach ((Element e, BoundingBoxXYZ box) in blockers)
        {
            if (box.Max.X < lo.X || box.Min.X > hi.X || box.Max.Y < lo.Y || box.Min.Y > hi.Y || box.Max.Z < lo.Z || box.Min.Z > hi.Z) continue;
            foreach (Solid s in SolidsOf(e, cache))
            {
                try
                {
                    if (s.IntersectWithCurve(ray, new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside })
                            .SegmentCount > 0)
                    {
                        by = e;
                        return true;
                    }
                }
                catch { /* a solid that cannot be intersected */ }
            }
        }
        return false;
    }

    // =============================================================================================

    /// <summary>The rule's elements visible in the view, after its filters.</summary>
    private static IEnumerable<Element> Candidates(Document doc, View view, Resolved r)
    {
        string mark = r.Rule.TypeMarkContains?.Trim() ?? "";
        string family = r.Rule.FamilyContains?.Trim() ?? "";
        double minFt = DrwUnits.FromMillimeters(Math.Max(0, r.Rule.MinLengthMm));
        foreach (Element e in new FilteredElementCollector(doc, view.Id).OfCategory(r.Category).WhereElementIsNotElementType())
        {
            if (e is SpatialElement s && s.Area <= 0) continue;
            ElementType? type = doc.GetElement(e.GetTypeId()) as ElementType;
            if (mark.Length > 0 &&
                (type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString() ?? "").IndexOf(mark, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (family.Length > 0 && (type?.FamilyName ?? "").IndexOf(family, StringComparison.OrdinalIgnoreCase) < 0) continue;
            string typeName = r.Rule.TypeNameContains?.Trim() ?? "";
            if (typeName.Length > 0 && (type?.Name ?? "").IndexOf(typeName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (minFt > 0 && e.Location is LocationCurve lc && lc.Curve.Length < minFt) continue;
            yield return e;
        }
    }

    /// <summary>Elements tagged in the view, each with its tags: family, material or not, and where
    /// a material tag reads its material.</summary>
    private static Dictionary<ElementId, List<TagInfo>> ExistingTags(Document doc, View view)
    {
        var map = new Dictionary<ElementId, List<TagInfo>>();
        void Add(ElementId id, Element tag, bool material, XYZ? head)
        {
            if (id == ElementId.InvalidElementId) return;
            if (!map.TryGetValue(id, out List<TagInfo>? list)) map[id] = list = new List<TagInfo>();
            list.Add(new TagInfo { Family = (doc.GetElement(tag.GetTypeId()) as ElementType)?.FamilyName ?? "", Material = material, Head = head });
        }
        foreach (Element e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)))
        {
            var it = (IndependentTag)e;
            bool material = it.Category?.Id.Value == (long)BuiltInCategory.OST_MaterialTags;
            XYZ? head = material ? ReadPoint(it) : null;
            foreach (ElementId id in it.GetTaggedLocalElementIds()) Add(id, e, material, head);
        }
        foreach (Element e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(SpatialElementTag)))
        {
            ElementId id = e switch
            {
                RoomTag rt => rt.TaggedLocalRoomId,
                SpaceTag st => st.Space?.Id ?? ElementId.InvalidElementId,
                _ => ElementId.InvalidElementId
            };
            Add(id, e, false, null);
        }
        return map;
    }

    /// <summary>The point a tag is measured from: the insertion point, a line's midpoint, or the
    /// middle of what the view shows.</summary>
    private static XYZ? Anchor(Element e, View view)
    {
        if (e is SpatialElement s && s.Location is not LocationPoint) return null;
        if (e.Location is LocationPoint lp) return lp.Point;
        if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
        BoundingBoxXYZ? bb = e.get_BoundingBox(view);
        return bb == null ? null : bb.Transform.OfPoint((bb.Min + bb.Max) / 2);
    }

    private static Element? Create(Document doc, View view, Element e, Resolved r, XYZ at)
    {
        if (e is Room room)
        {
            RoomTag? rt = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(at.X, at.Y), view.Id);
            if (rt != null && rt.GetTypeId() != r.Tag!.Id) rt.ChangeTypeId(r.Tag.Id);
            if (rt != null) rt.HasLeader = r.Rule.Leader;
            return rt;
        }
        if (e is Space space)
        {
            SpaceTag? st = doc.Create.NewSpaceTag(space, new UV(at.X, at.Y), (ViewPlan)view);
            if (st != null && st.GetTypeId() != r.Tag!.Id) st.ChangeTypeId(r.Tag.Id);
            if (st != null) st.HasLeader = r.Rule.Leader;
            return st;
        }
        if (!r.Tag!.IsActive) r.Tag.Activate();
        return IndependentTag.Create(doc, r.Tag.Id, view.Id, new Reference(e), r.Rule.Leader,
            r.Rule.Horizontal ? TagOrientation.Horizontal : TagOrientation.Vertical, at);
    }

    /// <summary>
    /// Puts a tag's head at <paramref name="to"/>. A room or space tag without a leader is made again
    /// there instead: moving one (MoveElement or its head) makes Revit switch its leader on and leave
    /// the tag hanging from the room's point — a leader the rule did not ask for, whose line then
    /// crossed the tags it was moved away from.
    /// </summary>
    private static void Move(Document doc, Pending p, XYZ to)
    {
        if (p.Tag is IndependentTag it) it.TagHeadPosition = to;
        else if (p.Tag is SpatialElementTag st && p.Rule.Rule.Leader) st.TagHeadPosition = to;
        else
        {
            Element? again = Create(doc, (View)doc.GetElement(p.Tag.OwnerViewId), p.Element, p.Rule, to);
            if (again == null) return;
            doc.Delete(p.Tag.Id);
            p.Tag = again;
        }
    }

    /// <summary>Paper mm a side spot may slide outward past the first one it tries, looking for room.
    /// Further than this and the tag no longer reads as the element's.</summary>
    private const double MaxSlideMm = 12;

    /// <summary>Paper mm kept between a tag moved aside and what it was moved away from.</summary>
    private const double GapMm = 1;

    /// <summary>The spots to try, preferred first: the rule's side, then the opposite side, the two
    /// other sides, and the centre.</summary>
    private static TagPosition[] Order(TagPosition preferred) => preferred switch
    {
        TagPosition.Above => new[] { TagPosition.Above, TagPosition.Below, TagPosition.Right, TagPosition.Left, TagPosition.Center },
        TagPosition.Below => new[] { TagPosition.Below, TagPosition.Above, TagPosition.Right, TagPosition.Left, TagPosition.Center },
        TagPosition.Left => new[] { TagPosition.Left, TagPosition.Right, TagPosition.Above, TagPosition.Below, TagPosition.Center },
        TagPosition.Right => new[] { TagPosition.Right, TagPosition.Left, TagPosition.Above, TagPosition.Below, TagPosition.Center },
        _ => new[] { TagPosition.Center, TagPosition.Above, TagPosition.Below, TagPosition.Right, TagPosition.Left }
    };

    private static string Word(TagPosition pos) => pos switch
    {
        TagPosition.Above => "above",
        TagPosition.Below => "below",
        TagPosition.Left => "to the left",
        TagPosition.Right => "to the right",
        _ => "onto the element"
    };

    /// <summary>The view direction of a side; the centre has none.</summary>
    private static (double X, double Y) Unit(TagPosition pos) => pos switch
    {
        TagPosition.Above => (0, 1),
        TagPosition.Below => (0, -1),
        TagPosition.Right => (1, 0),
        TagPosition.Left => (-1, 0),
        _ => (0, 0)
    };

    /// <summary>
    /// A clear spot along (ux, uy), or null. The head starts <paramref name="start"/> out from the
    /// anchor and slides outward past whatever it overlaps, at most <see cref="MaxSlideMm"/>. With no
    /// direction (the centre) the anchor itself is tried. <paramref name="rel"/> is the tag's box
    /// around its head point. Returns the head's offset from the anchor and its distance out.
    /// </summary>
    private static (double Dx, double Dy, double Along)? Clear(double ux, double uy, double start, Box rel,
        double ax, double ay, List<Box> obstacles, double paperMm, double tolerance)
    {
        if (ux == 0 && uy == 0)
            return obstacles.Any(o => o.Overlaps(rel.Shift(ax, ay), tolerance)) ? null : (0, 0, 0);

        double gap = GapMm * paperMm;
        double limit = start + MaxSlideMm * paperMm;
        double along = start;
        for (int i = 0; i < 24 && along <= limit; i++)
        {
            double dx = ux * along, dy = uy * along;
            Box at = rel.Shift(ax + dx, ay + dy);
            List<Box> hits = obstacles.Where(o => o.Overlaps(at, tolerance)).ToList();
            if (hits.Count == 0) return (dx, dy, along);
            // Past the farthest edge of everything in the way, measured out from the anchor.
            double edge = hits.Max(o => o.High(ux, uy)) - (ax * ux + ay * uy);
            along = Math.Max(edge - rel.Low(ux, uy) + gap, along + tolerance);
        }
        return null;
    }

    private static XYZ Offset(TagPosition pos, double d, XYZ right, XYZ up) => pos switch
    {
        TagPosition.Above => up * d,
        TagPosition.Below => up * -d,
        TagPosition.Left => right * -d,
        TagPosition.Right => right * d,
        _ => XYZ.Zero
    };

    private static (double X, double Y) Flat(XYZ p, XYZ right, XYZ up) => (p.DotProduct(right), p.DotProduct(up));

    private static Box? BoxOf(Element e, View view)
    {
        BoundingBoxXYZ? bb = e.get_BoundingBox(view);
        if (bb == null) return null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (double x in new[] { bb.Min.X, bb.Max.X })
        foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
        foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
        {
            (double fx, double fy) = Flat(bb.Transform.OfPoint(new XYZ(x, y, z)), view.RightDirection, view.UpDirection);
            minX = Math.Min(minX, fx); maxX = Math.Max(maxX, fx);
            minY = Math.Min(minY, fy); maxY = Math.Max(maxY, fy);
        }
        return maxX > minX && maxY > minY ? new Box(minX, minY, maxX, maxY) : null;
    }

    /// <summary>Annotation already in the view that a new tag must not sit on: tags, room and space
    /// tags, text notes and annotation symbols. The tags this run made are left out — they are added
    /// one by one as each settles.</summary>
    private static List<Box> Obstacles(Document doc, View view, HashSet<ElementId> mine)
    {
        var boxes = new List<Box>();
        foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
        {
            if (mine.Contains(e.Id)) continue;
            bool annotation = e is IndependentTag or SpatialElementTag or TextNote
                || (e is FamilyInstance && e.Category?.CategoryType == CategoryType.Annotation);
            if (!annotation) continue;
            if (BoxOf(e, view) is Box b) boxes.Add(b);
        }
        return boxes;
    }

    private static string Label(Element e)
    {
        string name = e is SpatialElement s
            ? $"{s.Number} {s.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? s.Name}".Trim()
            : e.Name;
        return $"{name} [{e.Id.Value}]";
    }

    private static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> row, string status, string? note = null)
    {
        var copy = row.ToDictionary(kv => kv.Key, kv => kv.Value);
        copy["Status"] = status;
        if (note != null) copy["Note"] = note;
        return copy;
    }
}
