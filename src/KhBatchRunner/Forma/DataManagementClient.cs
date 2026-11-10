using System.Net.Http;
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
public sealed partial class DataManagementClient
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
}
