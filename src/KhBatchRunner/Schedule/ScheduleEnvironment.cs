namespace KhBatchRunner.Schedule;

/// <summary>Environment-variable names shared by the Revit runner and the standalone launcher.</summary>
internal static class ScheduleEnvironment
{
    public const string Job = "KHBATCH_JOB";
    public const string Owner = "KHBATCH_OWNER";
    public const string Claim = "KHBATCH_JOB_CLAIMED";

    /// <summary>
    /// Whether this Revit was started to run work with nobody at the keyboard — a scheduled task or
    /// one group of an auto-split run. True whenever <see cref="Job"/> is set, INDEPENDENTLY of
    /// whether this edition goes on to accept the job.
    ///
    /// <para>That independence is the point. <c>ScheduledRunner.TryGetPendingJob</c> answers a
    /// narrower question — "is this job mine to run" — and returns false in the Full/DRW
    /// coexistence case for whichever edition does not own it. But both editions are loaded in the
    /// same process, and a modal dialog freezes the process, not an edition. Anything protecting an
    /// unattended Revit has to key off the launch, not off the claim.</para>
    /// </summary>
    public static bool IsUnattendedLaunch
    {
        get
        {
            try { return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Job)); }
            catch { return false; }
        }
    }

    /// <summary>
    /// How a tray notification's content reaches <c>KhBatchLauncher.exe --notify</c>. Environment
    /// variables rather than command-line arguments because run names and log paths carry spaces
    /// and quotes, and a quoting bug would truncate a message silently.
    ///
    /// Named here, in the one file both sides compile, for the same reason as the job variables
    /// above: a literal repeated in two projects drifts, and the failure is a notification that
    /// arrives blank rather than a build error.
    /// </summary>
    public const string NotifyTitle = "KHBATCH_NOTIFY_TITLE";
    public const string NotifyMessage = "KHBATCH_NOTIFY_MESSAGE";
    public const string NotifyFailed = "KHBATCH_NOTIFY_FAILED";
    public const string NotifyLog = "KHBATCH_NOTIFY_LOG";
    public const string NotifyMinutes = "KHBATCH_NOTIFY_MINUTES";

    /// <summary>The product name shown in the card's header, where a real Windows toast shows the
    /// app it came from. One launcher serves both editions, so it cannot know whether it is speaking
    /// for "KH Batch Script Runner" or "DRW Helper" unless the caller says so.</summary>
    public const string NotifyApp = "KHBATCH_NOTIFY_APP";

    /// <summary>
    /// The REAL toast raised by <c>KhBatchLauncher.exe --toast</c> (see ToastWinRt). These carry
    /// the same payload the PowerShell helper was given, and deliberately keep the same names: the
    /// two mechanisms are interchangeable, and a diagnostic report that has learned to read one
    /// should not have to learn a second vocabulary for the other.
    /// </summary>
    public const string NotifyToastXml = "KHBATCH_TOAST_XML";
    public const string NotifyToastTag = "KHBATCH_TOAST_TAG";
    public const string NotifyToastAppId = "KHBATCH_TOAST_APP_ID";
    public const string NotifyToastExpiry = "KHBATCH_TOAST_EXPIRY";
}
