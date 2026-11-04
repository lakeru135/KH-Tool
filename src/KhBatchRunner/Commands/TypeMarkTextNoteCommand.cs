using Autodesk.Revit.Attributes;
using KhBatchRunner.Actions;

namespace KhBatchRunner.Commands;

/// <summary>Multi Model Check button: batch "Type Mark by Text Note".</summary>
[Transaction(TransactionMode.Manual)]
public sealed class TypeMarkTextNoteCommand : BatchActionCommandBase
{
    protected override IBatchAction CreateAction() => new TypeMarkTextNoteAction();
}
