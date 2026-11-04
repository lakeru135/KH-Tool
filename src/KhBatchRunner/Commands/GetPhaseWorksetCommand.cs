using Autodesk.Revit.Attributes;
using KhBatchRunner.Actions;

namespace KhBatchRunner.Commands;

/// <summary>Multi Model Check button: batch "Get Phase &amp; Workset".</summary>
[Transaction(TransactionMode.Manual)]
public sealed class GetPhaseWorksetCommand : BatchActionCommandBase
{
    protected override IBatchAction CreateAction() => new GetPhaseAndWorksetAction();
}
