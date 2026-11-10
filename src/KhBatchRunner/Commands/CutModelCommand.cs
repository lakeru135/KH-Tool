using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Forma;
using KhBatchRunner.Actions;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;
using KhBatchRunner.UI;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// Forma Batch Action panel: "Cut model". Picks a model set + target Forma folder + a site code, confirms
/// (this creates NEW cloud models), then batch-cuts each source via <see cref="CutModelAction"/>
/// (temp local copy → activate the matching site location → publish) using the SAME proven
/// engine/runner as the other batch buttons. Manual + AlwaysAvailable so it runs with no document
/// open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CutModelCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;

        List<ModelPreset> presets = PresetStore.LoadAll();
        if (BatchCommandFlow.NoModelSets(presets, "KH Batch Script Runner", "Cut model"))
            return Result.Cancelled;

        CutModelWindow? dialog = BatchCommandFlow.TryCreateDialog(
            () => new CutModelWindow(presets, new DataManagementClient(new SessionTokenProvider())),
            "KH Batch Script Runner", "Cut model");
        if (dialog == null) return Result.Failed;
        using var _dlg = dialog;
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return Result.Cancelled;

        // A folder-backed set is re-scanned to its current members here (note = offline fallback).
        ModelPreset chosen = PresetResolver.Resolve(dialog.SelectedPreset!, out string? setNote);
        CutModelInputSet inputs = dialog.Inputs!;

        PublishTarget? target = BatchCommandFlow.TryPublishTarget(() => inputs.ToPublishTarget());
        if (target == null) return Result.Failed;

        // Preview + confirm — this publishes NEW cloud models, so make the plan explicit.
        var confirm = new TaskDialog("Cut Model — Forma Batch Action")
        {
            MainInstruction = $"Cut {chosen.Models.Count} model(s) for site '{inputs.Prefix}'?",
            MainContent =
                $"Model set: {chosen.Name}\n" +
                $"Site code: {inputs.Prefix} (activates the matching site location and becomes the name prefix)\n" +
                $"Other site locations: {(inputs.DeleteOtherLocations ? "DELETED from the cut copy" : "kept")}\n" +
                $"Target folder: {inputs.FolderPath}\n\n" +
                (setNote != null ? setNote + "\n\n" : "") +
                "Each model is cut on a local temp copy (the source is never modified) and " +
                "published as a NEW cloud model in that folder. Continue?",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (confirm.Show() != TaskDialogResult.Yes)
            return Result.Cancelled;

        var action = new CutModelAction(target, inputs.DeleteOtherLocations);

        // Parallel auto-split, same shape as Batch Run / Multi Model Check. Safe for Cut model:
        // each source is read-only (a Check) and every cut copy is staged as
        // %TEMP%\KhBatchRunner\…\<name>_<guid>.rvt (see PublishStaging.SaveAsLocal), so concurrent
        // instances cannot collide on a staging file. The child process rebuilds this action from
        // the job's CutModelPublish set — see ScheduledRunner.
        Result? split = Schedule.InteractiveSplit.TryLaunch(
            uiApp,
            new Schedule.ScheduledJob
            {
                Name = inputs.ProjectLogName,
                RunItems = new List<Schedule.RunItem> { new(CutModelAction.DisplayName, Schedule.RunItemKind.Action) },
                CutModelPublish = inputs,
                Completion = BatchCompletion.CloseDocuments,
                Sync = SyncMode.NoSync,   // the source is never modified
                DestinationFolder = inputs.DestinationFolder,
                ProjectLogName = inputs.ProjectLogName
            },
            chosen.Models, dialog.SplitEnabled, action.Name, dialog.SelectedCompletion);
        if (split.HasValue) return split.Value;

        // Modal message-loop runner, NOT BatchIdlingDriver — Cut model opens each SOURCE model
        // (often a Forma cloud model). See BatchCommandFlow.RunModal.
        return BatchCommandFlow.RunModal(uiApp, action, chosen, inputs.DestinationFolder,
            inputs.ProjectLogName, dialog.SelectedCompletion, "KH Batch Script Runner", "Cut model");
    }
}
