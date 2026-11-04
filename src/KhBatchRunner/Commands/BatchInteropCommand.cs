using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Interop;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// Batch Interop: pick a model set + Model Checker checkset, then batch-run the Autodesk
/// Interoperability Tools Model Checker over the set via <see cref="BatchRunnerWindow"/>
/// (the modal message-loop runner — Forma and local models alike), exporting per-model
/// HTML/Excel reports. Read-only: <see cref="BatchInteropAction"/> is a Check, so every
/// model closes without sync/save. Mirrors <see cref="BatchActionCommandBase"/> but can't
/// derive from it — the action carries per-run configuration (checkset), so it needs its
/// own dialog. Gated at runtime on the AIT install for this Revit year (see
/// <see cref="AitLocator"/>); no compile-time AIT dependency exists.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class BatchInteropCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;
        string year = uiApp.Application.VersionNumber;

        switch (AitLocator.Check(year))
        {
            case AitStatus.NotInstalled:
                TaskDialog.Show("Batch Interop",
                    $"Autodesk Interoperability Tools (Model Checker) is not installed for Revit {year}.\n\n" +
                    "Install \"BIM Interoperability Tools\" for this Revit version " +
                    "(interoperability.autodesk.com), then run Batch Interop again.");
                return Result.Cancelled;
            case AitStatus.InstalledUnverified:
                TaskDialog.Show("Batch Interop",
                    $"Autodesk Interoperability Tools is installed for Revit {year}, but Batch Interop " +
                    $"has not been verified against that version yet (verified: Revit 2026).\n\n" +
                    "Run it from Revit 2026, or ask for this version to be verified.");
                return Result.Cancelled;
        }

        List<ModelPreset> presets = PresetStore.LoadAll();
        if (presets.Count == 0)
        {
            TaskDialog.Show("KH Batch Script Runner",
                "No model sets found.\n\n" +
                "Create one first with Setting → Edit Model Set, then run this action.");
            return Result.Cancelled;
        }

        using var dialog = new BatchInteropWindow(presets, year);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return Result.Cancelled;

        var action = new BatchInteropAction(dialog.ChecksetPath!, dialog.ChecksetExaminesLinks);
        // NoSync documents intent only — a Check never persists regardless.
        var engine = new BatchEngine(uiApp, action, dialog.DestinationFolder!,
            dialog.ProjectLogName!, SyncMode.NoSync);
        // A folder-backed set is re-scanned to its current members here.
        Presets.ModelPreset chosen = Presets.PresetResolver.Resolve(dialog.SelectedPreset!, out _);

        // Parallel auto-split (1.3.7; single-process before that — see BatchInteropAction
        // .AllowsParallelRuns for what the investigation did and did not establish). The child
        // rebuilds this action from the job alone, which works because the job carries the checkset
        // in InteropChecksetPath and Duplicate() copies it — see ScheduledRunner.BuildJobPasses.
        var baseJob = new ScheduledJob
        {
            Name = dialog.ProjectLogName!,
            RunItems = new List<RunItem> { new(action.Name, RunItemKind.Action) },
            InteropChecksetPath = dialog.ChecksetPath,
            InteropChecksetExaminesLinks = dialog.ChecksetExaminesLinks,
            Completion = BatchCompletion.CloseDocuments,   // each group's Revit quits when done
            Sync = SyncMode.NoSync,                        // a Check never persists
            DestinationFolder = dialog.DestinationFolder,
            ProjectLogName = dialog.ProjectLogName
        };
        Result? split = InteractiveSplit.TryLaunch(
            uiApp, baseJob, chosen.Models, dialog.SplitEnabled, action.Name,
            dialog.SelectedCompletion,
            InteropSplitPolicy.BlockedReason(baseJob, year),
            // Always a separate Revit, even for a set too small to split: the Model Checker opens
            // models Revit can crash on, and that must never take this Revit down with it.
            alwaysSupervise: true);
        if (split != null) return split.Value;

        try
        {
            // Modal message-loop runner — see BatchActionCommandBase for
            // why batches never run from an Idling event (cloud opens hang/crash there).
            BatchRunnerWindow.RunModal(uiApp, engine, chosen,
                $"Run: {action.Name}", dialog.SelectedCompletion);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("KH Batch Script Runner", $"Could not run batch: {ex.Message}");
            return Result.Failed;
        }

        return Result.Succeeded;
    }
}
