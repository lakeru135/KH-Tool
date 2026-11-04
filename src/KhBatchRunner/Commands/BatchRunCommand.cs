using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

// WinForms and Revit both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// "Batch Run" (DYN multi player) ribbon button: mix model sets + ad-hoc models with one or
/// more actions/script sets, then either run immediately or hand the selection off to
/// Schedule Run to save as a recurring job (via <see cref="ScheduleRunWindow.PreloadDraft"/>).
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class BatchRunCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;

        var client = new Forma.DataManagementClient(new Auth.SessionTokenProvider());
        using var window = new BatchRunWindow(client);
        if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return Result.Cancelled;

        if (window.Outcome == BatchRunWindow.BatchRunOutcome.SaveAsSchedule)
        {
            var draft = new ScheduledJob
            {
                ModelSetNames = window.SelectedModelSetNames.ToList(),
                AdHocModels = window.SelectedAdHocModels.ToList(),
                // Carry the checklist order through to the schedule (list order is run order).
                RunItems = window.SelectedRunItems
                    .Select(t => new RunItem(t.Name, t.IsScriptSet ? RunItemKind.ScriptSet : RunItemKind.Action))
                    .ToList(),
                InitiatePublish = window.SelectedInitiateSpec,
                CutModelPublish = window.SelectedCutModelSpec,
                ExportNwcSpec = window.SelectedExportNwcSpec,
                Completion = window.SelectedCompletion,
                Sync = window.SelectedSyncMode,
                DestinationFolder = window.DestinationFolder,
                ProjectLogName = window.ProjectLogName,
                GroupSize = window.SplitEnabled ? ScheduledJob.GroupSizeAuto : 0
            };

            using var scheduleWindow = new ScheduleRunWindow(client);
            scheduleWindow.PreloadDraft(draft);
            if (scheduleWindow.ShowDialog() != System.Windows.Forms.DialogResult.OK || scheduleWindow.Job is null)
                return Result.Cancelled;

            ScheduledJob job = scheduleWindow.Job;
            if (scheduleWindow.Outcome == ScheduleRunWindow.ScheduleOutcome.RunNow)
            {
                // Same Idling-driven queue as ScheduleRunCommand's "Run now" — see
                // ScheduledRunner.TryRunInteractive's doc comment for why this replaced the
                // old synchronous, journal-wedging call here.
                try
                {
                    if (!ScheduledRunner.TryRunInteractive(uiApp, job, $"Batch Run — {job.Name}", out string? error))
                    {
                        TaskDialog.Show("Batch Run — Run now",
                            error ?? "A batch is already running. Wait for it to finish before starting another.");
                        return Result.Cancelled;
                    }
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Batch Run — Run now", $"Could not start run: {ex.Message}");
                    return Result.Failed;
                }
                return Result.Succeeded;
            }

            // "Save schedule" now saves inside the schedule window (which stays open); the
            // dialog only returns OK for Run now, handled above.
            return Result.Succeeded;
        }

        // Run now: run every checked action AND script set over the combined model selection,
        // in checklist order (list order is run order).
        ModelPreset combined = ModelPresetCombiner.Combine(
            "Batch Run", window.SelectedModelSetNames, window.SelectedAdHocModels);

        // Parallel auto-split: split the run into groups and launch one fresh Revit instance per
        // group (each runs headless via the KHBATCH_JOB path and closes when done), up to the
        // instance cap, with the rest queued. This automates the manual "several same-time jobs"
        // pattern and avoids the mid-batch failures a single long session hits. Small runs
        // (count <= group size) fall through to the normal single-process path.
        //
        // The size is derived HERE rather than in the dialog because this is the first point where
        // the model count is actually known — folder-backed sets are re-scanned by Combine above.
        int groupSize = window.SplitEnabled
            ? Schedule.InstanceBudget.AutoGroupSize(combined.Models)
            : 0;

        // A chunk child rebuilds every action from the job alone (ScheduledRunner.BuildJobPasses),
        // so an action that carries its own configuration can only be split if the base job below
        // carries that configuration too. Initiate and Cut model do. Internal Relink, Correct
        // Workset and Batch Interop keep theirs in RelinkSpec / CorrectWorksetSpec /
        // InteropChecksetPath, which this window has no field to fill — a child would throw
        // "carries no ... snapshot" and the whole group would do nothing, silently. None of them
        // is selectable here today, so this is a guard against that list growing, not a live bug.
        var unsplittable = window.SelectedRunItems
            .Where(t => !t.IsScriptSet &&
                        (t.Name == RelinkLinksAction.DisplayName ||
                         t.Name == CorrectWorksetAction.DisplayName ||
                         t.Name == BatchInteropAction.DisplayName))
            .Select(t => t.Name)
            .ToList();
        if (groupSize > 0 && unsplittable.Count > 0)
        {
            TaskDialog.Show("Batch Run — parallel groups",
                "Parallel groups are not available for: " + string.Join(", ", unsplittable) +
                ".\n\nThose actions carry their own settings, which a separate Revit instance " +
                "cannot be handed from here. Running everything in this instance instead.");
            groupSize = 0;
        }

        if (groupSize > 0 && combined.Models.Count > groupSize)
        {
            int groups = ChunkLauncher.GroupCount(combined.Models.Count, groupSize);
            bool hasPublish = window.SelectedRunItems.Any(t =>
                t.Name == InitiateToFormaAction.DisplayName || t.Name == CutModelAction.DisplayName);
            var confirm = new TaskDialog("Batch Run — parallel groups")
            {
                MainInstruction = $"Run {combined.Models.Count} models as {groups} group(s) of up to {groupSize}?",
                MainContent =
                    "Each group opens in its own Revit instance and closes when it finishes. " +
                    "Concurrency: " + Schedule.InstanceBudget.Describe(combined.Models) + "." +
                    (hasPublish ? "\n\nNOTE: this run publishes NEW cloud models (Initiate / Cut model)." : "") +
                    "\n\nContinue?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (confirm.Show() != TaskDialogResult.Yes)
                return Result.Cancelled;

            var baseJob = new ScheduledJob
            {
                Name = "Batch Run",
                RunItems = window.SelectedRunItems
                    .Select(t => new RunItem(t.Name, t.IsScriptSet ? RunItemKind.ScriptSet : RunItemKind.Action))
                    .ToList(),
                InitiatePublish = window.SelectedInitiateSpec,
                CutModelPublish = window.SelectedCutModelSpec,
                ExportNwcSpec = window.SelectedExportNwcSpec,
                Completion = BatchCompletion.CloseDocuments, // gated: parallel excludes "leave files open"
                Sync = window.SelectedSyncMode,
                DestinationFolder = window.DestinationFolder,
                ProjectLogName = window.ProjectLogName
            };

            RunManifest? manifest = ChunkLauncher.LaunchSupervised(
                baseJob, combined.Models, groupSize, uiApp.Application.VersionNumber, out string? chunkErr);
            if (manifest == null)
            {
                TaskDialog.Show("Batch Run", chunkErr ?? "Could not launch any group instances.");
                return Result.Failed;
            }
            RunSupervisor.StartInteractive(manifest, window.SelectedCompletion);
            string logRoot = string.IsNullOrWhiteSpace(window.DestinationFolder)
                ? "(default folder)" : window.DestinationFolder + "\\Log";
            TaskDialog.Show("Batch Run",
                ChunkLauncher.DescribeLaunch(manifest, groupSize) +
                $"\n\nResults are written to one shared log under:\n{logRoot}" +
                (chunkErr != null ? $"\n\n{chunkErr}" : ""));
            return Result.Succeeded;
        }

        // One BatchEngine per checked item (each names its own log files), run as a queue of
        // passes in a modal message-loop runner instead of the old Idling
        // driver — opening Forma cloud models from an Idling event hung/crashed. See UI/BatchRunnerWindow.
        // A script set becomes a RunScriptSetAction pass (the Dynamo runner); the modal runner
        // opens its models activated, which Dynamo requires.
        var scriptSets = Scripts.ScriptSetStore.LoadAll()
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var builtActions = new List<IBatchAction>();
        var missingSets = new List<string>();
        var publishTargets = new List<string>();
        foreach ((string name, bool isScriptSet) in window.SelectedRunItems)
        {
            IBatchAction action;
            if (isScriptSet)
            {
                if (!scriptSets.TryGetValue(name, out Scripts.ScriptSet? set))
                {
                    missingSets.Add(name);
                    continue;
                }
                action = new RunScriptSetAction(set);
            }
            else if (name == InitiateToFormaAction.DisplayName)
            {
                // Initiate publishes NEW cloud models — build from the window's snapshot.
                try
                {
                    action = new InitiateToFormaAction(window.SelectedInitiateSpec!.ToPublishTarget());
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Batch Run", $"Invalid Initiate publish target: {ex.Message}");
                    return Result.Failed;
                }
                publishTargets.Add($"Initiate to Forma → {window.SelectedInitiateSpec!.FolderPath}");
            }
            else if (name == CutModelAction.DisplayName)
            {
                // Cut model also publishes NEW cloud models — build from the window's snapshot.
                try
                {
                    action = new CutModelAction(
                        window.SelectedCutModelSpec!.ToPublishTarget(),
                        window.SelectedCutModelSpec.DeleteOtherLocations);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Batch Run", $"Invalid Cut model publish target: {ex.Message}");
                    return Result.Failed;
                }
                publishTargets.Add($"Cut model ({window.SelectedCutModelSpec!.Prefix}) → {window.SelectedCutModelSpec.FolderPath}");
            }
            else if (name == ExportNwcAction.DisplayName)
            {
                if (window.SelectedExportNwcSpec is null)
                {
                    TaskDialog.Show("Batch Run", "Export NWC needs an options set to be selected.");
                    return Result.Failed;
                }
                action = new ExportNwcAction(window.SelectedExportNwcSpec,
                                             uiApp.Application.VersionNumber);
            }
            else
            {
                action = ActionRegistry.Create(name);
            }
            builtActions.Add(action);
        }

        // Now that the full task list is known, build one engine per task so each can stamp its
        // "Task" column as "k of N" (see BatchEngine). Every task's detail logs land under
        // <dest>\<task>\; all passes share ONE run timestamp so their tracking rows go to a single
        // <dest>\Log\<stamp>_<project>_Batch Run.csv file.
        DateTime runStamp = DateTime.Now;
        var passes = new List<BatchRunnerWindow.Pass>();
        for (int i = 0; i < builtActions.Count; i++)
        {
            var engine = new BatchEngine(uiApp, builtActions[i], window.DestinationFolder,
                window.ProjectLogName, window.SelectedSyncMode,
                logNameRun: "Batch Run", taskIndex: i + 1, taskCount: builtActions.Count, runStamp: runStamp);
            passes.Add(new BatchRunnerWindow.Pass(engine, combined));
        }

        if (missingSets.Count > 0)
            TaskDialog.Show("Batch Run",
                "Script set(s) not found (deleted since the window opened?): " +
                string.Join(", ", missingSets) +
                (passes.Count > 0 ? "\n\nThe remaining item(s) will run now." : ""));
        if (passes.Count == 0)
            return Result.Cancelled;

        // Initiate / Cut model create NEW cloud models — confirm the destination(s) before an
        // interactive run (this is where the standalone commands' confirm gate lives for the batch
        // path; a headless scheduled run never prompts — it was confirmed when the schedule was saved).
        if (publishTargets.Count > 0)
        {
            var confirm = new TaskDialog("Batch Run — publish to Forma")
            {
                MainInstruction = $"Publish {combined.Models.Count} model(s) to Forma?",
                MainContent =
                    string.Join("\n", publishTargets) + "\n\n" +
                    "Each model in the run is published as a NEW cloud model. Continue?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (confirm.Show() != TaskDialogResult.Yes)
                return Result.Cancelled;
        }

        try
        {
            BatchRunnerWindow.RunModal(uiApp, passes, "Batch Run", window.SelectedCompletion);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Batch Run", $"Could not run batch: {ex.Message}");
            return Result.Failed;
        }

        return Result.Succeeded;
    }
}
