using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KhBatchRunner.Auth;

namespace KhBatchRunner.Forma;

/// <summary>
/// Thin client over the Autodesk Data Management API used by the Forma crawler to
/// browse <c>hubs → projects → topFolders → folder contents</c> and resolve each
/// <c>.rvt</c> to the cloud GUIDs a preset needs. Read-only; one call per tree level
/// (lazy-loaded by the browser UI).
///
/// Auth: a fresh bearer token is fetched from <see cref="ITokenProvider"/> per
/// request (never cached), per the plan's token rule. Opening a model does NOT use
/// this — that goes through the ambient Revit session.
///
/// GUID mapping (the crawler's main risk — verify against one known model):
///   ProjectGuid = the project id with its <c>b.</c> prefix stripped.
///   ModelGuid   = the lineage GUID at the tail of an item id
///                 (<c>urn:adsk.wipprod:dm.lineage:{guid}</c>).
/// </summary>
public sealed class DataManagementClient
{
    private const string BaseUrl = "https://developer.api.autodesk.com";

    /// <summary>
    /// How long ONE request may take. Not a cap on a crawl — a crawl makes thousands of these.
    ///
    /// <para><see cref="HttpClient"/>'s own default is 100 seconds, and that is what a Get File Log
    /// over a big folder started hitting once the extract covered every file type rather than only
    /// <c>.rvt</c>: the same folder that used to cost a handful of requests now costs one per DWG,
    /// PDF and IFC as well, and Forma answers a heavily-loaded account more slowly. The failure
    /// surfaced as "could not read this folder (…HttpClient.Timeout of 100 seconds elapsing)",
    /// which read like a folder-level limit and is nothing of the kind.</para>
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Attempts per request, including the first. Only transient failures are retried —
    /// see <see cref="IsTransient"/>.</summary>
    private const int MaxAttempts = 3;

    private readonly ITokenProvider _tokens;
    private readonly HttpClient _http = new() { Timeout = RequestTimeout };

    public DataManagementClient(ITokenProvider tokens) => _tokens = tokens;

    /// <summary>
    /// Human-readable summary of the last <see cref="GetRevitModels"/> scan
    /// (how many models resolved, and why any were skipped) for the status bar.
    /// </summary>
    public string? LastScanNote { get; private set; }

    /// <summary>True once we've obtained a non-empty session token.</summary>
    public bool HasToken() => !string.IsNullOrWhiteSpace(_tokens.GetAccessToken());

    public List<FormaEntry> GetHubs()
    {
        var result = new List<FormaEntry>();
        using JsonDocument doc = GetJson($"{BaseUrl}/project/v1/hubs");
        foreach (JsonElement d in Data(doc))
        {
            string id = Str(d, "id");
            string name = Attr(d, "name", "extension"); // hubs use attributes.name
            string region = AttrRaw(d, "region") ?? "US";
            result.Add(new FormaEntry
            {
                Kind = FormaKind.Hub, Id = id, Name = name, HubId = id, Region = NormalizeRegion(region)
            });
        }
        return Sorted(result);
    }

    public List<FormaEntry> GetProjects(FormaEntry hub)
    {
        var result = new List<FormaEntry>();
        using JsonDocument doc = GetJson($"{BaseUrl}/project/v1/hubs/{hub.Id}/projects");
        foreach (JsonElement d in Data(doc))
        {
            string id = Str(d, "id");
            result.Add(new FormaEntry
            {
                Kind = FormaKind.Project, Id = id, Name = Attr(d, "name"),
                HubId = hub.Id, ProjectId = id, Region = hub.Region
            });
        }
        return Sorted(result);
    }

    public List<FormaEntry> GetTopFolders(FormaEntry project)
    {
        var result = new List<FormaEntry>();
        using JsonDocument doc = GetJson(
            $"{BaseUrl}/project/v1/hubs/{project.HubId}/projects/{project.ProjectId}/topFolders");
        foreach (JsonElement d in Data(doc))
            result.Add(FolderEntry(d, project));
        return Sorted(result);
    }

    /// <summary>Subfolders of a folder (for tree navigation). Paginated.</summary>
    public List<FormaEntry> GetSubFolders(FormaEntry folder)
    {
        var result = new List<FormaEntry>();
        foreach (JsonElement d in EnumerateContents(folder))
        {
            if (Str(d, "type") == "folders")
                result.Add(FolderEntry(d, folder));
        }
        return Sorted(result);
    }

    /// <summary>
    /// The <c>.rvt</c> models directly in a folder, resolved to the cloud GUIDs a
    /// preset needs. Paginated.
    ///
    /// The model GUID comes from the item's <b>tip version</b>
    /// (<c>attributes.extension.data.modelGuid</c>) — NOT from the item's lineage URN,
    /// whose tail is URL-safe base64 in Forma (not a GUID). Versions arrive in the
    /// response's <c>included[]</c>; when absent we fetch the item's <c>/tip</c>.
    /// </summary>
    public List<FormaModelItem> GetRevitModels(FormaEntry folder)
    {
        var result = new List<FormaModelItem>();
        Guid.TryParse(StripB(folder.ProjectId), out Guid folderProjectGuid);

        int revitItems = 0, skippedNoVersion = 0, skippedNoGuid = 0, noYear = 0, copies = 0;

        string? url = $"{BaseUrl}/data/v1/projects/{folder.ProjectId}/folders/{folder.Id}/contents";
        while (!string.IsNullOrEmpty(url))
        {
            using JsonDocument doc = GetJson(url);
            Dictionary<string, JsonElement> versions = BuildIncludedMap(doc);

            foreach (JsonElement d in Data(doc))
            {
                if (Str(d, "type") != "items") continue;
                string name = Attr(d, "displayName");

                if (!TryGetTipVersion(d, versions, folder.ProjectId, out JsonElement version))
                {
                    if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                    { revitItems++; skippedNoVersion++; }
                    continue;
                }

                if (string.IsNullOrEmpty(name)) name = Attr(version, "displayName");
                bool hasGuids = TryCloudGuids(version, out Guid modelGuid, out Guid versionProjGuid);

                // A Revit model is anything named .rvt or anything that yields cloud GUIDs.
                // Non-Revit files (dwg/pdf/…) have neither and are skipped silently.
                if (!name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) && !hasGuids)
                    continue;

                revitItems++;
                if (!hasGuids) skippedNoGuid++;

                string revitYear = ExtData(version, "revitProjectVersion");
                if (revitYear.Length == 0) noYear++;

                // A COPY / SHARED / BRIDGE-CONSUMED instance: its version points back at the lineage
                // it was made from, so originalItemUrn is the SOURCE item rather than this one.
                // Proven on this account — a "Test Share" copy carries the "Test 2A" original's
                // lineage — and free, because both values are already in this response.
                string itemUrn = Str(d, "id");
                bool isCopy = IsCopyOf(d, version);
                if (isCopy) copies++;

                // A model without cloud GUIDs is LISTED since 1.3.8.2, greyed out and unselectable,
                // where it used to be dropped here. It is a real .rvt sitting in the folder the user
                // is looking at, and "it isn't in the list" and "it can't be run" are very different
                // answers to give someone who can see the file in Forma. FormaModelItem.BlockedReason
                // decides what happens to it; this only stops hiding it.
                result.Add(new FormaModelItem
                {
                    DisplayName = name,
                    Region = folder.Region,
                    ProjectGuid = versionProjGuid != Guid.Empty ? versionProjGuid : folderProjectGuid,
                    ModelGuid = modelGuid,
                    // For the Forma "Publish Latest Version" command: the item's lineage URN and the
                    // DM project id (b.-prefixed) — captured here so a saved set can publish later.
                    ItemUrn = itemUrn,
                    DmProjectId = folder.ProjectId,
                    HubId = folder.HubId,
                    // All read live, all display-only, none persisted — see BlockedReason.
                    RevitVersion = revitYear,
                    IsCloudWorkshared = hasGuids,
                    IsCopyOrShare = isCopy,
                    SourceItemUrn = isCopy ? ExtData(version, "originalItemUrn") : "",
                    ModelType = ExtData(version, "modelType")
                });
            }
            url = NextLink(doc);
        }

        LastScanNote = DescribeScan(revitItems, result.Count, skippedNoVersion, skippedNoGuid, noYear, copies);
        return result.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Everything Forma reports about each item in <paramref name="folder"/>, unfiltered — the
    /// diagnostic behind <see cref="GetRevitModels"/>. Same single request, so it costs nothing
    /// beyond the scan the browser already did; non-Revit items are included on purpose, because
    /// "why is this file not in the list?" is one of the questions it exists to answer.
    ///
    /// <para><paramref name="runningRevitVersion"/> only fills the Verdict column, so the CSV can
    /// be compared against what the user is looking at rather than reasoned about.</para>
    /// </summary>
    public List<FormaScanDetailRow> GetScanDetails(FormaEntry folder, string runningRevitVersion)
    {
        var rows = new List<FormaScanDetailRow>();
        Guid.TryParse(StripB(folder.ProjectId), out Guid folderProjectGuid);

        string? url = $"{BaseUrl}/data/v1/projects/{folder.ProjectId}/folders/{folder.Id}/contents";
        while (!string.IsNullOrEmpty(url))
        {
            using JsonDocument doc = GetJson(url);
            Dictionary<string, JsonElement> versions = BuildIncludedMap(doc);

            foreach (JsonElement d in Data(doc))
            {
                if (Str(d, "type") != "items") continue;

                string name = Attr(d, "displayName");
                bool hasVersion = TryGetTipVersion(d, versions, folder.ProjectId, out JsonElement version);
                if (string.IsNullOrEmpty(name) && hasVersion) name = Attr(version, "displayName");

                Guid modelGuid = Guid.Empty, versionProjGuid = Guid.Empty;
                bool hasGuids = hasVersion && TryCloudGuids(version, out modelGuid, out versionProjGuid);

                var item = new FormaModelItem
                {
                    DisplayName = name,
                    Region = folder.Region,
                    ProjectGuid = versionProjGuid != Guid.Empty ? versionProjGuid : folderProjectGuid,
                    ModelGuid = modelGuid,
                    ItemUrn = Str(d, "id"),
                    DmProjectId = folder.ProjectId,
                    RevitVersion = hasVersion ? ExtData(version, "revitProjectVersion") : "",
                    IsCloudWorkshared = hasGuids,
                    IsCopyOrShare = hasVersion && IsCopyOf(d, version)
                };

                rows.Add(new FormaScanDetailRow
                {
                    ModelName = name,
                    FolderPath = folder.Name,
                    ItemUrn = item.ItemUrn,
                    OriginalItemUrn = hasVersion ? ExtData(version, "originalItemUrn") : "",
                    ItemExtensionType = ExtType(d),
                    VersionExtensionType = hasVersion ? ExtType(version) : "",
                    ExtensionDataKeys = hasVersion ? ExtDataKeys(version) : "(no tip version)",
                    ModelGuid = modelGuid == Guid.Empty ? "" : modelGuid.ToString(),
                    ProjectGuid = versionProjGuid == Guid.Empty ? "" : versionProjGuid.ToString(),
                    RevitVersion = item.RevitVersion,
                    ModelType = hasVersion ? ExtData(version, "modelType") : "",
                    ProcessState = hasVersion ? ExtData(version, "processState") : "",
                    Verdict = item.BlockedReason(runningRevitVersion) ?? "selectable",
                });
            }
            url = NextLink(doc);
        }

        return rows.OrderBy(r => r.ModelName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- Extract: File Log + Folder Inventory (Forma Batch Action panel) --------

    /// <summary>
    /// The <b>file log</b> (version history) of the files in a folder — one row per version,
    /// newest folders first. A native port of the team's <c>Check_01_Get File Log from URL</c>
    /// graph, sourced from a picked folder instead of a pasted URL. Opens no Revit document; pure
    /// Data Management REST. Optionally recurses into subfolders.
    ///
    /// <para><b>Every file type, since 1.4.4.</b> This used to skip anything not named
    /// <c>.rvt</c>. Nothing in the report was ever Revit-specific — see <see cref="FileLogSpec"/>
    /// — so the filter is now the caller's choice, and a null <paramref name="spec"/> means every
    /// file.</para>
    /// </summary>
    /// <param name="progress">Called with the running row count as folders are read, so a crawl
    /// over a whole project doesn't look hung. Optional.</param>
    /// <param name="warn">Called once per FILE that could not be read, after its retries. Optional;
    /// when null such a file is skipped silently.</param>
    public List<FormaFileLogRow> GetFileLog(FormaEntry folder, bool includeSubfolders,
        FileLogSpec? spec = null, Action<int>? progress = null, Action<string>? warn = null)
    {
        var rows = new List<FormaFileLogRow>();
        CollectFileLog(folder, includeSubfolders, spec ?? FileLogSpec.Default, rows, progress, warn);
        return rows;
    }

    private void CollectFileLog(FormaEntry folder, bool recurse, FileLogSpec spec,
        List<FormaFileLogRow> rows, Action<int>? progress, Action<string>? warn)
    {
        var subfolders = new List<FormaEntry>();
        string? url = $"{BaseUrl}/data/v1/projects/{folder.ProjectId}/folders/{folder.Id}/contents";
        while (!string.IsNullOrEmpty(url))
        {
            using JsonDocument doc = GetJson(url);
            // Built only for the tip-only mode, and built from this response — the folder listing
            // already carries its items' tip versions in included[], the same way CollectInventory
            // reads them.
            Dictionary<string, JsonElement> tips = spec.LatestVersionOnly
                ? BuildIncludedMap(doc)
                : new Dictionary<string, JsonElement>();

            foreach (JsonElement d in Data(doc))
            {
                string type = Str(d, "type");
                if (type == "folders")
                {
                    if (recurse) subfolders.Add(FolderEntry(d, folder));
                    continue;
                }
                if (type != "items") continue;

                // Forma keeps deleted items in the listing, flagged. Invisible while only .rvt was
                // reported (a deleted model is rare); with every type in scope a folder's deleted
                // files would sit beside its live ones with nothing to tell them apart.
                if (!spec.IncludeHidden && AttrBool(d, "hidden") == true) continue;

                string name = Attr(d, "displayName");
                if (!spec.Accepts(name)) continue;
                string fileType = FileLogSpec.ExtensionOf(name);

                string itemId = Str(d, "id");

                // One file's history is one request, and a folder holds thousands of them. Losing
                // that request must cost that FILE, not the folder: the first version of this
                // threw straight out of the crawl, so a single slow answer discarded every row
                // already gathered and reported the whole folder as unreadable.
                try
                {
                    if (spec.LatestVersionOnly)
                    {
                        // Usually free: the tip is already in this page's included[].
                        // TryGetTipVersion fetches /tip only for an item the response left out, so
                        // the per-item request the full-history path always pays becomes the
                        // exception rather than the rule.
                        if (TryGetTipVersion(d, tips, folder.ProjectId, out JsonElement tip))
                            rows.Add(RowFor(name, fileType, tip, d, folder, itemId));
                        continue;
                    }

                    foreach (JsonElement v in GetItemVersions(folder.ProjectId, itemId))
                        rows.Add(RowFor(name, fileType, v, d, folder, itemId));
                }
                catch (Exception ex)
                {
                    warn?.Invoke($"{name}: {ex.Message.Split('\n')[0]}");
                }
            }
            url = NextLink(doc);
            progress?.Invoke(rows.Count);
        }

        // Recurse after finishing this folder's pages (depth-first, but pages first).
        foreach (FormaEntry sub in subfolders)
            CollectFileLog(sub, recurse, spec, rows, progress, warn);
    }

    /// <param name="item">The item this version belongs to — carried only so its own web link can
    /// stand in when the version has none.</param>
    /// <param name="folder">The folder being crawled: the project and folder URNs a constructed
    /// fallback URL needs, which are not present on the version itself.</param>
    private static FormaFileLogRow RowFor(string name, string fileType, JsonElement version,
        JsonElement item, FormaEntry folder, string itemUrn)
    {
        int versionNumber = AttrInt(version, "versionNumber") ?? 0;
        return new FormaFileLogRow
        {
            FileName = name,
            FileType = fileType,
            VersionNumber = versionNumber,
            Url = WebUrlFor(version, item, folder, itemUrn, versionNumber),
            VersionUrn = Str(version, "id"),
            LastModified = Attr(version, "lastModifiedTime"),
            ModifiedBy = Attr(version, "lastModifiedUserName"),
            StorageSize = AttrLong(version, "storageSize"),
            ProcessingState = ExtData(version, "processState"),
            Comment = Attr(version, "comment"),
        };
    }

    /// <summary>
    /// A browser link that opens this version in Forma, in descending order of trust.
    ///
    /// <list type="number">
    /// <item>The version's own <c>links.webView.href</c>. Authoritative — Forma built it, it points at
    /// the right site for this hub, and it is version-specific. Nothing had ever read it: the
    /// version element is already cloned whole (see <see cref="GetItemVersions"/>), so the link has
    /// been sitting in memory unused.</item>
    /// <item>The ITEM's <c>links.webView.href</c> — opens the file at its latest version. Wrong
    /// version, right file, which beats no link at all.</item>
    /// <item>A constructed URL. <b>Written from the documented URL shape, not verified against a
    /// live hub</b> — which is exactly why it is last. If the two above are ever present this is
    /// never used; the raw-JSON probe in <c>SelfTest</c> is how to find out.</item>
    /// </list>
    /// </summary>
    internal static string WebUrlFor(JsonElement version, JsonElement item, FormaEntry folder,
        string itemUrn, int versionNumber)
    {
        string href = LinkHref(version, "webView");
        if (href.Length > 0) return href;

        href = LinkHref(item, "webView");
        if (href.Length > 0) return href;

        return FallbackWebUrl(folder.ProjectId, folder.Id, itemUrn, versionNumber);
    }

    /// <summary>
    /// The hand-built Forma Docs link, used only when Forma supplied none.
    ///
    /// <para><b>Unverified against a live hub.</b> Nothing in this product had ever constructed an
    /// Forma web URL, so this follows the documented shape rather than an observed response — which
    /// is precisely why it sits behind two links Forma generates itself. Returns "" rather than a
    /// half-built URL when an identifier is missing: a blank cell is honest, a broken link is not.</para>
    /// </summary>
    internal static string FallbackWebUrl(string dmProjectId, string folderUrn, string itemUrn,
        int versionNumber)
    {
        string projectGuid = StripB(dmProjectId ?? "");
        if (projectGuid.Length == 0 || string.IsNullOrEmpty(itemUrn)) return "";

        string url = $"https://acc.autodesk.com/docs/files/projects/{Uri.EscapeDataString(projectGuid)}" +
                     $"?folderUrn={Uri.EscapeDataString(folderUrn ?? "")}" +
                     $"&entityId={Uri.EscapeDataString(itemUrn)}";
        return versionNumber > 0 ? url + "&version=" + versionNumber : url;
    }

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

    // ---- Publish Latest Version (C4R publish) --------------------------------

    /// <summary>
    /// Publishes the latest synced version of a C4R (cloud-worksharing) Revit model to Forma — the
    /// batch equivalent of Revit's "Publish Latest Version" — via the Data Management <b>commands</b>
    /// endpoint. Keyed on the DM project id (<paramref name="dmProjectId"/>, <c>b.</c>-prefixed) and
    /// the item's lineage <paramref name="itemUrn"/> — NOT the C4R GUIDs. <paramref name="withLinks"/>
    /// chooses <c>C4RModelPublish</c> (pulls in linked models) vs <c>C4RModelPublishWithoutLinks</c>
    /// (this model only). Throws <see cref="HttpRequestException"/> (with Forma's error body) on a
    /// non-success status — e.g. a 409 when a publish is already in progress — so the caller logs a
    /// per-model row either way.
    /// </summary>
    public PublishResult PublishCloudModel(string dmProjectId, string itemUrn, bool withLinks)
    {
        if (string.IsNullOrWhiteSpace(dmProjectId) || string.IsNullOrWhiteSpace(itemUrn))
            throw new InvalidOperationException("Publish needs both the DM project id and the item URN.");

        // The C4R publish command targets the model's TIP VERSION urn, not the lineage item urn
        // (the resource is type "items" but its id is the version urn) — resolve it first.
        string versionUrn = GetTipVersionUrn(dmProjectId, itemUrn);
        if (string.IsNullOrWhiteSpace(versionUrn))
            throw new InvalidOperationException("Could not resolve the model's latest version to publish.");

        string commandType = withLinks
            ? "commands:autodesk.bim360:C4RModelPublish"
            : "commands:autodesk.bim360:C4RModelPublishWithoutLinks";

        string requestBody = BuildCommandBody(commandType, versionUrn);
        using JsonDocument doc = PostJson($"{BaseUrl}/data/v1/projects/{dmProjectId}/commands", requestBody);

        string status = "accepted", commandId = "";
        try
        {
            if (doc.RootElement.TryGetProperty("data", out JsonElement data))
            {
                commandId = Str(data, "id");
                if (data.TryGetProperty("attributes", out JsonElement at) &&
                    at.TryGetProperty("extension", out JsonElement ext) &&
                    ext.TryGetProperty("data", out JsonElement edata))
                {
                    foreach (string key in new[] { "commandStatus", "status", "processState" })
                        if (edata.TryGetProperty(key, out JsonElement s) && s.ValueKind == JsonValueKind.String)
                        { status = s.GetString()!; break; }
                }
            }
        }
        catch { /* tolerate an unexpected response shape — success status already implies accepted */ }

        return new PublishResult { Accepted = true, Status = status, Message = commandId };
    }

    /// <summary>The tip (latest) VERSION urn of an item — the resource the C4R publish command
    /// targets. Returns "" when it can't be resolved.</summary>
    public string GetTipVersionUrn(string projectId, string itemUrn)
    {
        using JsonDocument doc = GetJson(
            $"{BaseUrl}/data/v1/projects/{projectId}/items/{Uri.EscapeDataString(itemUrn)}/tip");
        return doc.RootElement.TryGetProperty("data", out JsonElement data) ? Str(data, "id") : "";
    }

    /// <summary>The tip version's <c>extension.data.modelType</c> — <c>"multiuser"</c> for a workshared
    /// cloud model, <c>"singleuser"</c> for a non-workshared one — or "" when Forma does not say. Read at
    /// RUN time, never from a saved set: see <see cref="Batch.CloudOpenFacts"/>.</summary>
    public string GetTipModelType(string projectId, string itemUrn)
    {
        using JsonDocument doc = GetJson(
            $"{BaseUrl}/data/v1/projects/{projectId}/items/{Uri.EscapeDataString(itemUrn)}/tip");
        return doc.RootElement.TryGetProperty("data", out JsonElement data) ? ExtData(data, "modelType") : "";
    }

    /// <summary>The commands endpoint's JSON:API body: publish <paramref name="resourceUrn"/> (the
    /// tip version urn) with the given C4R command <paramref name="commandType"/>.</summary>
    private static string BuildCommandBody(string commandType, string resourceUrn)
    {
        var payload = new
        {
            jsonapi = new { version = "1.0" },
            data = new
            {
                type = "commands",
                attributes = new { extension = new { type = commandType, version = "1.0.0" } },
                relationships = new
                {
                    resources = new { data = new[] { new { type = "items", id = resourceUrn } } }
                }
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Diagnostic: POST a raw C4R command and return (httpStatus, responseBody) without
    /// throwing — used by the publish spike to probe command types/resources against the live API.</summary>
    public (int Status, string Body) PostCommandRaw(string dmProjectId, string commandType, string resourceUrn)
    {
        string requestBody = BuildCommandBody(commandType, resourceUrn);
        return System.Threading.Tasks.Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/data/v1/projects/{dmProjectId}/commands");
            string token = _tokens.GetAccessToken();
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(requestBody, Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.api+json");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
            using HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ((int)resp.StatusCode, text);
        }).GetAwaiter().GetResult();
    }

    private JsonDocument PostJson(string url, string jsonBody)
    {
        string body = System.Threading.Tasks.Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            string token = _tokens.GetAccessToken();
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // The commands endpoint requires EXACTLY "application/vnd.api+json" — a "; charset=utf-8"
            // suffix (what StringContent's 3-arg ctor adds) makes it 415 Unsupported Media Type. Set
            // the header without a charset, and send Accept for the same media type.
            req.Content = new StringContent(jsonBody, Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.api+json");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
            using HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} for {url}\n{text}");
            return text;
        }).GetAwaiter().GetResult();

        return JsonDocument.Parse(body);
    }

    // ---- helpers -------------------------------------------------------------

    private IEnumerable<JsonElement> EnumerateContents(FormaEntry folder)
    {
        // data/v1 folder contents, following links.next until exhausted.
        string? url = $"{BaseUrl}/data/v1/projects/{folder.ProjectId}/folders/{folder.Id}/contents";
        while (!string.IsNullOrEmpty(url))
        {
            using JsonDocument doc = GetJson(url);
            foreach (JsonElement d in Data(doc))
                yield return d;
            url = NextLink(doc);
        }
    }

    private static FormaEntry FolderEntry(JsonElement d, FormaEntry parent) => new()
    {
        Kind = FormaKind.Folder,
        Id = Str(d, "id"),
        Name = Attr(d, "displayName"),
        HubId = parent.HubId,
        ProjectId = parent.ProjectId,
        Region = parent.Region
    };

    private JsonDocument GetJson(string url) => JsonDocument.Parse(GetRaw(url));

    /// <summary>
    /// One GET, retried when the failure is the kind that passes.
    ///
    /// <para>A crawl of a large folder is thousands of requests against a rate-limited API, so a
    /// slow answer or a 429 somewhere in the middle is ordinary, not exceptional. Without a retry
    /// the first one of them killed the whole folder. Backoff is exponential, and honours Forma's own
    /// <c>Retry-After</c> when it sends one — being told to wait and not waiting is how a throttle
    /// turns into a wall.</para>
    /// </summary>
    private string GetRaw(string url)
    {
        // Fresh token per call; run off the UI thread and block, to avoid any
        // WinForms message-pump deadlock from awaiting on the captured context.
        return System.Threading.Tasks.Task.Run(async () =>
        {
            Exception? last = null;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                TimeSpan? wait = null;
                try
                {
                    // A request message cannot be re-sent, so each attempt builds its own — and
                    // takes a fresh token, which matters when a retry follows a long backoff.
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    string token = _tokens.GetAccessToken();
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) return text;

                    var failure = new HttpRequestException(
                        $"{(int)resp.StatusCode} {resp.ReasonPhrase} for {url}\n{text}");
                    if (attempt == MaxAttempts || !IsTransient(resp.StatusCode)) throw failure;

                    last = failure;
                    wait = RetryAfter(resp) ?? Backoff(attempt);
                }
                catch (Exception ex) when (IsTimeout(ex) && attempt < MaxAttempts)
                {
                    last = ex;
                    wait = Backoff(attempt);
                }

                if (wait is { } delay)
                    await System.Threading.Tasks.Task.Delay(delay).ConfigureAwait(false);
            }

            throw last ?? new HttpRequestException("Request failed for " + url);
        }).GetAwaiter().GetResult();
    }

    /// <summary>Status codes worth trying again: the throttle, and the gateway-side 5xx family.
    /// A 401/403/404 is an answer, not a hiccup, and retrying it only wastes the user's time.</summary>
    private static bool IsTransient(System.Net.HttpStatusCode code) =>
        (int)code == 429 || (int)code == 500 || (int)code == 502 || (int)code == 503 || (int)code == 504;

    /// <summary>
    /// True for a <see cref="RequestTimeout"/> expiry.
    ///
    /// <para>.NET reports it as a cancellation, not a timeout: <see cref="HttpClient"/> cancels its
    /// own token, so it arrives as <c>TaskCanceledException</c> — the shape behind the
    /// "the request was canceled due to the configured HttpClient.Timeout" text. Nobody cancels
    /// this crawl from outside, so a cancellation here can only be that.</para>
    /// </summary>
    private static bool IsTimeout(Exception ex) =>
        ex is TaskCanceledException or OperationCanceledException ||
        (ex is HttpRequestException && ex.InnerException is TimeoutException);

    /// <summary>The server's own instruction, in seconds or as an HTTP date; null when it gave none.
    /// Clamped so a hostile or mistaken header cannot park a run for an hour.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        RetryConditionHeaderValue? h = resp.Headers.RetryAfter;
        if (h == null) return null;
        TimeSpan? wait = h.Delta ?? (h.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is not { } w || w <= TimeSpan.Zero) return null;
        return w > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : w;
    }

    /// <summary>2s, then 6s — long enough for a throttle window to move, short enough that a folder
    /// of thousands of files does not spend its afternoon sleeping.</summary>
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(2 * Math.Pow(3, attempt - 1));

    // Exposed to the offline harness: the retry rules decide whether a large folder is readable at
    // all, and they are pure predicates that can be pinned without a network.
    internal static bool IsTimeoutForTest(Exception ex) => IsTimeout(ex);
    internal static bool IsTransientForTest(System.Net.HttpStatusCode code) => IsTransient(code);
    internal static TimeSpan BackoffForTest(int attempt) => Backoff(attempt);
    internal static TimeSpan RequestTimeoutForTest => RequestTimeout;

    private static IEnumerable<JsonElement> Data(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? NextLink(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("links", out JsonElement links) &&
            links.TryGetProperty("next", out JsonElement next) &&
            next.ValueKind == JsonValueKind.Object &&
            next.TryGetProperty("href", out JsonElement href))
            return href.GetString();
        return null;
    }

    /// <summary>
    /// <c>links.&lt;name&gt;.href</c> on ONE resource (an item, a version), or "" when absent.
    ///
    /// <para>The sibling of <see cref="NextLink"/>, which reads the same shape but only at the
    /// document ROOT and only for <c>next</c> — it takes a <see cref="JsonDocument"/>, so it cannot
    /// be pointed at a resource. Every other reader in this class is rooted at
    /// <c>attributes</c>, so until now nothing could see a resource's <c>links</c> at all.</para>
    /// </summary>
    private static string LinkHref(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("links", out JsonElement links) &&
            links.ValueKind == JsonValueKind.Object &&
            links.TryGetProperty(name, out JsonElement link) &&
            link.ValueKind == JsonValueKind.Object &&
            link.TryGetProperty("href", out JsonElement href) &&
            href.ValueKind == JsonValueKind.String)
            return href.GetString() ?? "";
        return "";
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    // attributes.<name>, tolerating displayName vs name differences.
    private static string Attr(JsonElement el, params string[] names)
    {
        if (!el.TryGetProperty("attributes", out JsonElement attrs)) return "";
        foreach (string n in names)
            if (attrs.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.String)
                return v.GetString()!;
        return "";
    }

    private static string? AttrRaw(JsonElement el, string name)
    {
        if (el.TryGetProperty("attributes", out JsonElement attrs) &&
            attrs.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    // attributes.<name> as a number, tolerating strings ("42") and absence.
    private static int? AttrInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty("attributes", out JsonElement attrs)) return null;
        if (!attrs.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
        return null;
    }

    private static long? AttrLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty("attributes", out JsonElement attrs)) return null;
        if (!attrs.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long s)) return s;
        return null;
    }

    /// <summary>
    /// <c>attributes.&lt;name&gt;</c> as a bool; null when absent or unreadable.
    ///
    /// <para>String-tolerant on purpose. <see cref="FormaModelItem"/> was once recorded as never
    /// receiving <c>revitProjectVersion</c> because a string-only reader discarded a JSON NUMBER;
    /// the same mistake in the other direction would make every deleted item look live.</para>
    /// </summary>
    private static bool? AttrBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty("attributes", out JsonElement attrs)) return null;
        if (!attrs.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool s)) return s;
        return null;
    }

    /// <summary>
    /// <c>attributes.extension.data.&lt;name&gt;</c> as a string — where a version's processing state,
    /// its cloud GUIDs and its Revit release all live.
    ///
    /// <para><b>NUMBERS COUNT TOO, since 1.3.8.3.</b> This used to return a value only for
    /// <see cref="JsonValueKind.String"/>, which was fine for every field it was written for and
    /// silently wrong for the one added in 1.3.8.2: Forma sends
    /// <c>"revitProjectVersion": 2026</c> as a JSON <b>number</b>. So the crawler's Revit column
    /// read "—" for every model on a hub that was reporting the year perfectly well, and the
    /// question of whether this hub carried the field at all — open since 1.3.5.5 — was answered
    /// "no" by our own reader rather than by Forma. A silent empty string is the worst possible
    /// failure for a field that governs whether a model may run.</para>
    /// </summary>
    private static string ExtData(JsonElement el, string name)
    {
        if (!el.TryGetProperty("attributes", out JsonElement attrs) ||
            !attrs.TryGetProperty("extension", out JsonElement ext) ||
            !ext.TryGetProperty("data", out JsonElement data) ||
            !data.TryGetProperty(name, out JsonElement v))
            return "";

        return ScalarText(v);
    }

    /// <summary>
    /// A JSON scalar as text, whatever kind Forma chose to send it as. Split out from
    /// <see cref="ExtData"/> so the offline harness can pin it: the 1.3.8.2 defect was a
    /// string-only read returning <c>""</c> for a perfectly good number, and "returns empty for a
    /// value that is present" is the one failure mode of this function that nothing downstream can
    /// detect.
    /// </summary>
    internal static string ScalarText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        // Invariant, so a Revit year never picks up a thousands separator from the PC's locale.
        JsonValueKind.Number => v.TryGetInt64(out long n)
            ? n.ToString(CultureInfo.InvariantCulture)
            : v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    /// <summary>
    /// The names of every key present under a version's <c>attributes.extension.data</c>, comma
    /// separated. Diagnostic only — it is what the browser's "Copy scan details" dump reports, so
    /// "which fields does this hub actually send?" can be answered without a code change. That
    /// question has started every Forma investigation in this project's history.
    /// </summary>
    private static string ExtDataKeys(JsonElement el)
    {
        if (!el.TryGetProperty("attributes", out JsonElement attrs) ||
            !attrs.TryGetProperty("extension", out JsonElement ext) ||
            !ext.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind != JsonValueKind.Object)
            return "";

        return string.Join(", ", data.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>
    /// Whether <paramref name="item"/> is a copy / shared / Bridge-consumed instance rather than
    /// the original: its version's <c>originalItemUrn</c> names a lineage other than its own id.
    /// See <see cref="FormaModelItem.IsCopyOrShare"/> for why that matters more than it sounds.
    /// </summary>
    private static bool IsCopyOf(JsonElement item, JsonElement version)
    {
        string itemUrn = Str(item, "id");
        string originalUrn = ExtData(version, "originalItemUrn");
        return originalUrn.Length > 0 && itemUrn.Length > 0 &&
               !string.Equals(originalUrn, itemUrn, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>attributes.extension.type</c> — e.g. <c>versions:autodesk.bim360:C4RModel</c>.
    /// Diagnostic: it does NOT distinguish a copy from a native model (both are C4RModel), which
    /// is why <see cref="FormaModelItem.IsCopyOrShare"/> keys on the lineage instead.</summary>
    private static string ExtType(JsonElement el)
    {
        if (el.TryGetProperty("attributes", out JsonElement attrs) &&
            attrs.TryGetProperty("extension", out JsonElement ext) &&
            ext.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String)
            return t.GetString()!;
        return "";
    }

    private static string StripB(string projectId) =>
        projectId.StartsWith("b.", StringComparison.OrdinalIgnoreCase) ? projectId[2..] : projectId;

    /// <summary>Index the response's <c>included[]</c> tip versions by their id.</summary>
    private static Dictionary<string, JsonElement> BuildIncludedMap(JsonDocument doc)
    {
        var map = new Dictionary<string, JsonElement>();
        if (doc.RootElement.TryGetProperty("included", out JsonElement inc) &&
            inc.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement v in inc.EnumerateArray())
            {
                string id = Str(v, "id");
                if (id.Length > 0) map[id] = v;
            }
        }
        return map;
    }

    /// <summary>
    /// Resolve an item's tip version — first from the already-loaded <c>included[]</c>
    /// map, else by fetching the item's <c>/tip</c>. The returned element is safe to
    /// read within the caller's scope (the fallback clones out of its temp document).
    /// </summary>
    private bool TryGetTipVersion(
        JsonElement item, Dictionary<string, JsonElement> included, string projectId, out JsonElement version)
    {
        version = default;

        string tipId = "";
        if (item.TryGetProperty("relationships", out JsonElement rel) &&
            rel.TryGetProperty("tip", out JsonElement tip) &&
            tip.TryGetProperty("data", out JsonElement td) && td.ValueKind == JsonValueKind.Object)
            tipId = Str(td, "id");

        if (tipId.Length > 0 && included.TryGetValue(tipId, out version))
            return true;

        string itemId = Str(item, "id");
        if (itemId.Length == 0) return false;
        try
        {
            using JsonDocument doc = GetJson(
                $"{BaseUrl}/data/v1/projects/{projectId}/items/{Uri.EscapeDataString(itemId)}/tip");
            if (doc.RootElement.TryGetProperty("data", out JsonElement data))
            {
                version = data.Clone(); // outlive the disposed document
                return true;
            }
        }
        catch
        {
            // Fall through — treated as "no version" by the caller.
        }
        return false;
    }

    /// <summary>
    /// Pull <c>modelGuid</c>/<c>projectGuid</c> from a version's
    /// <c>attributes.extension.data</c> — present for Revit cloud-worksharing (C4R)
    /// models. Returns false for non-Revit items (they lack a model GUID).
    /// </summary>
    private static bool TryCloudGuids(JsonElement version, out Guid modelGuid, out Guid projectGuid)
    {
        modelGuid = Guid.Empty;
        projectGuid = Guid.Empty;
        if (version.ValueKind != JsonValueKind.Object) return false;
        if (!version.TryGetProperty("attributes", out JsonElement attrs)) return false;
        if (!attrs.TryGetProperty("extension", out JsonElement ext)) return false;
        if (!ext.TryGetProperty("data", out JsonElement data)) return false;

        if (data.TryGetProperty("modelGuid", out JsonElement mg) && mg.ValueKind == JsonValueKind.String)
            Guid.TryParse(mg.GetString(), out modelGuid);
        if (data.TryGetProperty("projectGuid", out JsonElement pg) && pg.ValueKind == JsonValueKind.String)
            Guid.TryParse(pg.GetString(), out projectGuid);
        return modelGuid != Guid.Empty;
    }

    /// <summary>
    /// The status line under the model list. Since 1.3.8.2 a model without cloud GUIDs is LISTED
    /// (greyed) rather than dropped, so this no longer has to hedge with "not cloud-worksharing
    /// models?" about files the user could not see — it says how many are there and how many cannot
    /// be run. A model whose tip version could not be fetched at all is still genuinely absent.
    /// </summary>
    private static string DescribeScan(
        int revitItems, int listed, int noVersion, int noGuid, int noYear, int copies)
    {
        if (revitItems == 0) return "no Revit models in this folder.";
        var caveats = new List<string>();
        if (copies > 0) caveats.Add($"{copies} copied/shared from another folder, shown greyed");
        if (noGuid > 0) caveats.Add($"{noGuid} not cloud-worksharing, shown greyed");
        // Says out loud whether this hub reports revitProjectVersion at all. It does — 1.3.8.2 read
        // it as a string when Forma sends a number, so this said "all of them" on a hub that was
        // answering perfectly. Kept because it is still the honest way to report a genuine absence.
        if (noYear > 0) caveats.Add($"{noYear} with no Revit year reported by Forma");
        if (noVersion > 0) caveats.Add($"{noVersion} whose version could not be read, not listed");
        string tail = caveats.Count > 0 ? $" ({string.Join("; ", caveats)})" : "";
        return $"{listed} Revit model(s){tail}.";
    }

    private static string NormalizeRegion(string region) =>
        region.IndexOf("EMEA", StringComparison.OrdinalIgnoreCase) >= 0 ? "EMEA" : "US";

    private static List<FormaEntry> Sorted(List<FormaEntry> entries) =>
        entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
