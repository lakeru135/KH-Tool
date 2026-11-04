using KhBatchRunner.Actions;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Schedule;

/// <summary>
/// The one reason Internal Relink must not be split across Revit instances. Deliberately a plain
/// static class over plain data (<see cref="CloudModel"/>, <see cref="RelinkTarget"/>) rather than
/// a method on <c>RelinkModelCommand</c>: the command implements <c>IExternalCommand</c>, so
/// touching it drags in RevitAPIUI and the rule could not be covered by <c>tools/LivenessSim</c>,
/// which runs with no Revit installed.
/// </summary>
internal static class RelinkSplitGuard
{
    /// <summary>
    /// Splitting Internal Relink is normally safe — each host is edited and synced by exactly one
    /// group, and a model that is merely linked opens read-only in the others. It stops being safe
    /// when a model is on BOTH sides: one instance would hold it open ATTACHED for write while
    /// another loads the same model as a link, resolving that link against a central mid-edit.
    ///
    /// Returns the reason to show the user, or null when splitting is safe. Matching is by cloud
    /// identity, never by name — two different models can share a display name, and a name-based
    /// check would both miss real clashes and invent false ones.
    /// </summary>
    public static string? SelfLinkingHost(
        IReadOnlyList<CloudModel> hosts, Dictionary<string, List<RelinkTarget>> targets)
    {
        var targetIds = new HashSet<(Guid Project, Guid Model)>();
        foreach (List<RelinkTarget> candidates in targets.Values)
            foreach (RelinkTarget t in candidates)
                targetIds.Add((t.ProjectGuid, t.ModelGuid));

        // A local host has no cloud identity, so it can never be one of the scanned cloud targets.
        List<string> clashes = hosts
            .Where(h => !h.IsLocal && targetIds.Contains((h.ProjectGuid, h.ModelGuid)))
            .Select(h => h.DisplayName)
            .ToList();
        if (clashes.Count == 0) return null;

        return $"{clashes.Count} host model(s) are also relink targets " +
               $"({string.Join(", ", clashes.Take(3))}{(clashes.Count > 3 ? ", …" : "")}), so a " +
               "parallel run could edit one of them while another instance loads it as a link.";
    }
}
