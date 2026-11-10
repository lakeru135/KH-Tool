using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KhBatchRunner.Forma;
using KhBatchRunner.Actions;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Diagnostics;

public static partial class SelfTest
{
    /// <summary>
    /// Live Initiate-to-Forma test: find the target folder by name, find a source model in another
    /// folder, and publish a copy into the target via the real <see cref="InitiateToFormaAction"/>
    /// through <see cref="BatchEngine.ProcessModel"/> (one model, in this Idling tick). Verifies the
    /// SaveAs-local + SaveAsCloudModel flow end to end against the live account.
    /// </summary>
    private static void RunInitiateTest(UIApplication? uiApp, string targetName)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log */ }
        Append($"=== Initiate test {DateTime.Now:yyyy-MM-dd HH:mm:ss} -> target folder '{targetName}' ===");
        if (uiApp is null) { Append("No UIApplication. Aborting."); return; }

        var tokens = new SessionTokenProvider();
        string token = tokens.GetAccessToken();
        Append($"Token: length={token?.Length ?? 0}; LastError={tokens.LastError ?? "(none)"}");
        if (string.IsNullOrEmpty(token)) { Append("No token. Aborting."); return; }

        var dm = new DataManagementClient(tokens);
        List<FormaEntry> hubs = Step("GetHubs", () => dm.GetHubs()) ?? new();
        if (hubs.Count == 0) { Append("No hubs. Aborting."); return; }

        FormaEntry? target = FindFolderByName(dm, hubs, targetName, probeBudget: 80);
        if (target is null) { Append($"Target folder '{targetName}' not found within budget. Aborting."); return; }
        Append($"Target folder: name={target.Name} | hubId={target.HubId} | projectId={target.ProjectId} | urn={target.Id} | region={target.Region}");

        (FormaEntry Folder, List<FormaModelItem> Models)? source = FindFolderWithModels(dm, hubs, probeBudget: 40);
        if (source is null) { Append("No source folder with models found. Aborting."); return; }
        // Optional KHBATCH_INITIATE_SOURCE picks a specific source by name (e.g. the smaller model
        // for a faster upload); default is the first.
        string? srcName = Environment.GetEnvironmentVariable("KHBATCH_INITIATE_SOURCE");
        FormaModelItem m0 = (!string.IsNullOrWhiteSpace(srcName)
            ? source.Value.Models.FirstOrDefault(m => string.Equals(m.DisplayName, srcName, StringComparison.OrdinalIgnoreCase))
            : null) ?? source.Value.Models[0];
        Append($"Source model: {m0.DisplayName} | pGuid={m0.ProjectGuid} | mGuid={m0.ModelGuid} | region={m0.Region}");

        // Three naming scenarios in one run (source '<TEST-ST>'); a shared run token keeps names
        // unique across re-runs. ProcessModel is called directly here (not via BatchIdlingDriver),
        // so it has no dialog suppression of its own — SaveAs/SaveAsCloudModel can pop a modal that
        // would hang an unattended run; suppress for the duration, exactly as the real driver does.
        string run = DateTime.Now.ToString("HHmmss");
        var scenarios = new (string Label, string Prefix, PublishPrefixMode Mode, string Suffix)[]
        {
            ("rename-prefix", "RN", PublishPrefixMode.Rename, run),
            ("add-prefix",    "AP", PublishPrefixMode.Add,    run),
            ("add-suffix",    "",   PublishPrefixMode.Add,    "SX" + run),
        };
        string destFolder = Path.Combine(TestRoot,"initiatetest");

        foreach ((string label, string prefix, PublishPrefixMode mode, string suffix) in scenarios)
        {
            Append($"-- scenario '{label}': source='{m0.DisplayName}' prefix='{prefix}' mode={mode} suffix='{suffix}' sep='-' --");
            PublishTarget publishTarget;
            try { publishTarget = PublishTarget.FromFolder(target, target.Name, prefix, suffix, "-", mode); }
            catch (Exception ex) { Append("  PublishTarget.FromFolder failed: " + ex.Message); continue; }

            var action = new InitiateToFormaAction(publishTarget);
            var engine = new BatchEngine(uiApp, action, destFolder, "InitiateTest_" + label);
            try
            {
                using var suppressor = new DialogSuppressor(uiApp);
                BatchRunState state = engine.BeginRun();
                engine.ProcessModel(m0.ToCloudModel(), state);
                Append($"  result: {state.Result.SucceededCount} ok / {state.Result.FailedCount} failed");
                try { foreach (string l in File.ReadAllLines(engine.DetailLogPath)) Append("  detail: " + l); } catch { }
                try { foreach (string l in File.ReadAllLines(engine.TrackingLogPath)) Append("  track: " + l); } catch { }
            }
            catch (Exception ex) { Append("  ProcessModel threw: " + ex); }
        }

        // Best-effort verification: re-list the target folder's models.
        Append("-- target folder now contains --");
        try { foreach (FormaModelItem m in dm.GetRevitModels(target)) Append("  " + m.DisplayName); }
        catch (Exception ex) { Append("  (re-list failed: " + ex.Message + ")"); }

        Append("=== Initiate test complete ===");
    }

    /// <summary>
    /// Close test: run a read-only Check over the cloud models via the real engine, then list the
    /// still-open documents — verifies the model actually closes (with the relinquish-before-close
    /// fix) rather than staying stuck open and forcing a Revit force-quit.
    /// </summary>
    private static void RunCloseTest(UIApplication? uiApp)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { }
        Append($"=== Close test {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        if (uiApp is null) { Append("No UIApplication. Aborting."); return; }

        var tokens = new SessionTokenProvider();
        string token = tokens.GetAccessToken();
        Append($"Token: length={token?.Length ?? 0}; LastError={tokens.LastError ?? "(none)"}");
        if (string.IsNullOrEmpty(token)) { Append("No token. Aborting."); return; }

        var dm = new DataManagementClient(tokens);
        List<FormaEntry> hubs = Step("GetHubs", () => dm.GetHubs()) ?? new();
        (FormaEntry Folder, List<FormaModelItem> Models)? source = FindFolderWithModels(dm, hubs, probeBudget: 40);
        if (source is null) { Append("No source folder with models. Aborting."); return; }

        List<FormaModelItem> models = source.Value.Models;
        Autodesk.Revit.ApplicationServices.Application app = uiApp.Application;
        Append("open docs BEFORE: " + OpenDocTitles(app));

        var action = new GetPhaseAndWorksetAction();
        var engine = new BatchEngine(uiApp, action, Path.Combine(TestRoot,"closetest"), "CloseTest");
        try
        {
            using var suppressor = new DialogSuppressor(uiApp);
            BatchRunState state = engine.BeginRun();
            foreach (FormaModelItem m in models)
            {
                Append($"  processing {m.DisplayName}…");
                engine.ProcessModel(m.ToCloudModel(), state);
            }
            Append($"processed {models.Count}; {state.Result.SucceededCount} ok / {state.Result.FailedCount} failed");
        }
        catch (Exception ex) { Append("ProcessModel threw: " + ex); }

        // The key check: are the cloud models still open? With the fix they should be closed.
        Append("open docs AFTER: " + OpenDocTitles(app));
        Append("=== Close test complete ===");
    }

    /// <summary>
    /// Leave-open test: runs a read-only Check over a MULTI-model list (>= 2, since the crash
    /// this fixes needs multiple active-document switches) via the real engine — same as
    /// <see cref="RunCloseTest"/> — then kicks the actual fix under test:
    /// <see cref="LeftOpenDocsActivator.Surface"/>, which re-opens every model with a window, one
    /// per <see cref="ExternalEvent"/> tick. Deliberately does NOT go through
    /// <see cref="BatchIdlingDriver.TryStart"/> — that subscribes its own Idling handler
    /// (<c>uiApp.Idling +=</c>), which Revit forbids from inside an executing Idling event (this
    /// method runs from one); <see cref="ExternalEvent.Raise"/> has no such restriction. The Check
    /// pass is run synchronously here (like <see cref="RunCloseTest"/>) purely to mimic a real
    /// batch before the disposition kicks in — the crash under test is entirely inside the
    /// re-open, not the Check.
    /// </summary>
    private static void RunLeaveOpenTest(UIApplication? uiApp, string mode)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== Leave-open test {DateTime.Now:yyyy-MM-dd HH:mm:ss} (mode={mode}) ===");
        if (uiApp is null) { Append("No UIApplication. Aborting."); End(); return; }

        List<CloudModel> models;
        try { models = BuildLeaveOpenModels(uiApp, mode); }
        catch (Exception ex) { Append("Building model list threw: " + ex); End(); return; }
        if (models.Count == 0) { Append("No models resolved for the leave-open test. Aborting."); End(); return; }

        _leaveOpenExpectedCount = models.Count;
        Append($"models ({models.Count}): " + string.Join(", ", models.Select(m => m.DisplayName)));
        Autodesk.Revit.ApplicationServices.Application app = uiApp.Application;
        Append("open docs BEFORE: " + OpenDocTitles(app));

        // Run a Check pass synchronously to mimic a real batch (each model opens+closes in
        // ProcessModel) before surfacing. NOT a `using` — this suppressor must outlive this
        // method through the async surface; LeaveOpenDone disposes it once the drain finishes.
        _leaveOpenSuppressor = new DialogSuppressor(uiApp);
        var action = new GetPhaseAndWorksetAction();
        var engine = new BatchEngine(uiApp, action, Path.Combine(TestRoot,"leaveopentest"), "LeaveOpenTest");
        try
        {
            BatchRunState state = engine.BeginRun();
            foreach (CloudModel model in models)
            {
                Append($"  processing {model.DisplayName}…");
                engine.ProcessModel(model, state);
            }
            Append($"Check pass: {state.Result.SucceededCount} ok / {state.Result.FailedCount} failed");
        }
        catch (Exception ex) { Append("Check pass threw: " + ex); }

        // Kick the fix under test. LeaveOpenDone (the onAllSurfaced callback) fires once every
        // model has been re-opened — that's when it's safe to inspect the open documents and
        // End() the test; _deferEnd (set by OnIdling) keeps the outer finally from ending early.
        LeftOpenDocsActivator.Surface(models, onAllSurfaced: () => LeaveOpenDone(uiApp!));
    }

    /// <summary>
    /// Builds the multi-model list for <see cref="RunLeaveOpenTest"/>: local paths (from
    /// <c>KHBATCH_LEAVEOPEN_MODELS</c>, ';'-separated, or <paramref name="mode"/> == "local"'s
    /// fallback pair) when local, otherwise the first few models from the same
    /// <see cref="FindFolderWithModels"/> hub/folder probe <see cref="RunCloseTest"/> uses.
    /// </summary>
    private static List<CloudModel> BuildLeaveOpenModels(UIApplication uiApp, string mode)
    {
        string? envPaths = Environment.GetEnvironmentVariable("KHBATCH_LEAVEOPEN_MODELS");
        bool wantLocal = !string.IsNullOrWhiteSpace(envPaths)
            || string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase);

        if (wantLocal)
        {
            string[] paths = !string.IsNullOrWhiteSpace(envPaths)
                ? envPaths!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                : new[]
                {
                    Path.Combine(TestRoot, "models", "TestA.rvt"),
                    Path.Combine(TestRoot, "models", "TestB.rvt")
                };
            return paths
                .Select(p => new CloudModel { DisplayName = Path.GetFileName(p), LocalPath = p })
                .ToList();
        }

        var tokens = new SessionTokenProvider();
        string token = tokens.GetAccessToken();
        Append($"Token: length={token?.Length ?? 0}; LastError={tokens.LastError ?? "(none)"}");
        if (string.IsNullOrEmpty(token))
        {
            Append("No token — Revit isn't signed in to Forma in this (headless) session.");
            return new List<CloudModel>();
        }

        var dm = new DataManagementClient(tokens);
        List<FormaEntry> hubs = Step("GetHubs", () => dm.GetHubs()) ?? new();
        (FormaEntry Folder, List<FormaModelItem> Models)? found = FindFolderWithModels(dm, hubs, probeBudget: 40);
        if (found is null) return new List<CloudModel>();

        return found.Value.Models.Take(3).Select(m => m.ToCloudModel()).ToList();
    }

    /// <summary>
    /// <see cref="LeftOpenDocsActivator"/>'s <c>onAllSurfaced</c> callback for the leave-open test:
    /// logs the now-open documents, PASS/FAILs on whether at least the expected count surfaced,
    /// disposes the Check pass's suppressor, and force-quits (test determinism — same as every
    /// other self-test path).
    /// </summary>
    private static void LeaveOpenDone(UIApplication uiApp)
    {
        Autodesk.Revit.ApplicationServices.Application app = uiApp.Application;
        Append("open docs AFTER surface: " + OpenDocTitles(app));

        int count = 0;
        try { foreach (Autodesk.Revit.DB.Document d in app.Documents) if (d.IsValidObject) count++; }
        catch (Exception ex) { Append("enumerate failed: " + ex.Message); }
        Append($"SURFACED_DOC_COUNT={count} (expected >= {_leaveOpenExpectedCount})");
        Append(count >= _leaveOpenExpectedCount ? "LEAVEOPEN_TEST: PASS" : "LEAVEOPEN_TEST: FAIL");

        _leaveOpenSuppressor?.Dispose();
        _leaveOpenSuppressor = null;
        End(); // force-quit for test determinism, same as every other self-test path.
    }

    /// <summary>
    /// Driver test: exercises the real interactive <see cref="BatchIdlingDriver"/> end to end — the
    /// path NO other probe covers — for a given post-run disposition (env <c>KHBATCH_DRIVER_TEST</c>
    /// = <c>close</c> | <c>leaveopen</c> | <c>closerevit</c>). Runs a read-only Check over the cloud
    /// models through the driver's own Idling queue, then checks the disposition the driver applied:
    /// "close" must leave NO document open and pop no modal (finishes silently), "leaveopen" must
    /// surface the models (>= expected count). Because <see cref="BatchIdlingDriver.TryStart"/>
    /// subscribes <c>uiApp.Idling +=</c> — illegal inside an executing Idling event, which this method
    /// runs from — TryStart is invoked from an <see cref="ExternalEvent"/> (a legal context).
    /// </summary>
    private static void RunDriverTest(UIApplication? uiApp, string modeStr)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== Driver test {DateTime.Now:yyyy-MM-dd HH:mm:ss} (completion={modeStr}) ===");
        if (uiApp is null) { Append("No UIApplication. Aborting."); End(); return; }

        BatchCompletion completion = modeStr.Trim().ToLowerInvariant() switch
        {
            "leaveopen" => BatchCompletion.LeaveOpen,
            "closerevit" => BatchCompletion.CloseRevit,
            _ => BatchCompletion.CloseDocuments
        };

        List<CloudModel> models;
        try { models = BuildLeaveOpenModels(uiApp, "cloud"); }
        catch (Exception ex) { Append("Building models threw: " + ex); End(); return; }
        if (models.Count == 0) { Append("No models resolved for the driver test. Aborting."); End(); return; }
        _driverExpectedCount = models.Count;
        Append($"models ({models.Count}): " + string.Join(", ", models.Select(m => m.DisplayName)));

        var preset = new ModelPreset { Name = "DriverTest", Models = models };
        var engine = new BatchEngine(uiApp, new GetPhaseAndWorksetAction(),
            Path.Combine(TestRoot,"drivertest"), "DriverTest");

        // Escape the Idling context: BatchIdlingDriver.TryStart does uiApp.Idling += ..., forbidden
        // from inside an executing Idling event. An ExternalEvent's Execute is a legal context.
        _driverHandler = new RelayHandler(app =>
        {
            Append("open docs BEFORE: " + OpenDocTitles(app.Application));
            try
            {
                bool started = BatchIdlingDriver.TryStart(
                    app, new[] { (engine, preset) }, "DriverTest",
                    onComplete: () =>
                    {
                        // Fires once the queue drains, BEFORE the disposition switch and — the fix —
                        // with NO modal shown first. For close/closerevit the models are already
                        // closed here, so assert now; leave-open waits for onSurfaced (below).
                        Append("onComplete fired (queue drained, no modal before disposition)");
                        if (completion != BatchCompletion.LeaveOpen)
                            DriverDone(app, completion);
                    },
                    completion: completion,
                    onSurfaced: () => DriverDone(app, completion));
                Append("TryStart returned: " + started);
                if (!started) { Append("TryStart refused (already running?). Aborting."); DriverDone(app, completion); }
            }
            catch (Exception ex) { Append("TryStart threw: " + ex); DriverDone(app, completion); }
        });
        try { _driverEvent = ExternalEvent.Create(_driverHandler); _driverEvent.Raise(); }
        catch (Exception ex) { Append("ExternalEvent.Create/Raise threw: " + ex); End(); }
    }

    /// <summary>Driver-test verdict + teardown: logs the open docs after the disposition, PASS/FAILs it, then requests the shared normal Revit close. Guarded so the two callbacks (onComplete for close, onSurfaced for leave-open) can only settle it once.</summary>
    private static bool _driverDone;
    private static void DriverDone(UIApplication uiApp, BatchCompletion completion)
    {
        if (_driverDone) return;
        _driverDone = true;

        Autodesk.Revit.ApplicationServices.Application app = uiApp.Application;
        Append("open docs AFTER disposition: " + OpenDocTitles(app));
        int count = 0;
        try { foreach (Autodesk.Revit.DB.Document d in app.Documents) if (d.IsValidObject) count++; }
        catch (Exception ex) { Append("enumerate failed: " + ex.Message); }

        if (completion == BatchCompletion.LeaveOpen)
            Append(count >= _driverExpectedCount
                ? $"DRIVER_TEST(leaveopen): PASS ({count} open >= {_driverExpectedCount}, no modal)"
                : $"DRIVER_TEST(leaveopen): FAIL ({count} open < {_driverExpectedCount})");
        else
            Append(count == 0
                ? "DRIVER_TEST(close): PASS (no docs left open, no modal)"
                : $"DRIVER_TEST(close): FAIL ({count} still open)");

        Append("=== Driver test complete ===");
        try { _driverEvent?.Dispose(); } catch { /* best effort */ }
        _driverEvent = null;
        _driverHandler = null;
        End();
    }

    /// <summary>Trivial ExternalEvent handler that runs a supplied action — lets the driver test call BatchIdlingDriver.TryStart from a legal (non-Idling) context.</summary>
    private sealed class RelayHandler : IExternalEventHandler
    {
        private readonly Action<UIApplication> _action;
        public RelayHandler(Action<UIApplication> action) => _action = action;
        public void Execute(UIApplication app) => _action(app);
        public string GetName() => "KhBatchRunner self-test: driver relay";
    }
}
