using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.ExcelLink;
using KhBatchRunner.UI;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using DialogResult = System.Windows.Forms.DialogResult;

namespace KhBatchRunner.Commands;

/// <summary>
/// Excel Link panel: "Import Excel" — pick a workbook the exporter wrote (edited/saved in Excel),
/// ANALYZE it read-only (<see cref="ScheduleXlsxImporter.Analyze"/>), confirm what would change,
/// then apply everything in ONE transaction and show per-cell results. Active-document-only;
/// nothing is saved or synced — the user reviews, then saves/syncs or presses Ctrl+Z, exactly
/// like the "Run on active model" convention.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ExcelImportCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        Document? doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            TaskDialog.Show("Excel Link",
                "No model is open.\n\nOpen the project model the workbook was exported from.");
            return Result.Cancelled;
        }
        if (doc.IsFamilyDocument)
        {
            TaskDialog.Show("Excel Link",
                "Excel Link runs on project models only — the active document is a family.");
            return Result.Cancelled;
        }

        string path;
        using (var open = new System.Windows.Forms.OpenFileDialog
               {
                   Title = "Import Excel — Excel Link",
                   Filter = "Excel workbook (*.xlsx)|*.xlsx"
               })
        {
            if (open.ShowDialog() != DialogResult.OK) return Result.Cancelled;
            path = open.FileName;
        }

        // --- Phase 1: read + validate + diff (no writes) ---
        ExcelLinkPlan plan;
        try
        {
            plan = ScheduleXlsxImporter.Analyze(doc, path);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Excel Link", "Could not read the workbook:\n\n" + ex.Message);
            return Result.Failed;
        }

        if (plan.SheetsMatched == 0 && plan.Issues.Count == 0)
        {
            TaskDialog.Show("Excel Link",
                "No Excel Link sheets found in this workbook for the active model.\n\n" +
                "Export first with \"Export Excel\", edit the workbook in Excel, then import it here.");
            return Result.Cancelled;
        }

        if (plan.Changes.Count == 0)
        {
            if (plan.Issues.Count == 0)
            {
                TaskDialog.Show("Excel Link",
                    "No changes found — the workbook's editable values match the model.");
                return Result.Succeeded;
            }
            // Nothing writable changed, but there ARE things worth seeing (skipped sheets,
            // locked-cell edits, vanished elements) — surface them without touching the model.
            DialogHost.Show(() => new ActionResultsWindow(
                "Excel Link import (nothing applied)", doc.Title,
                ScheduleXlsxImporter.ResultColumns, plan.Issues,
                "no editable changes found", modelModified: false));
            return Result.Succeeded;
        }

        // --- Confirm ---
        var confirm = new TaskDialog("Import Excel — Excel Link")
        {
            MainInstruction = $"Apply {plan.Changes.Count} change(s) to {doc.Title}?",
            MainContent =
                string.Join("\n", plan.SheetSummaries) +
                (plan.Issues.Count > 0 ? $"\n{plan.Issues.Count} issue(s) will be listed in the results." : "") +
                "\n\nAll changes land as ONE undo step (Ctrl+Z reverts the whole import). " +
                "Nothing is saved or synced — review, then save/sync yourself.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (confirm.Show() != TaskDialogResult.Yes)
            return Result.Cancelled;

        // --- Phase 2: write (one transaction) ---
        int applied;
        List<IReadOnlyDictionary<string, string>> rows;
        try
        {
            rows = ScheduleXlsxImporter.Apply(doc, plan, out applied);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Excel Link", "Import failed:\n\n" + ex.Message);
            return Result.Failed;
        }
        rows.AddRange(plan.Issues);

        DialogHost.Show(() => new ActionResultsWindow(
            "Excel Link import", doc.Title,
            ScheduleXlsxImporter.ResultColumns, rows,
            $"{applied} value(s) updated", modelModified: applied > 0));
        return Result.Succeeded;
    }
}
