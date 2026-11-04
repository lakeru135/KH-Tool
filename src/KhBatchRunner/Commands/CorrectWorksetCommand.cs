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
/// Model Batch Action panel: "Correct Workset". Defines category rules (categories → target workset +
/// rename keyword) and the RVT-link rule, then runs <see cref="CorrectWorksetAction"/> in Check
/// (report only) or Fix mode over a model set, every model in a Forma folder, or the active model.
/// Fix renames are approved per model in a WinForms dialog mid-run (not auto-answered by the
/// dialog suppressor). Manual + AlwaysAvailable so it runs with no document open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CorrectWorksetCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;

        // Model sets are optional here (folder / active-model hosts work without any).
        List<ModelPreset> presets = PresetStore.LoadAll();
        var client = new DataManagementClient(new SessionTokenProvider());

        CorrectWorksetWindow dialog;
        try
        {
            dialog = new CorrectWorksetWindow(presets, client);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("KH Batch Script Runner", "Could not open the Correct Workset dialog:\n\n" + ex.Message);
            return Result.Failed;
        }
        using var _dlg = dialog;
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return Result.Cancelled;

        CorrectWorksetInputSet inputs = dialog.Inputs!;
        var action = new CorrectWorksetAction(inputs.Rules, inputs.ApplyLinkRule, inputs.LinkKeyword,
            inputs.Fix, inputs.ApplyCadRule, inputs.CadWorksetName)
        {
            // Per-model rename approval — a WinForms dialog, so the batch's DialogSuppressor
            // (Revit dialogs only) never auto-answers it.
            ApprovalCallback = (doc, proposals) =>
            {
                using var approval = new WorksetRenameApprovalDialog(doc.Title, proposals);
                approval.ShowDialog();
            }
        };

        // --- Active model: run directly on the open document (no sync — review/undo). ---
        if (inputs.RunOnActiveModel)
        {
            ActiveModelRunner.Run(uiApp, action);
            return Result.Succeeded;
        }

        // --- Batch hosts: the chosen model set (may be a folder-backed set — re-scanned here). ---
        ModelPreset chosen = PresetResolver.Resolve(dialog.SelectedPreset!, out string? setNote);

        // Fix mode changes models — make the plan explicit before the batch starts.
        if (inputs.Fix)
        {
            string rulesText = inputs.Rules.Count == 0
                ? "(no category rules)"
                : string.Join("\n", inputs.Rules.Select(r => "•  " + r.Label +
                    (string.IsNullOrWhiteSpace(r.Keyword) ? "" : $"  (rename keyword '{r.Keyword}')")));
            var confirm = new TaskDialog("Correct Workset — Model Batch Action")
            {
                MainInstruction = $"Correct worksets in {chosen.Models.Count} model(s)?",
                MainContent =
                    $"Host model set: {chosen.Name} ({chosen.Models.Count} model(s))\n" +
                    rulesText + "\n" +
                    (inputs.ApplyLinkRule
                        ? $"•  RVT links → each alone in '{inputs.LinkKeyword}_<model name>'\n" : "") +
                    (setNote != null ? setNote + "\n" : "") +
                    "\nWorkset RENAMES pause for your approval at each model; creates and moves " +
                    "run automatically. Each model is synced back to its central. Continue?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (confirm.Show() != TaskDialogResult.Yes)
                return Result.Cancelled;
        }

        // Parallel auto-split, same shape as Batch Run / Multi Model Check. Safe for Correct
        // Workset: each host is edited and synced by exactly one group, and there is no
        // cross-model coupling — the rules are per-model. Fix mode's workset RENAMES normally pause
        // for approval, but a chunk runs headless and auto-approves them, which is the same
        // behaviour a scheduled Correct Workset has always had (see ScheduledRunner). The child
        // process rebuilds this action from the job's CorrectWorksetSpec.
        Result? split = Schedule.InteractiveSplit.TryLaunch(
            uiApp,
            new Schedule.ScheduledJob
            {
                Name = inputs.ProjectLogName,
                RunItems = new List<Schedule.RunItem> { new(CorrectWorksetAction.DisplayName, Schedule.RunItemKind.Action) },
                CorrectWorksetSpec = inputs,
                Completion = BatchCompletion.CloseDocuments,
                Sync = inputs.Fix ? SyncMode.Sync : SyncMode.NoSync,
                DestinationFolder = inputs.DestinationFolder,
                ProjectLogName = inputs.ProjectLogName
            },
            chosen.Models, dialog.SplitEnabled, action.Name, dialog.SelectedCompletion);
        if (split.HasValue) return split.Value;

        var engine = new BatchEngine(uiApp, action, inputs.DestinationFolder, inputs.ProjectLogName);
        try
        {
            // Modal message-loop runner, never Idling — hosts are typically Forma cloud models.
            BatchRunnerWindow.RunModal(uiApp, engine, chosen, action.Name, dialog.SelectedCompletion);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("KH Batch Script Runner", $"Could not start Correct Workset: {ex.Message}");
            return Result.Failed;
        }

        return Result.Succeeded;
    }
}
