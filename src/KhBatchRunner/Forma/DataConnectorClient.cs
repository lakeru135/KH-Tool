using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace KhBatchRunner.Forma;

/// <summary>One saved Data Connector extract request (what Forma's UI calls a scheduled extract).</summary>
public sealed class DataConnectorRequest
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The Forma project this extract covers, as a BARE guid (Data Connector does not use
    /// Data Management's <c>b.</c> prefix). Empty for an account-wide extract.</summary>
    public string ProjectId { get; set; } = "";

    /// <summary>
    /// The project's real name, filled in by whoever can resolve it — see
    /// <see cref="DataConnectorClient.NameProjects"/>. Left empty when it could not be resolved,
    /// and <see cref="DisplayName"/> then falls back to something still recognisable.
    /// </summary>
    public string ProjectName { get; set; } = "";

    public string ScheduleInterval { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// What the pickers show.
    ///
    /// <para>Forma names an extract it created itself <c>"Insight Data Extraction for Account:
    /// c0ffee01-…, Project: c0ffee04-…"</c> — two GUIDs and no way to tell one project's extract
    /// from another's. So the project NAME leads when it is known, then the schedule, and the
    /// account-wide case says so in words rather than showing an empty project. Forma's own
    /// description is used only when a person actually wrote one.</para>
    /// </summary>
    public string DisplayName
    {
        get
        {
            string what = string.IsNullOrWhiteSpace(ProjectName)
                ? string.IsNullOrWhiteSpace(ProjectId) ? "Whole account" : "Project " + Short(ProjectId)
                : ProjectName;
            string when = ScheduleLabel();
            string authored = AuthoredDescription();
            return authored.Length > 0
                ? $"{what} — {when} — {authored}"
                : $"{what} — {when}";
        }
    }

    /// <summary>Forma's schedule words (<c>ONE_TIME</c>, <c>WEEK</c>) as something readable, and
    /// whether it is still running at all — a paused extract produces no new data, which is worth
    /// knowing before it is chosen for a nightly download.</summary>
    private string ScheduleLabel()
    {
        string interval = (ScheduleInterval ?? "").Replace('_', ' ').Trim().ToLowerInvariant();
        string label = interval.Length == 0 ? "extract"
            : interval == "one time" ? "one-time"
            : interval == "day" ? "daily"
            : interval == "week" ? "weekly"
            : interval == "month" ? "monthly"
            : interval == "year" ? "yearly"
            : interval;
        return IsActive ? label : label + ", paused";
    }

    /// <summary>
    /// The description when a person wrote it, empty when it is Forma's own generated sentence.
    ///
    /// <para>Matched on the generated form's fixed opening rather than on its GUIDs: the point is
    /// to drop a machine-written string, and a description someone typed is worth showing however
    /// long it is (bounded, because it lands in a combo box).</para>
    /// </summary>
    private string AuthoredDescription()
    {
        string d = (Description ?? "").Trim();
        if (d.Length == 0) return "";
        if (d.StartsWith("Insight Data Extraction for Account", StringComparison.OrdinalIgnoreCase))
            return "";
        return d.Length <= 60 ? d : d.Substring(0, 59) + "…";
    }

    public string ShortId => Short(Id);

    private static string Short(string id) =>
        (id ?? "").Length >= 8 ? id.Substring(0, 8) : id ?? "";

    public override string ToString() => DisplayName;
}

/// <summary>
/// One run of a <see cref="DataConnectorRequest"/>.
///
/// <para>Two status fields, and both matter: <c>status</c> is where the run is ("queued",
/// "running", "complete") and <c>completionStatus</c> is how it ended ("success", "failed"). A job
/// is only safe to download when the first says complete AND the second says success — a complete
/// job that failed has a data listing, and it is empty or partial.</para>
/// </summary>
public sealed class DataConnectorJob
{
    public string Id { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Status { get; set; } = "";
    public string CompletionStatus { get; set; } = "";
    public int Progress { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public bool IsComplete => string.Equals(Status, "complete", StringComparison.OrdinalIgnoreCase);
    public bool IsSuccess => IsComplete &&
        string.Equals(CompletionStatus, "success", StringComparison.OrdinalIgnoreCase);

    /// <summary>How to describe this job in a log row or a status bar.</summary>
    public string StateLabel =>
        IsSuccess ? "success"
        : IsComplete ? $"complete but {(string.IsNullOrWhiteSpace(CompletionStatus) ? "not successful" : CompletionStatus)}"
        : $"{(string.IsNullOrWhiteSpace(Status) ? "unknown" : Status)} ({Progress}%)";
}

/// <summary>One file inside a finished job's extract.</summary>
public sealed class DataConnectorFile
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public DateTime? CreatedAt { get; set; }

    public bool IsCsv => Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
    public bool IsZip => Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Forma Data Connector REST surface, read-only: list a user's extract requests, find a
/// request's runs, and download a finished run's CSVs.
///
/// <para><b>This client never creates or triggers an extract.</b> Forma's own schedule does that
/// (typically overnight); the add-in's job is to collect what it produced. Creating a request
/// needs <c>data:write</c> and would put a second, competing schedule on the account.</para>
///
/// <para>Shaped after <see cref="DataManagementClient"/> deliberately — same base URL, same
/// one-HttpClient-per-instance lifetime, same synchronous-over-async surface, same retry rules,
/// and the same <see cref="HttpRequestException"/> message format ("&lt;status&gt; &lt;reason&gt;
/// for &lt;url&gt;"), which <see cref="Presets.AccessDenied"/> parses to recognise a permissions
/// failure. Two differences are forced by the API, and are the reason this is a separate class
/// rather than more methods on the other one:</para>
/// <list type="bullet">
/// <item>Data Connector is not JSON:API. It pages with <c>limit</c>/<c>offset</c> and a
/// <c>pagination</c> object instead of <c>links.next</c>, so <see cref="DataManagementClient"/>'s
/// next-link reader does not apply. The data-listing endpoint is a bare array with no envelope
/// at all.</item>
/// <item>The file endpoints hand back a short-lived signed S3 URL rather than the bytes. That URL
/// carries its own credentials in the query string, so the Autodesk bearer token must NOT be sent
/// with it — S3 rejects a request that presents two forms of auth.</item>
/// </list>
///
/// <para>Account id: the <b>bare</b> GUID, without Data Management's <c>b.</c> prefix. Verified
/// against a live account 2026-09-18; <see cref="AccountIdFrom"/> is the one place that strips it.</para>
/// </summary>
public sealed class DataConnectorClient
{
    private const string BaseUrl = "https://developer.api.autodesk.com/data-connector/v1";

    /// <summary>Matches <see cref="DataManagementClient"/>: a listing is small, but a download is
    /// not, and the default 100 seconds is short for a multi-megabyte CSV on a site link.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private const int MaxAttempts = 3;

    /// <summary>Forma's page size cap for these endpoints; asking for more is rejected, not clamped.</summary>
    private const int PageSize = 100;

    /// <summary>A guard against an unbounded loop if a server ever stops advancing the offset.</summary>
    private const int MaxPages = 50;

    private readonly Auth.ITokenProvider _tokens;
    private readonly HttpClient _http = new() { Timeout = RequestTimeout };

    public DataConnectorClient(Auth.ITokenProvider tokens) => _tokens = tokens;

    /// <summary>
    /// The Data Connector account id for a Data Management hub id — the same GUID with the
    /// <c>b.</c> prefix removed. Accepts either spelling so a caller can pass a stored hub id
    /// straight through.
    /// </summary>
    public static string AccountIdFrom(string hubId)
    {
        string id = (hubId ?? "").Trim();
        return id.StartsWith("b.", StringComparison.OrdinalIgnoreCase) ? id.Substring(2) : id;
    }

    /// <summary>
    /// Every extract request on the account that the signed-in user can see.
    ///
    /// <para>Note the API's own rule: it returns the requests <b>that user created</b>. An extract
    /// set up by a colleague is invisible here however much of an admin the caller is — which is
    /// why a "no extracts found" result names the signed-in account rather than claiming there are
    /// none.</para>
    /// </summary>
    public List<DataConnectorRequest> GetRequests(string accountId)
    {
        var requests = new List<DataConnectorRequest>();
        foreach (JsonElement el in GetPaged($"accounts/{AccountIdFrom(accountId)}/requests"))
        {
            requests.Add(new DataConnectorRequest
            {
                Id = Str(el, "id"),
                Description = Str(el, "description"),
                ProjectId = Str(el, "projectId"),
                ScheduleInterval = Str(el, "scheduleInterval"),
                IsActive = Bool(el, "isActive"),
                CreatedAt = Date(el, "createdAt")
            });
        }
        return requests;
    }

    /// <summary>
    /// Fills in <see cref="DataConnectorRequest.ProjectName"/> for every request that names a
    /// project, using Data Management's project list for the same hub.
    ///
    /// <para><b>The id formats differ</b>, which is the whole reason this is a method and not a
    /// dictionary lookup at the call site: Data Management returns <c>b.&lt;guid&gt;</c> and Data
    /// Connector returns the bare <c>&lt;guid&gt;</c>, so a raw comparison matches nothing and
    /// silently leaves every extract unnamed. <see cref="AccountIdFrom"/> is what makes them
    /// comparable.</para>
    ///
    /// <para>Best effort by design: names are a convenience, and an account whose project list
    /// cannot be read must still be able to download its extract. A failure leaves the names empty
    /// and is swallowed — the ids still identify the extract, and every other call in this dialog
    /// reports its own failures already.</para>
    /// </summary>
    /// <param name="dm">The Data Management client; the project list is one request per hub.</param>
    /// <param name="hub">The hub whose projects to read — the same account the requests came from.</param>
    public static void NameProjects(DataManagementClient dm, FormaEntry hub,
        IEnumerable<DataConnectorRequest> requests)
    {
        List<DataConnectorRequest> needing = requests
            .Where(r => !string.IsNullOrWhiteSpace(r.ProjectId))
            .ToList();
        if (needing.Count == 0) return;

        var byBareId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (FormaEntry project in dm.GetProjects(hub))
            {
                string bare = AccountIdFrom(project.ProjectId);
                if (bare.Length > 0) byBareId[bare] = project.Name;
            }
        }
        catch
        {
            return; // names are a convenience; the ids already identify the extract
        }

        foreach (DataConnectorRequest request in needing)
            if (byBareId.TryGetValue(request.ProjectId, out string? name))
                request.ProjectName = name;
    }

    /// <summary>Every run of one request, newest first.</summary>
    public List<DataConnectorJob> GetJobs(string accountId, string requestId)
    {
        var jobs = new List<DataConnectorJob>();
        foreach (JsonElement el in GetPaged($"accounts/{AccountIdFrom(accountId)}/requests/{requestId}/jobs"))
        {
            jobs.Add(new DataConnectorJob
            {
                Id = Str(el, "id"),
                RequestId = Str(el, "requestId"),
                Status = Str(el, "status"),
                CompletionStatus = Str(el, "completionStatus"),
                Progress = Int(el, "progress"),
                CreatedAt = Date(el, "createdAt"),
                CompletedAt = Date(el, "completedAt")
            });
        }
        return jobs
            .OrderByDescending(j => j.CompletedAt ?? j.CreatedAt ?? DateTime.MinValue)
            .ToList();
    }

    /// <summary>
    /// The newest run of <paramref name="requestId"/> that finished successfully, and the newest
    /// run of any state — so a caller can say "last night's run is still going" instead of
    /// silently handing back the run from the night before.
    /// </summary>
    public (DataConnectorJob? Latest, DataConnectorJob? LatestSuccessful) GetLatestJobs(
        string accountId, string requestId)
    {
        List<DataConnectorJob> jobs = GetJobs(accountId, requestId);
        return (jobs.FirstOrDefault(), jobs.FirstOrDefault(j => j.IsSuccess));
    }

    /// <summary>
    /// The files a finished job produced.
    ///
    /// <para>Unlike every other endpoint here this one answers with a bare JSON array — no
    /// <c>pagination</c>, no <c>results</c> — so it does not go through <see cref="GetPaged"/>.</para>
    /// </summary>
    public List<DataConnectorFile> GetJobFiles(string accountId, string jobId)
    {
        string raw = GetRaw($"accounts/{AccountIdFrom(accountId)}/jobs/{jobId}/data-listing");
        var files = new List<DataConnectorFile>();
        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement root = doc.RootElement;
        // Tolerate an enveloped answer too: the listing is the one endpoint whose shape differs
        // from its siblings, and a future alignment must not read as "the extract is empty".
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("results", out JsonElement wrapped))
            root = wrapped;
        if (root.ValueKind != JsonValueKind.Array) return files;

        foreach (JsonElement el in root.EnumerateArray())
        {
            files.Add(new DataConnectorFile
            {
                Name = Str(el, "name"),
                Size = Long(el, "size"),
                CreatedAt = Date(el, "createdAt")
            });
        }
        return files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Downloads one file of a job's extract to <paramref name="destPath"/> and returns the bytes
    /// written.
    ///
    /// <para>Two hops: ask Data Connector for the file (it answers with a signed S3 URL valid for
    /// about a minute), then fetch that URL. The signed URL is requested immediately before it is
    /// used and never stored — a listing of forty CSVs takes longer to download than one URL
    /// stays valid.</para>
    ///
    /// <para>The response is streamed to a temporary file and moved into place only once it is
    /// complete, so an interrupted download cannot leave a half-written CSV that looks like a
    /// successful pull to the next run.</para>
    /// </summary>
    public long DownloadFile(string accountId, string jobId, string fileName, string destPath)
    {
        string signed = GetSignedUrl(accountId, jobId, fileName);
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string temp = destPath + ".part";
        try
        {
            long bytes = System.Threading.Tasks.Task.Run(async () =>
            {
                // No Authorization header: the signature in the query string IS the credential,
                // and S3 rejects a request that also presents a bearer token.
                using var req = new HttpRequestMessage(HttpMethod.Get, signed);
                using HttpResponseMessage resp =
                    await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new HttpRequestException(
                        $"{(int)resp.StatusCode} {resp.ReasonPhrase} downloading {fileName}\n{Snip(text)}");
                }

                using Stream source = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(file).ConfigureAwait(false);
                return file.Length;
            }).GetAwaiter().GetResult();

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(temp, destPath);
            return bytes;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The signed URL for one file of a job's extract.
    ///
    /// <para>The property name is read defensively. Autodesk's sample drives this endpoint through
    /// Postman variables rather than naming the field, so an unexpected shape must fail with the
    /// body in the message — a null here would otherwise surface as an unexplained download error
    /// much further along.</para>
    /// </summary>
    private string GetSignedUrl(string accountId, string jobId, string fileName)
    {
        string raw = GetRaw($"accounts/{AccountIdFrom(accountId)}/jobs/{jobId}/data/{Uri.EscapeDataString(fileName)}");
        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? "";
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in new[] { "signedUrl", "url", "signed_url", "downloadUrl" })
            {
                if (root.TryGetProperty(name, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
            }
        }
        throw new HttpRequestException(
            $"Data Connector returned no download URL for {fileName}. Response: {Snip(raw)}");
    }

    /// <summary>
    /// Walks a <c>limit</c>/<c>offset</c> listing and yields every item.
    ///
    /// <para>The stop condition is a page that returns nothing, not an arithmetic comparison
    /// against <c>totalResults</c>: the count is the number of rows the caller may see, and a row
    /// that disappears between pages would otherwise leave the loop asking for an offset that is
    /// never satisfied.</para>
    /// </summary>
    private List<JsonElement> GetPaged(string path)
    {
        var items = new List<JsonElement>();
        for (int page = 0; page < MaxPages; page++)
        {
            int offset = page * PageSize;
            string sep = path.Contains("?") ? "&" : "?";
            string raw = GetRaw($"{path}{sep}limit={PageSize}&offset={offset}");

            // Parsed into a document that outlives this iteration: JsonElement is a view over its
            // JsonDocument's buffer, so returning elements from a disposed document would hand the
            // caller freed memory. Cloned instead — the lists here are small.
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;
            JsonElement results = root;
            if (root.ValueKind == JsonValueKind.Object &&
                !root.TryGetProperty("results", out results))
                return items;
            if (results.ValueKind != JsonValueKind.Array) return items;

            int count = results.GetArrayLength();
            foreach (JsonElement el in results.EnumerateArray()) items.Add(el.Clone());
            if (count < PageSize) return items;
        }
        return items;
    }

    /// <summary>
    /// One GET, retried when the failure is the kind that passes. Same rules as
    /// <see cref="DataManagementClient"/>: a fresh token per attempt, exponential backoff that
    /// honours <c>Retry-After</c>, and no retry at all for 401/403/404 — those are answers.
    /// </summary>
    private string GetRaw(string path)
    {
        string url = $"{BaseUrl}/{path}";
        return System.Threading.Tasks.Task.Run(async () =>
        {
            Exception? last = null;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                TimeSpan? wait = null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    string token = _tokens.GetAccessToken();
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) return text;

                    var failure = new HttpRequestException(
                        $"{(int)resp.StatusCode} {resp.ReasonPhrase} for {url}\n{Snip(text)}");
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

    private static bool IsTransient(System.Net.HttpStatusCode code) =>
        (int)code == 429 || (int)code == 500 || (int)code == 502 || (int)code == 503 || (int)code == 504;

    private static bool IsTimeout(Exception ex) =>
        ex is TaskCanceledException or OperationCanceledException ||
        (ex is HttpRequestException && ex.InnerException is TimeoutException);

    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        RetryConditionHeaderValue? h = resp.Headers.RetryAfter;
        if (h == null) return null;
        TimeSpan? wait = h.Delta ?? (h.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is not { } w || w <= TimeSpan.Zero) return null;
        return w > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : w;
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(2 * Math.Pow(3, attempt - 1));

    /// <summary>Error bodies go into exception messages that reach a MessageBox and a CSV log row —
    /// a full HTML error page in either is unreadable.</summary>
    private static string Snip(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= 500 ? text : text.Substring(0, 500) + "…";

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out int i) ? i : 0;

    private static long Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out long l) ? l : 0;

    /// <summary>
    /// An ISO-8601 UTC timestamp as LOCAL time, or null.
    ///
    /// <para>Local because every date this client produces is read by a person deciding whether
    /// last night's extract ran — and "03:20" against a 01:00 schedule reads as a failure when it
    /// is really 10:20 local time (UTC+7).</para>
    /// </summary>
    private static DateTime? Date(JsonElement el, string name)
    {
        string raw = Str(el, name);
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc)
            ? utc.ToLocalTime()
            : null;
    }

    // Exposed to the offline harness for the same reason DataManagementClient exposes its own:
    // the retry rules are pure predicates, and pinning them needs no network.
    internal static bool IsTransientForTest(System.Net.HttpStatusCode code) => IsTransient(code);
    internal static TimeSpan BackoffForTest(int attempt) => Backoff(attempt);
}
