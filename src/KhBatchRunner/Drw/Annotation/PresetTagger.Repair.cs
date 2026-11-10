using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

internal static partial class PresetTagger
{
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
}
