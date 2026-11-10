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
}
