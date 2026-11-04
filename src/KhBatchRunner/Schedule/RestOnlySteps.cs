namespace KhBatchRunner.Schedule;

/// <summary>
/// What one REST-only step did, in the form its callers need: the log notes it produced, whether it
/// delivered, and one sentence a person can read.
///
/// <para><b>The summary is the last note, deliberately.</b> Both steps are written so that every
/// path out of their <c>RunSet</c> adds exactly one closing note describing the outcome — the
/// download summary, the "already downloaded" line, the "no successful run" sentence, the error.
/// Re-deriving that sentence here from the result objects would be a second opinion about the same
/// run, and the two would drift; the step that did the work is the one that should say what
/// happened.</para>
/// </summary>
public sealed class RestOnlyStepOutcome
{
    public string StepName { get; init; } = "";

    /// <summary>Every line for the run's own log, in order.</summary>
    public List<string> Notes { get; init; } = new();

    /// <summary>True when the step delivered what the job asked of it. False for a misconfigured
    /// step, a failure, and for "there was nothing to fetch" — from the schedule's point of view
    /// those all mean the files you expected are not there.</summary>
    public bool Ok { get; init; }

    public string TrackingLog { get; init; } = "";

    public string Summary =>
        Notes.Count > 0 ? Notes[Notes.Count - 1] : StepName + ": nothing was reported.";

    internal static RestOnlyStepOutcome Refused(string stepName, string message, string trackingLog) =>
        new()
        {
            StepName = stepName,
            Notes = new List<string> { message },
            Ok = false,
            TrackingLog = trackingLog
        };
}

/// <summary>Every REST-only step of one job, aggregated for the caller that has to report the run.</summary>
public sealed class RestOnlyOutcome
{
    public List<string> Notes { get; } = new();

    /// <summary>One sentence per step — the body of the run's notification, and of the "Run now"
    /// dialog.</summary>
    public List<string> Summaries { get; } = new();

    public int StepsOk { get; private set; }
    public int StepsFailed { get; private set; }

    /// <summary>The run's tracking log, so a notification can link the file the rows went into.</summary>
    public string TrackingLog { get; private set; } = "";

    public bool Failed => StepsFailed > 0;

    /// <summary>The notification/dialog body. Blank only when the job carried no REST-only step at
    /// all, which its callers already test for.</summary>
    public string Body => string.Join("  ", Summaries);

    internal void Add(RestOnlyStepOutcome step)
    {
        Notes.AddRange(step.Notes);
        Summaries.Add(step.Summary);
        if (step.Ok) StepsOk++; else StepsFailed++;
        if (TrackingLog.Length == 0) TrackingLog = step.TrackingLog;
    }
}

/// <summary>
/// The run items that open no Revit model — <see cref="FileLogStep"/> and
/// <see cref="DataExtractStep"/> — asked about together.
///
/// <para><b>Why this exists.</b> A REST-only step is wired into the runner at five places (the
/// file-log-only short-circuit, the split parent, the interactive "Run now", the headless action
/// path and the modal script path), plus the pipeline exclusion in <c>BuildJobPasses</c>, the strip
/// in <see cref="ChunkLauncher"/>, and the model-source waiver in the schedule editor. When there
/// was one such step, each site named it directly. With two, every one of those sites would have to
/// remember both — and a site that remembered only the first would produce a job the editor accepts
/// and the runner silently does nothing for. That is the failure this class removes: the sites ask
/// "is this job REST-only?" and "run whatever REST-only steps it has", and a third such step is
/// added here rather than at eight call sites.</para>
///
/// <para>Order is fixed and deliberate: the file log first, then the data extract. Both are
/// independent, but a run's log reads in the order its rows were written, and a stable order means
/// two runs of the same job can be diffed.</para>
/// </summary>
public static class RestOnlySteps
{
    /// <summary>True when this item is one of the REST-only steps.</summary>
    public static bool IsRestOnlyItem(RunItem item) =>
        FileLogStep.IsFileLogItem(item) || DataExtractStep.IsDataExtractItem(item);

    /// <summary>True when the job carries at least one REST-only step.</summary>
    public static bool IsRequested(ScheduledJob job) => job.RunItems.Any(IsRestOnlyItem);

    /// <summary>True when EVERY run item is REST-only, so the job opens no model at all.</summary>
    public static bool IsOnly(ScheduledJob job) => IsOnly(job.RunItems);

    /// <summary>
    /// The same test against a run-item list that has not been built into a job yet — what the
    /// schedule editor asks before <c>TryBuildJob</c> exists to be asked, so a REST-only job is not
    /// forced to name a model set it will never open.
    /// </summary>
    public static bool IsOnly(IReadOnlyList<RunItem> runItems) =>
        runItems.Count > 0 && runItems.All(IsRestOnlyItem);

    /// <summary>
    /// Runs every REST-only step the job carries, in order, and returns their notes for the
    /// caller's own log. Never throws: each step already swallows its own failures, because one of
    /// these steps failing must not take down a job that also opens models.
    /// </summary>
    public static List<string> Run(ScheduledJob job, string destFolder, string projectLog, DateTime runStamp) =>
        RunWithOutcome(job, destFolder, projectLog, runStamp).Notes;

    /// <summary>
    /// The same run, keeping what the notes alone cannot carry: whether each step delivered, and a
    /// sentence per step for whoever has to report the run.
    ///
    /// <para><b>Why this overload exists.</b> A job made only of REST-only steps finished in total
    /// silence until 1.5.1.3 — no notification, nothing on screen — because the runner's short-circuit
    /// for it wrote its log rows and quit, and only the model path ever called
    /// <c>RunNotification</c>. Measured on a real 06:00 data-extract schedule: it ran, it downloaded,
    /// and the only evidence was a CSV nobody was told about. The notes were never enough to report
    /// from, because "did this work" is not recoverable from prose; hence the outcome object rather
    /// than a caller parsing lines. Most callers still want just the notes and keep using
    /// <see cref="Run"/>.</para>
    /// </summary>
    public static RestOnlyOutcome RunWithOutcome(
        ScheduledJob job, string destFolder, string projectLog, DateTime runStamp)
    {
        var outcome = new RestOnlyOutcome();
        if (FileLogStep.IsRequested(job))
            outcome.Add(FileLogStep.RunStep(job, destFolder, projectLog, runStamp));
        if (DataExtractStep.IsRequested(job))
            outcome.Add(DataExtractStep.RunStep(job, destFolder, projectLog, runStamp));
        return outcome;
    }
}
