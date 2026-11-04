#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW BUILD"): writes Enlarge Door &amp; Window's detail item families from
/// Metric Detail Item.rft — parameters, reference planes, labelled dimensions and frame lines
/// locked to the planes — then flexes them and checks every line followed. The Revit API cannot
/// create labels, so the user opens each family once and places them (Mark, Description, Label,
/// Value…) before it is bundled. Output: DW_BUILD_OUT (default the report's folder).
/// </summary>
internal static partial class DrwSelfTest
{
    /// <summary>The detail item template of the Revit year this runs in (DW PORT builds 2024's set).</summary>
    private static string DwTemplate(Document doc) =>
        $@"C:\ProgramData\Autodesk\RVT {doc.Application.VersionNumber}\Family Templates\English\Metric Detail Item.rft";

    private static void DwBuildFamilies(Document doc)
    {
        string outDir = Environment.GetEnvironmentVariable("DW_BUILD_OUT") is { Length: > 0 } o ? o : Path.GetDirectoryName(_report)!;
        Directory.CreateDirectory(outDir);

        // Header: the cell's frame. Origin = the cell's top-left. Box 1 (elevation, or plan when the
        // preset puts plan first), box 2, then the header strip with Mark and Description.
        DwBuildFamily(doc, Path.Combine(outDir, "KH_DW_Header.rfa"),
            lengths: new[] { ("Cell Width", 3500.0), ("Box 1 Height", 2750.0), ("Box 2 Height", 1250.0), ("Header Height", 400.0) },
            texts: new[] { "KH_DW Mark", "KH_DW Description", "KH_DW Box 1 Title", "KH_DW Box 2 Title", "KH_DW Qty" },
            horizontals: new[] { "Box 1 Height", "Box 2 Height", "Header Height" },
            divider: null);

        // Row: one line of the table — label | value. Row Height = Line Height x Lines, so a value
        // wrapping to 3 lines makes a row three lines tall.
        DwBuildFamily(doc, Path.Combine(outDir, "KH_DW_Row.rfa"),
            lengths: new[] { ("Row Width", 3500.0), ("Label Width", 1100.0), ("Line Height", 200.0) },
            texts: new[] { "KH_DW Label", "KH_DW Value" },
            horizontals: new[] { "Row Height" },
            divider: "Label Width");

        DwBuildSeed(doc, Path.Combine(outDir, "KH_DW_Seed.rvt"));
    }

    /// <summary>
    /// The seed a model without legends gets its first legend from: a metric project holding one
    /// legend with one legend component (the smallest door type of the source's legend). Then the
    /// round trip the product depends on: the seed's legend copied INTO a model, in a rolled-back
    /// transaction, must arrive as a legend with exactly one component.
    /// </summary>
    private static void DwBuildSeed(Document doc, string path)
    {
        if (File.Exists(path) && Environment.GetEnvironmentVariable("DW_BUILD_FORCE") != "1")
        {
            Info($"{Path.GetFileName(path)} exists — left alone (DW_BUILD_FORCE=1 rebuilds it)");
            return;
        }
        string template = $@"C:\ProgramData\Autodesk\RVT {doc.Application.VersionNumber}\Templates\Default_M_ENU.rte";
        Element comp = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LegendComponents)
            .WhereElementIsNotElementType().First();
        var legend = (View)doc.GetElement(comp.OwnerViewId);
        Document seed = doc.Application.NewProjectDocument(template);
        try
        {
            using (var t = new Transaction(seed, "seed"))
            {
                t.Start();
                ICollection<ElementId> copied = ElementTransformUtils.CopyElements(doc, new List<ElementId> { legend.Id },
                    seed, Transform.Identity, new CopyPasteOptions());
                var seedLegend = copied.Select(seed.GetElement).OfType<View>().First(v => v.ViewType == ViewType.Legend);
                seedLegend.Name = "KH DW Seed";
                var comps = new FilteredElementCollector(seed, seedLegend.Id).OfCategory(BuiltInCategory.OST_LegendComponents).ToElementIds().ToList();
                foreach (ElementId id in comps.Skip(1)) seed.Delete(id);
                ElementId legendId = seedLegend.Id;
                t.Commit();
                Check("the seed legend survives its commit", seed.GetElement(legendId) is View);
                Info($"seed: {comps.Count} components copied, 1 kept");
            }
            seed.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            DwDropBackups(path);
            Info("saved " + path);
        }
        catch (Exception ex)
        {
            Fail(_scenario, $"seed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return;
        }
        finally { seed.Close(false); }

        // Round trip, from the saved file, into this model; rolled back.
        Document back = doc.Application.OpenDocumentFile(path);
        try
        {
            var seedLegend = new FilteredElementCollector(back).OfClass(typeof(View)).Cast<View>().First(v => v.ViewType == ViewType.Legend && !v.IsTemplate);
            int familiesBefore = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
            using var t = new Transaction(doc, "seed round trip");
            t.Start();
            ICollection<ElementId> ids = ElementTransformUtils.CopyElements(back, new List<ElementId> { seedLegend.Id },
                doc, Transform.Identity, new CopyPasteOptions());
            View? arrived = ids.Select(doc.GetElement).OfType<View>().FirstOrDefault(v => v.ViewType == ViewType.Legend);
            int n = arrived == null ? -1 : new FilteredElementCollector(doc, arrived.Id).OfCategory(BuiltInCategory.OST_LegendComponents).GetElementCount();
            int familiesAfter = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
            Info($"round trip: {ids.Count} ids, legend '{arrived?.Name}', {n} components, families {familiesBefore} -> {familiesAfter}");
            Check("the seed's legend copies into a model with its one component", arrived != null && n == 1, $"{n}");
            t.RollBack();
        }
        finally { back.Close(false); }
    }

    private static void DwDropBackups(string path)
    {
        foreach (string f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".0*" + Path.GetExtension(path)))
            File.Delete(f);
    }

    private static void DwBuildFamily(Document doc, string path, (string Name, double Mm)[] lengths, string[] texts,
        string[] horizontals, string? divider)
    {
        // The user edits these after they are built (labels, nested families): never overwrite one.
        if (File.Exists(path) && Environment.GetEnvironmentVariable("DW_BUILD_FORCE") != "1")
        {
            Info($"{Path.GetFileName(path)} exists — left alone (DW_BUILD_FORCE=1 rebuilds it)");
            return;
        }
        Info("building " + Path.GetFileName(path));
        Document fam = doc.Application.NewFamilyDocument(DwTemplate(doc));
        try
        {
            FamilyManager fm = fam.FamilyManager;
            View view = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<View>().First(v => !v.IsTemplate);
            using (var t = new Transaction(fam, "KH DW family"))
            {
                t.Start();
                if (fm.CurrentType == null) fm.NewType("Default");
                var p = new Dictionary<string, FamilyParameter>();
                foreach ((string name, double mm) in lengths)
                {
                    p[name] = fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, true);
                    fm.Set(p[name], mm / 304.8);
                }
                if (horizontals.Contains("Row Height"))
                {
                    p["Lines"] = fm.AddParameter("Lines", GroupTypeId.Geometry, SpecTypeId.Int.Integer, true);
                    fm.Set(p["Lines"], 1);
                    p["Row Height"] = fm.AddParameter("Row Height", GroupTypeId.Geometry, SpecTypeId.Length, true);
                    fm.SetFormula(p["Row Height"], "Line Height * Lines");
                    fam.Regenerate();
                }
                foreach (string name in texts)
                    fm.AddParameter(name, GroupTypeId.Text, SpecTypeId.String.Text, true);

                // The template's two centre planes are the left edge (x = 0) and the top (y = 0).
                var planes = new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
                ReferencePlane left = planes.First(rp => Math.Abs(rp.Direction.X) < 1e-9);   // runs along Y
                ReferencePlane top = planes.First(rp => Math.Abs(rp.Direction.Y) < 1e-9);    // runs along X
                Info("  template planes: " + string.Join("; ", planes.Select(rp => $"'{rp.Name}' dir {rp.Direction} at {rp.BubbleEnd}")));
                double width = p[lengths[0].Name] is var wp ? fm.CurrentType.AsDouble(wp) ?? 0 : 0;

                ReferencePlane Plane(XYZ a, XYZ b, string name)
                {
                    ReferencePlane rp = fam.FamilyCreate.NewReferencePlane(a, b, XYZ.BasisZ, view);
                    rp.Name = name;
                    fam.Regenerate();
                    return rp;
                }
                void Label(ReferencePlane a, ReferencePlane b, XYZ at, XYZ along, FamilyParameter fp)
                {
                    var refs = new ReferenceArray();
                    refs.Append(a.GetReference());
                    refs.Append(b.GetReference());
                    Dimension d = fam.FamilyCreate.NewDimension(view, Line.CreateBound(at, at + along), refs);
                    d.FamilyLabel = fp;
                }

                ReferencePlane right = Plane(new XYZ(width, 1, 0), new XYZ(width, -1, 0), "Right");
                Label(left, right, new XYZ(0, 0.8, 0), XYZ.BasisX, p[lengths[0].Name]);

                // Horizontal planes, top down, each a labelled distance below the last.
                var rows = new List<ReferencePlane> { top };
                double y = 0;
                foreach (string h in horizontals)
                {
                    y -= fm.CurrentType.AsDouble(p[h]) ?? 0;
                    ReferencePlane rp = Plane(new XYZ(-1, y, 0), new XYZ(width + 1, y, 0), h);
                    Label(rows[rows.Count - 1], rp, new XYZ(-0.8, 0, 0), XYZ.BasisY, p[h]);
                    rows.Add(rp);
                }
                ReferencePlane? div = null;
                if (divider != null)
                {
                    double dx = fm.CurrentType.AsDouble(p[divider]) ?? 0;
                    div = Plane(new XYZ(dx, 1, 0), new XYZ(dx, -1, 0), divider);
                    Label(left, div, new XYZ(0, 0.5, 0), XYZ.BasisX, p[divider]);
                }
                fam.Regenerate();

                // Frame lines on the planes, each locked along its plane and at both ends.
                double bottom = y;
                void LockedLine(ReferencePlane on, XYZ a, XYZ b, ReferencePlane endA, ReferencePlane endB)
                {
                    DetailCurve c = fam.FamilyCreate.NewDetailCurve(view, Line.CreateBound(a, b));
                    fam.FamilyCreate.NewAlignment(view, on.GetReference(), c.GeometryCurve.Reference);
                    fam.FamilyCreate.NewAlignment(view, endA.GetReference(), c.GeometryCurve.GetEndPointReference(0));
                    fam.FamilyCreate.NewAlignment(view, endB.GetReference(), c.GeometryCurve.GetEndPointReference(1));
                }
                ReferencePlane last = rows[rows.Count - 1];
                LockedLine(left, new XYZ(0, 0, 0), new XYZ(0, bottom, 0), top, last);
                LockedLine(right, new XYZ(width, 0, 0), new XYZ(width, bottom, 0), top, last);
                double yy = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (i > 0) yy -= fm.CurrentType.AsDouble(p[horizontals[i - 1]]) ?? 0;
                    LockedLine(rows[i], new XYZ(0, yy, 0), new XYZ(width, yy, 0), left, right);
                }
                if (div != null)
                    LockedLine(div, new XYZ(div.BubbleEnd.X, 0, 0), new XYZ(div.BubbleEnd.X, bottom, 0), top, last);
                t.Commit();
            }

            // Flex: double every length; every line must still meet the frame.
            using (var t = new Transaction(fam, "flex"))
            {
                t.Start();
                foreach ((string name, double mm) in lengths)
                    fm.Set(fm.get_Parameter(name), 2 * mm / 304.8);
                fam.Regenerate();
                var curves = new FilteredElementCollector(fam).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                    .Where(c => c.CurveElementType == CurveElementType.DetailCurve).ToList();
                double w2 = 2 * lengths[0].Mm / 304.8;
                double minX = curves.Min(c => Math.Min(c.GeometryCurve.GetEndPoint(0).X, c.GeometryCurve.GetEndPoint(1).X));
                double maxX = curves.Max(c => Math.Max(c.GeometryCurve.GetEndPoint(0).X, c.GeometryCurve.GetEndPoint(1).X));
                var horiz = curves.Where(c => Math.Abs(c.GeometryCurve.GetEndPoint(0).Y - c.GeometryCurve.GetEndPoint(1).Y) < 1e-6).ToList();
                bool spans = horiz.All(c => Math.Abs(c.GeometryCurve.Length - w2) < 1e-6);
                Check($"{Path.GetFileName(path)}: flexed width {SpMm(w2)} mm — frame spans it", Math.Abs(minX) < 1e-6 && Math.Abs(maxX - w2) < 1e-6 && spans,
                    $"x {SpMm(minX)}..{SpMm(maxX)}, horizontals " + string.Join("/", horiz.Select(c => SpMm(c.GeometryCurve.Length))));
                t.RollBack();
            }

            var save = new SaveAsOptions { OverwriteExistingFile = true };
            fam.SaveAs(path, save);
            DwDropBackups(path);
            Info("saved " + path);
        }
        catch (Exception ex)
        {
            Fail(_scenario, $"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            fam.Close(false);
        }
    }
}
#endif
