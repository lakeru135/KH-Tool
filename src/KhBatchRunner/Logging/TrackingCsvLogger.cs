using System.IO;
using System.Text;

namespace KhBatchRunner.Logging;

/// <summary>
/// Per-run tracking log: exactly one row per model in a fixed shape —
/// <c>Datetime (Finish) / Model / Task / Status / Message</c> ("is this run happening,
/// model by model, right now"). A fresh file every run, named
/// <c>&lt;Datetime&gt;_&lt;LogProject&gt;_&lt;LogNameRun&gt;.csv</c> in ONE <c>Log\</c> folder at the
/// destination root (<c>&lt;destination&gt;\Log\</c>) — not per task; see <see cref="PathFor"/>,
/// which is what the code actually does and what the diagnostic collector reads. The <c>Task</c>
/// column carries the pass label — prefixed <c>k of N</c> when the run has more than one task —
/// so a file read in isolation still says which task, and how many, produced it. This is distinct
/// from <see cref="DetailCsvLogger"/>, which accumulates the actual extracted data / action result.
/// </summary>
public sealed class TrackingCsvLogger : IRunLogger
{
    /// <summary>Fixed column order for every tracking log.</summary>
    private static readonly string[] ColumnHeaders =
        { "Datetime (Finish)", "Model", "Task", "Status", "Message" };

    private readonly string _path;
    private readonly string _taskLabel;

    public string CurrentModel { get; set; } = "";
    public string Path => _path;

    /// <param name="path">Full path of the per-run tracking CSV (see <see cref="PathFor"/>).</param>
    /// <param name="taskLabel">Value written to the <c>Task</c> column — the task's display name,
    /// already prefixed <c>k of N</c> by the engine when the run has multiple tasks.</param>
    public TrackingCsvLogger(string path, string taskLabel)
    {
        _path = path;
        _taskLabel = taskLabel;
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Builds the tracking-log path: <c>&lt;destinationFolder&gt;\Log\&lt;runStamp&gt;_&lt;projectLog&gt;_
    /// &lt;logNameRun&gt;.csv</c> — ONE file per run, in a single <c>Log\</c> folder at the destination
    /// root (not per task). <paramref name="runStamp"/> is computed once by the caller and shared across
    /// every task/pass in a run, so they all resolve to (and append to) the same file; the <c>Task</c>
    /// column then tells the tasks apart.
    /// </summary>
    public static string PathFor(string destinationFolder, string projectLog, string logNameRun, DateTime runStamp)
    {
        string logDir = System.IO.Path.Combine(destinationFolder, "Log");
        string safe = CsvFormat.SanitizeFileName($"{projectLog}_{logNameRun}");
        return System.IO.Path.Combine(logDir, $"{runStamp:yyyyMMdd_HHmmss}_{safe}.csv");
    }

    /// <summary>Append one model's outcome. Reads <c>Status</c> and <c>Message</c> from <paramref name="fields"/>.</summary>
    public void Log(IReadOnlyDictionary<string, string> fields)
    {
        try
        {
            // Guarded by a cross-process lock: an auto-split batch runs several Revit instances
            // that append to this same file, so the new-file check and the append must be atomic
            // against other writers (see LogFileLock). Harmless overhead for an ordinary run.
            LogFileLock.Write(_path, () =>
            {
                bool newFile = !File.Exists(_path) || new FileInfo(_path).Length == 0;

                var sb = new StringBuilder();
                if (newFile)
                    sb.AppendLine(string.Join(",", ColumnHeaders.Select(CsvFormat.Escape)));

                var row = new[]
                {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    CurrentModel,
                    _taskLabel,
                    fields.TryGetValue("Status", out string? s) ? s : "",
                    fields.TryGetValue("Message", out string? m) ? m : "",
                };

                sb.AppendLine(string.Join(",", row.Select(CsvFormat.Escape)));
                File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
            });
        }
        catch
        {
            // Fail-soft: never let a logging error abort the batch.
        }
    }
}
