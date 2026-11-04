namespace KhBatchRunner.Schedule;

/// <summary>
/// The Batch Interop step, as seen by code that must recognise it WITHOUT being able to touch it.
///
/// Mirrors <see cref="FileLogStep"/>, and exists for the same structural reason. Scheduling decisions
/// — how heavy a run is (<see cref="InstanceBudget.WeightFor"/>), whether it may split — are made in
/// files that the standalone controller compiles too, and the controller has no Revit assemblies.
/// <c>Actions.BatchInteropAction</c> derives from a Revit-typed base, so naming it from here would
/// (and briefly did) break that build with "the type BatchInteropAction does not exist".
///
/// The name is declared HERE and the action points at it, rather than the other way round: a Revit
/// action can always see plain scheduling data, but plain scheduling data can never see a Revit type.
/// That direction is what keeps one string from becoming two.
/// </summary>
internal static class InteropStep
{
    /// <summary>The single source of truth for the action's name.
    /// <c>Actions.BatchInteropAction.DisplayName</c> is an alias of this.</summary>
    public const string DisplayName = "Batch Interop";

    /// <summary>Whether this job runs the Model Checker at all — true for a dedicated Interop job
    /// and for a mixed one that merely includes the step, since either way some instance of the run
    /// pays for AIT.</summary>
    public static bool IsRequested(ScheduledJob? job) =>
        job != null && job.ActionNames.Contains(DisplayName);
}
