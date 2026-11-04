#if !EDITION_DRW
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Mcp;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// Setting panel: "AI Bridge ON/OFF" — a toggle like DRW's Sync View. While ON, an AI assistant
/// (Claude Code with the kh-tool MCP server) can run KH-Tool / DRW Helper tools in the open model;
/// every call it makes is one undo step. Off at every Revit start.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class AiBridgeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        // Silent like Sync View; the only thing worth a dialog is a refusal.
        if (McpBridge.Toggle() is string why)
            TaskDialog.Show("AI Bridge", why);
        return Result.Succeeded;
    }
}
#endif
