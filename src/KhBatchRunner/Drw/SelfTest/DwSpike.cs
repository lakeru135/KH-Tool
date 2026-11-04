#if EDITION_DRW
using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Spike for Enlarge Door &amp; Window (2026-10-20): what the API lets us do with legends before the
/// tool is built on it. A: can a legend view and a legend component be made, and pointed at a
/// door/window type and a view direction? B: can a legend component be dimensioned, and does the
/// dimension follow when the type's width changes? Everything is logged as info; checks mark only
/// the facts the design depends on.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void DwSpike(Document doc)
    {
        List<View> legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate).ToList();
        List<Element> comps = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LegendComponents)
            .WhereElementIsNotElementType().ToElements().ToList();
        Info($"model: {legends.Count} legends, {comps.Count} legend components");
        foreach (Element c in comps.Take(15)) Info("  comp " + DescribeComp(doc, c));

        Document? seedDoc = null;
        try
        {
            if (legends.Count == 0 || comps.Count == 0)
            {
                // Look for a seed in the shipped samples and copy its legend across documents.
                foreach (string path in new[]
                         {
                             @"C:\Program Files\Autodesk\Revit 2026\Samples\Snowdon Towers Sample Architectural.rvt",
                             @"C:\Program Files\Autodesk\Revit 2026\Samples\サンプル意匠.rvt"
                         })
                {
                    if (!System.IO.File.Exists(path)) continue;
                    var opt = new OpenOptions();
                    if (BasicFileInfo.Extract(path).IsWorkshared)
                        opt.DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets;
                    Document s = doc.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), opt);
                    var sComps = new FilteredElementCollector(s).OfCategory(BuiltInCategory.OST_LegendComponents)
                        .WhereElementIsNotElementType().ToElements();
                    Info($"seed candidate {System.IO.Path.GetFileName(path)}: {sComps.Count} legend components");
                    if (sComps.Count > 0) { seedDoc = s; break; }
                    s.Close(false);
                }
                if (seedDoc != null) SpikeCopyAcross(doc, seedDoc);
                legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate).ToList();
                comps = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LegendComponents)
                    .WhereElementIsNotElementType().ToElements().ToList();
            }
            Check("a legend and a legend component are available (in model or copied from a seed)",
                legends.Count > 0 && comps.Count > 0, $"{legends.Count} legends, {comps.Count} components");
            if (legends.Count == 0 || comps.Count == 0) return;
            SpikeInLegend(doc, comps[0]);
        }
        finally
        {
            try { seedDoc?.Close(false); } catch (Exception ex) { Info("seed close: " + ex.Message); }
        }
    }

    private static string DescribeComp(Document doc, Element c)
    {
        Parameter? p = c.get_Parameter(BuiltInParameter.LEGEND_COMPONENT);
        Parameter? v = c.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW);
        string sym = p == null ? "-" : (doc.GetElement(p.AsElementId()) is ElementType et
            ? $"{et.Category?.Name}: {et.FamilyName} : {et.Name}" : p.AsValueString() ?? "?");
        string view = v == null ? "-" : $"{v.StorageType} {v.AsInteger()} '{v.AsValueString()}'";
        return $"#{c.Id} in '{(doc.GetElement(c.OwnerViewId) as View)?.Name}' -> {sym}; view {view}";
    }

    private static void SpikeCopyAcross(Document doc, Document seed)
    {
        Element comp = new FilteredElementCollector(seed).OfCategory(BuiltInCategory.OST_LegendComponents)
            .WhereElementIsNotElementType().First();
        View legend = (View)seed.GetElement(comp.OwnerViewId);
        using var t = new Transaction(doc, "spike copy legend");
        t.Start();
        try
        {
            ICollection<ElementId> copied = ElementTransformUtils.CopyElements(seed,
                new List<ElementId> { legend.Id }, doc, Transform.Identity, new CopyPasteOptions());
            Info($"copied legend view across documents: {copied.Count} ids -> " +
                string.Join(", ", copied.Select(i => doc.GetElement(i)?.GetType().Name)));
            t.Commit();
        }
        catch (Exception ex)
        {
            Info("copy legend view across documents FAILED: " + ex.GetType().Name + ": " + ex.Message);
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
        }
    }

    private static void SpikeInLegend(Document doc, Element seedComp)
    {
        var src = (View)doc.GetElement(seedComp.OwnerViewId);
        FamilySymbol? door = SpikeSymbolWithInstances(doc, BuiltInCategory.OST_Doors);
        FamilySymbol? window = SpikeSymbolWithInstances(doc, BuiltInCategory.OST_Windows);
        Info($"door type: {door?.FamilyName} : {door?.Name}; window type: {window?.FamilyName} : {window?.Name}");
        if (door == null) { Check("model has a placed door type", false); return; }

        using var t = new Transaction(doc, "spike legend");
        t.Start();

        // A1: a new, empty legend by duplicating one (plain Duplicate copies no components).
        View legend = (View)doc.GetElement(src.Duplicate(ViewDuplicateOption.Duplicate));
        legend.Name = "KH DW spike legend";
        int inNew = new FilteredElementCollector(doc, legend.Id).OfCategory(BuiltInCategory.OST_LegendComponents).GetElementCount();
        Check("View.Duplicate(Duplicate) makes a new, empty legend", legend.ViewType == ViewType.Legend && inNew == 0,
            $"type {legend.ViewType}, {inNew} components");
        Info($"legend scale {legend.Scale}");

        // A2: copy the seed component into it and point it at the door type.
        ElementId compId = ElementTransformUtils.CopyElements(src, new List<ElementId> { seedComp.Id }, legend,
            Transform.Identity, new CopyPasteOptions()).First();
        Element comp = doc.GetElement(compId);
        Parameter pType = comp.get_Parameter(BuiltInParameter.LEGEND_COMPONENT);
        bool typeSet = false;
        try { typeSet = pType.Set(door.Id); } catch (Exception ex) { Info("set LEGEND_COMPONENT threw " + ex.Message); }
        doc.Regenerate();
        Check("LEGEND_COMPONENT accepts a door type id", typeSet && pType.AsElementId() == door.Id, DescribeComp(doc, comp));

        // A3: the view direction values. Probe each and record the extents it gives.
        Parameter pView = comp.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW);
        Info($"view param: storage {pView.StorageType}, readonly {pView.IsReadOnly}, now {pView.AsInteger()} '{pView.AsValueString()}'");
        double w = SpikeLen(door, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.FURNITURE_WIDTH);
        double h = SpikeLen(door, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.GENERIC_HEIGHT);
        Info($"door type width {SpMm(w)} mm, height {SpMm(h)} mm");
        // -7 is Elevation : Front (seen on every seed component). Scanning -12..2 crashed Regenerate
        // with an SEHException at -12, so only that value is set here; plan is probed at the end.
        const int frontValue = -7;
        pView.Set(frontValue);
        doc.Regenerate();
        BoundingBoxXYZ? fbb = comp.get_BoundingBox(legend);
        Info($"front: '{pView.AsValueString()}', box {(fbb == null ? "none" : $"{SpMm(fbb.Max.X - fbb.Min.X)} x {SpMm(fbb.Max.Y - fbb.Min.Y)} mm")}");

        // B: dimension the component's elevation. Collect its view geometry with references.
        var opts = new Options { View = legend, ComputeReferences = true, IncludeNonVisibleObjects = false };
        var lines = new List<(Line L, Reference R, string From)>();
        var faces = new List<(PlanarFace F, string From)>();
        SpikeWalk(comp.get_Geometry(opts), lines, faces, "top", 0);
        Info($"geometry: {lines.Count} lines with references, {faces.Count} planar faces with references");
        foreach (var g in lines.GroupBy(l => l.From)) Info($"  lines from {g.Key}: {g.Count()}");

        // Vertical lines (in the legend's plane, X/Y) at the two extremes in X → a width string.
        XYZ right = legend.RightDirection, up = legend.UpDirection;
        var verticals = lines.Where(l => Math.Abs(l.L.Direction.DotProduct(right)) < 1e-6
                                         && Math.Abs(l.L.Direction.DotProduct(up)) > 0.999).ToList();
        var horizontals = lines.Where(l => Math.Abs(l.L.Direction.DotProduct(up)) < 1e-6
                                           && Math.Abs(l.L.Direction.DotProduct(right)) > 0.999).ToList();
        Info($"{verticals.Count} vertical, {horizontals.Count} horizontal lines");
        Info($"legend dir {legend.ViewDirection}, origin {legend.Origin}; line depths " +
            string.Join(", ", lines.Select(l => Math.Round(l.L.Origin.DotProduct(legend.ViewDirection) * 304.8)).Distinct().Take(8)));

        // One geometry source per dimension: instance edges, or symbol edges. Mixing them (the first
        // run) gave -305 mm for both. Each variant in a sub-transaction; the closest to the type wins.
        // Concluded 2026-10-20: dimensions on a legend component's own edge references (instance or
        // symbol geometry, rebound to the component or not) are created but never resolve: 0
        // segments, Value -1 ft. The fallback: detail lines on the edges found exactly the type's
        // width / height apart, dimensioned; the tool moves the lines when the type changes.
        double? Pair(List<(Line L, Reference R, string From)> ls, XYZ across, double target) =>
            (from a in ls from b in ls
             let d = b.L.Origin.DotProduct(across) - a.L.Origin.DotProduct(across)
             where Math.Abs(d - target) < 1 / 304.8
             orderby a.L.Origin.DotProduct(across)
             select (double?)a.L.Origin.DotProduct(across)).FirstOrDefault();
        var instV = verticals.Where(l => l.From.EndsWith(">inst:edge")).ToList();
        var instH = horizontals.Where(l => l.From.EndsWith(">inst:edge")).ToList();
        double? x0 = Pair(instV, right, w), y0 = Pair(instH, up, h);
        Info($"nominal edges: left x {(x0 == null ? "-" : SpMm(x0.Value))} mm, bottom y {(y0 == null ? "-" : SpMm(y0.Value))} mm");
        Check("edges the type's width and height apart are found in the component's geometry", x0 != null && y0 != null);
        if (x0 == null || y0 == null) { t.RollBack(); return; }

        DetailCurve VLine(double x) => doc.Create.NewDetailCurve(legend,
            Line.CreateBound(new XYZ(x, y0.Value, 0), new XYZ(x, y0.Value + h, 0)));
        DetailCurve left = VLine(x0.Value), rightLine = VLine(x0.Value + w);
        var refs = new ReferenceArray();
        refs.Append(left.GeometryCurve.Reference);
        refs.Append(rightLine.GeometryCurve.Reference);
        double dy = y0.Value + h + 300 / 304.8;
        Dimension wDim = doc.Create.NewDimension(legend, Line.CreateBound(new XYZ(x0.Value, dy, 0), new XYZ(x0.Value + w, dy, 0)), refs);
        doc.Regenerate();
        Info($"width dim on detail lines: {SpMm(wDim.Value ?? 0)} mm");
        Check("a width dimension on detail lines at the edges reads the type's width", Math.Abs((wDim.Value ?? 0) - w) < 1 / 304.8,
            SpMm(wDim.Value ?? 0));

        // B2: the type gets wider. Where is the new right edge (is the left one fixed?), and does the
        // dimension follow when the line is moved there?
        Parameter? pw = door.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? door.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
        if (pw != null && !pw.IsReadOnly)
        {
            pw.Set(w + 100 / 304.8);
            doc.Regenerate();
            var after = new List<(Line L, Reference R, string From)>(); var af = new List<(PlanarFace, string)>();
            SpikeWalk(comp.get_Geometry(opts), after, af, "top", 0);
            var av = after.Where(l => l.From.EndsWith(">inst:edge") && Math.Abs(l.L.Direction.DotProduct(up)) > 0.999).ToList();
            double? nx = Pair(av, right, w + 100 / 304.8);
            Info($"after +100 mm: nominal left x {(nx == null ? "-" : SpMm(nx.Value))} mm (was {SpMm(x0.Value)})");
            if (nx != null)
            {
                ElementTransformUtils.MoveElement(doc, left.Id, new XYZ(nx.Value - x0.Value, 0, 0));
                ElementTransformUtils.MoveElement(doc, rightLine.Id, new XYZ(nx.Value + w + 100 / 304.8 - (x0.Value + w), 0, 0));
                doc.Regenerate();
            }
            double v = ((Dimension)doc.GetElement(wDim.Id)).Value ?? 0;
            Check("moving the lines to the new edges makes the dimension read the new width", nx != null && Math.Abs(v - w - 100 / 304.8) < 1 / 304.8,
                SpMm(v));
        }
        else Info("door width is not a writable type parameter; follow test skipped");

        // Plan view of the same component + a window, for the record.
        // Each candidate on its own fresh copy, in its own sub-transaction; stop at the first throw.
        int? planValue = null;
        foreach (int val in new[] { -8, -6, -5, -4, -9 })
        {
            using var st = new SubTransaction(doc);
            st.Start();
            try
            {
                Element probe = doc.GetElement(ElementTransformUtils.CopyElement(doc, comp.Id, new XYZ(20, 0, 0)).First());
                Parameter pv = probe.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW);
                bool ok = pv.Set(val);
                doc.Regenerate();
                BoundingBoxXYZ? bb = probe.get_BoundingBox(legend);
                string name = pv.AsValueString() ?? "";
                Info($"  view {val} set {ok} '{name}': {(bb == null ? "no box" : $"{SpMm(bb.Max.X - bb.Min.X)} x {SpMm(bb.Max.Y - bb.Min.Y)} mm")}");
                if (ok && name.IndexOf("Plan", StringComparison.OrdinalIgnoreCase) >= 0 && planValue == null)
                {
                    planValue = val;
                    var pl = new List<(Line, Reference, string)>(); var pf = new List<(PlanarFace, string)>();
                    SpikeWalk(probe.get_Geometry(opts), pl, pf, "top", 0);
                    Info($"  plan geometry: {pl.Count} lines with references");
                }
                st.Commit();
            }
            catch (Exception ex)
            {
                Info($"  view {val}: threw {ex.GetType().Name}: {ex.Message}");
                if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                break;
            }
        }
        Check("a view direction value gives Floor Plan", planValue != null, $"plan {planValue}");

        // Detail item placed in a legend (the table rows).
        FamilySymbol? detail = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_DetailComponents).Cast<FamilySymbol>().FirstOrDefault();
        if (detail != null)
        {
            if (!detail.IsActive) detail.Activate();
            FamilyInstance? fi = null;
            try { fi = doc.Create.NewFamilyInstance(new XYZ(0, -5, 0), detail, legend); }
            catch (Exception ex) { Info("detail item in legend threw " + ex.Message); }
            Check("a detail item can be placed in a legend", fi != null, detail.FamilyName);
        }
        else Info("no detail item family in model to test placement");

        t.RollBack();
    }

    private static Dimension? SpikeDim(Document doc, View legend, List<(Line L, Reference R, string From)> lines,
        XYZ across, XYZ along, string what, double target = 0)
    {
        if (lines.Count < 2) { Info($"{what}: fewer than two candidate lines"); return null; }
        var lo = lines.OrderBy(l => l.L.Origin.DotProduct(across)).First();
        var hi = lines.OrderBy(l => l.L.Origin.DotProduct(across)).Last();
        // The nominal size, not the trim: the outermost pair exactly target apart, when there is one.
        if (target > 0)
        {
            var pair = (from a in lines from b in lines
                        let d = b.L.Origin.DotProduct(across) - a.L.Origin.DotProduct(across)
                        where Math.Abs(d - target) < 1 / 304.8
                        orderby a.L.Origin.DotProduct(across)
                        select (a, b)).FirstOrDefault();
            if (pair.a.R != null) { lo = pair.a; hi = pair.b; }
            else Info($"{what}: no pair {SpMm(target)} apart; using the extremes");
        }
        Element comp = doc.GetElement(lo.R.ElementId) ?? doc.GetElement(hi.R.ElementId)!;
        string s0 = lo.R.ConvertToStableRepresentation(doc), s1 = hi.R.ConvertToStableRepresentation(doc);
        Info($"{what}: ref elem #{lo.R.ElementId} ({comp?.GetType().Name}/{comp?.Category?.Name}), stable '{s0}' / '{s1}'");
        var refs = new ReferenceArray();
        refs.Append(SpikeRebind(doc, lo.R, s0, what));
        refs.Append(SpikeRebind(doc, hi.R, s1, what));
        double minAlong = lines.Min(l => Math.Min(l.L.GetEndPoint(0).DotProduct(along), l.L.GetEndPoint(1).DotProduct(along)));
        XYZ p0 = lo.L.Origin - along * (lo.L.Origin.DotProduct(along) - minAlong + 1.0);
        XYZ vd = legend.ViewDirection;
        p0 -= vd * (p0 - legend.Origin).DotProduct(vd);   // onto the view plane
        Line dimLine = Line.CreateBound(p0, p0 + across * 1.0);
        try
        {
            Dimension d = doc.Create.NewDimension(legend, dimLine, refs);
            doc.Regenerate();
            Info($"{what}: dimension made from {lo.From} / {hi.From}, refs {lo.R.ElementReferenceType}; " +
                $"value {d.Value?.ToString() ?? "null"}, '{d.ValueString}', segments {d.Segments.Size}, refs {d.References.Size}");
            return d;
        }
        catch (Exception ex)
        {
            Info($"{what}: NewDimension threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>DW_SPIKE_REBIND=1: point a geometry reference at the legend component itself (the
    /// stable form's leading UniqueId swapped for the component's).</summary>
    internal static string SpikeRebindTo = "";
    private static Reference SpikeRebind(Document doc, Reference r, string stable, string what)
    {
        if (SpikeRebindTo.Length == 0) return r;
        int colon = stable.IndexOf(':');
        string rebuilt = SpikeRebindTo + stable.Substring(colon);
        try { return Reference.ParseFromStableRepresentation(doc, rebuilt); }
        catch (Exception ex) { Info($"{what}: rebind '{rebuilt}' threw {ex.Message}"); return r; }
    }

    private static void SpikeWalk(GeometryElement? ge, List<(Line, Reference, string)> lines,
        List<(PlanarFace, string)> faces, string from, int depth)
    {
        if (ge == null || depth > 4) return;
        foreach (GeometryObject o in ge)
        {
            switch (o)
            {
                case GeometryInstance gi:
                    SpikeWalk(gi.GetInstanceGeometry(), lines, faces, from + ">inst", depth + 1);
                    SpikeWalk(gi.GetSymbolGeometry(), lines, faces, from + ">sym", depth + 1);
                    break;
                case Line l when l.Reference != null:
                    lines.Add((l, l.Reference, from + ":line"));
                    break;
                case Solid s:
                    foreach (Edge e in s.Edges)
                        if (e.AsCurve() is Line el && e.Reference != null) lines.Add((el, e.Reference, from + ":edge"));
                    foreach (Face f in s.Faces)
                        if (f is PlanarFace pf && pf.Reference != null) faces.Add((pf, from));
                    break;
            }
        }
    }

    private static FamilySymbol? SpikeSymbolWithInstances(Document doc, BuiltInCategory cat) =>
        new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>()
            .GroupBy(i => i.Symbol.Id).OrderByDescending(g => g.Count()).Select(g => g.First().Symbol).FirstOrDefault();

    private static double SpikeLen(Element type, params BuiltInParameter[] bips)
    {
        foreach (BuiltInParameter b in bips)
            if (type.get_Parameter(b) is { StorageType: StorageType.Double } p) return p.AsDouble();
        return 0;
    }

    private static string SpMm(double ft) => Math.Round(ft * 304.8).ToString("0");
}
#endif
