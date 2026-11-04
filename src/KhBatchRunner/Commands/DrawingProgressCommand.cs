using Autodesk.Revit.Attributes;
using KhBatchRunner.Actions;

namespace KhBatchRunner.Commands;

/// <summary>Multi Model Check button: batch "Drawing Progress".</summary>
[Transaction(TransactionMode.Manual)]
public sealed class DrawingProgressCommand : BatchActionCommandBase
{
    protected override IBatchAction CreateAction() => new DrawingProgressAction();
}
