using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

internal static partial class PresetTagger
{
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
}
