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
public static partial class SelfTest
{
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
    // The first variable that is SET (even to an empty string) wins, exactly like the former
    // `??` chain; an empty or whitespace value then means "not requested".
    private static readonly string[] SelfTestEnvVars =
    {
        "KHBATCH_SELFTEST_V9",
        "KHBATCH_SELFTEST_V8",
        "KHBATCH_SELFTEST_V7",
        "KHBATCH_SELFTEST_V6",
        "KHBATCH_SELFTEST_V5",
        "KHBATCH_SELFTEST_V4",
        "KHBATCH_SELFTEST_V3",
        "KHBATCH_SELFTEST_V2",
        "KHBATCH_SELFTEST",
    };

    public static bool IsRequested(out string logPath)
    {
        logPath = "";
        foreach (string name in SelfTestEnvVars)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value != null) { logPath = value; break; }
        }
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
