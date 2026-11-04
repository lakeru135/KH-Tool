namespace KhBatchRunner.Schedule;

/// <summary>
/// Persists a job and (re)registers its Windows Task Scheduler task — shared by Schedule
/// Run's "Save schedule" and Batch Run's "Save as schedule" so both go through one path.
/// Deleting any previous task under the same id first makes this safe for both a brand new
/// job and an edit-in-place of an existing one.
/// </summary>
public static class ScheduleSaver
{
    public static (bool ok, string message) SaveAndRegister(ScheduledJob job)
    {
        // Full and DRW use the same schedule store, but can coexist in one Revit process. Persist
        // who created the task so only that edition accepts its KHBATCH_JOB launch.
        job.ScheduleOwner = Product.ScheduleOwner;
        // The windows rebuild the job from their fields, so anything they have no control for —
        // the paused flag, the last-run stamp — arrives at its default and would overwrite the
        // stored value. See ScheduleStore.CarryForwardState.
        ScheduleStore.CarryForwardState(job);
        ScheduleStore.Save(job);
        TaskSchedulerGateway.Delete(job);
        string revitExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
        (bool ok, string msg) = TaskSchedulerGateway.Create(job, revitExe);
        if (!ok) ScheduleStore.Delete(job.Id); // roll back the saved job if the task failed
        return (ok, msg);
    }
}
