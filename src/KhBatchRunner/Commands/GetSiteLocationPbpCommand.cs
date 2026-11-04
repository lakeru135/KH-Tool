using Autodesk.Revit.Attributes;
using KhBatchRunner.Actions;

namespace KhBatchRunner.Commands;

/// <summary>Multi Model Check button: batch "Get Site Location &amp; PBP".</summary>
[Transaction(TransactionMode.Manual)]
public sealed class GetSiteLocationPbpCommand : BatchActionCommandBase
{
    protected override IBatchAction CreateAction() => new GetSiteLocationPbpAction();
}
