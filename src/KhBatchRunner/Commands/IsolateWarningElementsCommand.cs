using Autodesk.Revit.Attributes;
using KhBatchRunner.Actions;

namespace KhBatchRunner.Commands;

/// <summary>Multi Model Action button: batch "Isolate Warning Elements".</summary>
[Transaction(TransactionMode.Manual)]
public sealed class IsolateWarningElementsCommand : BatchActionCommandBase
{
    protected override IBatchAction CreateAction() => new IsolateWarningElementsAction();
}
