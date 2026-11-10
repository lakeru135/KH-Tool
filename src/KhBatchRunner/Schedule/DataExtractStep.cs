using System.Linq;
using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Logging;
using KhBatchRunner.Reporting;

namespace KhBatchRunner.Schedule;

/// <summary>What one downloaded file of a Get Data Extract run produced.</summary>
public sealed class DataExtractFileOutcome
{
    public required string FileName { get; init; }
    public long Bytes { get; init; }
    public string Path { get; init; } = "";
    /// <summary>Null when the file downloaded; the first line of the failure otherwise.</summary>
    public string? Error { get; init; }
    public bool Ok => Error is null;
}

/// <summary>The whole run: one outcome per file, plus notes for a caller that logs its own.</summary>
public sealed class DataExtractResult
{
    public List<DataExtractFileOutcome> Files { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>The Forma job the files came from, for the caller's status line. Empty when the run
    /// never got as far as choosing one.</summary>
    public string JobId { get; set; } = "";

    /// <summary>Where the files landed, or the folder an earlier run had already filled.</summary>
    public string RunFolder { get; set; } = "";

    /// <summary>True when the newest successful job was already on disk and nothing was fetched.
    /// Not a failure — the normal answer when a daily schedule polls a weekly extract.</summary>
    public bool SkippedAlreadyDownloaded { get; set; }

    public int Downloaded => Files.Count(f => f.Ok);
    public int FailedFiles => Files.Count(f => !f.Ok);
    public long TotalBytes => Files.Where(f => f.Ok).Sum(f => f.Bytes);
}

/// <summary>
/// The Get Data Extract run — shared by the interactive dialog and the schedulable step, which is
/// the point: a run started by hand and the same run at 06:00 go through one body of code, so
/// neither can quietly grow behaviour the other lacks.
///
/// <para>It opens NO Revit model — a pure Data Connector REST pull — so the scheduled runner
/// handles it outside the per-model <see cref="Batch.BatchEngine"/> pipeline, exactly as it does
/// <see cref="FileLogStep"/>. The Forma session token works headless, so it runs unattended.</para>
///
/// <para><b>It collects; it never extracts.</b> Forma's own schedule produces the data overnight and
/// this step downloads the newest run that finished successfully. Creating a request from here
/// would put a second schedule on the account competing with the one the user already set up in
/// Forma, and would need a write scope this feature otherwise does not.</para>
///
/// <para><b>A job that is still running is not a job to download.</b> The step reports what it
/// found and writes nothing rather than falling back to the previous night's extract under
/// today's date — a stale file that looks fresh is the one outcome no reader can detect.</para>
/// </summary>
public static class DataExtractStep
{
    /// <summary>The run-item name that marks a job as including the Get Data Extract step (an
    /// <see cref="RunItemKind.Action"/> item, like <see cref="FileLogStep"/>).
    ///
    /// <para>ONE download for the whole step, however many files it pulls.
    /// <see cref="ChunkLauncher"/> strips the step from an auto-split run's child jobs by this
    /// exact name so the download happens once rather than once per group.</para></summary>
    public const string DisplayName = Actions.ActionNames.GetDataExtract;

    /// <summary>True when the job's only run item is the Get Data Extract step (no model is ever opened).</summary>
    public static bool IsOnly(ScheduledJob job) => IsOnly(job.RunItems);

    /// <summary>
    /// The same test against a run-item list that has not been built into a job yet — the schedule
    /// editor needs the answer before <c>TryBuildJob</c> exists to be asked, because a REST-only job
    /// opens no model and must not be forced to name one. Shared with the runner rather than
    /// restated, for the reason <see cref="FileLogStep.IsOnly(IReadOnlyList{RunItem})"/> gives.
    /// </summary>
    public static bool IsOnly(IReadOnlyList<RunItem> runItems) =>
        runItems.Count > 0 && runItems.All(IsDataExtractItem);

    /// <summary>True when the job includes a Get Data Extract step (possibly alongside model actions/scripts).</summary>
    public static bool IsRequested(ScheduledJob job) => job.RunItems.Any(IsDataExtractItem);

    /// <summary>The one place a run item is recognised as this REST-only step.</summary>
    public static bool IsDataExtractItem(RunItem item) =>
        item.Name == DisplayName && item.Kind == RunItemKind.Action;

    /// <summary>
    /// Runs the extract download for <paramref name="job"/>. Never throws — a download failure must
    /// not take down a job that also opens models — and writes its own rows into the RUN's tracking
    /// log so the outcome is visible beside the run's model rows.
    /// </summary>
    /// <param name="runStamp">The run's shared stamp. Names the output folder and picks the tracking
    /// log, so the extract lands in the same file as everything else this run did.</param>
    public static List<string> Run(ScheduledJob job, string destFolder, string projectLog, DateTime runStamp) =>
        RunStep(job, destFolder, projectLog, runStamp).Notes;

    /// <summary>
    /// The same run, reported rather than only logged: the notes plus whether the step delivered and
    /// one sentence about it. <see cref="RestOnlySteps.RunWithOutcome"/> is the only caller, and it
    /// exists because a job made only of REST-only steps used to finish in complete silence.
    /// </summary>
    internal static RestOnlyStepOutcome RunStep(
        ScheduledJob job, string destFolder, string projectLog, DateTime runStamp)
    {
        string trackingLog = ChunkLauncher.TrackingLogPathFor(job, runStamp);

        DataExtractInputSet? set = job.DataExtractInputs;
        if (set is null || !set.HasTarget)
        {
            // Loud, not silent — the same rule FileLogStep applies: a job that asks for the step and
            // names no set is misconfigured, and a run that quietly produces nothing teaches the
            // user to distrust the whole schedule.
            const string message =
                "Get Data Extract: this job has no Data Extract set — open Schedule Run, edit the " +
                "job and choose one under \"Get Data Extract set\". Nothing was downloaded.";
            RunNote.Write(trackingLog, DisplayName, "", "ERROR", message);
            return RestOnlyStepOutcome.Refused(DisplayName, message, trackingLog);
        }

        DataExtractResult result = RunSet(set, destFolder, projectLog, runStamp, trackingLog);

        // Same note, and for the same reason, as FileLogStep: every caller hoists this step to the
        // front of the run, so a job that lists it third produces a log whose first rows are the
        // download. Said once here rather than at each hoist site.
        if (!IsOnly(job))
        {
            int position = job.RunItems.ToList().FindIndex(IsDataExtractItem);
            if (position > 0)
                result.Notes.Insert(0,
                    $"{DisplayName} ran first, ahead of its position ({position + 1}) in this job's " +
                    "list: it opens no model, so the run pulls it before any model is opened.");
        }

        // "Nothing was downloaded" counts as NOT delivered, even where the step logged it as WARN
        // rather than ERROR: an extract Forma has not run yet is not this add-in's fault, but from the
        // schedule's point of view the files you expected are still missing, and that is the message
        // that has to survive until somebody reads it. An already-downloaded skip is the opposite —
        // the newest extract IS on disk, which is the answer a daily poll of a weekly extract should
        // give every other day of the week.
        return new RestOnlyStepOutcome
        {
            StepName = DisplayName,
            Notes = result.Notes,
            Ok = result.FailedFiles == 0 && (result.Downloaded > 0 || result.SkippedAlreadyDownloaded),
            TrackingLog = trackingLog
        };
    }

    /// <summary>
    /// Finds the newest successful run of the set's extract and downloads its files.
    ///
    /// <para>Shared with the interactive dialog, which passes its own destination and tracking-log
    /// path.</para>
    /// </summary>
    /// <param name="trackingLog">Where per-file OK/ERROR rows go. Pass "" to write none.</param>
    /// <param name="progress">Called with a human-readable line as each file starts, for a status
    /// bar. Optional.</param>
    public static DataExtractResult RunSet(DataExtractInputSet set, string destFolder, string projectLog,
        DateTime runStamp, string trackingLog, Action<string>? progress = null)
    {
        var result = new DataExtractResult();
        string logProject = string.IsNullOrWhiteSpace(projectLog) ? DisplayName : projectLog;
        string label = set.TargetLabel;

        DataConnectorClient client;
        try { client = new DataConnectorClient(new SessionTokenProvider()); }
        catch (Exception ex)
        {
            string message = "Get Data Extract: could not sign in to Forma (" + First(ex) + ").";
            RunNote.Write(trackingLog, DisplayName, "", "ERROR", message);
            result.Notes.Add(message);
            return result;
        }

        // 1. Which run of the extract to take.
        DataConnectorJob? latest, job;
        try
        {
            progress?.Invoke("Looking for the latest finished extract…");
            (latest, job) = client.GetLatestJobs(set.HubId, set.RequestId);
        }
        catch (Exception ex)
        {
            string error = First(ex);
            string message = $"Get Data Extract: could not read the extract's runs ({error}).";
            RunNote.Write(trackingLog, DisplayName, label, "ERROR", message);
            result.Notes.Add(message);
            return result;
        }

        if (job is null)
        {
            // A request with no successful run is not an error in this add-in — it is news about
            // Forma, and the two cases read very differently to whoever checks the log in the
            // morning, so they are reported as different sentences.
            string message = latest is null
                ? $"Get Data Extract: '{label}' has no runs yet — nothing to download."
                : $"Get Data Extract: '{label}' has no successful run to download; its latest run " +
                  $"({DataExtractPaths.ShortJobId(latest.Id)}) is {latest.StateLabel}.";
            RunNote.Write(trackingLog, DisplayName, label, "WARN", message);
            result.Notes.Add(message);
            return result;
        }

        result.JobId = job.Id;
        string when = job.CompletedAt is { } done ? done.ToString("yyyy-MM-dd HH:mm") : "unknown time";

        // 2. Has this exact run already been collected here?
        if (set.SkipIfAlreadyDownloaded)
        {
            string? existing = DataExtractPaths.ExistingRunFolder(destFolder, DisplayName, job.Id);
            if (existing != null)
            {
                result.SkippedAlreadyDownloaded = true;
                result.RunFolder = existing;
                string message =
                    $"Get Data Extract: the latest successful extract ({DataExtractPaths.ShortJobId(job.Id)}, " +
                    $"finished {when}) is already in {existing} — nothing downloaded.";
                RunNote.Write(trackingLog, DisplayName, label, "OK", message);
                result.Notes.Add(message);
                return result;
            }
        }

        // 3. What the run produced.
        List<DataConnectorFile> files;
        try
        {
            progress?.Invoke("Reading the extract's file list…");
            files = client.GetJobFiles(set.HubId, job.Id);
        }
        catch (Exception ex)
        {
            string error = First(ex);
            string message = $"Get Data Extract: could not list the extract's files ({error}).";
            RunNote.Write(trackingLog, DisplayName, label, "ERROR", message);
            result.Notes.Add(message);
            return result;
        }

        // The README is HTML documentation of the extract's columns, regenerated identically every
        // run; the ZIP is the same CSVs again. Taking either by default would double the download
        // for no reader's benefit, so the set chooses one shape and gets only that.
        List<DataConnectorFile> wanted = set.DownloadZipInstead
            ? files.Where(f => f.IsZip).ToList()
            : files.Where(f => f.IsCsv).ToList();

        if (wanted.Count == 0)
        {
            string message =
                $"Get Data Extract: the latest successful extract ({DataExtractPaths.ShortJobId(job.Id)}) " +
                $"contains no {(set.DownloadZipInstead ? "ZIP" : "CSV")} files " +
                $"({files.Count} file(s) listed). Nothing downloaded.";
            RunNote.Write(trackingLog, DisplayName, label, "WARN", message);
            result.Notes.Add(message);
            return result;
        }

        // 4. Download.
        string runFolder = DataExtractPaths.RunFolder(destFolder, logProject, DisplayName, runStamp, job.Id);
        result.RunFolder = runFolder;

        for (int i = 0; i < wanted.Count; i++)
        {
            DataConnectorFile file = wanted[i];
            progress?.Invoke($"Downloading {file.Name} ({i + 1} of {wanted.Count})…");
            string path = DataExtractPaths.FilePath(runFolder, file.Name);
            try
            {
                long bytes = client.DownloadFile(set.HubId, job.Id, file.Name, path);
                result.Files.Add(new DataExtractFileOutcome
                {
                    FileName = file.Name,
                    Bytes = bytes,
                    Path = path,
                });
            }
            catch (Exception ex)
            {
                string error = First(ex);
                result.Files.Add(new DataExtractFileOutcome { FileName = file.Name, Error = error });
                // Per-file, like the file-log crawl: losing one CSV must cost that CSV, not the
                // other thirty-nine — and the row names which one is missing, because the folder
                // itself will look complete.
                RunNote.Write(trackingLog, DisplayName, file.Name, "ERROR",
                    $"could not download this file ({error})");
            }
        }

        string summary =
            $"{result.Downloaded} file(s), {Megabytes(result.TotalBytes)} from extract " +
            $"{DataExtractPaths.ShortJobId(job.Id)} (finished {when}) → {runFolder}" +
            (result.FailedFiles > 0 ? $", {result.FailedFiles} file(s) FAILED" : "");
        RunNote.Write(trackingLog, DisplayName, label,
            result.FailedFiles > 0 ? "ERROR" : "OK", summary);
        result.Notes.Add("Get Data Extract: " + summary);
        return result;
    }

    /// <summary>Sizes are for a human scanning a log row, so one decimal place of megabytes — and
    /// kilobytes below the point where that reads as "0.0".</summary>
    private static string Megabytes(long bytes) =>
        bytes >= 1024L * 1024
            ? (bytes / 1024d / 1024d).ToString("0.0") + " MB"
            : Math.Max(1, bytes / 1024) + " KB";

    private static string First(Exception ex) => ex.Message.Split('\n')[0];
}
