using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

internal static partial class PresetTagger
{
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
}
