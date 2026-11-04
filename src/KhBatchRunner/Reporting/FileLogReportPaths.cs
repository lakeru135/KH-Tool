using System.IO;
using KhBatchRunner.Logging;

namespace KhBatchRunner.Reporting;

/// <summary>
/// Where one Get File Log run's reports go — one CSV per Forma folder read.
///
/// <code>
/// &lt;dest&gt;\Get File Log\20260826_143001_SITE_Get File Log_01-WIP.csv
/// &lt;dest&gt;\Get File Log\20260826_143001_SITE_Get File Log_02-Shared.csv
/// &lt;dest&gt;\Log\20260826_143001_SITE_Daily Check SITE.csv          ← the run's tracking log
/// </code>
///
/// <para><b>Why not <see cref="CsvReportWriter"/>'s own naming.</b> That stamps the file with
/// <c>DateTime.Now</c> to a whole second at the moment of writing. A loop that writes several
/// folders — which is now the normal case — completes well inside one second for small folders, so
/// every report after the first would silently overwrite the one before it. Here the stamp is the
/// RUN's, passed in once, and the folder name is what separates the files.</para>
///
/// <para><b>And why the ordinal suffix.</b> Folder names are not unique: <c>01-WIP</c> exists in
/// every project, and two folders in one set can sanitize to the same string. The first folder with
/// a given name keeps the plain name; later ones get <c>_2</c>, <c>_3</c> — the same disambiguation
/// <see cref="Interop.InteropReportPaths"/> applies to parallel Interop groups, for the same
/// reason: a silently overwritten report is worse than an ugly filename.</para>
///
/// <para>Pure path arithmetic, deliberately free of Revit types, so the offline harness can assert
/// the naming rule without a Revit process — and so the non-Revit controller can compile it.</para>
/// </summary>
public static class FileLogReportPaths
{
    /// <summary>The task folder every one of a run's reports lands in.</summary>
    public static string ReportFolder(string destinationFolder, string taskName) =>
        Path.Combine(destinationFolder, CsvFormat.SanitizeFileName(taskName));

    /// <summary>Longest each name part may be. Names are already long — a run stamp, a project log
    /// name, the task, a Forma project and a folder — and they land inside destinations like
    /// <c>…\OneDrive - Contoso\Project\10-Script &amp; Automation\</c>. Windows
    /// still stops at 260 characters for most callers, so each part is bounded rather than trusted.</summary>
    private const int MaxPart = 40;

    /// <summary>
    /// How one folder is named in a report file: its Forma PROJECT, then the folder itself.
    ///
    /// <para>Folder names repeat constantly — <c>2.1.1_Drawings</c> exists under every project — and
    /// a run over two of them produced two reports distinguished only by a trailing <c>_2</c>. The
    /// project is what actually varies within a run; the hub usually does not, so it is left out
    /// rather than repeated identically in every filename.</para>
    /// </summary>
    public static string FolderLabel(string accProject, string folderName)
    {
        string folder = Trim(folderName);
        string project = Trim(accProject);
        return project.Length == 0 ? folder : project + "_" + folder;
    }

    /// <summary>
    /// The report path for ONE folder of a run.
    /// </summary>
    /// <param name="projectLog">The job's log name ("SITE"). When it is the task's own name — which
    /// is what an interactive run has, having no job — it is printed once instead of twice.</param>
    /// <param name="stamp">The run's shared stamp — the same value for every folder in the run, so
    /// its reports sort together and can be told apart from the previous run's.</param>
    /// <param name="folderLabel">From <see cref="FolderLabel"/>.</param>
    /// <param name="ordinal">1 for the first report using this label in this run, 2 for the next,
    /// and so on. See the class remarks.</param>
    public static string OutputPath(string destinationFolder, string projectLog, string taskName,
        DateTime stamp, string folderLabel, int ordinal = 1)
    {
        // "20260826_203251_Get File Log_Get File Log_…" — the task name twice — is what an
        // interactive run produced, because it has no job to take a log name from and falls back
        // to the task's.
        string prefix = string.Equals(projectLog, taskName, StringComparison.OrdinalIgnoreCase)
            ? taskName
            : $"{projectLog}_{taskName}";

        string leaf = $"{stamp:yyyyMMdd_HHmmss}_{prefix}_{folderLabel}";
        if (ordinal > 1) leaf += "_" + ordinal;
        return Path.Combine(
            ReportFolder(destinationFolder, taskName),
            CsvFormat.SanitizeFileName(leaf) + ".csv");
    }

    /// <summary>
    /// Assigns each folder in a run its ordinal, in run order, so callers do not have to track
    /// name collisions themselves. Returns one entry per input, aligned by index.
    /// </summary>
    /// <param name="folderLabels">The labels from <see cref="FolderLabel"/> — NOT bare folder
    /// names. Keying on the bare name would number two folders whose names now differ because their
    /// projects do.</param>
    public static List<int> Ordinals(IEnumerable<string> folderLabels)
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ordinals = new List<int>();
        foreach (string label in folderLabels)
        {
            string key = CsvFormat.SanitizeFileName(label ?? "");
            used[key] = used.TryGetValue(key, out int n) ? n + 1 : 1;
            ordinals.Add(used[key]);
        }
        return ordinals;
    }

    /// <summary>Bounded, and cut at the END so the distinguishing head of a name survives.</summary>
    private static string Trim(string? part)
    {
        string s = (part ?? "").Trim();
        return s.Length <= MaxPart ? s : s.Substring(0, MaxPart).TrimEnd();
    }
}
