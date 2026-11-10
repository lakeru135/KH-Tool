using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Interop;
using KhBatchRunner.Presets;
using KhBatchRunner.UI;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// Multi Model Action panel: "Export NWC". Picks a model set and a saved options set, then writes
/// one <c>.nwc</c> per model through the same engine and modal runner every other batch button
/// uses. Manual + AlwaysAvailable so it runs with no document open.
///
/// <para>The options set is also what Batch Run and Schedule Run hand to a job, so a set created
/// here is the same object a scheduled export runs from — there is one place these options are
/// defined, not three.</para>
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ExportNwcCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;
        string year = uiApp.Application.VersionNumber;

        // Checked before the dialog, not after: there is no point configuring an export this PC
        // cannot perform, and the answer names what to install rather than failing at the first model.
        if (NavisworksLocator.Check(year) != NavisworksStatus.Installed)
        {
            TaskDialog.Show("Export NWC",
                "The Revit exporter for Navisworks was not found for Revit " + year + ".\n\n" +
                "It is installed with Navisworks itself (or its 'Revit exporter' add-in), not with " +
                "this add-in. Install it on this PC and try again.");
            return Result.Cancelled;
        }

        List<ModelPreset> presets = PresetStore.LoadAll();
        if (BatchCommandFlow.NoModelSets(presets, "Export NWC", "Export NWC"))
            return Result.Cancelled;

        using var dialog = new ExportNwcWindow();
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || dialog.RunSpec is null)
            return Result.Cancelled;

        ExportNwcInputSet inputs = dialog.RunSpec;
        ModelPreset? selected = presets.FirstOrDefault(p =>
            p.Name.Equals(dialog.SelectedModelSetName, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            TaskDialog.Show("Export NWC", "That model set could not be loaded.");
            return Result.Failed;
        }

        // A folder-backed set is re-scanned to its current members here, and the note reports any
        // model left out because this Revit cannot run it.
        ModelPreset chosen = PresetResolver.Resolve(selected, out string? setNote);
        var action = new ExportNwcAction(inputs, year);

        // Parallel auto-split, same shape as Cut model: every model is opened read-only and each
        // writes its own uniquely-named file, so concurrent instances cannot collide. The child
        // process rebuilds this action from the job's ExportNwcSpec — see ScheduledRunner.
        //
        // Always OFFERED (there is no disposition here that would forbid it — an export always
        // closes its models), and always confirmed by InteractiveSplit before anything launches.
        // Switched on in 1.3.8.5: a run that lost a model to a hung open took the other seven with
        // it, and a supervised split is the only thing that reruns them.
        Result? split = Schedule.InteractiveSplit.TryLaunch(
            uiApp,
            new Schedule.ScheduledJob
            {
                Name = inputs.ProjectLogName,
                RunItems = new List<Schedule.RunItem>
                    { new(ExportNwcAction.DisplayName, Schedule.RunItemKind.Action) },
                ExportNwcSpec = inputs,
                Completion = BatchCompletion.CloseDocuments,
                Sync = SyncMode.NoSync,   // nothing is written back to the source
                DestinationFolder = inputs.DestinationFolder,
                ProjectLogName = inputs.ProjectLogName
            },
            chosen.Models, splitEnabled: true, action.Name, BatchCompletion.CloseDocuments);
        if (split.HasValue) return split.Value;

        if (setNote != null) Diagnostics.RunTrace.Mark(setNote);

        // The modal message-loop runner, not BatchIdlingDriver — these are usually Forma cloud
        // models. Inside it the export takes the ordinary background path (NeedsActiveDocument is
        // false since 1.3.8.5), so each model is closed before the next one opens.
        return BatchCommandFlow.RunModal(uiApp, action, chosen, inputs.DestinationFolder,
            inputs.ProjectLogName, BatchCompletion.CloseDocuments, "Export NWC", "the export");
    }
}
