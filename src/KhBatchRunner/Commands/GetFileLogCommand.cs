using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.UI;

namespace KhBatchRunner.Commands;

/// <summary>
/// Forma Batch Action panel: "Get File Log". Opens the Extract window in File Log mode — pick an
/// Forma folder, pull every .rvt's version history to a CSV. Pure REST (no model opened), so it's
/// Manual + AlwaysAvailable and runs with no document open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class GetFileLogCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        DialogHost.Show(() => new FormaExtractWindow(
            new DataManagementClient(new SessionTokenProvider())));
        return Result.Succeeded;
    }
}
