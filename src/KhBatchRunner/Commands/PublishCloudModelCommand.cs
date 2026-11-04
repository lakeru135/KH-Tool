using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.Presets;
using KhBatchRunner.UI;

// WinForms and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// "Publish Cloud Model": batch "Publish Latest Version" over a saved model set via the Data
/// Management C4R publish command — refreshes each cloud model's PUBLISHED version in Forma without
/// opening any Revit document. Pure REST; Manual + AlwaysAvailable so it runs with no document open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PublishCloudModelCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        List<ModelPreset> presets = PresetStore.LoadAll();
        if (presets.Count == 0)
        {
            TaskDialog.Show("KH Batch Script Runner",
                "No model sets found.\n\n" +
                "Create one first with Setting → Edit Model Set, then run Publish Cloud Model.");
            return Result.Cancelled;
        }

        DialogHost.Show(() => new PublishCloudModelWindow(presets, new DataManagementClient(new SessionTokenProvider())));
        return Result.Succeeded;
    }
}
