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
}
