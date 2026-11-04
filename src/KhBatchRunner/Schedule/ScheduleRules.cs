namespace KhBatchRunner.Schedule;

/// <summary>What a scheduled job's next fire time is, decided without a clock, a form or Revit.
/// <see cref="ScheduleRulesKind.RanOnce"/> and <see cref="ScheduleRulesKind.Never"/> are answers in
/// their own right, not failures — a one-shot job that has already run has no next run, and a weekly
/// job with no day ticked can never fire.</summary>
public enum ScheduleRulesKind
{
    /// <summary>There is a next run, at <see cref="NextRun.At"/>.</summary>
    At,
    /// <summary>A "Once" job that has already run.</summary>
    RanOnce,
    /// <summary>No next run: an unreadable time, or a weekly job with no day selected.</summary>
    Never,
}

/// <summary>The answer from <see cref="ScheduleRules.NextRun"/>.</summary>
public readonly record struct NextRun(ScheduleRulesKind Kind, DateTime At)
{
    public static NextRun Never => new(ScheduleRulesKind.Never, default);
    public static NextRun RanOnce => new(ScheduleRulesKind.RanOnce, default);
    public static NextRun On(DateTime at) => new(ScheduleRulesKind.At, at);
}

/// <summary>
/// The schedule arithmetic the four Schedule* windows each carried their own copy of.
///
/// <para><b>Why it moved (1.4.0).</b> <c>DescribeNextRun</c> existed four times — in
/// ScheduleRunWindow and ScheduleInteropWindow (and, before 2.0.0, two more) — with no test
/// over any copy. The four had drifted only in comments and brace style when they were merged, so
/// nothing was silently different this time; the point is that nothing would have told us if it
/// had been. Its sibling <c>RefreshGrid</c> in the same four files HAD drifted, and shipped two
/// windows that lose your sort and your selected row.</para>
///
/// <para><b>Why the clock is a parameter.</b> The old copies each called <c>DateTime.Now</c>
/// directly, which is precisely why none of them could be tested: "the next Tuesday after now" has
/// no fixed answer. Passing <c>now</c> in makes every branch checkable — and the branches are
/// fiddly enough to deserve it (a Once job before and after its time, a Daily job either side of
/// the hour, a Weekly job searching forward across a month boundary).</para>
///
/// <para>Revit-free and UI-free on purpose, the same reason <c>Batch\ReopenLadder</c> and
/// <c>Batch\WorksetVisibilityRule</c> are: the harness can then own the rule the product runs.</para>
/// </summary>
public static class ScheduleRules
{
    /// <summary>
    /// When this job next fires, given the current time.
    /// </summary>
    /// <param name="recurrence">Once, Daily or Weekly.</param>
    /// <param name="weeklyDays">Days ticked for a Weekly job; ignored otherwise.</param>
    /// <param name="startDate">The fixed calendar date of a Once job. Null falls back to today,
    /// which is what a job saved before start dates existed looks like.</param>
    /// <param name="hasRun">Whether the job has ever run — the only thing that retires a Once job.</param>
    /// <param name="time">Time of day, already parsed from the job's stored "HH:mm".</param>
    /// <param name="now">The current local time.</param>
    public static NextRun Next(
        ScheduleRecurrence recurrence,
        IReadOnlyList<DayOfWeek> weeklyDays,
        DateTime? startDate,
        bool hasRun,
        TimeSpan time,
        DateTime now)
    {
        switch (recurrence)
        {
            case ScheduleRecurrence.Once:
                // A Once job is retired by having run, NOT by its date passing: a job whose time
                // slipped by while the PC was asleep still shows when it was due, which is what
                // someone looking at the list needs to see.
                if (hasRun) return NextRun.RanOnce;
                return NextRun.On((startDate?.Date ?? now.Date) + time);

            case ScheduleRecurrence.Daily:
                DateTime todayAt = now.Date + time;
                return NextRun.On(todayAt > now ? todayAt : todayAt.AddDays(1));

            case ScheduleRecurrence.Weekly:
                if (weeklyDays == null || weeklyDays.Count == 0) return NextRun.Never;
                // Eight days, not seven: today's slot may already have passed, so the same weekday
                // a week out has to stay reachable.
                for (int i = 0; i < 8; i++)
                {
                    DateTime candidate = now.Date.AddDays(i) + time;
                    if (candidate <= now) continue;
                    if (weeklyDays.Contains(candidate.DayOfWeek)) return NextRun.On(candidate);
                }
                return NextRun.Never;

            default:
                return NextRun.Never;
        }
    }

    /// <inheritdoc cref="Next(ScheduleRecurrence, IReadOnlyList{DayOfWeek}, DateTime?, bool, TimeSpan, DateTime)"/>
    public static NextRun Next(ScheduledJob job, TimeSpan time, DateTime now) =>
        Next(job.Recurrence, job.WeeklyDays, job.StartDate, job.LastRunAt.HasValue, time, now);

    /// <summary>
    /// Identity of an ad-hoc model for de-duplication: a local model is its path (case-insensitive,
    /// because Windows is), a cloud model is its GUID. Two entries with the same key are the same
    /// model however they were added.
    /// </summary>
    public static string AdHocKey(Presets.CloudModel m) =>
        m.IsLocal ? "local:" + (m.LocalPath ?? "").ToLowerInvariant() : "cloud:" + m.ModelGuid;

    /// <summary>The day a weekly-days checkbox stands for, by its label. Lives here rather than
    /// beside the checkboxes so the mapping is one thing rather than four.</summary>
    public static DayOfWeek DayForLabel(string label) => label switch
    {
        "Mon" => DayOfWeek.Monday,
        "Tue" => DayOfWeek.Tuesday,
        "Wed" => DayOfWeek.Wednesday,
        "Thu" => DayOfWeek.Thursday,
        "Fri" => DayOfWeek.Friday,
        "Sat" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };
}
