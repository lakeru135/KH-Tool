using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.UI;

namespace KhBatchRunner.Commands;

/// <summary>
/// Forma Batch Action panel: "Check Sheet Publish". Opens the window to build/save a folder set,
/// supply a sheet-numbers CSV, and report which sheets are present among each model's published
/// (translated) 2D sheet views. Pure REST (Data Management + Model Derivative), no model opened;
/// Manual + AlwaysAvailable so it runs with no document open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CheckSheetPublishCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        var tokens = new SessionTokenProvider();
        DialogHost.Show(() => new CheckSheetPublishWindow(
            new DataManagementClient(tokens), new ModelDerivativeClient(tokens)));
        return Result.Succeeded;
    }
}
