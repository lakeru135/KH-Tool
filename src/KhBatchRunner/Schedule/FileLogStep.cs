using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Logging;
using KhBatchRunner.Reporting;

namespace KhBatchRunner.Schedule;

/// <summary>What one folder of a Get File Log run produced.</summary>
public sealed class FileLogFolderOutcome
{
    public required string FolderName { get; init; }
    public required string FolderPath { get; init; }
    public int Rows { get; init; }
    public string ReportPath { get; init; } = "";
    /// <summary>Files whose own history could not be read, after retries. The folder still produced
    /// a report; these rows are missing from it.</summary>
    public int SkippedFiles { get; init; }
    /// <summary>Null when the folder was read; the first line of the failure otherwise.</summary>
    public string? Error { get; init; }
    public bool Ok => Error is null;
}

/// <summary>The whole run: one outcome per folder, plus notes for a caller that logs its own.</summary>
public sealed class FileLogResult
{
    public List<FileLogFolderOutcome> Folders { get; } = new();
    public List<string> Notes { get; } = new();
    public int TotalRows => Folders.Sum(f => f.Rows);
    public int FailedFolders => Folders.Count(f => !f.Ok);
    /// <summary>Files whose history could not be read across the whole run — rows missing from
    /// reports that otherwise look complete.</summary>
    public int SkippedFiles => Folders.Sum(f => f.SkippedFiles);
    public int ReportsWritten => Folders.Count(f => f.ReportPath.Length > 0);
}

/// <summary>
/// The Get File Log run — shared by the interactive dialog and the schedulable step, which is the
/// point: a run started by hand and the same run at 00:01 go through one body of code, so neither
/// can quietly grow behaviour the other lacks.
///
/// <para>It opens NO Revit model — a pure Data Management REST pull — so the scheduled runner
/// handles it outside the per-model <see cref="Batch.BatchEngine"/> pipeline. The Forma session token
/// works headless (as it does for Internal Relink), so it runs unattended.</para>
///
/// <para><b>One report per folder, since 1.4.5.</b> A run reads every folder of a
/// <see cref="FileLogInputSet"/> and writes each one its own CSV, because merging several folders
/// into one file threw away the thing people wanted to compare. Dedupe by version URN therefore
/// happens WITHIN a folder, not across them: a file that genuinely sits in two folders belongs in
/// both reports.</para>
/// </summary>
public static class FileLogStep
{
    /// <summary>The run-item name that marks a job as including the Get File Log step (an
    /// <see cref="RunItemKind.Action"/> item, like the synthetic Initiate/Cut publish passes).
    ///
    /// <para>ONE token for the whole step, however many folders it reads.
    /// <see cref="ChunkLauncher"/> strips the step from an auto-split run's child jobs by this
    /// exact name so the pull happens once rather than once per group; a per-folder run item would
    /// slip through that filter and have every group re-read every folder.</para></summary>
    public const string DisplayName = Actions.ActionNames.GetFileLog;

    /// <summary>True when the job's only run item is the Get File Log step (no model is ever opened).</summary>
    public static bool IsOnly(ScheduledJob job) => IsOnly(job.RunItems);

    /// <summary>
    /// The same test against a run-item list that has not been built into a job yet.
    ///
    /// <para>The schedule EDITOR needs this answer before <c>TryBuildJob</c> exists to be asked:
    /// a REST-only job opens no model, so it must not be forced to name one. Sharing the predicate
    /// rather than re-writing the condition in the window is the point — an editor that disagreed
    /// with the runner about what "opens no model" means would either refuse a job that runs fine
    /// or accept one that resolves nothing.</para>
    /// </summary>
    public static bool IsOnly(IReadOnlyList<RunItem> runItems) =>
        runItems.Count > 0 && runItems.All(IsFileLogItem);

    /// <summary>True when the job includes a Get File Log step (possibly alongside model actions/scripts).</summary>
    public static bool IsRequested(ScheduledJob job) => job.RunItems.Any(IsFileLogItem);

    /// <summary>The one place a run item is recognised as the REST-only step.</summary>
    public static bool IsFileLogItem(RunItem item) =>
        item.Name == DisplayName && item.Kind == RunItemKind.Action;

    /// <summary>
    /// Runs the file-log pull for <paramref name="job"/>. Never throws — a file-log failure must not
    /// take down a job that also opens models — and writes its own rows into the RUN's tracking log,
    /// one per folder, so the outcome is visible beside the run's model rows.
    /// </summary>
    /// <param name="runStamp">The run's shared stamp. Names the reports and picks the tracking log,
    /// so the file log lands in the same file as everything else this run did.</param>
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

        FileLogInputSet? set = job.FileLogInputs;
        if (set is null || !set.HasFolders)
        {
            // Loud, not silent. Before 1.4.5 the folders came from whatever folder-backed model sets
            // the job happened to carry, so a job could request a file log and produce nothing with
            // no explanation. A job that asks for the step and names no set is misconfigured.
            const string message =
                "Get File Log: this job has no File Log set — open Schedule Run, edit the job and " +
                "choose one under \"Get File Log set\". Nothing was pulled.";
            RunNote.Write(trackingLog, DisplayName, "", "ERROR", message);
            return RestOnlyStepOutcome.Refused(DisplayName, message, trackingLog);
        }

        FileLogResult result = RunSet(set, destFolder, projectLog, runStamp, trackingLog);

        // Say so when the run order does not match the saved order. All four callers hoist this
        // step to the front of the run whatever position it holds in RunItems, so a job that lists
        // it third produces a log whose first rows are the file log — which reads like the run
        // executed the wrong order. The note goes HERE, at the one place every caller passes
        // through, rather than at each of the four hoist sites: per-entry-point wiring is exactly
        // how a message ends up missing from the one path that needed it.
        if (!IsOnly(job))
        {
            int position = job.RunItems.ToList().FindIndex(IsFileLogItem);
            if (position > 0)
                result.Notes.Insert(0,
                    $"{DisplayName} ran first, ahead of its position ({position + 1}) in this job's " +
                    "list: it opens no model, so the run pulls it before any model is opened.");
        }

        return new RestOnlyStepOutcome
        {
            StepName = DisplayName,
            Notes = result.Notes,
            Ok = result.FailedFolders == 0,
            TrackingLog = trackingLog
        };
    }

    /// <summary>
    /// Reads every folder of <paramref name="set"/> and writes one report per folder.
    ///
    /// <para>Shared with the interactive dialog, which passes its own destination and a tracking-log
    /// path of its own — the interactive run gets a log for the first time in 1.4.5, from exactly
    /// this code.</para>
    /// </summary>
    /// <param name="trackingLog">Where per-folder OK/ERROR rows go. Pass "" to write none.</param>
    /// <param name="progress">Called with a human-readable line as each folder starts, for a status
    /// bar. Optional.</param>
    public static FileLogResult RunSet(FileLogInputSet set, string destFolder, string projectLog,
        DateTime runStamp, string trackingLog, Action<string>? progress = null)
    {
        var result = new FileLogResult();
        string logProject = string.IsNullOrWhiteSpace(projectLog) ? DisplayName : projectLog;

        DataManagementClient client;
        try { client = new DataManagementClient(new SessionTokenProvider()); }
        catch (Exception ex)
        {
            string message = "Get File Log: could not sign in to Forma (" + First(ex) + ").";
            RunNote.Write(trackingLog, DisplayName, "", "ERROR", message);
            result.Notes.Add(message);
            return result;
        }

        // Folder names are not unique — 2.1.1_Drawings exists under every project — so each report
        // is named for its Forma project as well as its folder. Ordinals are assigned up front, in
        // run order, and keyed on that combined label: a bare-name key would still number two
        // folders whose filenames now differ because their projects do.
        List<string> labels = set.Folders
            .Select(f => FileLogReportPaths.FolderLabel(f.FormaProjectName, f.Name))
            .ToList();
        List<int> ordinals = FileLogReportPaths.Ordinals(labels);

        for (int i = 0; i < set.Folders.Count; i++)
        {
            FolderRef folder = set.Folders[i];
            string label = string.IsNullOrEmpty(folder.DisplayPath) ? folder.Name : folder.DisplayPath;
            progress?.Invoke($"Reading {label}…");

            try
            {
                var rows = new List<FormaFileLogRow>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var skipped = new List<string>();
                foreach (FormaFileLogRow r in client.GetFileLog(
                             folder.ToEntry(), set.IncludeSubfolders, set.Spec,
                             n => progress?.Invoke($"Reading {label}… {n} version(s)"),
                             w => skipped.Add(w)))
                {
                    // Within this folder only: recursing into subfolders can meet the same version
                    // twice, but the same file in ANOTHER folder of the set gets its own report row.
                    string key = r.VersionUrn.Length > 0
                        ? r.VersionUrn
                        : r.FileName + "|" + r.VersionNumber;
                    if (seen.Add(key)) rows.Add(r);
                }

                string reportPath = "";
                if (rows.Count > 0)
                {
                    (IReadOnlyList<string> columns, List<IReadOnlyDictionary<string, string>> mapped) =
                        UI.FormaExtractReport.FileLog(rows);
                    reportPath = FileLogReportPaths.OutputPath(
                        destFolder, logProject, DisplayName, runStamp, labels[i], ordinals[i]);
                    CsvReportWriter.WriteTo(reportPath, columns, mapped);
                }

                result.Folders.Add(new FileLogFolderOutcome
                {
                    FolderName = folder.Name,
                    FolderPath = label,
                    Rows = rows.Count,
                    ReportPath = reportPath,
                    SkippedFiles = skipped.Count,
                });

                string note = rows.Count > 0
                    ? $"{rows.Count} version(s) → {reportPath}"
                    : $"no {set.Spec.ScopeDescription()} found — no report written";
                if (skipped.Count > 0)
                {
                    // Named, not just counted: a report missing rows is worse than a report that
                    // failed, because nothing about the file itself says anything is absent.
                    note += $" — {skipped.Count} file(s) SKIPPED after retries: " +
                            string.Join("; ", skipped.Take(5)) +
                            (skipped.Count > 5 ? $"; (+{skipped.Count - 5} more)" : "");
                }
                RunNote.Write(trackingLog, DisplayName, label,
                    skipped.Count > 0 ? "WARN" : "OK", note);
                result.Notes.Add($"Get File Log: {label}: {note}");
            }
            catch (Exception ex)
            {
                string error = First(ex);
                result.Folders.Add(new FileLogFolderOutcome
                {
                    FolderName = folder.Name,
                    FolderPath = label,
                    Error = error,
                });

                // Only the folder LISTING can still fail this way — a single file's history no
                // longer takes the folder down with it. When it is the timeout, say what actually
                // fixes it, because the message alone reads like a folder-size limit and there is
                // no such thing: it is one request that did not answer.
                string advice = "";
                if (error.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    error.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    advice = " — this folder is large; tick \"Latest version only\" on the set, " +
                             "which reads no per-file history at all, or split it into subfolders";
                }

                RunNote.Write(trackingLog, DisplayName, label, "ERROR",
                    $"could not read this folder ({error}){advice}");
                result.Notes.Add($"Get File Log: could not read folder '{label}' ({error}).");
            }
        }

        string summary =
            $"{result.ReportsWritten} report(s), {result.TotalRows} version(s) from " +
            $"{set.Folders.Count} folder(s)" +
            (result.FailedFolders > 0 ? $", {result.FailedFolders} folder(s) FAILED" : "") +
            (result.SkippedFiles > 0 ? $", {result.SkippedFiles} file(s) skipped" : "");
        RunNote.Write(trackingLog, DisplayName, "",
            result.FailedFolders > 0 ? "ERROR" : result.SkippedFiles > 0 ? "WARN" : "OK", summary);
        result.Notes.Add("Get File Log: " + summary);
        return result;
    }

    private static string First(Exception ex) => ex.Message.Split('\n')[0];
}
