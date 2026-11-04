using Autodesk.Revit.Attributes;
using KhBatchRunner.Actions;

namespace KhBatchRunner.Commands;

/// <summary>Multi Model Action button: batch "Purge Import CAD".</summary>
[Transaction(TransactionMode.Manual)]
public sealed class PurgeImportCadCommand : BatchActionCommandBase
{
    protected override IBatchAction CreateAction() => new PurgeImportCadAction();
}
