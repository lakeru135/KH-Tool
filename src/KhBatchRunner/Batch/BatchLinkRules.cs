using KhBatchRunner.Actions;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Batch;

/// <summary>
/// The identity of a link that a host ALREADY has, reduced to the two things that can be compared
/// against a source: the cloud model GUID, or the absolute local path. Exactly one is populated;
/// both are absent for a link whose reference could not be read, which is deliberately never a match
/// — an unreadable link must not silently suppress a source the user asked for.
/// </summary>
internal readonly record struct ExistingLink(Guid? ModelGuid, string? LocalPath);

/// <summary>
/// Every Batch Link RVT rule that can be decided WITHOUT Revit — which workset a link belongs on,
/// whether a source is already linked, whether a source is also a host, and whether a local path
/// will survive a run on another PC.
///
/// <para><b>Why they are here and <c>internal static</c>.</b> <c>tools/LivenessSim</c> runs with no
/// Revit installed and no <c>RevitAPI.dll</c> beside it, reaching in through
/// <c>&lt;InternalsVisibleTo Include="LivenessSim" /&gt;</c>. That is how
/// <c>BatchInteropAction.WorksetsToOpen</c> and <c>ReopenLadder.Refused</c> are asserted offline, and
/// the same reason applies with more force here: the workset-name rule is the ONE thing that has to
/// agree with <c>CorrectWorksetAction</c>, and a disagreement is invisible in a green run — it shows
/// up as the next Correct Workset run moving every link this tool just placed. A rule that lives
/// inside a <c>private</c> method of a Revit-typed action cannot be tested at all.</para>
/// </summary>
internal static class BatchLinkRules
{
    private const char Backslash = '\\';
    private const string UncPrefix = "\\\\";

    /// <summary>
    /// The workset a link to <paramref name="linkTypeName"/> belongs on.
    ///
    /// <para><paramref name="linkTypeName"/> must be <c>RevitLinkType.Name</c> as Revit assigned it
    /// AFTER the link was created — not the Forma display name or the file name it was created from.
    /// Revit is free to name the type something else (it disambiguates a name already in use), and
    /// <c>CorrectWorksetAction</c> reads the type's own name when it decides whether a link is on the
    /// right workset. Deriving this from anything else is how the two ends of the convention drift.</para>
    /// </summary>
    public static string WorksetNameFor(BatchLinkRvtInputSet inputs, string linkTypeName) =>
        inputs.PerLinkWorkset
            ? LinkWorksetNaming.Expected(inputs.LinkKeyword.Trim(), linkTypeName)
            : inputs.SharedWorksetName.Trim();

    /// <summary>
    /// True when <paramref name="source"/> is already among <paramref name="existing"/>.
    ///
    /// <para>Matching is by IDENTITY — cloud model GUID, or local path compared case-insensitively
    /// after normalising separators. Never by display name: two different models routinely share a
    /// name, so a name match would skip a source the user does want, and report nothing wrong.</para>
    /// </summary>
    public static bool AlreadyLinked(IEnumerable<ExistingLink> existing, CloudModel source)
    {
        foreach (ExistingLink link in existing)
        {
            if (source.IsLocal)
            {
                if (link.LocalPath != null && SamePath(link.LocalPath, source.LocalPath)) return true;
            }
            else if (link.ModelGuid.HasValue && source.ModelGuid != Guid.Empty &&
                     link.ModelGuid.Value == source.ModelGuid)
            {
                return true;
            }
        }
        return false;
    }

    private static bool SamePath(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Norm(a), Norm(b),
            StringComparison.OrdinalIgnoreCase);

    private static string Norm(string p) =>
        p.Replace('/', Backslash).TrimEnd(Backslash);

    /// <summary>
    /// The models that are BOTH a host in this run and a source being linked in, by cloud identity.
    ///
    /// <para>This is not a style problem, it is a hard engine limit: once host A is open and has just
    /// been given a link to model S, S is loaded as a LINK of A. If S is itself a host still to be
    /// processed, <c>BatchEngine.OpenModel</c> hands back a read-only linked document and fails that
    /// model. Batch Link RVT is the one action that CREATES this condition, so the command warns
    /// before the run rather than letting it surface as N failed models — and "Close files" is the
    /// disposition that avoids it, the same advice Internal Relink gives.</para>
    ///
    /// <para>Matched by cloud identity, never by name — the rule <c>RelinkSplitGuard</c> states for
    /// the same clash. A local host has no cloud identity and can never collide.</para>
    /// </summary>
    public static List<string> SourcesThatAreAlsoHosts(
        IReadOnlyList<CloudModel> hosts, IReadOnlyList<CloudModel> sources)
    {
        var sourceIds = new HashSet<(Guid Project, Guid Model)>();
        foreach (CloudModel s in sources)
            if (!s.IsLocal && s.ModelGuid != Guid.Empty)
                sourceIds.Add((s.ProjectGuid, s.ModelGuid));

        return hosts
            .Where(h => !h.IsLocal && h.ModelGuid != Guid.Empty &&
                        sourceIds.Contains((h.ProjectGuid, h.ModelGuid)))
            .Select(h => h.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True for a UNC path (<c>\\server\share\…</c>). A local source on a drive letter resolves
    /// against whatever that letter means on the PC running the batch — which, for a mapped drive or
    /// another user's account, is usually nothing. Warned about, not blocked: a run on this PC with
    /// this user's drives is perfectly valid, and the user is the one who knows which it is.
    /// </summary>
    public static bool IsUncPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Replace('/', Backslash).StartsWith(UncPrefix, StringComparison.Ordinal);

    /// <summary>Local sources whose path is not UNC — the ones the dialog warns about.</summary>
    public static List<string> NonUncLocalSources(IEnumerable<CloudModel> sources) =>
        sources.Where(s => !string.IsNullOrWhiteSpace(s.LocalPath) && !IsUncPath(s.LocalPath!))
               .Select(s => s.DisplayName)
               .ToList();
}
