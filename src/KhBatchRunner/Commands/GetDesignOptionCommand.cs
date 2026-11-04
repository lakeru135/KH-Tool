using Autodesk.Revit.Attributes;
using KhBatchRunner.Actions;

namespace KhBatchRunner.Commands;

/// <summary>Multi Model Check button: batch "Get Design Option".</summary>
[Transaction(TransactionMode.Manual)]
public sealed class GetDesignOptionCommand : BatchActionCommandBase
{
    protected override IBatchAction CreateAction() => new GetDesignOptionAction();
}
