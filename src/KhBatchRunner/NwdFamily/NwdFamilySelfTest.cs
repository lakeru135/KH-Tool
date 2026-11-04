#if !EDITION_DRW
using System.IO;
using System.Text;
using System.Text.Json;
using KhBatchRunner.NwdFamily;
using Application = Autodesk.Revit.ApplicationServices.Application;
using Autodesk.Revit.DB;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// NWD Helper's "Create rfa from NWD" cases in Parameter Helper's self-test
/// (<c>PARAMHELPER_SELFTEST_ONLY=nwd</c>): the shape rules on hand-made meshes, then .khnwd.json
/// fixtures through the real reader and builder. Each built .rfa is reopened and flexed — a size
/// parameter doubled must move the extrusion exactly as a family author would expect — and its
/// Navisworks parameters are read back.
/// </summary>
internal static partial class ParamHelperSelfTest
{
    private static void Nwd(Application app)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("NWD_PROBE"), "circle", StringComparison.OrdinalIgnoreCase))
        {
            Scenario("NWD: which diameter dimension drives an extruded circle (probe)", () => NwdCircleProbe(app));
            return;
        }
        Scenario("NWD: boxes and cylinders are recognised; open, notched and faceted shapes are not", NwdFitter);
        Scenario("NWD: one centred box — Width/Depth/Height resize it about its centre, bottom stays", () => NwdCentredBox(app));
        Scenario("NWD: two boxes off-centre — named per element, the side nearer the centre stays", () => NwdTwoBoxes(app));
        Scenario("NWD: an upright cylinder on the family centre — Diameter grows about the centre", () => NwdCentredCylinder(app));
        Scenario("NWD: an upright cylinder — Diameter keeps its axis, Height keeps its bottom", () => NwdCylinder(app));
        Scenario("NWD: an open mesh stays a mesh even with rebuild on", () => NwdOpenMesh(app));
        Scenario("NWD: a v2 file builds as before — one FreeForm solid, no parameters", () => NwdVersion2(app));
        Scenario("NWD: Navisworks properties — added, built-ins filled, clashes renamed, values typed", () => NwdParameters(app));
    }

    // ------------------------------------------------------------------ fitter

    private static void NwdFitter()
    {
        NwdShape? box = NwdShapeFitter.Fit(BoxMesh(-1, -0.5, 0, 1, 0.5, 2));
        Check(box?.Kind == NwdShapeKind.Box && Math.Abs(box.MaxX - 1) < 1e-9 && Math.Abs(box.MaxZ - 2) < 1e-9, "a 12-triangle box is a box");
        Check(NwdShapeFitter.Fit(BoxMesh(0, 0, 0, 1, 1, 1, subdivideTop: true))?.Kind == NwdShapeKind.Box, "a box with a split top face is still a box");
        Check(NwdShapeFitter.Fit(BoxMesh(0, 0, 0, 1, 1, 1, openTop: true)) == null, "a box without its top is not a box");
        Check(NwdShapeFitter.Fit(LMesh()) == null, "an L-shaped block is not a box");

        NwdShape? cyl = NwdShapeFitter.Fit(CylinderMesh(0.3, -0.2, 0.25, 0, 1.5, 24, fan: true));
        Check(cyl?.Kind == NwdShapeKind.Cylinder && Math.Abs(cyl.Radius - 0.25) < 1e-6 && Math.Abs(cyl.CentreX - 0.3) < 1e-6,
            $"a 24-sided fan-capped cylinder is a cylinder (r {cyl?.Radius:0.######})");
        Check(NwdShapeFitter.Fit(CylinderMesh(0, 0, 0.5, 0.1, 1, 9, fan: true))?.Kind == NwdShapeKind.Cylinder, "a 9-sided (odd) cylinder is a cylinder");
        Check(NwdShapeFitter.Fit(CylinderMesh(0, 0, 0.5, 0, 1, 16, fan: false))?.Kind == NwdShapeKind.Cylinder, "caps without a centre vertex still count");
        Check(NwdShapeFitter.Fit(CylinderMesh(0, 0, 0.5, 0, 1, 6, fan: true)) == null, "a hexagonal prism is not a cylinder");
    }

    // ------------------------------------------------------------------ builds

    private static void NwdCentredBox(Application app)
    {
        Document fam = NwdBuild(app, "NWD-box", "GenericModels", rebuild: true, new[] { ("Box", BoxMesh(-1, -0.75, 0, 1, 0.75, 2)) }, null, out NwdBuildReport report);
        try
        {
            Check(report.ParametricBoxes == 1 && report.ParametricFellBack == 0, "rebuilt as one resizable box: " + Flat(report));
            Check(Extrusions(fam).Count == 1 && Others(fam) == 0, $"the family holds one extrusion and nothing else ({Extrusions(fam).Count} / {Others(fam)})");
            NearLen(fam, "Width", 2);
            NearLen(fam, "Depth", 1.5);
            NearLen(fam, "Height", 2);
            Flex(fam, "Width", 4, b => Bx(b, -2, -0.75, 0, 2, 0.75, 2), "Width 4 ft grows both ways (EQ)");
            Flex(fam, "Depth", 3, b => Bx(b, -1, -1.5, 0, 1, 1.5, 2), "Depth 3 ft grows both ways (EQ)");
            Flex(fam, "Height", 4, b => Bx(b, -1, -0.75, 0, 1, 0.75, 4), "Height 4 ft grows up from the reference level");
        }
        finally { TryClose(fam); }
    }

    private static void NwdTwoBoxes(Application app)
    {
        Document fam = NwdBuild(app, "NWD-two", "MechanicalEquipment", rebuild: true, new[]
        {
            ("Base", BoxMesh(0.5, -1, 0.25, 3, 1, 0.75)),     // right of centre, raised 0.25
            ("Motor", BoxMesh(-2, -0.5, 0, -0.5, 0.5, 1)),    // left of centre
        }, null, out NwdBuildReport report);
        try
        {
            Check(report.ParametricBoxes == 2, "both rebuilt: " + Flat(report));
            Check(Param(fam, "Base Width") != null && Param(fam, "Motor Width") != null && Param(fam, "Width") == null,
                "parameters are named after each element");
            List<Extrusion> ex = Extrusions(fam);
            Flex(fam, "Base Width", 5, b => Bx(b, 0.5, -1, 0.25, 5.5, 1, 0.75), "Base Width: the side nearer the centre (x 0.5) stays", ex[0]);
            Flex(fam, "Motor Width", 3, b => Bx(b, -3.5, -0.5, 0, -0.5, 0.5, 1), "Motor Width: the side nearer the centre (x -0.5) stays", ex[1]);
            Flex(fam, "Base Height", 1, b => Bx(b, 0.5, -1, 0.25, 3, 1, 1.25), "Base Height: a raised bottom stays at 0.25", ex[0]);
        }
        finally { TryClose(fam); }
    }

    private static void NwdCylinder(Application app)
    {
        Document fam = NwdBuild(app, "NWD-cyl", "GenericModels", rebuild: true, new[] { ("Tank", CylinderMesh(0.5, 0.25, 0.75, 0.2, 1.7, 32, fan: true)) },
            null, out NwdBuildReport report);
        try
        {
            Check(report.ParametricCylinders == 1 && report.ParametricFellBack == 0, "rebuilt as a resizable cylinder: " + Flat(report));
            NearLen(fam, "Diameter", 1.5);
            NearLen(fam, "Height", 1.5);
            FlexCircle(fam, "Diameter", 3, 0.5, 0.25, 1.5, "Diameter 3 ft keeps the axis at (0.5, 0.25)");
            Flex(fam, "Height", 3, b => Math.Abs(b.Min.Z - 0.2) < 1e-6 && Math.Abs(b.Max.Z - 3.2) < 1e-6 ? null : $"z {b.Min.Z:0.###}-{b.Max.Z:0.###}",
                "Height 3 ft keeps the bottom at 0.2");
        }
        finally { TryClose(fam); }
    }

    private static void NwdCentredCylinder(Application app)
    {
        Document fam = NwdBuild(app, "NWD-cyl0", "GenericModels", rebuild: true, new[] { ("Pipe", CylinderMesh(0, 0, 0.5, 0, 2, 24, fan: true)) },
            null, out NwdBuildReport report);
        try
        {
            Check(report.ParametricCylinders == 1, "rebuilt as a resizable cylinder: " + Flat(report));
            FlexCircle(fam, "Diameter", 2, 0, 0, 1, "Diameter 2 ft grows about the family centre");
        }
        finally { TryClose(fam); }
    }

    private static void NwdOpenMesh(Application app)
    {
        Document fam = NwdBuild(app, "NWD-open", "GenericModels", rebuild: true, new[] { ("Tray", BoxMesh(0, 0, 0, 1, 1, 0.5, openTop: true)) },
            null, out NwdBuildReport report);
        try
        {
            Check(report.ParametricBoxes == 0 && report.MeshShapes + report.Solids == 1, "kept as fixed geometry: " + Flat(report));
            Check(Extrusions(fam).Count == 0 && Param(fam, "Width") == null, "no extrusion, no size parameters");
        }
        finally { TryClose(fam); }
    }

    private static void NwdVersion2(Application app)
    {
        Document fam = NwdBuild(app, "NWD-v2", "GenericModels", rebuild: false, new[] { ("Box", BoxMesh(-1, -1, 0, 1, 1, 1)) },
            null, out NwdBuildReport report, version: 2);
        try
        {
            Check(report.Solids == 1 && report.ParametricBoxes == 0, "one FreeForm solid, as before v3: " + Flat(report));
            Check(new FilteredElementCollector(fam).OfClass(typeof(FreeFormElement)).GetElementCount() == 1 && Extrusions(fam).Count == 0,
                "the family holds the FreeForm, no extrusion");
            Check(report.ParametersCreated + report.ParametersFilled == 0 && Param(fam, "Width") == null, "no parameters added");
        }
        finally { TryClose(fam); }
    }

    private static void NwdParameters(Application app)
    {
        var parameters = new (string Name, string Type, object Value)[]
        {
            ("Manufacturer", "text", "Grundfos"),
            ("Width", "length", 1.25),            // clashes with the box's own Width
            ("Family Name", "text", "nope"),      // looks like a Revit built-in; Revit accepts it as a family parameter
            ("Bad[Name]", "text", "x"),
            ("Count", "integer", 4),
            ("Weight", "number", 12.5),
            ("Is Spare", "yesno", true),
            ("Odd", "colour", "red"),             // a type this reader does not know: ignored
        };
        Document fam = NwdBuild(app, "NWD-params", "MechanicalEquipment", rebuild: true, new[] { ("Pump", BoxMesh(-1, -1, 0, 1, 1, 1)) },
            parameters, out NwdBuildReport report);
        try
        {
            FamilyManager fm = fam.FamilyManager;
            Log($"    parameters: {string.Join(", ", fm.GetParameters().Select(p => p.Definition.Name))}");
            Check(fm.CurrentType.AsString(Param(fam, "Manufacturer")!) == "Grundfos" && Param(fam, "Manufacturer (NW)") == null,
                "Manufacturer fills the template's own parameter");
            NearLen(fam, "Width", 2);
            Check(Param(fam, "Width (NW)") is { } w && Math.Abs((fm.CurrentType.AsDouble(w) ?? 0) - 1.25) < 1e-9 && !w.IsInstance,
                "a Width property beside the box's Width is added as 'Width (NW)', a type length");
            Check((Param(fam, "Family Name") ?? Param(fam, "Family Name (NW)")) is { } fn && fm.CurrentType.AsString(fn) == "nope",
                "a property named like a Revit built-in (Family Name) still lands, as itself or with (NW)");
            Check(Param(fam, "BadName") is { } bn && fm.CurrentType.AsString(bn) == "x", "forbidden characters are dropped from a name");
            Check(Param(fam, "Count") is { } c && c.Definition.GetDataType() == SpecTypeId.Int.Integer && fm.CurrentType.AsInteger(c) == 4, "integer");
            Check(Param(fam, "Weight") is { } wt && wt.Definition.GetDataType() == SpecTypeId.Number && Math.Abs((fm.CurrentType.AsDouble(wt) ?? 0) - 12.5) < 1e-9, "number");
            Check(Param(fam, "Is Spare") is { } sp && sp.Definition.GetDataType() == SpecTypeId.Boolean.YesNo && fm.CurrentType.AsInteger(sp) == 1, "yes/no");
            Check(Param(fam, "Odd") == null, "an unknown type is ignored");
            Check(report.ParametersSkipped == 0, "nothing skipped: " + Flat(report));
        }
        finally { TryClose(fam); }
    }

    // ------------------------------------------------------------------ probe

    /// <summary>
    /// 2026-10-21: a Diameter label on the solid's rim edge never drove the circle (and Regenerate took
    /// 9 minutes); labels on the sketch arcs made outside sketch mode did not drive it either. Tries
    /// each way in a fresh family and logs whether doubling the size doubles the extrusion.
    /// </summary>
    private static void NwdCircleProbe(Application app)
    {
        string template = NwdFamilyBuilder.FindTemplate(app, NwdCategories.Find("GenericModels"));
        // 2nd round: one arc labelled (drives, but grew from one side); now pin the centre point.
        foreach (string variant in new[] { "one arc, centre free", "one arc, centre locked (off-centre)", "one arc, centre aligned (at origin)" })
        {
            Document fam = app.NewFamilyDocument(template);
            try
            {
                FamilyParameter? size = null;
                Extrusion? ext = null;
                XYZ c0 = variant.Contains("origin") ? XYZ.Zero : new XYZ(0.5, 0.25, 0);
                ViewPlan plan = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate);
                var planes = new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
                ReferencePlane px = planes.First(p => Math.Abs(p.Normal.X) > 0.999 && Math.Abs(p.BubbleEnd.X) < 1e-6);
                ReferencePlane py = planes.First(p => Math.Abs(p.Normal.Y) > 0.999 && Math.Abs(p.BubbleEnd.Y) < 1e-6);
                using (var t = new Transaction(fam, "circle"))
                {
                    t.Start();
                    FamilyManager fm = fam.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType("T");
                    size = fm.AddParameter("Diameter", GroupTypeId.Geometry, SpecTypeId.Length, false);
                    fm.Set(size, 1.0);
                    var loop = new CurveArray();
                    loop.Append(Arc.Create(c0, 0.5, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                    loop.Append(Arc.Create(c0, 0.5, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                    var profile = new CurveArrArray();
                    profile.Append(loop);
                    ext = fam.FamilyCreate.NewExtrusion(true, profile, SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero)), 1);
                    fam.Regenerate();
                    Curve c = ext.Sketch.Profile.get_Item(0).get_Item(0);
                    fam.FamilyCreate.NewDiameterDimension(plan, c.Reference, c0 + new XYZ(1.5, 1.5, 0)).FamilyLabel = size;
                    fam.Regenerate();

                    var arcs = ext.Sketch.GetAllElements().Select(fam.GetElement).OfType<CurveElement>().Where(ce => ce.GeometryCurve is Arc).ToList();
                    Log($"    {variant}: {arcs.Count} sketch arc element(s), centre refs: {string.Join(", ", arcs.Select(ce => ce.CenterPointReference != null))}");
                    if (!variant.Contains("free"))
                        foreach (CurveElement ce in arcs)
                        {
                            Reference? centre = ce.CenterPointReference;
                            if (centre == null) { Log("      no centre reference"); continue; }
                            foreach ((ReferencePlane plane, XYZ along) in new[] { (px, XYZ.BasisX), (py, XYZ.BasisY) })
                            {
                                try
                                {
                                    if (variant.Contains("aligned"))
                                        fam.FamilyCreate.NewAlignment(plan, plane.GetReference(), centre);
                                    else
                                    {
                                        var refs = new ReferenceArray();
                                        refs.Append(plane.GetReference());
                                        refs.Append(centre);
                                        XYZ at = new XYZ(0, 0, 0) - along.CrossProduct(XYZ.BasisZ) * 2;
                                        fam.FamilyCreate.NewDimension(plan, Line.CreateBound(at, at + along), refs).IsLocked = true;
                                    }
                                    fam.Regenerate();
                                    Log($"      centre pinned to {(along.IsAlmostEqualTo(XYZ.BasisX) ? "X" : "Y")} plane");
                                }
                                catch (Exception ex) { Log($"      pin {(along.IsAlmostEqualTo(XYZ.BasisX) ? "X" : "Y")} threw {ex.GetType().Name}: {ex.Message}"); }
                            }
                        }
                    t.Commit();
                }

                using (var t = new Transaction(fam, "flex"))
                {
                    t.Start();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    double before = fam.FamilyManager.CurrentType.AsDouble(size!) ?? 0;
                    fam.FamilyManager.Set(size!, before * 2);
                    fam.Regenerate();
                    BoundingBoxXYZ b = fam.GetElement(ext!.Id).get_BoundingBox(null);
                    Log($"    {variant}: size {before:0.###} → {before * 2:0.###}: box ({b.Min.X:0.###}, {b.Min.Y:0.###})-({b.Max.X:0.###}, {b.Max.Y:0.###}) in {watch.Elapsed.TotalSeconds:0.0}s" +
                        $" — {(Math.Abs(b.Max.X - b.Min.X - 2) < 1e-6 ? "DRIVES" : "does not drive")}");
                    t.RollBack();
                }
            }
            catch (Exception ex) { Log($"    {variant}: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { TryClose(fam); }
        }
        Check(true, "probe logged");
    }

    private sealed class NwdProbeFailures : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (FailureMessageAccessor f in a.GetFailureMessages())
            {
                Log($"    failure {f.GetSeverity()}: {f.GetDescriptionText()}");
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
            }
            return FailureProcessingResult.Continue;
        }
    }

    // ------------------------------------------------------------------ build + read back

    private static Document NwdBuild(Application app, string name, string category, bool rebuild, (string Name, NwdMesh Mesh)[] meshes,
        (string Name, string Type, object Value)[]? parameters, out NwdBuildReport report, int version = 3)
    {
        string json = Path.Combine(_dir, name + ".khnwd.json");
        using (var stream = File.Create(json))
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", "KH-NWD Tool exchange");
            w.WriteNumber("version", version);
            w.WriteString("units", "feet");
            w.WriteString("familyName", name);
            w.WriteString("category", category);
            w.WriteString("sourceFile", "selftest.nwd");
            w.WriteStartArray("origin"); w.WriteNumberValue(10); w.WriteNumberValue(20); w.WriteNumberValue(0); w.WriteEndArray();
            if (version >= 3)
            {
                w.WriteBoolean("rebuildShapes", rebuild);
                w.WriteStartArray("parameters");
                foreach (var p in parameters ?? Array.Empty<(string, string, object)>())
                {
                    w.WriteStartObject();
                    w.WriteString("name", p.Name);
                    w.WriteString("type", p.Type);
                    w.WritePropertyName("value");
                    switch (p.Value)
                    {
                        case string s: w.WriteStringValue(s); break;
                        case bool b: w.WriteBooleanValue(b); break;
                        default: w.WriteNumberValue(Convert.ToDouble(p.Value)); break;
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteStartArray("meshes");
            foreach ((string meshName, NwdMesh m) in meshes)
            {
                w.WriteStartObject();
                w.WriteString("name", meshName);
                w.WriteString("layer", "Test");
                w.WriteStartArray("color"); w.WriteNumberValue(200); w.WriteNumberValue(120); w.WriteNumberValue(40); w.WriteEndArray();
                w.WriteStartArray("vertices"); foreach (double d in m.Vertices) w.WriteNumberValue(d); w.WriteEndArray();
                w.WriteStartArray("triangles"); foreach (int i in m.Triangles) w.WriteNumberValue(i); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        NwdExchangeFile export = NwdExchangeFile.Read(json);
        string rfa = Path.Combine(_dir, name + ".rfa");
        report = NwdFamilyBuilder.Build(app, export, rfa);
        Log("    built " + Flat(report));
        return app.OpenDocumentFile(rfa);
    }

    private static string Flat(NwdBuildReport r) => r.ToString().Replace("\n", " | ");

    private static FamilyParameter? Param(Document fam, string name) => fam.FamilyManager.get_Parameter(name);

    private static List<Extrusion> Extrusions(Document fam) =>
        new FilteredElementCollector(fam).OfClass(typeof(Extrusion)).Cast<Extrusion>().OrderBy(e => e.Id.Value).ToList();

    private static int Others(Document fam) =>
        new FilteredElementCollector(fam).OfClass(typeof(FreeFormElement)).GetElementCount() +
        new FilteredElementCollector(fam).OfClass(typeof(DirectShape)).GetElementCount();

    private static void NearLen(Document fam, string name, double want)
    {
        FamilyParameter? fp = Param(fam, name);
        double got = fp == null ? double.NaN : fam.FamilyManager.CurrentType.AsDouble(fp) ?? double.NaN;
        Check(fp != null && Math.Abs(got - want) < 1e-6 && !fp.IsInstance && fp.Definition.GetDataType() == SpecTypeId.Length,
            $"{name} is a type length of {want} ft (got {got:0.######})");
    }

    /// <summary>Sets one size, regenerates, checks the extrusion's box, rolls back.</summary>
    private static void Flex(Document fam, string name, double value, Func<BoundingBoxXYZ, string?> expect, string what, Extrusion? which = null)
    {
        using var t = new Transaction(fam, "flex " + name);
        t.Start();
        try
        {
            FamilyParameter fp = Param(fam, name) ?? throw new InvalidOperationException("no parameter " + name);
            fam.FamilyManager.Set(fp, value);
            fam.Regenerate();
            Extrusion e = which ?? Extrusions(fam).Single();
            BoundingBoxXYZ? box = fam.GetElement(e.Id).get_BoundingBox(null);
            string? wrong = box == null ? "no bounding box" : expect(box);
            Check(wrong == null, what + (wrong == null ? "" : $" — {wrong}"));
        }
        catch (Exception ex) { Check(false, $"{what} — threw {ex.GetType().Name}: {ex.Message}"); }
        finally { t.RollBack(); }
    }

    /// <summary>Sets a diameter, regenerates, checks the circle's centre and radius (not the bounding box: approximate on curves).</summary>
    private static void FlexCircle(Document fam, string name, double value, double cx, double cy, double r, string what)
    {
        using var t = new Transaction(fam, "flex " + name);
        t.Start();
        try
        {
            fam.FamilyManager.Set(Param(fam, name) ?? throw new InvalidOperationException("no parameter " + name), value);
            fam.Regenerate();
            var rim = NwdParametricShapes.Rim(Extrusions(fam).Single());
            Check(rim is { } c && Math.Abs(c.Centre.X - cx) < 1e-6 && Math.Abs(c.Centre.Y - cy) < 1e-6 && Math.Abs(c.Radius - r) < 1e-6,
                what + (rim is { } g ? $" (centre ({g.Centre.X:0.###}, {g.Centre.Y:0.###}), r {g.Radius:0.###})" : " (no circle)"));
        }
        catch (Exception ex) { Check(false, $"{what} — threw {ex.GetType().Name}: {ex.Message}"); }
        finally { t.RollBack(); }
    }

    /// <summary>Null when the box is (x0,y0,z0)-(x1,y1,z1) within 1e-6 ft, else what it is.</summary>
    private static string? Bx(BoundingBoxXYZ b, double x0, double y0, double z0, double x1, double y1, double z1)
    {
        bool ok = Math.Abs(b.Min.X - x0) < 1e-6 && Math.Abs(b.Min.Y - y0) < 1e-6 && Math.Abs(b.Min.Z - z0) < 1e-6 &&
                  Math.Abs(b.Max.X - x1) < 1e-6 && Math.Abs(b.Max.Y - y1) < 1e-6 && Math.Abs(b.Max.Z - z1) < 1e-6;
        return ok ? null : $"box ({b.Min.X:0.###}, {b.Min.Y:0.###}, {b.Min.Z:0.###})-({b.Max.X:0.###}, {b.Max.Y:0.###}, {b.Max.Z:0.###})";
    }

    // ------------------------------------------------------------------ meshes

    private static NwdMesh BoxMesh(double x0, double y0, double z0, double x1, double y1, double z1, bool openTop = false, bool subdivideTop = false)
    {
        var v = new List<double>();
        var t = new List<int>();
        int P(double x, double y, double z) { v.Add(x); v.Add(y); v.Add(z); return v.Count / 3 - 1; }
        int a = P(x0, y0, z0), b = P(x1, y0, z0), c = P(x1, y1, z0), d = P(x0, y1, z0);
        int e = P(x0, y0, z1), f = P(x1, y0, z1), g = P(x1, y1, z1), h = P(x0, y1, z1);
        void Q(int p, int q, int r, int s) { t.AddRange(new[] { p, q, r, p, r, s }); }
        Q(a, d, c, b);                       // bottom
        Q(a, b, f, e); Q(b, c, g, f); Q(c, d, h, g); Q(d, a, e, h);
        if (subdivideTop)
        {
            int m = P((x0 + x1) / 2, (y0 + y1) / 2, z1);
            t.AddRange(new[] { e, f, m, f, g, m, g, h, m, h, e, m });
        }
        else if (!openTop)
            Q(e, f, g, h);
        return new NwdMesh { Vertices = v.ToArray(), Triangles = t.ToArray() };
    }

    /// <summary>Two 1×1×1 boxes side by side plus one on top of the first: an L, not a box.</summary>
    private static NwdMesh LMesh()
    {
        NwdMesh a = BoxMesh(0, 0, 0, 2, 1, 1), b = BoxMesh(0, 0, 1, 1, 1, 2);
        int offset = a.Vertices.Length / 3;
        return new NwdMesh
        {
            Vertices = a.Vertices.Concat(b.Vertices).ToArray(),
            Triangles = a.Triangles.Concat(b.Triangles.Select(i => i + offset)).ToArray(),
        };
    }

    private static NwdMesh CylinderMesh(double cx, double cy, double r, double z0, double z1, int sides, bool fan)
    {
        var v = new List<double>();
        var t = new List<int>();
        int P(double x, double y, double z) { v.Add(x); v.Add(y); v.Add(z); return v.Count / 3 - 1; }
        var bottom = new int[sides];
        var top = new int[sides];
        for (int i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides;
            bottom[i] = P(cx + r * Math.Cos(a), cy + r * Math.Sin(a), z0);
            top[i] = P(cx + r * Math.Cos(a), cy + r * Math.Sin(a), z1);
        }
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            t.AddRange(new[] { bottom[i], bottom[j], top[j], bottom[i], top[j], top[i] });
        }
        if (fan)
        {
            int cb = P(cx, cy, z0), ct = P(cx, cy, z1);
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                t.AddRange(new[] { cb, bottom[j], bottom[i], ct, top[i], top[j] });
            }
        }
        else
            for (int i = 1; i + 1 < sides; i++)
                t.AddRange(new[] { bottom[0], bottom[i + 1], bottom[i], top[0], top[i], top[i + 1] });
        return new NwdMesh { Vertices = v.ToArray(), Triangles = t.ToArray() };
    }
}
#endif
