#if !EDITION_DRW
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Mcp;

namespace KhBatchRunner.Commands;

/// <summary>
/// Setting panel: "AI Code ON/OFF", beside AI Bridge. While ON, the AI's run_code tool runs its
/// own C# in the open model without asking (the user, 2026-10-22); each run is one undo step and
/// is logged. Off at every Revit start.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class AiCodeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        // Silent like AI Bridge: the button's caption says which way it is.
        McpCode.Toggle();
        return Result.Succeeded;
    }
}
#endif
