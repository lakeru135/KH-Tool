using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.UI;

namespace KhBatchRunner.Commands;

/// <summary>
/// Multi Model Check panel: "Rebuild Matrix". Opens the window that regenerates a Phase &amp;
/// Workset or Design Option matrix workbook from a detail CSV the add-in already exported — no
/// Revit model opened. (An RVT Links Workset matrix was offered here until 1.3.8.2 removed that
/// workbook entirely.) Manual + AlwaysAvailable so it runs with no document open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class RebuildMatrixCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        DialogHost.Show(() => new RebuildMatrixWindow());
        return Result.Succeeded;
    }
}
