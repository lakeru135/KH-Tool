using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.UI;

namespace KhBatchRunner.Commands;

/// <summary>
/// Forma Batch Action panel: "Get Data Extract". Opens the Data Extract window — pick an extract
/// Forma's Data Connector already runs, download its newest finished run as CSVs. Pure REST (no
/// model opened), so it's Manual + AlwaysAvailable and runs with no document open.
///
/// <para>Both clients share ONE token provider, the way CheckSheetPublishCommand does: two
/// providers would each borrow the Revit session token separately, and a window that signs in
/// twice for one dialog is two chances to fail rather than one.</para>
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class GetDataExtractCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        var tokens = new SessionTokenProvider();
        DialogHost.Show(() => new DataExtractWindow(
            new DataManagementClient(tokens), new DataConnectorClient(tokens)));
        return Result.Succeeded;
    }
}
