using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.UI;

namespace KhBatchRunner.Commands;

/// <summary>Setting panel button: open the "Edit DYN Script Set" manager.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class EditScriptSetCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        using var window = new EditScriptSetWindow();
        window.ShowDialog();
        return Result.Succeeded;
    }
}
