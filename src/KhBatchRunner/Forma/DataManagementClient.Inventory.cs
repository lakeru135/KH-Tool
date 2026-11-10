using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KhBatchRunner.Forma;

public sealed partial class DataManagementClient
{
    /// <summary>
    /// A <b>folder inventory</b> — every <c>.rvt</c> in a folder with the cloud identity a preset
    /// would capture (project/model GUID, region), its tip-version metadata, and whether it's a
    /// published cloud-worksharing model. The crawler's data widened to a CSV. Opens no Revit
    /// document. Optionally recurses into subfolders.
    /// </summary>
    public List<FormaInventoryRow> GetFolderInventory(FormaEntry folder, bool includeSubfolders)
    {
        var rows = new List<FormaInventoryRow>();
        CollectInventory(folder, folder.Name, includeSubfolders, rows);
        return rows;
    }

    private void CollectInventory(FormaEntry folder, string folderPath, bool recurse, List<FormaInventoryRow> rows)
    {
        Guid.TryParse(StripB(folder.ProjectId), out Guid folderProjectGuid);
        var subfolders = new List<FormaEntry>();

        string? url = $"{BaseUrl}/data/v1/projects/{folder.ProjectId}/folders/{folder.Id}/contents";
        while (!string.IsNullOrEmpty(url))
        {
            using JsonDocument doc = GetJson(url);
            Dictionary<string, JsonElement> versions = BuildIncludedMap(doc);

            foreach (JsonElement d in Data(doc))
            {
                string type = Str(d, "type");
                if (type == "folders")
                {
                    if (recurse) subfolders.Add(FolderEntry(d, folder));
                    continue;
                }
                if (type != "items") continue;

                string name = Attr(d, "displayName");
                bool hasVersion = TryGetTipVersion(d, versions, folder.ProjectId, out JsonElement version);
                if (string.IsNullOrEmpty(name) && hasVersion) name = Attr(version, "displayName");

                // Pre-initialised so the && short-circuit (version absent → TryCloudGuids not
                // called) still leaves them definitely assigned.
                Guid modelGuid = Guid.Empty, versionProjGuid = Guid.Empty;
                bool hasGuids = hasVersion && TryCloudGuids(version, out modelGuid, out versionProjGuid);

                // A Revit model is anything named .rvt or anything that yields cloud GUIDs.
                if (!name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) && !hasGuids)
                    continue;

                rows.Add(new FormaInventoryRow
                {
                    ModelName = name,
                    FolderPath = folderPath,
                    ProjectGuid = versionProjGuid != Guid.Empty ? versionProjGuid : folderProjectGuid,
                    ModelGuid = modelGuid,
                    Region = folder.Region,
                    LatestVersion = hasVersion ? AttrInt(version, "versionNumber") : null,
                    LastModified = hasVersion ? Attr(version, "lastModifiedTime") : "",
                    ModifiedBy = hasVersion ? Attr(version, "lastModifiedUserName") : "",
                    Published = hasGuids,
                    // Carried so a folder-backed model set can apply the same run/don't-run rule the
                    // browser applies (FormaModelItem.BlockedReason) — those sets are re-scanned
                    // through this method at run time and never see the browser at all.
                    RevitVersion = hasVersion ? ExtData(version, "revitProjectVersion") : "",
                    IsCopyOrShare = hasVersion && IsCopyOf(d, version),
                    // The identity a re-scanned set has to keep — see FormaInventoryRow.ItemUrn.
                    ItemUrn = Str(d, "id"),
                    DmProjectId = folder.ProjectId,
                    HubId = folder.HubId,
                });
            }
            url = NextLink(doc);
        }

        foreach (FormaEntry sub in subfolders)
            CollectInventory(sub, folderPath + " / " + sub.Name, recurse, rows);
    }

    /// <summary>
    /// DIAGNOSTIC: the raw Data Management JSON pages of a folder's contents (items + included tip
    /// versions), exactly as the API returned them — none of this client's filtering. Used by the
    /// bridge spike to see what an incoming-Bridge item's version actually carries (extension.data
    /// GUIDs, other keys, or nothing). Not used by any feature path.
    /// </summary>
    internal IEnumerable<string> GetRawFolderContentsPages(FormaEntry folder)
    {
        string? url = $"{BaseUrl}/data/v1/projects/{folder.ProjectId}/folders/{folder.Id}/contents";
        while (!string.IsNullOrEmpty(url))
        {
            string page = GetRaw(url);
            yield return page;
            using JsonDocument doc = JsonDocument.Parse(page);
            url = NextLink(doc);
        }
    }

    /// <summary>
    /// DIAGNOSTIC: the raw <c>/items/{id}/versions</c> JSON, exactly as the API returned it.
    ///
    /// <para>The folder-contents dump above shows items and their TIP versions only. The file log's
    /// full-history mode reads this endpoint instead, and it is the one place a per-version
    /// <c>links.webView.href</c> would appear — the link the URL column prefers over anything this
    /// product constructs. Without this there is no way to find out whether Forma sends it.</para>
    /// </summary>
    internal IEnumerable<string> GetRawItemVersionsPages(string projectId, string itemId)
    {
        string? url = $"{BaseUrl}/data/v1/projects/{projectId}/items/{Uri.EscapeDataString(itemId)}/versions";
        while (!string.IsNullOrEmpty(url))
        {
            string page = GetRaw(url);
            yield return page;
            using JsonDocument doc = JsonDocument.Parse(page);
            url = NextLink(doc);
        }
    }

    /// <summary>The item ids in a folder, newest page first — so a diagnostic can pick one to dump
    /// versions for without re-implementing the crawl.</summary>
    internal List<string> GetItemIds(FormaEntry folder)
    {
        var ids = new List<string>();
        string? url = $"{BaseUrl}/data/v1/projects/{folder.ProjectId}/folders/{folder.Id}/contents";
        while (!string.IsNullOrEmpty(url))
        {
            using JsonDocument doc = GetJson(url);
            foreach (JsonElement d in Data(doc))
                if (Str(d, "type") == "items")
                    ids.Add(Str(d, "id"));
            url = NextLink(doc);
        }
        return ids;
    }

    /// <summary>
    /// Every <c>.rvt</c> in a folder resolved to its tip <b>version URN</b> (for Model Derivative),
    /// optionally recursing subfolders. Used by Check Sheet Publish. Items whose tip version can't
    /// be resolved are skipped.
    /// </summary>
    public List<FormaModelRef> GetRevitItemRefs(FormaEntry folder, bool includeSubfolders)
    {
        var refs = new List<FormaModelRef>();
        CollectItemRefs(folder, folder.Name, includeSubfolders, refs);
        return refs;
    }

    private void CollectItemRefs(FormaEntry folder, string folderPath, bool recurse, List<FormaModelRef> refs)
    {
        var subfolders = new List<FormaEntry>();
        string? url = $"{BaseUrl}/data/v1/projects/{folder.ProjectId}/folders/{folder.Id}/contents";
        while (!string.IsNullOrEmpty(url))
        {
            using JsonDocument doc = GetJson(url);
            Dictionary<string, JsonElement> versions = BuildIncludedMap(doc);
            foreach (JsonElement d in Data(doc))
            {
                string type = Str(d, "type");
                if (type == "folders")
                {
                    if (recurse) subfolders.Add(FolderEntry(d, folder));
                    continue;
                }
                if (type != "items") continue;

                string name = Attr(d, "displayName");
                if (!name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryGetTipVersion(d, versions, folder.ProjectId, out JsonElement version)) continue;
                if (string.IsNullOrEmpty(name)) name = Attr(version, "displayName");

                string versionUrn = Str(version, "id");
                if (string.IsNullOrEmpty(versionUrn)) continue;

                refs.Add(new FormaModelRef
                {
                    DisplayName = name,
                    VersionUrn = versionUrn,
                    Region = folder.Region,
                    FolderPath = folderPath,
                });
            }
            url = NextLink(doc);
        }

        foreach (FormaEntry sub in subfolders)
            CollectItemRefs(sub, folderPath + " / " + sub.Name, recurse, refs);
    }

    /// <summary>All versions of one item (newest-first as Forma returns them), paginated.</summary>
    private List<JsonElement> GetItemVersions(string projectId, string itemId)
    {
        var versions = new List<JsonElement>();
        string? url = $"{BaseUrl}/data/v1/projects/{projectId}/items/{Uri.EscapeDataString(itemId)}/versions";
        while (!string.IsNullOrEmpty(url))
        {
            using JsonDocument doc = GetJson(url);
            foreach (JsonElement v in Data(doc))
                versions.Add(v.Clone()); // clone to outlive the disposed document
            url = NextLink(doc);
        }
        return versions;
    }
}
