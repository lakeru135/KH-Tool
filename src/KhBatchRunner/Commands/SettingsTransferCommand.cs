using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.UI;

namespace KhBatchRunner.Commands;

/// <summary>
/// Setting panel: "Import / Export Settings". Opens the window that copies saved sets to and from
/// a single zip so a new PC does not start empty. Opens no model and changes no schedule.
/// Manual + AlwaysAvailable so it runs with no document open — moving settings onto a fresh
/// machine is the first thing done there, before anything is opened.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class SettingsTransferCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        DialogHost.Show(() => new SettingsTransferWindow());
        return Result.Succeeded;
    }
}
