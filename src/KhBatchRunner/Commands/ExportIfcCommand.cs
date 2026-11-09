using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;
using KhBatchRunner.UI;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// NWD Helper panel: "Export IFC + Data". Picks a model set and a saved options set, then exports
/// one IFC plus the report's CSVs per model through the same engine and modal runner Export NWC
/// uses. Manual + AlwaysAvailable so it runs with no document open. Unlike Export NWC there is no
/// exporter to check for: Revit's IFC exporter ships with Revit.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ExportIfcCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;

        List<ModelPreset> presets = PresetStore.LoadAll();
        if (presets.Count == 0)
        {
            TaskDialog.Show("Export IFC + Data",
                "No model sets found.\n\n" +
                "Create one first with Setting → Edit Model Set, then run Export IFC + Data.");
            return Result.Cancelled;
        }

        using var dialog = new ExportIfcWindow();
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || dialog.RunSpec is null)
            return Result.Cancelled;

        ExportIfcInputSet inputs = dialog.RunSpec;
        ModelPreset? selected = presets.FirstOrDefault(p =>
            p.Name.Equals(dialog.SelectedModelSetName, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            TaskDialog.Show("Export IFC + Data", "That model set could not be loaded.");
            return Result.Failed;
        }

        ModelPreset chosen = PresetResolver.Resolve(selected, out string? setNote);
        var action = new ExportIfcDataAction(inputs);

        // Parallel auto-split, same shape as Export NWC: every model is opened read-only and lands in
        // its own folder; the child processes rebuild this action from the job's ExportIfcSpec.
        Result? split = Schedule.InteractiveSplit.TryLaunch(
            uiApp,
            new Schedule.ScheduledJob
            {
                Name = inputs.ProjectLogName,
                RunItems = new List<Schedule.RunItem>
                    { new(ExportIfcDataAction.DisplayName, Schedule.RunItemKind.Action) },
                ExportIfcSpec = inputs,
                Completion = BatchCompletion.CloseDocuments,
                Sync = SyncMode.NoSync,   // nothing is written back to the source
                DestinationFolder = inputs.DestinationFolder,
                ProjectLogName = inputs.ProjectLogName
            },
            chosen.Models, splitEnabled: true, action.Name, BatchCompletion.CloseDocuments);
        if (split.HasValue) return split.Value;

        if (setNote != null) Diagnostics.RunTrace.Mark(setNote);

        var engine = new BatchEngine(uiApp, action, inputs.DestinationFolder, inputs.ProjectLogName);

        try
        {
            // The modal message-loop runner, as Export NWC: cloud opens hang from an Idling event.
            BatchRunnerWindow.RunModal(uiApp, engine, chosen, action.Name, BatchCompletion.CloseDocuments);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Export IFC + Data", $"Could not start the export: {ex.Message}");
            return Result.Failed;
        }

        return Result.Succeeded;
    }
}
