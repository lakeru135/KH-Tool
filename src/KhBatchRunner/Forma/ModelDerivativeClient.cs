using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KhBatchRunner.Auth;

namespace KhBatchRunner.Forma;

/// <summary>
/// Thin client over the APS <b>Model Derivative</b> API, used by Check Sheet Publish to read which
/// <b>sheet views</b> a cloud model was published/translated with. When a Revit model is published
/// to Forma it is translated; the resulting derivative's 2D viewables are its sheets. We list them
/// via <c>/modelderivative/v2/designdata/{urn}/metadata</c> and match their names against the
/// user's expected sheet numbers.
///
/// The <c>urn</c> is the model's <b>version URN</b> (from Data Management) Base64url-encoded.
/// Auth reuses the signed-in Revit session token (<see cref="ITokenProvider"/>), same as
/// <see cref="DataManagementClient"/>. A 401/403 is almost certainly APS enrollment / token scope
/// (needs <c>data:read</c>/<c>viewables:read</c>), not this code.
/// </summary>
public sealed class ModelDerivativeClient
{
    private const string BaseUrl = "https://developer.api.autodesk.com";
    private readonly ITokenProvider _tokens;
    private readonly HttpClient _http = new();

    public ModelDerivativeClient(ITokenProvider tokens) => _tokens = tokens;

    /// <summary>
    /// The 2D viewable (sheet) names in the model's published derivative, or <c>null</c> if the
    /// model has no completed translation (never published / still processing / no access). An
    /// empty list means it translated but exposes no 2D sheet views.
    /// </summary>
    public IReadOnlyList<string>? GetPublishedSheetViewNames(string versionUrn, string region)
    {
        string urn = Base64Url(versionUrn);
        // Model Derivative is region-scoped; EMEA data lives behind /regions/eu.
        string regionSeg = string.Equals(region, "EMEA", StringComparison.OrdinalIgnoreCase)
            ? "/regions/eu" : "";
        string url = $"{BaseUrl}/modelderivative/v2{regionSeg}/designdata/{urn}/metadata";

        JsonDocument? doc = TryGetJson(url);
        if (doc is null) return null;
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) ||
                !data.TryGetProperty("metadata", out JsonElement md) ||
                md.ValueKind != JsonValueKind.Array)
                return null;

            var names = new List<string>();
            foreach (JsonElement v in md.EnumerateArray())
            {
                string role = v.TryGetProperty("role", out JsonElement r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString()! : "";
                if (role != "2d") continue; // 2D viewables are the sheets
                string name = v.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()! : "";
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }
    }

    /// <summary>GET that returns null on any non-success (404/202-pending/error) instead of throwing — a model that never translated simply has no manifest.</summary>
    private JsonDocument? TryGetJson(string url)
    {
        try
        {
            string? body = System.Threading.Tasks.Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                string token = _tokens.GetAccessToken();
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }).GetAwaiter().GetResult();

            return string.IsNullOrEmpty(body) ? null : JsonDocument.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Raw manifest fetch for diagnostics — "HTTP {code}: {snippet}". Lets the self-test see WHY
    /// <see cref="GetPublishedSheetViewNames"/> returned null (404 = never translated, 202/pending
    /// = still translating, 200 with a "success"/"inprogress" status, 401/403 = scope/enrollment).
    /// </summary>
    public string ManifestDiagnostic(string versionUrn, string region)
    {
        string urn = Base64Url(versionUrn);
        string regionSeg = string.Equals(region, "EMEA", StringComparison.OrdinalIgnoreCase) ? "/regions/eu" : "";
        string url = $"{BaseUrl}/modelderivative/v2{regionSeg}/designdata/{urn}/manifest";
        try
        {
            (int status, string body) = System.Threading.Tasks.Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                string token = _tokens.GetAccessToken();
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
                string b = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ((int)resp.StatusCode, b);
            }).GetAwaiter().GetResult();
            string snip = body.Length > 500 ? body.Substring(0, 500) : body;
            return $"HTTP {status}: {snip}";
        }
        catch (Exception ex)
        {
            return "manifest error: " + ex.Message;
        }
    }

    private static string Base64Url(string s)
    {
        string b = Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        return b.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
