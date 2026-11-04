using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.UI;

namespace KhBatchRunner.Commands;

/// <summary>
/// Setting panel command: "Edit Model Set". Opens the model-set manager — browse Forma and/or
/// add local .rvt files, and create/load/edit/delete saved sets. Auth borrows the signed-in
/// Revit session token via <see cref="SessionTokenProvider"/>; the window still opens without
/// a token so local-only or existing sets can be managed offline (the Forma tree just won't load).
/// (Class name kept as BrowseFormaCommand for continuity; the button reads "Edit Model Set".)
///
/// Manual (not ReadOnly) so it can run with no document open — makes no model edits and opens
/// no transaction. Paired with AlwaysAvailable so the button is clickable on the home screen.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class BrowseFormaCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        var client = new DataManagementClient(new SessionTokenProvider());
        using var window = new FormaBrowserWindow(client);
        window.ShowDialog();
        return Result.Succeeded;
    }
}
