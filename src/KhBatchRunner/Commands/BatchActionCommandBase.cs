using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// Shared "Multi Model Check" command: pick a model set, log location and completion, then
/// batch-run the concrete <see cref="IBatchAction"/> over the set via
/// <see cref="BatchRunnerWindow"/> (a modal message-loop runner that
/// replaced the Idling driver so Forma cloud opens don't hang/crash). Subclasses only supply the
/// action. Manual transaction + AlwaysAvailable so it runs with no model open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public abstract class BatchActionCommandBase : IExternalCommand
{
    /// <summary>The action this button runs. Created fresh per invocation.</summary>
    protected abstract IBatchAction CreateAction();

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;
        IBatchAction action = CreateAction();

        // A runnable active document (a project model, not a family) lets the user run this action
        // on what's open right now instead of a model set — offered as a checkbox in the dialog.
        Document? activeDoc = uiApp.ActiveUIDocument?.Document;
        string? activeName = (activeDoc != null && !activeDoc.IsFamilyDocument) ? activeDoc.Title : null;

        List<ModelPreset> presets = PresetStore.LoadAll();
        if (presets.Count == 0 && activeName == null)
        {
            TaskDialog.Show("KH Batch Script Runner",
                "No model sets found, and no model is open.\n\n" +
                "Create a model set with Setting → Edit Model Set, or open a model to run this " +
                "action on it directly.");
            return Result.Cancelled;
        }

        using var dialog = new RunActionWindow(presets, action.Name, action.Category, activeName);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return Result.Cancelled;

        // Run on the model open in Revit — no engine, no logs, results shown on screen.
        if (dialog.RunOnActiveModel)
            return Batch.ActiveModelRunner.Run(uiApp, action) ? Result.Succeeded : Result.Cancelled;

        // One or more model sets — combine into a single de-duplicated run (same combiner Batch Run uses).
        ModelPreset chosen = ModelPresetCombiner.Combine(
            action.Name, dialog.SelectedPresets.Select(p => p.Name), Array.Empty<CloudModel>());
        string destFolder = dialog.DestinationFolder!;
        string projectLog = dialog.ProjectLogName!;

        // Parallel auto-split: when a group size is set and the model count exceeds it, run the
        // action as several groups, each in its own fresh Revit instance launched at the same time
        // (headless via the KHBATCH_JOB path, closing when done). Only for actions the registry can
        // recreate by name in the child process — every Multi Model Check/Action button qualifies.
        // Derived here, not in the dialog: this is the first point where the model count is real
        // (folder-backed sets re-scan at resolve time). See InstanceBudget.AutoGroupSize.
        int groupSize = dialog.SplitEnabled
            ? Schedule.InstanceBudget.AutoGroupSize(chosen.Models)
            : 0;
        bool canChunk = ActionRegistry.All().Any(a => a.Name == action.Name);
        if (groupSize > 0 && canChunk && chosen.Models.Count > groupSize)
        {
            int groups = ChunkLauncher.GroupCount(chosen.Models.Count, groupSize);
            var confirm = new TaskDialog("Run — parallel groups")
            {
                MainInstruction = $"Run {action.Name} on {chosen.Models.Count} models as {groups} group(s) of up to {groupSize}?",
                MainContent =
                    "Each group opens in its own Revit instance and closes when it finishes. " +
                    "Concurrency: " + Schedule.InstanceBudget.Describe(chosen.Models) + ".\n\nContinue?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (confirm.Show() != TaskDialogResult.Yes)
                return Result.Cancelled;

            var baseJob = new ScheduledJob
            {
                Name = projectLog,
                RunItems = new List<RunItem> { new(action.Name, RunItemKind.Action) },
                Completion = BatchCompletion.CloseDocuments,
                Sync = dialog.SelectedSyncMode,
                DestinationFolder = destFolder,
                ProjectLogName = projectLog
            };
            RunManifest? manifest = ChunkLauncher.LaunchSupervised(
                baseJob, chosen.Models, groupSize, uiApp.Application.VersionNumber, out string? chunkErr);
            if (manifest == null)
            {
                TaskDialog.Show("KH Batch Script Runner", chunkErr ?? "Could not launch any group instances.");
                return Result.Failed;
            }
            // Supervise: a group whose Revit dies would otherwise take its models with it, silently.
            RunSupervisor.StartInteractive(manifest, dialog.SelectedCompletion);
            TaskDialog.Show("KH Batch Script Runner",
                Schedule.ChunkLauncher.DescribeLaunch(manifest, groupSize) +
                $"\n\nResults are written to one shared log under:\n{destFolder}\\Log" +
                (chunkErr != null ? $"\n\n{chunkErr}" : ""));
            return Result.Succeeded;
        }

        var engine = new BatchEngine(uiApp, action, destFolder, projectLog, dialog.SelectedSyncMode);

        try
        {
            // Run the batch inside a modal dialog's message loop rather than
            // from a UIApplication.Idling event. Opening a Forma cloud model from an Idling event
            // made Revit's own CloudWorksharing.UI fail ("Can not subscribe to an event during
            // execution of that event") and then hang on the "Open Model" dialog or crash
            // (0xc0000005); from a modal message loop the open behaves like a normal user open.
            // RunModal shows the completion summary itself (Close files / Leave files open).
            BatchRunnerWindow.RunModal(uiApp, engine, chosen, $"Run: {action.Name}", dialog.SelectedCompletion);
        }
        catch (Exception ex)
        {
            // e.g. the destination folder (a free-text box) is an unreachable share, or a model
            // open threw before the per-model catch — surface it instead of failing silently.
            TaskDialog.Show("KH Batch Script Runner", $"Could not run batch: {ex.Message}");
            return Result.Failed;
        }

        return Result.Succeeded;
    }
}
