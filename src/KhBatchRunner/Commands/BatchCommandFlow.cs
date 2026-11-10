using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;
using KhBatchRunner.UI;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// The steps the per-action batch commands (Cut model, Initiate, Internal Relink, Correct Workset,
/// Batch Link RVT, Export NWC, Export IFC + Data, Batch Interop) had written out word for word.
/// Each helper shows exactly the dialog its callers showed, with the title and wording they pass;
/// everything that differs between the commands (confirm dialogs, split jobs, guards) stays in the
/// command itself.
/// </summary>
internal static class BatchCommandFlow
{
    /// <summary>
    /// Shows "No model sets found" under <paramref name="title"/> and returns true when
    /// <paramref name="presets"/> is empty; the caller then returns <see cref="Result.Cancelled"/>.
    /// </summary>
    /// <param name="runWhat">Ends the hint: "…, then run <paramref name="runWhat"/>."</param>
    public static bool NoModelSets(List<ModelPreset> presets, string title, string runWhat)
    {
        if (presets.Count != 0) return false;
        TaskDialog.Show(title,
            "No model sets found.\n\n" +
            "Create one first with Setting → Edit Model Set, then run " + runWhat + ".");
        return true;
    }

    /// <summary>
    /// Builds the command's dialog; if its constructor throws, says so under <paramref name="title"/>
    /// and returns null (the caller then returns <see cref="Result.Failed"/>).
    /// </summary>
    public static T? TryCreateDialog<T>(Func<T> create, string title, string dialogName) where T : class
    {
        try
        {
            return create();
        }
        catch (Exception ex)
        {
            TaskDialog.Show(title, "Could not open the " + dialogName + " dialog:\n\n" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Resolves a publish target from the dialog's inputs; if that throws, reports it and returns
    /// null (the caller then returns <see cref="Result.Failed"/>).
    /// </summary>
    public static PublishTarget? TryPublishTarget(Func<PublishTarget> resolve)
    {
        try
        {
            return resolve();
        }
        catch (Exception ex)
        {
            TaskDialog.Show("KH Batch Script Runner", $"Invalid publish target: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates the engine and runs <paramref name="action"/> over <paramref name="chosen"/> in the
    /// modal message-loop runner — never from an Idling event: these models are usually Forma cloud
    /// models, and opening a cloud model from an Idling event hangs/crashes Revit's cloud-open UI
    /// (see <see cref="BatchRunnerWindow"/>). A failure to start is shown as
    /// "Could not start <paramref name="failSubject"/>: …" under <paramref name="failTitle"/>.
    /// </summary>
    public static Result RunModal(UIApplication uiApp, IBatchAction action, ModelPreset chosen,
        string destinationFolder, string projectLogName, BatchCompletion completion,
        string failTitle, string failSubject)
    {
        var engine = new BatchEngine(uiApp, action, destinationFolder, projectLogName);

        try
        {
            BatchRunnerWindow.RunModal(uiApp, engine, chosen, action.Name, completion);
        }
        catch (Exception ex)
        {
            TaskDialog.Show(failTitle, $"Could not start {failSubject}: {ex.Message}");
            return Result.Failed;
        }

        return Result.Succeeded;
    }
}
