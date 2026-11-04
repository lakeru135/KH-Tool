namespace KhBatchRunner.Interop;

/// <summary>Whether the Autodesk Interoperability Tools Model Checker can run under the
/// current Revit. Decided at runtime by probing the AIT install folder — never at compile
/// time, because AIT is installed per Revit year independently of this add-in's build.</summary>
public enum AitStatus
{
    /// <summary>Installed for this Revit year and verified with this add-in.</summary>
    Supported,
    /// <summary>Installed for this Revit year, but Batch Interop hasn't been verified against
    /// that AIT build (e.g. Revit 2024's AIT 10.0.1 on net48) — blocked until tested.</summary>
    InstalledUnverified,
    /// <summary>No AIT install for this Revit year.</summary>
    NotInstalled,
}

public static class AitLocator
{
    // Years whose AIT build the Batch Interop spike/verification has actually been run
    // against. Grows as further years are tested (2024 is installed but unverified).
    private static readonly string[] VerifiedYears = { "2026" };

    public static AitStatus Check(string revitVersionNumber)
    {
        if (!ModelCheckerInvoker.IsInstalled(revitVersionNumber))
            return AitStatus.NotInstalled;
        return VerifiedYears.Contains(revitVersionNumber)
            ? AitStatus.Supported
            : AitStatus.InstalledUnverified;
    }
}
