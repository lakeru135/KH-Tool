using KhBatchRunner.Diagnostics;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Works out, at Revit's startup, whether THIS Revit was started to run a scheduled job — for the
/// launch shape that cannot simply say so.
///
/// <para><b>Why anything has to be worked out at all.</b> Every other launch announces itself in an
/// environment variable: <c>KhBatchLauncher.exe</c> sets <c>KHBATCH_JOB</c> on the child it
/// starts, the cmd.exe fallback action does the same with <c>set</c>, and an auto-split parent sets
/// it on each group it launches (<see cref="ChunkLauncher"/>). The Revit-direct task action has no
/// such channel: Task Scheduler can start a program, but it cannot give that program an environment
/// variable, and Revit's command line belongs to Revit — a stray argument there is something Revit
/// tries to open as a file. What a task CAN hand its program is a working directory, so that is the
/// channel: one empty folder per registered job, named after the job
/// (<c>TaskSchedulerGateway.FireDirectory</c>), and its name is the whole message.</para>
///
/// <para><b>Why that action exists</b> is <see cref="TaskSchedulerGateway"/>'s story: a PC whose
/// policy refuses this add-in's own launcher used to get a cmd.exe task action, which runs the
/// schedule correctly and leaves a console window on the desktop for the entire run, because cmd
/// waits for Revit on purpose. Starting Revit itself draws nothing.</para>
///
/// <para><b>It cannot misfire.</b> An interactive Revit's working directory is its own program
/// folder, or whatever folder the shortcut names — never a folder under this add-in's schedule
/// store, which nothing but a registered task is ever pointed at. So a hand-started Revit reaches
/// the first test and stops there, and no second, weaker signal (which task is running, which job
/// is due) is needed or used: those can be wrong, and claiming the wrong job is worse than claiming
/// none.</para>
///
/// <para><b>MEASURED before it was relied on</b> (2026-09-18, Revit 2026.5): Revit started with an
/// explicit working directory still reported that directory from the add-in's <c>OnStartup</c> —
/// <c>launch cwd=…\Scheduled\fire\CWDPROBE…</c>. Nothing in Revit's own start-up changes it first.
/// The <c>launch cwd=</c> breadcrumb in <c>App.OnStartup</c> stays for exactly that reason: if a
/// future Revit ever does change it, that one line is the difference between a diagnosable
/// regression and a schedule that silently stops running.</para>
/// </summary>
internal static class LaunchIdentity
{
    private static bool _resolved;

    /// <summary>
    /// Publishes <c>KHBATCH_JOB</c>/<c>KHBATCH_OWNER</c> for this process when this Revit was
    /// started by a Revit-direct scheduled task, so everything downstream — the unattended-launch
    /// flag, the dialog watchdog, <c>ScheduledRunner.TryGetPendingJob</c>, the per-process claim —
    /// sees exactly what it would have seen from any other launch shape. Nothing downstream knows
    /// this class exists.
    ///
    /// <para>Must run BEFORE <see cref="ScheduleEnvironment.IsUnattendedLaunch"/> is first read,
    /// which is why it is called at the top of <c>App.OnStartup</c>. Never throws, and a Revit it
    /// does not recognise is left exactly as it was: interactive.</para>
    /// </summary>
    public static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;

        try
        {
            // Already announced — the launcher, the cmd.exe action, or an auto-split parent. Both
            // editions load in one process and both call this; whichever gets here second finds the
            // variable set and leaves it alone.
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ScheduleEnvironment.Job)))
                return;

            string jobId = TaskSchedulerGateway.JobIdFromFireDirectory(Environment.CurrentDirectory);
            if (jobId.Length == 0) return;   // an ordinary interactive Revit; say nothing

            ScheduledJob? job = ScheduleStore.GetById(jobId);
            if (job == null)
            {
                // Loud: the task fired, Revit started, and the job it names is gone. Silence here
                // would look exactly like a schedule that never triggered.
                RunTrace.Mark(
                    $"scheduled launch folder names job '{jobId}', which is not saved in this user " +
                    "profile — nothing will run. Delete and re-save the schedule.");
                return;
            }

            Environment.SetEnvironmentVariable(ScheduleEnvironment.Job, job.Id);
            Environment.SetEnvironmentVariable(ScheduleEnvironment.Owner, job.ScheduleOwner ?? "");
            RunTrace.Mark(
                $"scheduled launch identified by folder: '{job.Name}' id={job.Id} " +
                $"owner={job.ScheduleOwner}");
        }
        catch (Exception ex)
        {
            // A Revit that cannot identify its job is still a usable Revit. Reported, never thrown.
            RunTrace.Mark("scheduled launch identity not resolved: " + ex.Message);
        }
    }
}
