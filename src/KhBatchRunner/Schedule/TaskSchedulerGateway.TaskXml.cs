using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KhBatchRunner.Schedule;

public static partial class TaskSchedulerGateway
{
    /// <summary>
    /// Builds a locale-independent task definition. A StartBoundary with no UTC offset is
    /// intentionally a local wall-clock value: Windows applies the timezone and daylight-saving
    /// rules of the PC registering the task, rather than parsing a culture-specific /SD string.
    /// </summary>
    /// <summary>The launcher-backed task definition — the normal shape.</summary>
    internal static XDocument BuildTaskXml(
        ScheduledJob job, string launcherPath, string revitExePath, DateTime start) =>
        BuildTaskXmlWithAction(job, launcherPath, LauncherArguments(job, revitExePath), start);

    /// <summary>
    /// The same definition with an explicit command and arguments, so the shell fallback
    /// (<see cref="ShellExePath"/> + <see cref="ShellArguments"/>) registers through one builder
    /// rather than a copy of it. Everything except the two <c>Exec</c> values is identical between
    /// the two shapes, and a second copy is where settings quietly diverge.
    /// </summary>
    internal static XDocument BuildTaskXmlWithAction(
        ScheduledJob job, string command, string arguments, DateTime start,
        string? workingDirectory = null)
    {
        string owner = EffectiveOwner(job);
        string userId;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            userId = identity.User?.Value ?? identity.Name;

        XElement trigger = BuildTrigger(job, start);

        return new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(TaskNs + "Task", new XAttribute("version", "1.4"),
                new XElement(TaskNs + "RegistrationInfo",
                    new XElement(TaskNs + "Date", LocalBoundary(DateTime.Now)),
                    new XElement(TaskNs + "Author", userId),
                    new XElement(TaskNs + "Description",
                        $"{Product.Name} scheduled job: {job.Name} ({owner})")),
                new XElement(TaskNs + "Triggers", trigger),
                new XElement(TaskNs + "Principals",
                    new XElement(TaskNs + "Principal", new XAttribute("id", "Author"),
                        new XElement(TaskNs + "UserId", userId),
                        new XElement(TaskNs + "LogonType", "InteractiveToken"),
                        new XElement(TaskNs + "RunLevel", "LeastPrivilege"))),
                new XElement(TaskNs + "Settings",
                    new XElement(TaskNs + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNs + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNs + "StopIfGoingOnBatteries", "false"),
                    // Never let Task Scheduler escalate an End/timeout into forced process
                    // termination. Revit shutdown is requested normally by ScheduledRunner.
                    new XElement(TaskNs + "AllowHardTerminate", "false"),
                    new XElement(TaskNs + "StartWhenAvailable", "true"),
                    new XElement(TaskNs + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(TaskNs + "IdleSettings",
                        new XElement(TaskNs + "StopOnIdleEnd", "false"),
                        new XElement(TaskNs + "RestartOnIdle", "false")),
                    new XElement(TaskNs + "AllowStartOnDemand", "true"),
                    // THE task-level arm/disarm flag, and it must follow the job (1.4.7).
                    // Hardcoding "true" here is not cosmetic: MigrateLegacyRegistrations re-registers
                    // through this builder on every interactive Revit startup, and ScheduleSaver
                    // deletes and re-creates the task on every save -- so a paused schedule would
                    // quietly re-arm itself the next time Revit started or the job was edited.
                    // The two TRIGGER-level <Enabled> elements below stay true: mixing the two makes
                    // the state unreadable in Task Scheduler, which drives only this one.
                    new XElement(TaskNs + "Enabled", job.Enabled ? "true" : "false"),
                    new XElement(TaskNs + "Hidden", "false"),
                    new XElement(TaskNs + "RunOnlyIfIdle", "false"),
                    new XElement(TaskNs + "WakeToRun", "false"),
                    // PT0S means no scheduler-imposed timeout. The liveness supervisor reports and
                    // stops launching work while leaving a questionable Revit open for inspection.
                    new XElement(TaskNs + "ExecutionTimeLimit", "PT0S"),
                    new XElement(TaskNs + "Priority", "7")),
                new XElement(TaskNs + "Actions", new XAttribute("Context", "Author"),
                    new XElement(TaskNs + "Exec",
                        new XElement(TaskNs + "Command", command),
                        // Everything the old generated script baked into its own source is an
                        // argument now, so the launcher binary is identical on every machine and
                        // only the task XML varies per job.
                        // OMITTED rather than left empty when there are none: the Revit-direct
                        // action passes Revit nothing at all, and a stray argument is the kind of
                        // thing Revit tries to open as a file.
                        arguments.Length > 0 ? new XElement(TaskNs + "Arguments", arguments) : null,
                        // Usually just a stable folder. For the Revit-direct action it IS the
                        // message: a scheduled task cannot set an environment variable, but it can
                        // hand its program a working directory, and that is how the run says which
                        // job fired. See LaunchIdentity.
                        new XElement(TaskNs + "WorkingDirectory",
                            workingDirectory ?? ScheduleStore.ScheduledDirectory)))));
    }

    private static void WriteTaskXml(string path, XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Encoding.Unicode,
            Indent = true,
            OmitXmlDeclaration = false
        };
        using XmlWriter writer = XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    private static XElement BuildTrigger(ScheduledJob job, DateTime start)
    {
        if (job.Recurrence == ScheduleRecurrence.Once)
            return new XElement(TaskNs + "TimeTrigger",
                new XElement(TaskNs + "StartBoundary", LocalBoundary(start)),
                new XElement(TaskNs + "Enabled", "true"));

        XElement schedule = job.Recurrence == ScheduleRecurrence.Daily
            ? new XElement(TaskNs + "ScheduleByDay",
                new XElement(TaskNs + "DaysInterval", "1"))
            : new XElement(TaskNs + "ScheduleByWeek",
                new XElement(TaskNs + "WeeksInterval", "1"),
                new XElement(TaskNs + "DaysOfWeek",
                    EffectiveWeeklyDays(job, start.DayOfWeek).Select(d => new XElement(TaskNs + XmlDay(d)))));

        return new XElement(TaskNs + "CalendarTrigger",
            new XElement(TaskNs + "StartBoundary", LocalBoundary(start)),
            new XElement(TaskNs + "Enabled", "true"),
            schedule);
    }

    internal static DateTime TriggerStartLocal(ScheduledJob job, DateTime now)
    {
        TimeSpan time = StoredTime(job.Time);
        if (job.Recurrence == ScheduleRecurrence.Once)
            return (job.StartDate?.Date ?? now.Date) + time;

        if (job.Recurrence == ScheduleRecurrence.Daily)
        {
            DateTime candidate = now.Date + time;
            return candidate > now ? candidate : candidate.AddDays(1);
        }

        HashSet<DayOfWeek> days = new(EffectiveWeeklyDays(job, now.DayOfWeek));
        for (int offset = 0; offset <= 7; offset++)
        {
            DateTime candidate = now.Date.AddDays(offset) + time;
            if (days.Contains(candidate.DayOfWeek) && candidate > now)
                return candidate;
        }
        throw new InvalidOperationException("Could not calculate the next weekly run time.");
    }

    /// <summary>
    /// Recurring migration always targets the next occurrence. For a legacy one-time job whose
    /// JSON predates StartDate, LastRunAt is the closest persisted record of its original date;
    /// retaining that past boundary upgrades the action without scheduling another run.
    /// </summary>
    internal static DateTime MigrationTriggerStartLocal(ScheduledJob job, DateTime now)
    {
        if (job.Recurrence != ScheduleRecurrence.Once)
            return TriggerStartLocal(job, now);

        TimeSpan time = StoredTime(job.Time);
        DateTime date = job.StartDate?.Date ?? job.LastRunAt?.Date ?? now.Date;
        return date + time;
    }

    private static IReadOnlyList<DayOfWeek> EffectiveWeeklyDays(
        ScheduledJob job, DayOfWeek fallbackDay) =>
        job.WeeklyDays.Count > 0
            ? job.WeeklyDays.Distinct().OrderBy(d => d).ToList()
            : new List<DayOfWeek> { fallbackDay };

    internal static string LocalBoundary(DateTime value) =>
        value.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);

    private static TimeSpan StoredTime(string? storedTime)
    {
        if (!TimeSpan.TryParse(storedTime, CultureInfo.InvariantCulture, out TimeSpan time) ||
            time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            throw new FormatException(
                $"The schedule time '{storedTime}' is invalid. Edit the schedule and choose a time again.");
        return new TimeSpan(time.Hours, time.Minutes, 0);
    }
}
