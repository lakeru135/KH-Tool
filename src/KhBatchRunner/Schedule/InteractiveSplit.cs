using Autodesk.Revit.UI;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Schedule;

/// <summary>
/// The "should this ribbon command run as parallel groups, and if so do it" step, factored out of
/// the commands that each had their own copy of it (<see cref="Commands.BatchActionCommandBase"/>,
/// <c>BatchRunCommand</c>, <c>BatchInteropCommand</c>). Extracted when Cut model, Internal Relink,
/// Correct Workset and Initiate to Forma needed the same behaviour: six near-identical copies of a
/// confirm-launch-supervise sequence is where the rules quietly drift apart.
///
/// The engine underneath is already general — <see cref="ChunkLauncher"/> serialises the whole
/// <see cref="ScheduledJob"/> (including its <c>CutModelPublish</c> / <c>RelinkSpec</c> /
/// <c>CorrectWorksetSpec</c> / <c>InitiatePublish</c> input set) and <c>ScheduledRunner</c>
/// rebuilds the action from it in the child process. Nothing here is action-specific.
/// </summary>
internal static class InteractiveSplit
{
    /// <summary>
    /// Splits the run across fresh Revit instances when that is both wanted and worthwhile, and
    /// returns the command's <see cref="Result"/>. Returns <c>null</c> when nothing was split, which
    /// means "carry on with the ordinary single-process run" — the caller's existing code path.
    /// </summary>
    /// <param name="baseJob">A fully-formed job for the run: run items, completion, sync, folders,
    /// and whatever input set the action needs. <see cref="ScheduledJob.GroupSize"/> is set here.</param>
    /// <param name="splitEnabled">The dialog's choice — false forces the single-process path.</param>
    /// <param name="blockedReason">When non-null, splitting is refused for a correctness reason
    /// (e.g. Internal Relink whose set contains one of its own link targets) and the user is told
    /// once, before the run continues single-process. Not an error.</param>
    /// <param name="supervisorCompletion">What the SUPERVISING Revit does once every group is
    /// accounted for — the dialog's disposition, not the groups' (each group always closes).</param>
    /// <param name="alwaysSupervise">Run even a set too small to split in ONE supervised child
    /// Revit, so a native crash there can never close this Revit (Batch Interop — see
    /// <see cref="SupervisedGroupSize"/>).</param>
    public static Result? TryLaunch(
        UIApplication uiApp,
        ScheduledJob baseJob,
        IReadOnlyList<CloudModel> models,
        bool splitEnabled,
        string runLabel,
        BatchCompletion supervisorCompletion,
        string? blockedReason = null,
        bool alwaysSupervise = false)
    {
        if (!splitEnabled) return null;

        if (blockedReason != null)
        {
            TaskDialog.Show("KH Batch Script Runner",
                "Parallel groups skipped — " + blockedReason +
                "\n\nRunning all models in this one Revit instead.");
            return null;
        }

        // Derived here, not in the dialog: this is the first point where the model count is real
        // (a folder-backed set re-scans at resolve time). See InstanceBudget.AutoGroupSize.
        RunWeight weight = InstanceBudget.WeightFor(baseJob);
        int autoSize = InstanceBudget.AutoGroupSize(models, weight);
        int groupSize = SupervisedGroupSize(models.Count, autoSize, alwaysSupervise);
        if (groupSize <= 0) return null;

        bool realSplit = models.Count > groupSize;
        if (realSplit)
        {
            int groups = ChunkLauncher.GroupCount(models.Count, groupSize);
            var confirm = new TaskDialog("Run — parallel groups")
            {
                MainInstruction = $"Run {runLabel} on {models.Count} models as {groups} group(s) of up to {groupSize}?",
                MainContent =
                    "Each group opens in its own Revit instance and closes when it finishes. " +
                    "Concurrency: " + InstanceBudget.Describe(models, weight) + ".\n\nContinue?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (confirm.Show() != TaskDialogResult.Yes)
                return Result.Cancelled;
        }

        baseJob.GroupSize = groupSize;
        RunManifest? manifest = ChunkLauncher.LaunchSupervised(
            baseJob, models, groupSize, uiApp.Application.VersionNumber, out string? chunkErr);
        if (manifest == null)
        {
            TaskDialog.Show("KH Batch Script Runner", chunkErr ?? "Could not launch any group instances.");
            return Result.Failed;
        }

        // Supervise: a group whose Revit dies would otherwise take its models with it, silently.
        RunSupervisor.StartInteractive(manifest, supervisorCompletion);
        TaskDialog.Show("KH Batch Script Runner",
            (realSplit
                ? ChunkLauncher.DescribeLaunch(manifest, groupSize)
                : $"{runLabel} runs on {models.Count} model(s) in a separate Revit, so a crash in it " +
                  "can't close this one. A model that crashes Revit is retried once, then skipped.") +
            $"\n\nResults are written to one shared log under:\n{baseJob.DestinationFolder}\\Log" +
            (chunkErr != null ? $"\n\n{chunkErr}" : ""));
        return Result.Succeeded;
    }

    /// <summary>
    /// The group size to launch with, or 0 for "run in this Revit". Revit-free so the harness pins it.
    ///
    /// <para>Ordinary rule: split only when the set is bigger than one group. With
    /// <paramref name="alwaysSupervise"/>, a set that fits in one group still goes to ONE child Revit
    /// rather than this one. MEASURED 2026-11-03: a 7-model Batch Interop run on a 32 GB PC (Interop
    /// cap 1, so the group size is the whole set) ran in the user's own Revit, and a native crash on a
    /// non-workshared cloud model's open closed that Revit and lost the batch — no supervisor existed to
    /// notice, retry, or carry on.</para>
    /// </summary>
    internal static int SupervisedGroupSize(int modelCount, int autoGroupSize, bool alwaysSupervise)
    {
        if (modelCount <= 0 || autoGroupSize <= 0) return 0;
        if (modelCount > autoGroupSize) return autoGroupSize;
        return alwaysSupervise ? modelCount : 0;
    }
}
