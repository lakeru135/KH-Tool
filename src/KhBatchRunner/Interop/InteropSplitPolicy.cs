using KhBatchRunner.Actions;
using KhBatchRunner.Schedule;

namespace KhBatchRunner.Interop;

/// <summary>
/// Whether a job containing <see cref="BatchInteropAction"/> may be split across parallel Revit
/// instances, and if not, the sentence to tell the user.
///
/// One place on purpose. v1.3.4's equivalent rule lived as a private helper on
/// <c>ScheduledRunner</c>, which meant the scheduled and Run-now paths were covered and the
/// interactive command enforced the same policy by simply not calling the splitter — three
/// expressions of one rule, and nothing that would notice if they drifted. Every entry point now
/// asks this.
/// </summary>
internal static class InteropSplitPolicy
{
    /// <summary>
    /// Null when this job may split, otherwise a reason phrased to drop into
    /// "Parallel groups skipped — {reason} Running all models in this one Revit instead."
    /// A job with no Interop step always returns null; this class judges Interop only.
    /// </summary>
    /// <param name="revitVersionNumber">e.g. "2026". AIT installs per Revit year, so the answer is
    /// per year — not a property of this build.</param>
    public static string? BlockedReason(ScheduledJob job, string revitVersionNumber)
    {
        if (!job.ActionNames.Contains(BatchInteropAction.DisplayName)) return null;

#pragma warning disable CS0162 // unreachable while the const is true — kept as the kill switch
        if (!BatchInteropAction.AllowsParallelRuns)
            return "Batch Interop is restricted to a single Revit process in this build.";
#pragma warning restore CS0162

        // The Model Checker bridge is verified per Revit year (AitLocator.VerifiedYears). Running
        // ONE instance against an unverified AIT build is a supported experiment; running several
        // is not the way to find out whether that build behaves.
        if (AitLocator.Check(revitVersionNumber) != AitStatus.Supported)
            return $"the Model Checker has not been verified for Revit {revitVersionNumber}, " +
                   "and parallel groups are only enabled for verified years.";

        return null;
    }
}
