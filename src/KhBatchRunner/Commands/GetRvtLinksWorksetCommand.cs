using Autodesk.Revit.Attributes;
using KhBatchRunner.Actions;

namespace KhBatchRunner.Commands;

/// <summary>Multi Model Check button: batch "Get Link Workset" (RVT/IFC links, CAD links + imports,
/// images and point clouds).</summary>
[Transaction(TransactionMode.Manual)]
public sealed class GetRvtLinksWorksetCommand : BatchActionCommandBase
{
    protected override IBatchAction CreateAction() => new GetRvtLinksWorksetAction();
}
