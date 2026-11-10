using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;

namespace KhBatchRunner.UI;

/// <summary>
/// The one-line "what am I about to run" summary under the model-set list.
///
/// WHY IT EXISTS. The dialogs used to report only how many SETS were ticked, which is the number a
/// user cares least about: two sets can be 3 models or 300, and the parallel group size turns that
/// into anything from one Revit to thirty. Both facts were only discoverable by starting the run.
/// This states the model count and the resulting instance count, and updates as the ticks and the
/// group size change.
///
/// DELIBERATELY LOCAL. The count comes from each set's saved model list, NOT from
/// <see cref="ModelPresetCombiner.Combine"/> — that re-scans every folder-backed set over the
/// network, which is fine once at run time and unusable on every checkbox click. De-duplication
/// uses <see cref="ModelKey"/>, the same key the run and the recovery pass use, so the number
/// shown is the number that will run for a set with a fixed model list.
///
/// A folder-backed set is the honest exception: its members are re-scanned at run time, so the
/// snapshot here can be out of date. That is called out rather than hidden.
/// </summary>
public static class SelectionSummary
{
    /// <summary>
    /// Builds the summary line. <paramref name="splitEnabled"/> is whether the chosen disposition
    /// permits a parallel run at all; the group size itself is no longer a user setting — it is
    /// derived here from the model count and the machine, exactly as the launcher will derive it
    /// (<see cref="InstanceBudget.AutoGroupSize"/>), so the preview cannot disagree with the run.
    /// </summary>
    public static string Describe(IReadOnlyList<ModelPreset> checkedSets,
                                  IReadOnlyList<CloudModel> adHocModels,
                                  int actionCount,
                                  bool splitEnabled)
    {
        var seen = new Dictionary<string, CloudModel>();
        foreach (CloudModel m in checkedSets.SelectMany(s => s.Models).Concat(adHocModels))
            seen[ModelKey.For(m)] = m;

        int models = seen.Count;
        List<CloudModel> resolved = seen.Values.ToList();
        int sets = checkedSets.Count;
        bool folderBacked = checkedSets.Any(s => s.IsFolderBacked);

        string text = $"Selected: {sets} model set(s) · {models} model(s), " +
                      $"{actionCount} action(s)/script set(s).";

        if (models > 0)
        {
            // Same arithmetic the launcher uses, so the preview cannot disagree with the split.
            int groupSize = splitEnabled ? InstanceBudget.AutoGroupSize(resolved) : 0;
            int groups = ChunkLauncher.GroupCount(models, groupSize);
            if (groupSize > 0 && models > groupSize)
            {
                // "group(s)", not "Revit instance(s)": more groups than the cap means the extras
                // queue for a free slot rather than running, and calling them instances up front is
                // what sets up the "it promised seven Revits and I can see three" surprise.
                int cap = InstanceBudget.MaxConcurrent(resolved);
                text += $"  →  {groups} group(s) of up to {groupSize} model(s)" +
                        (groups > cap ? $", {cap} Revit running at a time." : ", all at once.");
            }
            else
            {
                text += "  →  1 Revit instance (no split).";
            }
        }

        if (folderBacked)
            text += "  Folder-backed set(s) re-scan at run time, so the model count may change.";

        return text;
    }
}
