using System.IO;
using Autodesk.Revit.UI;
using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Diagnostics;

public static partial class SelfTest
{
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
}
