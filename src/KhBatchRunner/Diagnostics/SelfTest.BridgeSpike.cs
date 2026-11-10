using System.IO;
using Autodesk.Revit.UI;
using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Diagnostics;

public static partial class SelfTest
{
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
}
