// Builds the Batch Interop bench models (docs/interop-runtime-plan.md, Step 1) from Revit's own
// Snowdon Towers samples. Run it as the BODY of a KH MCP run_code call (nothing needs to be open);
// it opens everything in the background and closes it again.
//
// Before running, copy into <bench>:
//   src\Snowdon-Arch.rvt, src\Snowdon-HVAC.rvt                 (the hosts)
//   Bench-Link-Structural.rvt, Bench-Link-HVAC.rvt, Bench-Link-Plumbing.rvt   (the links)
// Output: Bench-Arch-01.rvt and Bench-MEP-01.rvt, workshared centrals with the links on a "Links"
// workset (first link pinned, the rest unpinned), plus seeded failures: a view on no sheet with no
// View Group, an imported and a linked DWG, a raster image, duplicate instances, an unused wall type.
// The -02..-04 copies are plain file copies (the probes open them detached).

string bench = Environment.GetEnvironmentVariable("KHBATCH_TEST_ROOT") is { Length: > 0 } root
    ? Path.Combine(root, "interopbench") : @"C:\Users\SiwakornRodsangvan\KhBatchTest\interopbench";
var rvt = app.Application;
var report = new List<string>();

void Try(string what, Action act)
{
    try { act(); report.Add("  ok   " + what); }
    catch (Exception ex) { report.Add("  FAIL " + what + ": " + ex.GetType().Name + " " + ex.Message); }
}

void Tx(Document d, string name, Action act)
{
    using var t = new Transaction(d, name);
    t.Start();
    act();
    t.Commit();
}

void Build(string src, string outName, string[] links)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    report.Add(outName + ":");
    Document d = rvt.OpenDocumentFile(Path.Combine(bench, "src", src));
    try
    {
        if (!d.IsWorkshared) d.EnableWorksharing("Shared Levels and Grids", "Workset1");
        WorksetId linksWs = WorksetId.InvalidWorksetId;
        Tx(d, "Links workset", () => linksWs = Workset.Create(d, "Links").Id);

        for (int i = 0; i < links.Length; i++)
        {
            string link = links[i];
            Try("link " + link + (i == 0 ? " (pinned)" : " (unpinned)"), () => Tx(d, "Link " + link, () =>
            {
                var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(Path.Combine(bench, link));
                LinkLoadResult r = RevitLinkType.Create(d, mp, new RevitLinkOptions(false));
                RevitLinkInstance inst = RevitLinkInstance.Create(d, r.ElementId);
                foreach (Element e in new Element[] { inst, d.GetElement(r.ElementId) })
                    e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.Set(linksWs.IntegerValue);
                inst.Pinned = i == 0;
            }));
        }

        Level level = new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation).First();
        ViewFamilyType planType = new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.FloorPlan);
        ViewPlan? plan = null;
        Try("view on no sheet, no View Group", () => Tx(d, "Bench view", () =>
        {
            plan = ViewPlan.Create(d, planType.Id, level.Id);
            plan.Name = "BENCH - not on sheet";
        }));

        Try("unused wall type", () => Tx(d, "Unused type", () =>
            new FilteredElementCollector(d).OfClass(typeof(WallType)).Cast<WallType>()
                .First(w => w.Kind == WallKind.Basic).Duplicate("BENCH Unused Wall")));

        Try("3 duplicate instances", () => Tx(d, "Duplicates", () =>
        {
            int made = 0;
            foreach (FamilyInstance fi in new FilteredElementCollector(d).OfClass(typeof(FamilyInstance))
                         .Cast<FamilyInstance>().Where(f => f.Host == null && f.SuperComponent == null).Take(20))
            {
                try { ElementTransformUtils.CopyElement(d, fi.Id, XYZ.Zero); made++; } catch { }
                if (made == 3) break;
            }
            if (made < 3) throw new InvalidOperationException("only " + made + " copied");
        }));

        if (plan != null)
        {
            string tmp = Path.Combine(bench, "src");
            Try("imported DWG + linked DWG", () =>
            {
                d.Export(tmp, "bench-cad", new List<ElementId> { plan.Id }, new DWGExportOptions());
                string dwg = Directory.GetFiles(tmp, "bench-cad*.dwg").First();
                Tx(d, "CAD", () =>
                {
                    d.Import(dwg, new DWGImportOptions { ThisViewOnly = true }, plan, out ElementId _);
                    d.Link(dwg, new DWGImportOptions { ThisViewOnly = true }, plan, out ElementId _);
                });
            });
            Try("raster image", () =>
            {
                // A model-view export (not a screen capture) gives the PNG to place.
                var opts = new ImageExportOptions
                {
                    FilePath = Path.Combine(tmp, "bench-image"), ExportRange = ExportRange.SetOfViews,
                    ZoomType = ZoomFitType.Zoom, Zoom = 50, HLRandWFViewsFileType = ImageFileType.PNG,
                };
                opts.SetViewsAndSheets(new List<ElementId> { plan.Id });
                d.ExportImage(opts);
                string png = Directory.GetFiles(tmp, "bench-image*.png").First();
                Tx(d, "Raster", () =>
                {
                    ImageType type = ImageType.Create(d, new ImageTypeOptions(png, false, ImageTypeSource.Import));
                    ImageInstance.Create(d, plan, type.Id, new ImagePlacementOptions());
                });
            });
        }

        var so = new SaveAsOptions { OverwriteExistingFile = true };
        so.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
        d.SaveAs(Path.Combine(bench, outName + "-01.rvt"), so);
        report.Add($"  saved {outName}-01.rvt; warnings={d.GetWarnings().Count}; {sw.Elapsed.TotalSeconds:0}s");
    }
    finally { d.Close(false); }
}

Build("Snowdon-Arch.rvt", "Bench-Arch", new[] { "Bench-Link-Structural.rvt", "Bench-Link-HVAC.rvt" });
Build("Snowdon-HVAC.rvt", "Bench-MEP", new[] { "Bench-Link-Plumbing.rvt" });
return report;
