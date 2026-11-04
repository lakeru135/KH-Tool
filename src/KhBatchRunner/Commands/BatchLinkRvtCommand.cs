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
/// Multi Model Action panel: "Batch Link RVT". Picks a host model set and the source models to link
/// into every one of them, confirms, then runs <see cref="BatchLinkRvtAction"/> on the SAME proven
/// engine and modal runner every other batch button uses. Manual + AlwaysAvailable so it runs with
/// no document open.
///
/// <para><b>No parallel split.</b> <c>InteractiveSplit.TryLaunch</c> serialises a
/// <c>ScheduledJob</c> and the child Revit rebuilds the action from a spec on it; with no spec,
/// <c>ScheduledRunner.BuildJobPasses</c> would fall through to <c>ActionRegistry.Create</c>, which
/// returns the FIRST registered action for a name it does not know — a split run would silently
/// perform "Get Site Location &amp; PBP" on every model and report it green. Split arrives with
/// scheduling, or not at all.</para>
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class BatchLinkRvtCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;

        List<ModelPreset> presets = PresetStore.LoadAll();
        if (presets.Count == 0)
        {
            TaskDialog.Show("Batch Link RVT",
                "No model sets found.\n\n" +
                "Create one first with Setting → Edit Model Set, then run Batch Link RVT.");
            return Result.Cancelled;
        }

        var client = new DataManagementClient(new SessionTokenProvider());

        BatchLinkRvtWindow dialog;
        try
        {
            dialog = new BatchLinkRvtWindow(presets, client);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Batch Link RVT", "Could not open the Batch Link RVT dialog:\n\n" + ex.Message);
            return Result.Failed;
        }
        using var _dlg = dialog;
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return Result.Cancelled;

        BatchLinkRvtInputSet inputs = dialog.Inputs!;

        // Hosts come from the chosen model set (which may be a folder-backed set — re-scanned to its
        // current members here; note = offline fallback).
        ModelPreset chosen = PresetResolver.Resolve(dialog.SelectedPreset!, out string? setNote);

        // A model that is both a host and a source cannot be linked into itself, and once an earlier
        // host has loaded it as a link, BatchEngine.OpenModel can only reopen it read-only and fails
        // it. This action is the one that CREATES that condition, so it is named before the run
        // rather than discovered as N failed models afterwards.
        List<string> clashes = BatchLinkRules.SourcesThatAreAlsoHosts(chosen.Models, inputs.Sources);

        string worksetLine = inputs.PerLinkWorkset
            ? $"Each link gets its own workset, named {inputs.LinkKeyword}_<link name>."
            : $"Every link goes on the workset \"{inputs.SharedWorksetName}\".";

        var confirm = new TaskDialog("Batch Link RVT — Multi Model Action")
        {
            MainInstruction =
                $"Link {inputs.Sources.Count} model(s) into {chosen.Models.Count} host model(s)?",
            MainContent =
                $"Host model set: {chosen.Name} ({chosen.Models.Count} model(s))\n" +
                $"Linking: {Describe(inputs.Sources)}\n" +
                worksetLine + "\n" +
                (inputs.SkipIfAlreadyLinked
                    ? "A source a host already links is skipped, not linked twice.\n"
                    : "DUPLICATES ARE ALLOWED — a host that already links a source gets a second link to it.\n") +
                (clashes.Count > 0
                    ? $"\nWARNING — {clashes.Count} model(s) are BOTH a host and a source " +
                      $"({string.Join(", ", clashes.Take(3))}{(clashes.Count > 3 ? ", …" : "")}). " +
                      "A model cannot link itself, and once an earlier host has loaded it as a link it " +
                      "can only be opened read-only, so it will be reported as failed. Remove it from " +
                      "one side, or run with \"Close files\".\n"
                    : "") +
                (setNote != null ? "\n" + setNote + "\n" : "") + "\n" +
                "Each host is opened, given the links, and synced back to its cloud central. Continue?",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (confirm.Show() != TaskDialogResult.Yes)
            return Result.Cancelled;

        var action = new BatchLinkRvtAction(inputs);
        var engine = new BatchEngine(uiApp, action, inputs.DestinationFolder, inputs.ProjectLogName);

        try
        {
            // Modal message-loop runner, NOT BatchIdlingDriver — hosts are Forma cloud models, and
            // opening a cloud model from an Idling event hangs/crashes Revit's cloud-open UI.
            BatchRunnerWindow.RunModal(uiApp, engine, chosen, action.Name, dialog.SelectedCompletion);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Batch Link RVT", $"Could not start Batch Link RVT: {ex.Message}");
            return Result.Failed;
        }

        return Result.Succeeded;
    }

    /// <summary>The source list for the confirm dialog: names when there are few, counts when many.</summary>
    private static string Describe(IReadOnlyList<CloudModel> sources)
    {
        int local = sources.Count(s => s.IsLocal);
        string kinds = local == 0 ? "all from Forma"
            : local == sources.Count ? "all local files"
            : $"{sources.Count - local} from Forma, {local} local";
        return sources.Count <= 3
            ? string.Join(", ", sources.Select(s => s.DisplayName)) + $"  ({kinds})"
            : $"{sources.Count} models ({kinds})";
    }
}
