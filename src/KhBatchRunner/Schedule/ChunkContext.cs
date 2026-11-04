namespace KhBatchRunner.Schedule;

/// <summary>
/// Which auto-split group THIS Revit process is running, published once when the chunk job is
/// armed at startup (<see cref="ScheduledRunner.ArmOnIdling"/>).
///
/// Process-wide state on purpose. A Revit launched by <see cref="ChunkLauncher"/> carries exactly
/// one <c>KHBATCH_JOB</c> and runs exactly one group for its whole life, so "which group am I" is
/// a property of the process, not something worth threading through every action signature — the
/// same reasoning that already makes <see cref="Diagnostics.RunTrace"/> ambient.
///
/// It exists because an action that writes per-run FILES cannot ask the job anything:
/// <c>IBatchAction.ConfigureOutput</c> receives a destination folder and a log name, nothing else.
/// Batch Interop names its report folder from a timestamp, and sibling groups launch inside the
/// same second — without a per-group discriminator they share one folder and overwrite each other's
/// reports. See <see cref="Interop.InteropReportPaths"/>.
/// </summary>
internal static class ChunkContext
{
    /// <summary>1-based group number, or 0 when this process is not running an auto-split chunk
    /// (an ordinary interactive run, or a scheduled job that was not split).</summary>
    public static int GroupNumber { get; private set; }

    /// <summary>The stamp every sibling of this run shares (<see cref="ScheduledJob.LogRunStamp"/>),
    /// so their output folders sort together and read as one run. Null when not a chunk.</summary>
    public static DateTime? RunStamp { get; private set; }

    /// <summary>Short suffix identifying this group in a file or folder name — <c>g3</c> — or null
    /// when this process is not a chunk, in which case names stay exactly as they were before
    /// splitting existed.</summary>
    public static string? FolderTag => GroupNumber > 0 ? "g" + GroupNumber : null;

    /// <summary>Records the armed job's group identity. Safe to call with any job: a non-chunk
    /// leaves the context empty.</summary>
    public static void Publish(ScheduledJob job)
    {
        if (job.IsEphemeralChunk && job.ChunkIndex > 0)
        {
            GroupNumber = job.ChunkIndex;
            RunStamp = job.LogRunStamp;
        }
        else
        {
            GroupNumber = 0;
            RunStamp = null;
        }
    }
}
