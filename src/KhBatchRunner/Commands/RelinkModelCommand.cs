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
/// Forma Batch Action panel: "Internal Relink". Picks a host model set + the Forma folders to scan for the
/// new-prefix target models + an OLD → NEW site-code pair, scans the target folders once (REST) to
/// resolve every candidate target's cloud identity, confirms, then batch-relinks each host model's
/// Revit links via <see cref="RelinkLinksAction"/> on the SAME proven engine/runner as the other
/// batch buttons. Manual + AlwaysAvailable so it runs with no document open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class RelinkModelCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;

        List<ModelPreset> presets = PresetStore.LoadAll();
        if (BatchCommandFlow.NoModelSets(presets, "KH Batch Script Runner", "Internal Relink"))
            return Result.Cancelled;

        var client = new DataManagementClient(new SessionTokenProvider());

        RelinkModelWindow? dialog = BatchCommandFlow.TryCreateDialog(
            () => new RelinkModelWindow(presets, client), "KH Batch Script Runner", "Internal Relink");
        if (dialog == null) return Result.Failed;
        using var _dlg = dialog;
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return Result.Cancelled;

        RelinkInputSet inputs = dialog.Inputs!;

        // Hosts come from the chosen model set (which may be a folder-backed set — re-scanned to
        // its current members here; note = offline fallback).
        ModelPreset chosen = PresetResolver.Resolve(dialog.SelectedPreset!, out string? setNote);

        // Scan the target folders ONCE to resolve every candidate target model's cloud identity.
        Dictionary<string, List<RelinkTarget>> targets;
        int multiNameCount;
        try
        {
            targets = BuildTargetMap(client, inputs, out multiNameCount);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("KH Batch Script Runner",
                "Could not scan the target folders for models:\n\n" + ex.Message);
            return Result.Failed;
        }

        if (targets.Count == 0)
        {
            TaskDialog.Show("Internal Relink — Forma Batch Action",
                "No target models with a cloud GUID were found in the selected folders" +
                (inputs.IncludeSubfolders ? " (subfolders included)." : ".") +
                "\n\nPick the folder(s) that contain the new-prefix models, then try again.");
            return Result.Cancelled;
        }

        // Preview + confirm — this rewrites the host models' link references and syncs them.
        int candidateCount = targets.Values.Sum(l => l.Count);
        var confirm = new TaskDialog("Internal Relink — Forma Batch Action")
        {
            MainInstruction = $"Relink {chosen.Models.Count} host model(s): {inputs.OldCode} → {inputs.NewCode}?",
            MainContent =
                $"Host model set: {chosen.Name} ({chosen.Models.Count} model(s))\n" +
                $"Remap: links named '{inputs.OldCode}{inputs.Separator}…' → '{inputs.NewCode}{inputs.Separator}…'\n" +
                (inputs.MatchByExactName
                    ? "Other links are re-pointed when a target model has the exact same name (bridge/shared models).\n"
                    : "") +
                $"Target models found: {candidateCount} ({targets.Count} name(s)) across {inputs.TargetFolders.Count} folder(s)" +
                (multiNameCount > 0
                    ? $"\n{multiNameCount} name(s) have several same-named candidates — resolved per link " +
                      "(the copy a link already points at is excluded; anything still ambiguous is reported, not guessed)."
                    : "") + "\n" +
                (setNote != null ? setNote + "\n" : "") + "\n" +
                "Each host is opened, its matching links are re-pointed to the target models, " +
                "and the host is synced back to its cloud central. Continue?",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (confirm.Show() != TaskDialogResult.Yes)
            return Result.Cancelled;

        var action = new RelinkLinksAction(inputs.OldCode, inputs.NewCode, inputs.Separator,
            inputs.MatchByExactName, targets);

        // Parallel auto-split, same shape as Batch Run / Multi Model Check — but Internal Relink is
        // the one action with a real reason to refuse it (see RelinkSplitGuard). The child process
        // rebuilds this action from the job's RelinkSpec and re-scans the target folders itself.
        Result? split = Schedule.InteractiveSplit.TryLaunch(
            uiApp,
            new Schedule.ScheduledJob
            {
                Name = inputs.ProjectLogName,
                RunItems = new List<Schedule.RunItem> { new(RelinkLinksAction.DisplayName, Schedule.RunItemKind.Action) },
                RelinkSpec = inputs,
                Completion = BatchCompletion.CloseDocuments,
                Sync = SyncMode.Sync,     // each host is synced back to its central
                DestinationFolder = inputs.DestinationFolder,
                ProjectLogName = inputs.ProjectLogName
            },
            chosen.Models, dialog.SplitEnabled, action.Name, dialog.SelectedCompletion,
            Schedule.RelinkSplitGuard.SelfLinkingHost(chosen.Models, targets));
        if (split.HasValue) return split.Value;

        // Modal message-loop runner, NOT BatchIdlingDriver — hosts are Forma cloud models.
        return BatchCommandFlow.RunModal(uiApp, action, chosen, inputs.DestinationFolder,
            inputs.ProjectLogName, dialog.SelectedCompletion, "KH Batch Script Runner", "Internal Relink");
    }

    /// <summary>
    /// The HOSTS for a folder-mode run: every cloud model found in the chosen host folder, as an
    /// ad-hoc model preset. Shared with the headless relink spike; the scan itself lives in
    /// <see cref="FolderHosts.BuildPreset"/> (also used by Correct Workset).
    /// </summary>
    internal static ModelPreset BuildHostPreset(DataManagementClient client, RelinkInputSet inputs)
    {
        if (inputs.HostFolder is null)
            throw new InvalidOperationException("No host folder stored in the relink inputs.");
        return FolderHosts.BuildPreset(client, inputs.HostFolder, inputs.HostIncludeSubfolders);
    }

    /// <summary>
    /// Scans every chosen target folder and maps normalized model name → ALL same-named cloud
    /// identities. Only Revit models with a cloud GUID are targets. A name that recurs as the SAME
    /// model (overlapping folders/subfolders) is de-duped; DIFFERENT models sharing a name are all
    /// kept — the action resolves per link (excluding the copy the link already points at) and
    /// reports anything still ambiguous instead of guessing. <paramref name="multiNameCount"/> is
    /// how many names carry several candidates (surfaced in the confirm dialog).
    /// </summary>
    internal static Dictionary<string, List<RelinkTarget>> BuildTargetMap(
        DataManagementClient client, RelinkInputSet inputs, out int multiNameCount)
    {
        var map = new Dictionary<string, List<RelinkTarget>>(StringComparer.OrdinalIgnoreCase);

        foreach (FolderRef folder in inputs.TargetFolders)
        {
            foreach (FormaInventoryRow row in client.GetFolderInventory(folder.ToEntry(), inputs.IncludeSubfolders))
            {
                if (row.ModelGuid == Guid.Empty) continue;
                string key = RelinkLinksAction.Normalize(row.ModelName);
                if (!map.TryGetValue(key, out List<RelinkTarget>? list))
                    map[key] = list = new List<RelinkTarget>();
                if (list.Any(t => t.ModelGuid == row.ModelGuid))
                    continue; // same model reached twice (overlapping folders) — keep the first
                list.Add(new RelinkTarget
                {
                    DisplayName = row.ModelName,
                    Region = row.Region,
                    ProjectGuid = row.ProjectGuid,
                    ModelGuid = row.ModelGuid,
                    FolderPath = row.FolderPath
                });
            }
        }

        multiNameCount = map.Values.Count(l => l.Count > 1);
        return map;
    }
}
