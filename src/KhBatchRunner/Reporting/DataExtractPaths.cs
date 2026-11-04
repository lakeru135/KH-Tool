using System.IO;
using KhBatchRunner.Logging;

namespace KhBatchRunner.Reporting;

/// <summary>
/// Where one Get Data Extract run's files go — the extract's own CSVs, saved under a folder named
/// for the Forma job that produced them.
///
/// <code>
/// &lt;dest&gt;\Get Data Extract\20260918_060500_SampleHub_c0ffee03\admin_projects.csv
/// &lt;dest&gt;\Get Data Extract\20260918_060500_SampleHub_c0ffee03\issues_issues.csv
/// &lt;dest&gt;\Log\20260918_060500_SampleHub_Daily extract.csv          ← the run's tracking log
/// </code>
///
/// <para><b>Why a folder per run rather than a stamp per file.</b> An extract is forty-odd CSVs
/// whose names are fixed by Forma (<c>admin_projects.csv</c>, <c>issues_issues.csv</c>…). Stamping
/// each one, as <see cref="FileLogReportPaths"/> does for its one-file-per-folder output, would
/// produce forty renamed files a downstream Power BI query could no longer find by name. Keeping
/// Forma's own filenames inside a stamped folder means the path to "today's issues" is predictable
/// and yesterday's copy is still there beside it.</para>
///
/// <para><b>Why the job id is in the folder name.</b> It is what makes "have I already downloaded
/// this run?" answerable by looking, without a state file to keep in step — see
/// <see cref="ExistingRunFolder"/>. A weekly extract polled daily otherwise re-downloads the same
/// job every morning under a new name.</para>
///
/// <para>Pure path arithmetic, deliberately free of Revit types, so the offline harness can assert
/// the naming rule without a Revit process — and so the non-Revit controller can compile it.</para>
/// </summary>
public static class DataExtractPaths
{
    /// <summary>The task folder every one of a run's downloads lands in.</summary>
    public static string ReportFolder(string destinationFolder, string taskName) =>
        Path.Combine(destinationFolder, CsvFormat.SanitizeFileName(taskName));

    /// <summary>Longest each name part may be — the same bound, and for the same reason, as
    /// <see cref="FileLogReportPaths"/>: these paths sit inside long OneDrive destinations and
    /// Windows still stops at 260 characters for most callers.</summary>
    private const int MaxPart = 40;

    /// <summary>The short form of a Forma job id used in folder names. Eight hex characters is
    /// enough to tell two runs of one extract apart and short enough to read.</summary>
    public static string ShortJobId(string jobId)
    {
        string id = (jobId ?? "").Trim();
        return id.Length >= 8 ? id.Substring(0, 8) : id;
    }

    /// <summary>
    /// The folder holding ONE run's downloaded files.
    /// </summary>
    /// <param name="projectLog">The job's log name. When it is the task's own name — which is what
    /// an interactive run has, having no job — it is printed once instead of twice.</param>
    /// <param name="stamp">The run's shared stamp, so a run's folder sorts beside its tracking log
    /// and can be told apart from the previous run's.</param>
    public static string RunFolder(string destinationFolder, string projectLog, string taskName,
        DateTime stamp, string jobId)
    {
        string prefix = string.Equals(projectLog, taskName, StringComparison.OrdinalIgnoreCase)
            ? taskName
            : $"{projectLog}_{taskName}";

        string leaf = $"{stamp:yyyyMMdd_HHmmss}_{Trim(prefix)}_{ShortJobId(jobId)}";
        return Path.Combine(
            ReportFolder(destinationFolder, taskName),
            CsvFormat.SanitizeFileName(leaf));
    }

    /// <summary>One downloaded file inside its run folder, keeping Forma's own name.</summary>
    public static string FilePath(string runFolder, string fileName) =>
        Path.Combine(runFolder, CsvFormat.SanitizeFileName(fileName));

    /// <summary>
    /// An earlier run folder for the same Forma job, or null when this job has not been downloaded
    /// into this destination yet.
    ///
    /// <para>Matched on the job id suffix rather than on a recorded state, because the question the
    /// caller is really asking is "are these files already on disk?" and the files themselves are
    /// the only honest answer — a state file survives a folder the user has since deleted.</para>
    /// </summary>
    public static string? ExistingRunFolder(string destinationFolder, string taskName, string jobId)
    {
        string root = ReportFolder(destinationFolder, taskName);
        if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(jobId)) return null;

        string suffix = "_" + ShortJobId(jobId);
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(d => Path.GetFileName(d).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                // Newest first: a run that was interrupted and re-run leaves two, and the later one
                // is the one that completed.
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            // An unreadable destination is the download's problem to report, not this predicate's.
            return null;
        }
    }

    /// <summary>Bounded, and cut at the END so the distinguishing head of a name survives.</summary>
    private static string Trim(string? part)
    {
        string s = (part ?? "").Trim();
        return s.Length <= MaxPart ? s : s.Substring(0, MaxPart).TrimEnd();
    }
}
