using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using KhBatchRunner.Drw.Common;

namespace KhBatchRunner.Drw.Enlarge;

/// <summary>
/// One room, stair or multistory stair to enlarge, reduced to what the creator needs: a name, the
/// levels to plan, horizontal outline loops, a height range and an inside test. Rooms and stairs
/// differ only in how these are worked out.
/// </summary>
internal sealed class EnlargeTarget
{
    public string Kind { get; init; } = "";
    public string BaseName { get; set; } = "";
    public string Display { get; init; } = "";
    public ElementId SourceId { get; init; } = ElementId.InvalidElementId;
    public List<Level> PlanLevels { get; init; } = new();
    /// <summary>Stairs name each plan with its level, because a stair has more than one.</summary>
    public bool LevelSuffix { get; init; }
    /// <summary>A stair (standalone or multistory). Stairs only ever get sections.</summary>
    public bool IsStair { get; init; }
    /// <summary>Outline loops; [0] is the outer one.</summary>
    public List<List<Line>> Loops { get; init; } = new();
    public double BottomZ { get; init; }
    public double TopZ { get; init; }
    /// <summary>What a section's top is measured from by default: the top of a room, or the top of
    /// a stair's last step (its top level + offset — not the stringers standing above it).</summary>
    public double LastStepZ { get; init; }
    /// <summary>The first level above the room's level / the stair's top level, if any (e.g. Roof
    /// over a stair that ends at L2). The other thing a section's top can be measured from.</summary>
    public Level? NextLevel { get; init; }
    public Func<XYZ, bool> Inside { get; init; } = _ => false;
    /// <summary>A room's number, used to tell apart rooms that share a name on the same level.</summary>
    public string Number { get; init; } = "";
    /// <summary>Marks to write before the views are made (multistory stair members).</summary>
    public List<(ElementId Id, string Mark)> MarkWrites { get; } = new();

    public IEnumerable<XYZ> OutlinePoints() =>
        Loops.SelectMany(l => l).SelectMany(s => new[] { s.GetEndPoint(0), s.GetEndPoint(1) });
}

internal static class EnlargeTargets
{
    // ------------------------------------------------------------------ rooms

    /// <summary>The first level more than 1 mm above <paramref name="level"/>, from a list sorted by elevation.</summary>
    private static Level? LevelAbove(List<Level> sortedLevels, Level level) =>
        sortedLevels.FirstOrDefault(l => l.Elevation > level.Elevation + 1.0 / 304.8);

    private static List<Level> SortedLevels(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();

    public static List<EnlargeTarget> Rooms(Document doc, RoomLabel label)
    {
        List<Level> levels = SortedLevels(doc);
        List<Room> rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType().OfType<Room>()
            .Where(r => r.Area > 0 && r.Location != null && r.Level != null)
            .OrderBy(r => r.Level.Elevation)
            .ThenBy(r => r.Number, StringComparer.OrdinalIgnoreCase).ToList();

        var targets = new List<EnlargeTarget>();
        int fallback = 0;
        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
        };
        foreach (Room room in rooms)
        {
            IList<IList<BoundarySegment>>? boundary = room.GetBoundarySegments(options);
            if (boundary == null || boundary.Count == 0) continue;

            var loops = boundary.Select(loop => loop.SelectMany(s => ToLines(s.GetCurve())).ToList())
                .Where(l => l.Count > 0).ToList();
            if (loops.Count == 0) continue;

            BoundingBoxXYZ? bb = room.get_BoundingBox(null);
            double bottom = bb?.Min.Z ?? room.Level.Elevation;
            double top = bb?.Max.Z ?? bottom + 10;

            string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()?.Trim() ?? "";
            string number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString()?.Trim() ?? "";
            string baseName = label switch
            {
                RoomLabel.Number => number,
                RoomLabel.NumberName => string.Join(" ", new[] { number, name }.Where(s => s.Length > 0)),
                _ => name
            };
            if (baseName.Length == 0) baseName = $"RM-{++fallback}";

            double probeZ = bottom + Math.Min(1.0, (top - bottom) / 2);
            targets.Add(new EnlargeTarget
            {
                Kind = "Room",
                BaseName = baseName,
                Display = $"{room.Level.Name}  |  {number}  {name}".TrimEnd(),
                SourceId = room.Id,
                PlanLevels = new List<Level> { room.Level },
                Loops = loops,
                BottomZ = bottom,
                TopZ = top,
                LastStepZ = top,
                NextLevel = LevelAbove(levels, room.Level),
                Inside = p => room.IsPointInRoom(new XYZ(p.X, p.Y, probeZ)),
                Number = number
            });
        }

        // Rooms share names ("Utility" on every level). Without this the second one's views would
        // collide with the first's and be skipped as "already exists". Level first, then number,
        // so the names are the same on every run and re-runs still skip what they made before.
        Disambiguate(targets, t => t.PlanLevels[0].Name);
        Disambiguate(targets, t => t.Number);
        return targets;
    }

    private static void Disambiguate(List<EnlargeTarget> targets, Func<EnlargeTarget, string> extra)
    {
        foreach (IGrouping<string, EnlargeTarget> clash in targets
                     .GroupBy(t => t.BaseName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
        {
            foreach (EnlargeTarget t in clash)
            {
                string x = extra(t);
                if (!string.IsNullOrWhiteSpace(x)) t.BaseName += " - " + x;
            }
        }
    }

    private static IEnumerable<Line> ToLines(Curve c)
    {
        if (c is Line l) { yield return l; yield break; }
        IList<XYZ> pts = c.Tessellate();
        for (int i = 1; i < pts.Count; i++)
            if (pts[i - 1].DistanceTo(pts[i]) > 1e-3)
                yield return Line.CreateBound(pts[i - 1], pts[i]);
    }

    // ------------------------------------------------------------------ stairs

    private static readonly Regex StMark = new(@"^ST-(\d+)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Standalone stairs and multistory stairs, one target each. A multistory stair is enlarged as
    /// ONE target (its full height), named from the group's mark; its members get
    /// <c>mark + delimiter + n</c> from the bottom up, when <paramref name="delimiter"/> is given.
    /// Stairs with no mark are named ST-n, continuing after the highest ST-n already in the model.
    /// </summary>
    /// <param name="beforeFirstStepM">How far the boundary reaches in front of each stair's first
    /// riser (0 = the stair itself).</param>
    /// <param name="afterLastStepM">How far it reaches past the last riser.</param>
    public static List<EnlargeTarget> Stairs(Document doc, string delimiter, bool overrideMemberMarks,
        double beforeFirstStepM = 0, double afterLastStepM = 0)
    {
        List<Level> levels = SortedLevels(doc);
        double before = DrwUnits.FromMeters(Math.Max(0, beforeFirstStepM));
        double after = DrwUnits.FromMeters(Math.Max(0, afterLastStepM));
        List<Stairs> allStairs = new FilteredElementCollector(doc).OfClass(typeof(Stairs))
            .Cast<Stairs>().ToList();
        List<MultistoryStairs> multi = new FilteredElementCollector(doc).OfClass(typeof(MultistoryStairs))
            .Cast<MultistoryStairs>().ToList();

        int next = 1 + allStairs.Cast<Element>().Concat(multi)
            .Select(e => StMark.Match(DrwParams.Mark(e) ?? ""))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0).Max();

        var targets = new List<EnlargeTarget>();

        foreach (MultistoryStairs ms in multi)
        {
            List<Stairs> members = ms.GetAllStairsIds().Select(doc.GetElement).OfType<Stairs>()
                .OrderBy(s => s.get_BoundingBox(null)?.Min.Z ?? 0).ToList();
            if (members.Count == 0) continue;

            string mark = (DrwParams.Mark(ms) ?? "").Trim();
            if (mark.Length == 0)
                mark = (DrwParams.Mark(doc.GetElement(ms.StandardStairsId)) ?? "").Trim();
            bool generated = mark.Length == 0;
            if (generated) mark = $"ST-{next++}";

            EnlargeTarget t = StairTarget(doc, levels, members, mark, "Multistory stair", ms.Id, before, after,
                ms.GetAllConnectedLevels());
            if (overrideMemberMarks)
            {
                if (generated || (DrwParams.Mark(ms) ?? "") != mark) t.MarkWrites.Add((ms.Id, mark));
                for (int i = 0; i < members.Count; i++)
                    t.MarkWrites.Add((members[i].Id, $"{mark}{delimiter}{i + 1}"));
            }
            targets.Add(t);
        }

        foreach (Stairs st in allStairs.Where(s => s.MultistoryStairsId == ElementId.InvalidElementId))
        {
            string mark = (DrwParams.Mark(st) ?? "").Trim();
            if (mark.Length == 0) mark = $"ST-{next++}";
            targets.Add(StairTarget(doc, levels, new List<Stairs> { st }, mark, "Stair", st.Id, before, after, null));
        }

        // Two stairs carrying the same Mark would otherwise collide the same way rooms do.
        Disambiguate(targets, t => t.PlanLevels[0].Name);
        return targets.OrderBy(t => t.BottomZ).ThenBy(t => t.BaseName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The plans are the levels the stair actually CONNECTS, never every level in its height
    /// range: a model with several blocks has many levels at similar elevations, and a
    /// range test planned the stair on all of them (seen on Snowdon Towers, 2026-09-22). A
    /// standalone stair gives its base and top level; a multistory stair its connected levels
    /// plus each member's base and top.
    /// </summary>
    private static EnlargeTarget StairTarget(Document doc, List<Level> levels, List<Stairs> members,
        string mark, string kind, ElementId sourceId, double before, double after,
        ICollection<ElementId>? connectedLevels)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        double lastStep = double.MinValue;
        var levelIds = new HashSet<ElementId>(connectedLevels ?? Array.Empty<ElementId>());
        foreach (Stairs s in members)
        {
            BoundingBoxXYZ? bb = s.get_BoundingBox(null);
            if (bb != null)
            {
                minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y); minZ = Math.Min(minZ, bb.Min.Z);
                maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y); maxZ = Math.Max(maxZ, bb.Max.Z);
            }
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.STAIRS_BASE_LEVEL_PARAM, BuiltInParameter.STAIRS_TOP_LEVEL_PARAM })
            {
                ElementId id = s.get_Parameter(bip)?.AsElementId() ?? ElementId.InvalidElementId;
                if (doc.GetElement(id) is Level) levelIds.Add(id);
            }

            // The last step is where the stair arrives: its top level plus the top offset. The
            // bounding box would say more — stringers and railing posts stand above the last tread.
            if (doc.GetElement(s.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.AsElementId()
                    ?? ElementId.InvalidElementId) is Level topLevel)
                lastStep = Math.Max(lastStep, topLevel.Elevation +
                    (s.get_Parameter(BuiltInParameter.STAIRS_TOP_OFFSET)?.AsDouble() ?? 0));
            else if (bb != null)
                lastStep = Math.Max(lastStep, bb.Max.Z);

            // Room in front of the first riser and past the last one, when asked for.
            foreach (XYZ p in StepApproaches(doc, s, before, after))
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
        }

        List<Level> planLevels = levels.Where(l => levelIds.Contains(l.Id)).ToList();
        if (planLevels.Count == 0)
            planLevels = levels.OrderBy(l => Math.Abs(l.Elevation - minZ)).Take(1).ToList();
        double baseElev = planLevels.First().Elevation, topElev = planLevels.Last().Elevation;

        double z = planLevels[0].Elevation;
        var corners = new[]
        {
            new XYZ(minX, minY, z), new XYZ(maxX, minY, z), new XYZ(maxX, maxY, z), new XYZ(minX, maxY, z)
        };
        var loop = new List<Line>();
        for (int i = 0; i < 4; i++) loop.Add(Line.CreateBound(corners[i], corners[(i + 1) % 4]));

        return new EnlargeTarget
        {
            Kind = kind,
            BaseName = mark,
            Display = $"{mark}  |  {kind}  |  {planLevels.First().Name} → {planLevels.Last().Name}",
            SourceId = sourceId,
            PlanLevels = planLevels,
            LevelSuffix = true,
            IsStair = true,
            Loops = new List<List<Line>> { loop },
            BottomZ = Math.Min(minZ, baseElev),
            TopZ = Math.Max(maxZ, topElev),
            LastStepZ = lastStep > double.MinValue ? lastStep : Math.Max(maxZ, topElev),
            NextLevel = LevelAbove(levels, planLevels.Last()),
            Inside = p => p.X > minX && p.X < maxX && p.Y > minY && p.Y < maxY
        };
    }

    /// <summary>
    /// Points that stretch a stair's outline <paramref name="before"/> in front of its first riser
    /// and <paramref name="after"/> past its last one. The riser edge is found from the run's
    /// footprint: the points furthest back (first run) or forward (last run) along the walking
    /// direction at that end of its path. Each is returned as it is and moved outward, so the
    /// stretch keeps the run's width.
    /// </summary>
    private static IEnumerable<XYZ> StepApproaches(Document doc, Stairs stair, double before, double after)
    {
        if (before <= 0 && after <= 0) yield break;
        List<StairsRun> runs = stair.GetStairsRuns().Select(doc.GetElement).OfType<StairsRun>()
            .OrderBy(r => r.BaseElevation).ToList();
        if (runs.Count == 0) yield break;
        if (before > 0)
            foreach (XYZ p in RiserEdge(runs[0], atStart: true, before)) yield return p;
        if (after > 0)
            foreach (XYZ p in RiserEdge(runs[runs.Count - 1], atStart: false, after)) yield return p;
    }

    private static IEnumerable<XYZ> RiserEdge(StairsRun run, bool atStart, double distance)
    {
        List<Curve> path = run.GetStairsPath().ToList();
        if (path.Count == 0) yield break;
        Curve end = atStart ? path[0] : path[path.Count - 1];
        XYZ d = end.ComputeDerivatives(atStart ? 0.0 : 1.0, true).BasisX;
        d = new XYZ(d.X, d.Y, 0);
        if (d.GetLength() < 1e-9) yield break;
        XYZ walk = d.Normalize();
        XYZ outward = atStart ? walk.Negate() : walk;

        List<XYZ> footprint = run.GetFootprintBoundary().SelectMany(c => c.Tessellate()).ToList();
        if (footprint.Count == 0) yield break;
        double edge = atStart ? footprint.Min(p => p.DotProduct(walk)) : footprint.Max(p => p.DotProduct(walk));
        const double tolerance = 15.0 / 304.8;   // 15 mm
        foreach (XYZ p in footprint.Where(p => Math.Abs(p.DotProduct(walk) - edge) < tolerance))
        {
            yield return p;
            yield return p + outward * distance;
        }
    }
}
