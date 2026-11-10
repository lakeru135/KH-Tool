using System.IO;
using Autodesk.Revit.UI;
using KhBatchRunner.Forma;
using KhBatchRunner.Actions;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Diagnostics;

public static partial class SelfTest
{
    /// <summary>
    /// Dynamo-runner spike (<c>KHBATCH_DYN_SPIKE=&lt;path.dyn&gt;</c>): answers the Phase-4
    /// integration questions against the real DynamoRevit API. Part A runs the graph with a
    /// model open-and-ACTIVATED (the shape the runner will use — the first activate from the
    /// Home screen is legal even inside Idling because there's no active document to switch
    /// away from). Part B opens a second model in the BACKGROUND (no window) and runs the graph
    /// again WITHOUT activating, to observe which document Dynamo actually binds to. The graph
    /// itself writes a marker file naming the document it saw, so the answer is unambiguous.
    /// </summary>
    private static ExternalEvent? _dynSpikeEvent;
    private static IExternalEventHandler? _dynSpikeHandler;

    private static void RunDynSpike(UIApplication? uiApp, string dynPath)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== Dynamo spike {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Append("dyn: " + dynPath);
        if (uiApp is null) { Append("No UIApplication. Aborting."); return; }

        // First spike finding: DynamoRevit.ExecuteCommand HANGS when invoked from inside an
        // executing Idling event (it never returned; no dialog). Run the spike body from an
        // ExternalEvent's Execute instead — the same context class as an external command,
        // which is where the production runner (modal message-loop) will invoke it.
        _deferEnd = true;
        _dynSpikeHandler = new RelayHandler(app =>
        {
            try { DynSpikeBody(app, dynPath); }
            catch (Exception ex) { Append("spike body threw: " + ex); }
            finally
            {
                try { _dynSpikeEvent?.Dispose(); } catch { /* best effort */ }
                _dynSpikeEvent = null;
                _dynSpikeHandler = null;
                End();
            }
        });
        try { _dynSpikeEvent = ExternalEvent.Create(_dynSpikeHandler); _dynSpikeEvent.Raise(); }
        catch (Exception ex) { Append("ExternalEvent.Create/Raise threw: " + ex); End(); }
    }

    private static void DynSpikeBody(UIApplication uiApp, string dynPath)
    {
        string modelA = Path.Combine(TestRoot, "models", "TestA.rvt");
        string modelB = Path.Combine(TestRoot, "models", "TestB.rvt");
        using var suppressor = new DialogSuppressor(uiApp);
        Scripts.DynamoInvoker.Trace = Append; // incremental step log — locates a hang inside Dynamo

        // Part A — active document (the runner's intended shape).
        try
        {
            Autodesk.Revit.DB.Document docA = uiApp.OpenAndActivateDocument(modelA).Document;
            Append($"A: opened+activated {docA.Title}");
            string result = Step("A: RunGraph (active doc)",
                () => Scripts.DynamoInvoker.RunGraph(uiApp, dynPath)) ?? "(threw)";
            Append("A: result=" + result);
        }
        catch (Exception ex) { Append("A: open/run threw: " + ex); }

        // Part B — background document, nothing activated since A.
        try
        {
            Autodesk.Revit.DB.Document docB = uiApp.Application.OpenDocumentFile(modelB);
            Append($"B: opened background {docB.Title}; active=" +
                   (uiApp.ActiveUIDocument?.Document?.Title ?? "(none)"));
            string result = Step("B: RunGraph (background doc open, old doc still active)",
                () => Scripts.DynamoInvoker.RunGraph(uiApp, dynPath)) ?? "(threw)";
            Append("B: result=" + result);
            try { docB.Close(false); } catch { /* background doc — best effort */ }
        }
        catch (Exception ex) { Append("B: open/run threw: " + ex); }

        Append("open docs AFTER: " + OpenDocTitles(uiApp.Application));
        Append("=== Dynamo spike complete — see the graph's own marker file for the doc it saw ===");
    }
}
