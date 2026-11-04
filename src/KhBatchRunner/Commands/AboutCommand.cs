using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.UI;

namespace KhBatchRunner.Commands;

/// <summary>Setting panel: "About" — shows the add-in's logo, version, publisher and description.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        DialogHost.Show(() => new AboutWindow());
        return Result.Succeeded;
    }
}
