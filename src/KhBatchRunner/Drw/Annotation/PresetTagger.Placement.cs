using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

internal static partial class PresetTagger
{
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
