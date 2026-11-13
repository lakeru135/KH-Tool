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

        // A runnable active document (a project model, not a family) lets the user run this on what
        // is open right now instead of a model set — the same offer the simple Check/Action buttons
        // make (see BatchActionCommandBase).
        Document? activeDoc = uiApp.ActiveUIDocument?.Document;
        string? activeName = (activeDoc != null && !activeDoc.IsFamilyDocument) ? activeDoc.Title : null;

        List<ModelPreset> presets = PresetStore.LoadAll();
        if (activeName == null && BatchCommandFlow.NoModelSets(presets, "Export IFC + Data", "Export IFC + Data"))
            return Result.Cancelled;

        using var dialog = new ExportIfcWindow(activeName);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || dialog.RunSpec is null)
            return Result.Cancelled;

        ExportIfcInputSet inputs = dialog.RunSpec;

        // Active model: export the open document as it is open (see ExportNwcCommand). The runner
        // calls ConfigureOutput and OnRunComplete around the one model, so the run folder, the
        // manifest and the converter behave as they do for a one-model batch.
        if (dialog.RunOnActiveModel)
            return ActiveModelRunner.Run(uiApp, new ExportIfcDataAction(inputs),
                    inputs.DestinationFolder, inputs.ProjectLogName)
                ? Result.Succeeded : Result.Cancelled;

        // One or more model sets, combined into a single de-duplicated run — the combiner Batch Run
        // and the Check/Action buttons use. A folder-backed set is re-scanned to its current members
        // there, and the notes report a set that is gone or a model this Revit cannot run.
        IReadOnlyList<string> setNames = dialog.SelectedModelSetNames;
        ModelPreset chosen = ModelPresetCombiner.Combine(
            setNames.Count == 1 ? setNames[0] : "Export IFC + Data", setNames, Array.Empty<CloudModel>(),
            out List<string> setNotes);
        if (chosen.Models.Count == 0)
        {
            TaskDialog.Show("Export IFC + Data",
                "The ticked model set(s) have no models to run." +
                (setNotes.Count > 0 ? "\n\n" + string.Join("\n", setNotes) : ""));
            return Result.Failed;
        }
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

        foreach (string note in setNotes) Diagnostics.RunTrace.Mark(note);

        // The modal message-loop runner, as Export NWC: cloud opens hang from an Idling event.
        return BatchCommandFlow.RunModal(uiApp, action, chosen, inputs.DestinationFolder,
            inputs.ProjectLogName, BatchCompletion.CloseDocuments, "Export IFC + Data", "the export");
    }
}
