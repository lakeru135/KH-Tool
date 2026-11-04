using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.UI;

// WinForms and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Batch;

/// <summary>
/// Runs a single <see cref="IBatchAction"/> against the model currently open in Revit —
/// the "Run on active model" option on the Check/Action buttons. Deliberately bypasses
/// <see cref="BatchEngine"/> entirely: no model set, no background open, no CSV/tracking/detail
/// logs, no xlsx matrix, and NO save/sync/close. The action commits its own transaction (so
/// a write action's changes land on the user's undo stack, exactly like manual editing), and
/// the rows are shown in an <see cref="ActionResultsWindow"/>. The user reviews, then saves/
/// syncs or presses Ctrl+Z — this runner never touches the document's lifecycle.
/// </summary>
public static class ActiveModelRunner
{
    /// <summary>Runs the action on the active document. Returns false when there is no runnable
    /// active document (nothing open, or a family document); a friendly dialog is shown first.</summary>
    public static bool Run(UIApplication uiApp, IBatchAction action)
    {
        Document? doc = uiApp.ActiveUIDocument?.Document;
        if (doc == null)
        {
            TaskDialog.Show("KH Batch Script Runner",
                "No model is open.\n\nOpen a project model first, or untick \"Run on active model\".");
            return false;
        }
        if (doc.IsFamilyDocument)
        {
            TaskDialog.Show("KH Batch Script Runner",
                "This runs on project models only — the active document is a family.");
            return false;
        }

        IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
        try
        {
            // RunDetailRows only (never Run() as well) — same call the engine makes per model,
            // so a write action's DoX() work happens exactly once.
            rows = action.RunDetailRows(uiApp, doc);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("KH Batch Script Runner", $"\"{action.Name}\" failed on this model:\n\n{ex.Message}");
            return true;
        }

        using var results = new ActionResultsWindow(
            action.Name, MatrixModelName(doc), action.Columns, rows, action.PerModelNote, action.ModifiesModel);
        results.ShowDialog();
        return true;
    }

    // The active doc's Title is the friendliest name to show; a detached local copy carries a
    // trailing "_detached" that has no meaning on a live open model, so trim it like the matrix does.
    private static string MatrixModelName(Document doc)
    {
        string title = doc.Title ?? "";
        const string suffix = "_detached";
        return title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? title.Substring(0, title.Length - suffix.Length)
            : title;
    }
}
