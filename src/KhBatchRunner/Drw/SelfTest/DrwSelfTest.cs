#if EDITION_DRW
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KhBatchRunner.Actions;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.Drw.Dialogs;
using KhBatchRunner.Drw.Enlarge;
using KhBatchRunner.Settings;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// DRW Helper's headless self-test. Launch Revit with <c>DRW_SELFTEST=&lt;report path&gt;</c> and
/// <c>DRW_SELFTEST_MODEL=&lt;.rvt&gt;</c>: on the first Idling tick it opens the model in the
/// background, runs every scenario, checks each result by reading the model back (never by trusting
/// the tool's own row), writes PASS/FAIL lines and a RESULT line, closes the model unsaved, and shuts
/// Revit down.
///
/// <para>Every scenario runs inside a TransactionGroup that is rolled back, so scenarios cannot see
/// each other's dimensions, grids or views. Fixture scenarios build their own grids far from the
/// model's, in their own new plans, with the model's grids hidden there — so what they expect is
/// fully known. The model-wide scenarios (Snowdon's plans, sections and elevations) check invariants
/// instead: every Created row is a dimension that exists, with the references it claims.</para>
///
/// <para>Match Datums and the dialogs are interactive-only in the product; here their engines and
/// the dialogs' Capture/Apply round trip are driven directly.</para>
/// </summary>
internal static partial class DrwSelfTest
{
    public const string ReportVariable = "DRW_SELFTEST";
    public const string ModelVariable = "DRW_SELFTEST_MODEL";

    private static UIControlledApplication? _app;
    private static EventHandler<IdlingEventArgs>? _handler;
    private static string _report = "";
    private static string _model = "";
    private static int _pass, _fail;
    private static readonly List<string> Failures = new();
    private static string _scenario = "";

    public static bool IsRequested(out string report, out string model)
    {
        report = Environment.GetEnvironmentVariable(ReportVariable) ?? "";
        model = Environment.GetEnvironmentVariable(ModelVariable) ?? "";
        return report.Length > 0 && model.Length > 0;
    }

    public static void ArmOnIdling(UIControlledApplication app, string report, string model)
    {
        _app = app;
        _report = report;
        _model = model;
        _handler = OnIdling;
        app.Idling += _handler;
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (_app != null && _handler != null) _app.Idling -= _handler;
        _handler = null;
        Document? doc = null;
        try
        {
            try { File.Delete(_report); } catch { /* fresh report, best effort */ }
            Log($"=== DRW Helper {Product.Version} self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            Log("Model: " + _model);
            var uiApp = (UIApplication)sender!;
            var started = DateTime.Now;
            doc = Open(uiApp.Application, _model);
            Log($"Opened in {(DateTime.Now - started).TotalSeconds:0}s: {doc.Title}");
            RunAll(doc);
        }
        catch (Exception ex)
        {
            Fail("harness", "FATAL: " + ex);
        }
        finally
        {
            Log($"RESULT: {_pass} passed, {_fail} failed");
            foreach (string f in Failures) Log("  FAILED " + f);
            try { doc?.Close(false); } catch (Exception ex) { Log("close: " + ex.Message); }
            Schedule.RevitShutdown.RequestCurrent("DRW self-test complete");
        }
    }

    private static Document Open(Autodesk.Revit.ApplicationServices.Application app, string path)
    {
        ModelPath mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
        var options = new OpenOptions();
        if (BasicFileInfo.Extract(path).IsWorkshared)
            options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
        return app.OpenDocumentFile(mp, options);
    }

    // =============================================================================================
    private static void RunAll(Document doc)
    {
        // --- pure logic, no model ---
        Scenario("Names: legacy Grid Dimension names map to Datum Dimension", NamesMap);
        Scenario("Presets: a pre-0.2.6 preset keeps its meaning", OldPresetLoads);
        Scenario("Dialog: Datum Dimension round-trips every field and side", DatumWindowRoundTrip);
        Scenario("Dialog: Match Datums ticks, filters and reports", MatchWindowBehaves);
        Scenario("UI layout: every DRW dialog at its default and minimum size (+ screenshots)", UiLayout);
        Isolated(doc, "DW SPIKE: legend views, legend components and their dimensions by API", () => DwSpike(doc));
        Scenario("DW BUILD: Enlarge Door & Window detail families (dev only, run on request)", () => { if (DwDev) DwBuildFamilies(doc); });
        Scenario("DW FIX: label text 3 mm, 5 mm header labels, reload into the row (dev only)", () => { if (DwDev) DwFixFamilies(doc); });
        Scenario("DW LOCK: row labels locked to parameter planes (dev only)", () => { if (DwDev) DwLockRow(doc); });
        Scenario("DW WEIGHT: outline weight 3, row lines thin, thick last-row bottom (dev only)", () => { if (DwDev) DwWeight(doc); });
        Scenario("DW PORT: this Revit year's families from one hand-made label (dev only)", () => { if (DwDev) DwPortFamilies(doc); });
        Scenario("WMT PORT: this Revit year's Wall Mat Tag family from the library material tag (dev only)", () => { if (DwDev) WmtPortFamily(doc); });
        Isolated(doc, "DW legend: build, values, dimensions; rerun after edits", () => DwLegendBuild(doc));
        Isolated(doc, "DW legend: a model without legends is seeded", () => DwLegendSeed(doc));
        Isolated(doc, "DW legend: wide types span cells, tall rows grow", () => DwLegendMerged(doc));
        Scenario("DW legend: sheet numbers and the frame's grid", DwSheetNumbers);
        Isolated(doc, "DW legend: sheets from a title block, legends inside the frame", () => DwSheetsBuild(doc));
        Isolated(doc, "DW legend: title block frames — grid lines inside are not the edge", () => DwTitleBlockFrames(doc));
        Scenario("DW legend: the Sheets tab packs as the builder does", DwSheetsTab);
        Scenario("DW legend: Layout fields numbered as the sample cell, real distances, a number clicked picks its field", DwLayoutNumbers);
        Isolated(doc, "DW legend: live update (schedule values inline, size and field changes rebuilt, live off, apply now)", () => DwLegendLive(doc));
        Isolated(doc, "DW legend: a door set and a window set, each its own", () => DwLegendSplit(doc));
        Isolated(doc, "DW legend: the set's own starter schedule, material rows", () => DwStarter(doc));
        Isolated(doc, "DW legend: families v2, wall stubs, values wrapped by the cell's width", () => DwRound4Build(doc));
        Isolated(doc, "DW legend: a cell width set by hand is remembered; reset forgets it", () => DwRound4Widths(doc));
        Isolated(doc, "DW legend: an instance-sized door is drawn as an outline", () => DwRound4Instance(doc));
        Isolated(doc, "DW legend: legends with no title, the set's sheet parameters", () => DwRound4Sheets(doc));
        Isolated(doc, "DW legend: Size and Qty from the schedule's fields, extents end at the cells", () => DwRound5Schedule(doc));
        Isolated(doc, "DW legend: the level symbol on the floor line", () => DwRound5Floor(doc));
        Isolated(doc, "DW legend: dimensions moved by hand survive a rebuild; reset puts them back", () => DwRound5Dims(doc));
        Scenario("DW TEXT R4: header text 3 mm, table text 2 mm, unwrapped Value label, version (dev only)", () => { if (DwDev) DwTextR4(doc); });
        Scenario("DW TEXT R5: narrow Value label that never wraps, families v3 (dev only)", () => { if (DwDev) DwTextR5(doc); });
        Isolated(doc, "DW PROBE R4: host length, label line breaks, viewport types, sheet organization (dev only)", () => { if (DwDev) DwProbeR4(doc); });
        Scenario("DW PROBE R5: combined size and Count fields, the Value label box (dev only)", () => { if (DwDev) DwProbeR5(doc); });
        Isolated(doc, "DW PROBE R5 LABEL: narrow label boxes that do not wrap (dev only)", () => { if (DwDev) DwProbeR5Label(doc); });
        Isolated(doc, "DW TIMING: open, build and live edits timed; a schedule read inside the updater (dev only)", () => { if (DwDev) DwTiming(doc); });
        Scenario("DW PROBE: what the saved Enlarge Door & Window families contain (dev only)", () => { if (DwDev) DwProbeFamilies(doc); });

        // --- Datum Dimension, controlled fixture ---
        Isolated(doc, "DD fixture: most bubbles picks the end showing bubbles", () => DdMostBubbles(doc));
        Isolated(doc, "DD fixture: sides TopRight / BottomLeft / Both", () => DdSides(doc));
        Isolated(doc, "DD fixture: string options (overall, every, extra, two datums)", () => DdStringOptions(doc));
        Isolated(doc, "DD fixture: spacing from the type's snap distance", () => DdSnapSpacing(doc));
        Isolated(doc, "DD fixture: missing dimension type warns and still runs", () => DdMissingType(doc));
        Isolated(doc, "DD fixture: nearly parallel grid is left out, the rest still made", () => DdNearlyParallel(doc));
        Isolated(doc, "DD fixture: arc grid left out, angled family dimensioned", () => DdArcAndAngled(doc));
        Isolated(doc, "DD fixture: skip views already dimensioned", () => DdSkip(doc));
        Isolated(doc, "DD fixture: levels in a plan are nothing to do", () => DdLevelsInPlan(doc));

        // --- Datum Dimension over the real model ---
        Isolated(doc, "DD model: plans, most bubbles", () => DdModel(doc, plans: true, sections: false, elevations: false, grids: true, levels: true));
        Isolated(doc, "DD model: sections, grids and levels", () => DdModel(doc, plans: false, sections: true, elevations: false, grids: true, levels: true));
        Isolated(doc, "DD model: elevations, grids and levels", () => DdModel(doc, plans: false, sections: false, elevations: true, grids: true, levels: true));
        Isolated(doc, "DD model: skip judged apart for grids and levels", () => DdSkipApart(doc));
        Isolated(doc, "DD batch: legacy-named action runs through ActionRegistry", () => DdBatchLegacy(doc));

        // --- Match Datums ---
        Isolated(doc, "MD probe: how Revit's datum leader API behaves", () => MdLeaderProbe(doc));
        Isolated(doc, "MD fixture: ends, bubbles, elbows copied; rerun unchanged", () => MdPlans(doc));
        Isolated(doc, "MD fixture: a moved elbow is copied where it is", () => MdMovedElbow(doc));
        Isolated(doc, "MD fixture: 3D end restored, hidden skipped, elbow removal reported", () => MdEdgeCases(doc));
        Isolated(doc, "MD fixture: scope-box datum matched, box named", () => MdScopeBox(doc));
        Isolated(doc, "MD: candidates are exactly the parallel views", () => MdCandidates(doc));
        Isolated(doc, "MD sections: a level's end copied to a parallel section", () => MdSectionLevels(doc));

        // --- 0.2.7: crop fitting, only-what-stuck, the matrix conditions, elevations ---
        Isolated(doc, "MD exact copy: uncropped source → cropped target, ends beyond the crop kept and named", () => MdCropFit(doc, sourceCropped: false));
        Isolated(doc, "MD exact copy: cropped source → smaller crop", () => MdCropFit(doc, sourceCropped: true));
        Isolated(doc, "MD exact copy: uncropped plan → Snowdon's scope-box plans, all ends 2D where the source draws them", () => MdScopeBoxViews(doc));
        Isolated(doc, "MD matrix: grids turned off in VG are skipped, not 'matched'", () => MdCategoryHidden(doc));
        Isolated(doc, "MD matrix: multi-segment rows are told apart; ceiling plans offered", () => MdMultiSegmentAndCeiling(doc));
        Isolated(doc, "Elevations: centre 4 sides take the section crop and height", () => ElevationCrop(doc, ElevationMode.ByCenter4));
        Isolated(doc, "Elevations: by boundary take the section crop and height", () => ElevationCrop(doc, ElevationMode.ByBoundary));
        Scenario("Elevations: a pre-0.2.7 elevation preset migrates to the same crop", ElevationPresetMigrates);

        // --- 0.2.8: Single Font ---
        Isolated(doc, "SF: everything to Tahoma — types, schedules, families; rerun unchanged", () => SfAll(doc));
        Isolated(doc, "SF: type names carrying the old font are renamed; a taken name is kept", () => SfRename(doc));
        Isolated(doc, "SF: dry run reports and changes nothing", () => SfDryRun(doc));
        Isolated(doc, "SF: only the ticked groups change", () => SfGroupsOff(doc));
        Isolated(doc, "SF: a missing font warns; batch preset runs through ActionRegistry", () => SfMissingFontAndBatch(doc));

        // --- 0.3.0: Preset Tagger ---
        Scenario("PT: presets and the dialog round-trip", PtPresetAndDialog);
        Isolated(doc, "PT: doors, windows, rooms tagged without overlap; rerun adds nothing", () => PtBasicAndRerun(doc));
        Isolated(doc, "PT: distances are paper mm at 1:50 and 1:200", () => PtScale(doc));
        Isolated(doc, "PT: no overlaps at 1:48, 1:96, 1:192 (commercial tagger: 31 / 87 / 333)", () => PtSbpScales(doc));
        Isolated(doc, "PT: overlapping spot avoided; fully covered door skipped", () => PtOverlap(doc));
        Isolated(doc, "PT: missing tag type fails one rule; two door rules both tag", () => PtMissingTypeAndTwoRules(doc));
        Isolated(doc, "PT: dry run changes nothing; batch preset through ActionRegistry", () => PtDryRunAndBatch(doc));
        Scenario("PT: an old preset loads unchanged", PtOldPreset);
        Isolated(doc, "PT: by element — doors on the exterior / interior side", () => PtElementSide(doc));
        Isolated(doc, "PT: Wall Mat Tag — 5 mm from the face, direction types, rerun, Coarse", () => PtMaterial(doc));
        Isolated(doc, "PT: Wall Mat Tag — '?' material tags repaired", () => PtRepair(doc));
        Isolated(doc, "PT: Wall Mat Tag — pairs move together; one pair per wall piece", () => PtMaterialPieces(doc));
        Isolated(doc, "PT: Wall Mat Tag — only seen faces; walls meeting a face cut it", () => PtSeenFaces(doc));
        Isolated(doc, "PT: Wall Mat Tag — tags line up across a wall, none added", () => PtAlign(doc));
        Isolated(doc, "PT: Wall Mat Tag — bundled family loads; tags turn with a wall off the axes", () => PtBundledAndTurn(doc));
        Isolated(doc, "PT: Wall Mat Tag — a plan tags only the walls it cuts", () => PtCutOnly(doc));
        Isolated(doc, "PT: Wall Mat Tag — sections tag on the cut, up the wall", () => PtSection(doc));
        Isolated(doc, "PT: Wall Mat Tag — a painted face is read through its paint", () => PtPainted(doc));
        Isolated(doc, "PT: Wall Mat Tag — elevations tag the surface they look at", () => PtElevation(doc));
        Scenario("PT: Wall Mat Tag — preset and dialog round-trip", WmtPresetAndDialog);

        // --- probes (0.2.7): facts about Revit, logged, that decide how Match Datums handles each case ---
        Isolated(doc, "PROBE scope box: uncropped source → scope-box target", () => ProbeScopeBoxTarget(doc));
        Isolated(doc, "PROBE small manual crop target", () => ProbeManualCropTarget(doc));
        Isolated(doc, "PROBE category hidden by VG", () => ProbeCategoryHidden(doc));
        Isolated(doc, "PROBE arc grid, pinned grid, dependent view", () => ProbeArcPinnedDependent(doc));
        Isolated(doc, "PROBE multi-segment grid", () => ProbeMultiSegment(doc));
        Scenario("PROBE worksharing and linked grids", () => ProbeWorksharingAndLinks(doc));

        // --- regression: the 0.2.5 tools ---
        Isolated(doc, "Regression: Enlarge Room", () => EnlargeRegression(doc, stairs: false));
        Isolated(doc, "Regression: Enlarge Stair", () => EnlargeRegression(doc, stairs: true));
        Isolated(doc, "Regression: Stair Annotation", () => StairRegression(doc));
        Isolated(doc, "Stair Annotation: tread numbers in sections", () => StairSections(doc));
        Isolated(doc, "Stair dims PROBE: run face references in a plan", () => StairDimProbe(doc));
        Isolated(doc, "Stair dims PROBE: what lies at each run end (view vs model)", () => StairEndProbe(doc));
        Isolated(doc, "Stair dims: straight run — run prefix, width, rerun, upper plan", () => StairDimStraight(doc));
        Isolated(doc, "Stair dims: U stair — the user's snapshot", () => StairDimU(doc));
        Isolated(doc, "Stair dims: L stair — four sides", () => StairDimL(doc));
        Isolated(doc, "Stair dims: legacy preset, label format", () => StairDimSkips(doc));
        Isolated(doc, "Stair dims: batch preset through ActionRegistry", () => StairDimBatch(doc));
        Isolated(doc, "Stair dims: Snowdon's stairs", () => StairDimModel(doc));
    }

    // =============================================================================================
    // Pure logic

    private static void NamesMap()
    {
        Check("legacy prefix maps", DrwActions.CanonicalName("Grid Dimension — A1") == "Datum Dimension — A1");
        Check("current name unchanged", DrwActions.CanonicalName("Datum Dimension — A1") == "Datum Dimension — A1");
        Check("other tools unchanged", DrwActions.CanonicalName("Enlarge Room — Grid Dimension — x") == "Enlarge Room — Grid Dimension — x");
        Check("em dash required (no loose match)", DrwActions.CanonicalName("Grid Dimension - A1") == "Grid Dimension - A1");
    }

    private const string TestPreset = "__drw selftest__";

    private static void OldPresetLoads()
    {
        // Exactly what 0.2.5 wrote: no Grids/Levels/Plans/Sections/Elevations, Side spelled out.
        string dir = SettingsPaths.DrwFolder("GridDimensionPresets");
        string file = Path.Combine(dir, TestPreset + ".json");
        File.WriteAllText(file, "{\n  \"Name\": \"" + TestPreset + "\",\n  \"DimensionTypeName\": \"\",\n  \"EveryGrid\": true,\n" +
                                "  \"Overall\": true,\n  \"OverallOffsetMm\": 10,\n  \"EveryGridOffsetMm\": 18,\n  \"SpacingFromType\": false,\n" +
                                "  \"ExtraString\": false,\n  \"Side\": \"Both\",\n  \"Scope\": \"AllPlans\",\n  \"ViewNameContains\": \"\",\n" +
                                "  \"SkipDimensioned\": true\n}");
        try
        {
            DatumDimensionPreset? p = DatumDimensionStore.Store.Load(TestPreset);
            Check("loads", p != null);
            if (p == null) return;
            Check("side kept (Both)", p.Side == GridSide.Both, p.Side.ToString());
            Check("still plans only", p.Plans && !p.Sections && !p.Elevations);
            Check("grids on", p.Grids);
            Check("new default side is MostBubbles", new DatumDimensionPreset().Side == GridSide.MostBubbles);
            IBatchAction a = ActionRegistry.Create("Grid Dimension — " + TestPreset);
            Check("legacy name resolves to the Datum action", a.Name == "Datum Dimension — " + TestPreset, a.Name);
            bool threw = false;
            try { ActionRegistry.Create("Datum Dimension — no such preset"); } catch (InvalidOperationException) { threw = true; }
            Check("missing preset throws, never runs another", threw);
        }
        finally { File.Delete(file); }
    }

    private static void DatumWindowRoundTrip()
    {
        foreach (GridSide side in Enum.GetValues(typeof(GridSide)))
        {
            var p = new DatumDimensionPreset
            {
                Name = "Default", DimensionTypeName = "B : 2", EveryGrid = false, Overall = true, OverallOffsetMm = 7,
                EveryGridOffsetMm = 21, SpacingFromType = true, ExtraString = true, Side = side, Grids = side != GridSide.Both,
                Levels = true, Plans = false, Sections = true, Elevations = side == GridSide.TopRight,
                Scope = DrwViewScope.AllPlans, ViewNameContains = "L0", SkipDimensioned = false
            };
            using var w = new DatumDimensionWindow(new List<string> { "A : 1", "B : 2" },
                new Dictionary<string, double?> { ["A : 1"] = 5, ["B : 2"] = null }, "L1", 0, p);
            var back = (DatumDimensionPreset)typeof(DatumDimensionWindow)
                .GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null)!;
            string a = Describe(p), b = Describe(back);
            Check($"round trip ({side})", a == b, a == b ? "" : $"\n      sent {a}\n      got  {b}");
        }
    }

    private static string Describe(DatumDimensionPreset p) =>
        $"{p.DimensionTypeName}|{p.EveryGrid}|{p.Overall}|{p.OverallOffsetMm}|{p.EveryGridOffsetMm}|{p.SpacingFromType}|{p.ExtraString}|" +
        $"{p.Side}|{p.Grids}|{p.Levels}|{p.Plans}|{p.Sections}|{p.Elevations}|{p.Scope}|{p.ViewNameContains}|{p.SkipDimensioned}";

    private static void MatchWindowBehaves()
    {
        var choices = new List<MatchDatumsWindow.Choice>
        {
            new(1, "Level 1", "Plan", 50), new(2, "Level 2", "Plan", -1), new(3, "Roof", "Plan", 50), new(4, "Site", "Plan", -1)
        };
        using var w = new MatchDatumsWindow("L0", "Plan", 50, "Box", choices, new long[] { 2, 99 });
        Check("only listed, selected views arrive ticked", w.SelectedIds.SequenceEqual(new long[] { 2 }), string.Join(",", w.SelectedIds));
        Invoke(w, "TickWhere", new Func<MatchDatumsWindow.Choice, bool>(c => c.ScopeBox == 50));
        Check("Same Scope Box ticks the box's views", w.SelectedIds.SequenceEqual(new long[] { 1, 2, 3 }), string.Join(",", w.SelectedIds));
        var filter = (System.Windows.Forms.TextBox)typeof(MatchDatumsWindow).GetField("_filter", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        filter.Text = "Level";
        var list = (System.Windows.Forms.CheckedListBox)typeof(MatchDatumsWindow).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        Check("filter narrows the list", list.Items.Count == 2, list.Items.Count.ToString());
        Invoke(w, "TickWhere", new Func<MatchDatumsWindow.Choice, bool>(_ => true));
        Check("All ticks only what the filter shows", w.SelectedIds.SequenceEqual(new long[] { 1, 2, 3 }), string.Join(",", w.SelectedIds));
        filter.Text = "";
        Check("hidden ticks survive the filter", list.CheckedItems.Count == 3, list.CheckedItems.Count.ToString());
    }

    // =============================================================================================
    // UI layout — the ThemePreview layout gate cannot run on this PC (app control blocks the exe), so
    // the same kind of check runs here, inside Revit: each DRW dialog is shown off-screen at its
    // default and its minimum size, measured, and photographed to <report folder>\ui\.

    private static string UiDir => Path.Combine(Path.GetDirectoryName(_report) ?? ".", "ui");

    private static IEnumerable<(string Name, Func<System.Windows.Forms.Form> Make)> DrwWindows()
    {
        yield return ("DatumDimension", () => new DatumDimensionWindow(
            new List<string> { "Linear Dimension Style : Diagonal - 2.5mm Arial", "Linear Dimension Style : Arrow - 3.5mm Arial" },
            new Dictionary<string, double?> { ["Linear Dimension Style : Diagonal - 2.5mm Arial"] = 8.0 },
            "Level 1 - Floor Plan", 3, new DatumDimensionPreset()));
        yield return ("StairAnnotation", () => new StairAnnotationWindow(
            new List<string> { "Automatic Up/Down Direction : Standard", "Fixed Up Direction : Standard" },
            new List<string> { "Linear Dimension Style : Diagonal - 2.5mm Arial", "Linear Dimension Style : Arrow - 3.5mm Arial" },
            new Dictionary<string, double?> { ["Linear Dimension Style : Diagonal - 2.5mm Arial"] = 8.0 },
            "Level 1", 3, new StairAnnotationPreset { PlaceDimensions = true }));
        yield return ("MatchDatums", () => new MatchDatumsWindow("Level 1", "Plan", 101, "Scope Box 1",
            Enumerable.Range(1, 14).Select(i => new MatchDatumsWindow.Choice(200 + i,
                $"Level {i + 1}" + (i % 3 == 0 ? " - Furniture Plan with a longer name" : ""), "Plan", i % 2 == 0 ? 101 : -1)).ToList(),
            new long[] { 202, 205 }));
        yield return ("EnlargeRoom", () => new EnlargeWindow(EnlargeFixture(false), new EnlargePreset(), EnlargeStores.Room));
        yield return ("EnlargeRoomElevation", () =>
        {
            var w = new EnlargeWindow(EnlargeFixture(false), new EnlargePreset { ViewKind = EnlargeViewKind.Elevation }, EnlargeStores.Room);
            return w;
        });
        yield return ("EnlargeStair", () => new EnlargeWindow(EnlargeFixture(true), new EnlargePreset(), EnlargeStores.Stair));
        yield return ("SingleFont", () => new SingleFontWindow(SingleFontEngine.InstalledFonts(),
            new Dictionary<string, int>
            {
                ["TextTypes"] = 24, ["DimensionTypes"] = 31, ["ModelTextTypes"] = 2, ["ScheduleFonts"] = 118,
                ["TagFamilies"] = 64, ["TitleBlocks"] = 5, ["HeadFamilies"] = 22, ["OtherAnnotationFamilies"] = 41, ["ModelFamilies"] = 312
            }, new SingleFontPreset()));
        yield return ("PresetTagger", () => new PresetTaggerWindow(PresetTaggerFixture(), new PresetTaggerPreset()));
        yield return ("WallMatTag", () => new WallMatTagWindow(WallMatTagFixture(), new WallMatTagPreset()));
        yield return ("DoorWindow", () => new DoorWindowWindow(DoorWindowFixture(), new KhBatchRunner.Drw.DoorWindow.DoorWindowPreset()));
        yield return ("DoorWindowWindows", () => new DoorWindowWindow(DoorWindowFixture(),
            new KhBatchRunner.Drw.DoorWindow.DoorWindowPreset { Category = KhBatchRunner.Drw.DoorWindow.DwCategory.Window }));
        yield return ("DoorWindowSheets", () =>
        {
            var w = new DoorWindowWindow(DoorWindowFixture(), new KhBatchRunner.Drw.DoorWindow.DoorWindowPreset { TitleBlockName = "A3 Thai : A3" });
            w.ShowSheets();
            return w;
        });
        yield return ("DoorWindowEditing", () =>
        {
            var w = new DoorWindowWindow(DoorWindowFixture(), new KhBatchRunner.Drw.DoorWindow.DoorWindowPreset());
            w.ShowPart(DoorWindowDiagram.Part.LabelWidth);
            return w;
        });
    }

    private static DoorWindowDialogData DoorWindowFixture() => new()
    {
        Schedules = new()
        {
            // Door rows as a KH door schedule gives them; the window one still needs fixing.
            new KhBatchRunner.Drw.DoorWindow.DwScheduleData
            {
                Name = "KH Door Schedule (Enlarge)", IsDoor = true,
                // The starter's Width x Height (ขนาด) and Count (Qty.) fields are the Size and Qty rows.
                Headings = new() { "Description", "ขนาด", "วัสดุวงกบ", "วัสดุกรอบบาน", "หมายเหตุ", "Qty." },
                SizeColumn = 1, CountColumn = 5,
                ByMark = new()
                {
                    ["D1"] = new() { "ประตูบานเปิดเดี่ยว", "900 x 2100", "Wood - Teak", "ไม้อัดสัก ทาสีย้อมไม้", "-", "24" },
                    ["D2"] = new() { "ประตูบานเปิดคู่", "1800 x 2100", "Aluminium - White", "กระจกเทมเปอร์ 8 มม. ใส", "บานพับ 4 ตัว", "6" },
                    ["D3"] = new() { "ประตูบานเลื่อน", "3000 x 2400", "Aluminium - White", "กระจกเทมเปอร์ 10 มม.", "", "2" }
                },
                // วัสดุวงกบ is Frame Material: the cell says the material's name; the row shows its Description.
                MaterialColumns = new() { 2 },
                MaterialOf = new()
                {
                    ["D1"] = new() { [2] = 101 }, ["D2"] = new() { [2] = 102 }, ["D3"] = new() { [2] = 102 }
                },
                Materials = new()
                {
                    [101] = new() { ["Name"] = "Wood - Teak", ["Description"] = "ไม้เนื้อแข็ง", ["Mark"] = "M-01" },
                    [102] = new() { ["Name"] = "Aluminium - White", ["Description"] = "อลูมิเนียม สีอบขาว", ["Mark"] = "M-02" }
                },
                MaterialProperties = new() { "Name", "Description", "Mark" },
                FieldIds = new() { -1002, -1, 501, 502, -1010103, -1152353 },
                // As Read fills it: the material field shows its Description by default.
                ValueFrom = new() { [0] = "cell", [1] = "cell", [2] = "material:Description", [3] = "cell", [4] = "cell", [5] = "cell" },
                TypeParameters = new() { "Frame Spec", "Type Comments" },
                InstanceParameters = new() { "Comments", "Mark" },
                TypeValues = new() { ["D1"] = new() { ["Frame Spec"] = "วงกบไม้ 2x4" } }
            },
            new KhBatchRunner.Drw.DoorWindow.DwScheduleData
            {
                Name = "Window Schedule", IsDoor = false, Headings = new() { "Description", "Type Comments" },
                Problems = new() { "It lists every instance ('Itemize every instance' is ticked)." }
            }
        },
        Items = new()
        {
            new() { Mark = "D1", Category = "Door", Size = "900 x 2100 mm", Qty = 24, Types = "M_Single-Flush : 0915 x 2134mm",
                WidthMm = 900, HeightSillMm = 2100, PlanWidthMm = 2200 },
            new() { Mark = "D2", Category = "Door", Size = "1800 x 2100 mm", Qty = 6, Types = "M_Double-Flush : 1830 x 2134mm, M_Double-Glass : 1830 x 2134mm",
                Warnings = "Type Mark D2 merges 2 types of the same size.", WidthMm = 1800, HeightSillMm = 2100, PlanWidthMm = 3100 },
            new() { Mark = "D3", Category = "Door", Size = "3000 x 2400 mm", Qty = 2, Types = "Sliding Door : 3000 x 2400",
                WidthMm = 3000, HeightSillMm = 2400, PlanWidthMm = 5600 },
            new() { Mark = "W1", Category = "Window", Size = "2500 x 2800 mm", Qty = 12, Types = "Sliding : 2500 x 2800",
                WidthMm = 2500, HeightSillMm = 3400, PlanWidthMm = 3700 },
            new() { Mark = "W2", Category = "Window", Size = "800 x 500 mm", Qty = 8, Types = "Awning : 800 x 500",
                WidthMm = 800, HeightSillMm = 2300, PlanWidthMm = 2000 },
            new() { Mark = "W3", Category = "Window", Size = "2000 x 1500 mm", Qty = 10, Types = "Sliding : 2000 x 1500",
                WidthMm = 2000, HeightSillMm = 2400, PlanWidthMm = 3200 },
            new() { Mark = "W4", Category = "Window", Size = "800 x 2000 mm", Qty = 4, Types = "Fixed : 800 x 2000",
                WidthMm = 800, HeightSillMm = 2600, PlanWidthMm = 2000 }
        },
        Warnings = new() { "2 window type(s) have no Type Mark and are left out: Fixed : 600 x 600, Fixed : 900 x 600" },
        DimensionTypes = new() { "Linear - 2.5mm Arial", "Linear - 2mm Arial" },
        TitleBlocks = new()
        {
            // An A3 with a 10 mm border and a 40 mm title strip along the bottom (the A-12 example).
            new KhBatchRunner.Drw.DoorWindow.DwTitleBlockData
            {
                Name = "A3 Thai : A3", WidthMm = 420, HeightMm = 297,
                Frame = new KhBatchRunner.Drw.DoorWindow.DwFrame(10, 50, 410, 287),
                Lines = new() { (10, 10, 410, 10), (410, 10, 410, 287), (410, 287, 10, 287), (10, 287, 10, 10), (10, 50, 410, 50),
                                (130, 10, 130, 50), (250, 10, 250, 50), (330, 10, 330, 50) }
            }
        }
    };

    private static PresetTaggerDialogData PresetTaggerFixture() => new()
    {
        Categories = new List<(string, string)> { ("OST_Doors", "Doors"), ("OST_Windows", "Windows"), ("OST_Rooms", "Rooms"),
            ("OST_Walls", "Walls"), ("OST_Furniture", "Furniture") },
        TagTypes = new Dictionary<string, List<string>>
        {
            ["OST_Doors"] = new() { "M_Door Tag : Standard", "Door Fire Rating Tag with a much longer family name : 60 min" },
            ["OST_Windows"] = new() { "M_Window Tag : Standard" },
            ["OST_Rooms"] = new() { "M_Room Tag : Room Tag With Area", "M_Room Tag : Room Tag" },
            ["OST_Walls"] = new() { "M_Wall Tag : 6mm" },
            ["OST_Furniture"] = new()
        },
        ActiveView = "Level 1 - Floor Plan", SelectedViews = 3
    };

    private static WallMatTagDialogData WallMatTagFixture() => new()
    {
        MaterialTagTypes = new() { "M-Material Wall Tag : Up", "M-Material Wall Tag : Down", "M-Material Wall Tag : Left",
            "M-Material Wall Tag : Right", "M_Material Tag : Standard" },
        ActiveView = "Level 1 - Floor Plan", SelectedViews = 3
    };

    private static EnlargeDialogData EnlargeFixture(bool isStair) => new()
    {
        IsStair = isStair,
        PlanTypes = new() { "Floor Plan : Floor Plan" },
        SectionTypes = new() { "Section : Building Section", "Section : Wall Section" },
        ElevationTypes = new() { "Elevation : Interior Elevation" },
        PlanTemplates = new() { "Enlarged Plan" },
        SectionTemplates = new() { "Enlarged Section" },
        ElevationTemplates = new() { "Interior Elevation" },
        Targets = isStair ? new() { "ST-1", "ST-2", "ST-3 (multistory)" } : new() { "101 Kitchen", "102 Toilet", "201 Office" },
        Preselected = new() { 1 },
        FromSelection = 1,
        ViewParameters = new()
        {
            new ParamChoice("Drawing Type  (Project · Text)", "Drawing Type", ""),
            new ParamChoice("Title on Sheet  (Text)", "Title on Sheet", ""),
        },
        TemplateScales = new()
    };

    private static void UiLayout()
    {
        Directory.CreateDirectory(UiDir);
        foreach ((string name, Func<System.Windows.Forms.Form> make) in DrwWindows())
            foreach (bool minimum in new[] { false, true })
            {
                using System.Windows.Forms.Form form = make();
                string label = $"{name} ({(minimum ? "minimum" : "default")} {{0}})";
                form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                form.Location = new System.Drawing.Point(-4000, -4000);
                form.ShowInTaskbar = false;
                form.Show();
                if (minimum) form.Size = form.MinimumSize;
                Settle(form);
                string size = $"{form.ClientSize.Width}x{form.ClientSize.Height}";
                label = string.Format(label, size);

                var tabs = Descendants(form).OfType<System.Windows.Forms.TabControl>().FirstOrDefault();
                int pages = tabs?.TabCount ?? 1;
                for (int page = 0; page < pages; page++)
                {
                    if (tabs != null) { tabs.SelectedIndex = page; Settle(form); }
                    string where = tabs != null ? $"{label}, tab '{tabs.TabPages[page].Text}'" : label;
                    List<string> problems = LayoutProblems(form);
                    Check($"{where}: nothing clipped or overlapping", problems.Count == 0, string.Join("\n        ", problems.Take(12)));
                    Shot(form, $"{name}-{(minimum ? "min" : "default")}" + (tabs != null ? $"-tab{page + 1}" : ""));
                }

                object? presetBar = form.GetType().GetField("_bar", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);
                if (presetBar?.GetType().GetProperty("Row")?.GetValue(presetBar) is System.Windows.Forms.Control row)
                {
                    var combo = Descendants(row).OfType<System.Windows.Forms.ComboBox>().First();
                    Check($"{label}: preset list at least 150 px wide", combo.Width >= 150, $"{combo.Width} px");
                    var last = Descendants(row).OfType<System.Windows.Forms.Button>().OrderBy(b => b.Right).LastOrDefault();
                    if (last != null)
                        Check($"{label}: the preset row's last button ('{last.Text}') is inside the window",
                            form.RectangleToClient(last.RectangleToScreen(last.ClientRectangle)).Right <= form.ClientSize.Width,
                            $"right {form.RectangleToClient(last.RectangleToScreen(last.ClientRectangle)).Right} > {form.ClientSize.Width}");
                }

                if (form is DatumDimensionWindow dd) ScopeBehaves(dd, label);
                if (name == "EnlargeRoomElevation")
                {
                    var crop = Field<System.Windows.Forms.NumericUpDown>(form, "_sectionCrop");
                    var below = Field<System.Windows.Forms.NumericUpDown>(form, "_sectionBelow");
                    var type = Field<System.Windows.Forms.ComboBox>(form, "_sectionType");
                    Check($"{label}: Elevation enables crop and height, not the section type",
                        crop.Enabled && below.Enabled && !type.Enabled, $"crop {crop.Enabled}, below {below.Enabled}, section type {type.Enabled}");
                }
                form.Close();
            }
        Info("screenshots: " + UiDir);
    }

    /// <summary>One-of-three radios, and the "Covering" row enabled only with the name filter.</summary>
    private static void ScopeBehaves(DatumDimensionWindow w, string label)
    {
        object scope = typeof(DatumDimensionWindow).GetField("_scope", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        System.Windows.Forms.RadioButton R(string f) => (System.Windows.Forms.RadioButton)scope.GetType()
            .GetField(f, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scope)!;
        var radios = new[] { R("_active"), R("_selected"), R("_all") };
        var plans = Field<System.Windows.Forms.CheckBox>(w, "_plans");
        foreach (var rb in radios)
        {
            rb.PerformClick();
            Settle(w);
            Check($"{label}: clicking '{rb.Text.Split('(')[0].Trim()}' leaves exactly one option ticked",
                radios.Count(r => r.Checked) == 1 && rb.Checked, string.Join(", ", radios.Select(r => r.Checked)));
            Check($"{label}: 'Covering' enabled only with the name filter", plans.Enabled == (rb == radios[2]), $"enabled {plans.Enabled}");
        }
    }

    private static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static void Settle(System.Windows.Forms.Form form)
    {
        for (int i = 0; i < 3; i++)
        {
            form.PerformLayout();
            System.Windows.Forms.Application.DoEvents();
        }
    }

    private static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control c)
    {
        foreach (System.Windows.Forms.Control child in c.Controls)
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>
    /// Visible controls that stick out of their parent sideways (or downwards, unless the parent
    /// scrolls), combo boxes too narrow for their chosen text, and buttons on one row with different
    /// heights — the faults the layout gate catches, measured the same way.
    /// </summary>
    private static List<string> LayoutProblems(System.Windows.Forms.Form form)
    {
        var problems = new List<string>();
        foreach (var c in Descendants(form).Where(c => c.Visible && c.Parent != null && c.Parent.Visible))
        {
            var p = c.Parent!;
            var client = p.ClientRectangle;
            bool scrolls = p is System.Windows.Forms.ScrollableControl sc && sc.AutoScroll;
            string who = $"{c.GetType().Name} '{Short(c.Text)}' in {p.GetType().Name} '{Short(p.Text)}'";
            if (c.Right > client.Width + 1 && !(p is System.Windows.Forms.ScrollableControl s2 && s2.HorizontalScroll.Visible))
                problems.Add($"{who}: right edge {c.Right} > {client.Width}");
            if (!scrolls && c.Bottom > client.Height + 1 && p is not System.Windows.Forms.TabPage)
                problems.Add($"{who}: bottom {c.Bottom} > {client.Height}");
            if (c.Width <= 2 && c is not System.Windows.Forms.Label && c.Controls.Count == 0)
                problems.Add($"{who}: {c.Width} px wide");
            if (c is System.Windows.Forms.ComboBox cb)
            {
                // A long "Family : Type" may be shortened in the closed box (normal Windows), but the
                // open list must show every item whole.
                int need = cb.Items.Cast<object>().Select(i => System.Windows.Forms.TextRenderer.MeasureText(i?.ToString() ?? "", cb.Font).Width + 8)
                    .DefaultIfEmpty(0).Max();
                if (need > Math.Max(cb.Width, cb.DropDownWidth)) problems.Add($"{who}: its list needs {need} px, opens {cb.DropDownWidth}");
                if (cb.Width < 90) problems.Add($"{who}: only {cb.Width} px wide");
            }
        }
        foreach (var row in Descendants(form).OfType<System.Windows.Forms.FlowLayoutPanel>())
        {
            var buttons = row.Controls.OfType<System.Windows.Forms.Button>().Where(b => b.Visible).ToList();
            if (buttons.Select(b => b.Height).Distinct().Count() > 1)
                problems.Add($"buttons {string.Join("/", buttons.Select(b => $"'{b.Text}' {b.Height}"))} differ in height");
        }
        return problems;
    }

    private static string Short(string? s) => s == null ? "" : s.Length > 28 ? s.Substring(0, 28) + "…" : s;

    private static void Shot(System.Windows.Forms.Form form, string name)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(UiDir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Info($"screenshot {name} failed: {ex.Message}"); }
    }

    private static void Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    // =============================================================================================
    // Fixture: new plans on the two lowest levels, the model's own grids hidden there, and grids of
    // our own far to the east of everything.

    private sealed class Fixture
    {
        public ViewPlan A = null!, B = null!;
        public double X0, Y0;
        public double Bottom, Top;
        public List<Level> Levels = new();
    }

    private static Fixture MakeFixture(Document doc)
    {
        var f = new Fixture();
        f.Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        ViewFamilyType plan = DrwLookup.ViewTypes(doc, ViewFamily.FloorPlan).First();
        List<Grid> existing = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
        double maxX = 0, minY = 0;
        foreach (Grid g in existing)
        {
            BoundingBoxXYZ? bb = g.get_BoundingBox(null);
            if (bb == null) continue;
            maxX = Math.Max(maxX, bb.Max.X);
            minY = Math.Min(minY, bb.Min.Y);
        }
        f.X0 = maxX + 300;
        f.Y0 = minY;
        f.Bottom = f.Levels.First().Elevation - 10;
        f.Top = f.Levels.Last().Elevation + 10;

        using var t = new Transaction(doc, "fixture views");
        t.Start();
        f.A = NewPlan(doc, plan.Id, f.Levels[0].Id, "__st A");
        f.B = NewPlan(doc, plan.Id, f.Levels[Math.Min(1, f.Levels.Count - 1)].Id, "__st B");
        if (existing.Count > 0)
        {
            var ids = existing.Where(g => g.CanBeHidden(f.A)).Select(g => g.Id).ToList();
            if (ids.Count > 0) { f.A.HideElements(ids); f.B.HideElements(ids); }
        }
        t.Commit();
        return f;
    }

    private static ViewPlan NewPlan(Document doc, ElementId type, ElementId level, string name)
    {
        ViewPlan v = ViewPlan.Create(doc, type, level);
        v.ViewTemplateId = ElementId.InvalidElementId;
        v.CropBoxActive = false;
        v.Scale = 100;
        v.Name = DrwNaming.Unique(name, DrwNaming.ExistingViewNames(doc));
        return v;
    }

    private static int _gridNo;

    /// <summary>A straight grid from <paramref name="a"/> to <paramref name="b"/> (plan points, feet).</summary>
    private static Grid AddGrid(Document doc, Fixture f, XYZ a, XYZ b)
    {
        Grid g = Grid.Create(doc, Line.CreateBound(a, b));
        g.Name = "__st" + (++_gridNo);
        g.SetVerticalExtents(f.Bottom, f.Top);
        return g;
    }

    /// <summary>Vertical fixture grids x = X0 + k*spacing, running 0..100 ft north of Y0.</summary>
    private static List<Grid> VerticalGrids(Document doc, Fixture f, int count, double spacing = 20, double dx = 0)
    {
        var list = new List<Grid>();
        using var t = new Transaction(doc, "fixture grids");
        t.Start();
        for (int k = 0; k < count; k++)
            list.Add(AddGrid(doc, f, new XYZ(f.X0 + dx + k * spacing, f.Y0, 0), new XYZ(f.X0 + dx + k * spacing, f.Y0 + 100, 0)));
        t.Commit();
        return list;
    }

    /// <summary>The top (+Y) and bottom ends of a vertical grid as a view draws it.</summary>
    private static (DatumEnds Top, DatumEnds Bottom) Ends(DatumPlane d, View v)
    {
        Curve c = d.GetCurvesInView(DatumExtentType.ViewSpecific, v).First();
        return c.GetEndPoint(1).Y > c.GetEndPoint(0).Y ? (DatumEnds.End1, DatumEnds.End0) : (DatumEnds.End0, DatumEnds.End1);
    }

    private static void SetBubbles(Document doc, IEnumerable<DatumPlane> datums, View v, bool top, bool bottom)
    {
        using var t = new Transaction(doc, "bubbles");
        t.Start();
        foreach (DatumPlane d in datums)
        {
            (DatumEnds te, DatumEnds be) = Ends(d, v);
            if (top) d.ShowBubbleInView(te, v); else d.HideBubbleInView(te, v);
            if (bottom) d.ShowBubbleInView(be, v); else d.HideBubbleInView(be, v);
        }
        t.Commit();
    }

    private static DatumDimensionPreset Preset(GridSide side = GridSide.MostBubbles) => new()
    {
        Name = "selftest", Side = side, Grids = true, Levels = true, Plans = true, SkipDimensioned = false
    };

    /// <summary>Dimensions owned by the view, by id.</summary>
    private static HashSet<ElementId> Dims(Document doc, View v) =>
        new(new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>()
            .Where(d => d.OwnerViewId == v.Id).Select(d => d.Id));

    private static List<Dimension> NewDims(Document doc, View v, HashSet<ElementId> before) =>
        Dims(doc, v).Where(id => !before.Contains(id)).Select(id => (Dimension)doc.GetElement(id)).ToList();

    /// <summary>The Y of a horizontal dimension line (strings across vertical grids).</summary>
    private static double LineY(Dimension d) => ((Line)d.Curve).Origin.Y;

    private static int Count(List<IReadOnlyDictionary<string, string>> rows, string status) =>
        rows.Count(r => r.TryGetValue("Status", out string? s) && s == status);

    private static string Rows(List<IReadOnlyDictionary<string, string>> rows) =>
        string.Join("", rows.Select(r => "\n        " + string.Join(" | ", r.Values)));

    // =============================================================================================
    // Datum Dimension — fixture

    private static void DdMostBubbles(Document doc)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> grids = VerticalGrids(doc, f, 4);

        foreach ((bool top, bool bottom, string expect) in new[]
                 {
                     (true, false, "top"), (false, true, "bottom"), (true, true, "both"), (false, false, "both")
                 })
        {
            SetBubbles(doc, grids, f.A, top, bottom);
            HashSet<ElementId> before = Dims(doc, f.A);
            var rows = DatumDimensioner.Run(doc, new View[] { f.A }, Preset());
            List<Dimension> made = NewDims(doc, f.A, before);
            string label = $"bubbles top={top} bottom={bottom}";
            Check($"{label}: no failures", Count(rows, "Failed") == 0, Rows(rows));
            int expectedCount = expect == "both" ? 4 : 2;   // overall + every, per side
            Check($"{label}: {expectedCount} strings", made.Count == expectedCount && Count(rows, "Created") == expectedCount,
                $"made {made.Count}, rows {Count(rows, "Created")}{Rows(rows)}");
            double mid = f.Y0 + 50;
            int atTop = made.Count(d => LineY(d) > mid), atBottom = made.Count(d => LineY(d) < mid);
            bool placed = expect switch
            {
                "top" => atTop == 2 && atBottom == 0,
                "bottom" => atBottom == 2 && atTop == 0,
                _ => atTop == 2 && atBottom == 2
            };
            Check($"{label}: strings at the {expect} end", placed, $"top {atTop}, bottom {atBottom}");
            Check($"{label}: every string measures the fixture grids",
                made.All(d => d.References.Cast<Reference>().All(r => grids.Any(g => g.Id == r.ElementId))));
            Check($"{label}: note states the bubble count", rows.Any(r => r["Note"].Contains("Bubbles shown")), Rows(rows));
            // Clean slate for the next case.
            using var t = new Transaction(doc, "clear");
            t.Start();
            doc.Delete(made.Select(d => d.Id).ToList());
            t.Commit();
        }
    }

    private static void DdSides(Document doc)
    {
        Fixture f = MakeFixture(doc);
        VerticalGrids(doc, f, 3);
        foreach ((GridSide side, int top, int bottom) in new[]
                 { (GridSide.TopRight, 2, 0), (GridSide.BottomLeft, 0, 2), (GridSide.Both, 2, 2) })
        {
            HashSet<ElementId> before = Dims(doc, f.A);
            var rows = DatumDimensioner.Run(doc, new View[] { f.A }, Preset(side));
            List<Dimension> made = NewDims(doc, f.A, before);
            double mid = f.Y0 + 50;
            Check($"{side}: {top} at top, {bottom} at bottom, none failed",
                made.Count(d => LineY(d) > mid) == top && made.Count(d => LineY(d) < mid) == bottom && Count(rows, "Failed") == 0,
                $"top {made.Count(d => LineY(d) > mid)}, bottom {made.Count(d => LineY(d) < mid)}{Rows(rows)}");
            Check($"{side}: rows say top/bottom", rows.Where(r => r["Status"] == "Created")
                .All(r => r["Dimension"].Contains("(top)") || r["Dimension"].Contains("(bottom)")), Rows(rows));
            using var t = new Transaction(doc, "clear");
            t.Start();
            doc.Delete(made.Select(d => d.Id).ToList());
            t.Commit();
        }
    }

    private static void DdStringOptions(Document doc)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> three = VerticalGrids(doc, f, 3);
        var cases = new (string Name, Action<DatumDimensionPreset> Set, int Expected, int[] RefCounts)[]
        {
            ("overall only", p => { p.EveryGrid = false; }, 1, new[] { 2 }),
            ("every only", p => { p.Overall = false; }, 1, new[] { 3 }),
            ("overall + every + extra", p => { p.ExtraString = true; }, 3, new[] { 2, 3, 3 }),
        };
        foreach (var c in cases)
        {
            DatumDimensionPreset p = Preset(GridSide.TopRight);
            c.Set(p);
            HashSet<ElementId> before = Dims(doc, f.A);
            var rows = DatumDimensioner.Run(doc, new View[] { f.A }, p);
            List<Dimension> made = NewDims(doc, f.A, before);
            Check($"{c.Name}: {c.Expected} string(s)", made.Count == c.Expected && Count(rows, "Failed") == 0, $"{made.Count}{Rows(rows)}");
            Check($"{c.Name}: reference counts", made.Select(d => d.References.Size).OrderBy(x => x).SequenceEqual(c.RefCounts.OrderBy(x => x)),
                string.Join(",", made.Select(d => d.References.Size)));
            if (c.Expected == 3)
            {
                // From the grid end inward: overall nearest the top, then every, then extra.
                var byY = made.OrderByDescending(LineY).Select(d => d.References.Size).ToList();
                Check("order from the end inward: overall, every, extra", byY.SequenceEqual(new[] { 2, 3, 3 }), string.Join(",", byY));
            }
            using var t = new Transaction(doc, "clear");
            t.Start();
            doc.Delete(made.Select(d => d.Id).ToList());
            t.Commit();
        }

        // Two grids: the overall string would duplicate the every-grid one, so only one is made.
        Fixture f2 = MakeFixture(doc);
        VerticalGrids(doc, f2, 2, dx: 200);
        HashSet<ElementId> before2 = Dims(doc, f2.A);
        var rows2 = DatumDimensioner.Run(doc, new View[] { f2.A }, Preset(GridSide.TopRight));
        List<Dimension> made2 = NewDims(doc, f2.A, before2);
        Check("two grids: one string, not a duplicate overall", made2.Count(d => d.References.Size == 2) == 1, $"{made2.Count}{Rows(rows2)}");
    }

    private static void DdSnapSpacing(Document doc)
    {
        Fixture f = MakeFixture(doc);
        VerticalGrids(doc, f, 3);
        DimensionType? withSnap = DatumDimensioner.LinearTypes(doc).FirstOrDefault(t => DatumDimensioner.SnapDistanceMm(t) is > 1);
        if (withSnap == null) { Info("no linear dimension type with a snap distance in this model — not exercised"); return; }
        double snapMm = DatumDimensioner.SnapDistanceMm(withSnap)!.Value;
        DatumDimensionPreset p = Preset(GridSide.TopRight);
        p.DimensionTypeName = DrwLookup.TypeLabel(withSnap);
        p.SpacingFromType = true;
        HashSet<ElementId> before = Dims(doc, f.A);
        var rows = DatumDimensioner.Run(doc, new View[] { f.A }, p);
        List<Dimension> made = NewDims(doc, f.A, before).OrderByDescending(LineY).ToList();
        Check("two strings made", made.Count == 2 && Count(rows, "Failed") == 0, Rows(rows));
        if (made.Count != 2) return;
        double gapMm = UnitUtils.ConvertFromInternalUnits(LineY(made[0]) - LineY(made[1]), UnitTypeId.Millimeters) / f.A.Scale;
        Check($"gap equals the snap distance ({snapMm:0.##} mm)", Math.Abs(gapMm - snapMm) < 0.05, $"gap {gapMm:0.###} mm");
        Check("the chosen type was used", made.All(d => d.DimensionType.Id == withSnap.Id));
        double overallMm = UnitUtils.ConvertFromInternalUnits(f.Y0 + 100 - LineY(made[0]), UnitTypeId.Millimeters) / f.A.Scale;
        Check("overall string sits OverallOffsetMm in from the end", Math.Abs(overallMm - p.OverallOffsetMm) < 0.05, $"{overallMm:0.###} mm");
    }

    private static void DdMissingType(Document doc)
    {
        Fixture f = MakeFixture(doc);
        VerticalGrids(doc, f, 3);
        DatumDimensionPreset p = Preset(GridSide.TopRight);
        p.DimensionTypeName = "No Such Family : No Such Type";
        var rows = DatumDimensioner.Run(doc, new View[] { f.A }, p);
        Check("first row is a Warning naming the type", rows.Count > 0 && rows[0]["Status"] == "Warning" && rows[0]["Note"].Contains("No Such Type"), Rows(rows));
        Check("strings still made", Count(rows, "Created") == 2 && Count(rows, "Failed") == 0, Rows(rows));
    }

    private static void DdNearlyParallel(Document doc)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> three = VerticalGrids(doc, f, 3);
        Grid slanted;
        using (var t = new Transaction(doc, "slanted grid"))
        {
            t.Start();
            double x = f.X0 + 60;
            slanted = AddGrid(doc, f, new XYZ(x, f.Y0, 0), new XYZ(x + 100 * Math.Tan(0.5 * Math.PI / 180), f.Y0 + 100, 0));
            t.Commit();
        }
        HashSet<ElementId> before = Dims(doc, f.A);
        var rows = DatumDimensioner.Run(doc, new View[] { f.A }, Preset(GridSide.TopRight));
        List<Dimension> made = NewDims(doc, f.A, before);
        Check("no Failed row (0.2.5 lost the whole view here)", Count(rows, "Failed") == 0, Rows(rows));
        Check("the three parallel grids are dimensioned", made.Count == 2 && made.All(d => d.References.Cast<Reference>().All(r => three.Any(g => g.Id == r.ElementId))),
            $"{made.Count}{Rows(rows)}");
        Check("the slanted grid is named as nearly parallel", rows.Any(r => r["Status"] == "Skipped" && r["Note"].Contains("nearly parallel") && r["Note"].Contains(slanted.Name)), Rows(rows));
    }

    private static void DdArcAndAngled(Document doc)
    {
        Fixture f = MakeFixture(doc);
        VerticalGrids(doc, f, 3);
        using (var t = new Transaction(doc, "arc + angled grids"))
        {
            t.Start();
            Grid arc = Grid.Create(doc, Arc.Create(new XYZ(f.X0 + 150, f.Y0 + 50, 0), 30, 0, Math.PI / 2, XYZ.BasisX, XYZ.BasisY));
            arc.Name = "__st" + (++_gridNo);
            arc.SetVerticalExtents(f.Bottom, f.Top);
            XYZ dir = new XYZ(Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6), 0), across = new XYZ(-dir.Y, dir.X, 0);
            for (int k = 0; k < 3; k++)
            {
                XYZ start = new XYZ(f.X0 + 250, f.Y0, 0) + across * (k * 15);
                AddGrid(doc, f, start, start + dir * 80);
            }
            t.Commit();
        }
        var rows = DatumDimensioner.Run(doc, new View[] { f.A }, Preset(GridSide.Both));
        Check("no failures", Count(rows, "Failed") == 0, Rows(rows));
        Check("arc grid named as left out", rows.Any(r => r["Note"].Contains("arc grid")), Rows(rows));
        Check("30° family dimensioned", rows.Count(r => r["Status"] == "Created" && r["Dimension"].StartsWith("Grids at 30")) == 4, Rows(rows));
        Check("vertical family dimensioned", rows.Count(r => r["Status"] == "Created" && r["Dimension"].StartsWith("Vertical grids")) == 4, Rows(rows));
    }

    private static void DdSkip(Document doc)
    {
        Fixture f = MakeFixture(doc);
        VerticalGrids(doc, f, 3);
        DatumDimensionPreset p = Preset(GridSide.TopRight);
        p.SkipDimensioned = true;
        var first = DatumDimensioner.Run(doc, new View[] { f.A }, p);
        var second = DatumDimensioner.Run(doc, new View[] { f.A }, p);
        Check("first run creates", Count(first, "Created") == 2, Rows(first));
        Check("second run skips (already has grid dimensions)", Count(second, "Created") == 0 && second.Any(r => r["Note"].Contains("Already has grid")), Rows(second));
        p.SkipDimensioned = false;
        var third = DatumDimensioner.Run(doc, new View[] { f.A }, p);
        Check("with skip off it creates again", Count(third, "Created") == 2, Rows(third));
    }

    private static void DdLevelsInPlan(Document doc)
    {
        Fixture f = MakeFixture(doc);
        VerticalGrids(doc, f, 3);
        DatumDimensionPreset p = Preset();
        p.Grids = false;
        var rows = DatumDimensioner.Run(doc, new View[] { f.A }, p);
        Check("skipped with the reason", rows.Count == 1 && rows[0]["Status"] == "Skipped" && rows[0]["Note"].Contains("plans show grids only"), Rows(rows));
    }

    // =============================================================================================
    // Datum Dimension — the real model

    /// <summary>Views of a kind that show at least two straight datums, at most <paramref name="max"/>.</summary>
    private static List<View> ViewsWithDatums(Document doc, bool plans, bool sections, bool elevations, int max)
    {
        var views = DrwScope.ResolveDatumViews(doc, null, DrwViewScope.AllPlans, "", plans, sections, elevations);
        return views.Where(v =>
        {
            int n = new FilteredElementCollector(doc, v.Id).OfClass(typeof(Grid)).GetElementCount()
                  + (v is ViewPlan ? 0 : new FilteredElementCollector(doc, v.Id).OfClass(typeof(Level)).GetElementCount());
            return n >= 2;
        }).Take(max).ToList();
    }

    private static void DdModel(Document doc, bool plans, bool sections, bool elevations, bool grids, bool levels)
    {
        List<View> views = ViewsWithDatums(doc, plans, sections, elevations, 6);
        if (views.Count == 0) { Info("no such views with datums in this model — not exercised"); return; }
        Info("views: " + string.Join(", ", views.Select(v => v.Name)));
        var before = views.ToDictionary(v => v.Id, v => Dims(doc, v));
        DatumDimensionPreset p = Preset();
        p.Grids = grids; p.Levels = levels; p.Plans = plans; p.Sections = sections; p.Elevations = elevations;
        var rows = DatumDimensioner.Run(doc, views, p);
        Check("no Failed rows", Count(rows, "Failed") == 0, Rows(rows.Where(r => r["Status"] == "Failed").ToList()));
        Check("something was made", Count(rows, "Created") > 0, Rows(rows));
        foreach (View v in views)
        {
            List<Dimension> made = NewDims(doc, v, before[v.Id]);
            var created = rows.Where(r => r["View"] == v.Name && r["Status"] == "Created").ToList();
            Check($"{v.Name}: every Created row is a dimension in the view", made.Count == created.Count, $"dims {made.Count}, rows {created.Count}");
            Check($"{v.Name}: each dimension measures only grids or only levels", made.All(d =>
            {
                var els = d.References.Cast<Reference>().Select(r => doc.GetElement(r.ElementId)).ToList();
                return els.All(e => e is Grid) || els.All(e => e is Level);
            }));
            if (v is ViewPlan)
                Check($"{v.Name}: no level strings in a plan", !created.Any(r => r["Dimension"].StartsWith("Levels")));
        }
        if (!plans && levels)
            Check("levels were dimensioned somewhere", rows.Any(r => r["Status"] == "Created" && r["Dimension"].StartsWith("Levels")), Rows(rows));
    }

    private static void DdSkipApart(Document doc)
    {
        View? section = ViewsWithDatums(doc, false, true, false, 30)
            .FirstOrDefault(v => new FilteredElementCollector(doc, v.Id).OfClass(typeof(Grid)).GetElementCount() >= 2 &&
                                 new FilteredElementCollector(doc, v.Id).OfClass(typeof(Level)).GetElementCount() >= 2);
        if (section == null) { Info("no section showing both grids and levels — not exercised"); return; }
        Info("section: " + section.Name);
        DatumDimensionPreset levelsOnly = Preset();
        levelsOnly.Grids = false;
        levelsOnly.SkipDimensioned = true;
        var first = DatumDimensioner.Run(doc, new[] { section }, levelsOnly);
        Check("levels dimensioned", first.Any(r => r["Status"] == "Created" && r["Dimension"].StartsWith("Levels")), Rows(first));
        DatumDimensionPreset both = Preset();
        both.SkipDimensioned = true;
        var second = DatumDimensioner.Run(doc, new[] { section }, both);
        Check("levels skipped as already dimensioned", second.Any(r => r["Dimension"] == "Levels" && r["Status"] == "Skipped" && r["Note"].Contains("Already has level")), Rows(second));
        Check("grids still dimensioned", second.Any(r => r["Status"] == "Created" && !r["Dimension"].StartsWith("Levels")), Rows(second));
    }

    private static void DdBatchLegacy(Document doc)
    {
        List<View> plans = ViewsWithDatums(doc, true, false, false, 1);
        if (plans.Count == 0) { Info("no plan with grids — not exercised"); return; }
        var preset = new DatumDimensionPreset
        {
            Name = TestPreset, Side = GridSide.MostBubbles, Scope = DrwViewScope.AllPlans, ViewNameContains = plans[0].Name,
            SkipDimensioned = false
        };
        DatumDimensionStore.Store.Save(preset);
        try
        {
            Check("the preset is offered to Batch Run", ActionRegistry.All().Any(a => a.Name == "Datum Dimension — " + TestPreset));
            IBatchAction action = ActionRegistry.Create("Grid Dimension — " + TestPreset);
            Check("legacy name gives the Datum action", action.Name == "Datum Dimension — " + TestPreset, action.Name);
            var rows = action.RunDetailRows(null!, doc).ToList();
            Check("the batch run dimensions the named plan", rows.Any(r => r["Status"] == "Created") && rows.All(r => r["Status"] != "Failed"),
                Rows(rows.Select(r => (IReadOnlyDictionary<string, string>)r).ToList()));
            Check("the batch run stays inside its name filter", rows.Where(r => r["View"].Length > 0).All(r => r["View"].Contains(plans[0].Name)));
        }
        finally { DatumDimensionStore.Store.Delete(TestPreset); }
    }

    // =============================================================================================
    // Match Datums

    private static void MdPlans(Document doc)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> g = VerticalGrids(doc, f, 3);
        View a = f.A, b = f.B;
        Leader? sourceLeader;
        using (var t = new Transaction(doc, "arrange the source"))
        {
            t.Start();
            // G1: its top end 2D and pulled 15 ft down.
            (DatumEnds top1, _) = Ends(g[0], a);
            g[0].SetDatumExtentType(top1, a, DatumExtentType.ViewSpecific);
            Curve c = g[0].GetCurvesInView(DatumExtentType.ViewSpecific, a).First();
            XYZ p0 = c.GetEndPoint(0), p1 = c.GetEndPoint(1);
            XYZ newTop = (top1 == DatumEnds.End1 ? p1 : p0) - new XYZ(0, 15, 0);
            g[0].SetCurveInView(DatumExtentType.ViewSpecific, a,
                top1 == DatumEnds.End1 ? Line.CreateBound(p0, newTop) : Line.CreateBound(newTop, p1));
            // G2: bubble at the top only, in the source; both in the target.
            (DatumEnds top2, DatumEnds bottom2) = Ends(g[1], a);
            g[1].ShowBubbleInView(top2, a); g[1].HideBubbleInView(bottom2, a);
            g[1].ShowBubbleInView(top2, b); g[1].ShowBubbleInView(bottom2, b);
            // G3: an elbow at the top, in the source only.
            (DatumEnds top3, _) = Ends(g[2], a);
            g[2].ShowBubbleInView(top3, a);
            g[2].ShowBubbleInView(top3, b);
            g[2].AddLeader(top3, a);
            t.Commit();
            sourceLeader = g[2].GetLeader(top3, a);
        }
        Check("fixture: the source has an elbow", sourceLeader != null);

        var rows = DatumMatcher.Run(doc, a, new[] { b });
        Check("no Failed rows", Count(rows, "Failed") == 0, Rows(rows));
        Row(rows, g[0], "Matched");
        Row(rows, g[1], "Matched");
        Row(rows, g[2], "Matched");

        (DatumEnds t1, _) = Ends(g[0], a);
        Check("G1: its top end is 2D in the target", g[0].GetDatumExtentTypeInView(t1, b) == DatumExtentType.ViewSpecific);
        Check("G1: the 2D line matches in plan", SameXY(g[0].GetCurvesInView(DatumExtentType.ViewSpecific, a).First(),
            g[0].GetCurvesInView(DatumExtentType.ViewSpecific, b).First()));
        Check("every grid: both ends 2D in the target, exactly where the source draws them (0.2.7 rule)",
            g.All(x => TwoDLike(x, a, b)), string.Join("; ", g.Select(x => $"{x.Name}: {State(x, b)}")));
        (DatumEnds t2, DatumEnds b2) = Ends(g[1], a);
        Check("G2: bottom bubble hidden, top shown", !g[1].IsBubbleVisibleInView(b2, b) && g[1].IsBubbleVisibleInView(t2, b));
        (DatumEnds t3, _) = Ends(g[2], a);
        Leader? copied = g[2].GetLeader(t3, b);
        Check("G3: the elbow was added", copied != null);
        if (copied != null && sourceLeader != null)
            Check("G3: the elbow sits where the source's does, in plan",
                SameXY(copied.End, sourceLeader.End) && SameXY(copied.Elbow, sourceLeader.Elbow),
                $"src end {sourceLeader.End} elbow {sourceLeader.Elbow}; got end {copied.End} elbow {copied.Elbow}");

        var again = DatumMatcher.Run(doc, a, new[] { b });
        Check("a second run changes nothing", again.All(r => r["Status"] == "Unchanged"), Rows(again));
    }

    /// <summary>Both ends of <paramref name="d"/> are 2D in <paramref name="target"/> and its line there
    /// is the one <paramref name="source"/> draws, in the source view's frame (plan XY, or along a section).</summary>
    private static bool TwoDLike(DatumPlane d, View source, View target)
    {
        if (BothEnds.Any(e => d.GetDatumExtentTypeInView(e, target) != DatumExtentType.ViewSpecific)) return false;
        Curve s = Line2D(d, source), t = Line2D(d, target);
        XYZ r = source.RightDirection, u = source.UpDirection;
        bool Near(XYZ a, XYZ b) => Math.Abs(a.DotProduct(r) - b.DotProduct(r)) < 1e-4 && Math.Abs(a.DotProduct(u) - b.DotProduct(u)) < 1e-4;
        return Near(s.GetEndPoint(0), t.GetEndPoint(0)) && Near(s.GetEndPoint(1), t.GetEndPoint(1));
    }

    private static string P(XYZ p) => $"({p.X:0.###}, {p.Y:0.###}, {p.Z:0.###})";

    private static string L(Leader? l) => l == null ? "none" : $"end {P(l.End)} elbow {P(l.Elbow)} anchor {P(l.Anchor)}";

    /// <summary>Not a pass/fail test: logs what AddLeader / the Leader setters / SetLeader actually do
    /// to a grid's elbow, so a mismatch in the elbow scenarios can be read against Revit's behaviour.</summary>
    private static void MdLeaderProbe(Document doc)
    {
        Fixture f = MakeFixture(doc);
        Grid grid = VerticalGrids(doc, f, 1)[0];
        (DatumEnds top, _) = Ends(grid, f.A);
        using var t = new Transaction(doc, "probe");
        t.Start();
        grid.ShowBubbleInView(top, f.A);
        grid.ShowBubbleInView(top, f.B);
        Info("curve A: " + P(grid.GetCurvesInView(DatumExtentType.ViewSpecific, f.A).First().GetEndPoint(top == DatumEnds.End0 ? 0 : 1)));
        Info("curve B: " + P(grid.GetCurvesInView(DatumExtentType.ViewSpecific, f.B).First().GetEndPoint(top == DatumEnds.End0 ? 0 : 1)));
        grid.AddLeader(top, f.A);
        doc.Regenerate();
        Info("A after AddLeader+regen:      " + L(grid.GetLeader(top, f.A)));
        grid.AddLeader(top, f.B);
        doc.Regenerate();
        Info("B after AddLeader+regen:      " + L(grid.GetLeader(top, f.B)));
        Leader lb = grid.GetLeader(top, f.B);
        grid.SetLeader(top, f.B, lb);
        doc.Regenerate();
        Info("B after SetLeader(unchanged)+regen: " + L(grid.GetLeader(top, f.B)));
        lb = grid.GetLeader(top, f.B);
        lb.Elbow = lb.Elbow + new XYZ(1, 0, 0);
        Info("B object after Elbow += 1 X:  " + L(lb));
        grid.SetLeader(top, f.B, lb);
        doc.Regenerate();
        Info("B after SetLeader+regen:      " + L(grid.GetLeader(top, f.B)));
        lb = grid.GetLeader(top, f.B);
        lb.End = lb.End + new XYZ(0, -2, 0);
        Info("B object after End -= 2 Y:    " + L(lb));
        grid.SetLeader(top, f.B, lb);
        doc.Regenerate();
        Info("B after SetLeader+regen:      " + L(grid.GetLeader(top, f.B)));
        t.Commit();
        Info("B after commit:               " + L(grid.GetLeader(top, f.B)));
        Check("probe ran", true);
    }

    private static void MdMovedElbow(Document doc)
    {
        Fixture f = MakeFixture(doc);
        Grid grid = VerticalGrids(doc, f, 2)[0];
        (DatumEnds top, _) = Ends(grid, f.A);
        using (var t = new Transaction(doc, "a moved elbow in the source"))
        {
            t.Start();
            grid.ShowBubbleInView(top, f.A);
            grid.ShowBubbleInView(top, f.B);
            grid.AddLeader(top, f.A);
            Leader l = grid.GetLeader(top, f.A);
            l.Elbow = l.Elbow + new XYZ(3, 1, 0);
            grid.SetLeader(top, f.A, l);
            t.Commit();
        }
        Leader src = grid.GetLeader(top, f.A);
        Info("source: " + L(src));
        var rows = DatumMatcher.Run(doc, f.A, new View[] { f.B });
        Leader? got = grid.GetLeader(top, f.B);
        Info("target: " + L(got));
        Check("no Failed rows", Count(rows, "Failed") == 0, Rows(rows));
        Check("the elbow is where the source's is, in plan", got != null && SameXY(got.End, src.End) && SameXY(got.Elbow, src.Elbow) && SameXY(got.Anchor, src.Anchor),
            "source " + L(src) + "; target " + L(got));
        var again = DatumMatcher.Run(doc, f.A, new View[] { f.B });
        Check("a second run changes nothing", again.All(r => r["Status"] == "Unchanged"), Rows(again));
    }

    private static void MdEdgeCases(Document doc)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> g = VerticalGrids(doc, f, 3);
        View a = f.A, b = f.B;
        ViewPlan c;
        using (var t = new Transaction(doc, "arrange"))
        {
            t.Start();
            c = NewPlan(doc, a.GetTypeId(), a.GenLevel.Id, "__st C");
            var hideExisting = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(x => !g.Contains(x) && x.CanBeHidden(c)).Select(x => x.Id).ToList();
            if (hideExisting.Count > 0) c.HideElements(hideExisting);
            c.HideElements(new List<ElementId> { g[0].Id });
            // G3 in the TARGET: its bottom end made 2D, where the source is 3D.
            (_, DatumEnds bottom3) = Ends(g[2], b);
            g[2].SetDatumExtentType(bottom3, b, DatumExtentType.ViewSpecific);
            // G2 in the TARGET: an elbow the source does not have.
            (DatumEnds top2, _) = Ends(g[1], b);
            g[1].ShowBubbleInView(top2, a);
            g[1].ShowBubbleInView(top2, b);
            g[1].AddLeader(top2, b);
            t.Commit();
        }
        var rows = DatumMatcher.Run(doc, a, new View[] { b, c });
        Check("no Failed rows", Count(rows, "Failed") == 0, Rows(rows));
        // 0.2.7 rule: a source 3D end arrives as a 2D end at the position the source draws it.
        Check("G3: its source 3D ends arrive as 2D ends where the source draws them", TwoDLike(g[2], a, b), State(g[2], b));
        var hidden = rows.FirstOrDefault(r => r["View"] == c.Name && r["Datum"] == "Grid " + g[0].Name);
        Check("hidden grid skipped and said so", hidden != null && hidden["Status"] == "Skipped" && hidden["Note"].Contains("Hidden"), Rows(rows));
        var elbow = rows.FirstOrDefault(r => r["View"] == b.Name && r["Datum"] == "Grid " + g[1].Name);
        bool removed = elbow != null && elbow["Note"].Contains("elbow removed");
        bool reported = elbow != null && elbow["Note"].Contains("elbow left in place");
        Check("an elbow the source lacks is removed or reported, never silently kept", removed || reported, Rows(rows));
        (DatumEnds top, _) = Ends(g[1], b);
        Check("the row tells the truth about the elbow", removed == (g[1].GetLeader(top, b) == null));
        Info(removed ? "elbow removal: hiding and re-showing the bubble DOES clear an elbow" : "elbow removal: not possible through the API (reported)");
    }

    private static void MdScopeBox(Document doc)
    {
        Element? box = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().FirstElement();
        if (box == null) { Info("this model has no scope box — not exercised"); return; }
        Fixture f = MakeFixture(doc);
        // The grids must cross the box: Revit refuses to tie a datum that misses it (it raised a
        // modal "cannot be associated" error in the first run of this test).
        BoundingBoxXYZ bb = box.get_BoundingBox(null);
        XYZ mid = (bb.Min + bb.Max) / 2;
        f.X0 = mid.X - 10;
        f.Y0 = bb.Min.Y - 20;
        List<Grid> g = VerticalGrids(doc, f, 2, spacing: 10);
        using (var t = DrwStrictFailures.Start(doc, "tie to scope box", out DrwStrictFailures failures))
        {
            Parameter? p = g[0].get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST);
            if (p == null || p.IsReadOnly) { t.RollBack(); Info("scope box parameter not settable — not exercised"); return; }
            p.Set(box.Id);
            if (t.Commit() != TransactionStatus.Committed)
            {
                Fail(_scenario, "fixture: Revit would not tie the grid to the box: " + string.Join("; ", failures.Errors));
                return;
            }
        }
        Info($"grid {g[0].Name} tied to scope box '{box.Name}'");
        (DatumEnds top, DatumEnds bottom) = Ends(g[0], f.A);
        using (var t = new Transaction(doc, "source bubbles"))
        {
            t.Start();
            g[0].ShowBubbleInView(top, f.A); g[0].HideBubbleInView(bottom, f.A);
            g[0].ShowBubbleInView(top, f.B); g[0].ShowBubbleInView(bottom, f.B);
            t.Commit();
        }
        var rows = DatumMatcher.Run(doc, f.A, new View[] { f.B });
        var row = rows.FirstOrDefault(r => r["Datum"] == "Grid " + g[0].Name);
        // 0.2.7: the box is named only when it stops something; a clean match says just what changed.
        Check("tied grid is matched", row != null && row["Status"] == "Matched", Rows(rows));
        Check("its bubbles were copied", !g[0].IsBubbleVisibleInView(bottom, f.B) && g[0].IsBubbleVisibleInView(top, f.B));
        Check("its ends are 2D where the source draws them, box or not", TwoDLike(g[0], f.A, f.B), State(g[0], f.B));
        Check("no Failed rows", Count(rows, "Failed") == 0, Rows(rows));
    }

    private static void MdCandidates(Document doc)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(DatumMatcher.IsMatchView).ToList();
        View? plan = all.FirstOrDefault(v => v is ViewPlan);
        View? section = all.FirstOrDefault(v => v.ViewType == ViewType.Section);
        if (plan != null)
        {
            var c = DatumMatcher.Candidates(doc, plan);
            Check("a plan's candidates are plans only, at its scale", c.All(v => v is ViewPlan && v.Scale == plan.Scale) && !c.Contains(plan));
            int expected = all.Count(v => v is ViewPlan && v.Scale == plan.Scale) - 1;
            Check("every other plan at that scale is offered", c.Count == expected, $"{c.Count} of {expected}");
            Info($"plan '{plan.Name}' (1:{plan.Scale}): {c.Count} candidates; {all.Count(v => v is ViewPlan && v.Scale != plan.Scale)} plans at other scales not offered");
        }
        if (section != null)
        {
            var c = DatumMatcher.Candidates(doc, section);
            Check("a section's candidates face the same or the opposite way, at its scale",
                c.All(v => v is not ViewPlan && v.Scale == section.Scale && Math.Abs(v.ViewDirection.DotProduct(section.ViewDirection)) > 0.999999999));
            int expected = all.Count(v => v.Id != section.Id && v is not ViewPlan && v.Scale == section.Scale &&
                                          Math.Abs(v.ViewDirection.DotProduct(section.ViewDirection)) > 0.999999999);
            Check("and all of them", c.Count == expected, $"{c.Count} of {expected}");
            Info($"section '{section.Name}': {c.Count} parallel views at 1:{section.Scale}");
        }

        // A parallel plan at another scale is not offered.
        Fixture f = MakeFixture(doc);
        using (var t = new Transaction(doc, "rescale B"))
        {
            t.Start();
            f.B.Scale = 50;
            t.Commit();
        }
        var fc = DatumMatcher.Candidates(doc, f.A);
        Check("a plan at 1:50 is not offered for a 1:100 source", !fc.Any(v => v.Id == f.B.Id));
    }

    private static void MdSectionLevels(Document doc)
    {
        View? section = ViewsWithDatums(doc, false, true, false, 40)
            .OfType<ViewSection>()
            .FirstOrDefault(v => new FilteredElementCollector(doc, v.Id).OfClass(typeof(Level)).GetElementCount() >= 2);
        if (section == null) { Info("no section showing levels — not exercised"); return; }
        ViewSection copy;
        Level level;
        using (var t = new Transaction(doc, "parallel section"))
        {
            t.Start();
            // A fresh, fully enabled box on the source's frame, 3 ft further along its depth — the
            // crop box a section reports is not accepted back as a section box.
            BoundingBoxXYZ crop = section.CropBox;
            Transform tr = Transform.Identity;
            tr.Origin = crop.Transform.Origin + crop.Transform.BasisZ * 3;
            tr.BasisX = crop.Transform.BasisX.Normalize();
            tr.BasisY = crop.Transform.BasisY.Normalize();
            tr.BasisZ = tr.BasisX.CrossProduct(tr.BasisY).Normalize();
            double depth = Math.Max(10, crop.Max.Z - crop.Min.Z);
            var box = new BoundingBoxXYZ
            {
                Transform = tr,
                Min = new XYZ(crop.Min.X, crop.Min.Y, -depth),
                Max = new XYZ(crop.Max.X, crop.Max.Y, 0)
            };
            copy = ViewSection.CreateSection(doc, section.GetTypeId(), box);
            copy.ViewTemplateId = ElementId.InvalidElementId;
            copy.Scale = section.Scale;   // Match Datums lists only views at the source's scale
            level = new FilteredElementCollector(doc, section.Id).OfClass(typeof(Level)).Cast<Level>()
                .First(l => l.GetCurvesInView(DatumExtentType.ViewSpecific, section).Any());
            level.SetDatumExtentType(DatumEnds.End0, section, DatumExtentType.ViewSpecific);
            Curve c = level.GetCurvesInView(DatumExtentType.ViewSpecific, section).First();
            XYZ d = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
            level.SetCurveInView(DatumExtentType.ViewSpecific, section, Line.CreateBound(c.GetEndPoint(0) + d * 5, c.GetEndPoint(1)));
            t.Commit();
        }
        Info($"section '{section.Name}' → new parallel section; level '{level.Name}'");
        Check("the new section is a candidate", DatumMatcher.Candidates(doc, section).Any(v => v.Id == copy.Id));
        var rows = DatumMatcher.Run(doc, section, new View[] { copy });
        var row = rows.FirstOrDefault(r => r["Datum"] == "Level " + level.Name);
        Check("the level row says Matched", row != null && row["Status"] == "Matched", Rows(rows));
        Check("no Failed rows", Count(rows, "Failed") == 0, Rows(rows.Where(r => r["Status"] == "Failed").ToList()));
        Check("the level's ends are 2D and sit exactly where the source section draws them (crop or not)",
            TwoDLike(level, section, copy), State(level, copy));
    }

    private static void Row(List<IReadOnlyDictionary<string, string>> rows, DatumPlane d, string status)
    {
        var row = rows.FirstOrDefault(r => r["Datum"].EndsWith(" " + d.Name));
        Check($"{d.Name}: {status}", row != null && row["Status"] == status, row == null ? "no row" : row["Status"] + " — " + row["Note"]);
    }

    private static bool SameXY(Curve a, Curve b) => SameXY(a.GetEndPoint(0), b.GetEndPoint(0)) && SameXY(a.GetEndPoint(1), b.GetEndPoint(1));

    private static bool SameXY(XYZ a, XYZ b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    // =============================================================================================
    // 0.2.7 scenarios

    /// <summary>Crops plan <paramref name="v"/> to x0..x1, y0..y1 (model feet).</summary>
    private static void CropPlan(Document doc, View v, double x0, double y0, double x1, double y1)
    {
        using var t = new Transaction(doc, "crop");
        t.Start();
        BoundingBoxXYZ box = v.CropBox;
        Transform inv = box.Transform.Inverse;
        XYZ lo = inv.OfPoint(new XYZ(x0, y0, 0)), hi = inv.OfPoint(new XYZ(x1, y1, 0));
        box.Min = new XYZ(Math.Min(lo.X, hi.X), Math.Min(lo.Y, hi.Y), box.Min.Z);
        box.Max = new XYZ(Math.Max(lo.X, hi.X), Math.Max(lo.Y, hi.Y), box.Max.Z);
        v.CropBoxActive = true;
        v.CropBox = box;
        t.Commit();
    }

    private static Curve Line2D(DatumPlane d, View v) => d.GetCurvesInView(DatumExtentType.ViewSpecific, v).First();

    private static (double Top, double Bottom) Ys(DatumPlane d, View v)
    {
        Curve c = Line2D(d, v);
        return (Math.Max(c.GetEndPoint(0).Y, c.GetEndPoint(1).Y), Math.Min(c.GetEndPoint(0).Y, c.GetEndPoint(1).Y));
    }

    private static void MdCropFit(Document doc, bool sourceCropped)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> g = VerticalGrids(doc, f, 3);
        // Target B shows only the middle of the grids: y 25..75.
        CropPlan(doc, f.B, f.X0 - 10, f.Y0 + 25, f.X0 + 50, f.Y0 + 75);
        if (sourceCropped) CropPlan(doc, f.A, f.X0 - 10, f.Y0 + 10, f.X0 + 50, f.Y0 + 95);
        // In A: G1's top end 2D at y 90 (outside B's crop; 5 ft inside A's crop when A is cropped).
        using (var t = new Transaction(doc, "G1 top 2D at 90"))
        {
            t.Start();
            (DatumEnds top, _) = Ends(g[0], f.A);
            g[0].SetDatumExtentType(top, f.A, DatumExtentType.ViewSpecific);
            Curve c = Line2D(g[0], f.A);
            XYZ p0 = c.GetEndPoint(0), p1 = c.GetEndPoint(1);
            XYZ newTop = new XYZ(p0.X, f.Y0 + 90, (top == DatumEnds.End1 ? p1 : p0).Z);
            g[0].SetCurveInView(DatumExtentType.ViewSpecific, f.A,
                top == DatumEnds.End1 ? Line.CreateBound(p0, newTop) : Line.CreateBound(newTop, p1));
            t.Commit();
        }
        var rows = DatumMatcher.Run(doc, f.A, new View[] { f.B });
        Check("no Failed / Not applied rows", rows.All(r => r["Status"] is not ("Failed" or "Not applied" or "Partly matched")), Rows(rows));
        // The user's rule: exact copy, even beyond the crop — and the row says the crop hides it.
        Check("every grid is Matched and says an end is beyond the crop", g.All(x => rows.Any(r => r["Datum"] == "Grid " + x.Name &&
            r["Status"] == "Matched" && r["Note"].Contains("beyond this view's crop"))), Rows(rows));
        foreach (Grid x in g)
            Check($"{x.Name}: both ends 2D, exactly where A draws them (beyond B's crop)", TwoDLike(x, f.A, f.B),
                $"A {State(x, f.A)} | B {State(x, f.B)}");
        (double t1, _) = Ys(g[0], f.B);
        Check("G1's top is at y 90, as set in A", Math.Abs(t1 - (f.Y0 + 90)) < 1e-4, $"top {t1 - f.Y0:0.###}");
        var again = DatumMatcher.Run(doc, f.A, new View[] { f.B });
        Check("a second run changes nothing", again.All(r => r["Status"] == "Unchanged"), Rows(again));
        var back = DatumMatcher.Run(doc, f.B, new View[] { f.A });
        Check("the reverse direction (cropped → larger view) has no failures",
            back.All(r => r["Status"] is not ("Failed" or "Not applied")), Rows(back));
    }

    private static readonly DatumEnds[] BothEnds = { DatumEnds.End0, DatumEnds.End1 };

    private static void MdScopeBoxViews(Document doc)
    {
        var boxed = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => DrwScope.IsPlan(v) && DatumMatcher.ScopeBox(v) != ElementId.InvalidElementId).Take(6).ToList();
        if (boxed.Count == 0) { Info("no scope-box plans — not exercised"); return; }
        ViewPlan source;
        using (var t = new Transaction(doc, "uncropped source with 2D ends pulled far out"))
        {
            t.Start();
            source = NewPlan(doc, boxed[0].GetTypeId(), boxed[0].GenLevel.Id, "__md uncropped");
            // Make every grid's ends 2D in the source, 5 ft short of their 3D ends.
            foreach (Grid g in new FilteredElementCollector(doc, source.Id).OfClass(typeof(Grid)).Cast<Grid>().Where(g => !g.IsCurved))
            {
                try
                {
                    foreach (DatumEnds e in BothEnds) g.SetDatumExtentType(e, source, DatumExtentType.ViewSpecific);
                    Curve c = Line2D(g, source);
                    XYZ d = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
                    g.SetCurveInView(DatumExtentType.ViewSpecific, source, Line.CreateBound(c.GetEndPoint(0) + d * 5, c.GetEndPoint(1) - d * 5));
                }
                catch { /* a grid Revit will not shorten stays as it is */ }
            }
            t.Commit();
        }
        var targets = boxed.Where(v => v.GenLevel != null).Cast<View>().ToList();
        Info("targets: " + string.Join(", ", targets.Select(v => $"{v.Name} ({Crop(v)})")));
        var rows = DatumMatcher.Run(doc, source, targets);
        Info($"{Count(rows, "Matched")} matched, {Count(rows, "Unchanged")} unchanged, {Count(rows, "Skipped")} skipped, " +
             $"{Count(rows, "Partly matched") + Count(rows, "Not applied")} not fully kept, {Count(rows, "Failed")} failed");
        Check("no Failed rows", Count(rows, "Failed") == 0, Rows(rows.Where(r => r["Status"] == "Failed").ToList()));
        Check("nothing Revit refused to keep", Count(rows, "Partly matched") + Count(rows, "Not applied") == 0,
            Rows(rows.Where(r => r["Status"] is "Partly matched" or "Not applied").ToList()));
        int wrong = 0, checkedDatums = 0;
        var detail = new List<string>();
        foreach (View v in targets)
        {
            // The user's rule for scope-box views: every matched grid's ends 2D, exactly where the
            // uncropped source draws them (after commit — read here, outside the run's transactions).
            var touched = new HashSet<string>(rows.Where(r => r["View"] == v.Name && r["Status"] == "Matched").Select(r => r["Datum"]));
            foreach (Grid g in new FilteredElementCollector(doc, v.Id).OfClass(typeof(Grid)).Cast<Grid>()
                         .Where(g => !g.IsCurved && touched.Contains("Grid " + g.Name)))
            {
                checkedDatums++;
                if (TwoDLike(g, source, v)) continue;
                wrong++;
                if (detail.Count < 8) detail.Add($"{v.Name}: grid {g.Name} {State(g, v)}");
            }
        }
        Info($"{checkedDatums} matched grids checked in the scope-box views");
        Check("every matched grid in every scope-box view is 2D where the source draws it", wrong == 0 && checkedDatums > 0,
            string.Join("; ", detail));
    }

    private static void MdCategoryHidden(Document doc)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> g = VerticalGrids(doc, f, 2);
        (DatumEnds top, DatumEnds bottom) = Ends(g[0], f.A);
        using (var t = new Transaction(doc, "hide grids in B; change A"))
        {
            t.Start();
            f.B.SetCategoryHidden(new ElementId(BuiltInCategory.OST_Grids), true);
            g[0].ShowBubbleInView(top, f.A); g[0].HideBubbleInView(bottom, f.A);
            g[0].ShowBubbleInView(top, f.B); g[0].ShowBubbleInView(bottom, f.B);
            t.Commit();
        }
        var rows = DatumMatcher.Run(doc, f.A, new View[] { f.B });
        Check("every grid row says Skipped: turned off in Visibility/Graphics",
            rows.All(r => r["Status"] == "Skipped" && r["Note"].Contains("Visibility/Graphics")), Rows(rows));
        Check("and nothing was changed in B", g[0].IsBubbleVisibleInView(bottom, f.B));
    }

    private static void MdMultiSegmentAndCeiling(Document doc)
    {
        Fixture f = MakeFixture(doc);
        using (var t = new Transaction(doc, "multi-segment grid"))
        {
            t.Start();
            SketchPlane sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, f.Levels[0].Elevation)));
            var loop = new CurveLoop();
            double z = f.Levels[0].Elevation;
            loop.Append(Line.CreateBound(new XYZ(f.X0, f.Y0, z), new XYZ(f.X0, f.Y0 + 50, z)));
            loop.Append(Line.CreateBound(new XYZ(f.X0, f.Y0 + 50, z), new XYZ(f.X0 + 30, f.Y0 + 80, z)));
            MultiSegmentGrid.Create(doc, doc.GetDefaultElementTypeId(ElementTypeGroup.GridType), loop, sp.Id);
            t.Commit();
        }
        var rows = DatumMatcher.Run(doc, f.A, new View[] { f.B });
        var seg = rows.Where(r => r["Datum"].Contains("segment")).Select(r => r["Datum"]).ToList();
        Check("each segment has its own label", seg.Count == 2 && seg.Distinct().Count() == 2, string.Join(" / ", seg));
        Check("no failures", Count(rows, "Failed") == 0, Rows(rows));

        View? rcp = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<View>()
            .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.CeilingPlan);
        if (rcp == null) { Info("no ceiling plan in this model — not exercised"); return; }
        using (var t = new Transaction(doc, "source at the ceiling plan's scale"))
        {
            t.Start();
            f.A.Scale = rcp.Scale;   // only views at the source's scale are offered
            t.Commit();
        }
        Check("a floor plan's candidates include ceiling plans (same scale)", DatumMatcher.Candidates(doc, f.A).Any(v => v.Id == rcp.Id));
        Check("a ceiling plan can be the source", DatumMatcher.IsMatchView(rcp));
    }

    private static void ElevationCrop(Document doc, ElevationMode mode)
    {
        var p = new EnlargePreset
        {
            Name = "selftest", CreatePlan = true, ViewKind = EnlargeViewKind.Elevation, ElevationMode = mode,
            North = true, East = true, SectionCropOffsetM = 0.4, SectionBelowM = 0.2, SectionAboveM = 0.6,
            SectionTopFrom = SectionTopFrom.NextLevel, MinBoundaryLengthM = 2.0
        };
        List<EnlargeTarget> targets = EnlargeTargets.Rooms(doc, p.RoomLabel).Take(2).ToList();
        if (targets.Count == 0) { Info("no rooms — not exercised"); return; }
        var rows = new EnlargeCreator(doc, p, false).Create(targets, writeMarks: false);
        var made = rows.Where(r => r["Status"] == "Created" && r["Kind"] == "Elevation").ToList();
        Check("elevations created, none failed", made.Count > 0 && Count(rows, "Failed") == 0, Rows(rows.Where(r => r["Status"] != "Created").ToList()));
        double below = UnitUtils.ConvertToInternalUnits(0.2, UnitTypeId.Meters), above = UnitUtils.ConvertToInternalUnits(0.6, UnitTypeId.Meters);
        int bad = 0;
        var detail = new List<string>();
        foreach (var r in made)
        {
            View v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().First(x => !x.IsTemplate && x.Name == r["View"]);
            EnlargeTarget t = targets.First(x => r["View"].Contains(x.BaseName));
            BoundingBoxXYZ box = v.CropBox;
            double bottom = box.Transform.OfPoint(box.Min).Z, top = box.Transform.OfPoint(box.Max).Z;
            double expectTop = (t.NextLevel?.Elevation ?? t.LastStepZ) + above;
            bool ok = Math.Abs(bottom - (t.BottomZ - below)) < 1e-3 && Math.Abs(top - expectTop) < 1e-3;
            if (!ok) { bad++; detail.Add($"{v.Name}: bottom {bottom:0.###} (want {t.BottomZ - below:0.###}), top {top:0.###} (want {expectTop:0.###})"); }
            if (!r["Note"].Contains("height")) { bad++; detail.Add($"{v.Name}: row note lacks the height"); }
        }
        Info($"{made.Count} elevations checked");
        Check("each crop runs Below under the floor to Above over the next level", bad == 0, string.Join("\n        ", detail));
    }

    private static void ElevationPresetMigrates()
    {
        string dir = SettingsPaths.DrwFolder("EnlargeRoomPresets");
        string file = Path.Combine(dir, TestPreset + ".json");
        File.WriteAllText(file, "{ \"Name\": \"" + TestPreset + "\", \"ViewKind\": \"Elevation\", \"ElevationOffsetM\": 0.45, " +
                                "\"SectionCropOffsetM\": 0.3, \"SectionBelowM\": 0.3, \"SectionAboveM\": 0.3, \"SectionTopFrom\": \"NextLevel\" }");
        try
        {
            EnlargePreset? p = EnlargeStores.Room.Load(TestPreset);
            Check("loads", p != null);
            if (p == null) return;
            Check("its crop offset moved into side, below and above", p.SectionCropOffsetM == 0.45 && p.SectionBelowM == 0.45 && p.SectionAboveM == 0.45,
                $"{p.SectionCropOffsetM}/{p.SectionBelowM}/{p.SectionAboveM}");
            Check("top measured from the room, as before", p.SectionTopFrom == SectionTopFrom.TargetTop);
            EnlargeStores.Room.Save(p);
            Check("saving it again drops the old field", !File.ReadAllText(file).Contains("ElevationOffsetM"));
        }
        finally { File.Delete(file); }
    }

    // =============================================================================================
    // 0.2.8: Single Font

    private const string SfFont = "Tahoma";

    private static string? FontOf(Element e) => e.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString();

    /// <summary>Every project type Single Font covers, with its font now.</summary>
    private static Dictionary<ElementId, string> ProjectFonts(Document doc) =>
        SingleFontEngine.ProjectTypes(doc, new SingleFontPreset()).ToDictionary(t => t.Id, t => FontOf(t) ?? "");

    /// <summary>Families whose text types are not all <paramref name="font"/>, found by opening each.</summary>
    private static List<string> FamiliesNotIn(Document doc, SingleFontPreset p, string font)
    {
        var bad = new List<string>();
        foreach ((Family f, _) in SingleFontEngine.Families(doc, p))
        {
            Document? fd = null;
            try
            {
                fd = doc.EditFamily(f);
                var off = new FilteredElementCollector(fd).WhereElementIsElementType().Cast<Element>()
                    .Select(FontOf).Where(x => x != null && x != font).Distinct().ToList();
                if (off.Count > 0) bad.Add($"{f.Name} ({string.Join(", ", off)})");
            }
            catch (Exception ex) { bad.Add($"{f.Name}: {ex.Message}"); }
            finally { try { fd?.Close(false); } catch { } }
        }
        return bad;
    }

    private static void SfAll(Document doc)
    {
        var p = new SingleFontPreset { Name = "selftest", FontName = SfFont };
        var started = DateTime.Now;
        List<IReadOnlyDictionary<string, string>> rows;
        try { rows = SingleFontEngine.Run(doc, p); }
        catch (Exception ex) { Fail(_scenario, "Run threw: " + ex); return; }
        Info($"{(DateTime.Now - started).TotalSeconds:0}s: " + string.Join(", ", rows.GroupBy(r => r["Status"]).Select(g => $"{g.Count()} {g.Key}")) +
             $"; families: {rows.Count(r => r["Kind"].Contains("amil") || r["Kind"] == "Title block")}");
        Info("the TransactionGroup assimilated the family reloads (one undo step)");
        Check("no Failed rows", Count(rows, "Failed") == 0, Rows(rows.Where(r => r["Status"] == "Failed").Take(10).ToList()));
        var project = ProjectFonts(doc);
        Check("every text, dimension and model-text type is Tahoma", project.Values.All(f => f == SfFont),
            string.Join(", ", project.Where(kv => kv.Value != SfFont).Take(8).Select(kv => $"{doc.GetElement(kv.Key).Name}={kv.Value}")));
        List<string> bad = FamiliesNotIn(doc, p, SfFont);
        Check("every processed family's text is Tahoma (re-opened to check)", bad.Count == 0, string.Join("; ", bad.Take(8)));
        int badCells = 0;
        foreach (ViewSchedule vs in SingleFontEngine.Schedules(doc))
        {
            ScheduleDefinition def = vs.Definition;
            for (int i = 0; i < def.GetFieldCount(); i++)
            {
                TableCellStyle st = def.GetField(i).GetStyle();
                if (st.GetCellStyleOverrideOptions().Font && st.FontName != SfFont) badCells++;
            }
        }
        Check("every schedule column with its own font is Tahoma", badCells == 0, $"{badCells} columns");
        var again = SingleFontEngine.Run(doc, p);
        Check("a second run changes nothing", Count(again, "Changed") == 0 && Count(again, "Failed") == 0,
            Rows(again.Where(r => r["Status"] is "Changed" or "Failed").Take(10).ToList()));
    }

    private static void SfRename(Document doc)
    {
        TextNoteType baseType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().First();
        TextNoteType a, collide, taken;
        using (var t = new Transaction(doc, "rename fixtures"))
        {
            t.Start();
            a = (TextNoteType)baseType.Duplicate("__sf 2.5mm Arial");
            a.get_Parameter(BuiltInParameter.TEXT_FONT).Set("Arial");
            collide = (TextNoteType)baseType.Duplicate("__sfc Arial");
            collide.get_Parameter(BuiltInParameter.TEXT_FONT).Set("Arial");
            taken = (TextNoteType)baseType.Duplicate("__sfc Tahoma");
            t.Commit();
        }
        var p = new SingleFontPreset
        {
            Name = "selftest", FontName = SfFont, DimensionTypes = false, ModelTextTypes = false, ScheduleFonts = false,
            TagFamilies = false, TitleBlocks = false, HeadFamilies = false, OtherAnnotationFamilies = false
        };
        var rows = SingleFontEngine.Run(doc, p);
        Check("\"__sf 2.5mm Arial\" became \"__sf 2.5mm Tahoma\"", a.Name == "__sf 2.5mm Tahoma" && FontOf(a) == SfFont, $"{a.Name} / {FontOf(a)}");
        var row = rows.FirstOrDefault(r => r["Element"] == "__sfc Arial");
        Check("a name already taken is kept, and the row says so", collide.Name == "__sfc Arial" && FontOf(collide) == SfFont &&
                                                                  row != null && row["Note"].Contains("kept its name"),
            row == null ? "no row" : row["Status"] + " — " + row["Note"]);
        Check("RenamedFor leaves a longer word alone", SingleFontEngine.RenamedFor("2.5mm Arialx", "Arial", "Tahoma") == "2.5mm Arialx");
        Check("RenamedFor ignores case", SingleFontEngine.RenamedFor("2.5mm ARIAL", "Arial", "Tahoma") == "2.5mm Tahoma");
    }

    private static void SfDryRun(Document doc)
    {
        var before = ProjectFonts(doc);
        var p = new SingleFontPreset { Name = "selftest", FontName = SfFont, DryRun = true, TitleBlocks = false, OtherAnnotationFamilies = false };
        var rows = SingleFontEngine.Run(doc, p);
        Check("rows say Would change", Count(rows, "Would change") > 0 && Count(rows, "Changed") == 0, Rows(rows.Take(5).ToList()));
        var after = ProjectFonts(doc);
        Check("no project type changed", before.All(kv => after[kv.Key] == kv.Value));
        var tagNotTahoma = FamiliesNotIn(doc, new SingleFontPreset { TagFamilies = true, TitleBlocks = false, HeadFamilies = false, OtherAnnotationFamilies = false }, SfFont);
        Info($"{tagNotTahoma.Count} tag families still not Tahoma after the dry run");
        Check("families untouched (tag families still in their own fonts)",
            tagNotTahoma.Count > 0 || rows.All(r => r["Kind"] != "Tag family" || r["Status"] == "Unchanged"));
    }

    private static void SfGroupsOff(Document doc)
    {
        var before = ProjectFonts(doc);
        var p = new SingleFontPreset
        {
            Name = "selftest", FontName = SfFont, DimensionTypes = false, ModelTextTypes = false, ScheduleFonts = false,
            TagFamilies = false, TitleBlocks = false, HeadFamilies = false, OtherAnnotationFamilies = false
        };
        var rows = SingleFontEngine.Run(doc, p);
        Check("only text types in the rows", rows.All(r => r["Kind"] is "Text type" or "Setup"), string.Join(", ", rows.Select(r => r["Kind"]).Distinct()));
        bool dimsKept = before.Where(kv => doc.GetElement(kv.Key) is DimensionType).All(kv => FontOf(doc.GetElement(kv.Key)) == kv.Value);
        Check("dimension types keep their fonts", dimsKept);
        Check("text types are Tahoma", before.Keys.Select(doc.GetElement).OfType<TextNoteType>().All(t => FontOf(t) == SfFont));
    }

    private static void SfMissingFontAndBatch(Document doc)
    {
        var missing = new SingleFontPreset
        {
            Name = "selftest", FontName = "No Such Font 4711", DimensionTypes = false, ModelTextTypes = false, ScheduleFonts = false,
            TagFamilies = false, TitleBlocks = false, HeadFamilies = false, OtherAnnotationFamilies = false, DryRun = true
        };
        var rows = SingleFontEngine.Run(doc, missing);
        Check("first row warns the font is not installed", rows.Count > 0 && rows[0]["Status"] == "Warning" && rows[0]["Note"].Contains("not installed"),
            Rows(rows.Take(2).ToList()));

        var batch = new SingleFontPreset
        {
            Name = TestPreset, FontName = SfFont, DimensionTypes = false, ModelTextTypes = false, ScheduleFonts = false,
            TagFamilies = false, TitleBlocks = false, HeadFamilies = false, OtherAnnotationFamilies = false
        };
        SingleFontStore.Store.Save(batch);
        try
        {
            IBatchAction action = ActionRegistry.Create("Single Font — " + TestPreset);
            IReadOnlyDictionary<string, string> summary = action.Run(null!, doc);
            Check("the batch summary counts the Changed rows", int.Parse(summary["Created"]) > 0, string.Join(", ", summary));
            Check("the batch action made every text type Tahoma",
                new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<Element>().All(t => FontOf(t) == SfFont));
            var rerun = action.RunDetailRows(null!, doc).ToList();
            Check("a batch rerun changes nothing", rerun.All(r => r["Status"] != "Changed"));
        }
        finally { SingleFontStore.Store.Delete(TestPreset); }
    }

    // =============================================================================================
    // 0.3.0: Preset Tagger. Each scenario tags the model's own doors, windows and rooms in a NEW plan
    // of the level with the most doors — a new plan carries no tags, so what it holds afterwards is
    // exactly what the tool made.

    private static Level PtLevel(Document doc) =>
        new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType()
            .Select(d => doc.GetElement(d.LevelId) as Level).Where(l => l != null)
            .GroupBy(l => l!.Id).OrderByDescending(g => g.Count()).First().First()!;

    private static ViewPlan PtPlan(Document doc, int scale, string name = "__pt")
    {
        using var t = new Transaction(doc, "pt plan");
        t.Start();
        ViewPlan v = NewPlan(doc, DrwLookup.ViewTypes(doc, ViewFamily.FloorPlan).First().Id, PtLevel(doc).Id, name);
        v.Scale = scale;
        t.Commit();
        return v;
    }

    private static string PtFirstType(Document doc, BuiltInCategory c) =>
        PresetTagger.TagTypes(doc, c).Where(s => s.Category.Id.Value != (long)BuiltInCategory.OST_MultiCategoryTags)
            .Select(s => DrwLookup.TypeLabel(s)).FirstOrDefault() ?? "";

    private static PresetTaggerPreset PtPreset(Document doc, params TagRule[] rules) => new()
    {
        Name = TestPreset,
        Rules = rules.Length > 0 ? rules.ToList() : new List<TagRule>
        {
            new() { Category = "OST_Doors", TagType = PtFirstType(doc, BuiltInCategory.OST_Doors) },
            new() { Category = "OST_Windows", TagType = PtFirstType(doc, BuiltInCategory.OST_Windows) },
            new() { Category = "OST_Rooms", TagType = PtFirstType(doc, BuiltInCategory.OST_Rooms) }
        },
        Scope = DrwViewScope.AllPlans, ViewNameContains = "__pt", Plans = true
    };

    /// <summary>Every tag in the view: the element it tags, and the tag.</summary>
    private static List<(ElementId Tagged, Element Tag)> PtTags(Document doc, View v)
    {
        var list = new List<(ElementId, Element)>();
        foreach (IndependentTag t in new FilteredElementCollector(doc, v.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            foreach (ElementId id in t.GetTaggedLocalElementIds()) list.Add((id, t));
        foreach (RoomTag t in new FilteredElementCollector(doc, v.Id).OfClass(typeof(SpatialElementTag)).OfType<RoomTag>())
            list.Add((t.TaggedLocalRoomId, t));
        return list;
    }

    private static int PtCount(List<IReadOnlyDictionary<string, string>> rows, string status) => rows.Count(r => r["Status"] == status);

    /// <summary>Pairs of tags in a plan whose boxes overlap by more than 0.2 mm on paper.</summary>
    private static List<string> PtOverlaps(IEnumerable<Element> tags, View v)
    {
        double tol = DrwUnits.FromMillimeters(0.2 * v.Scale);
        var boxes = tags.Select(t => (t, b: t.get_BoundingBox(v))).Where(x => x.b != null).ToList();
        var bad = new List<string>();
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                BoundingBoxXYZ a = boxes[i].b!, b = boxes[j].b!;
                if (a.Min.X < b.Max.X - tol && b.Min.X < a.Max.X - tol && a.Min.Y < b.Max.Y - tol && b.Min.Y < a.Max.Y - tol)
                    bad.Add($"{boxes[i].t.Id} / {boxes[j].t.Id}");
            }
        return bad;
    }

    private static void PtBasicAndRerun(Document doc)
    {
        PresetTaggerPreset p = PtPreset(doc);
        Info("tag types: " + string.Join(" / ", p.Rules.Select(r => r.TagType)));
        ViewPlan v = PtPlan(doc, 100);
        var rows = PresetTagger.Run(doc, new List<View> { v }, p);
        int created = PtCount(rows, "Created") + PtCount(rows, "Warning");
        Info($"{PtCount(rows, "Created")} created, {PtCount(rows, "Warning")} warned, {PtCount(rows, "Skipped")} skipped, {PtCount(rows, "Failed")} failed");
        foreach (var r in rows.Where(r => r["Note"].Length > 0)) Info($"  {r["Category"]} {r["Element"]}: {r["Status"]} — {r["Note"]}");
        Check("doors, windows and rooms tagged, none failed", created > 0 && PtCount(rows, "Failed") == 0, Rows(rows.Take(6).ToList()));
        List<(ElementId Tagged, Element Tag)> tags = PtTags(doc, v);
        Check("the view holds exactly one tag per Created row", tags.Count == created, $"{tags.Count} tags, {created} rows");
        Check("no element carries two tags", tags.GroupBy(t => t.Tagged).All(g => g.Count() == 1));
        Check("no tag got a leader the rule did not ask for",
            tags.All(t => t.Tag is IndependentTag it ? !it.HasLeader : !((SpatialElementTag)t.Tag).HasLeader));
        foreach (string cat in new[] { "Doors", "Windows", "Rooms" })
            Check($"{cat}: at least one tagged", rows.Any(r => r["Status"] == "Created" && r["Category"] == cat));
        List<string> overlaps = PtOverlaps(tags.Select(t => t.Tag), v);
        Check("no two new tags overlap", overlaps.Count == 0, $"{overlaps.Count}: " + string.Join(", ", overlaps.Take(8)));
        foreach (string pair in overlaps.Take(4))
            foreach (string id in pair.Split('/').Select(s => s.Trim()))
            {
                var hit = tags.First(x => x.Tag.Id.Value.ToString() == id);
                Element el = doc.GetElement(hit.Tagged);
                BoundingBoxXYZ b = hit.Tag.get_BoundingBox(v);
                var row = rows.FirstOrDefault(r => r["Element"].EndsWith($"[{el.Id.Value}]"));
                Info($"  tag {id} ({hit.Tag.GetType().Name}) on {el.Category?.Name} {el.Id}: box {b.Min.X:0.00},{b.Min.Y:0.00}..{b.Max.X:0.00},{b.Max.Y:0.00}" +
                     $"; note '{row?["Note"]}'");
                if (hit.Tag is RoomTag rt)
                {
                    XYZ loc = ((LocationPoint)rt.Location).Point, head = rt.TagHeadPosition, roomPt = ((LocationPoint)((Room)el).Location).Point;
                    Info($"    room tag: leader {rt.HasLeader}, location {loc.X:0.00},{loc.Y:0.00}, head {head.X:0.00},{head.Y:0.00}, " +
                         $"room point {roomPt.X:0.00},{roomPt.Y:0.00}");
                    // Probe: the same tag type placed fresh at its current spot — is the box the same?
                    using var probe = new Transaction(doc, "probe");
                    probe.Start();
                    RoomTag fresh = doc.Create.NewRoomTag(new LinkElementId(el.Id), new UV(loc.X, loc.Y), v.Id);
                    fresh.ChangeTypeId(rt.GetTypeId());
                    doc.Regenerate();
                    BoundingBoxXYZ fb = fresh.get_BoundingBox(v);
                    Info($"    the same tag placed fresh there: box {fb.Min.X:0.00},{fb.Min.Y:0.00}..{fb.Max.X:0.00},{fb.Max.Y:0.00}");
                    probe.RollBack();
                }
            }
        Check("every skipped row says why", rows.Where(r => r["Status"] == "Skipped").All(r => r["Note"].Length > 0));

        var rerun = PresetTagger.Run(doc, new List<View> { v }, p);
        Check("a rerun creates nothing", PtCount(rerun, "Created") == 0, Rows(rerun.Where(r => r["Status"] == "Created").Take(4).ToList()));
        Check("a rerun leaves the tag count alone", PtTags(doc, v).Count == tags.Count);
        Check("a rerun skips every tagged element as already tagged",
            rerun.Count(r => r["Note"] == "Already tagged in this view.") >= created,
            $"{rerun.Count(r => r["Note"] == "Already tagged in this view.")} of {created}");
    }

    /// <summary>The distance is paper millimetres: at 1:200 the tag sits four times as far (in model
    /// terms) from the door as at 1:50.</summary>
    private static void PtScale(Document doc)
    {
        var rule = new TagRule { Category = "OST_Doors", TagType = PtFirstType(doc, BuiltInCategory.OST_Doors),
            Position = TagPosition.Above, DistanceMm = 8 };
        foreach (int scale in new[] { 50, 200 })
        {
            ViewPlan v = PtPlan(doc, scale, "__pt " + scale);
            var rows = PresetTagger.Run(doc, new List<View> { v }, PtPreset(doc, rule));
            double expected = DrwUnits.FromMillimeters(8.0 * scale);
            var ratios = new List<double>();
            foreach (IndependentTag t in new FilteredElementCollector(doc, v.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                if (doc.GetElement(t.GetTaggedLocalElementIds().First()) is not FamilyInstance door
                    || door.Location is not LocationPoint lp) continue;
                bool preferred = rows.Any(r => r["Element"].EndsWith($"[{door.Id.Value}]") && r["Note"].Length == 0);
                if (preferred) ratios.Add((t.TagHeadPosition - lp.Point).DotProduct(v.UpDirection) / expected);
            }
            Info($"1:{scale}: {ratios.Count} tags at the preferred spot; offset / expected = " +
                 string.Join(", ", ratios.Take(6).Select(r => r.ToString("0.000"))));
            Check($"1:{scale}: tags at the preferred spot sit 8 mm on paper above the door",
                ratios.Count > 0 && ratios.All(r => Math.Abs(r - 1) < 0.01), $"{ratios.Count} tags");
        }
    }

    /// <summary>The scales a commercial tagger was measured at (26 Sep 2026, Snowdon L2_SD: 31, 87 and
    /// 333 overlapping tags). Here: doors, windows and rooms on the level with the most doors.</summary>
    private static void PtSbpScales(Document doc)
    {
        foreach (int scale in new[] { 48, 96, 192 })
        {
            ViewPlan v = PtPlan(doc, scale, "__pt " + scale);
            var rows = PresetTagger.Run(doc, new List<View> { v }, PtPreset(doc));
            List<string> overlaps = PtOverlaps(PtTags(doc, v).Select(t => t.Tag), v);
            Info($"1:{scale}: {PtCount(rows, "Created")} created, {PtCount(rows, "Warning")} warned, " +
                 $"{PtCount(rows, "Skipped")} skipped (every spot taken), {overlaps.Count} overlapping pairs");
            Check($"1:{scale}: no two tags overlap", overlaps.Count == 0, string.Join(", ", overlaps.Take(8)));
            Check($"1:{scale}: none failed", PtCount(rows, "Failed") == 0);
        }
    }

    /// <summary>A text note on the preferred spot sends the tag elsewhere; one covering every spot
    /// leaves the door untagged and Skipped.</summary>
    private static void PtOverlap(Document doc)
    {
        ViewPlan v = PtPlan(doc, 100);
        FamilyInstance door = new FilteredElementCollector(doc, v.Id).OfCategory(BuiltInCategory.OST_Doors)
            .WhereElementIsNotElementType().OfType<FamilyInstance>().First(d => d.Location is LocationPoint);
        XYZ at = ((LocationPoint)door.Location).Point;
        ElementId textType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId();
        var rule = new TagRule { Category = "OST_Doors", TagType = PtFirstType(doc, BuiltInCategory.OST_Doors),
            FamilyContains = door.Symbol.FamilyName };
        string mine = $"[{door.Id.Value}]";

        TextNote note;
        using (var t = new Transaction(doc, "pt note"))
        {
            t.Start();
            note = TextNote.Create(doc, v.Id, at, "XX", new TextNoteOptions(textType) { HorizontalAlignment = HorizontalTextAlignment.Center });
            t.Commit();
        }
        var rows = PresetTagger.Run(doc, new List<View> { v }, PtPreset(doc, rule));
        var row = rows.FirstOrDefault(r => r["Element"].EndsWith(mine));
        {
            // Diagnostics: the note's box and, in a clean plan, the tag's box — both from the door's point, in paper mm.
            double mm = DrwUnits.FromMillimeters(v.Scale);
            string Rel(BoundingBoxXYZ? b) => b == null ? "none" :
                $"x {(b.Min.X - at.X) / mm:0.0}..{(b.Max.X - at.X) / mm:0.0}, y {(b.Min.Y - at.Y) / mm:0.0}..{(b.Max.Y - at.Y) / mm:0.0}";
            Info("note box: " + Rel(note.get_BoundingBox(v)));
            ViewPlan clean = PtPlan(doc, 100, "__pt clean");
            PresetTagger.Run(doc, new List<View> { clean }, PtPreset(doc, rule));
            Element? cleanTag = PtTags(doc, clean).FirstOrDefault(x => x.Tagged == door.Id).Tag;
            Info("tag box with nothing in the way: " + Rel(cleanTag?.get_BoundingBox(clean)) +
                 (cleanTag is IndependentTag ct ? $"; head {(ct.TagHeadPosition.X - at.X) / mm:0.0}, {(ct.TagHeadPosition.Y - at.Y) / mm:0.0}" : ""));
            foreach (Element other in PtTags(doc, v).Select(x => x.Tag))
                if (other.get_BoundingBox(v) is BoundingBoxXYZ ob && Math.Abs((ob.Min.X + ob.Max.X) / 2 - at.X) < 20 * mm
                    && Math.Abs((ob.Min.Y + ob.Max.Y) / 2 - at.Y) < 20 * mm)
                    Info("nearby tag: " + Rel(ob));
        }
        Check("the door under the note is tagged at another spot", row != null && row["Status"] == "Created" && row["Note"].StartsWith("Moved"),
            row == null ? "no row" : string.Join(" | ", row.Values));
        Element? tag = PtTags(doc, v).FirstOrDefault(x => x.Tagged == door.Id).Tag;
        Check("the moved tag clears the note", tag != null && PtOverlaps(new[] { tag, note }, v).Count == 0);

        // Now a note far larger than any spot: every candidate overlaps it.
        ViewPlan v2 = PtPlan(doc, 100, "__pt blocked");
        using (var t = new Transaction(doc, "pt wall of text"))
        {
            t.Start();
            string block = string.Join("\r", Enumerable.Repeat("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", 16));
            TextNote big = TextNote.Create(doc, v2.Id, at, Math.Min(TextElement.GetMaximumAllowedWidth(doc, textType), DrwUnits.FromMillimeters(100)), block,
                new TextNoteOptions(textType) { HorizontalAlignment = HorizontalTextAlignment.Center });
            // Centre it on the door: move by the difference between the door and the note's middle.
            doc.Regenerate();
            BoundingBoxXYZ bb = big.get_BoundingBox(v2);
            ElementTransformUtils.MoveElement(doc, big.Id, new XYZ(at.X - (bb.Min.X + bb.Max.X) / 2, at.Y - (bb.Min.Y + bb.Max.Y) / 2, 0));
            t.Commit();
        }
        var blocked = PresetTagger.Run(doc, new List<View> { v2 }, PtPreset(doc, rule));
        var brow = blocked.FirstOrDefault(r => r["Element"].EndsWith(mine));
        Check("a door with every spot covered is Skipped", brow != null && brow["Status"] == "Skipped" && brow["Note"].StartsWith("Every position"),
            brow == null ? "no row" : string.Join(" | ", brow.Values));
        Check("…and no tag is left on it", PtTags(doc, v2).All(x => x.Tagged != door.Id));
    }

    private static void PtMissingTypeAndTwoRules(Document doc)
    {
        ViewPlan v = PtPlan(doc, 100);
        var rows = PresetTagger.Run(doc, new List<View> { v }, PtPreset(doc,
            new TagRule { Category = "OST_Doors", TagType = "No Such Tag : 4711" },
            new TagRule { Category = "OST_Windows", TagType = PtFirstType(doc, BuiltInCategory.OST_Windows) }));
        Check("a missing tag type fails its own rule", rows.Count(r => r["Status"] == "Failed" && r["Category"] == "Doors") == 1,
            Rows(rows.Where(r => r["Status"] == "Failed").ToList()));
        Check("…tags no door", PtTags(doc, v).All(x => doc.GetElement(x.Tagged)?.Category?.Id.Value != (long)BuiltInCategory.OST_Doors));
        Check("…and the windows are still tagged", rows.Any(r => r["Status"] == "Created" && r["Category"] == "Windows"));

        // Two rules for doors with two different tag families: each door gets both.
        List<string> families = PresetTagger.TagTypes(doc, BuiltInCategory.OST_Doors)
            .GroupBy(s => s.FamilyName).Select(g => DrwLookup.TypeLabel(g.First())).ToList();
        if (families.Count < 2)
        {
            Info("only one door tag family in this model: two-rule case not testable here");
            return;
        }
        ViewPlan v2 = PtPlan(doc, 100, "__pt two");
        var two = PresetTagger.Run(doc, new List<View> { v2 }, PtPreset(doc,
            new TagRule { Category = "OST_Doors", TagType = families[0] },
            new TagRule { Category = "OST_Doors", TagType = families[1], Position = TagPosition.Below, DistanceMm = 4 }));
        Info($"{families[0]} + {families[1]}: {PtCount(two, "Created")} created, {PtCount(two, "Skipped")} skipped");
        foreach (var g in two.Where(r => r["Status"] != "Created").GroupBy(r => r["Tag type"] + " → " + r["Status"] + ": " + r["Note"]))
            Info($"  {g.Count()} × {g.Key}");
        var perDoor = PtTags(doc, v2).GroupBy(x => x.Tagged).ToList();
        Check("two door rules with different families both tag", perDoor.Any(g => g.Count() == 2), $"{perDoor.Count} doors tagged");
        var again = PresetTagger.Run(doc, new List<View> { v2 }, PtPreset(doc,
            new TagRule { Category = "OST_Doors", TagType = families[0] },
            new TagRule { Category = "OST_Doors", TagType = families[1], Position = TagPosition.Below, DistanceMm = 4 }));
        Check("…and a rerun of both adds nothing", PtCount(again, "Created") == 0);
    }

    private static void PtDryRunAndBatch(Document doc)
    {
        ViewPlan v = PtPlan(doc, 100);
        PresetTaggerPreset dry = PtPreset(doc);
        dry.DryRun = true;
        int before = new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).GetElementCount()
                     + new FilteredElementCollector(doc).OfClass(typeof(SpatialElementTag)).GetElementCount();
        var rows = PresetTagger.Run(doc, new List<View> { v }, dry);
        int after = new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).GetElementCount()
                    + new FilteredElementCollector(doc).OfClass(typeof(SpatialElementTag)).GetElementCount();
        Check("dry run reports what it would tag", PtCount(rows, "Would create") > 0 && PtCount(rows, "Created") == 0);
        Check("dry run creates nothing", before == after, $"{before} → {after}");

        PresetTaggerStore.Store.Save(PtPreset(doc));
        try
        {
            IBatchAction action = ActionRegistry.Create(DrwActions.PresetTaggerPrefix + TestPreset);
            Check("the batch action is found by its preset name", action.Name == DrwActions.PresetTaggerPrefix + TestPreset, action.Name);
            IReadOnlyDictionary<string, string> summary = action.Run(null!, doc);
            Check("the batch run tags the __pt plan", int.Parse(summary["Created"]) > 0 && summary["Failed"] == "0", string.Join(", ", summary));
            var rerun = action.RunDetailRows(null!, doc).ToList();
            Check("a batch rerun creates nothing", rerun.All(r => r["Status"] != "Created"));
        }
        finally { PresetTaggerStore.Store.Delete(TestPreset); }
    }

    // ---------------------------------------------------------------------------------------------
    // 0.3.0: position by element, wall material tags, "?" repair.

    /// <summary>A wall's side face on the exterior or interior, as Revit gives it.</summary>
    private static PlanarFace? PtFace(Wall w, bool exterior) =>
        HostObjectUtils.GetSideFaces(w, exterior ? ShellLayerType.Exterior : ShellLayerType.Interior)
            .Select(r => w.GetGeometryObjectFromReference(r)).OfType<PlanarFace>().FirstOrDefault(f => Math.Abs(f.FaceNormal.Z) < 0.1);

    /// <summary>Doors tagged on their exterior side sit outside the host wall's exterior face, and
    /// on the interior side outside its interior face — whichever way each door or plan is turned. A
    /// note on a door's exterior spot moves its tag further out, never to the interior.</summary>
    private static void PtElementSide(Document doc)
    {
        string type = PtFirstType(doc, BuiltInCategory.OST_Doors);
        var heads = new Dictionary<ElementId, XYZ>();
        foreach (TagSide side in new[] { TagSide.Exterior, TagSide.Interior })
        {
            ViewPlan v = PtPlan(doc, 100, "__pt side " + side);
            var rule = new TagRule { Category = "OST_Doors", TagType = type, PositionMode = TagPositionMode.Element, Side = side, DistanceMm = 1 };
            var rows = PresetTagger.Run(doc, new List<View> { v }, PtPreset(doc, rule));
            Info($"{side}: {PtCount(rows, "Created")} created, {PtCount(rows, "Warning")} warned, {PtCount(rows, "Skipped")} skipped, {PtCount(rows, "Failed")} failed");
            foreach (var g in rows.Where(r => r["Note"].Length > 0).GroupBy(r => r["Status"] + ": " + r["Note"]).Take(6))
                Info($"  {g.Count()} × {g.Key}");
            Check($"{side}: doors tagged, none failed", PtCount(rows, "Created") + PtCount(rows, "Warning") > 0 && PtCount(rows, "Failed") == 0);
            int good = 0, wrong = 0;
            var bad = new List<string>();
            foreach ((ElementId id, Element tag) in PtTags(doc, v))
            {
                if (doc.GetElement(id) is not FamilyInstance door || door.Host is not Wall w || tag is not IndependentTag it) continue;
                if (PtFace(w, side == TagSide.Exterior) is not PlanarFace face) continue;
                double outside = (it.TagHeadPosition - face.Origin).DotProduct(new XYZ(face.FaceNormal.X, face.FaceNormal.Y, 0).Normalize());
                if (outside > 0) good++;
                else { wrong++; bad.Add($"{door.Id}: {outside / DrwUnits.FromMillimeters(1):0} mm"); }
                if (side == TagSide.Exterior) heads[door.Id] = it.TagHeadPosition;
            }
            Check($"{side}: every door tag is outside the wall's {side.ToString().ToLowerInvariant()} face", good > 0 && wrong == 0,
                $"{good} right, {wrong} wrong: " + string.Join(", ", bad.Take(6)));
        }
        Wall? any = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault(x => PtFace(x, true) != null);
        if (any != null)
            Info($"exterior face normal · Wall.Orientation = {PtFace(any, true)!.FaceNormal.DotProduct(any.Orientation):0.00} (1 = they agree)");

        // A note on one door's exterior spot: its tag stays on the exterior.
        if (heads.Count == 0) return;
        (ElementId doorId, XYZ head) = heads.First();
        var door1 = (FamilyInstance)doc.GetElement(doorId);
        ViewPlan vb = PtPlan(doc, 100, "__pt side blocked");
        ElementId textType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId();
        using (var t = new Transaction(doc, "pt note"))
        {
            t.Start();
            TextNote.Create(doc, vb.Id, head, "XXXX", new TextNoteOptions(textType) { HorizontalAlignment = HorizontalTextAlignment.Center });
            t.Commit();
        }
        var blocked = PresetTagger.Run(doc, new List<View> { vb }, PtPreset(doc,
            new TagRule { Category = "OST_Doors", TagType = type, PositionMode = TagPositionMode.Element, Side = TagSide.Exterior, DistanceMm = 1,
                FamilyContains = door1.Symbol.FamilyName }));
        var row = blocked.FirstOrDefault(r => r["Element"].EndsWith($"[{doorId.Value}]"));
        Info("blocked door: " + (row == null ? "no row" : row["Status"] + " — " + row["Note"]));
        Element? moved = PtTags(doc, vb).FirstOrDefault(x => x.Tagged == doorId).Tag;
        bool exteriorStill = moved is IndependentTag mt && door1.Host is Wall hw && PtFace(hw, true) is PlanarFace ef &&
                             (mt.TagHeadPosition - ef.Origin).DotProduct(ef.FaceNormal) > 0;
        Check("a blocked exterior tag slides out on the exterior, or is skipped — never the interior",
            row != null && (row["Status"] == "Skipped" ? moved == null : exteriorStill && row["Note"].Contains("further out")),
            row == null ? "no row" : string.Join(" | ", row.Values));
    }

    private const string PtWallMark = "__PTFIN";

    private sealed class PtWallSet
    {
        public Wall H = null!, V = null!;
        public ElementId Ext = ElementId.InvalidElementId, Int = ElementId.InvalidElementId;
        public double ExtMid, IntMid;   // from the centre line to each finish tag point (5 mm in from the face), feet
    }

    /// <summary>Two new walls (one along X, one along Y) of a new type: 15 mm exterior finish, 200 mm
    /// core, 20 mm interior finish, each finish its own material with a Mark. Clear of the model.</summary>
    private static PtWallSet PtMakeWalls(Document doc)
    {
        using var t = new Transaction(doc, "pt walls");
        t.Start();
        WallType basic = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(w => w.Kind == WallKind.Basic);
        var type = (WallType)basic.Duplicate("__pt finish wall");
        var set = new PtWallSet
        {
            Ext = Material.Create(doc, "__pt exterior finish"),
            Int = Material.Create(doc, "__pt interior finish")
        };
        ElementId core = Material.Create(doc, "__pt core");
        doc.GetElement(set.Ext).get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("E1");
        doc.GetElement(set.Int).get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("I1");
        doc.GetElement(set.Ext).get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.Set("Exterior plaster");
        doc.GetElement(set.Int).get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.Set("Interior plaster");
        double e = DrwUnits.FromMillimeters(15), c = DrwUnits.FromMillimeters(200), i = DrwUnits.FromMillimeters(20);
        CompoundStructure cs = CompoundStructure.CreateSimpleCompoundStructure(new List<CompoundStructureLayer>
        {
            new(e, MaterialFunctionAssignment.Finish1, set.Ext),
            new(c, MaterialFunctionAssignment.Structure, core),
            new(i, MaterialFunctionAssignment.Finish2, set.Int)
        });
        cs.StructuralMaterialIndex = 1;
        type.SetCompoundStructure(cs);
        type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.Set(PtWallMark);
        double half = (e + c + i) / 2;
        set.ExtMid = half - DrwUnits.FromMillimeters(PresetTagger.FacePointMm);
        set.IntMid = -(half - DrwUnits.FromMillimeters(PresetTagger.FacePointMm));

        Level level = PtLevel(doc);
        double maxX = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Select(w => w.get_BoundingBox(null)).Where(b => b != null)
            .Select(b => b!.Max.X).DefaultIfEmpty(0).Max();
        var o = new XYZ(maxX + 60, 0, level.Elevation);
        set.H = Wall.Create(doc, Line.CreateBound(o, o + new XYZ(20, 0, 0)), type.Id, level.Id, 10, 0, false, false);
        set.V = Wall.Create(doc, Line.CreateBound(o + new XYZ(40, 0, 0), o + new XYZ(40, 20, 0)), type.Id, level.Id, 10, 0, false, false);
        set.H.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)WallLocationLine.WallCenterline);
        set.V.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)WallLocationLine.WallCenterline);
        t.Commit();
        return set;
    }

    /// <summary>Four material tag families, one per side the tag sits on, each a triangle on that
    /// side of its point with the tip at the point — the Thai tag's four types, drawn as the test
    /// needs them. They have no label (the API cannot add one), so their tags read nothing.</summary>
    private static List<string> PtMakeMaterialTagFamily(Document doc)
    {
        var app = doc.Application;
        string rft = Path.Combine(app.FamilyTemplatePath, "Annotations", "Metric Generic Tag.rft");
        if (!File.Exists(rft))
            rft = $@"C:\ProgramData\Autodesk\RVT {app.VersionNumber}\Family Templates\English\Annotations\Metric Generic Tag.rft";
        if (!File.Exists(rft)) return new List<string>();
        double s = DrwUnits.FromMillimeters(3);
        var sides = new (string Name, XYZ Out, XYZ Across)[]
        {
            ("Above", XYZ.BasisY, XYZ.BasisX), ("Below", -XYZ.BasisY, XYZ.BasisX),
            ("Left", -XYZ.BasisX, XYZ.BasisY), ("Right", XYZ.BasisX, XYZ.BasisY)
        };
        var labels = new List<string>();
        foreach ((string name, XYZ outward, XYZ across) in sides)
        {
            string family = "__pt MT " + name;
            string path = Path.Combine(Path.GetTempPath(), family + ".rfa");
            Document fam = app.NewFamilyDocument(rft);
            try
            {
                using (var t = new Transaction(fam, "pt family"))
                {
                    t.Start();
                    fam.OwnerFamily.FamilyCategory = Category.GetCategory(fam, BuiltInCategory.OST_MaterialTags);
                    View fv = new FilteredElementCollector(fam).OfClass(typeof(View)).Cast<View>().First(v => !v.IsTemplate);
                    XYZ a = XYZ.Zero, b = outward * s - across * (s / 2), c = outward * s + across * (s / 2);
                    foreach ((XYZ p, XYZ q) in new[] { (a, b), (b, c), (c, a), (a, -outward * s) })
                        fam.FamilyCreate.NewDetailCurve(fv, Line.CreateBound(p, q));
                    fam.FamilyManager.NewType(name);
                    // The template's sample note made the family's box metres wide.
                    ICollection<ElementId> notes = new FilteredElementCollector(fam).OfClass(typeof(TextNote)).ToElementIds();
                    if (notes.Count > 0) fam.Delete(notes);
                    t.Commit();
                }
                fam.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally { fam.Close(false); }
            using (var t = new Transaction(doc, "pt load"))
            {
                t.Start();
                doc.LoadFamily(path, out Family _);
                t.Commit();
            }
            labels.Add($"{family} : {name}");
        }
        return labels;
    }

    private static WallMatTagPreset PtWmt(List<string> types, MaterialFaces faces = MaterialFaces.Both) => new()
    {
        Name = TestPreset, Faces = faces,
        TypeAbove = types[0], TypeBelow = types[1], TypeLeft = types[2], TypeRight = types[3],
        TypeMarkContains = PtWallMark,   // the test walls only: every material tag costs Revit a view redraw
        RepairMaterialTags = false, Scope = DrwViewScope.AllPlans, ViewNameContains = "__pt", Plans = true
    };

    private static List<IReadOnlyDictionary<string, string>> Wmt(Document doc, View v, WallMatTagPreset p) =>
        WallMatTag.Run(doc, new List<View> { v }, p);

    private static List<IndependentTag> PtMaterialTags(Document doc, View v, Wall w) =>
        new FilteredElementCollector(doc, v.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
            .Where(t => t.Category?.Id.Value == (long)BuiltInCategory.OST_MaterialTags && t.GetTaggedLocalElementIds().Contains(w.Id)).ToList();

    /// <summary>Wall material tags: one per finish face, each point in the middle of that face's
    /// outermost layer, each of the type that sits on the side the view shows; a rerun adds none; a
    /// Coarse view tags nothing.</summary>
    private static void PtMaterial(Document doc)
    {
        List<string> modelTypes = DrwLookup.Labels(PresetTagger.MaterialTagTypes(doc));
        Info("material tag types already in the model: " + (modelTypes.Count == 0 ? "none" : string.Join(", ", modelTypes)));
        List<string> types = PtMakeMaterialTagFamily(doc);
        Check("four one-direction material tag families were made for the test", types.Count == 4);
        if (types.Count != 4) return;
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);

        ViewPlan v = PtPlan(doc, 100, "__pt material");
        using (var t = new Transaction(doc, "pt medium")) { t.Start(); v.DetailLevel = ViewDetailLevel.Medium; t.Commit(); }
        WallMatTagPreset rule = PtWmt(types);
        var rows = Wmt(doc, v, rule);
        var mine = rows.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]") || r["Element"].Contains($"[{walls.V.Id.Value}]")).ToList();
        foreach (var r in mine) Info($"  {r["Element"]}: {r["Tag type"]} {r["Status"]} — {r["Note"]}");
        Check("each test wall: both faces tagged, none failed, skipped or warned",
            mine.Count == 4 && mine.All(r => r["Status"] == "Created"), Rows(mine));
        Check("…each row says the tag shows '?' until touched in Revit", mine.All(r => r["Note"].Contains(PresetTagger.UntouchedNote)));

        foreach ((Wall w, string name) in new[] { (walls.H, "wall along X"), (walls.V, "wall along Y") })
        {
            List<IndependentTag> tags = PtMaterialTags(doc, v, w);
            Check($"{name}: two material tags", tags.Count == 2, tags.Count.ToString());
            XYZ start = ((LocationCurve)w.Location).Curve.GetEndPoint(0);
            foreach (IndependentTag tag in tags)
            {
                double off = (tag.TagHeadPosition - start).DotProduct(w.Orientation);
                bool exterior = off > 0;
                double want = exterior ? walls.ExtMid : walls.IntMid;
                XYZ n = exterior ? w.Orientation : w.Orientation.Negate();
                double ux = n.DotProduct(v.RightDirection), uy = n.DotProduct(v.UpDirection);
                string sits = Math.Abs(ux) >= Math.Abs(uy) ? (ux > 0 ? "Right" : "Left") : (uy > 0 ? "Above" : "Below");
                string typeName = doc.GetElement(tag.GetTypeId())?.Name ?? "";
                Info($"  {name} {(exterior ? "exterior" : "interior")}: point {off / mm:0.0} mm from the centre line (want {want / mm:0.0}, 5 mm in from the face), " +
                     $"type {typeName}, leader {tag.HasLeader}");
                Check($"{name} {(exterior ? "exterior" : "interior")}: point 5 mm in from the finish face", Math.Abs(off - want) < 0.5 * mm,
                    $"{off / mm:0.0} vs {want / mm:0.0}");
                Check($"{name} {(exterior ? "exterior" : "interior")}: the '{sits}' type, no leader", typeName == sits && !tag.HasLeader, typeName);
            }
        }

        foreach ((Wall w, string name) in new[] { (walls.H, "wall along X"), (walls.V, "wall along Y") })
        {
            Line wl = (Line)((LocationCurve)w.Location).Curve;
            var along = PtMaterialTags(doc, v, w).Select(x => (x.TagHeadPosition - wl.GetEndPoint(0)).DotProduct(wl.Direction)).ToList();
            Check($"{name}: the two faces' tags line up", along.Count == 2 && Math.Abs(along[0] - along[1]) < mm,
                string.Join(" / ", along.Select(a => $"{a / mm:0} mm")));
        }

        var rerun = Wmt(doc, v, rule);
        Check("a material rerun adds nothing", PtCount(rerun, "Created") + PtCount(rerun, "Warning") == 0 &&
            PtMaterialTags(doc, v, walls.H).Count == 2, Rows(rerun.Where(r => r["Status"] != "Skipped").Take(4).ToList()));
        Check("…each face skipped as already tagged",
            rerun.Count(r => r["Note"] == "This face already has a material tag in this view." &&
                             (r["Element"].Contains($"[{walls.H.Id.Value}]") || r["Element"].Contains($"[{walls.V.Id.Value}]"))) == 4);

        // Only the exterior face.
        ViewPlan ve = PtPlan(doc, 100, "__pt material ext");
        using (var t = new Transaction(doc, "pt medium")) { t.Start(); ve.DetailLevel = ViewDetailLevel.Fine; t.Commit(); }
        Wmt(doc, ve, PtWmt(types, MaterialFaces.Exterior));
        Check("'Exterior face' tags one face per wall", PtMaterialTags(doc, ve, walls.H).Count == 1 && PtMaterialTags(doc, ve, walls.V).Count == 1);

        // Coarse: no layers, nothing made.
        ViewPlan vc = PtPlan(doc, 100, "__pt material coarse");
        using (var t = new Transaction(doc, "pt coarse")) { t.Start(); vc.DetailLevel = ViewDetailLevel.Coarse; t.Commit(); }
        var coarse = Wmt(doc, vc, rule);
        Check("a Coarse view tags no material, and says why",
            PtMaterialTags(doc, vc, walls.H).Count == 0 && coarse.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]"))
                .All(r => r["Status"] == "Skipped" && r["Note"].Contains("Coarse")),
            Rows(coarse.Take(3).ToList()));

        // A wall rule (not material) on the same walls is not blocked by the material tags.
        string wallTag = PtFirstType(doc, BuiltInCategory.OST_Walls);
        if (wallTag.Length > 0)
        {
            var both = PresetTagger.Run(doc, new List<View> { v }, PtPreset(doc, new TagRule { Category = "OST_Walls", TagType = wallTag, TypeMarkContains = PtWallMark }));
            Check("a wall tag rule still tags walls that carry material tags",
                both.Any(r => r["Element"].Contains($"[{walls.H.Id.Value}]") && r["Status"] is "Created" or "Warning"),
                Rows(both.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]")).ToList()));
        }

        // A finish material with neither a Description nor a Mark: the tag would read "?" even once touched.
        using (var t = new Transaction(doc, "pt blank material"))
        {
            t.Start();
            doc.GetElement(walls.Int).get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.Set("");
            doc.GetElement(walls.Int).get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("");
            t.Commit();
        }
        ViewPlan vb = PtPlan(doc, 100, "__pt material blank");
        using (var t = new Transaction(doc, "pt medium")) { t.Start(); vb.DetailLevel = ViewDetailLevel.Medium; t.Commit(); }
        var blank = Wmt(doc, vb, rule);
        var blankRows = blank.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]")).ToList();
        Check("a finish material with no Description and no Mark is a Warning that names it",
            blankRows.Any(r => r["Element"].EndsWith("interior face") && r["Status"] == "Warning" && r["Note"].Contains("__pt interior finish")) &&
            blankRows.Any(r => r["Element"].EndsWith("exterior face") && r["Status"] == "Created"), Rows(blankRows));
    }

    /// <summary>Repair moves a material tag's point back into its wall when it is off the wall or on
    /// the line between two layers (the reasons a tag reads "?"); a tag inside a layer is left exactly
    /// where it is. (That a hand-placed tag then reads again was checked by exporting the view.)</summary>
    private static void PtRepair(Document doc)
    {
        List<string> types = PtMakeMaterialTagFamily(doc);
        if (types.Count != 4) { Check("a material tag type to test with", false); return; }
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);
        ViewPlan v = PtPlan(doc, 100, "__pt repair");
        using (var t = new Transaction(doc, "pt medium")) { t.Start(); v.DetailLevel = ViewDetailLevel.Medium; t.Commit(); }
        Wmt(doc, v, PtWmt(types));
        List<IndependentTag> h = PtMaterialTags(doc, v, walls.H), vv = PtMaterialTags(doc, v, walls.V);
        Check("four material tags to repair", h.Count == 2 && vv.Count == 2);
        if (h.Count != 2 || vv.Count != 2) return;
        double Off(IndependentTag x, Wall w) =>
            (x.TagHeadPosition - ((LocationCurve)w.Location).Curve.GetEndPoint(0)).DotProduct(w.Orientation);
        IndependentTag offWall = h.First(x => Off(x, walls.H) > 0);           // pushed 1 m off the exterior
        IndependentTag onLine = h.First(x => x.Id != offWall.Id);              // put on the line between interior finish and core
        IndependentTag fine = vv.First();                                      // left alone
        XYZ fineBefore = fine.TagHeadPosition;
        double coreEdge = -(walls.ExtMid + DrwUnits.FromMillimeters(PresetTagger.FacePointMm)) + DrwUnits.FromMillimeters(235 - 200 - 15);  // interior face + 20 mm
        using (var t = new Transaction(doc, "pt break"))
        {
            t.Start();
            offWall.TagHeadPosition = offWall.TagHeadPosition + walls.H.Orientation * DrwUnits.FromMillimeters(1000);
            onLine.TagHeadPosition = onLine.TagHeadPosition + walls.H.Orientation * (coreEdge - Off(onLine, walls.H));
            t.Commit();
        }
        Info($"off the wall: {Off(offWall, walls.H) / mm:0} mm; on the line: {Off(onLine, walls.H) / mm:0.0} mm (interior finish/core line at {coreEdge / mm:0.0})");

        WallMatTagPreset preset = PtWmt(new List<string> { "", "", "", "" });   // repair only
        preset.RepairMaterialTags = true;
        preset.DryRun = true;
        var dry = Wmt(doc, v, preset);
        Check("a dry run reports both repairs and moves nothing",
            dry.Count(r => r["Status"] == "Would repair") == 2 && Off(offWall, walls.H) > 900 * mm, Rows(dry));
        preset.DryRun = false;
        var rows = Wmt(doc, v, preset);
        foreach (var r in rows) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        Check("two tags Repaired, each saying why", rows.Count(r => r["Status"] == "Repaired") == 2 &&
            rows.Any(r => r["Note"].Contains("outside the exterior face")) && rows.Any(r => r["Note"].Contains("between two layers")), Rows(rows));
        Check("the off-wall tag's point is back in the exterior finish layer", Math.Abs(Off(offWall, walls.H) - walls.ExtMid) < 0.5 * mm,
            $"{Off(offWall, walls.H) / mm:0.0} mm");
        Check("the on-the-line tag's point is 5 mm in from the interior face", Math.Abs(Off(onLine, walls.H) - walls.IntMid) < 0.5 * mm,
            $"{Off(onLine, walls.H) / mm:0.0} mm, want {walls.IntMid / mm:0.0}");
        Check("a tag inside its layer is not moved", fine.TagHeadPosition.IsAlmostEqualTo(fineBefore));
        var again = Wmt(doc, v, preset);
        Check("repairing again changes nothing", again.All(r => r["Status"] != "Repaired"), Rows(again));
    }

    /// <summary>A note on the wall's middle: both faces move on together to the next spot, still lined
    /// up. A door in the middle: per piece, each piece gets its own pair, none in the opening; a piece
    /// shorter than the minimum is skipped; one per wall puts one pair outside the opening.</summary>
    private static void PtMaterialPieces(Document doc)
    {
        List<string> types = PtMakeMaterialTagFamily(doc);
        if (types.Count != 4) { Check("a material tag type to test with", false); return; }
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);
        Line hl = (Line)((LocationCurve)walls.H.Location).Curve;
        double Along(IndependentTag x) => (x.TagHeadPosition - hl.GetEndPoint(0)).DotProduct(hl.Direction);
        ViewPlan Medium(string name)
        {
            ViewPlan pv = PtPlan(doc, 100, name);
            using var t = new Transaction(doc, "pt medium");
            t.Start();
            pv.DetailLevel = ViewDetailLevel.Medium;
            t.Commit();
            return pv;
        }

        // A note over the exterior spot at the wall's middle.
        ViewPlan vn = Medium("__pt material note");
        ElementId textType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId();
        using (var t = new Transaction(doc, "pt note"))
        {
            t.Start();
            TextNote.Create(doc, vn.Id, hl.Evaluate(0.5, true) + walls.H.Orientation * walls.ExtMid, "XXXX",
                new TextNoteOptions(textType) { HorizontalAlignment = HorizontalTextAlignment.Center });
            t.Commit();
        }
        var noted = Wmt(doc, vn, PtWmt(types));
        List<IndependentTag> pair = PtMaterialTags(doc, vn, walls.H);
        var rowsH = noted.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]")).ToList();
        foreach (var r in rowsH) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        Check("a blocked spot moves both faces on together, still lined up",
            pair.Count == 2 && Math.Abs(Along(pair[0]) - Along(pair[1])) < mm && Math.Abs(Along(pair[0]) - hl.Length / 2) > 100 * mm &&
            rowsH.All(r => r["Note"].Contains("with the other face's tag")),
            string.Join(" / ", pair.Select(x => $"{Along(x) / mm:0} mm")) + " | " + Rows(rowsH));

        // A door at a third of the X wall, so its two pieces differ in length.
        FamilySymbol? doorType = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Doors)
            .Cast<FamilySymbol>().FirstOrDefault();
        if (doorType == null) { Info("no door type in the model: pieces are checked by hand"); return; }
        FamilyInstance door;
        using (var t = new Transaction(doc, "pt door"))
        {
            t.Start();
            if (!doorType.IsActive) doorType.Activate();
            door = doc.Create.NewFamilyInstance(hl.Evaluate(0.35, true), doorType, walls.H, (Level)doc.GetElement(walls.H.LevelId),
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            t.Commit();
        }
        BoundingBoxXYZ db = door.get_BoundingBox(null);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (XYZ c in new[] { db.Min, db.Max, new XYZ(db.Min.X, db.Max.Y, 0), new XYZ(db.Max.X, db.Min.Y, 0) })
        {
            double a = (c - hl.GetEndPoint(0)).DotProduct(hl.Direction);
            lo = Math.Min(lo, a); hi = Math.Max(hi, a);
        }
        Info($"door from {lo / mm:0} to {hi / mm:0} mm along a {hl.Length / mm:0} mm wall");

        WallMatTagPreset per = PtWmt(types);
        per.PerPiece = true;
        ViewPlan vp = Medium("__pt material pieces");
        var pieceRows = Wmt(doc, vp, per).Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]")).ToList();
        foreach (var r in pieceRows) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        List<IndependentTag> pieceTags = PtMaterialTags(doc, vp, walls.H);
        Check("per piece: two pieces × two faces = four tags, none in the door", pieceTags.Count == 4 &&
            pieceTags.All(x => Along(x) < lo || Along(x) > hi), string.Join(" / ", pieceTags.Select(x => $"{Along(x) / mm:0}")));
        Check("…each piece's pair lines up", pieceTags.GroupBy(x => Along(x) < lo).All(g => g.Count() == 2 &&
            Math.Abs(Along(g.First()) - Along(g.Last())) < mm));
        Check("…the rows name the piece", pieceRows.Count(r => r["Element"].Contains("piece 1 of 2")) == 2 &&
            pieceRows.Count(r => r["Element"].Contains("piece 2 of 2")) == 2, Rows(pieceRows));
        var pieceAgain = Wmt(doc, vp, per);
        Check("per piece: a rerun adds nothing", PtMaterialTags(doc, vp, walls.H).Count == 4 &&
            pieceAgain.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]")).All(r => r["Status"] == "Skipped"));

        // A minimum longer than the shorter piece: that piece is left out.
        WallMatTagPreset longOnly = PtWmt(types);
        longOnly.PerPiece = true;
        longOnly.MinPieceMm = (lo + (hl.Length - hi)) / 2 / mm;   // between the two pieces' lengths
        ViewPlan vl = Medium("__pt material long pieces");
        var longRows = Wmt(doc, vl, longOnly).Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]")).ToList();
        List<IndependentTag> longTags = PtMaterialTags(doc, vl, walls.H);
        Check($"a piece shorter than the minimum ({longOnly.MinPieceMm:0} mm) is left out: one pair, on the longer piece",
            longTags.Count == 2 && longTags.All(x => Along(x) > hi), $"{longTags.Count} tags | " + Rows(longRows));

        // One pair per wall: outside the opening.
        ViewPlan vw = Medium("__pt material whole");
        Wmt(doc, vw, PtWmt(types));
        List<IndependentTag> whole = PtMaterialTags(doc, vw, walls.H);
        Check("one per wall: one pair, outside the door", whole.Count == 2 && whole.All(x => Along(x) < lo || Along(x) > hi),
            string.Join(" / ", whole.Select(x => $"{Along(x) / mm:0}")));
    }

    /// <summary>Only seen faces. A finish drawn as its own wall against the X wall: the faces where
    /// the two touch are not tagged, flush or 5 mm apart; at 30 mm apart (or with the option off)
    /// they are. Walls butting into the X wall's interior cut that face into pieces, the exterior
    /// stays one piece, and no tag lands on a junction.</summary>
    private static void PtSeenFaces(Document doc)
    {
        List<string> types = PtMakeMaterialTagFamily(doc);
        if (types.Count != 4) { Check("a material tag type to test with", false); return; }
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);
        Line hl = (Line)((LocationCurve)walls.H.Location).Curve;
        XYZ n = walls.H.Orientation;
        double Along(IndependentTag x) => (x.TagHeadPosition - hl.GetEndPoint(0)).DotProduct(hl.Direction);
        double Off(IndependentTag x) => (x.TagHeadPosition - hl.GetEndPoint(0)).DotProduct(n);
        string Where(IEnumerable<IndependentTag> tags) => string.Join(" / ", tags.Select(x => $"{Along(x) / mm:0} along, {Off(x) / mm:0.0} out"));

        // The finish wall: one 15 mm layer of its own material, flush on the X wall's exterior.
        Wall plaster;
        using (var t = new Transaction(doc, "pt plaster wall"))
        {
            t.Start();
            var type = (WallType)walls.H.WallType.Duplicate("__pt plaster wall");   // keeps the test Type Mark
            ElementId m = Material.Create(doc, "__pt plaster");
            doc.GetElement(m).get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("P1");
            doc.GetElement(m).get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.Set("Plaster wall");
            CompoundStructure cs = CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure,
                DrwUnits.FromMillimeters(15), m);
            type.SetCompoundStructure(cs);
            XYZ shift = n * DrwUnits.FromMillimeters(235 / 2.0 + 7.5);
            plaster = Wall.Create(doc, Line.CreateBound(hl.GetEndPoint(0) + shift, hl.GetEndPoint(1) + shift), type.Id, walls.H.LevelId, 10, 0, false, false);
            plaster.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)WallLocationLine.WallCenterline);
            t.Commit();
        }
        Check("the finish wall faces the same way as the X wall", plaster.Orientation.IsAlmostEqualTo(n));
        string hId = $"[{walls.H.Id.Value}]", pId = $"[{plaster.Id.Value}]";

        int run = 0;
        (List<IReadOnlyDictionary<string, string>> Rows, List<IndependentTag> H, List<IndependentTag> P) RunIn(WallMatTagPreset p)
        {
            ViewPlan v = PtPlan(doc, 100, $"__pt seen {++run}");
            using (var t = new Transaction(doc, "pt medium")) { t.Start(); v.DetailLevel = ViewDetailLevel.Medium; t.Commit(); }
            var rows = Wmt(doc, v, p).Where(r => r["Element"].Contains(hId) || r["Element"].Contains(pId)).ToList();
            foreach (var r in rows) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
            return (rows, PtMaterialTags(doc, v, walls.H), plaster.IsValidObject ? PtMaterialTags(doc, v, plaster) : new List<IndependentTag>());
        }
        double outer = DrwUnits.FromMillimeters(235 / 2.0 + 15 - PresetTagger.FacePointMm);
        void Covered(string label, (List<IReadOnlyDictionary<string, string>> Rows, List<IndependentTag> H, List<IndependentTag> P) res, double gapMm)
        {
            bool Hidden(IReadOnlyDictionary<string, string> r) => r["Status"] == "Skipped" && r["Note"].StartsWith("Not a seen face");
            Check($"{label}: the X wall's exterior face and the finish wall's back are not tagged, each saying so",
                res.Rows.Count(Hidden) == 2 &&
                res.Rows.Any(r => Hidden(r) && r["Element"].Contains(hId) && r["Element"].Contains("exterior")) &&
                res.Rows.Any(r => Hidden(r) && r["Element"].Contains(pId) && r["Element"].Contains("interior")), Rows(res.Rows));
            Check($"{label}: the X wall is tagged on its interior face only", res.H.Count == 1 && Off(res.H[0]) < 0, Where(res.H));
            Check($"{label}: the finish wall is tagged on its outer face, 5 mm in", res.P.Count == 1 &&
                Math.Abs(Off(res.P[0]) - outer - gapMm * mm) < 0.5 * mm, Where(res.P) + $", want {(outer / mm + gapMm):0.0} out");
            Check($"{label}: the two tags back to back across the walls do not push each other along",
                res.H.Count == 1 && res.P.Count == 1 && Math.Abs(Along(res.H[0]) - hl.Length / 2) < mm && Math.Abs(Along(res.P[0]) - hl.Length / 2) < mm,
                Where(res.H.Concat(res.P)));
        }

        Covered("flush", RunIn(PtWmt(types)), 0);
        WallMatTagPreset all = PtWmt(types);
        all.SeenFacesOnly = false;
        var off = RunIn(all);
        Check("with 'only seen faces' off, every face is tagged as before", off.H.Count == 2 && off.P.Count == 2, Where(off.H.Concat(off.P)));

        using (var t = new Transaction(doc, "pt gap 5")) { t.Start(); ElementTransformUtils.MoveElement(doc, plaster.Id, n * 5 * mm); t.Commit(); }
        Covered("5 mm apart", RunIn(PtWmt(types)), 5);
        using (var t = new Transaction(doc, "pt gap 30")) { t.Start(); ElementTransformUtils.MoveElement(doc, plaster.Id, n * 25 * mm); t.Commit(); }
        var apart = RunIn(PtWmt(types));
        Check("30 mm apart: both walls are tagged on both faces", apart.H.Count == 2 && apart.P.Count == 2, Where(apart.H.Concat(apart.P)));

        // Flush again, joined, with a door in the X wall's middle that cuts the finish wall too (it
        // is not the finish wall's insert): the X wall's exterior is still not a seen face (finish,
        // door, finish), and the finish wall's tag keeps out of the door.
        FamilySymbol? doorType = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Doors)
            .Cast<FamilySymbol>().FirstOrDefault();
        if (doorType != null)
        {
            double before = plaster.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ?? 0;
            FamilyInstance door;
            using (var t = new Transaction(doc, "pt door through"))
            {
                t.Start();
                ElementTransformUtils.MoveElement(doc, plaster.Id, -n * 30 * mm);
                if (!JoinGeometryUtils.AreElementsJoined(doc, walls.H, plaster)) JoinGeometryUtils.JoinGeometry(doc, walls.H, plaster);
                if (!doorType.IsActive) doorType.Activate();
                door = doc.Create.NewFamilyInstance(hl.Evaluate(0.5, true), doorType, walls.H, (Level)doc.GetElement(walls.H.LevelId),
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                t.Commit();
            }
            double after = plaster.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ?? 0;
            BoundingBoxXYZ db = door.get_BoundingBox(null);
            double dlo = double.MaxValue, dhi = double.MinValue;
            foreach (XYZ c in new[] { db.Min, db.Max, new XYZ(db.Min.X, db.Max.Y, 0), new XYZ(db.Max.X, db.Min.Y, 0) })
            {
                double a = (c - hl.GetEndPoint(0)).DotProduct(hl.Direction);
                dlo = Math.Min(dlo, a); dhi = Math.Max(dhi, a);
            }
            Info($"finish wall volume {before:0.###} → {after:0.###} ft³ with the door ({(after < before - 1e-6 ? "the door cuts it" : "not cut")}), door {dlo / mm:0}–{dhi / mm:0} mm");
            var through = RunIn(PtWmt(types));
            Check("a door through the X wall and the finish wall: the X wall's exterior is still not a seen face",
                through.Rows.Any(r => r["Status"] == "Skipped" && r["Note"].StartsWith("Not a seen face") && r["Element"].Contains(hId) && r["Element"].Contains("exterior")) &&
                through.H.All(x => Off(x) < 0), Where(through.H) + " | " + Rows(through.Rows));
            Check("…the finish wall's tag keeps out of the door that cuts it",
                after >= before - 1e-6 || (through.P.Count == 1 && (Along(through.P[0]) < dlo || Along(through.P[0]) > dhi)), Where(through.P));
            using (var t = new Transaction(doc, "pt no door")) { t.Start(); doc.Delete(door.Id); t.Commit(); }
        }

        // Walls butting into the X wall's interior at a third and two thirds.
        WallType? plain = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
            .FirstOrDefault(w => w.Kind == WallKind.Basic && !(w.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString() ?? "").Contains(PtWallMark));
        if (plain == null) { Info("no plain wall type: junctions are checked by hand"); return; }
        double tw = plain.Width;
        var tees = new List<double>();
        void Tee(double f)
        {
            using var t = new Transaction(doc, "pt tee");
            t.Start();
            XYZ at = hl.Evaluate(f, true);
            Wall.Create(doc, Line.CreateBound(at, at - n * DrwUnits.FromMillimeters(2000)), plain.Id, walls.H.LevelId, 10, 0, false, false);
            t.Commit();
            tees.Add(f * hl.Length);
        }
        using (var t = new Transaction(doc, "pt no plaster")) { t.Start(); doc.Delete(plaster.Id); t.Commit(); }
        Tee(1 / 3.0);
        Tee(2 / 3.0);
        bool OnTee(IndependentTag x) => tees.Any(s => Math.Abs(Along(x) - s) < tw / 2 + 10 * mm);
        Info($"junctions at {string.Join(", ", tees.Select(s => $"{s / mm:0}"))} mm, {tw / mm:0} mm thick, on a {hl.Length / mm:0} mm wall");

        WallMatTagPreset per = PtWmt(types);
        per.PerPiece = true;
        var pieces = RunIn(per);
        List<IndependentTag> inner = pieces.H.Where(x => Off(x) < 0).ToList(), outerTags = pieces.H.Where(x => Off(x) > 0).ToList();
        Check("per piece: the interior face is cut by the two walls into 3 pieces, the exterior stays 1",
            inner.Count == 3 && outerTags.Count == 1 &&
            pieces.Rows.Count(r => r["Element"].Contains("interior face, piece") && r["Element"].Contains("of 3")) == 3, Where(pieces.H) + " | " + Rows(pieces.Rows));
        Check("…no interior tag sits on a junction", !inner.Any(OnTee), Where(inner));
        Check("…the exterior lines up with the middle room's tag, so keeps the wall's middle",
            outerTags.Count == 1 && Math.Abs(Along(outerTags[0]) - hl.Length / 2) < mm, Where(outerTags));
        var again = RunIn(per);   // a new view: tags again; the same view again adds nothing
        Check("per piece in another view: the same 4 tags", again.H.Count == 4, Where(again.H));

        var whole = RunIn(PtWmt(types));
        Check("one per wall: a lined-up pair, clear of the junctions", whole.H.Count == 2 && !whole.H.Any(OnTee) &&
            Math.Abs(Along(whole.H[0]) - Along(whole.H[1])) < mm, Where(whole.H));
        Tee(0.5);
        var middle = RunIn(PtWmt(types));
        Check("a wall meeting the middle: the pair moves on together, clear of every junction",
            middle.H.Count == 2 && !middle.H.Any(OnTee) && Math.Abs(Along(middle.H[0]) - Along(middle.H[1])) < mm &&
            Math.Abs(Along(middle.H[0]) - hl.Length / 2) > 100 * mm, Where(middle.H));
    }

    /// <summary>Tags line up across a wall without adding any: an exterior facing a longer and a
    /// shorter interior piece lines up with the longer; across a finish drawn as its own wall too;
    /// two walls facing each other across a room are not paired.</summary>
    private static void PtAlign(Document doc)
    {
        List<string> types = PtMakeMaterialTagFamily(doc);
        if (types.Count != 4) { Check("a material tag type to test with", false); return; }
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);
        Line hl = (Line)((LocationCurve)walls.H.Location).Curve;
        XYZ n = walls.H.Orientation;
        double Along(IndependentTag x) => (x.TagHeadPosition - hl.GetEndPoint(0)).DotProduct(hl.Direction);
        double Off(IndependentTag x) => (x.TagHeadPosition - hl.GetEndPoint(0)).DotProduct(n);
        string Where(IEnumerable<IndependentTag> tags) => string.Join(" / ", tags.Select(x => $"{Along(x) / mm:0} along, {Off(x) / mm:0.0} out"));
        int run = 0;
        ViewPlan Medium()
        {
            ViewPlan v = PtPlan(doc, 100, $"__pt align {++run}");
            using var t = new Transaction(doc, "pt medium");
            t.Start();
            v.DetailLevel = ViewDetailLevel.Medium;
            t.Commit();
            return v;
        }
        void Show(List<IReadOnlyDictionary<string, string>> rows)
        {
            foreach (var r in rows) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        }

        WallType? plain = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
            .FirstOrDefault(w => w.Kind == WallKind.Basic && !(w.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString() ?? "").Contains(PtWallMark));
        if (plain == null) { Info("no plain wall type: lining up is checked by hand"); return; }
        double teeAt = 0.3 * hl.Length;
        using (var t = new Transaction(doc, "pt tee"))
        {
            t.Start();
            XYZ at = hl.Evaluate(0.3, true);
            Wall.Create(doc, Line.CreateBound(at, at - n * DrwUnits.FromMillimeters(2000)), plain.Id, walls.H.LevelId, 10, 0, false, false);
            t.Commit();
        }
        WallMatTagPreset per = PtWmt(types);
        per.PerPiece = true;

        // One wall: the exterior (one piece) faces a short and a long interior piece.
        ViewPlan v1 = Medium();
        var rows1 = Wmt(doc, v1, per).Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]")).ToList();
        Show(rows1);
        List<IndependentTag> h1 = PtMaterialTags(doc, v1, walls.H);
        List<IndependentTag> ext1 = h1.Where(x => Off(x) > 0).ToList(), long1 = h1.Where(x => Off(x) < 0 && Along(x) > teeAt).ToList();
        Check("the exterior lines up with the interior piece it shares the most wall with (overlap 70% of the exterior)",
            h1.Count == 3 && ext1.Count == 1 && long1.Count == 1 && Math.Abs(Along(ext1[0]) - Along(long1[0])) < mm &&
            Math.Abs(Along(ext1[0]) - hl.Length / 2) > 100 * mm, Where(h1));
        Check("…no tag added: exterior 1, interior 2", h1.Count(x => Off(x) < 0) == 2 && ext1.Count == 1, Where(h1));

        // A finish drawn as its own wall on the exterior: its tag lines up with the X wall's interior.
        Wall plaster;
        using (var t = new Transaction(doc, "pt plaster wall"))
        {
            t.Start();
            var type = (WallType)walls.H.WallType.Duplicate("__pt plaster wall");
            ElementId m = Material.Create(doc, "__pt plaster");
            doc.GetElement(m).get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("P1");
            type.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure,
                DrwUnits.FromMillimeters(15), m));
            XYZ shift = n * DrwUnits.FromMillimeters(235 / 2.0 + 7.5);
            plaster = Wall.Create(doc, Line.CreateBound(hl.GetEndPoint(0) + shift, hl.GetEndPoint(1) + shift), type.Id, walls.H.LevelId, 10, 0, false, false);
            plaster.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)WallLocationLine.WallCenterline);
            t.Commit();
        }
        ViewPlan v2 = Medium();
        Show(Wmt(doc, v2, per).Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]") || r["Element"].Contains($"[{plaster.Id.Value}]")).ToList());
        List<IndependentTag> h2 = PtMaterialTags(doc, v2, walls.H), p2 = PtMaterialTags(doc, v2, plaster);
        List<IndependentTag> long2 = h2.Where(x => Along(x) > teeAt).ToList();
        Check("across a finish wall: its tag lines up with the X wall's interior tag on the longer piece",
            p2.Count == 1 && long2.Count == 1 && Math.Abs(Along(p2[0]) - Along(long2[0])) < mm && Math.Abs(Along(p2[0]) - hl.Length / 2) > 100 * mm,
            Where(p2) + " | " + Where(h2));
        Check("…no tag added: finish wall 1, X wall 2 (interior only)", p2.Count == 1 && h2.Count == 2 && h2.All(x => Off(x) < 0), Where(h2.Concat(p2)));

        // Two walls facing each other across a room, offset along: never paired.
        Line vl = (Line)((LocationCurve)walls.V.Location).Curve;
        XYZ vo = walls.V.Orientation;
        Wall across;
        using (var t = new Transaction(doc, "pt across"))
        {
            t.Start();
            XYZ shift = vo * DrwUnits.FromMillimeters(1500) + vl.Direction * DrwUnits.FromMillimeters(2000);
            across = Wall.Create(doc, Line.CreateBound(vl.GetEndPoint(0) + shift, vl.GetEndPoint(1) + shift), walls.V.WallType.Id, walls.V.LevelId, 10, 0, false, false);
            across.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)WallLocationLine.WallCenterline);
            t.Commit();
        }
        ViewPlan v3 = Medium();
        Wmt(doc, v3, PtWmt(types));
        double VAlong(IndependentTag x) => (x.TagHeadPosition - vl.GetEndPoint(0)).DotProduct(vl.Direction);
        List<IndependentTag> vt = PtMaterialTags(doc, v3, walls.V), at3 = PtMaterialTags(doc, v3, across);
        Check("two walls facing each other across a room are not lined up: each keeps its own middle",
            vt.Count == 2 && at3.Count == 2 && vt.All(x => Math.Abs(VAlong(x) - vl.Length / 2) < mm) &&
            at3.All(x => Math.Abs(VAlong(x) - (vl.Length / 2 + 2000 * mm)) < mm),
            string.Join(" / ", vt.Concat(at3).Select(x => $"{VAlong(x) / mm:0}")));
    }

    /// <summary>The Wall Mat Tag family that comes with DRW Helper loads when the model lacks it and is
    /// the default; each of its types sits on its own side of the face; on a wall off the view's axes
    /// the tag turns so its triangle points straight at the face.</summary>
    private static void PtBundledAndTurn(Document doc)
    {
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);
        int run = 0;
        ViewPlan Medium()
        {
            ViewPlan v = PtPlan(doc, 100, $"__pt turn {++run}");
            using var t = new Transaction(doc, "pt medium");
            t.Start();
            v.DetailLevel = ViewDetailLevel.Medium;
            t.Commit();
            return v;
        }
        // Where a tag's box lies from its point, across the view, in degrees anticlockwise from right.
        double Toward(IndependentTag x, View v)
        {
            BoundingBoxXYZ? b = x.get_BoundingBox(v);
            if (b == null) return double.NaN;
            XYZ d = (b.Min + b.Max) / 2 - x.TagHeadPosition;
            return Math.Atan2(d.DotProduct(v.UpDirection), d.DotProduct(v.RightDirection)) * 180 / Math.PI;
        }
        double OutDeg(XYZ o, View v) => Math.Atan2(o.DotProduct(v.UpDirection), o.DotProduct(v.RightDirection)) * 180 / Math.PI;
        double Apart(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);
        string Show(IEnumerable<IndependentTag> tags, View v) => string.Join(" / ", tags.Select(x =>
            $"{(doc.GetElement(x.GetTypeId()) as ElementType)?.Name} box at {Toward(x, v):0}°, turned {x.RotationAngle * 180 / Math.PI:0.0}°"));

        Check("the turn from a tag's side to the face: 30° off up is +30°, upright is 0, a hair off is 0",
            Math.Abs(PresetTagger.Turn(TagPosition.Above, Math.Cos(2.0944), Math.Sin(2.0944)) - 0.5236) < 1e-3 &&
            PresetTagger.Turn(TagPosition.Right, 1, 0) == 0 && PresetTagger.Turn(TagPosition.Below, 0.001, -1) == 0 &&
            Math.Abs(PresetTagger.Turn(TagPosition.Left, -1, -0.5) - Math.Atan(0.5)) < 1e-6);

        // A wall turned 30°: each face's tag turns with it (the test family's triangle: box straight out from the face).
        Wall turned;
        using (var t = new Transaction(doc, "pt turned wall"))
        {
            t.Start();
            XYZ o = ((LocationCurve)walls.H.Location).Curve.GetEndPoint(0) + new XYZ(0, -40, 0);
            XYZ dir = new XYZ(Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6), 0);
            turned = Wall.Create(doc, Line.CreateBound(o, o + dir * 20), walls.H.WallType.Id, walls.H.LevelId, 10, 0, false, false);
            turned.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)WallLocationLine.WallCenterline);
            t.Commit();
        }
        List<string> types = PtMakeMaterialTagFamily(doc);
        if (types.Count == 4)
        {
            ViewPlan v1 = Medium();
            Wmt(doc, v1, PtWmt(types));
            List<IndependentTag> t1 = PtMaterialTags(doc, v1, turned);
            double outDeg = OutDeg(turned.Orientation, v1);
            // Above (out 120°) and below (out -60°) both turn +30° anticlockwise; the bundled family's
            // boxes below show the turn goes the right way.
            Check("a wall turned 30°: both tags turned +30° with it",
                t1.Count == 2 && t1.All(x => Math.Abs(x.RotationAngle * 180 / Math.PI - 30) < 0.5), $"out {outDeg:0}° — " + Show(t1, v1));
            List<IndependentTag> h1 = PtMaterialTags(doc, v1, walls.H);
            Check("…a wall along the view's axes keeps its tags upright", h1.Count == 2 && h1.All(x => Math.Abs(x.RotationAngle) < 1e-9), Show(h1, v1));
        }
        else Check("a material tag type to test turning with", false);

        if (!BundledFamilies.Has(BundledFamilies.WallMatTag))
        {
            Info($"Revit {doc.Application.VersionNumber}: no bundled Wall Mat Tag family this year; the default types are blank");
            Check("…so a new preset names no type", new WallMatTagPreset().TypeAbove.Length == 0);
            return;
        }
        WallMatTagPreset p = new() { TypeMarkContains = PtWallMark, RepairMaterialTags = false, Scope = DrwViewScope.AllPlans, ViewNameContains = "__pt turn" };
        Check("a new preset uses the bundled family, one type per side",
            p.TypeAbove == BundledFamilies.WallMatTag + " : Down" && p.TypeBelow == BundledFamilies.WallMatTag + " : Up" &&
            p.TypeLeft == BundledFamilies.WallMatTag + " : Right" && p.TypeRight == BundledFamilies.WallMatTag + " : Left" &&
            p.TypeElevation == BundledFamilies.WallMatTag + " : Up" && !p.Elevations, p.TypeAbove);
        Family? had = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == BundledFamilies.WallMatTag);
        if (had != null)
            using (var t = new Transaction(doc, "pt unload"))
            {
                t.Start();
                doc.Delete(had.Id);
                t.Commit();
            }

        ViewPlan dry = Medium();
        p.DryRun = true;
        var dryRows = WallMatTag.Run(doc, new List<View> { dry }, p);
        Check("a dry run reports the family it would load, and leaves it out of the model",
            dryRows.Any(r => r["Status"] == "Would load") &&
            !new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Any(f => f.Name == BundledFamilies.WallMatTag),
            string.Join(" | ", dryRows.Take(3).Select(r => r["Status"] + " " + r["Element"])));

        p.DryRun = false;
        ViewPlan v2 = Medium();
        var rows = WallMatTag.Run(doc, new List<View> { v2 }, p);
        Check("the model lacks the family: Wall Mat Tag loads it, says so, and tags with it",
            rows.Count(r => r["Status"] == "Loaded") == 1 && rows.Any(r => r["Status"] == "Created" && r["Tag type"].Contains(BundledFamilies.WallMatTag)),
            string.Join(" | ", rows.Take(4).Select(r => r["Status"] + " " + r["Element"] + " " + r["Tag type"])));
        // Each type sits on its own side: its box lies out from its own face, off the wall.
        // Revit 2024 boxes a tag with the family's HIDDEN lines and labels too (probed 2026-10-22: every
        // type reads the same 8 x 8 mm box round its head, all four triangles), though it draws only its
        // own; there the box can only be checked to be that union, centred on the head.
        bool union = doc.Application.VersionNumber == "2024";
        foreach (Wall w in new[] { walls.H, walls.V, turned })
        {
            List<IndependentTag> tags = PtMaterialTags(doc, v2, w);
            XYZ mid = ((LocationCurve)w.Location).Curve.Evaluate(0.5, true);
            double FaceOut(IndependentTag x) => OutDeg((x.TagHeadPosition - mid).DotProduct(w.Orientation) > 0 ? w.Orientation : -w.Orientation, v2);
            bool Centred(IndependentTag x) => x.get_BoundingBox(v2) is BoundingBoxXYZ b &&
                ((b.Min + b.Max) / 2 - x.TagHeadPosition).GetLength() * 304.8 / v2.Scale < 0.5;
            string wall = w == turned ? "the 30° wall" : w == walls.H ? "the horizontal wall" : "the vertical wall";
            Info($"  wall {w.Id.Value} out {OutDeg(w.Orientation, v2):0}°: {Show(tags, v2)}");
            if (union)
                Check($"…{wall}: each bundled tag's box is all four directions round its head (Revit 2024)",
                    tags.Count == 2 && tags.All(Centred), Show(tags, v2));
            else
                Check($"…{wall}: each bundled tag's box lies out from its own face",
                    tags.Count == 2 && tags.All(x => Apart(Toward(x, v2), FaceOut(x)) < 45), Show(tags, v2));
        }
        ViewPlan v3 = Medium();
        var again = WallMatTag.Run(doc, new List<View> { v3 }, p);
        Check("…a second run finds it loaded and loads nothing", !again.Any(r => r["Status"] == "Loaded"));
    }

    /// <summary>A plan tags only the walls its cut plane passes through: a bulkhead above it and a low
    /// wall below it are left alone (on Snowdon AR they gave floating tags).</summary>
    private static void PtCutOnly(Document doc)
    {
        List<string> types = PtMakeMaterialTagFamily(doc);
        if (types.Count != 4) { Check("a material tag type to test with", false); return; }
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);
        Line hl = (Line)((LocationCurve)walls.H.Location).Curve;
        Wall high, low;
        using (var t = new Transaction(doc, "pt bulkhead and low wall"))
        {
            t.Start();
            XYZ s1 = walls.H.Orientation * 6;
            high = Wall.Create(doc, Line.CreateBound(hl.GetEndPoint(0) + s1, hl.GetEndPoint(1) + s1), walls.H.WallType.Id, walls.H.LevelId,
                DrwUnits.FromMillimeters(600), DrwUnits.FromMillimeters(2400), false, false);
            XYZ s2 = walls.H.Orientation * 12;
            low = Wall.Create(doc, Line.CreateBound(hl.GetEndPoint(0) + s2, hl.GetEndPoint(1) + s2), walls.H.WallType.Id, walls.H.LevelId,
                DrwUnits.FromMillimeters(900), 0, false, false);
            t.Commit();
        }
        ViewPlan v = PtPlan(doc, 100, "__pt cut only");
        using (var t = new Transaction(doc, "pt medium"))
        {
            t.Start();
            v.DetailLevel = ViewDetailLevel.Medium;
            t.Commit();
        }
        var rows = Wmt(doc, v, PtWmt(types));
        foreach (var r in rows.Where(r => r["Element"].Contains($"[{high.Id.Value}]") || r["Element"].Contains($"[{low.Id.Value}]")))
            Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        bool NotCut(Wall w) => PtMaterialTags(doc, v, w).Count == 0 &&
            rows.Where(r => r["Element"].Contains($"[{w.Id.Value}]")).All(r => r["Status"] == "Skipped" && r["Note"].Contains("Not cut by this plan"));
        Check("a bulkhead above the cut plane gets no tag (said why, if the view lists it)", NotCut(high));
        Check("a 900 mm wall below the cut plane gets no tag, and says it is not cut", NotCut(low) &&
            rows.Any(r => r["Element"].Contains($"[{low.Id.Value}]") && r["Note"].Contains("Not cut by this plan")));
        Check("…the walls the plan cuts are still tagged on both faces",
            PtMaterialTags(doc, v, walls.H).Count == 2 && PtMaterialTags(doc, v, walls.V).Count == 2);
    }

    private static ViewSection PtSectionAcross(Document doc, XYZ at, XYZ look, double halfWidth, double height, double depth)
    {
        using var t = new Transaction(doc, "pt section");
        t.Start();
        ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .First(x => x.ViewFamily == ViewFamily.Section);
        Transform tr = Transform.Identity;
        tr.Origin = at;
        // Revit looks along the box's Z: the cut is its Min.Z face, the far clip its Max.Z.
        tr.BasisZ = look.Normalize();
        tr.BasisY = XYZ.BasisZ;
        tr.BasisX = tr.BasisY.CrossProduct(tr.BasisZ).Normalize();
        var box = new BoundingBoxXYZ { Transform = tr, Min = new XYZ(-halfWidth, -1, 0), Max = new XYZ(halfWidth, height, depth) };
        ViewSection s = ViewSection.CreateSection(doc, vft.Id, box);
        s.ViewTemplateId = ElementId.InvalidElementId;
        s.Name = "__pt section " + Guid.NewGuid().ToString("N").Substring(0, 6);
        s.Scale = 50;
        s.DetailLevel = ViewDetailLevel.Medium;
        t.Commit();
        return s;
    }

    /// <summary>Sections: tags on the cut, 5 mm in from each face, up the wall; a door at the cut is
    /// kept out; a wall the section does not cut is skipped; a floor against the face cuts it into
    /// storeys (per piece); a wall crossing at an angle is tagged on the plane too.</summary>
    private static void PtSection(Document doc)
    {
        List<string> types = PtMakeMaterialTagFamily(doc);
        if (types.Count != 4) { Check("a material tag type to test with", false); return; }
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);
        Line hl = (Line)((LocationCurve)walls.H.Location).Curve;
        XYZ n = walls.H.Orientation;
        double baseZ = hl.GetEndPoint(0).Z, top = baseZ + 10;
        XYZ mid = hl.Evaluate(0.5, true);
        // Looking along the wall, cutting it at its middle.
        ViewSection s1 = PtSectionAcross(doc, mid, hl.Direction, 6, 12, 15);
        XYZ cut1 = PresetTagger.CutPlane(s1);
        Info($"section cut {cut1.X:0.00},{cut1.Y:0.00} — wall middle {mid.X:0.00},{mid.Y:0.00}; looking {s1.ViewDirection.X:0.0},{s1.ViewDirection.Y:0.0}");
        Check("the cut plane of a section is read on its section line, not at its far clip",
            Math.Abs((cut1 - mid).DotProduct(s1.ViewDirection)) < 1e-6, $"{(cut1 - mid).DotProduct(s1.ViewDirection):0.000} ft off");
        double OnPlane(XYZ p) => Math.Abs((p - cut1).DotProduct(s1.ViewDirection)) / mm;
        double Out(XYZ p) => (p - mid).DotProduct(n) / mm;
        string Show(IEnumerable<IndependentTag> tags) => string.Join(" / ", tags.Select(x =>
            $"plane {OnPlane(x.TagHeadPosition):0.0} mm, out {Out(x.TagHeadPosition):0.0} mm, up {(x.TagHeadPosition.Z - baseZ) / mm:0} mm"));
        var rows = Wmt(doc, s1, PtWmt(types));
        foreach (var r in rows.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]"))) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        List<IndependentTag> h = PtMaterialTags(doc, s1, walls.H);
        double half = (walls.ExtMid > 0 ? walls.ExtMid : -walls.ExtMid) / mm;
        Check("a section across a wall: one tag per face, on the cut, 5 mm in from each face, at mid-height",
            h.Count == 2 && h.All(x => OnPlane(x.TagHeadPosition) < 1 && Math.Abs(Math.Abs(Out(x.TagHeadPosition)) - half) < 1 &&
                                      Math.Abs(x.TagHeadPosition.Z - (baseZ + top) / 2) < 1 * mm) &&
            h.Count(x => Out(x.TagHeadPosition) > 0) == 1, $"5 mm in = {half:0.0} out — " + Show(h));
        Check("…the plan's other wall, along the section's depth but not cut by it, gets no tag",
            PtMaterialTags(doc, s1, walls.V).Count == 0);

        // A wall beyond the section line, not reaching it; a door at the cut; a wall crossing at 60°.
        WallType? plain = walls.H.WallType;
        Wall beyond, angled;
        FamilySymbol? doorType = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Doors)
            .Cast<FamilySymbol>().FirstOrDefault();
        FamilyInstance? door = null;
        using (var t = new Transaction(doc, "pt section walls"))
        {
            t.Start();
            XYZ off = -n * 3;
            beyond = Wall.Create(doc, Line.CreateBound(hl.Evaluate(0.6, true) + off, hl.Evaluate(0.9, true) + off), plain.Id, walls.H.LevelId, 10, 0, false, false);
            XYZ c = mid - n * 6;
            XYZ d = (hl.Direction * Math.Cos(Math.PI / 3) + n * Math.Sin(Math.PI / 3)).Normalize();
            angled = Wall.Create(doc, Line.CreateBound(c - d * 1.5, c + d * 1.5), plain.Id, walls.H.LevelId, 10, 0, false, false);
            if (doorType != null)
            {
                if (!doorType.IsActive) doorType.Activate();
                door = doc.Create.NewFamilyInstance(mid, doorType, walls.H, (Level)doc.GetElement(walls.H.LevelId),
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            t.Commit();
        }
        ViewSection s2 = PtSectionAcross(doc, mid, hl.Direction, 9, 12, 15);
        // No elevation type: the section's cut rule only, so a wall it does not cut says so.
        WallMatTagPreset cutOnly = PtWmt(types);
        cutOnly.TypeElevation = "";
        var rows2 = Wmt(doc, s2, cutOnly);
        Check("a wall that ends before the section line is not tagged, and says so",
            PtMaterialTags(doc, s2, beyond).Count == 0 &&
            rows2.Any(r => r["Element"].Contains($"[{beyond.Id.Value}]") && r["Note"].Contains("Not cut by this section")),
            string.Join(" | ", rows2.Where(r => r["Element"].Contains($"[{beyond.Id.Value}]")).Select(r => r["Status"] + " " + r["Note"])));
        List<IndependentTag> ang = PtMaterialTags(doc, s2, angled);
        Check("a wall crossing the section at 60°: both tags on the cut plane",
            ang.Count == 2 && ang.All(x => Math.Abs((x.TagHeadPosition - PresetTagger.CutPlane(s2)).DotProduct(s2.ViewDirection)) < 1 * mm),
            string.Join(" / ", ang.Select(x => $"{Math.Abs((x.TagHeadPosition - PresetTagger.CutPlane(s2)).DotProduct(s2.ViewDirection)) / mm:0.0} mm off")));
        if (door != null)
        {
            double doorTop = door.get_BoundingBox(null).Max.Z;
            List<IndependentTag> hd = PtMaterialTags(doc, s2, walls.H);
            Check("a door at the cut: the wall's tags go above it",
                hd.Count == 2 && hd.All(x => x.TagHeadPosition.Z > doorTop + 40 * mm),
                $"door top {(doorTop - baseZ) / mm:0} mm — " + string.Join(" / ", hd.Select(x => $"up {(x.TagHeadPosition.Z - baseZ) / mm:0} mm")));
        }
        else Info("no door type: the door at the cut is checked by hand");

        // A wall seen beyond the cut, facing the section: tagged as an elevation sees it — one upright
        // tag of the elevation type; the walls the section cuts keep their cut tags.
        Wall seen;
        using (var t = new Transaction(doc, "pt wall beyond"))
        {
            t.Start();
            XYZ b0 = mid + hl.Direction * 6 - n * 1.5, b1 = mid + hl.Direction * 6 - n * 5;
            seen = Wall.Create(doc, Line.CreateBound(b0, b1), plain.Id, walls.H.LevelId, 10, 0, false, false);
            t.Commit();
        }
        ViewSection s4 = PtSectionAcross(doc, mid, hl.Direction, 9, 12, 15);
        WallMatTagPreset both = PtWmt(types);
        both.TypeElevation = types[1];
        var rows4 = Wmt(doc, s4, both);
        foreach (var r in rows4.Where(r => r["Element"].Contains($"[{seen.Id.Value}]"))) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        List<IndependentTag> sv = PtMaterialTags(doc, s4, seen);
        Check("a section: a wall seen beyond the cut gets one upright elevation tag on the face towards the viewer",
            sv.Count == 1 && (doc.GetElement(sv[0].GetTypeId()) as ElementType)?.Name == "Below" && Math.Abs(sv[0].RotationAngle) < 1e-9 &&
            !rows4.Any(r => r["Element"].Contains($"[{seen.Id.Value}]") && r["Note"].Contains("Not cut")),
            string.Join(" | ", rows4.Where(r => r["Element"].Contains($"[{seen.Id.Value}]")).Select(r => r["Status"] + " " + r["Note"])));
        Check("…the cut walls keep their cut tags (no elevation tag on them)",
            PtMaterialTags(doc, s4, walls.H).Count == 2 && PtMaterialTags(doc, s4, angled).Count == 2 &&
            PtMaterialTags(doc, s4, walls.H).All(x => (doc.GetElement(x.GetTypeId()) as ElementType)?.Name != "Below" || Math.Abs((x.TagHeadPosition - PresetTagger.CutPlane(s4)).DotProduct(s4.ViewDirection)) < 1 * mm));

        // A floor against the exterior face at 1500 mm: per piece, the exterior gets a tag per storey.
        FloorType? ft = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault(f => !f.IsFoundationSlab);
        if (ft == null) { Info("no floor type: storeys are checked by hand"); return; }
        Wall tall;
        using (var t = new Transaction(doc, "pt floor"))
        {
            t.Start();
            XYZ far = mid - n * 30;
            tall = Wall.Create(doc, Line.CreateBound(far - hl.Direction * 5, far + hl.Direction * 5), plain.Id, walls.H.LevelId, 10, 0, false, false);
            tall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)WallLocationLine.WallCenterline);
            doc.Regenerate();
            PlanarFace? ef = HostObjectUtils.GetSideFaces(tall, ShellLayerType.Exterior).Select(r => tall.GetGeometryObjectFromReference(r)).OfType<PlanarFace>().FirstOrDefault();
            XYZ fo = ef!.Origin, o = tall.Orientation;
            XYZ a = new XYZ(far.X, far.Y, baseZ) + o * ((fo - far).DotProduct(o)) - hl.Direction * 4;
            XYZ b = a + hl.Direction * 8, c = b + o * 6, d = a + o * 6;
            var loop = CurveLoop.Create(new List<Curve> { Line.CreateBound(a, b), Line.CreateBound(b, c), Line.CreateBound(c, d), Line.CreateBound(d, a) });
            Floor f = Floor.Create(doc, new List<CurveLoop> { loop }, ft.Id, walls.H.LevelId);
            f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(DrwUnits.FromMillimeters(1500));
            t.Commit();
        }
        ViewSection s3 = PtSectionAcross(doc, mid - n * 30, hl.Direction, 9, 12, 15);
        WallMatTagPreset per = PtWmt(types);
        per.PerPiece = true;
        var rows3 = Wmt(doc, s3, per);
        foreach (var r in rows3.Where(r => r["Element"].Contains($"[{tall.Id.Value}]"))) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        List<IndependentTag> st = PtMaterialTags(doc, s3, tall);
        double TallOut(XYZ p) => (p - (mid - n * 30)).DotProduct(tall.Orientation);
        Check("a floor against the face, per piece: the exterior gets a tag below the floor and one above, the interior one",
            st.Count(x => TallOut(x.TagHeadPosition) > 0) == 2 && st.Count(x => TallOut(x.TagHeadPosition) < 0) == 1 &&
            st.Where(x => TallOut(x.TagHeadPosition) > 0).Count(x => x.TagHeadPosition.Z < baseZ + 1300 * mm) == 1,
            string.Join(" / ", st.Select(x => $"out {TallOut(x.TagHeadPosition) / mm:0}, up {(x.TagHeadPosition.Z - baseZ) / mm:0}")));
    }

    /// <summary>A painted face: the plan's tag point sits just inside the paint (0.5 mm), so the tag
    /// reads it; the elevation tags the painted surface with its paint.</summary>
    private static void PtPainted(Document doc)
    {
        List<string> types = PtMakeMaterialTagFamily(doc);
        if (types.Count != 4) { Check("a material tag type to test with", false); return; }
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);
        Line hl = (Line)((LocationCurve)walls.H.Location).Curve;
        XYZ n = walls.H.Orientation, mid = hl.Evaluate(0.5, true);
        PlanarFace? ef = HostObjectUtils.GetSideFaces(walls.H, ShellLayerType.Exterior).Select(r => walls.H.GetGeometryObjectFromReference(r)).OfType<PlanarFace>().FirstOrDefault();
        if (ef == null) { Check("the exterior face to paint", false); return; }
        using (var t = new Transaction(doc, "pt paint"))
        {
            t.Start();
            ElementId m = Material.Create(doc, "__pt paint");
            doc.Paint(walls.H.Id, ef, m);
            t.Commit();
        }
        ViewPlan v = PtPlan(doc, 100, "__pt painted");
        using (var t = new Transaction(doc, "pt medium"))
        {
            t.Start();
            v.DetailLevel = ViewDetailLevel.Medium;
            t.Commit();
        }
        Wmt(doc, v, PtWmt(types));
        List<IndependentTag> tags = PtMaterialTags(doc, v, walls.H);
        double faceOut = (ef.Origin - mid).DotProduct(n);
        Check("a painted face: its tag point sits 0.5 mm inside the paint, the other face's 5 mm in",
            tags.Count == 2 && tags.Any(x => Math.Abs((x.TagHeadPosition - mid).DotProduct(n) - (faceOut - 0.5 * mm)) < 0.1 * mm) &&
            tags.Any(x => Math.Abs((x.TagHeadPosition - mid).DotProduct(n) - walls.IntMid) < 0.1 * mm),
            string.Join(" / ", tags.Select(x => $"{(x.TagHeadPosition - mid).DotProduct(n) / mm:0.0} mm out (face {faceOut / mm:0.0})")));
    }

    /// <summary>Elevations: one upright tag on the surface the elevation looks at, of the elevation
    /// type; none on faces turned away; out of a door; clear of a wall in front.</summary>
    private static void PtElevation(Document doc)
    {
        List<string> types = PtMakeMaterialTagFamily(doc);
        if (types.Count != 4) { Check("a material tag type to test with", false); return; }
        PtWallSet walls = PtMakeWalls(doc);
        double mm = DrwUnits.FromMillimeters(1);
        Line hl = (Line)((LocationCurve)walls.H.Location).Curve;
        XYZ n = walls.H.Orientation, mid = hl.Evaluate(0.5, true);
        double baseZ = hl.GetEndPoint(0).Z;
        ViewPlan host = PtPlan(doc, 100, "__pt elevation host");
        ViewSection? MakeElevation(XYZ at, XYZ look)
        {
            using var t = new Transaction(doc, "pt elevation");
            t.Start();
            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(x => x.ViewFamily == ViewFamily.Elevation);
            ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, at, 50);
            // All of them first: deleting a marker's last view deletes the marker.
            List<ViewSection> four = Enumerable.Range(0, marker.MaximumViewCount).Where(marker.IsAvailableIndex)
                .Select(i => marker.CreateElevation(doc, host.Id, i)).ToList();
            // A one-view marker faces one way: turn it round to look at the wall.
            if (four.Count == 1 && four[0].ViewDirection.DotProduct(-look) < 0.9)
            {
                double turn = Math.Atan2(-look.Y, -look.X) - Math.Atan2(four[0].ViewDirection.Y, four[0].ViewDirection.X);
                ElementTransformUtils.RotateElement(doc, marker.Id, Line.CreateBound(at, at + XYZ.BasisZ), turn);
                doc.Regenerate();
            }
            ViewSection? found = four.FirstOrDefault(e => e.ViewDirection.DotProduct(-look) > 0.9);
            if (found == null)
                Info($"elevation marker: {marker.MaximumViewCount} views, made {four.Count}: " +
                     string.Join(" / ", four.Select(e => $"{e.ViewDirection.X:0.0},{e.ViewDirection.Y:0.0}")) + $" — want {-look.X:0.0},{-look.Y:0.0}");
            foreach (ViewSection e in four.Where(e => e != found)) doc.Delete(e.Id);
            if (found != null)
            {
                found.ViewTemplateId = ElementId.InvalidElementId;
                found.Name = "__pt elevation " + Guid.NewGuid().ToString("N").Substring(0, 6);
                found.CropBoxActive = false;
                found.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.Set(0);
            }
            t.Commit();
            return found;
        }
        WallMatTagPreset p = PtWmt(types);
        p.TypeElevation = types[1];   // the test family's "Below": the triangle under the point, pointing up
        ViewSection? el = MakeElevation(mid + n * 8, -n);
        if (el == null) { Check("an elevation looking at the test wall", false); return; }
        Info($"elevation plane {(PresetTagger.CutPlane(el) - mid).DotProduct(n) / mm:0} mm out from the wall (origin {(el.Origin - mid).DotProduct(n) / mm:0}); looking {el.ViewDirection.X:0.0},{el.ViewDirection.Y:0.0}");
        var rows = Wmt(doc, el, p);
        foreach (var r in rows.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]"))) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        double Along(XYZ x) => (x - hl.GetEndPoint(0)).DotProduct(hl.Direction);
        List<IndependentTag> h = PtMaterialTags(doc, el, walls.H);
        Check("an elevation: one upright tag of the elevation type on the face it looks at, near its middle",
            h.Count == 1 && Math.Abs(h[0].RotationAngle) < 1e-9 && (doc.GetElement(h[0].GetTypeId()) as ElementType)?.Name == "Below" &&
            Math.Abs(Along(h[0].TagHeadPosition) - hl.Length / 2) < hl.Length * 0.15 &&
            Math.Abs(h[0].TagHeadPosition.Z - (baseZ + 5)) < 1.5,
            string.Join(" / ", h.Select(x => $"{(doc.GetElement(x.GetTypeId()) as ElementType)?.Name} at {Along(x.TagHeadPosition) / mm:0} along, up {(x.TagHeadPosition.Z - baseZ) / mm:0}")));
        Check("…the perpendicular wall, seen edge-on, gets none", PtMaterialTags(doc, el, walls.V).Count == 0);

        // A door at the middle and a wall in front of the middle: the tag goes clear of both.
        FamilySymbol? doorType = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Doors)
            .Cast<FamilySymbol>().FirstOrDefault();
        Wall front;
        using (var t = new Transaction(doc, "pt door and front wall"))
        {
            t.Start();
            if (doorType != null)
            {
                if (!doorType.IsActive) doorType.Activate();
                doc.Create.NewFamilyInstance(mid, doorType, walls.H, (Level)doc.GetElement(walls.H.LevelId), Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            XYZ f0 = hl.Evaluate(0.55, true) + n * 3, f1 = hl.Evaluate(0.8, true) + n * 3;
            front = Wall.Create(doc, Line.CreateBound(f0, f1), walls.H.WallType.Id, walls.H.LevelId, 10, 0, false, false);
            t.Commit();
        }
        ViewSection? el2 = MakeElevation(mid + n * 8, -n);
        if (el2 == null) { Check("a second elevation", false); return; }
        var rows2 = Wmt(doc, el2, p);
        foreach (var r in rows2.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]"))) Info($"  {r["Element"]}: {r["Status"]} — {r["Note"]}");
        List<IndependentTag> h2 = PtMaterialTags(doc, el2, walls.H);
        double a2 = h2.Count == 1 ? Along(h2[0].TagHeadPosition) / hl.Length : -1;
        Check("…with a door at the middle and a wall in front of part of it: the tag lands clear of both",
            h2.Count == 1 && (a2 < 0.55 || a2 > 0.8) && !(a2 > 0.45 && a2 < 0.55 && h2[0].TagHeadPosition.Z < baseZ + 7.2),
            $"at {a2:0.00} of the wall, up {(h2.Count == 1 ? (h2[0].TagHeadPosition.Z - baseZ) / mm : 0):0} mm");

        // The whole face hidden: skipped, saying what by.
        Wall cover;
        using (var t = new Transaction(doc, "pt cover wall"))
        {
            t.Start();
            XYZ c0 = hl.GetEndPoint(0) + n * 2 - hl.Direction * 2, c1 = hl.GetEndPoint(1) + n * 2 + hl.Direction * 2;
            cover = Wall.Create(doc, Line.CreateBound(c0, c1), walls.H.WallType.Id, walls.H.LevelId, 12, -1, false, false);
            t.Commit();
        }
        ViewSection? el3 = MakeElevation(mid + n * 8, -n);
        if (el3 == null) { Check("a third elevation", false); return; }
        var rows3 = Wmt(doc, el3, p);
        Check("…a wall hiding the whole face: no tag, and it says what hides it",
            PtMaterialTags(doc, el3, walls.H).Count == 0 &&
            rows3.Any(r => r["Element"].Contains($"[{walls.H.Id.Value}]") && r["Note"].Contains("Hidden in this elevation")),
            string.Join(" | ", rows3.Where(r => r["Element"].Contains($"[{walls.H.Id.Value}]")).Select(r => r["Status"] + " " + r["Note"])));
    }

    /// <summary>The Wall Mat Tag dialog gives back every option it was given.</summary>
    private static void WmtPresetAndDialog()
    {
        var p = new WallMatTagPreset
        {
            Name = "rt", Faces = MaterialFaces.Interior,
            TypeAbove = "M-Material Wall Tag : Up", TypeBelow = "M-Material Wall Tag : Down", TypeLeft = "",
            TypeRight = "Gone Tag : Right",   // one the model no longer has: kept as it is
            PerPiece = true, MinPieceMm = 450, SeenFacesOnly = false, TypeMarkContains = "EX", TypeNameContains = "Brick", MinLengthMm = 600,
            TypeElevation = "M-Material Wall Tag : Up", Elevations = true,
            RepairMaterialTags = false, Plans = false, Sections = true, Scope = DrwViewScope.AllPlans, ViewNameContains = "L2", DryRun = true
        };
        using var w = new WallMatTagWindow(WallMatTagFixture(), p);
        var back = (WallMatTagPreset)w.GetType().GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null)!;
        back.Name = p.Name;   // an unsaved preset's name is not in the preset list, so it is not given back
        Check("the Wall Mat Tag dialog gives back every option it was given",
            System.Text.Json.JsonSerializer.Serialize(back) == System.Text.Json.JsonSerializer.Serialize(p),
            System.Text.Json.JsonSerializer.Serialize(back));
        var fresh = new WallMatTagPreset();
        Check("a new Wall Mat Tag preset repairs '?' tags, tags both faces, only seen ones",
            fresh.RepairMaterialTags && fresh.Faces == MaterialFaces.Both && fresh.SeenFacesOnly);
        WallMatTagPreset? old = System.Text.Json.JsonSerializer.Deserialize<WallMatTagPreset>("{\"Name\":\"old\",\"TypeAbove\":\"A : B\",\"PerPiece\":true}");
        Check("a 0.3.0.0 Wall Mat Tag preset loads with 'only seen faces' on", old != null && old.SeenFacesOnly && old.PerPiece);
    }

    /// <summary>A preset saved before 0.3.0's element mode and material tags loads as it was.</summary>
    private static void PtOldPreset()
    {
        const string old = "{\"Name\":\"old\",\"Rules\":[{\"Enabled\":true,\"Category\":\"OST_Doors\",\"TagType\":\"A : B\"," +
                           "\"Position\":\"Above\",\"DistanceMm\":3,\"Leader\":false}],\"Plans\":true,\"Scope\":\"AllPlans\"}";
        var opts = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        PresetTaggerPreset? p = System.Text.Json.JsonSerializer.Deserialize<PresetTaggerPreset>(old, opts);
        TagRule? r = p?.Rules.FirstOrDefault();
        Check("an old preset loads: by the view, the element (not material), no repair",
            r != null && r.PositionMode == TagPositionMode.View && r.Position == TagPosition.Above && r.What == TagWhat.Element &&
            !r.TagsMaterial() && p!.RepairMaterialTags == false && !r.MaterialPerPiece && r.MinPieceMm == 300 && r.TypeNameContains == "");
    }

    private static void PtPresetAndDialog()
    {
        PresetTaggerPreset fresh = DrwPresetStore<PresetTaggerPreset>.Clone(new PresetTaggerPreset());
        Check("a new preset has door, window and room rules",
            fresh.Rules.Select(r => r.Category).SequenceEqual(new[] { "OST_Doors", "OST_Windows", "OST_Rooms" }));

        var p = new PresetTaggerPreset
        {
            Name = "rt",
            Rules = new List<TagRule>
            {
                new() { Category = "OST_Doors", TagType = "M_Door Tag : Standard", Position = TagPosition.Below, DistanceMm = 3.5,
                    Leader = true, Horizontal = false, TypeMarkContains = "D", FamilyContains = "Single", MinLengthMm = 0 },
                new() { Category = "OST_Walls", TagType = "M_Wall Tag : 6mm", Enabled = false, MinLengthMm = 1200 },
                new() { Category = "OST_Ramps", TagType = "Ramp Tag : X" },   // not in the fixture model
                new() { Category = "OST_Doors", TagType = "M_Door Tag : Standard", PositionMode = TagPositionMode.Element,
                    Side = TagSide.Interior, DistanceMm = 2 },
                new() { Category = "OST_Walls", TagType = "M_Wall Tag : 6mm", Faces = MaterialFaces.Exterior,
                    MaterialTypeAbove = "M-Material Wall Tag : Up", MaterialTypeBelow = "M-Material Wall Tag : Down",
                    MaterialTypeLeft = "", MaterialTypeRight = "Gone Tag : Right" }   // one the model no longer has
            },
            Plans = false, CeilingPlans = true, Sections = true, Scope = DrwViewScope.AllPlans, ViewNameContains = "L2", DryRun = true
        };
        using var w = new PresetTaggerWindow(PresetTaggerFixture(), p);
        var back = (PresetTaggerPreset)w.GetType().GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null)!;
        string Json(PresetTaggerPreset x) => System.Text.Json.JsonSerializer.Serialize(new { x.Rules, x.Plans, x.CeilingPlans, x.Sections, x.Elevations, x.Scope, x.ViewNameContains, x.DryRun });
        Check("the dialog gives back every rule and option it was given", Json(back) == Json(p), Json(back));
    }

    // =============================================================================================
    // Probes (0.2.7): facts about Revit, logged with Info, that decide how Match Datums handles each
    // case in the condition matrix. They pass as long as they run; the log IS the result.

    private static string Crop(View v)
    {
        string box = DatumMatcher.ScopeBox(v) is { } id && id != ElementId.InvalidElementId
            ? $" (scope box '{v.Document.GetElement(id)?.Name}')" : "";
        if (!v.CropBoxActive) return "crop OFF" + box;
        BoundingBoxXYZ b = v.CropBox;
        return $"crop {P(b.Transform.OfPoint(b.Min))} .. {P(b.Transform.OfPoint(b.Max))}{box}";
    }

    private static string State(DatumPlane d, View v)
    {
        try
        {
            Curve c = d.GetCurvesInView(DatumExtentType.ViewSpecific, v).First();
            string E(DatumEnds e) => d.GetDatumExtentTypeInView(e, v) == DatumExtentType.Model ? "3D" : "2D";
            string B(DatumEnds e) { try { return d.IsBubbleVisibleInView(e, v) ? "o" : "-"; } catch { return "?"; } }
            return $"{P(c.GetEndPoint(0))}[{E(DatumEnds.End0)}{B(DatumEnds.End0)}] → {P(c.GetEndPoint(1))}[{E(DatumEnds.End1)}{B(DatumEnds.End1)}]" +
                   (c is Arc ? " arc" : "");
        }
        catch (Exception ex) { return "n/a: " + ex.Message; }
    }

    private static string Tie(DatumPlane d)
    {
        ElementId id = d.get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST)?.AsElementId() ?? ElementId.InvalidElementId;
        return id == ElementId.InvalidElementId ? "untied" : $"tied to '{d.Document.GetElement(id)?.Name}'";
    }

    private static void LogRows(List<IReadOnlyDictionary<string, string>> rows)
    {
        foreach (var r in rows) Info("   row: " + string.Join(" | ", r.Values));
    }

    /// <summary>Sets a view-specific line on a datum and logs what Revit reports straight after, after
    /// a regeneration, and (by the caller) after the commit.</summary>
    private static void TrySet(Document doc, DatumPlane d, View v, Curve wanted, string label)
    {
        try
        {
            foreach (DatumEnds e in new[] { DatumEnds.End0, DatumEnds.End1 })
                d.SetDatumExtentType(e, v, DatumExtentType.ViewSpecific);
            bool valid = d.IsCurveValidInView(DatumExtentType.ViewSpecific, v, wanted);
            Info($"   {label}: want {P(wanted.GetEndPoint(0))} → {P(wanted.GetEndPoint(1))}; IsCurveValidInView={valid}");
            if (!valid) return;
            d.SetCurveInView(DatumExtentType.ViewSpecific, v, wanted);
            Info($"   {label}: read at once   {State(d, v)}");
            doc.Regenerate();
            Info($"   {label}: after Regenerate {State(d, v)}");
        }
        catch (Exception ex) { Info($"   {label}: threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void ProbeScopeBoxTarget(Document doc)
    {
        ViewPlan? target = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => DrwScope.IsPlan(v) && DatumMatcher.ScopeBox(v) != ElementId.InvalidElementId)
            .FirstOrDefault(v => new FilteredElementCollector(doc, v.Id).OfClass(typeof(Grid)).GetElementCount() >= 2);
        if (target == null) { Info("no plan cropped by a scope box — not probed"); return; }
        ViewPlan source;
        using (var t = new Transaction(doc, "probe source"))
        {
            t.Start();
            source = NewPlan(doc, target.GetTypeId(), target.GenLevel.Id, "__probe uncropped");
            t.Commit();
        }
        Info($"target '{target.Name}': {Crop(target)}");
        Info($"source '{source.Name}': {Crop(source)}");
        List<Grid> grids = new FilteredElementCollector(doc, target.Id).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(g => !g.IsCurved).OrderBy(g => g.Name).Take(4).ToList();
        foreach (Grid g in grids)
            Info($"{g.Name} ({Tie(g)}): source {State(g, source)} | target {State(g, target)}");

        Info("(a) Match as the user did, source ends untouched:");
        var rows = DatumMatcher.Run(doc, source, new View[] { target });
        LogRows(rows.Where(r => grids.Any(g => r["Datum"] == "Grid " + g.Name)).ToList());
        foreach (Grid g in grids) Info($"   after: {g.Name} target {State(g, target)}");

        Info("(b) Direct SetCurveInView on the target, ends 20 ft OUTSIDE its crop, then 5 ft inside:");
        Grid probe = grids[0];
        Curve cur = probe.GetCurvesInView(DatumExtentType.ViewSpecific, target).First();
        XYZ dir = (cur.GetEndPoint(1) - cur.GetEndPoint(0)).Normalize();
        using (var t = new Transaction(doc, "probe set"))
        {
            t.Start();
            TrySet(doc, probe, target, Line.CreateBound(cur.GetEndPoint(0) - dir * 20, cur.GetEndPoint(1) + dir * 20), "outside");
            TrySet(doc, probe, target, Line.CreateBound(cur.GetEndPoint(0) + dir * 5, cur.GetEndPoint(1) - dir * 5), "inside");
            t.Commit();
        }
        Info($"   after commit: {State(probe, target)}");
        Check("probe ran", true);
    }

    private static void ProbeManualCropTarget(Document doc)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> g = VerticalGrids(doc, f, 3);
        using (var t = new Transaction(doc, "crop B to the middle of the grids"))
        {
            t.Start();
            BoundingBoxXYZ box = f.B.CropBox;
            Transform inv = box.Transform.Inverse;
            XYZ lo = inv.OfPoint(new XYZ(f.X0 - 10, f.Y0 + 25, 0)), hi = inv.OfPoint(new XYZ(f.X0 + 50, f.Y0 + 75, 0));
            box.Min = new XYZ(Math.Min(lo.X, hi.X), Math.Min(lo.Y, hi.Y), box.Min.Z);
            box.Max = new XYZ(Math.Max(lo.X, hi.X), Math.Max(lo.Y, hi.Y), box.Max.Z);
            f.B.CropBoxActive = true;
            f.B.CropBox = box;
            t.Commit();
        }
        Info($"source A: {Crop(f.A)}; target B: {Crop(f.B)}");
        foreach (Grid x in g) Info($"{x.Name}: A {State(x, f.A)} | B {State(x, f.B)}");
        Info("(a) match A → B with 3D ends in A:");
        LogRows(DatumMatcher.Run(doc, f.A, new View[] { f.B }));
        foreach (Grid x in g) Info($"   after: {x.Name} B {State(x, f.B)}");
        Info("(b) make G1's top end 2D in A, 10 ft short of its 3D end (still outside B's crop), then match:");
        using (var t = new Transaction(doc, "2D end in A"))
        {
            t.Start();
            (DatumEnds top, _) = Ends(g[0], f.A);
            g[0].SetDatumExtentType(top, f.A, DatumExtentType.ViewSpecific);
            Curve c = g[0].GetCurvesInView(DatumExtentType.ViewSpecific, f.A).First();
            XYZ p0 = c.GetEndPoint(0), p1 = c.GetEndPoint(1);
            g[0].SetCurveInView(DatumExtentType.ViewSpecific, f.A, top == DatumEnds.End1
                ? Line.CreateBound(p0, p1 - new XYZ(0, 10, 0)) : Line.CreateBound(p0 - new XYZ(0, 10, 0), p1));
            t.Commit();
        }
        Info($"   A now {State(g[0], f.A)}");
        LogRows(DatumMatcher.Run(doc, f.A, new View[] { f.B }));
        Info($"   after: B {State(g[0], f.B)}");
        Info("(c) direct SetCurveInView on B beyond its crop / inside its crop:");
        Curve cur = g[1].GetCurvesInView(DatumExtentType.ViewSpecific, f.B).First();
        XYZ d = (cur.GetEndPoint(1) - cur.GetEndPoint(0)).Normalize();
        using (var t = new Transaction(doc, "probe set"))
        {
            t.Start();
            TrySet(doc, g[1], f.B, Line.CreateBound(cur.GetEndPoint(0) - d * 30, cur.GetEndPoint(1) + d * 30), "outside");
            TrySet(doc, g[1], f.B, Line.CreateBound(new XYZ(cur.GetEndPoint(0).X, f.Y0 + 35, cur.GetEndPoint(0).Z),
                new XYZ(cur.GetEndPoint(0).X, f.Y0 + 65, cur.GetEndPoint(0).Z)), "inside");
            t.Commit();
        }
        Info($"   after commit: B {State(g[1], f.B)}");
        Check("probe ran", true);
    }

    private static void ProbeCategoryHidden(Document doc)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> g = VerticalGrids(doc, f, 2);
        (DatumEnds top, DatumEnds bottom) = Ends(g[0], f.A);
        using (var t = new Transaction(doc, "hide grids category in B; change A"))
        {
            t.Start();
            f.B.SetCategoryHidden(new ElementId(BuiltInCategory.OST_Grids), true);
            g[0].ShowBubbleInView(top, f.A); g[0].HideBubbleInView(bottom, f.A);
            g[0].ShowBubbleInView(top, f.B); g[0].ShowBubbleInView(bottom, f.B);
            t.Commit();
        }
        Info($"B grids category hidden: {f.B.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Grids))}; " +
             $"IsHidden(g0)={g[0].IsHidden(f.B)}; CanBeVisible={g[0].CanBeVisibleInView(f.B)}; " +
             $"in view collector: {new FilteredElementCollector(doc, f.B.Id).OfClass(typeof(Grid)).ToElementIds().Contains(g[0].Id)}");
        LogRows(DatumMatcher.Run(doc, f.A, new View[] { f.B }));
        Info($"after: B {State(g[0], f.B)}");
        Check("probe ran", true);
    }

    private static void ProbeArcPinnedDependent(Document doc)
    {
        Fixture f = MakeFixture(doc);
        List<Grid> g = VerticalGrids(doc, f, 2);
        Grid arc;
        ElementId depId;
        using (var t = new Transaction(doc, "arc, pin, dependent"))
        {
            t.Start();
            arc = Grid.Create(doc, Arc.Create(new XYZ(f.X0 + 120, f.Y0 + 50, 0), 30, 0, Math.PI / 2, XYZ.BasisX, XYZ.BasisY));
            arc.Name = "__st" + (++_gridNo);
            arc.SetVerticalExtents(f.Bottom, f.Top);
            g[1].Pinned = true;
            depId = f.B.Duplicate(ViewDuplicateOption.AsDependent);
            t.Commit();
        }
        var dep = (View)doc.GetElement(depId);
        using (var t = new Transaction(doc, "2D changes in A"))
        {
            t.Start();
            // Arc: its End1 made 2D and pulled back 20°.
            arc.SetDatumExtentType(DatumEnds.End1, f.A, DatumExtentType.ViewSpecific);
            var a = (Arc)arc.GetCurvesInView(DatumExtentType.ViewSpecific, f.A).First();
            Info($"arc in A: {State(arc, f.A)}");
            try
            {
                Arc shorter = Arc.Create(a.GetEndPoint(0), a.Evaluate(0.75, true), a.Evaluate(0.4, true));
                arc.SetCurveInView(DatumExtentType.ViewSpecific, f.A, shorter);
            }
            catch (Exception ex) { Info("arc shorten threw: " + ex.Message); }
            // Pinned grid: its top end 2D and 10 ft shorter.
            (DatumEnds top, _) = Ends(g[1], f.A);
            try
            {
                g[1].SetDatumExtentType(top, f.A, DatumExtentType.ViewSpecific);
                Curve c = g[1].GetCurvesInView(DatumExtentType.ViewSpecific, f.A).First();
                XYZ p0 = c.GetEndPoint(0), p1 = c.GetEndPoint(1);
                g[1].SetCurveInView(DatumExtentType.ViewSpecific, f.A, top == DatumEnds.End1
                    ? Line.CreateBound(p0, p1 - new XYZ(0, 10, 0)) : Line.CreateBound(p0 - new XYZ(0, 10, 0), p1));
                Info("pinned grid: 2D end change in the SOURCE accepted");
            }
            catch (Exception ex) { Info("pinned grid: 2D end change in the source threw: " + ex.Message); }
            t.Commit();
        }
        Info($"A: arc {State(arc, f.A)}; pinned {State(g[1], f.A)}");
        Info($"dependent '{dep.Name}': {Crop(dep)}; parent B {Crop(f.B)}");
        Info("match A → dependent view only:");
        LogRows(DatumMatcher.Run(doc, f.A, new View[] { dep }));
        Info($"after: dependent arc {State(arc, dep)} | parent arc {State(arc, f.B)}");
        Info($"after: dependent pinned {State(g[1], dep)} | parent pinned {State(g[1], f.B)}");
        Check("probe ran", true);
    }

    private static void ProbeMultiSegment(Document doc)
    {
        Fixture f = MakeFixture(doc);
        ElementId gridType = doc.GetDefaultElementTypeId(ElementTypeGroup.GridType);
        MultiSegmentGrid? msg = null;
        using (var t = new Transaction(doc, "multi-segment grid"))
        {
            t.Start();
            try
            {
                SketchPlane sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, f.Levels[0].Elevation)));
                var loop = new CurveLoop();
                XYZ a = new XYZ(f.X0, f.Y0, f.Levels[0].Elevation), b = new XYZ(f.X0, f.Y0 + 50, f.Levels[0].Elevation),
                    c = new XYZ(f.X0 + 30, f.Y0 + 80, f.Levels[0].Elevation);
                loop.Append(Line.CreateBound(a, b));
                loop.Append(Line.CreateBound(b, c));
                ElementId id = MultiSegmentGrid.Create(doc, gridType, loop, sp.Id);
                msg = doc.GetElement(id) as MultiSegmentGrid;
                t.Commit();
            }
            catch (Exception ex) { Info("create threw: " + ex.Message); t.RollBack(); }
        }
        if (msg == null) { Check("probe ran", true); return; }
        var members = msg.GetGridIds().Select(doc.GetElement).OfType<Grid>().ToList();
        Info($"multi-segment '{msg.Name}': {members.Count} member grids; in A's view collector: " +
             $"{new FilteredElementCollector(doc, f.A.Id).OfClass(typeof(Grid)).ToElementIds().Count(id => members.Any(m => m.Id == id))}; " +
             $"MultiSegmentGrid itself in collector: {new FilteredElementCollector(doc, f.A.Id).OfClass(typeof(MultiSegmentGrid)).GetElementCount()}");
        foreach (Grid m in members) Info($"  member {m.Name}: A {State(m, f.A)}");
        LogRows(DatumMatcher.Run(doc, f.A, new View[] { f.B }));
        Check("probe ran", true);
    }

    private static void ProbeWorksharingAndLinks(Document doc)
    {
        Info($"workshared: {doc.IsWorkshared}");
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
        int linkedGrids = links.Sum(l => l.GetLinkDocument() is Document ld
            ? new FilteredElementCollector(ld).OfClass(typeof(Grid)).GetElementCount() : 0);
        Info($"links: {links.Count} ({links.Count(l => l.GetLinkDocument() != null)} loaded), grids inside them: {linkedGrids}");
        Check("probe ran", true);
    }

    // =============================================================================================
    // Regression

    private static void EnlargeRegression(Document doc, bool stairs)
    {
        var p = new EnlargePreset { Name = "selftest" };
        List<EnlargeTarget> targets = stairs
            ? EnlargeTargets.Stairs(doc, p.MarkDelimiter, false, p.StairBeforeFirstStepM, p.StairAfterLastStepM)
            : EnlargeTargets.Rooms(doc, p.RoomLabel);
        if (targets.Count == 0) { Info("no targets — not exercised"); return; }
        var rows = new EnlargeCreator(doc, p, stairs).Create(targets.Take(3).ToList(), writeMarks: false);
        Info($"{targets.Count} targets, 3 used: {Count(rows, "Created")} created");
        Check("views created, none failed", Count(rows, "Created") > 0 && Count(rows, "Failed") == 0,
            Rows(rows.Where(r => r["Status"] == "Failed").ToList()));
    }

    private static void StairRegression(Document doc)
    {
        List<ViewPlan> plans = DrwScope.Resolve(doc, null, DrwViewScope.AllPlans, "")
            .Where(v => v.ViewType == ViewType.FloorPlan).Take(6).ToList();
        var rows = StairAnnotator.Run(doc, plans, new StairAnnotationPreset { Name = "selftest" });
        Info($"{plans.Count} plans: {Count(rows, "Created")} created, {Count(rows, "Skipped")} skipped");
        Check("none failed", Count(rows, "Failed") == 0, Rows(rows.Where(r => r["Status"] == "Failed").ToList()));
    }

    /// <summary>0.2.9: tread numbers in sections — made through the stairs by Enlarge Stair, then numbered.</summary>
    private static void StairSections(Document doc)
    {
        var before = new HashSet<ElementId>(new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).ToElementIds());
        var ep = new EnlargePreset { Name = "selftest", CreatePlan = false, ViewKind = EnlargeViewKind.Section, North = true, East = true, Prefix = "STSEC" };
        List<EnlargeTarget> targets = EnlargeTargets.Stairs(doc, ep.MarkDelimiter, false, ep.StairBeforeFirstStepM, ep.StairAfterLastStepM);
        if (targets.Count == 0) { Info("no stairs — not exercised"); return; }
        new EnlargeCreator(doc, ep, true).Create(targets.Take(3).ToList(), writeMarks: false);
        List<View> sections = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<View>()
            .Where(v => !before.Contains(v.Id) && !v.IsTemplate && v.ViewType == ViewType.Section).ToList();
        Info($"{sections.Count} sections made through {Math.Min(3, targets.Count)} stairs");
        Check("sections made", sections.Count > 0);

        var p = new StairAnnotationPreset { Name = "selftest" };
        var rows = StairAnnotator.Run(doc, sections, p);
        Info($"{Count(rows, "Created")} created, {Count(rows, "Skipped")} skipped, {Count(rows, "Failed")} failed");
        Check("tread numbers created in sections", rows.Any(r => r["Annotation"] == "Tread numbers" && r["Status"] == "Created"));
        Check("none failed", Count(rows, "Failed") == 0, Rows(rows.Where(r => r["Status"] == "Failed").ToList()));
        Check("no stair path in a section", rows.All(r => r["Annotation"] != "Stair path"));
        int placed = sections.Sum(v => new FilteredElementCollector(doc, v.Id).OfClass(typeof(NumberSystem)).GetElementCount());
        Check("numbers are really in the sections", placed == Count(rows, "Created"), $"{placed} in the views, {Count(rows, "Created")} rows");

        var again = StairAnnotator.Run(doc, sections, p);
        Check("rerun creates nothing", Count(again, "Created") == 0 && Count(again, "Failed") == 0, Rows(again.Take(5).ToList()));

        var off = StairAnnotator.Run(doc, sections, new StairAnnotationPreset { Name = "selftest", PlaceNumbers = false });
        Check("numbers off: a section takes nothing", off.Count == 0, Rows(off.Take(5).ToList()));

        // The name filter covers sections only when the preset ticks them — batches saved before 0.2.9 unchanged.
        List<View> withSections = DrwScope.ResolveStairViews(doc, null, DrwViewScope.AllPlans, "STSEC", sections: true);
        List<View> without = DrwScope.ResolveStairViews(doc, null, DrwViewScope.AllPlans, "STSEC", sections: false);
        Check("name filter takes sections only when ticked",
            withSections.Count(v => v.ViewType == ViewType.Section) == sections.Count && without.All(v => v.ViewType != ViewType.Section),
            $"ticked {withSections.Count}, unticked {without.Count}, made {sections.Count}");
        var legacy = (StairAnnotationPreset?)System.Text.Json.JsonSerializer.Deserialize<StairAnnotationPreset>("{\"Name\":\"old\"}");
        Check("a preset saved before 0.2.9 reads Sections = false", legacy != null && !legacy.Sections);
    }

    // =============================================================================================
    // Stair dimensions (0.2.9.1)

    private const double Mm = 1 / 304.8;

    /// <summary>
    /// A fixture stair east of everything, on the lowest level. Shape "straight" = one run of 10
    /// treads; "U" = the user's snapshot: 10 treads up, an automatic landing, 9 treads back, runs
    /// 1100 wide with a 750 gap; "L" = the second run turned 90°.
    /// </summary>
    private static Stairs? MakeStair(Document doc, Fixture f, string shape, out string why)
    {
        why = "";
        Level bottom = f.Levels[0], top = f.Levels[Math.Min(1, f.Levels.Count - 1)];
        double x0 = f.X0 + 200, y0 = f.Y0;
        var failures = new DrwStrictFailures();
        // The model's own stairs out of the fixture plans, so each check sees the fixture stair only.
        using (var th = new Transaction(doc, "hide model stairs"))
        {
            th.Start();
            foreach (ViewPlan v in new[] { f.A, f.B })
            {
                var ids = new FilteredElementCollector(doc, v.Id).OfClass(typeof(Stairs)).ToElements()
                    .Where(e => e.CanBeHidden(v)).Select(e => e.Id).ToList();
                if (ids.Count > 0) v.HideElements(ids);
            }
            th.Commit();
        }
        try
        {
            using var scope = new StairsEditScope(doc, "fixture stair " + shape);
            ElementId id = scope.Start(bottom.Id, top.Id);
            using (var t = new Transaction(doc, "fixture runs"))
            {
                t.Start();
                var st = (Stairs)doc.GetElement(id);
                double d = st.ActualTreadDepth;
                double w = 1100 * Mm;
                StairsRun r1 = StairsRun.CreateStraightRun(doc, id,
                    Line.CreateBound(new XYZ(x0, y0, 0), new XYZ(x0, y0 + 10 * d, 0)), StairsRunJustification.Center);
                r1.ActualRunWidth = w;
                if (shape != "straight")
                {
                    double z = r1.TopElevation;
                    double yEnd = y0 + 10 * d;
                    Line path = shape == "U"
                        ? Line.CreateBound(new XYZ(x0 + 1850 * Mm, y0 + 9 * d, z), new XYZ(x0 + 1850 * Mm, y0, z))
                        : Line.CreateBound(new XYZ(x0 + w / 2, yEnd + w / 2, z), new XYZ(x0 + w / 2 + 9 * d, yEnd + w / 2, z));
                    StairsRun r2 = StairsRun.CreateStraightRun(doc, id, path, StairsRunJustification.Center);
                    r2.ActualRunWidth = w;
                    // Sketched: the automatic landing refused runs whose ends are not level with each other.
                    double xMax = shape == "U" ? x0 + 1850 * Mm + w / 2 : x0 + w / 2;
                    var pts = new[]
                    {
                        new XYZ(x0 - w / 2, yEnd, 0), new XYZ(xMax, yEnd, 0),
                        new XYZ(xMax, yEnd + w, 0), new XYZ(x0 - w / 2, yEnd + w, 0)
                    };
                    var loop = new CurveLoop();
                    for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                    StairsLanding.CreateSketchedLanding(doc, id, loop, z);
                }
                t.Commit();
            }
            scope.Commit(failures);
            if (doc.GetElement(id) is Stairs made)
            {
                // A new element takes the active view's phase; the fixture plan may show an earlier one.
                using var tp = new Transaction(doc, "fixture stair phase");
                tp.Start();
                ElementId phase = f.A.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId;
                Parameter? created = made.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (phase != ElementId.InvalidElementId && created != null && !created.IsReadOnly) created.Set(phase);
                tp.Commit();
                bool seen = new FilteredElementCollector(doc, f.A.Id).OfClass(typeof(Stairs)).ToElementIds().Contains(id);
                Info($"fixture {shape} stair {id}: in the plan '{f.A.Name}': {seen}");
                if (!seen)
                {
                    using (var tr = new Transaction(doc, "regen")) { tr.Start(); doc.Regenerate(); tr.Commit(); }
                    bool afterRegen = new FilteredElementCollector(doc, f.A.Id).OfClass(typeof(Stairs)).ToElementIds().Contains(id);
                    BoundingBoxXYZ? bb = made.get_BoundingBox(null);
                    PlanViewRange vr = f.A.GetViewRange();
                    Info($"  after Regenerate: {afterRegen}; phase created {made.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsValueString()}, " +
                         $"view phase {f.A.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsValueString()}, phase filter {f.A.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsValueString()}; " +
                         $"bbox {bb?.Min} → {bb?.Max}; view level {f.A.GenLevel?.Name} @ {f.A.GenLevel?.Elevation:0.##}; " +
                         $"cut {vr.GetOffset(PlanViewPlane.CutPlane):0.##}, bottom {vr.GetOffset(PlanViewPlane.BottomClipPlane):0.##}, depth {vr.GetOffset(PlanViewPlane.ViewDepthPlane):0.##}; " +
                         $"hidden {made.IsHidden(f.A)}; stairs category shown {!f.A.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Stairs))}; " +
                         $"workset {made.WorksetId}; design option {made.DesignOption?.Name}; crop {f.A.CropBoxActive}");
                }
                return made;
            }
            why = "stair gone after commit: " + string.Join("; ", failures.Errors);
        }
        catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message + " " + string.Join("; ", failures.Errors); }
        return null;
    }

    /// <summary>
    /// The dimension engine on a fixture stair. Straight to it: a view's collector leaves out a
    /// stair made by the API in this session (phase, range, crop, hiding all checked), though
    /// Revit takes dimensions to it; the collector path is covered on Snowdon's own stairs.
    /// </summary>
    private static List<IReadOnlyDictionary<string, string>> DimFixture(Document doc, ViewPlan view, Stairs st,
        StairAnnotationPreset? p = null)
    {
        p ??= DimPreset();
        return StairDimensioner.Place(doc, view, st, $"Stair {st.Id}", p, null, "", StairDimensioner.GapMm(doc, p, null, out _));
    }

    /// <summary>The paper-mm distance of a straight dimension's line from the model origin, across <paramref name="along"/>.</summary>
    private static double LineAcrossMm(Dimension d, XYZ along, int scale)
    {
        XYZ across = new XYZ(-along.Y, along.X, 0);
        return ((Line)d.Curve).Origin.DotProduct(across) / Mm / scale;
    }

    /// <summary>
    /// Every prefixed run segment among <paramref name="dims"/> checked against treads × depth of
    /// the stair it measures (to the rounded mm Revit shows); returns the ones that are off.
    /// </summary>
    private static List<string> RunSegmentsOff(Document doc, IEnumerable<Dimension> dims)
    {
        var off = new List<string>();
        foreach (Dimension d in dims)
        {
            Stairs? st = null;
            foreach (Reference r in d.References)
            {
                Element? e = doc.GetElement(r.ElementId);
                st = e is StairsRun run ? run.GetStairs() : e is StairsLanding l ? l.GetStairs() : null;
                if (st != null) break;
            }
            if (st == null) continue;
            foreach ((long mm, string prefix) in Segs(d))
            {
                if (prefix.Length == 0) continue;
                int n = int.Parse(prefix.Substring(0, prefix.IndexOf('T')));
                long want = (long)Math.Round(n * st.ActualTreadDepth / Mm);
                if (mm != want) off.Add($"stair {st.Id}: '{prefix}{mm}' (treads × depth = {want})");
            }
        }
        return off;
    }

    private static StairAnnotationPreset DimPreset() => new()
    {
        Name = "selftest", PlacePath = false, PlaceNumbers = false, PlaceDimensions = true
    };

    /// <summary>The segment values (mm, rounded) and prefixes of a dimension, in order.</summary>
    private static List<(long Mm, string Prefix)> Segs(Dimension d)
    {
        var list = new List<(long, string)>();
        if (d.NumberOfSegments == 0) list.Add((Math.Round((d.Value ?? 0) / Mm) is var v ? (long)v : 0, d.Prefix ?? ""));
        else foreach (DimensionSegment s in d.Segments) list.Add(((long)Math.Round((s.Value ?? 0) / Mm), s.Prefix ?? ""));
        return list;
    }

    private static string Show(IEnumerable<Dimension> dims) =>
        string.Join("; ", dims.Select(d => string.Join(" | ", Segs(d).Select(s => s.Prefix + s.Mm))));

    private static void StairDimProbe(Document doc)
    {
        Fixture f = MakeFixture(doc);
        Stairs? st = MakeStair(doc, f, "straight", out string why);
        Check("fixture straight stair made", st != null, why);
        if (st == null) return;
        StairsRun run = (StairsRun)doc.GetElement(st.GetStairsRuns().First());
        XYZ a = XYZ.BasisY;
        var model = StairDimensioner.Faces(run, a, null);
        var inView = StairDimensioner.Faces(run, a, f.A);
        Info($"run faces facing a: {model.Count} from model geometry, {inView.Count} from the plan's geometry; " +
             $"treads {run.ActualTreadsNumber}, risers {run.ActualRisersNumber}, depth {Math.Round(st.ActualTreadDepth / Mm)} mm, " +
             $"width {Math.Round(run.ActualRunWidth / Mm)} mm, style {run.StairsRunStyle}");
        Check("a run has vertical faces with references", model.Count >= 2);
        var ends = model.OrderBy(x => x.At).ToList();
        using var t = new Transaction(doc, "probe dim");
        t.Start();
        var arr = new ReferenceArray();
        arr.Append(ends.First().Reference);
        arr.Append(ends.Last().Reference);
        Dimension? dim = null;
        string err = "";
        try
        {
            dim = doc.Create.NewDimension(f.A, Line.CreateBound(new XYZ(f.X0 + 190, ends.First().At, 0), new XYZ(f.X0 + 190, ends.Last().At, 0)), arr);
        }
        catch (Exception ex) { err = ex.Message; }
        Check("NewDimension takes the run's face references in a plan", dim != null, err);
        if (dim != null) Info($"probe dimension reads {Math.Round((dim.Value ?? 0) / Mm)} mm");
        t.RollBack();

        // What run geometry looks like: the fixture's and a few of Snowdon's.
        var ids = new List<ElementId> { st.Id };
        ids.AddRange(new long[] { 622037, 2120094, 620883, 695423 }.Select(n => new ElementId(n)));
        foreach (ElementId sid in ids)
        {
            if (doc.GetElement(sid) is not Stairs s) continue;
            foreach (ElementId rid in s.GetStairsRuns())
            {
                var r = (StairsRun)doc.GetElement(rid);
                Curve? path = r.GetStairsPath()?.FirstOrDefault();
                Info($"stair {sid} run {rid}: style {r.StairsRunStyle}, path {path?.GetType().Name} {path?.GetEndPoint(0)} → {path?.GetEndPoint(1)}, " +
                     $"base level {s.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsValueString()}");
                Info("    " + DumpGeometry(r.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine })));
            }
        }
    }

    /// <summary>
    /// 0.2.9.1 follow-up: what lies within 5 mm of each run end — in the plan's own geometry
    /// (what it draws) and in model geometry — so the dimension can attach to a drawn, exact line.
    /// </summary>
    private static void StairEndProbe(Document doc)
    {
        Fixture f = MakeFixture(doc);
        Stairs? fx = MakeStair(doc, f, "U", out string why);
        var cases = new List<(Stairs St, ViewPlan View)>();
        if (fx != null) cases.Add((fx, f.A)); else Info("fixture not made: " + why);
        ViewPlan? parking = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate && v.Name == "Parking");
        foreach (long id in new long[] { 620883, 688457, 1556525 })
            if (doc.GetElement(new ElementId(id)) is Stairs s && parking != null) cases.Add((s, parking));
        foreach ((Stairs st, ViewPlan view) in cases)
        {
            double d = st.ActualTreadDepth;
            foreach (ElementId rid in st.GetStairsRuns())
            {
                var run = (StairsRun)doc.GetElement(rid);
                List<Curve> path = run.GetStairsPath().ToList();
                XYZ s0 = path[0].GetEndPoint(0), s1 = path[path.Count - 1].GetEndPoint(1);
                XYZ a = new XYZ(s1.X - s0.X, s1.Y - s0.Y, 0).Normalize();
                double start = s0.DotProduct(a), end = s1.DotProduct(a);
                Info($"stair {st.Id} run {rid} risers {run.ActualRisersNumber} begins/ends riser {run.BeginsWithRiser}/{run.EndsWithRiser} in '{view.Name}': {run.ActualTreadsNumber}T × {Math.Round(d / Mm, 1)} = " +
                     $"{Math.Round(run.ActualTreadsNumber * d / Mm, 1)}; path {Math.Round((end - start) / Mm, 1)} mm");
                var parts = new List<Element> { run };
                parts.AddRange(st.GetStairsLandings().Select(doc.GetElement));
                foreach ((string label, Options o) in new[]
                {
                    ("view", new Options { ComputeReferences = true, View = view }),
                    ("model", new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine }),
                    ("model±60", new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine })
                })
                {
                    var found = new List<string>();
                    foreach (Element e in parts)
                    {
                        _probeWindowMm = label.Contains("60") ? 60 : 5;
                        ProbeNear(e.get_Geometry(o), Transform.Identity, a, start, end, e is StairsRun ? "run" : "landing", found, doc);
                    }
                    Info($"    {label}: " + (found.Count == 0 ? "nothing within 5 mm" : string.Join("; ", found.Distinct())));
                }
            }
        }
        Check("probe ran", true);
    }

    private static double _probeWindowMm = 5;

    private static void ProbeNear(GeometryElement? geo, Transform tf, XYZ a, double start, double end, string part,
        List<string> found, Document doc, bool symbol = false)
    {
        if (geo == null) return;
        XYZ across = new XYZ(-a.Y, a.X, 0);
        void Note(string kind, Reference? r, double at, GraphicsStyle? gs)
        {
            foreach ((string which, double target) in new[] { ("start", start), ("end", end) })
            {
                double mm = (at - target) * 304.8;
                if (Math.Abs(mm) <= _probeWindowMm)
                    found.Add($"{which} {part} {kind}{(symbol ? "(sym)" : "")} {mm:+0.0;-0.0;0} mm ref={(r != null ? "y" : "n")}" +
                              (gs != null ? $" style={gs.Name}" : ""));
            }
        }
        foreach (GeometryObject o in geo)
        {
            var gs = doc.GetElement(o.GraphicsStyleId) as GraphicsStyle;
            if (o is GeometryInstance gi)
            {
                ProbeNear(gi.GetSymbolGeometry(), tf.Multiply(gi.Transform), a, start, end, part, found, doc, true);
                ProbeNear(gi.GetInstanceGeometry(), tf, a, start, end, part, found, doc, false);
                continue;
            }
            if (o is Line l)
            {
                XYZ dir = tf.OfVector(l.Direction);
                if (Math.Abs(dir.Z) < 1e-6 && Math.Abs(Math.Abs(dir.DotProduct(across)) - 1) < 1e-6)
                    Note("curve", l.Reference, tf.OfPoint(l.GetEndPoint(0)).DotProduct(a), gs);
                continue;
            }
            if (o is not Solid s || s.Faces.IsEmpty) continue;
            foreach (Face face in s.Faces)
                if (face is PlanarFace pf)
                {
                    XYZ n = tf.OfVector(pf.FaceNormal);
                    if (Math.Abs(n.Z) < 1e-6 && Math.Abs(Math.Abs(n.DotProduct(a)) - 1) < 1e-6)
                        Note("face", pf.Reference, tf.OfPoint(pf.Origin).DotProduct(a), gs);
                }
            foreach (Edge edge in s.Edges)
                if (edge.AsCurve() is Line line)
                {
                    XYZ dir = tf.OfVector(line.Direction);
                    if (Math.Abs(dir.Z) < 1e-6 && Math.Abs(Math.Abs(dir.DotProduct(across)) - 1) < 1e-6)
                    {
                        XYZ p0 = tf.OfPoint(line.GetEndPoint(0));
                        Note($"edge z{Math.Round(p0.Z / Mm)}", edge.Reference, p0.DotProduct(a), gs);
                    }
                }
        }
    }

    private static string DumpGeometry(GeometryElement? geo, int depth = 0)
    {
        if (geo == null) return "no geometry";
        var parts = new List<string>();
        foreach (GeometryObject o in geo)
        {
            if (o is GeometryInstance gi) { parts.Add("instance{" + DumpGeometry(gi.GetInstanceGeometry(), depth + 1) + "}"); continue; }
            if (o is Solid s)
            {
                if (s.Faces.IsEmpty) { parts.Add("empty solid"); continue; }
                var normals = new Dictionary<string, int>();
                int refs = 0;
                foreach (Face f in s.Faces)
                {
                    string key = f is PlanarFace pf ? $"({pf.FaceNormal.X:0.##},{pf.FaceNormal.Y:0.##},{pf.FaceNormal.Z:0.##})" : f.GetType().Name;
                    normals[key] = normals.TryGetValue(key, out int c) ? c + 1 : 1;
                    if (f.Reference != null) refs++;
                }
                parts.Add($"solid {s.Faces.Size} faces, {refs} refs: " + string.Join(" ", normals.Select(kv => kv.Key + "×" + kv.Value)));
                continue;
            }
            parts.Add(o.GetType().Name);
        }
        return string.Join(" / ", parts);
    }

    private static void StairDimStraight(Document doc)
    {
        Fixture f = MakeFixture(doc);
        Stairs? st = MakeStair(doc, f, "straight", out string why);
        Check("fixture straight stair made", st != null, why);
        if (st == null) return;
        StairsRun run = (StairsRun)doc.GetElement(st.GetStairsRuns().First());
        long runMm = (long)Math.Round(run.ActualTreadsNumber * st.ActualTreadDepth / Mm);
        string prefix = StairDimensioner.RunLabel("{N}T @ {D} = ", run.ActualTreadsNumber, st.ActualTreadDepth);

        var before = Dims(doc, f.A);
        var rows = DimFixture(doc, f.A, st);
        List<Dimension> made = NewDims(doc, f.A, before);
        Info(Rows(rows) + "\n        dims: " + Show(made));
        Check("two strings created, none failed", Count(rows, "Created") == 2 && Count(rows, "Failed") == 0, Rows(rows));
        Check($"run string reads '{prefix}{runMm}'", made.Any(d => Segs(d).SequenceEqual(new[] { (runMm, prefix) })), Show(made));
        Check("width string reads 1100", made.Any(d => Segs(d).SequenceEqual(new[] { (1100L, "") })), Show(made));
        Check("no overall strings that repeat a string", made.Count == 2);

        var again = DimFixture(doc, f.A, st);
        Check("rerun skips an already dimensioned stair", Count(again, "Created") == 0 &&
            again.Any(r => r["Status"] == "Skipped" && r["Note"].Contains("Already dimensioned")), Rows(again));

        var off = StairAnnotator.Run(doc, new List<View> { f.B }, new StairAnnotationPreset { Name = "selftest", PlacePath = false, PlaceNumbers = false });
        Check("dimensions off: nothing", off.All(r => !r["Annotation"].StartsWith("Dimensions")), Rows(off));
        var upper = StairAnnotator.Run(doc, new List<View> { f.B }, DimPreset());
        Check("the plan of the level above adds no dimensions", Count(upper, "Created") == 0, Rows(upper));
    }

    private static void StairDimU(Document doc)
    {
        Fixture f = MakeFixture(doc);
        Stairs? st = MakeStair(doc, f, "U", out string why);
        Check("fixture U stair made", st != null, why);
        if (st == null) return;
        var runs = st.GetStairsRuns().Select(id => (StairsRun)doc.GetElement(id)).ToList();
        double d = st.ActualTreadDepth;
        Info($"treads {string.Join(", ", runs.Select(r => r.ActualTreadsNumber))}, depth {Math.Round(d / Mm)} mm, landings {st.GetStairsLandings().Count}");
        long r1 = (long)Math.Round(runs[0].ActualTreadsNumber * d / Mm), r2 = (long)Math.Round(runs[1].ActualTreadsNumber * d / Mm);
        string p1 = StairDimensioner.RunLabel("{N}T @ {D} = ", runs[0].ActualTreadsNumber, d);
        string p2 = StairDimensioner.RunLabel("{N}T @ {D} = ", runs[1].ActualTreadsNumber, d);

        var before = Dims(doc, f.A);
        var rows = DimFixture(doc, f.A, st);
        List<Dimension> made = NewDims(doc, f.A, before);
        Info(Rows(rows) + "\n        dims: " + Show(made));
        Check("five strings created, none failed", Count(rows, "Created") == 5 && Count(rows, "Failed") == 0, Rows(rows));
        Check("widths read 1100 | 750 | 1100", made.Any(x => Segs(x).Select(s => s.Mm).SequenceEqual(new[] { 1100L, 750, 1100 })), Show(made));
        Check("overall width reads 2950", made.Any(x => Segs(x).Select(s => s.Mm).SequenceEqual(new[] { 2950L })), Show(made));
        long total = made.SelectMany(Segs).Select(s => s.Mm).DefaultIfEmpty(0).Max();
        Check($"left string reads '{p1}{r1}' then the landing", made.Any(x => Segs(x).Count == 2 && Segs(x)[0] == (r1, p1) && Segs(x)[1].Prefix == ""
            && Segs(x)[0].Mm + Segs(x)[1].Mm == total), Show(made));
        Check($"right string reads '{p2}{r2}' then the gap to the landing's far edge", made.Any(x => Segs(x).Count == 2 && Segs(x)[0] == (r2, p2)
            && Segs(x)[0].Mm + Segs(x)[1].Mm == total), Show(made));
        Check("overall length is the largest string", made.Any(x => Segs(x).SequenceEqual(new[] { (total, "") })), Show(made));
        Info($"snapshot comparison: overall {total} (snapshot 3850 at 275 mm treads)");
        Check("run segments read treads × depth exactly", RunSegmentsOff(doc, made).Count == 0, string.Join("; ", RunSegmentsOff(doc, made)));
        Check("each run row names what its ends are on", rows.Where(r => r["Annotation"].Contains("Run ")).All(r => r["Note"].Contains("start on")), Rows(rows));

        // Spacing: the type's snap distance by default; the mm value when that is turned off.
        XYZ a = XYZ.BasisY;
        double SpacingMm(List<Dimension> dims)
        {
            Dimension? run1 = dims.FirstOrDefault(x => Segs(x).Count == 2 && Segs(x)[0].Prefix == p1);
            Dimension? overall = dims.FirstOrDefault(x => Segs(x).Count == 1 && Segs(x)[0].Mm == total);
            return run1 == null || overall == null ? double.NaN
                : Math.Abs(LineAcrossMm(overall, a, f.A.Scale) - LineAcrossMm(run1, a, f.A.Scale));
        }
        double snap = StairDimensioner.GapMm(doc, DimPreset(), null, out string snapNote);
        double got = SpacingMm(made);
        Check($"strings spaced by the type's snap distance ({snap:0.0} mm{(snapNote.Length > 0 ? ", " + snapNote : "")})",
            Math.Abs(got - snap) < 0.05, $"{got:0.00} mm");

        var mmPreset = DimPreset();
        mmPreset.SpacingFromType = false;
        mmPreset.DimSpacingMm = 12;
        var beforeB = Dims(doc, f.B);
        DimFixture(doc, f.B, st, mmPreset);
        double gotMm = SpacingMm(NewDims(doc, f.B, beforeB));
        Check("with the snap tick off, strings spaced by the mm value (12)", Math.Abs(gotMm - 12) < 0.05, $"{gotMm:0.00} mm");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<StairAnnotationPreset>("{\"Name\":\"old\"}");
        Check("a preset saved without the field spaces by the snap distance", legacy != null && legacy.SpacingFromType);
    }

    /// <summary>The L layout the user chose: each run's string on its outer side, each run's width opposite.</summary>
    private static void StairDimL(Document doc)
    {
        Fixture f = MakeFixture(doc);
        Stairs? st = MakeStair(doc, f, "L", out string why);
        Check("fixture L stair made", st != null, why);
        if (st == null) return;
        var runs = st.GetStairsRuns().Select(id => (StairsRun)doc.GetElement(id)).ToList();
        double d = st.ActualTreadDepth;
        long r1 = (long)Math.Round(runs[0].ActualTreadsNumber * d / Mm), r2 = (long)Math.Round(runs[1].ActualTreadsNumber * d / Mm);
        string p1 = StairDimensioner.RunLabel("{N}T @ {D} = ", runs[0].ActualTreadsNumber, d);
        string p2 = StairDimensioner.RunLabel("{N}T @ {D} = ", runs[1].ActualTreadsNumber, d);

        var before = Dims(doc, f.A);
        var rows = DimFixture(doc, f.A, st);
        List<Dimension> made = NewDims(doc, f.A, before);
        Info(Rows(rows) + "\n        dims: " + Show(made));
        Check("six strings created, none failed", Count(rows, "Created") == 6 && Count(rows, "Failed") == 0, Rows(rows));
        bool Has(Func<List<(long Mm, string Prefix)>, bool> test) => made.Any(x => test(Segs(x)));
        // Run 1 goes up +Y on the west; the landing is its top 1100; run 2 runs east from it.
        Check($"run 1 string: '{p1}{r1}' | 1100", Has(s => s.Count == 2 && s[0] == (r1, p1) && s[1] == (1100L, "")), Show(made));
        Check($"run 2 string: 1100 | '{p2}{r2}' (landing then run 2)", Has(s => s.Count == 2
            && s.Any(x => x == (r2, p2)) && s.Any(x => x == (1100L, ""))), Show(made));
        Check($"run 2 width, opposite run 1: {r1} | 1100", Has(s => s.Count == 2 && s.All(x => x.Prefix == "")
            && s[0].Mm == r1 && s[1].Mm == 1100), Show(made));
        Check($"run 1 width, opposite run 2: 1100 | {r2}", Has(s => s.Count == 2 && s.All(x => x.Prefix == "")
            && s.Any(x => x.Mm == 1100) && s.Any(x => x.Mm == r2) && !(s[0].Mm == r1)), Show(made));
        Check($"overall length {r1 + 1100}", Has(s => s.Count == 1 && s[0].Mm == r1 + 1100), Show(made));
        Check($"overall width {r2 + 1100}", Has(s => s.Count == 1 && s[0].Mm == r2 + 1100), Show(made));
        Check("run segments read treads × depth exactly", RunSegmentsOff(doc, made).Count == 0, string.Join("; ", RunSegmentsOff(doc, made)));

        // Sides: run 1's string west of the stair, run 2's north; each width string opposite.
        Dimension? s1 = made.FirstOrDefault(x => Segs(x).Any(y => y.Prefix == p1));
        Dimension? s2 = made.FirstOrDefault(x => Segs(x).Any(y => y.Prefix == p2));
        BoundingBoxXYZ? bb = st.get_BoundingBox(null);
        if (s1 != null && s2 != null && bb != null)
        {
            double x1 = ((Line)s1.Curve).Origin.X, y2 = ((Line)s2.Curve).Origin.Y;
            Check("run 1's string is west of the stair, run 2's north", x1 < bb.Min.X && y2 > bb.Max.Y,
                $"run 1 x {x1:0.00} vs stair {bb.Min.X:0.00}; run 2 y {y2:0.00} vs stair {bb.Max.Y:0.00}");
        }
        var again = DimFixture(doc, f.A, st);
        Check("rerun skips the L stair", Count(again, "Created") == 0, Rows(again));
    }

    private static void StairDimSkips(Document doc)
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<StairAnnotationPreset>("{\"Name\":\"old\"}");
        Check("a preset saved before 0.2.9.1 reads PlaceDimensions = false", legacy != null && !legacy.PlaceDimensions);
        Check("run label fills {N} and {D}", StairDimensioner.RunLabel("{N}T @ {D} = ", 10, 275 * Mm) == "10T @ 0.275 = ",
            StairDimensioner.RunLabel("{N}T @ {D} = ", 10, 275 * Mm));
    }

    private static void StairDimBatch(Document doc)
    {
        var p = DimPreset();
        p.Name = TestPreset;
        p.ViewNameContains = "Parking";
        StairAnnotationStore.Store.Save(p);
        try
        {
            IBatchAction action = ActionRegistry.Create("Stair Annotation — " + TestPreset);
            var rows = action.RunDetailRows(null!, doc).ToList();
            var list = rows.Select(r => (IReadOnlyDictionary<string, string>)r).ToList();
            Info($"{Count(list, "Created")} created, {Count(list, "Skipped")} skipped");
            Check("the batch run dimensions Snowdon's Parking stairs", Count(list, "Created") > 0 && Count(list, "Failed") == 0, Rows(list));
            Check("the batch run stays inside its name filter", list.Where(r => r["View"].Length > 0).All(r => r["View"].Contains("Parking")));
        }
        finally { StairAnnotationStore.Store.Delete(TestPreset); }
    }

    /// <summary>Snowdon's own stairs, in the plans of their base levels.</summary>
    private static void StairDimModel(Document doc)
    {
        List<View> plans = DrwScope.ResolveStairViews(doc, null, DrwViewScope.AllPlans, "", false)
            .Where(v => v.ViewType == ViewType.FloorPlan).ToList();
        var before = new HashSet<ElementId>(new FilteredElementCollector(doc).OfClass(typeof(Dimension)).ToElementIds());
        var rows = StairAnnotator.Run(doc, plans, DimPreset());
        var made = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().Where(x => !before.Contains(x.Id)).ToList();
        List<string> off = RunSegmentsOff(doc, made);
        Info($"{made.Count} dimensions; run segments off treads × depth: {off.Count}");
        foreach (string o in off.Take(10)) Info("  " + o);
        foreach (var g in rows.Where(r => r["Note"].Contains("start on")).GroupBy(r => System.Text.RegularExpressions.Regex.Replace(
                     r["Note"].Substring(r["Note"].IndexOf("start on", StringComparison.Ordinal)), @"[-+]?\d+(\.\d+)?", "#")))
            Info($"ends ×{g.Count()}: {g.Key}");
        // A run whose top meets a floor with no line at the run's end (stair 1556525: nothing within
        // 60 mm) cannot read exactly; its row must then say so.
        int flagged = rows.Count(r => r["Note"].Contains("check by hand"));
        Check("every run segment reads treads × depth, or its row says to check it by hand", off.Count <= flagged,
            $"{off.Count} off, {flagged} flagged: " + string.Join("; ", off.Take(10)));
        var dimRows = rows.Where(r => r["Annotation"].StartsWith("Dimensions")).ToList();
        Info($"{plans.Count} plans: {Count(dimRows, "Created")} strings created, {Count(dimRows, "Skipped")} skipped, {Count(dimRows, "Failed")} failed");
        foreach (var g in dimRows.Where(r => r["Status"] == "Skipped").GroupBy(r => r["Note"])) Info($"skipped ×{g.Count()}: {g.Key}");
        Check("no Failed rows", Count(dimRows, "Failed") == 0, Rows(dimRows.Where(r => r["Status"] == "Failed").ToList()));
        Check("prefix kept on every run string", dimRows.All(r => !r["Note"].Contains("prefix")), Rows(dimRows.Where(r => r["Note"].Contains("prefix")).ToList()));
    }

    // =============================================================================================
    // Reporting

    /// <summary>DRW_SELFTEST_ONLY=&lt;text&gt; runs only the scenarios whose name contains it (e.g. PROBE).</summary>
    private static readonly string Only = Environment.GetEnvironmentVariable("DRW_SELFTEST_ONLY") ?? "";

    /// <summary>DW_DEV=1: also run the Enlarge Door &amp; Window family scenarios (BUILD, PROBE, FIX,
    /// LOCK), which write .rfa files in DW_BUILD_OUT. Never part of a normal run.</summary>
    private static readonly bool DwDev = Environment.GetEnvironmentVariable("DW_DEV") == "1";

    private static void Scenario(string name, Action body)
    {
        if (Only.Length > 0 && name.IndexOf(Only, StringComparison.OrdinalIgnoreCase) < 0) return;
        _scenario = name;
        Log("");
        Log("## " + name);
        try { body(); }
        catch (Exception ex) { Fail(name, "threw " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
    }

    private static void Isolated(Document doc, string name, Action body)
    {
        if (Only.Length > 0 && name.IndexOf(Only, StringComparison.OrdinalIgnoreCase) < 0) return;
        using var group = new TransactionGroup(doc, "DRW self-test");
        group.Start();
        try { Scenario(name, body); }
        finally { if (group.HasStarted() && !group.HasEnded()) group.RollBack(); }
    }

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok)
        {
            _pass++;
            Log("  PASS " + what);
        }
        else Fail(_scenario, what + (detail.Length > 0 ? " — " + detail : ""));
    }

    private static void Fail(string scenario, string what)
    {
        _fail++;
        Failures.Add($"[{scenario}] {what.Split('\n')[0]}");
        Log("  FAIL " + what);
    }

    private static void Info(string what) => Log("  info " + what);

    private static void Log(string line)
    {
        try { File.AppendAllText(_report, line + Environment.NewLine, Encoding.UTF8); } catch { /* best effort */ }
    }
}
#endif
