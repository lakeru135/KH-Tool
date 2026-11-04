using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

/// <summary>
/// Stair Annotation's dimensions (0.2.9.1): plans only, stairs whose runs are straight and either
/// parallel or at 90° to the first (single run, U / switchback, L). With `a` the first run's
/// direction and `p` its left, the same rule runs on each axis X:
/// <list type="bullet">
/// <item>each run along X gets a string on its outer side — its start and end plus the stair's
/// extremes along X, the run segment prefixed "10T @ 0.275 = " (Revit still shows the live length);</item>
/// <item>the runs across X give a widths string (their sides plus the extremes) on the opposite
/// side — or, with no run along X (a U's width axis), beyond the end the first run climbs to;</item>
/// <item>the overall, outside the first of those strings.</item>
/// </list>
/// Everything is dimensioned to faces and tread lines of the runs and landings themselves.
/// </summary>
internal static class StairDimensioner
{
    private const double Tol = 1.0 / 304.8;       // 1 mm, in feet
    private const double Exact = 0.1 / 304.8;     // 0.1 mm

    /// <summary>Something to dimension to, its position along the axis, and what it is (for the notes).</summary>
    internal readonly struct FaceRef
    {
        public FaceRef(Reference reference, double at, string kind) { Reference = reference; At = at; Kind = kind; }
        public Reference Reference { get; }
        public double At { get; }
        public string Kind { get; }
    }

    private sealed class RunInfo
    {
        public StairsRun Run = null!;
        public int Index;
        public bool AlongA;
        public FaceRef Start, End, SideMin, SideMax;
        public string EndsNote = "";
    }

    /// <summary>The run shapes this version dimensions; null = supported, else why not.</summary>
    public static string? WhyNotSupported(Document doc, Stairs st, out List<StairsRun> runs, out XYZ a)
    {
        runs = st.GetStairsRuns().Select(doc.GetElement).OfType<StairsRun>().ToList();
        a = XYZ.BasisY;
        if (runs.Count == 0) return "no runs";
        if (runs.Any(r => r.StairsRunStyle != StairsRunStyle.Straight))
            return "not a straight-run stair (winders, spiral or curved runs come later)";
        var dirs = new List<XYZ>();
        foreach (StairsRun r in runs)
        {
            XYZ? d = Direction(r);
            if (d == null) return $"run {r.Id} has no straight path";
            dirs.Add(d);
        }
        a = dirs[0];
        XYZ first = a;
        if (dirs.Any(d => Math.Abs(first.X * d.Y - first.Y * d.X) > 1e-6 && Math.Abs(first.DotProduct(d)) > 1e-6))
            return "runs are neither parallel nor at 90° to each other";
        return null;
    }

    /// <summary>A straight run's climbing direction in plan (unit, Z = 0).</summary>
    private static XYZ? Direction(StairsRun run)
    {
        try
        {
            Curve? c = run.GetStairsPath()?.FirstOrDefault();
            if (c == null) return null;
            XYZ v = c.GetEndPoint(1) - c.GetEndPoint(0);
            v = new XYZ(v.X, v.Y, 0);
            return v.GetLength() < 1e-9 ? null : v.Normalize();
        }
        catch { return null; }
    }

    /// <summary>
    /// Everything of <paramref name="e"/> a plan dimension along <paramref name="axis"/> can attach
    /// to: vertical planar faces facing ±axis, and (with <paramref name="edges"/>) horizontal straight
    /// edges running across it — tread and riser lines, since risers are often sloped and so face
    /// nothing squarely. Instance geometry is read through its symbol, whose references dimension.
    /// </summary>
    public static List<FaceRef> Faces(Element e, XYZ axis, View? view, bool edges = false, string part = "")
    {
        var list = new List<FaceRef>();
        var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false };
        if (view != null) options.View = view; else options.DetailLevel = ViewDetailLevel.Fine;
        GeometryElement? geo = e.get_Geometry(options);
        if (geo == null) return list;
        if (part.Length == 0) part = e is StairsLanding ? "landing" : "run";
        Collect(geo, Transform.Identity, axis, edges, part, list);
        return list;
    }

    private static void Collect(GeometryElement geo, Transform tf, XYZ axis, bool edges, string part, List<FaceRef> list)
    {
        XYZ across = new XYZ(-axis.Y, axis.X, 0);
        foreach (GeometryObject o in geo)
        {
            if (o is GeometryInstance gi) { Collect(gi.GetSymbolGeometry(), tf.Multiply(gi.Transform), axis, edges, part, list); continue; }
            if (o is not Solid s || s.Faces.IsEmpty) continue;
            foreach (Face f in s.Faces)
            {
                if (f is not PlanarFace pf || pf.Reference == null) continue;
                XYZ n = tf.OfVector(pf.FaceNormal);
                if (Math.Abs(n.Z) > 1e-6 || Math.Abs(Math.Abs(n.DotProduct(axis)) - 1) > 1e-6) continue;
                list.Add(new FaceRef(pf.Reference, tf.OfPoint(pf.Origin).DotProduct(axis), part + " face"));
            }
            if (!edges) continue;
            foreach (Edge edge in s.Edges)
            {
                if (edge.Reference == null || edge.AsCurve() is not Line line) continue;
                XYZ dir = tf.OfVector(line.Direction);
                if (Math.Abs(dir.Z) > 1e-6 || Math.Abs(Math.Abs(dir.DotProduct(across)) - 1) > 1e-6) continue;
                list.Add(new FaceRef(edge.Reference, tf.OfPoint(line.GetEndPoint(0)).DotProduct(axis),
                    part == "landing" ? "landing edge" : "tread line"));
            }
        }
    }

    /// <summary>
    /// A run's start and end along its own direction <paramref name="axis"/>: where its stair path
    /// begins and ends (the run length Revit counts, treads × depth), each tied to a face or line of
    /// the run or its landings there.
    /// <para>A sloped riser gives two lines about 1 mm apart, and where a run meets a floor rather
    /// than a landing only the one off the path is there — taken alone it made "8T @ 0.275 = 2201".
    /// So both ends are taken at the SAME offset from the path when they can be (both exact, or
    /// both 0.9 mm along): the run segment then reads treads × depth exactly. The note says what
    /// each end is on, and how far from Revit's run line when not on it.</para>
    /// </summary>
    private static (FaceRef Start, FaceRef End)? RunEnds(StairsRun run, XYZ axis, double treadDepth, IEnumerable<Element> landings, out string note)
    {
        note = "";
        List<Curve> path = run.GetStairsPath()?.ToList() ?? new List<Curve>();
        if (path.Count == 0) { note = "no stair path"; return null; }
        double s = path[0].GetEndPoint(0).DotProduct(axis), e = path[path.Count - 1].GetEndPoint(1).DotProduct(axis);
        List<FaceRef> candidates = Faces(run, axis, null, edges: true)
            .Concat(landings.SelectMany(l => Faces(l, axis, null, edges: true))).ToList();
        double reach = Math.Max(2 * Tol, treadDepth / 2);
        List<FaceRef> near(double at) => candidates.Where(c => Math.Abs(c.At - at) <= reach).ToList();
        List<FaceRef> atStart = near(s), atEnd = near(e);
        if (atStart.Count == 0 || atEnd.Count == 0)
        {
            note = $"no face or line near the run's {(atStart.Count == 0 ? "start" : "end")}";
            return null;
        }
        // In the run's own direction, an offset means the same thing at both ends.
        double dirSign = e >= s ? 1 : -1;
        (FaceRef S, FaceRef E) best = (atStart[0], atEnd[0]);
        double bestScore = double.MaxValue;
        foreach (FaceRef cs in atStart)
            foreach (FaceRef ce in atEnd)
            {
                double os = (cs.At - s) * dirSign, oe = (ce.At - e) * dirSign;
                double mismatch = Math.Abs(os - oe);
                double score = (mismatch <= Exact ? 0 : 1000 + mismatch) + Math.Abs(os) + Math.Abs(oe);
                if (score < bestScore) { bestScore = score; best = (cs, ce); }
            }
        double offS = (best.S.At - s) * 304.8 * dirSign, offE = (best.E.At - e) * 304.8 * dirSign;
        string Where(FaceRef f, double off) => f.Kind + (Math.Abs(off) <= 0.1 ? "" : $" {off:+0.0;-0.0} mm from Revit's run line");
        note = $"start on {Where(best.S, offS)}, end on {Where(best.E, offE)}";
        if (Math.Abs(offS - offE) > 0.1)
            note += $" — the ends are {Math.Abs(offS - offE):0.0} mm apart in offset, so the run reads off; check by hand";
        return best;
    }

    private static (FaceRef Min, FaceRef Max)? Extremes(List<FaceRef> faces) =>
        faces.Count == 0 ? null : (faces.OrderBy(f => f.At).First(), faces.OrderBy(f => f.At).Last());

    /// <summary>
    /// Ordered by position, one per position (1 mm). Of near-equal ones the one listed FIRST wins,
    /// so a run's own ends (listed first) are kept over a stair extreme 0.9 mm from them.
    /// </summary>
    private static List<FaceRef> Distinct(IEnumerable<FaceRef> faces)
    {
        var kept = new List<FaceRef>();
        foreach (FaceRef f in faces)
            if (kept.All(k => Math.Abs(k.At - f.At) > Tol)) kept.Add(f);
        return kept.OrderBy(f => f.At).ToList();
    }

    /// <summary>Whether the view already holds a dimension to any run or landing of the stair.</summary>
    public static bool AlreadyDimensioned(Document doc, View view, Stairs st)
    {
        var parts = new HashSet<ElementId>(st.GetStairsRuns().Concat(st.GetStairsLandings())) { st.Id };
        // By owner view, not the view's collector, which can leave out dimensions to a stair it has not drawn yet.
        foreach (Dimension d in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            if (d.OwnerViewId != view.Id) continue;
            ReferenceArray refs;
            try { refs = d.References; } catch { continue; }
            foreach (Reference r in refs)
                if (parts.Contains(r.ElementId)) return true;
        }
        return false;
    }

    /// <summary>The run label with {N} (treads) and {D} (tread depth in metres) filled in.</summary>
    public static string RunLabel(string format, int treads, double depthFt) =>
        (format ?? "")
            .Replace("{N}", treads.ToString(CultureInfo.InvariantCulture))
            .Replace("{D}", (depthFt * 0.3048).ToString("0.###", CultureInfo.InvariantCulture));

    /// <summary>Paper mm between strings: the type's Dimension Line Snap Distance when asked and it has one.</summary>
    public static double GapMm(Document doc, StairAnnotationPreset p, DimensionType? dimType, out string note)
    {
        note = "";
        if (!p.SpacingFromType) return Math.Max(1, p.DimSpacingMm);
        DimensionType? type = dimType
            ?? doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType)) as DimensionType;
        double? snap = DatumDimensioner.SnapDistanceMm(type);
        if (snap is > 0) return snap.Value;
        note = (type == null ? "Revit's default dimension type" : $"'{DrwLookup.TypeLabel(type)}'") +
               $" has no Dimension Line Snap Distance — strings spaced {p.DimSpacingMm:0.#} mm";
        return Math.Max(1, p.DimSpacingMm);
    }

    public static List<IReadOnlyDictionary<string, string>> Place(Document doc, ViewPlan view, Stairs st, string label,
        StairAnnotationPreset p, DimensionType? dimType, string setupNote, double gapMm)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        IReadOnlyDictionary<string, string> Row(string what, string status, string note) =>
            DrwRows.Row(StairAnnotator.Columns, view.Name, label, "Dimensions" + (what.Length > 0 ? " — " + what : ""), status,
                string.Join("; ", new[] { note, setupNote }.Where(s => s.Length > 0)));

        string? why = WhyNotSupported(doc, st, out List<StairsRun> runs, out XYZ a);
        if (why != null) { rows.Add(Row("", "Skipped", why)); return rows; }
        if (AlreadyDimensioned(doc, view, st)) { rows.Add(Row("", "Skipped", "Already dimensioned in this view")); return rows; }

        XYZ left = new XYZ(-a.Y, a.X, 0);
        List<Element> landings = st.GetStairsLandings().Select(doc.GetElement).Where(e => e != null).ToList()!;
        double depth = st.ActualTreadDepth;

        // Each run: its ends along its own axis, its sides along the other.
        var infos = new List<RunInfo>();
        for (int i = 0; i < runs.Count; i++)
        {
            StairsRun r = runs[i];
            bool alongA = Math.Abs(Direction(r)!.DotProduct(a)) > 0.5;
            XYZ own = alongA ? a : left, other = alongA ? left : a;
            var ends = RunEnds(r, own, depth, landings, out string endsNote);
            if (ends == null) { rows.Add(Row("", "Failed", $"Run {i + 1}: {endsNote}")); return rows; }
            var sides = Extremes(Faces(r, other, null));
            if (sides == null) { rows.Add(Row("", "Failed", $"Run {i + 1}: no side faces to dimension to")); return rows; }
            infos.Add(new RunInfo
            {
                Run = r, Index = i + 1, AlongA = alongA, Start = ends.Value.Start, End = ends.Value.End,
                SideMin = sides.Value.Min, SideMax = sides.Value.Max, EndsNote = endsNote
            });
        }

        // The stair's extremes on each axis: run ends and sides, and the landings.
        List<FaceRef> On(bool axisA) =>
            infos.SelectMany(r => r.AlongA == axisA ? new[] { r.Start, r.End } : new[] { r.SideMin, r.SideMax })
                .Concat(landings.SelectMany(l => Faces(l, axisA ? a : left, null, edges: true))).ToList();
        var extA = Extremes(On(true))!.Value;
        var extP = Extremes(On(false))!.Value;

        double scale = Math.Max(1, view.Scale);
        double off = DrwUnits.FromMillimeters(p.DimOffsetMm) * scale;
        double gap = DrwUnits.FromMillimeters(gapMm) * scale;
        double z = view.GenLevel?.Elevation ?? 0;

        foreach (bool axisA in new[] { true, false })
        {
            XYZ X = axisA ? a : left, Y = axisA ? left : a;
            var (minX, maxX) = axisA ? extA : extP;
            var (minY, maxY) = axisA ? extP : extA;
            double centreY = (minY.At + maxY.At) / 2;
            var layers = new Dictionary<bool, int> { [true] = 0, [false] = 0 };   // true = the max-Y side
            double AtY(bool maxSide) => maxSide ? maxY.At + off + layers[maxSide]++ * gap : minY.At - off - layers[maxSide]++ * gap;

            List<RunInfo> along = infos.Where(r => r.AlongA == axisA).ToList();
            List<RunInfo> across = infos.Where(r => r.AlongA != axisA).ToList();
            bool? firstSide = null;
            var strings = new List<List<FaceRef>>();

            // Run strings, each on its outer side; a lone run on the max side (left of the first run).
            foreach (RunInfo r in along)
            {
                bool maxSide = (r.SideMin.At + r.SideMax.At) / 2 >= centreY - Tol;
                firstSide ??= maxSide;
                var refs = Distinct(new[] { r.Start, r.End, minX, maxX });
                strings.Add(refs);
                string prefix = RunLabel(p.RunLabel, r.Run.ActualTreadsNumber, depth);
                rows.Add(Create(doc, view, X, Y, AtY(maxSide), z, refs, dimType, (r.Start.At, r.End.At, prefix),
                    $"Run {r.Index}", (w, s, n) => Row(w, s, string.Join("; ", new[] { n, r.EndsNote }.Where(x => x.Length > 0)))));
            }

            // Widths of the runs crossing this axis: opposite the run strings, else beyond run 1's top end.
            bool widthSide = firstSide.HasValue ? !firstSide.Value : Direction(infos[0].Run)!.DotProduct(Y) >= 0;
            if (across.Count > 0)
            {
                var refs = Distinct(across.SelectMany(r => new[] { r.SideMin, r.SideMax }).Concat(new[] { minX, maxX }));
                strings.Add(refs);
                rows.Add(Create(doc, view, X, Y, AtY(widthSide), z, refs, dimType, null,
                    across.Count == 1 ? $"Run {across[0].Index} width" : "Widths", Row));
            }

            // Overall, outside the first string — unless a string already is exactly it.
            var overall = Distinct(new[] { minX, maxX });
            bool repeats = strings.Any(s => s.Count == 2 && overall.Count == 2
                                            && Math.Abs(s[0].At - overall[0].At) <= Tol && Math.Abs(s[1].At - overall[1].At) <= Tol);
            if (p.DimOverall && overall.Count == 2 && !repeats)
                rows.Add(Create(doc, view, X, Y, AtY(firstSide ?? widthSide), z, overall, dimType, null,
                    axisA ? "Overall length" : "Overall width", Row));
        }
        return rows;
    }

    /// <summary>
    /// One string along <paramref name="axis"/> at <paramref name="atOther"/> on the other axis.
    /// <paramref name="run"/>, when given, is the segment to prefix (between its two positions).
    /// </summary>
    private static IReadOnlyDictionary<string, string> Create(Document doc, ViewPlan view, XYZ axis, XYZ other, double atOther,
        double z, List<FaceRef> refs, DimensionType? dimType, (double From, double To, string Prefix)? run, string what,
        Func<string, string, string, IReadOnlyDictionary<string, string>> row)
    {
        if (refs.Count < 2) return row(what, "Skipped", "Nothing to dimension (one position only)");
        XYZ P(double along) => axis * along + other * atOther + XYZ.BasisZ * z;
        var array = new ReferenceArray();
        foreach (FaceRef f in refs) array.Append(f.Reference);
        string values = string.Join(" | ", refs.Zip(refs.Skip(1), (x, y) => Math.Round((y.At - x.At) * 304.8).ToString(CultureInfo.InvariantCulture)));

        int segment = -1;
        if (run is { } r)
        {
            int i0 = refs.FindIndex(f => Math.Abs(f.At - Math.Min(r.From, r.To)) <= Exact);
            int i1 = refs.FindIndex(f => Math.Abs(f.At - Math.Max(r.From, r.To)) <= Exact);
            if (i0 >= 0 && i1 == i0 + 1) segment = i0;
        }

        using Transaction t = DrwStrictFailures.Start(doc, "Stair dimensions " + view.Name, out DrwStrictFailures failures);
        ElementId id;
        string prefixNote = "";
        try
        {
            Line line = Line.CreateBound(P(refs[0].At), P(refs[refs.Count - 1].At));
            Dimension? dim = dimType != null ? doc.Create.NewDimension(view, line, array, dimType) : doc.Create.NewDimension(view, line, array);
            if (dim == null) { t.RollBack(); return row(what, "Failed", "Revit returned no dimension"); }
            id = dim.Id;
            if (run is { } rr)
            {
                if (segment < 0) prefixNote = "run segment not found, no prefix";
                else
                {
                    doc.Regenerate();
                    if (dim.NumberOfSegments == 0) dim.Prefix = rr.Prefix;
                    else dim.Segments.get_Item(segment).Prefix = rr.Prefix;
                }
            }
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            return row(what, "Failed", ex.Message);
        }
        if (t.Commit() != TransactionStatus.Committed)
            return row(what, "Failed", "Revit refused it" + (failures.Errors.Count > 0 ? ": " + string.Join("; ", failures.Errors) : ""));
        if (doc.GetElement(id) is not Dimension made)
            return row(what, "Failed", "Revit deleted the dimension when the change was committed");
        if (run is { } check && segment >= 0)
        {
            string got = made.NumberOfSegments == 0 ? made.Prefix : made.Segments.get_Item(segment).Prefix;
            if (got != check.Prefix) prefixNote = $"prefix not kept (reads '{got}')";
        }
        return row(what, "Created", string.Join("; ", new[] { values, prefixNote }.Where(s => s.Length > 0)));
    }
}
