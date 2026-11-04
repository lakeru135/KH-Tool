using KhBatchRunner.Presets;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Splits a flat, already-resolved+deduped model list into groups of at most <c>groupSize</c> for
/// the auto-split runner, <b>dealing</b> the models round-robin rather than cutting the list into
/// contiguous blocks.
///
/// <para>Contiguous blocks were the obvious implementation and the wrong one. A resolved model list
/// arrives ordered by set and discipline, so similar models sit next to each other — and similar
/// models take similar times. MEASURED 2026-07-26 on a 98-model run: every heavy <c>BLDC-SIGN</c>
/// landed in the same two groups, and round 1's groups ranged from <b>27.3 to 61.1
/// model-minutes</b>. A round cannot finish until its slowest group does, so five of eight
/// instances sat idle for the last twenty-five minutes of it.</para>
///
/// <para>Dealing like cards breaks that correlation: consecutive models — the ones most likely to
/// cost the same — go to different instances. It is not cost-aware (nothing here knows how long a
/// model takes), it just stops the ordering from concentrating the expensive ones.</para>
/// </summary>
internal static class ChunkSplitter
{
    /// <summary>
    /// Partitions <paramref name="models"/> into groups of at most <paramref name="groupSize"/>,
    /// dealt round-robin. A non-positive <paramref name="groupSize"/> (or a list that already fits
    /// in one group) returns a single group containing everything — callers decide whether that's
    /// worth splitting.
    ///
    /// The group COUNT is identical to the old contiguous split for every input, so everything that
    /// reasons about "how many instances will this be" (<see cref="ChunkLauncher.GroupCount"/>, the
    /// confirmation dialogs, <see cref="UI.SelectionSummary"/>) is unaffected — only which models
    /// land together changes.
    /// </summary>
    public static List<List<CloudModel>> Split(IReadOnlyList<CloudModel> models, int groupSize)
    {
        var groups = new List<List<CloudModel>>();
        if (models.Count == 0) return groups;
        if (groupSize <= 0)
        {
            groups.Add(models.ToList());
            return groups;
        }

        int groupCount = (models.Count + groupSize - 1) / groupSize;
        for (int g = 0; g < groupCount; g++) groups.Add(new List<CloudModel>());
        for (int i = 0; i < models.Count; i++) groups[i % groupCount].Add(models[i]);
        return groups;
    }
}
