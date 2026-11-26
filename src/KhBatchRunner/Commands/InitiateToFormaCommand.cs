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
/// Forma Batch Action panel: "Initiate to Forma". Picks a model set + target Forma folder, confirms
/// (this creates NEW cloud models), then batch-publishes each source via
/// <see cref="InitiateToFormaAction"/> using the SAME proven engine/driver as the other batch
/// buttons (background open, one model per Idling tick, dialog suppression, completion handling).
/// Manual + AlwaysAvailable so it runs with no document open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class InitiateToFormaCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;

        List<ModelPreset> presets = PresetStore.LoadAll();
        if (BatchCommandFlow.NoModelSets(presets, Product.Name, "Initiate to Forma"))
            return Result.Cancelled;

        InitiateWindow? dialog = BatchCommandFlow.TryCreateDialog(
            () => new InitiateWindow(presets, new DataManagementClient(new SessionTokenProvider())),
            Product.Name, "Initiate");
        if (dialog == null) return Result.Failed;
        using var _dlg = dialog;
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return Result.Cancelled;

        // A folder-backed set is re-scanned to its current members here (note = offline fallback).
        ModelPreset chosen = PresetResolver.Resolve(dialog.SelectedPreset!, out string? setNote);
        InitiateInputSet inputs = dialog.Inputs!;

        PublishTarget? target = BatchCommandFlow.TryPublishTarget(() => inputs.ToPublishTarget());
        if (target == null) return Result.Failed;

        // Preview + confirm — this publishes NEW cloud models, so make the destination explicit.
        // Asked in the run window (Confirm state), not a TaskDialog.
        var confirm = new RunConfirmation(
            $"Publish {chosen.Models.Count} model(s) to Forma?",
            $"Model set: {chosen.Name}\n" +
            $"Target folder: {inputs.FolderPath}\n\n" +
            (setNote != null ? setNote + "\n\n" : "") +
            "Each model is published as a NEW cloud model in that folder.",
            "Publish");

        var action = new InitiateToFormaAction(target);

        // Parallel auto-split, same shape as Batch Run / Multi Model Check. Safe for Initiate: each
        // source is opened read-only and staged as %TEMP%\KhBatchRunner\…\<name>_<guid>.rvt (see
        // PublishStaging.SaveAsLocal), so concurrent instances cannot collide on a staging file, and
        // each model is published by exactly one group. The child process rebuilds this action from
        // the job's InitiatePublish set — see ScheduledRunner.
        Result? split = Schedule.InteractiveSplit.TryLaunch(
            uiApp,
            new Schedule.ScheduledJob
            {
                Name = inputs.ProjectLogName,
                RunItems = new List<Schedule.RunItem> { new(InitiateToFormaAction.DisplayName, Schedule.RunItemKind.Action) },
                InitiatePublish = inputs,
                Completion = BatchCompletion.CloseDocuments,
                Sync = SyncMode.NoSync,   // the source is never modified
                DestinationFolder = inputs.DestinationFolder,
                ProjectLogName = inputs.ProjectLogName
            },
            chosen.Models, dialog.SplitEnabled, action.Name, dialog.SelectedCompletion,
            actionConfirm: confirm);
        if (split.HasValue) return split.Value;

        // Modal message-loop runner, NOT BatchIdlingDriver. Initiate opens each SOURCE model (often
        // a Forma cloud model) before publishing it; opening a cloud model from an Idling event is
        // what left the "Open Model" dialog stuck here — the same bug the Multi Model Check/Action
        // buttons had. See BatchCommandFlow.RunModal.
        return BatchCommandFlow.RunModal(uiApp, action, chosen, inputs.DestinationFolder,
            inputs.ProjectLogName, dialog.SelectedCompletion, Product.Name, "Initiate",
            confirm);
    }
}
