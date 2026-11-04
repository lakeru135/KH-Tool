using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace KhBatchRunner.UI;

/// <summary>
/// Local display helpers for every schedule window. Values shown to the user follow the current
/// Windows regional date/time settings (the same settings used by the taskbar), while persisted
/// schedule times remain canonical and culture-independent.
/// </summary>
internal static class ScheduleDisplay
{
    public const string StoredTimePattern = "HH:mm";

    /// <summary>Shown in the "Next run" column when there is no next run to show.</summary>
    public const string NoNextRun = "—";

    /// <summary>Shown in the "Next run" column for a schedule that is switched off.</summary>
    public const string Disabled = "(disabled)";

    /// <summary>
    /// The schedule as one line — "Weekly Tuesday,Friday 23:17".
    ///
    /// <para>Was a private static copy in each of the four Schedule* windows until 1.4.0. Pure
    /// formatting, so it belongs beside the other display helpers those windows already call.</para>
    /// </summary>
    public static string DescribeSchedule(Schedule.ScheduledJob j) => j.Recurrence switch
    {
        Schedule.ScheduleRecurrence.Weekly => $"Weekly {string.Join(",", j.WeeklyDays)} {LocalTime(j.Time)}",
        Schedule.ScheduleRecurrence.Daily => $"Daily {LocalTime(j.Time)}",
        _ => $"Once {LocalTime(j.Time)}"
    };

    /// <summary>
    /// When the job next fires, in the user's regional format — or "(ran once)" / "—".
    ///
    /// <para>The arithmetic is <see cref="Schedule.ScheduleRules.Next(Schedule.ScheduledJob, TimeSpan, DateTime)"/>,
    /// which takes the clock as a parameter and is therefore testable; this wrapper supplies
    /// <see cref="DateTime.Now"/> and turns the answer into text. That split is the whole reason the
    /// rule can now be checked by the harness — four copies of it previously called DateTime.Now
    /// inline, where "the next Tuesday after now" has no fixed answer to assert against.</para>
    /// </summary>
    public static string DescribeNextRun(Schedule.ScheduledJob job)
    {
        // A paused schedule has no next run, and saying otherwise is the kind of quiet
        // contradiction that costs a morning: the row would show tomorrow's date for a task
        // Windows will not fire.
        if (!job.Enabled) return Disabled;
        if (!TryStoredTime(job.Time, out TimeSpan time)) return NoNextRun;

        Schedule.NextRun next = Schedule.ScheduleRules.Next(job, time, DateTime.Now);
        return next.Kind switch
        {
            Schedule.ScheduleRulesKind.At => Local(next.At),
            Schedule.ScheduleRulesKind.RanOnce => "(ran once)",
            _ => NoNextRun
        };
    }

    /// <summary>
    /// The culture Windows itself formats the taskbar clock with: the user's REGIONAL-FORMAT
    /// locale, with their Region-page overrides applied.
    ///
    /// Deliberately not <see cref="CultureInfo.CurrentCulture"/>. Revit sets the thread culture
    /// from its own UI language, so an English Revit on a Thai-formatted PC reports en-US here and
    /// the schedule windows would print a format the user does not see anywhere else on their
    /// machine — which is the whole thing these helpers exist to avoid.
    ///
    /// Resolved once: the locale cannot change without a restart of the process picking it up
    /// anyway, and every grid cell would otherwise pay for the P/Invoke.
    /// </summary>
    public static CultureInfo DisplayCulture { get; } = ResolveDisplayCulture();

    public static string DatePattern => DisplayCulture.DateTimeFormat.ShortDatePattern;
    public static string TimePattern => DisplayCulture.DateTimeFormat.ShortTimePattern;
    public static string DateTimePattern => DatePattern + " " + TimePattern;

    /// <summary>Local date + time in the PC's current Windows display format.</summary>
    public static string Local(System.DateTime dt) =>
        dt.ToString(DateTimePattern, DisplayCulture);

    /// <summary>A stored canonical <c>HH:mm</c> time rendered like the Windows taskbar clock.</summary>
    public static string LocalTime(string? hhmm) =>
        TryStoredTime(hhmm, out System.TimeSpan t)
            ? (System.DateTime.Today + t).ToString(TimePattern, DisplayCulture)
            : (hhmm ?? "");

    /// <summary>
    /// The "Last result" cell for one job: what Windows recorded for its task, in words. An em dash
    /// means Windows has no result for it — usually a job whose task was never registered, which is
    /// itself worth seeing.
    /// </summary>
    public static string LastResult(
        IReadOnlyDictionary<string, int> taskResults, string taskName) =>
        taskResults.TryGetValue(taskName, out int code)
            ? Schedule.TaskSchedulerGateway.DescribeLastResult(code)
            : "—";

    /// <summary>Parses the canonical stored time independently of the PC's display culture.</summary>
    public static bool TryStoredTime(string? hhmm, out System.TimeSpan time) =>
        System.TimeSpan.TryParse(hhmm, CultureInfo.InvariantCulture, out time) &&
        time >= System.TimeSpan.Zero && time < System.TimeSpan.FromDays(1);

    /// <summary>
    /// Reads the user default locale straight from Windows and builds a culture from it WITH user
    /// overrides, so a hand-edited short-date pattern on the Region page is honoured. Any failure
    /// falls back to the thread culture — a slightly wrong format is not a reason to fail a window.
    /// </summary>
    private static CultureInfo ResolveDisplayCulture()
    {
        try
        {
            var buffer = new StringBuilder(LocaleNameMaxLength);
            if (GetUserDefaultLocaleName(buffer, buffer.Capacity) > 0)
            {
                string name = buffer.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    return new CultureInfo(name, useUserOverride: true);
            }
        }
        catch { /* fall through */ }
        return CultureInfo.CurrentCulture;
    }

    /// <summary>LOCALE_NAME_MAX_LENGTH from winnt.h.</summary>
    private const int LocaleNameMaxLength = 85;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultLocaleName(StringBuilder lpLocaleName, int cchLocaleName);
}
