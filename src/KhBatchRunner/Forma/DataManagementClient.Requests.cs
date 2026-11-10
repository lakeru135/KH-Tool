using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KhBatchRunner.Forma;

public sealed partial class DataManagementClient
{
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
}
