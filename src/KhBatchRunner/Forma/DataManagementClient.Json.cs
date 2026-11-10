using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KhBatchRunner.Auth;

namespace KhBatchRunner.Forma;

public sealed partial class DataManagementClient
{
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
