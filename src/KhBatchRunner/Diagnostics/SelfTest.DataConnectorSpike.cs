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
    /// Forma Data Connector auth probe (<c>KHBATCH_DC_SPIKE=1</c>): answers the one question that
    /// decides whether a "download last night's extract" feature can live in this add-in at all —
    /// does the token Revit already holds reach <c>/data-connector/v1/…</c>?
    ///
    /// <para>The doubt is real: Data Connector wants a 3-legged token from a client id the Forma
    /// account has whitelisted under Custom Integrations, and ours is Revit's own client id, not
    /// one we registered. Data Management accepts it; C4R publish did not (which is why that
    /// button is archived off the ribbon). So this probe asks Forma rather than the documentation,
    /// read-only, before any UI is built on the answer.</para>
    ///
    /// <para>Every step prints its raw HTTP status and body (truncated) and swallows its own
    /// exception: a probe that aborts on the first 403 tells us less than one that walks the whole
    /// chain — requests → jobs → data-listing — and shows exactly where the door closes. It also
    /// tries the account id BOTH ways (bare GUID and <c>b.</c>-prefixed), because the published
    /// samples call that path variable <c>hub_id_b360</c> without saying which form it holds.</para>
    ///
    /// <para>Optional <c>KHBATCH_DC_ACCOUNT=&lt;guid&gt;</c> skips hub discovery and probes that
    /// account directly — for the case where the signed-in user sees many hubs.</para>
    /// </summary>
    private static void RunDataConnectorSpike()
    {
        Append($"=== Data Connector auth probe {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

        var tokens = new SessionTokenProvider();
        string token = tokens.GetAccessToken();
        Append($"Token: length={token?.Length ?? 0}; LastError={tokens.LastError ?? "(none)"}");
        if (string.IsNullOrEmpty(token))
        {
            Append("No token — Revit isn't signed in to Forma in this session. Aborting.");
            return;
        }
        // Data Connector documents data:read (+ data:write to CREATE a request, which this feature
        // never does). Printed rather than asserted: a missing scope explains a 403 we would
        // otherwise blame on the integration whitelist.
        Append("token scopes: " + TokenScopes(token));

        var accounts = new List<string>();
        string configured = Environment.GetEnvironmentVariable("KHBATCH_DC_ACCOUNT") ?? "";
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Append($"account from KHBATCH_DC_ACCOUNT: {configured}");
            accounts.Add(configured.Trim());
        }
        else
        {
            var dm = new DataManagementClient(tokens);
            List<FormaEntry> hubs = Step("GetHubs", () => dm.GetHubs()) ?? new();
            Append($"hubs: {hubs.Count}");
            foreach (FormaEntry hub in hubs)
            {
                Append($"  hub: name={hub.Name} | id={hub.Id} | region={hub.Region}");
                accounts.Add(hub.Id);
            }
            if (accounts.Count == 0) { Append("No hubs. Aborting."); return; }
        }

        foreach (string account in accounts)
        {
            string bare = account.StartsWith("b.", StringComparison.OrdinalIgnoreCase) ? account.Substring(2) : account;
            Append($"-- account {account} (bare {bare})");

            // Both spellings, because a 404 on one form and a 200 on the other is the answer to a
            // question the samples leave open — and a 403 on BOTH is a different answer entirely.
            (int status, string body) = DcGet(token, $"accounts/{bare}/requests");
            if (status != 200)
            {
                (int prefixed, string prefixedBody) = DcGet(token, $"accounts/b.{bare}/requests");
                if (prefixed == 200) { status = prefixed; body = prefixedBody; bare = "b." + bare; }
            }
            if (status != 200) continue;

            string requestId = FirstJsonId(body, "requests");
            if (string.IsNullOrEmpty(requestId)) { Append("   (no extract requests on this account)"); continue; }
            Append($"   newest request id: {requestId}");

            (int jobStatus, string jobBody) = DcGet(token, $"accounts/{bare}/requests/{requestId}/jobs");
            if (jobStatus != 200) continue;

            string jobId = FirstJsonId(jobBody, "jobs");
            if (string.IsNullOrEmpty(jobId)) { Append("   (request has no jobs yet)"); continue; }
            Append($"   newest job id: {jobId}");

            DcGet(token, $"accounts/{bare}/jobs/{jobId}/data-listing");
        }

        Append("=== Data Connector probe complete ===");
    }

    /// <summary>
    /// One read-only Data Connector GET, logged as status + truncated body. Never throws.
    ///
    /// <para>Deliberately self-contained rather than routed through
    /// <see cref="DataManagementClient"/>: this probe exists to decide whether a Data Connector
    /// client is worth writing, and adding REST surface to the shared client (which compiles into
    /// the Revit-free controller too) before that answer is in would be building on the question.</para>
    /// </summary>
    private static (int Status, string Body) DcGet(string token, string path)
    {
        string url = "https://developer.api.autodesk.com/data-connector/v1/" + path;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(1) };
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            // Sync-over-async off the UI thread, the same shape DataManagementClient.GetRaw uses to
            // avoid a WinForms/Revit message-pump deadlock.
            (int status, string body) = System.Threading.Tasks.Task.Run(async () =>
            {
                using System.Net.Http.HttpResponseMessage resp = await http.SendAsync(req).ConfigureAwait(false);
                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ((int)resp.StatusCode, text);
            }).GetAwaiter().GetResult();

            Append($"   GET {path} -> HTTP {status}  {Truncate(body.Replace("\r", "").Replace("\n", " "), 900)}");
            return (status, body);
        }
        catch (Exception ex)
        {
            Append($"   GET {path} -> EXCEPTION {ex.Message}");
            return (0, "");
        }
    }

    /// <summary>
    /// The <c>id</c> of the first element of a Data Connector list response — the newest, since
    /// these endpoints return newest-first by default. Reads both the paginated shape
    /// (<c>{"pagination":…,"results":[…]}</c>) and a bare array, and reports which one it found
    /// so a shape change shows up as a probe line rather than as an empty result.
    /// </summary>
    private static string FirstJsonId(string body, string what)
    {
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
            System.Text.Json.JsonElement root = doc.RootElement;
            System.Text.Json.JsonElement list = root;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (!root.TryGetProperty("results", out list) && !root.TryGetProperty("data", out list))
                {
                    Append($"   ({what}: response is an object with neither 'results' nor 'data')");
                    return "";
                }
            }
            if (list.ValueKind != System.Text.Json.JsonValueKind.Array) return "";
            Append($"   {what} returned: {list.GetArrayLength()}");
            foreach (System.Text.Json.JsonElement item in list.EnumerateArray())
                return item.TryGetProperty("id", out System.Text.Json.JsonElement id) ? id.ToString() : "";
            return "";
        }
        catch (Exception ex) { Append($"   ({what}: parse failed — {ex.Message})"); return ""; }
    }

    /// <summary>The "scope" claim of a JWT access token (base64url payload) — to confirm data:write is present.</summary>
    private static string TokenScopes(string token)
    {
        try
        {
            string[] parts = token.Split('.');
            if (parts.Length < 2) return "(not a JWT)";
            string p = parts[1].Replace('-', '+').Replace('_', '/');
            p += new string('=', (4 - p.Length % 4) % 4);
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(p));
            return doc.RootElement.TryGetProperty("scope", out var s) ? s.ToString() : "(no scope claim)";
        }
        catch (Exception ex) { return "(decode failed: " + ex.Message + ")"; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
}
