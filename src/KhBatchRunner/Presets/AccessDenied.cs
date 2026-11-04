namespace KhBatchRunner.Presets;

/// <summary>
/// Recognises "the signed-in Autodesk account is not allowed to do this" — as opposed to any other
/// failure — from either half of the product.
///
/// <para><b>Why it is worth telling apart.</b> A transient failure deserves a retry and an offline
/// fallback; a denial deserves neither. Before 1.5.0 both looked identical:
/// <see cref="PresetResolver"/> caught every folder-scan failure alike and fell back to the set's
/// save-time snapshot, so a run under an account without access proceeded to attempt every stale
/// GUID it had saved, and <c>BatchEngine.LogOpenFailure</c> classified exactly one thing — a
/// wrong Revit version — so a permission failure was logged as its own raw exception text with no
/// mention of the account it was refused to.</para>
///
/// <para><b>Two vocabularies, one question.</b> The REST half fails with an
/// <c>HttpRequestException</c> whose message carries the status line
/// (<c>DataManagementClient.GetRaw</c> formats it that way, and already treats 401/403 as
/// "an answer, not a hiccup" by never retrying them). The Revit half throws
/// <c>RevitServerUnauthorizedException</c>, <c>CentralModelMissing</c> or <c>LinkNotFound</c> —
/// the three 0.9.1 proved a model outside the account produces, which until now existed in this
/// codebase only as a comment. The two sets cannot be confused with each other, so one predicate
/// answers for both.</para>
/// </summary>
public static class AccessDenied
{
    /// <summary>Revit exception TYPE names that mean "not yours to open".</summary>
    private static readonly string[] DeniedTypeNames =
    {
        "RevitServerUnauthorizedException",
        "CentralModelMissingException",
        "CentralModelMissing",
        "LinkNotFoundException",
        "LinkNotFound",
    };

    /// <summary>
    /// Message fragments that mean the same thing. Matched case-insensitively against the whole
    /// exception chain's text.
    ///
    /// <para>Deliberately narrow. A false positive here suppresses the offline fallback that keeps
    /// a run working through a flaky connection, so anything that could plausibly describe a
    /// transient fault — a timeout, a 500, a reset — is not on this list.</para>
    /// </summary>
    private static readonly string[] DeniedFragments =
    {
        "401", "403", "unauthorized", "forbidden", "not authorized",
        "access denied", "permission", "no access",
    };

    /// <summary>True when <paramref name="ex"/> (or anything it wraps) means the account was refused.</summary>
    public static bool Is(Exception? ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            string typeName = e.GetType().Name;
            foreach (string denied in DeniedTypeNames)
                if (typeName.IndexOf(denied, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

            string message = e.Message ?? "";
            foreach (string fragment in DeniedFragments)
                if (message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
        }
        return false;
    }

    /// <summary>
    /// The account to name in a message about a denial — the signed-in Autodesk id, or a neutral
    /// phrase when Revit never told us who that is. Never a guess.
    /// </summary>
    public static string Account =>
        UI.HostServices.SignedInUserId.Length > 0
            ? UI.HostServices.SignedInUserId
            : "the signed-in Autodesk account";
}
