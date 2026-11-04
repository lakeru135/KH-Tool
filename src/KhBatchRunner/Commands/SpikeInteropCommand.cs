#if DEBUG
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Interop;

using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// DEV-only Phase A spike for Batch Interop: exercises <see cref="ModelCheckerInvoker"/>
/// end-to-end and logs every step, answering the plan's verification goals —
/// G0 (which AIT assemblies are loaded, and in what context), G3 (run against the active doc,
/// then against a background-opened doc), G4 (run against a cloud doc — open one and click
/// again), G5 (doc.IsModified before/after), G6 (HTML + Excel export). Hardcoded to this
/// machine's test rig; never ships (whole file is #if DEBUG).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SpikeInteropCommand : IExternalCommand
{
    private static string SpikeDir => Path.Combine(Diagnostics.SelfTest.TestRoot, "interopspike");
    private static string BackgroundTestModel => Path.Combine(Diagnostics.SelfTest.TestRoot, "models", "TestA.rvt");
    // The hosted checkset this machine last used in the AIT UI (RecentFiles_MC.json); a local
    // interopspike\checkset.xml takes precedence when present.
    private const string FallbackChecksetUrl =
        "https://interoperability.autodesk.com/modelchecker/hostedchecks/bestpractices-2024.xml";

    private StreamWriter? _log;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiApp = commandData.Application;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(SpikeDir);
        string logPath = Path.Combine(SpikeDir, $"spike-{stamp}.log");
        string reportDir = Path.Combine(SpikeDir, "reports", stamp);

        var summary = new List<string>();
        using (_log = new StreamWriter(logPath) { AutoFlush = true })
        {
            ModelCheckerInvoker.Trace = Note;
            try
            {
                Note($"=== Batch Interop spike, Revit {uiApp.Application.VersionNumber} ===");
                LogLoadedAitAssemblies(); // G0

                string checkset = Path.Combine(SpikeDir, "checkset.xml");
                if (!File.Exists(checkset)) checkset = FallbackChecksetUrl;
                Note("checkset: " + checkset);

                Document? active = uiApp.ActiveUIDocument?.Document;
                if (active != null)
                    summary.Add(RunOn(uiApp, active, checkset, reportDir, "active doc"));
                else
                {
                    Note("no active document — skipping active-doc run");
                    summary.Add("Active doc: (none open — skipped)");
                }

                // G3b: a background-opened doc (no window) — decides NeedsActiveDocument.
                if (File.Exists(BackgroundTestModel) &&
                    !string.Equals(active?.PathName, BackgroundTestModel, StringComparison.OrdinalIgnoreCase))
                {
                    Note("opening background doc " + BackgroundTestModel);
                    Document bg = uiApp.Application.OpenDocumentFile(BackgroundTestModel);
                    try { summary.Add(RunOn(uiApp, bg, checkset, reportDir, "background doc")); }
                    finally { Note("closing background doc (no save)"); bg.Close(false); }
                }
                else
                {
                    Note("background test model unavailable or same as active — skipped");
                    summary.Add("Background doc: (skipped)");
                }
            }
            catch (Exception ex)
            {
                Note("SPIKE FAILED: " + ex);
                summary.Add("FAILED: " + ex.Message);
            }
            finally
            {
                ModelCheckerInvoker.Trace = null;
            }
        }
        _log = null;

        TaskDialog.Show("Spike Interop",
            string.Join("\n\n", summary) + $"\n\nFull log:\n{logPath}\nReports:\n{reportDir}");
        return Result.Succeeded;
    }

    private string RunOn(UIApplication uiApp, Document doc, string checkset, string reportDir, string label)
    {
        Note($"--- run on {label}: {doc.Title}");
        Note($"    PathName={doc.PathName}");
        Note($"    IsModelInCloud={doc.IsModelInCloud}, IsWorkshared={doc.IsWorkshared}, IsLinked={doc.IsLinked}");
        Note($"    IsModified BEFORE run: {doc.IsModified}"); // G5

        string safeTitle = string.Join("_", doc.Title.Split(Path.GetInvalidFileNameChars()));
        string htmlDir = Path.Combine(reportDir, safeTitle);
        string xlsxPath = Path.Combine(reportDir, safeTitle + ".xlsx");
        Directory.CreateDirectory(htmlDir);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ModelCheckerRunResult result = ModelCheckerInvoker.RunCheckset(
            uiApp, doc, checkset, htmlDir, xlsxPath);
        sw.Stop();

        Note($"    IsModified AFTER run: {doc.IsModified}"); // G5
        Note($"    duration: {sw.Elapsed}");
        string html = Directory.Exists(htmlDir) && Directory.GetFiles(htmlDir, "*.html").Length > 0
            ? "yes" : "MISSING";
        string xlsx = File.Exists(xlsxPath) ? "yes" : "MISSING";
        Note($"    html written: {html}, xlsx written: {xlsx}");

        return $"{label} ({doc.Title}): {result.OverallResult} — {result.Passed}/{result.TotalChecks} passed, " +
               $"{result.Failed} failed, {result.Errors} errors, {result.NotRun} not run " +
               $"({result.PassPercent:0.#}%) in {sw.Elapsed.TotalSeconds:0.#}s; " +
               $"html={html} xlsx={xlsx}; IsModified={doc.IsModified}";
    }

    /// <summary>G0: which AIT / validation assemblies the process already has, and where from.</summary>
    private void LogLoadedAitAssemblies()
    {
        Note("G0 — loaded AIT-related assemblies:");
        int n = 0;
        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = asm.GetName().Name ?? "";
            if (!name.StartsWith("AIT.", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Autodesk.ValidationTool", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Autodesk.BIT", StringComparison.OrdinalIgnoreCase))
                continue;
            n++;
#if NETFRAMEWORK
            Note($"    {name} @ {SafeLocation(asm)}");
#else
            var ctx = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm);
            Note($"    {name} @ {SafeLocation(asm)} [context: {ctx?.Name ?? "(unknown)"}]");
#endif
        }
        if (n == 0) Note("    (none loaded yet — invoker will LoadFrom the AIT folder)");
    }

    private static string SafeLocation(System.Reflection.Assembly asm)
    {
        try { return string.IsNullOrEmpty(asm.Location) ? "(dynamic)" : asm.Location; }
        catch { return "(unavailable)"; }
    }

    private void Note(string msg)
    {
        _log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {msg}");
    }
}
#endif
