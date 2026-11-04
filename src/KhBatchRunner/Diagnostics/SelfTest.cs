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

/// <summary>
/// Headless self-test of the Forma REST stack (token → crawler → file log → inventory → version
/// URN → Model Derivative), used to reproduce/diagnose failures against a real signed-in Forma
/// session without clicking through the UI. Activated only when Revit is launched with the
/// <c>KHBATCH_SELFTEST</c> env var set to an output log path; arms a one-shot Idling handler
/// from <c>OnStartup</c> (a legal context, mirroring <see cref="Schedule.ScheduledRunner"/>),
/// writes each step (and any exception, with full stack) to the log <b>incrementally</b> so a
/// hard crash still leaves the trail up to the failing step, then requests a normal Revit close.
/// </summary>
public static class SelfTest
{
    private const string EnvVar = "KHBATCH_SELFTEST";

    /// <summary>Root of the local test models and probe output: %USERPROFILE%\KhBatchTest unless
    /// KHBATCH_TEST_ROOT says otherwise.</summary>
    internal static string TestRoot =>
        Environment.GetEnvironmentVariable("KHBATCH_TEST_ROOT") is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "KhBatchTest");

    private static UIControlledApplication? _app;
    private static EventHandler<IdlingEventArgs>? _handler;
    private static string _logPath = "";
    private static bool _ran;

    // ---- leave-open test state (see RunLeaveOpenTest / LeaveOpenDone) ----------------------
    // The leave-open test surfaces its models asynchronously (one ExternalEvent tick per model,
    // via LeftOpenDocsActivator) and only finishes — and calls End() — once that drain's
    // onAllSurfaced callback fires. _deferEnd tells the outer OnIdling not to End() early.
    private static bool _deferEnd;
    private static DialogSuppressor? _leaveOpenSuppressor;
    private static int _leaveOpenExpectedCount;

    // ---- driver test state (see RunDriverTest) --------------------------------------------
    // The driver test exercises the real interactive BatchIdlingDriver.Complete() disposition
    // (no existing probe does). BatchIdlingDriver.TryStart subscribes uiApp.Idling +=, which
    // Revit forbids from inside an executing Idling event — so TryStart is called from an
    // ExternalEvent (a legal context), kept alive in these static fields for the run's duration.
    private static ExternalEvent? _driverEvent;
    private static IExternalEventHandler? _driverHandler;
    private static int _driverExpectedCount;

    public static bool IsRequested(out string logPath)
    {
        // Newest gate first: a launcher that wants ONLY a freshly-built dev build to arm sets the
        // highest version name — older editions in the same Addins folder (a stale lite edition or an
        // installed ProgramData build) know only an earlier name, so they stay dormant instead of
        // racing this build for the same log file and forcibly closing Revit mid-run (which
        // silently ate the relink spike three times). V3 was added for the Initiate publish spike:
        // the currently-installed editions know V2 at most, so gating on V3 isolates this build.
        // V4 was added for the Bridge spike (same isolation story, one version later).
        // V5 isolates a dev build carrying enriched relink error reporting from the installed 0.9.0
        // editions (which know V4 at most) so only it arms — avoids the two-edition race.
        // V6 isolates the 1.0.1 seed-capable publish spike: the installed 1.0.0 editions know V5
        // at most, so gating on V6 keeps them dormant (BOTH editions are installed side-by-side,
        // and a V3 launch would arm both — the old one racing this build for the same log).
        // V7 isolates the 1.3.4 no-forced-close/cloud-serialization verification from an older
        // 1.3.4 candidate that may still be installed alongside the development manifest.
        // V8 isolates the 1.5.1.2 Data Connector auth probe: every installed edition knows V7 at
        // most, so only the dev build carrying the probe arms and claims the log.
        // V9 isolates the Interop profile/KHBATCH_TEST_ROOT build: DRW Helper is built from this
        // same SelfTest and its installed copy knows V8, so a V8 launch would arm it too.
        logPath = Environment.GetEnvironmentVariable("KHBATCH_SELFTEST_V9")
                  ?? Environment.GetEnvironmentVariable("KHBATCH_SELFTEST_V8")
                  ?? Environment.GetEnvironmentVariable("KHBATCH_SELFTEST_V7")
                  ?? Environment.GetEnvironmentVariable("KHBATCH_SELFTEST_V6")
                  ?? Environment.GetEnvironmentVariable("KHBATCH_SELFTEST_V5")
                  ?? Environment.GetEnvironmentVariable("KHBATCH_SELFTEST_V4")
                  ?? Environment.GetEnvironmentVariable("KHBATCH_SELFTEST_V3")
                  ?? Environment.GetEnvironmentVariable("KHBATCH_SELFTEST_V2")
                  ?? Environment.GetEnvironmentVariable(EnvVar) ?? "";
        return !string.IsNullOrWhiteSpace(logPath);
    }

    public static void ArmOnIdling(UIControlledApplication app, string logPath)
    {
        _app = app;
        _logPath = logPath;
        _ran = false;
        _handler = OnIdling;
        app.Idling += _handler; // legal: OnStartup is not itself an Idling event
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (_ran) return;
        _ran = true;
        try
        {
            // If KHBATCH_INITIATE_TEST=<targetFolderName> is set, run a live Initiate-to-Forma test
            // (publishes a copy of a source model into that folder); otherwise the read-only probe.
            string? initiateTarget = Environment.GetEnvironmentVariable("KHBATCH_INITIATE_TEST");
            string? closeTest = Environment.GetEnvironmentVariable("KHBATCH_CLOSE_TEST");
            string? leaveOpen = Environment.GetEnvironmentVariable("KHBATCH_LEAVEOPEN_TEST");
            string? driverTest = Environment.GetEnvironmentVariable("KHBATCH_DRIVER_TEST");
            string? dynSpike = Environment.GetEnvironmentVariable("KHBATCH_DYN_SPIKE");
            string? interopSpike = Environment.GetEnvironmentVariable("KHBATCH_INTEROP_SPIKE");
            string? relinkSpike = Environment.GetEnvironmentVariable("KHBATCH_RELINK_SPIKE");
            string? initiateSpike = Environment.GetEnvironmentVariable("KHBATCH_INITIATE_SPIKE");
            string? publishSpike = Environment.GetEnvironmentVariable("KHBATCH_PUBLISH_SPIKE");
            string? bridgeSpike = Environment.GetEnvironmentVariable("KHBATCH_BRIDGE_SPIKE");
            string? dcSpike = Environment.GetEnvironmentVariable("KHBATCH_DC_SPIKE");
            // REST only — no document is opened, so unlike the cloud spikes this one is safe to run
            // straight from Idling rather than from an ExternalEvent.
            if (!string.IsNullOrWhiteSpace(dcSpike))
                RunDataConnectorSpike();
            else if (!string.IsNullOrWhiteSpace(bridgeSpike))
                RunBridgeSpike(sender as UIApplication);
            else if (!string.IsNullOrWhiteSpace(relinkSpike))
                RunRelinkSpike(sender as UIApplication);
            else if (!string.IsNullOrWhiteSpace(initiateSpike))
                RunInitiatePublishSpike(sender as UIApplication);
            else if (!string.IsNullOrWhiteSpace(publishSpike))
                RunPublishSpike(sender as UIApplication);
            else if (!string.IsNullOrWhiteSpace(interopSpike))
                RunInteropSpike(sender as UIApplication, interopSpike!);
            else if (!string.IsNullOrWhiteSpace(dynSpike))
                RunDynSpike(sender as UIApplication, dynSpike!);
            else if (!string.IsNullOrWhiteSpace(initiateTarget))
                RunInitiateTest(sender as UIApplication, initiateTarget!);
            else if (!string.IsNullOrWhiteSpace(driverTest))
            {
                // The driver drains asynchronously (its own Idling loop) and disposes via an
                // ExternalEvent — defer End() to DriverDone instead of the finally below.
                _deferEnd = true;
                RunDriverTest(sender as UIApplication, driverTest!);
            }
            else if (!string.IsNullOrEmpty(leaveOpen))
            {
                // Leave-open re-open path finishes asynchronously (LeftOpenDocsActivator's own
                // ExternalEvent drain) — defer End() to LeaveOpenDone instead of the finally below.
                _deferEnd = true;
                RunLeaveOpenTest(sender as UIApplication, leaveOpen!);
            }
            else if (!string.IsNullOrWhiteSpace(closeTest))
                RunCloseTest(sender as UIApplication);
            else
                Probe(sender as UIApplication);
        }
        catch (Exception ex) { Append("FATAL (unhandled): " + ex); }
        finally { if (!_deferEnd) End(); }
    }

    private static void Probe(UIApplication? uiApp)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== KhBatchRunner self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

        var tokens = new SessionTokenProvider();
        string token = "";
        try { token = tokens.GetAccessToken(); }
        catch (Exception ex) { Append("Token threw: " + ex); }
        Append($"Token: length={token?.Length ?? 0}; LastError={tokens.LastError ?? "(none)"}");
        if (string.IsNullOrEmpty(token))
        {
            Append("No token — Revit isn't signed in to Forma in this (headless) session. Aborting.");
            return;
        }

        var dm = new DataManagementClient(tokens);

        List<FormaEntry> hubs = Step("GetHubs", () => dm.GetHubs()) ?? new();
        foreach (FormaEntry h in hubs.Take(5))
            Append($"  hub: {h.Name} | region={h.Region} | id={h.Id}");
        if (hubs.Count == 0) { Append("No hubs. Aborting."); return; }

        // Breadth-first hunt for the first folder that contains at least one .rvt, bounded so a
        // big account can't make this run forever.
        (FormaEntry Folder, List<FormaModelItem> Models)? found = FindFolderWithModels(dm, hubs, probeBudget: 40);
        if (found is null) { Append("No folder with .rvt models found within the probe budget. Aborting."); return; }

        (FormaEntry folder, List<FormaModelItem> models) = found.Value;
        Append($"Chosen folder: projectId={folder.ProjectId} | region={folder.Region} | id={folder.Id}");
        Append($"  models in folder: {models.Count}; scan note: {dm.LastScanNote}");
        FormaModelItem m0 = models[0];
        Append($"  MODEL[0]: name={m0.DisplayName} | projectGuid={m0.ProjectGuid} | modelGuid={m0.ModelGuid} | region={m0.Region}");
        Append("  ^^^ use these GUIDs to hand-author a cloud preset for a real batch Check test.");

        Step("GetFileLog(folder)", () =>
        {
            List<FormaFileLogRow> rows = dm.GetFileLog(folder, includeSubfolders: false);
            Append($"    file-log rows: {rows.Count}");
            foreach (FormaFileLogRow r in rows.Take(3))
                Append($"      v{r.VersionNumber} {r.FileName} [{r.FileType}] | {r.LastModified} | by {r.ModifiedBy} | state={r.ProcessingState} | size={r.StorageSize}");
            return rows;
        });

        Step("GetFolderInventory(folder)", () =>
        {
            List<FormaInventoryRow> rows = dm.GetFolderInventory(folder, includeSubfolders: false);
            Append($"    inventory rows: {rows.Count}");
            foreach (FormaInventoryRow r in rows.Take(3))
                Append($"      {r.ModelName} | published={r.Published} | v{r.LatestVersion} | pGuid={r.ProjectGuid} | mGuid={r.ModelGuid}");
            return rows;
        });

        List<FormaModelRef>? refs = Step("GetRevitItemRefs(folder)", () => dm.GetRevitItemRefs(folder, includeSubfolders: false));
        if (refs is { Count: > 0 })
        {
            var md = new ModelDerivativeClient(tokens);
            foreach (FormaModelRef r in refs)
            {
                Append($"    ref: {r.DisplayName} | versionUrn={r.VersionUrn}");
                IReadOnlyList<string>? sheets = Step($"MD sheets ({r.DisplayName})",
                    () => md.GetPublishedSheetViewNames(r.VersionUrn, r.Region));
                if (sheets is null) Append($"      Model Derivative: NULL for {r.DisplayName}");
                else
                {
                    Append($"      2D sheet views: {sheets.Count}");
                    foreach (string s in sheets.Take(12)) Append($"        view: {s}");
                }
            }
            // Raw manifest of the first model, to see the actual status behind a NULL.
            Append("    manifest[0] raw: " + md.ManifestDiagnostic(refs[0].VersionUrn, refs[0].Region));
        }

        Append("=== self-test complete ===");
    }

    /// <summary>Breadth-first search across hubs/projects/top-folders for the first folder with a .rvt, bounded by <paramref name="probeBudget"/> folder scans.</summary>
    private static (FormaEntry, List<FormaModelItem>)? FindFolderWithModels(
        DataManagementClient dm, List<FormaEntry> hubs, int probeBudget)
    {
        var queue = new Queue<FormaEntry>();
        foreach (FormaEntry hub in hubs.Take(3))
        {
            List<FormaEntry> projects = Step($"GetProjects({hub.Name})", () => dm.GetProjects(hub)) ?? new();
            Append($"  hub '{hub.Name}': {projects.Count} project(s)");
            foreach (FormaEntry proj in projects.Take(8))
            {
                List<FormaEntry> tops = Step($"GetTopFolders({proj.Name})", () => dm.GetTopFolders(proj)) ?? new();
                foreach (FormaEntry t in tops) queue.Enqueue(t);
            }
        }

        int probes = 0;
        while (queue.Count > 0 && probes < probeBudget)
        {
            FormaEntry folder = queue.Dequeue();
            probes++;
            List<FormaModelItem> models;
            try { models = dm.GetRevitModels(folder); }
            catch (Exception ex) { Append($"  probe '{folder.Name}' threw: {ex.Message}"); continue; }

            Append($"  probe #{probes} '{folder.Name}': {models.Count} model(s)");
            if (models.Count > 0) return (folder, models);

            try { foreach (FormaEntry sub in dm.GetSubFolders(folder)) queue.Enqueue(sub); }
            catch (Exception ex) { Append($"    subfolders of '{folder.Name}' threw: {ex.Message}"); }
        }
        return null;
    }

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

    /// <summary>
    /// Internal Relink spike (<c>KHBATCH_RELINK_SPIKE=1</c>): replays the user's LAST Internal
    /// Relink inputs (<c>relink-lastused.json</c> — target folders, old→new codes, host model set)
    /// through the REAL engine+action (the same ProcessModel/Close-files path the interactive run
    /// took), then REOPENS each host and dumps every top-level link's name + referenced cloud model
    /// GUID to prove whether the relink PERSISTED. This closes the workset loop: it verifies data
    /// state (reliable headlessly), not UI behavior.
    /// </summary>
    private static ExternalEvent? _relinkSpikeEvent;
    private static IExternalEventHandler? _relinkSpikeHandler;

    private static void RunRelinkSpike(UIApplication? uiApp)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== Internal Relink spike {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        if (uiApp is null) { Append("No UIApplication. Aborting."); return; }

        // Cloud opens from a synchronous Idling body crash natively (tried once: Revit died mid
        // ProcessModel with nothing logged — the same crash class the modal-runner work documented).
        // So the body runs from an ExternalEvent like the other spikes. The old-edition SelfTest
        // race that once ate this deferral is gone: launchers arm this build via KHBATCH_SELFTEST_V2,
        // which stale editions don't know.
        _deferEnd = true;
        _relinkSpikeHandler = new RelayHandler(app =>
        {
            try { RelinkSpikeBody(app); }
            catch (Exception ex) { Append("spike body threw: " + ex); }
            finally
            {
                try { _relinkSpikeEvent?.Dispose(); } catch { /* best effort */ }
                _relinkSpikeEvent = null;
                _relinkSpikeHandler = null;
                End();
            }
        });
        try { _relinkSpikeEvent = ExternalEvent.Create(_relinkSpikeHandler); _relinkSpikeEvent.Raise(); }
        catch (Exception ex) { Append("ExternalEvent.Create/Raise threw: " + ex); End(); }
    }

    private static void RelinkSpikeBody(UIApplication uiApp)
    {
        using var suppressor = new DialogSuppressor(uiApp);

        RelinkInputSet? inputs = RelinkSetStore.TryLoadLastUsed();
        if (inputs is null) { Append("No relink-lastused.json — run the Internal Relink dialog once first."); return; }
        Append($"inputs: '{inputs.OldCode}' → '{inputs.NewCode}' (sep '{inputs.Separator}'), " +
               $"{inputs.TargetFolders.Count} target folder(s), host set '{inputs.ModelSetName}'");

        var client = new DataManagementClient(new SessionTokenProvider());

        ModelPreset? preset;
        if (inputs.HostsFromFolder)
        {
            preset = Commands.RelinkModelCommand.BuildHostPreset(client, inputs);
            Append($"hosts from folder '{inputs.HostFolder?.DisplayPath}' (subfolders: {inputs.HostIncludeSubfolders})");
        }
        else
        {
            preset = PresetStore.LoadAll()
                .FirstOrDefault(p => p.Name.Equals(inputs.ModelSetName, StringComparison.OrdinalIgnoreCase));
            if (preset is null) { Append($"Model set '{inputs.ModelSetName}' not found. Aborting."); return; }
        }
        if (preset.Models.Count == 0) { Append("No host models. Aborting."); return; }
        Append("hosts: " + string.Join(", ", preset.Models.Select(m => m.DisplayName)));
        Dictionary<string, List<Actions.RelinkTarget>> targets =
            Commands.RelinkModelCommand.BuildTargetMap(client, inputs, out int multiName);
        Append($"target scan: {targets.Values.Sum(l => l.Count)} model(s), {targets.Count} name(s) " +
               $"({multiName} name(s) with several candidates — resolved per link)");
        foreach (Actions.RelinkTarget t in targets.Values.SelectMany(l => l))
            Append($"  target '{t.DisplayName}' modelGuid={t.ModelGuid} projectGuid={t.ProjectGuid} region={t.Region} ({t.FolderPath})");
        if (targets.Count == 0) { Append("No targets — nothing to relink. Aborting."); return; }

        // --- RUN the real engine + action (ProcessModel = the Close-files path) ---
        string dest = Path.Combine(Path.GetTempPath(), "KhBatchRunner", "relinkspike");
        Directory.CreateDirectory(dest);
        var action = new Actions.RelinkLinksAction(inputs.OldCode, inputs.NewCode, inputs.Separator,
            inputs.MatchByExactName, targets);
        var engine = new BatchEngine(uiApp, action, dest, "Relink Spike");
        BatchRunState state = engine.BeginRun();
        foreach (CloudModel model in preset.Models)
        {
            // Lingering documents (e.g. a previous host's freshly-loaded LINKS still in session)
            // are the prime suspect for "first model relinks, later ones say closed workset".
            Append("   open docs BEFORE: " + OpenDocTitles(uiApp.Application));
            Append($"-- ProcessModel {model.DisplayName} …");
            engine.ProcessModel(model, state);
            Append("   note: " + (string.IsNullOrEmpty(action.PerModelNote) ? "(none)" : action.PerModelNote));
        }
        Append("   open docs AFTER RUN: " + OpenDocTitles(uiApp.Application));
        Append($"run done: ok={state.Result.SucceededCount} fail={state.Result.FailedCount}");
        Append("tracking log: " + engine.TrackingLogPath);

        // --- VERIFY persistence: fresh reopen (closed worksets — reading is fine), dump links ---
        int stillOld = 0, nowTargets = 0;
        foreach (CloudModel model in preset.Models)
        {
            Append($"-- VERIFY {model.DisplayName}");
            Autodesk.Revit.DB.Document? doc = null;
            try
            {
                Autodesk.Revit.DB.ModelPath path = Autodesk.Revit.DB.ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                    string.Equals(model.Region, "EMEA", StringComparison.OrdinalIgnoreCase)
                        ? Autodesk.Revit.DB.ModelPathUtils.CloudRegionEMEA
                        : Autodesk.Revit.DB.ModelPathUtils.CloudRegionUS,
                    model.ProjectGuid, model.ModelGuid);
                var opts = new Autodesk.Revit.DB.OpenOptions();
                opts.SetOpenWorksetsConfiguration(new Autodesk.Revit.DB.WorksetConfiguration(
                    Autodesk.Revit.DB.WorksetConfigurationOption.CloseAllWorksets));
                doc = uiApp.Application.OpenDocumentFile(path, opts);

                foreach (Autodesk.Revit.DB.RevitLinkType t in
                         new Autodesk.Revit.DB.FilteredElementCollector(doc)
                             .OfClass(typeof(Autodesk.Revit.DB.RevitLinkType))
                             .Cast<Autodesk.Revit.DB.RevitLinkType>())
                {
                    if (t.IsNestedLink) continue;
                    string target = DescribeLinkTarget(t, targets, out bool pointsAtTarget);
                    // A link whose name still remaps under old→new was NOT relinked.
                    bool looksOld = t.Name.StartsWith(inputs.OldCode + inputs.Separator, StringComparison.OrdinalIgnoreCase);
                    if (pointsAtTarget) nowTargets++;
                    else if (looksOld && targets.ContainsKey(Actions.RelinkLinksAction.Normalize(
                                 inputs.NewCode + t.Name.Substring(inputs.OldCode.Length)))) stillOld++;
                    Append($"   link '{t.Name}' | {target}" + (pointsAtTarget ? "  << SCANNED TARGET" : ""));
                }
            }
            catch (Exception ex) { Append("   verify open failed: " + ex.Message); }
            finally
            {
                try
                {
                    if (doc != null && doc.IsValidObject)
                    {
                        try
                        {
                            Autodesk.Revit.DB.WorksharingUtils.RelinquishOwnership(doc,
                                new Autodesk.Revit.DB.RelinquishOptions(true),
                                new Autodesk.Revit.DB.TransactWithCentralOptions());
                        }
                        catch { /* read-only inspection — nothing borrowed */ }
                        doc.Close(false);
                    }
                }
                catch { /* best effort */ }
            }
        }
        Append($"=== relink spike VERDICT: {nowTargets} link(s) point at scanned targets; {stillOld} still old ⇒ " +
               (stillOld == 0 && nowTargets > 0 ? "PASS" : "FAIL") + " ===");
    }

    /// <summary>The cloud model a link type currently references, read from its external FILE
    /// reference (cloud paths carry the model GUID) or, for links stored as external RESOURCE
    /// references (BIM 360/Forma), from the reference-information dictionary.</summary>
    private static string DescribeLinkTarget(
        Autodesk.Revit.DB.RevitLinkType t,
        IReadOnlyDictionary<string, List<Actions.RelinkTarget>> targets,
        out bool pointsAtTarget)
    {
        // The GUID read itself is shared with the action (LinkCloudIdentity) so spike and feature
        // can never disagree about what a link references; this adds the descriptive dump.
        Guid? current = Actions.LinkCloudIdentity.TryGetCurrentModelGuid(t);
        pointsAtTarget = current is not null &&
                         targets.Values.Any(l => l.Any(x => x.ModelGuid == current.Value));
        try
        {
            if (t.IsExternalFileReference())
            {
                Autodesk.Revit.DB.ExternalFileReference efr = t.GetExternalFileReference();
                Autodesk.Revit.DB.ModelPath mp = efr.GetAbsolutePath();
                if (mp != null && mp.CloudPath)
                    return $"fileRef cloudModelGuid={mp.GetModelGUID()}";
                if (mp != null)
                    return "fileRef path=" + Autodesk.Revit.DB.ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
            }
        }
        catch { /* fall through to resource references */ }

        try
        {
            foreach (var kv in t.GetExternalResourceReferences())
            {
                var info = kv.Value.GetReferenceInformation();
                // Cloud link references carry the model GUID as "LinkedModelModelId" (verified in
                // the live spike log); older/other providers may use "modelGuid".
                return "resourceRef {" + string.Join("; ", info.Select(p => p.Key + "=" + p.Value)) + "}";
            }
        }
        catch { /* no readable reference at all */ }
        return "(no readable target reference)";
    }

    /// <summary>
    /// Bridge diagnostic spike (<c>KHBATCH_BRIDGE_SPIKE=1</c>): answers the ONE open question that
    /// blocks relinking to incoming-Bridge models — what does the Data Management API actually return
    /// for a bridged item's tip version (extension.data GUIDs? whose projectGuid? nothing?). Dumps:
    /// (1) the RAW folder-contents JSON of the bridge folder (<c>KHBATCH_BRIDGE_FOLDER</c>, default
    /// "Test Bridge") — items + included versions, unfiltered; (2) what <see cref="DataManagementClient
    /// .GetFolderInventory"/> makes of the same folder (which rows get a ModelGuid and which are
    /// dropped); (3) each top-level link's reference info in the first host model of
    /// <c>KHBATCH_BRIDGE_HOSTFOLDER</c> (default "Test Model") — how the host stores the bridge link.
    /// Read-only: the host opens with all worksets closed and is closed without saving. Runs from an
    /// ExternalEvent (cloud opens from a synchronous Idling body crash natively).
    /// </summary>
    private static ExternalEvent? _bridgeSpikeEvent;
    private static IExternalEventHandler? _bridgeSpikeHandler;

    private static void RunBridgeSpike(UIApplication? uiApp)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== Bridge diagnostic spike {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        if (uiApp is null) { Append("No UIApplication. Aborting."); return; }

        _deferEnd = true;
        _bridgeSpikeHandler = new RelayHandler(app =>
        {
            try { BridgeSpikeBody(app); }
            catch (Exception ex) { Append("spike body threw: " + ex); }
            finally
            {
                try { _bridgeSpikeEvent?.Dispose(); } catch { /* best effort */ }
                _bridgeSpikeEvent = null;
                _bridgeSpikeHandler = null;
                End();
            }
        });
        try { _bridgeSpikeEvent = ExternalEvent.Create(_bridgeSpikeHandler); _bridgeSpikeEvent.Raise(); }
        catch (Exception ex) { Append("ExternalEvent.Create/Raise threw: " + ex); End(); }
    }

    private static void BridgeSpikeBody(UIApplication uiApp)
    {
        using var suppressor = new DialogSuppressor(uiApp);

        string bridgeFolderName = Environment.GetEnvironmentVariable("KHBATCH_BRIDGE_FOLDER") ?? "Test Bridge";
        string hostFolderName = Environment.GetEnvironmentVariable("KHBATCH_BRIDGE_HOSTFOLDER") ?? "Test Model";
        Append($"inputs: bridge folder '{bridgeFolderName}', host folder '{hostFolderName}'");

        var tokens = new SessionTokenProvider();
        string token = tokens.GetAccessToken();
        Append($"Token: length={token?.Length ?? 0}; LastError={tokens.LastError ?? "(none)"}");
        if (string.IsNullOrEmpty(token)) { Append("No token — Revit isn't signed in to Forma in this session. Aborting."); return; }

        var dm = new DataManagementClient(tokens);
        List<FormaEntry> hubs = Step("GetHubs", () => dm.GetHubs()) ?? new();
        if (hubs.Count == 0) { Append("No hubs. Aborting."); return; }

        // --- 1) RAW folder-contents JSON of the bridge folder --------------------------------
        FormaEntry? bridgeFolder = FindFolderByName(dm, hubs, bridgeFolderName, probeBudget: 80);
        if (bridgeFolder is null) { Append($"Bridge folder '{bridgeFolderName}' not found within budget. Aborting."); return; }
        Append($"bridge folder: name={bridgeFolder.Name} | projectId={bridgeFolder.ProjectId} | urn={bridgeFolder.Id} | region={bridgeFolder.Region}");
        try
        {
            int page = 0;
            foreach (string raw in dm.GetRawFolderContentsPages(bridgeFolder))
                Append($"-- RAW contents page {++page} --\n{raw}");
            if (page == 0) Append("-- RAW contents: (no pages) --");
        }
        catch (Exception ex) { Append("RAW contents dump FAILED: " + ex); }

        // --- 1b) RAW /versions of the FIRST item -------------------------------------------
        // The dump above shows items and tip versions. The file log's full-history mode reads
        // /versions, and that response is the only place a per-version links.webView.href can
        // appear — the link the URL column prefers over the one this product constructs. One item
        // is enough to answer "does Forma send it on this hub?".
        try
        {
            List<string> itemIds = dm.GetItemIds(bridgeFolder);
            Append($"-- items in folder: {itemIds.Count} --");
            if (itemIds.Count > 0)
            {
                Append($"-- RAW /versions of {itemIds[0]} --");
                int vpage = 0;
                foreach (string raw in dm.GetRawItemVersionsPages(bridgeFolder.ProjectId, itemIds[0]))
                {
                    Append($"-- versions page {++vpage} --\n{raw}");
                    if (vpage >= 2) break;   // two pages is plenty to see the shape
                }
                if (vpage == 0) Append("-- RAW /versions: (no pages) --");
            }
        }
        catch (Exception ex) { Append("RAW /versions dump FAILED: " + ex); }

        // --- 2) what the inventory (the relink target scan) makes of the same folder ---------
        try
        {
            List<FormaInventoryRow> inv = dm.GetFolderInventory(bridgeFolder, includeSubfolders: true);
            Append($"-- inventory sees {inv.Count} row(s) --");
            foreach (FormaInventoryRow r in inv)
                Append($"   '{r.ModelName}' modelGuid={r.ModelGuid} projectGuid={r.ProjectGuid} " +
                       $"region={r.Region} published={r.Published} v{r.LatestVersion?.ToString() ?? "?"} ({r.FolderPath})");
        }
        catch (Exception ex) { Append("inventory FAILED: " + ex); }

        // --- 3) how the HOST stores its links (incl. the bridge link) ------------------------
        FormaEntry? hostFolder = FindFolderByName(dm, hubs, hostFolderName, probeBudget: 80);
        if (hostFolder is null) { Append($"Host folder '{hostFolderName}' not found within budget. Skipping host dump."); return; }
        List<FormaModelItem> hosts = Step("GetRevitModels(host)", () => dm.GetRevitModels(hostFolder)) ?? new();
        Append($"host folder '{hostFolder.Name}': {hosts.Count} model(s); scan note: {dm.LastScanNote}");
        // Open EVERY cloud host read-only and dump every link's FULL reference info — reveals how a
        // WORKING (e.g. manually-relinked) bridge link is actually stored vs a broken one.
        foreach (FormaModelItem host in hosts.Where(m => !m.IsLocal && m.ModelGuid != Guid.Empty))
            DumpHostLinks(uiApp, host);
        Append("=== bridge diagnostic complete — compare each link's LinkedModelProjectId/ModelId to the inventory GUIDs ===");
    }

    /// <summary>Opens one cloud host read-only (all worksets closed) and logs every RevitLinkType's
    /// full external reference (cloud file-ref GUID, or the resourceRef dictionary with ALL keys),
    /// then closes without saving.</summary>
    private static void DumpHostLinks(UIApplication uiApp, FormaModelItem host)
    {
        Append($"-- host '{host.DisplayName}' (model={host.ModelGuid} proj={host.ProjectGuid})");
        var noTargets = new Dictionary<string, List<Actions.RelinkTarget>>();
        Autodesk.Revit.DB.Document? doc = null;
        try
        {
            Autodesk.Revit.DB.ModelPath path = Autodesk.Revit.DB.ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                string.Equals(host.Region, "EMEA", StringComparison.OrdinalIgnoreCase)
                    ? Autodesk.Revit.DB.ModelPathUtils.CloudRegionEMEA
                    : Autodesk.Revit.DB.ModelPathUtils.CloudRegionUS,
                host.ProjectGuid, host.ModelGuid);
            var opts = new Autodesk.Revit.DB.OpenOptions();
            opts.SetOpenWorksetsConfiguration(new Autodesk.Revit.DB.WorksetConfiguration(
                Autodesk.Revit.DB.WorksetConfigurationOption.CloseAllWorksets));
            doc = uiApp.Application.OpenDocumentFile(path, opts);

            foreach (Autodesk.Revit.DB.RevitLinkType t in
                     new Autodesk.Revit.DB.FilteredElementCollector(doc)
                         .OfClass(typeof(Autodesk.Revit.DB.RevitLinkType))
                         .Cast<Autodesk.Revit.DB.RevitLinkType>())
            {
                if (t.IsNestedLink) continue;
                string target = DescribeLinkTarget(t, noTargets, out _);
                Append($"     link '{t.Name}' | {target}");
            }
        }
        catch (Exception ex) { Append("     host open/dump failed: " + ex.Message); }
        finally
        {
            try
            {
                if (doc != null && doc.IsValidObject)
                {
                    try
                    {
                        Autodesk.Revit.DB.WorksharingUtils.RelinquishOwnership(doc,
                            new Autodesk.Revit.DB.RelinquishOptions(true),
                            new Autodesk.Revit.DB.TransactWithCentralOptions());
                    }
                    catch { /* read-only inspection — nothing borrowed */ }
                    doc.Close(false);
                }
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Initiate-to-Forma publish spike (<c>KHBATCH_INITIATE_SPIKE=1</c>): replays a saved SOURCE model
    /// set (<c>KHBATCH_INITIATE_SET</c>, resolved through <see cref="Presets.PresetResolver"/> so a
    /// folder-backed set works) into a TARGET Forma folder found by name (<c>KHBATCH_INITIATE_TARGET</c>)
    /// through the REAL <see cref="InitiateToFormaAction"/> + <see cref="BatchEngine.ProcessModel"/> — the
    /// exact publish path the button takes — then re-lists the target folder to prove the new cloud
    /// models appeared. Mirrors <see cref="RunRelinkSpike"/>: runs from an ExternalEvent (opening a
    /// cloud SOURCE from a synchronous Idling body crashes Revit natively), suppresses dialogs, and
    /// force-quits. Publish names get a unique run prefix (<c>KHBATCH_INITIATE_PREFIX</c>, default
    /// <c>SPIKE-&lt;HHmmss&gt;</c>) so a re-run never collides and the artifacts are obvious to clean up.
    /// </summary>
    private static ExternalEvent? _initiateSpikeEvent;
    private static IExternalEventHandler? _initiateSpikeHandler;

    private static void RunInitiatePublishSpike(UIApplication? uiApp)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== Initiate publish spike {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        if (uiApp is null) { Append("No UIApplication. Aborting."); return; }

        _deferEnd = true;
        _initiateSpikeHandler = new RelayHandler(app =>
        {
            try { InitiatePublishSpikeBody(app); }
            catch (Exception ex) { Append("spike body threw: " + ex); }
            finally
            {
                try { _initiateSpikeEvent?.Dispose(); } catch { /* best effort */ }
                _initiateSpikeEvent = null;
                _initiateSpikeHandler = null;
                End();
            }
        });
        try { _initiateSpikeEvent = ExternalEvent.Create(_initiateSpikeHandler); _initiateSpikeEvent.Raise(); }
        catch (Exception ex) { Append("ExternalEvent.Create/Raise threw: " + ex); End(); }
    }

    private static void InitiatePublishSpikeBody(UIApplication uiApp)
    {
        using var suppressor = new DialogSuppressor(uiApp);

        string setName   = Environment.GetEnvironmentVariable("KHBATCH_INITIATE_SET") ?? "";
        string targetName = Environment.GetEnvironmentVariable("KHBATCH_INITIATE_TARGET") ?? "";
        string prefix = Environment.GetEnvironmentVariable("KHBATCH_INITIATE_PREFIX")
                        ?? ("SPIKE-" + DateTime.Now.ToString("HHmmss"));
        Append($"inputs: source set '{setName}', target folder '{targetName}', prefix '{prefix}' (Add, sep '-')");
        if (string.IsNullOrWhiteSpace(setName) || string.IsNullOrWhiteSpace(targetName))
        {
            Append("KHBATCH_INITIATE_SET and KHBATCH_INITIATE_TARGET are both required. Aborting.");
            return;
        }

        var tokens = new SessionTokenProvider();
        string token = tokens.GetAccessToken();
        Append($"Token: length={token?.Length ?? 0}; LastError={tokens.LastError ?? "(none)"}");
        if (string.IsNullOrEmpty(token)) { Append("No token — Revit isn't signed in to Forma in this session. Aborting."); return; }

        // Source model set (folder-backed sets are re-scanned to their current members here).
        ModelPreset? raw = PresetStore.LoadAll()
            .FirstOrDefault(p => p.Name.Equals(setName, StringComparison.OrdinalIgnoreCase));
        if (raw is null) { Append($"Model set '{setName}' not found. Aborting."); return; }
        ModelPreset src = Presets.PresetResolver.Resolve(raw, out string? setNote);
        if (setNote != null) Append("set note: " + setNote);
        if (src.Models.Count == 0) { Append("Source set has no models. Aborting."); return; }
        Append("sources: " + string.Join(", ", src.Models.Select(m => m.DisplayName)));

        // Target folder (by name — same BFS the Initiate live test uses).
        var dm = new DataManagementClient(tokens);
        List<FormaEntry> hubs = Step("GetHubs", () => dm.GetHubs()) ?? new();
        if (hubs.Count == 0) { Append("No hubs. Aborting."); return; }
        FormaEntry? target = FindFolderByName(dm, hubs, targetName, probeBudget: 80);
        if (target is null) { Append($"Target folder '{targetName}' not found within budget. Aborting."); return; }
        Append($"target: name={target.Name} | hubId={target.HubId} | projectId={target.ProjectId} | urn={target.Id} | region={target.Region}");

        PublishTarget publishTarget;
        try { publishTarget = PublishTarget.FromFolder(target, target.Name, prefix, "", "-", PublishPrefixMode.Add); }
        catch (Exception ex) { Append("PublishTarget.FromFolder failed: " + ex.Message); return; }

        var expectedNames = src.Models
            .Select(m => publishTarget.BuildPublishName(m.DisplayName))
            .ToList();
        Append("expected published names: " + string.Join(", ", expectedNames));

        // --- RUN the real engine + action (ProcessModel = the background publish path) ---
        string dest = Path.Combine(Path.GetTempPath(), "KhBatchRunner", "initiatespike");
        Directory.CreateDirectory(dest);
        var action = new InitiateToFormaAction(publishTarget);
        var engine = new BatchEngine(uiApp, action, dest, "Initiate Spike");
        BatchRunState state = engine.BeginRun();
        foreach (CloudModel model in src.Models)
        {
            Append("   open docs BEFORE: " + OpenDocTitles(uiApp.Application));
            Append($"-- ProcessModel {model.DisplayName} …");
            engine.ProcessModel(model, state);
            Append("   note: " + (string.IsNullOrEmpty(action.PerModelNote) ? "(none)" : action.PerModelNote));
        }
        Append("   open docs AFTER RUN: " + OpenDocTitles(uiApp.Application));
        Append($"run done: ok={state.Result.SucceededCount} fail={state.Result.FailedCount}");
        try { foreach (string l in File.ReadAllLines(engine.DetailLogPath)) Append("  detail: " + l); } catch { }
        try { foreach (string l in File.ReadAllLines(engine.TrackingLogPath)) Append("  track: " + l); } catch { }

        // --- VERIFY: re-list the target folder and confirm each expected name now exists ---
        Append("-- VERIFY: target folder now contains --");
        var listed = new List<string>();
        try { foreach (FormaModelItem m in dm.GetRevitModels(target)) { listed.Add(m.DisplayName); Append("   " + m.DisplayName); } }
        catch (Exception ex) { Append("   (re-list failed: " + ex.Message + ")"); }

        string Norm(string n) => (n.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n).Trim();
        var listedNorm = new HashSet<string>(listed.Select(Norm), StringComparer.OrdinalIgnoreCase);
        int present = expectedNames.Count(n => listedNorm.Contains(Norm(n)));
        Append($"=== initiate spike VERDICT: {present}/{expectedNames.Count} expected model(s) present, " +
               $"{state.Result.FailedCount} failed ⇒ " +
               (present == expectedNames.Count && state.Result.FailedCount == 0 ? "PASS" : "FAIL") + " ===");
    }

    /// <summary>
    /// Publish-Latest spike (<c>KHBATCH_PUBLISH_SPIKE=1</c>): scans a Forma folder found by name
    /// (<c>KHBATCH_PUBLISH_FOLDER</c>) for cloud <c>.rvt</c> models — which now carry their item URN
    /// + DM project id — and calls the REAL <see cref="DataManagementClient.PublishCloudModel"/>
    /// (C4R publish command) on each, WITHOUT opening any model. Pure REST, so it runs synchronously
    /// in this Idling tick like <see cref="Probe"/> (no ExternalEvent needed). Set
    /// <c>KHBATCH_PUBLISH_WITHLINKS=1</c> to exercise the with-links variant. This validates the
    /// commands-endpoint call + payload against the live account before the button is wired to it.
    /// </summary>
    private static ExternalEvent? _publishSpikeEvent;
    private static IExternalEventHandler? _publishSpikeHandler;

    private static void RunPublishSpike(UIApplication? uiApp)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== Publish-Latest spike {DateTime.Now:yyyy-MM-dd HH:mm:ss} (build {Product.Version}) ===");
        if (uiApp is null) { Append("No UIApplication. Aborting."); return; }

        // The optional SEED step OPENS a cloud model, which is illegal from a synchronous Idling
        // body (native crash — same reason the bridge/relink spikes escape), so the whole
        // diagnostic runs from an ExternalEvent like RunBridgeSpike. The pure-REST probes are
        // legal there too.
        _deferEnd = true;
        _publishSpikeHandler = new RelayHandler(app =>
        {
            try { PublishSpikeBody(app); }
            catch (Exception ex) { Append("spike body threw: " + ex); }
            finally
            {
                try { _publishSpikeEvent?.Dispose(); } catch { /* best effort */ }
                _publishSpikeEvent = null;
                _publishSpikeHandler = null;
                End();
            }
        });
        try { _publishSpikeEvent = ExternalEvent.Create(_publishSpikeHandler); _publishSpikeEvent.Raise(); }
        catch (Exception ex) { Append("ExternalEvent.Create/Raise threw: " + ex); End(); }
    }

    private static void PublishSpikeBody(UIApplication uiApp)
    {
        string folderName = Environment.GetEnvironmentVariable("KHBATCH_PUBLISH_FOLDER") ?? "";
        string withLinksEnv = Environment.GetEnvironmentVariable("KHBATCH_PUBLISH_WITHLINKS") ?? "";
        bool withLinks = withLinksEnv is "1" or "true" or "TRUE" or "yes";
        bool seed = (Environment.GetEnvironmentVariable("KHBATCH_PUBLISH_SEED") ?? "") is "1" or "true" or "TRUE" or "yes";
        Append($"inputs: folder '{folderName}', withLinks={withLinks}, seed={seed}");
        if (string.IsNullOrWhiteSpace(folderName)) { Append("KHBATCH_PUBLISH_FOLDER is required. Aborting."); return; }

        var tokens = new SessionTokenProvider();
        string token = tokens.GetAccessToken();
        Append($"Token: length={token?.Length ?? 0}; LastError={tokens.LastError ?? "(none)"}");
        if (string.IsNullOrEmpty(token)) { Append("No token — Revit isn't signed in to Forma in this session. Aborting."); return; }

        var dm = new DataManagementClient(tokens);
        List<FormaEntry> hubs = Step("GetHubs", () => dm.GetHubs()) ?? new();
        if (hubs.Count == 0) { Append("No hubs. Aborting."); return; }
        FormaEntry? folder = FindFolderByName(dm, hubs, folderName, probeBudget: 80);
        if (folder is null) { Append($"Folder '{folderName}' not found within budget. Aborting."); return; }
        Append($"folder: name={folder.Name} | projectId={folder.ProjectId} | urn={folder.Id} | region={folder.Region}");

        List<FormaModelItem> models = Step("GetRevitModels", () => dm.GetRevitModels(folder)) ?? new();
        Append($"models in folder: {models.Count}; scan note: {dm.LastScanNote}");
        if (models.Count == 0) { Append("No cloud models to publish. Aborting."); return; }

        // One-run DIAGNOSTIC on the first eligible model: token scopes, the model's publish-state
        // (GetPublishJob), and which command type + resource (item vs version urn) the C4R publish
        // engine actually accepts — the raw HTTP status + body for each, so nothing is guessed.
        FormaModelItem? probe = models.FirstOrDefault(m => !string.IsNullOrEmpty(m.ItemUrn) && !string.IsNullOrEmpty(m.DmProjectId));
        if (probe is null) { Append("No cloud model carries a publish reference. Aborting."); return; }
        string proj = probe.DmProjectId, item = probe.ItemUrn;
        Append($"probe model: {probe.DisplayName}  item={item}  proj={proj}");
        Append("token scopes: " + TokenScopes(token));

        // SEED: make a real unpublished change on the probe model first. Publishing an
        // up-to-date model errors ("Failed to publish model"), so without a fresh synced edit
        // the Publish probes can't distinguish "command broken" from "nothing to publish".
        if (seed) SeedUnpublishedChange(uiApp, probe);

        string version = "";
        try { version = dm.GetTipVersionUrn(proj, item); } catch (Exception ex) { Append("  tip resolve FAILED: " + ex.Message); }
        Append($"tip version urn: {(string.IsNullOrEmpty(version) ? "(unresolved)" : version)}");

        void Probe(string label, string cmd, string res)
        {
            if (string.IsNullOrEmpty(res)) { Append($"-- {label}: (resource unresolved, skipped)"); return; }
            try { (int st, string body) = dm.PostCommandRaw(proj, cmd, res); Append($"-- {label}: HTTP {st}  {Truncate(body, 900)}"); }
            catch (Exception ex) { Append($"-- {label}: EXCEPTION {ex.Message}"); }
        }
        const string PUB = "commands:autodesk.bim360:C4RModelPublish";
        const string PUBNL = "commands:autodesk.bim360:C4RModelPublishWithoutLinks";
        const string JOB = "commands:autodesk.bim360:C4RModelGetPublishJob";
        Probe("GetPublishJob (item urn)", JOB, item);
        Probe("GetPublishJob (version urn)", JOB, version);
        Probe("Publish (item urn)", PUB, item);
        Probe("Publish (version urn)", PUB, version);
        Probe("PublishWithoutLinks (item urn)", PUBNL, item);
        Append("=== publish DIAGNOSTIC complete ===");
    }

    /// <summary>
    /// Publish-spike seed (<c>KHBATCH_PUBLISH_SEED=1</c>): opens the probe model in the
    /// background (worksets closed — Project Information sits on a system workset, which is
    /// always loaded), stamps Project Status with the current time, syncs + relinquishes, and
    /// closes. Runs only from the spike's ExternalEvent body (cloud opens crash from Idling).
    /// </summary>
    private static void SeedUnpublishedChange(UIApplication uiApp, FormaModelItem model)
    {
        Autodesk.Revit.DB.Document? doc = null;
        using var suppressor = new DialogSuppressor(uiApp);
        try
        {
            Autodesk.Revit.DB.ModelPath path = Autodesk.Revit.DB.ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                string.Equals(model.Region, "EMEA", StringComparison.OrdinalIgnoreCase)
                    ? Autodesk.Revit.DB.ModelPathUtils.CloudRegionEMEA
                    : Autodesk.Revit.DB.ModelPathUtils.CloudRegionUS,
                model.ProjectGuid, model.ModelGuid);
            var opts = new Autodesk.Revit.DB.OpenOptions(); // DoNotDetach — Revit rejects detach for cloud models
            opts.SetOpenWorksetsConfiguration(new Autodesk.Revit.DB.WorksetConfiguration(
                Autodesk.Revit.DB.WorksetConfigurationOption.CloseAllWorksets));
            doc = uiApp.Application.OpenDocumentFile(path, opts);

            string stamp = $"SeedTouch {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            using (var t = new Autodesk.Revit.DB.Transaction(doc, "Publish spike seed"))
            {
                t.Start();
                doc.ProjectInformation.Status = stamp;
                t.Commit();
            }
            var swc = new Autodesk.Revit.DB.SynchronizeWithCentralOptions();
            swc.SetRelinquishOptions(new Autodesk.Revit.DB.RelinquishOptions(true));
            doc.SynchronizeWithCentral(new Autodesk.Revit.DB.TransactWithCentralOptions(), swc);
            Append($"seed: '{model.DisplayName}' Project Status = '{stamp}' — synced to central");
        }
        catch (Exception ex) { Append("seed FAILED: " + ex.Message); }
        finally
        {
            try { if (doc != null && doc.IsValidObject) doc.Close(false); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Forma Data Connector auth probe (<c>KHBATCH_DC_SPIKE=1</c>): answers the one question that
    /// decides whether a "download last night's extract" feature can live in this add-in at all —
    /// does the token Revit already holds reach <c>/data-connector/v1/…</c>?
    ///
    /// <para>The doubt is real: Data Connector wants a 3-legged token from a client id the Forma
    /// account has whitelisted under Custom Integrations, and ours is Revit's own client id, not
    /// one we registered. Data Management accepts it; C4R publish did not (which is why that
    /// button is archived off the ribbon). So this probe asks Forma rather than the documentation,
    /// read-only, before any UI is built on the answer.</para>
    ///
    /// <para>Every step prints its raw HTTP status and body (truncated) and swallows its own
    /// exception: a probe that aborts on the first 403 tells us less than one that walks the whole
    /// chain — requests → jobs → data-listing — and shows exactly where the door closes. It also
    /// tries the account id BOTH ways (bare GUID and <c>b.</c>-prefixed), because the published
    /// samples call that path variable <c>hub_id_b360</c> without saying which form it holds.</para>
    ///
    /// <para>Optional <c>KHBATCH_DC_ACCOUNT=&lt;guid&gt;</c> skips hub discovery and probes that
    /// account directly — for the case where the signed-in user sees many hubs.</para>
    /// </summary>
    private static void RunDataConnectorSpike()
    {
        Append($"=== Data Connector auth probe {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

        var tokens = new SessionTokenProvider();
        string token = tokens.GetAccessToken();
        Append($"Token: length={token?.Length ?? 0}; LastError={tokens.LastError ?? "(none)"}");
        if (string.IsNullOrEmpty(token))
        {
            Append("No token — Revit isn't signed in to Forma in this session. Aborting.");
            return;
        }
        // Data Connector documents data:read (+ data:write to CREATE a request, which this feature
        // never does). Printed rather than asserted: a missing scope explains a 403 we would
        // otherwise blame on the integration whitelist.
        Append("token scopes: " + TokenScopes(token));

        var accounts = new List<string>();
        string configured = Environment.GetEnvironmentVariable("KHBATCH_DC_ACCOUNT") ?? "";
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Append($"account from KHBATCH_DC_ACCOUNT: {configured}");
            accounts.Add(configured.Trim());
        }
        else
        {
            var dm = new DataManagementClient(tokens);
            List<FormaEntry> hubs = Step("GetHubs", () => dm.GetHubs()) ?? new();
            Append($"hubs: {hubs.Count}");
            foreach (FormaEntry hub in hubs)
            {
                Append($"  hub: name={hub.Name} | id={hub.Id} | region={hub.Region}");
                accounts.Add(hub.Id);
            }
            if (accounts.Count == 0) { Append("No hubs. Aborting."); return; }
        }

        foreach (string account in accounts)
        {
            string bare = account.StartsWith("b.", StringComparison.OrdinalIgnoreCase) ? account.Substring(2) : account;
            Append($"-- account {account} (bare {bare})");

            // Both spellings, because a 404 on one form and a 200 on the other is the answer to a
            // question the samples leave open — and a 403 on BOTH is a different answer entirely.
            (int status, string body) = DcGet(token, $"accounts/{bare}/requests");
            if (status != 200)
            {
                (int prefixed, string prefixedBody) = DcGet(token, $"accounts/b.{bare}/requests");
                if (prefixed == 200) { status = prefixed; body = prefixedBody; bare = "b." + bare; }
            }
            if (status != 200) continue;

            string requestId = FirstJsonId(body, "requests");
            if (string.IsNullOrEmpty(requestId)) { Append("   (no extract requests on this account)"); continue; }
            Append($"   newest request id: {requestId}");

            (int jobStatus, string jobBody) = DcGet(token, $"accounts/{bare}/requests/{requestId}/jobs");
            if (jobStatus != 200) continue;

            string jobId = FirstJsonId(jobBody, "jobs");
            if (string.IsNullOrEmpty(jobId)) { Append("   (request has no jobs yet)"); continue; }
            Append($"   newest job id: {jobId}");

            DcGet(token, $"accounts/{bare}/jobs/{jobId}/data-listing");
        }

        Append("=== Data Connector probe complete ===");
    }

    /// <summary>
    /// One read-only Data Connector GET, logged as status + truncated body. Never throws.
    ///
    /// <para>Deliberately self-contained rather than routed through
    /// <see cref="DataManagementClient"/>: this probe exists to decide whether a Data Connector
    /// client is worth writing, and adding REST surface to the shared client (which compiles into
    /// the Revit-free controller too) before that answer is in would be building on the question.</para>
    /// </summary>
    private static (int Status, string Body) DcGet(string token, string path)
    {
        string url = "https://developer.api.autodesk.com/data-connector/v1/" + path;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(1) };
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            // Sync-over-async off the UI thread, the same shape DataManagementClient.GetRaw uses to
            // avoid a WinForms/Revit message-pump deadlock.
            (int status, string body) = System.Threading.Tasks.Task.Run(async () =>
            {
                using System.Net.Http.HttpResponseMessage resp = await http.SendAsync(req).ConfigureAwait(false);
                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ((int)resp.StatusCode, text);
            }).GetAwaiter().GetResult();

            Append($"   GET {path} -> HTTP {status}  {Truncate(body.Replace("\r", "").Replace("\n", " "), 900)}");
            return (status, body);
        }
        catch (Exception ex)
        {
            Append($"   GET {path} -> EXCEPTION {ex.Message}");
            return (0, "");
        }
    }

    /// <summary>
    /// The <c>id</c> of the first element of a Data Connector list response — the newest, since
    /// these endpoints return newest-first by default. Reads both the paginated shape
    /// (<c>{"pagination":…,"results":[…]}</c>) and a bare array, and reports which one it found
    /// so a shape change shows up as a probe line rather than as an empty result.
    /// </summary>
    private static string FirstJsonId(string body, string what)
    {
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
            System.Text.Json.JsonElement root = doc.RootElement;
            System.Text.Json.JsonElement list = root;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (!root.TryGetProperty("results", out list) && !root.TryGetProperty("data", out list))
                {
                    Append($"   ({what}: response is an object with neither 'results' nor 'data')");
                    return "";
                }
            }
            if (list.ValueKind != System.Text.Json.JsonValueKind.Array) return "";
            Append($"   {what} returned: {list.GetArrayLength()}");
            foreach (System.Text.Json.JsonElement item in list.EnumerateArray())
                return item.TryGetProperty("id", out System.Text.Json.JsonElement id) ? id.ToString() : "";
            return "";
        }
        catch (Exception ex) { Append($"   ({what}: parse failed — {ex.Message})"); return ""; }
    }

    /// <summary>The "scope" claim of a JWT access token (base64url payload) — to confirm data:write is present.</summary>
    private static string TokenScopes(string token)
    {
        try
        {
            string[] parts = token.Split('.');
            if (parts.Length < 2) return "(not a JWT)";
            string p = parts[1].Replace('-', '+').Replace('_', '/');
            p += new string('=', (4 - p.Length % 4) % 4);
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(p));
            return doc.RootElement.TryGetProperty("scope", out var s) ? s.ToString() : "(no scope claim)";
        }
        catch (Exception ex) { return "(decode failed: " + ex.Message + ")"; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    /// <summary>
    /// AIT Model Checker spike (<c>KHBATCH_INTEROP_SPIKE=&lt;checkset .xml path or http URL&gt;</c>):
    /// answers the Batch Interop Phase-A questions against the real AIT install. Part A runs a
    /// checkset with a model open-and-ACTIVATED (the modal-runner shape); Part B opens a second
    /// model in the BACKGROUND (no window) and runs against that Document object directly WITHOUT
    /// activating — DocCheckRunner takes the Document explicitly, so if B passes the action can
    /// use the cheaper background path (NeedsActiveDocument=false). Both parts log IsModified
    /// before/after (read-only proof) and verify the HTML + Excel exports appear.
    /// Mirrors RunDynSpike: the body runs from an ExternalEvent, not inside Idling.
    /// </summary>
    private static ExternalEvent? _interopSpikeEvent;
    private static IExternalEventHandler? _interopSpikeHandler;

    private static void RunInteropSpike(UIApplication? uiApp, string checkset)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== Interop (AIT Model Checker) spike {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Append("checkset: " + checkset);
        if (uiApp is null) { Append("No UIApplication. Aborting."); return; }

        _deferEnd = true;
        _interopSpikeHandler = new RelayHandler(app =>
        {
            try { InteropSpikeBody(app, checkset); }
            catch (Exception ex) { Append("spike body threw: " + ex); }
            finally
            {
                try { _interopSpikeEvent?.Dispose(); } catch { /* best effort */ }
                _interopSpikeEvent = null;
                _interopSpikeHandler = null;
                // A supervised probe ends from the supervisor's callback, once its children are done.
                if (!_interopSupervising) End();
            }
        });
        try { _interopSpikeEvent = ExternalEvent.Create(_interopSpikeHandler); _interopSpikeEvent.Raise(); }
        catch (Exception ex) { Append("ExternalEvent.Create/Raise threw: " + ex); End(); }
    }

    private static void InteropSpikeBody(UIApplication uiApp, string checkset)
    {
        string modelA = Path.Combine(TestRoot, "models", "TestA.rvt");
        string modelB = Path.Combine(TestRoot, "models", "TestB.rvt");

        // "engine:<checkset>" — skip the raw-invoker parts and drive the REAL production path
        // instead: BatchInteropAction + BatchEngine + BatchRunnerWindow.RunModal over both test
        // models (verifies ConfigureOutput naming, CSV columns, PerModelNote, close-as-we-go).
        if (checkset.StartsWith("engine:", StringComparison.OrdinalIgnoreCase))
        {
            InteropEngineProbe(uiApp, checkset.Substring("engine:".Length), modelA, modelB);
            return;
        }
        // "supervised:<model set name>|<checkset>" — exactly what the Batch Interop button does since
        // 2026-11-03: the whole set in ONE supervised child Revit, crash → plain retry → skip.
        if (checkset.StartsWith("supervised:", StringComparison.OrdinalIgnoreCase))
        {
            InteropSupervisedProbe(uiApp, checkset.Substring("supervised:".Length));
            return;
        }
        // "schedule:<checkset>" — drive the Schedule Interop data path: a ScheduledJob with
        // InteropChecksetPath through the real ScheduledRunner.BuildJobPasses branch +
        // TryRunInteractive, exactly what ScheduleInteropCommand's "Run now" does.
        if (checkset.StartsWith("schedule:", StringComparison.OrdinalIgnoreCase))
        {
            InteropScheduleProbe(uiApp, checkset.Substring("schedule:".Length), modelA, modelB);
            return;
        }
        // "profile:<checkset>" — where an Interop run's time goes, on one bench model: the whole
        // checkset three times (cold, then warm) with the invoker's phase timings, then each check on
        // its own. Model = KHBATCH_INTEROP_MODEL, default the interopbench Arch model.
        if (checkset.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
        {
            InteropProfile(uiApp, checkset.Substring("profile:".Length));
            return;
        }
        string reportDir = Path.Combine(
            TestRoot, "interopspike", "reports",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        using var suppressor = new DialogSuppressor(uiApp);
        Interop.ModelCheckerInvoker.Trace = Append; // incremental step log — locates a failure inside AIT

        // G0 — which AIT assemblies the process already has (AIT's add-in loads at startup).
        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = asm.GetName().Name ?? "";
            if (name.StartsWith("AIT.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Autodesk.ValidationTool", StringComparison.OrdinalIgnoreCase))
                Append("G0 loaded: " + name);
        }

        // Part A — active document (the modal-runner shape).
        try
        {
            Autodesk.Revit.DB.Document docA = uiApp.OpenAndActivateDocument(modelA).Document;
            Append($"A: opened+activated {docA.Title}");
            InteropRunOn(uiApp, docA, checkset, reportDir, "A");
        }
        catch (Exception ex) { Append("A: open/run threw: " + ex); }

        // Part B — background document, nothing activated since A. DocCheckRunner gets docB.
        try
        {
            Autodesk.Revit.DB.Document docB = uiApp.Application.OpenDocumentFile(modelB);
            Append($"B: opened background {docB.Title}; active=" +
                   (uiApp.ActiveUIDocument?.Document?.Title ?? "(none)"));
            InteropRunOn(uiApp, docB, checkset, reportDir, "B");
            try { docB.Close(false); } catch { /* background doc — best effort */ }
        }
        catch (Exception ex) { Append("B: open/run threw: " + ex); }

        Append("open docs AFTER: " + OpenDocTitles(uiApp.Application));
        Append("=== Interop spike complete ===");
    }

    /// <summary>Drives the real Batch Interop production path headless (everything the button
    /// does except the dialogs): action + engine + modal runner over the two local test models,
    /// then logs the tracking/detail CSVs so the columns and roll-up note can be inspected.</summary>
    private static void InteropEngineProbe(UIApplication uiApp, string checkset, string modelA, string modelB)
    {
        string dest = Path.Combine(TestRoot, "interopspike", "enginetest");
        Directory.CreateDirectory(dest);
        Append("engine probe: checkset=" + checkset);

        var preset = new ModelPreset { Name = "InteropSpike" };
        // KHBATCH_INTEROP_MODELS (';'-separated paths) swaps the two tiny test models for a real
        // bench set — what a runtime measurement needs.
        string[] models = (Environment.GetEnvironmentVariable("KHBATCH_INTEROP_MODELS") ?? "")
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (models.Length == 0) models = new[] { modelA, modelB };
        foreach (string m in models)
            preset.Models.Add(new CloudModel { DisplayName = Path.GetFileName(m), LocalPath = m });

        var action = new BatchInteropAction(checkset);
        var engine = new BatchEngine(uiApp, action, dest, "InteropEngineProbe", SyncMode.NoSync);
        var results = UI.BatchRunnerWindow.RunModal(
            uiApp, new[] { new UI.BatchRunnerWindow.Pass(engine, preset) },
            "Run: Batch Interop (engine probe)", BatchCompletion.CloseDocuments, showSummary: false);

        foreach (var r in results)
        {
            Append($"pass: action={r.ActionName} preset={r.PresetName} result={r.Result}");
            foreach (string? csv in new[] { r.TrackingLogPath, r.DetailLogPath })
            {
                if (string.IsNullOrEmpty(csv) || !File.Exists(csv)) { Append("csv MISSING: " + csv); continue; }
                Append("csv " + csv + ":");
                foreach (string line in File.ReadAllLines(csv))
                    Append("    " + line);
            }
        }
        Append("open docs AFTER: " + OpenDocTitles(uiApp.Application));
        Append("=== Interop spike complete ==="); // same end marker the launcher script watches for
    }

    /// <summary>True while a supervised probe waits for its children — the spike's own finally must
    /// not close this Revit, because this Revit IS the supervisor.</summary>
    private static volatile bool _interopSupervising;

    /// <summary>
    /// The Batch Interop button's path, headless: a saved model set resolved, launched as ONE
    /// supervised child Revit (<see cref="Schedule.InteractiveSplit.SupervisedGroupSize"/>), and this
    /// Revit kept alive as the supervisor until every model has a row — so a crash in the child is
    /// observed, the model retried once plain, and skipped if it crashes again. The loop test
    /// (<c>tools/interopbench/Loop-TestInterop.ps1</c>) runs this from a cold cache.
    /// </summary>
    private static void InteropSupervisedProbe(UIApplication uiApp, string spec)
    {
        int bar = spec.IndexOf('|');
        if (bar <= 0) { Append("supervised probe: expected '<model set>|<checkset>', got: " + spec); return; }
        string setName = spec.Substring(0, bar).Trim();
        string checkset = spec.Substring(bar + 1).Trim();
        string dest = Path.Combine(TestRoot, "interopspike", "supervised");
        Directory.CreateDirectory(dest);
        Append($"supervised probe: set='{setName}' checkset={checkset}");

        ModelPreset? preset = PresetStore.LoadAll()
            .FirstOrDefault(p => string.Equals(p.Name, setName, StringComparison.OrdinalIgnoreCase));
        if (preset == null) { Append("supervised probe: no model set named " + setName); return; }
        ModelPreset chosen = PresetResolver.Resolve(preset, out _);
        Append($"supervised probe: {chosen.Models.Count} model(s)");
        foreach (CloudModel m in chosen.Models)
            Append($"  fact before: {m.DisplayName} → {Describe(Batch.CloudOpenFacts.Get(m))}");

        var action = new BatchInteropAction(checkset);
        var baseJob = new Schedule.ScheduledJob
        {
            Name = "InteropSupervisedProbe",
            RunItems = new List<Schedule.RunItem> { new(action.Name, Schedule.RunItemKind.Action) },
            InteropChecksetPath = checkset,
            Completion = BatchCompletion.CloseDocuments,
            Sync = SyncMode.NoSync,
            DestinationFolder = dest,
            ProjectLogName = "InteropSupervisedProbe",
        };
        Schedule.RunWeight weight = Schedule.InstanceBudget.WeightFor(baseJob);
        int groupSize = Schedule.InteractiveSplit.SupervisedGroupSize(
            chosen.Models.Count, Schedule.InstanceBudget.AutoGroupSize(chosen.Models, weight), alwaysSupervise: true);
        baseJob.GroupSize = groupSize;

        // The children inherit this process's environment: without this they would arm the same probe
        // and each try to supervise a run of their own.
        foreach (string v in new[] { "KHBATCH_SELFTEST_V9", "KHBATCH_SELFTEST_V8", "KHBATCH_INTEROP_SPIKE" })
            Environment.SetEnvironmentVariable(v, null);

        Schedule.RunManifest? manifest = Schedule.ChunkLauncher.LaunchSupervised(
            baseJob, chosen.Models, groupSize, uiApp.Application.VersionNumber, out string? err);
        if (manifest == null) { Append("supervised probe: launch failed: " + err); return; }
        Append($"supervised probe: launched {manifest.CurrentRound!.Groups.Count} child Revit(s), group size {groupSize}" +
               (err != null ? " — " + err : ""));

        _interopSupervising = true;
        Schedule.RunSupervisor.Start(manifest, m =>
        {
            try
            {
                Append($"supervised probe: run {m.State}; rounds={m.RoundNumber}; unrecoverable={m.Unrecoverable.Count}");
                foreach (KeyValuePair<string, int> c in m.OpenCrashes) Append($"  open crashes: {c.Key} × {c.Value}");
                foreach (CloudModel cm in chosen.Models)
                    Append($"  fact after: {cm.DisplayName} → {Describe(Batch.CloudOpenFacts.Get(cm))}");
                if (File.Exists(m.TrackingLogPath))
                {
                    Append("tracking csv " + m.TrackingLogPath + ":");
                    foreach (string line in File.ReadAllLines(m.TrackingLogPath)) Append("    " + line);
                }
            }
            catch (Exception ex) { Append("supervised probe: report failed: " + ex.Message); }
            Append("=== Interop spike complete ===");
            // WM_CLOSE only — safe from the supervisor's thread, unlike unhooking Idling.
            Schedule.RevitShutdown.RequestCurrent("diagnostic probe complete");
        }, showRoundNotifications: false);

        static string Describe(Batch.CloudOpenFacts.Fact? f) => f == null
            ? "(none)"
            : $"workshared={(f.Workshared?.ToString() ?? "?")} source={f.Source} crashRetry={f.CrashRetry}";
    }

    /// <summary>Drives the Schedule Interop data path headless: builds a <see cref="Schedule.ScheduledJob"/>
    /// exactly as <see cref="UI.ScheduleInteropWindow"/>.Commit would (ad-hoc local models,
    /// InteropChecksetPath, ActionNames=[BatchInteropAction.DisplayName]) and runs it through the
    /// real <see cref="Schedule.ScheduledRunner"/>.TryRunInteractive — the same call
    /// ScheduleInteropCommand's "Run now" makes. Proves the new BuildJobPasses branch end to end
    /// without needing an actual Windows Task Scheduler task.</summary>
    private static void InteropScheduleProbe(UIApplication uiApp, string checkset, string modelA, string modelB)
    {
        string dest = Path.Combine(TestRoot, "interopspike", "scheduletest");
        Directory.CreateDirectory(dest);
        Append("schedule probe: checkset=" + checkset);

        var job = new Schedule.ScheduledJob
        {
            Name = "InteropScheduleProbe",
            AdHocModels = new List<CloudModel>
            {
                new() { DisplayName = "TestA.rvt", LocalPath = modelA },
                new() { DisplayName = "TestB.rvt", LocalPath = modelB },
            },
            RunItems = new List<Schedule.RunItem>
                { new(Actions.BatchInteropAction.DisplayName, Schedule.RunItemKind.Action) },
            InteropChecksetPath = checkset,
            // Explicitly the default (skip proven link-only worksets), so this probe exercises the
            // branch a real scheduled job takes rather than only the opt-out one.
            InteropChecksetExaminesLinks = false,
            Sync = SyncMode.NoSync,
            Completion = BatchCompletion.CloseDocuments,
            DestinationFolder = dest,
            ProjectLogName = "InteropScheduleProbe",
        };

        bool ok = Schedule.ScheduledRunner.TryRunInteractive(uiApp, job, "Schedule Interop probe", out string? error);
        Append($"TryRunInteractive returned {ok}; error={error ?? "(none)"}; job.LastRunAt={job.LastRunAt}");

        foreach (string f in Directory.GetFiles(Path.Combine(dest, "Log"), "*.csv"))
        {
            Append("tracking csv " + f + ":");
            foreach (string line in File.ReadAllLines(f)) Append("    " + line);
        }
        foreach (string f in Directory.GetFiles(dest, "*.csv"))
        {
            Append("detail csv " + f + ":");
            foreach (string line in File.ReadAllLines(f)) Append("    " + line);
        }
        Append("open docs AFTER: " + OpenDocTitles(uiApp.Application));
        Append("=== Interop spike complete ===");
    }

    private static void InteropRunOn(
        UIApplication uiApp, Autodesk.Revit.DB.Document doc, string checkset, string reportDir, string label)
    {
        Append($"{label}: IsModelInCloud={doc.IsModelInCloud}, IsWorkshared={doc.IsWorkshared}");
        Append($"{label}: IsModified BEFORE run: {doc.IsModified}");
        string safeTitle = string.Join("_", doc.Title.Split(Path.GetInvalidFileNameChars()));
        string htmlDir = Path.Combine(reportDir, label + "_" + safeTitle);
        string xlsxPath = Path.Combine(reportDir, label + "_" + safeTitle + ".xlsx");
        Directory.CreateDirectory(htmlDir);

        var sw = Stopwatch.StartNew();
        Interop.ModelCheckerRunResult? result = Step($"{label}: RunCheckset",
            () => Interop.ModelCheckerInvoker.RunCheckset(uiApp, doc, checkset, htmlDir, xlsxPath));
        sw.Stop();
        if (result == null) return; // Step already logged the exception

        Append($"{label}: IsModified AFTER run: {doc.IsModified}");
        Append($"{label}: result={result.OverallResult} passed={result.Passed}/{result.TotalChecks} " +
               $"failed={result.Failed} errors={result.Errors} notRun={result.NotRun} " +
               $"({result.PassPercent:0.#}%) in {sw.Elapsed.TotalSeconds:0.#}s");
        Append($"{label}: html files={Directory.GetFiles(htmlDir, "*.html").Length}, " +
               $"xlsx exists={File.Exists(xlsxPath)}");
    }

    /// <summary>Profiles one checkset on one model: three whole runs (the first pays the cold AIT
    /// load), then a run per check, ranked slowest first. The model opens detached with every
    /// workset open — what a first Batch Interop run of a model sees — and closes unsaved.</summary>
    private static void InteropProfile(UIApplication uiApp, string checkset)
    {
        string benchDir = Path.Combine(TestRoot, "interopbench");
        string model = Environment.GetEnvironmentVariable("KHBATCH_INTEROP_MODEL") is { Length: > 0 } m
            ? m : Path.Combine(benchDir, "Bench-Arch-01.rvt");
        string outDir = Path.Combine(benchDir, "profile", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir);
        Interop.ModelCheckerInvoker.Trace = Append; // phase ms lines land in this log
        Append("profile: checkset=" + checkset);
        Append("profile: model=" + model);

        using var suppressor = new DialogSuppressor(uiApp);
        Autodesk.Revit.DB.Document? doc = null;
        try
        {
            Autodesk.Revit.DB.ModelPath path = Autodesk.Revit.DB.ModelPathUtils.ConvertUserVisiblePathToModelPath(model);
            var options = new Autodesk.Revit.DB.OpenOptions();
            if (Autodesk.Revit.DB.BasicFileInfo.Extract(model).IsWorkshared)
            {
                options.DetachFromCentralOption = Autodesk.Revit.DB.DetachFromCentralOption.DetachAndPreserveWorksets;
                options.SetOpenWorksetsConfiguration(new Autodesk.Revit.DB.WorksetConfiguration(
                    Autodesk.Revit.DB.WorksetConfigurationOption.OpenAllWorksets));
            }
            var open = Stopwatch.StartNew();
            doc = uiApp.Application.OpenDocumentFile(path, options);
            Append($"profile: open took {open.ElapsedMilliseconds} ms ({doc.Title})");
            // A detached open may already count as modified; read it before any check runs so an
            // "IsModified after" of True can be pinned on the open or on AIT.
            Append("profile: IsModified right after open: " + doc.IsModified);

            for (int i = 1; i <= 3; i++)
            {
                // Run 1 = AIT's own Excel exporter (the baseline); runs 2-3 = ours. Same model, same
                // checkset, so the two report.xlsx files can be diffed cell for cell.
                Interop.ModelCheckerInvoker.ForceAitExcel = i == 1;
                string runDir = Path.Combine(outDir, "full" + i);
                Directory.CreateDirectory(runDir);
                var sw = Stopwatch.StartNew();
                Interop.ModelCheckerRunResult? r = Step($"full run {i}", () => Interop.ModelCheckerInvoker.RunCheckset(
                    uiApp, doc, checkset, runDir, Path.Combine(runDir, "report.xlsx")));
                if (r != null)
                    Append($"profile: FULL {i} total {sw.ElapsedMilliseconds} ms — {r.OverallResult} " +
                           $"total={r.TotalChecks} pass={r.Passed} fail={r.Failed} err={r.Errors} notRun={r.NotRun} report={r.ReportOnly}");
            }

            Interop.ModelCheckerInvoker.ForceAitExcel = false;
            Append("profile: IsModified after full runs: " + doc.IsModified);

            // Per check: no export (that cost is measured above), so the number is the check alone.
            Interop.ModelCheckerInvoker.Trace = null;
            var rows = new List<(long ms, Interop.SplitCheck c, string outcome)>();
            foreach (Interop.SplitCheck c in Interop.ChecksetSplitter.SplitPerCheck(checkset, Path.Combine(outDir, "checks")))
            {
                var sw = Stopwatch.StartNew();
                string outcome;
                try
                {
                    Interop.ModelCheckerRunResult r = Interop.ModelCheckerInvoker.RunCheckset(uiApp, doc, c.Path);
                    outcome = $"total={r.TotalChecks} pass={r.Passed} fail={r.Failed} err={r.Errors} notRun={r.NotRun} report={r.ReportOnly}";
                }
                catch (Exception ex) { outcome = "THREW " + ex.GetType().Name + ": " + ex.Message; }
                rows.Add((sw.ElapsedMilliseconds, c, outcome));
            }

            Append($"profile: per-check sum {rows.Sum(x => x.ms)} ms over {rows.Count} checks; slowest first:");
            foreach (var (ms, c, outcome) in rows.OrderByDescending(x => x.ms))
                Append($"  {ms,8} ms | {c.CheckType,-24} | {(c.IsChecked ? "on " : "off")} | {c.Heading} | {c.Name} | {outcome}");
            File.WriteAllLines(Path.Combine(outDir, "per-check.csv"),
                new[] { "ms,checkType,isChecked,heading,name,outcome" }.Concat(rows.Select(x =>
                    $"{x.ms},{Csv(x.c.CheckType)},{x.c.IsChecked},{Csv(x.c.Heading)},{Csv(x.c.Name)},{Csv(x.outcome)}")));
            Append("profile: IsModified after all runs: " + doc.IsModified);
        }
        catch (Exception ex) { Append("profile threw: " + ex); }
        finally
        {
            Interop.ModelCheckerInvoker.Trace = null;
            Interop.ModelCheckerInvoker.ForceAitExcel = false;
            try { doc?.Close(false); } catch { /* best effort */ }
            Append("=== Interop spike complete ===");
        }

        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static string OpenDocTitles(Autodesk.Revit.ApplicationServices.Application app)
    {
        var titles = new List<string>();
        try { foreach (Autodesk.Revit.DB.Document d in app.Documents) if (d.IsValidObject) titles.Add(d.Title); }
        catch (Exception ex) { return "(enumerate failed: " + ex.Message + ")"; }
        return titles.Count == 0 ? "(none)" : string.Join(", ", titles);
    }

    /// <summary>BFS across hubs/projects/top-folders/subfolders for the first folder whose name matches (case-insensitive).</summary>
    private static FormaEntry? FindFolderByName(DataManagementClient dm, List<FormaEntry> hubs, string name, int probeBudget)
    {
        var queue = new Queue<FormaEntry>();
        foreach (FormaEntry hub in hubs.Take(3))
            foreach (FormaEntry proj in (Step($"GetProjects({hub.Name})", () => dm.GetProjects(hub)) ?? new()).Take(8))
                foreach (FormaEntry t in Step($"GetTopFolders({proj.Name})", () => dm.GetTopFolders(proj)) ?? new())
                    queue.Enqueue(t);

        int probes = 0;
        while (queue.Count > 0 && probes < probeBudget)
        {
            FormaEntry f = queue.Dequeue();
            probes++;
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            try { foreach (FormaEntry sub in dm.GetSubFolders(f)) queue.Enqueue(sub); } catch { /* skip */ }
        }
        return null;
    }

    /// <summary>Runs one probe step, logging its exception (with full stack) and returning default(T) on failure.</summary>
    private static T? Step<T>(string name, Func<T> work)
    {
        try { return work(); }
        catch (Exception ex)
        {
            Append($"STEP '{name}' FAILED: {ex}");
            return default;
        }
    }

    private static void Append(string line)
    {
        try { File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8); }
        catch { /* logging must never throw */ }
    }

    private static void End()
    {
        if (_app != null && _handler != null) _app.Idling -= _handler;
        _app = null;
        _handler = null;
        Schedule.RevitShutdown.RequestCurrent("diagnostic probe complete");
    }
}
