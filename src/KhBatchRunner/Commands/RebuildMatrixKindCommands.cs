using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Reporting;
using KhBatchRunner.UI;

namespace KhBatchRunner.Commands;

/// <summary>
/// The entries of the Multi Model Check panel's "Rebuild Report" dropdown (named Rebuild Matrix
/// until 1.5.5, when it gained the Batch Interop summary).
///
/// <para><b>Why one class per entry rather than one parameterised command.</b> Revit identifies a
/// ribbon item by the command class it points at and gives that command no way to ask which entry
/// was clicked — <c>ExternalCommandData</c> carries the application and the view, not the invoking
/// button. A dropdown with three entries therefore needs three <see cref="IExternalCommand"/> types.
/// All are one line over the same window, so there is no logic to keep in step.</para>
///
/// <para><see cref="RebuildMatrixCommand"/> stays as it is: older ribbons surfaced it as a
/// single button on Multi Model Check, where there is no Action panel to put a dropdown on.</para>
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class RebuildMatrixPhaseWorksetCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        DialogHost.Show(() => new RebuildMatrixWindow(MatrixKind.PhaseWorkset));
        return Result.Succeeded;
    }
}

/// <inheritdoc cref="RebuildMatrixPhaseWorksetCommand"/>
[Transaction(TransactionMode.Manual)]
public sealed class RebuildMatrixDesignOptionCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        DialogHost.Show(() => new RebuildMatrixWindow(MatrixKind.DesignOption));
        return Result.Succeeded;
    }
}

/// <inheritdoc cref="RebuildMatrixPhaseWorksetCommand"/>
[Transaction(TransactionMode.Manual)]
public sealed class RebuildInteropSummaryCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        DialogHost.Show(() => new RebuildMatrixWindow(interopSummary: true));
        return Result.Succeeded;
    }
}
