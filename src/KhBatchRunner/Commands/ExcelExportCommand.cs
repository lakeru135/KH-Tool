using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.UI;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// Excel Link panel: "Export Excel" — export selected schedules of the ACTIVE document to one
/// workbook (one worksheet per schedule) for editing in Excel. Active-document-only by design
/// (no batch): the same guards as <see cref="Batch.ActiveModelRunner"/>, then the picker dialog.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ExcelExportCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        Document? doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            TaskDialog.Show("Excel Link",
                "No model is open.\n\nOpen the project model whose schedules you want to export.");
            return Result.Cancelled;
        }
        if (doc.IsFamilyDocument)
        {
            TaskDialog.Show("Excel Link",
                "Excel Link runs on project models only — the active document is a family.");
            return Result.Cancelled;
        }

        DialogHost.Show(() => new ExcelExportWindow(doc));
        return Result.Succeeded;
    }
}
