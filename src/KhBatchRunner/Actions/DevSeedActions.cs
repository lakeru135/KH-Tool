#if DEBUG
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// The WinForms global using (UseWindowsForms) also defines a "View" type; this add-in means Revit's.
using RevitView = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Actions;

/// <summary>
/// DEBUG-only seed action for the automated test loop: imports the tiny fixture DXF
/// (<c>Fixtures/seed.dxf</c>, a single line) so a scratch model reliably has a non-linked
/// <see cref="ImportInstance"/> for <see cref="PurgeImportCadAction"/> to purge against.
/// Compiled only in Debug — this is a test fixture, not a shipped feature, and is never
/// registered in a Release build (see <see cref="ActionRegistry.All"/>).
/// </summary>
public sealed class SeedCadImportAction : BatchActionBase
{
    public override string Name => "DEV Seed CAD Import";

    public override WorksetPosture Worksets => WorksetPosture.ReadsNoInstances;

    public override string WorksetPostureReason =>
        "Imports a DXF into a view it finds by ViewPlan/View3D — views, not user-workset content. " +
        "Dev fixture: it CREATES the import that Purge Import CAD is then meant to find.";

    public override ActionCategory Category => ActionCategory.Action;

    public override IReadOnlyList<string> Columns { get; } = new[] { "Result" };

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        string dxfPath = ResolveFixturePath();
        RevitView view = doc.ActiveView ?? FindFallbackView(doc);

        using var t = new Transaction(doc, "DEV Seed CAD Import");
        t.Start();
        var options = new DWGImportOptions
        {
            Placement = ImportPlacement.Origin,
            ThisViewOnly = false
        };
        bool ok = doc.Import(dxfPath, options, view, out ElementId _);
        t.Commit();

        return new Dictionary<string, string> { ["Result"] = ok ? "Imported" : "Import returned false" };
    }

    /// <summary>The fixture ships next to the built DLL (see the csproj's CopyToOutputDirectory item).</summary>
    private static string ResolveFixturePath()
    {
        string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        string path = Path.Combine(asmDir, "Fixtures", "seed.dxf");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Seed DXF fixture not found: {path}");
        return path;
    }

    /// <summary>Headless (no ActiveView, e.g. a scheduled/CI run) — any plan or 3D view will accept the import.</summary>
    private static RevitView FindFallbackView(Document doc)
    {
        RevitView? plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate);
        if (plan != null) return plan;

        RevitView? view3d = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
            .FirstOrDefault(v => !v.IsTemplate);
        if (view3d != null) return view3d;

        throw new InvalidOperationException("No usable view found to import the seed DXF into.");
    }
}

/// <summary>
/// DEBUG-only seed action for the automated test loop: creates two identical, overlapping
/// walls on the model's first level — that reliably raises Revit's "walls overlap" warning
/// so <see cref="IsolateWarningElementsAction"/> has something real to isolate. Compiled
/// only in Debug, same as <see cref="SeedCadImportAction"/>.
/// </summary>
public sealed class SeedWarningAction : BatchActionBase
{
    public override string Name => "DEV Seed Warning";

    public override WorksetPosture Worksets => WorksetPosture.PartialByDesign;

    public override string WorksetPostureReason =>
        "Needs a Level to place its two overlapping walls on, and Level is an INSTANCE normally " +
        "living in Shared Levels and Grids — so under a closed open the collector can come back " +
        "empty and this throws 'No Level found'. A loud failure, not a silent wrong answer, which " +
        "is why a dev fixture can carry it. Declared 2026-08-16.";

    public override ActionCategory Category => ActionCategory.Action;

    public override IReadOnlyList<string> Columns { get; } = new[] { "Result" };

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        Level? level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
        if (level == null)
            throw new InvalidOperationException("No Level found to seed the overlapping walls on.");

        Line line = Line.CreateBound(new XYZ(0, 0, 0), new XYZ(10, 0, 0));

        using var t = new Transaction(doc, "DEV Seed Warning");
        t.Start();
        try
        {
            Wall.Create(doc, line, level.Id, false);
            Wall.Create(doc, line, level.Id, false); // identical + overlapping → "walls overlap" warning
        }
        catch (Exception ex)
        {
            t.RollBack();
            throw new InvalidOperationException("Could not seed overlapping walls: " + ex.Message, ex);
        }
        t.Commit();

        return new Dictionary<string, string> { ["Result"] = "Created 2 overlapping walls" };
    }
}
#endif
