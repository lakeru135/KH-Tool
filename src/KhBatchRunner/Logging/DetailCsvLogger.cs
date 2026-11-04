using System.IO;
using System.Text;

namespace KhBatchRunner.Logging;

/// <summary>
/// Persistent detail/output log: the extracted data / action result. Append-only, one row per
/// data item (e.g. one row per phase) rather than one joined-cell summary row per model. Named
/// <c>&lt;yyyyMMdd&gt;_&lt;ProjectLog&gt;_&lt;Task&gt;.csv</c> inside the task's own folder
/// (<c>&lt;destination&gt;\&lt;Task&gt;\</c>) — one file per calendar day, appended to across
/// every run that day. <see cref="TrackingCsvLogger"/>'s per-run file is NOT beside it: it lives in
/// a single <c>&lt;destination&gt;\Log\</c> folder shared by every task of the run.
/// </summary>
public sealed class DetailCsvLogger
{
    private readonly string _path;
    private readonly string _runTag;
    private List<string>? _columns;

    public string Path => _path;

    /// <summary>Name of the column carrying <see cref="RunTag"/>. Public because the run-end matrix
    /// merge filters on it (<see cref="Reporting.RunEndMatrix"/>).</summary>
    public const string RunColumn = "Run";

    /// <summary>
    /// The value written to the <see cref="RunColumn"/> column: one run's shared stamp, formatted
    /// exactly as it appears in that run's tracking-log filename so the two can be lined up by eye.
    /// </summary>
    public static string RunTag(DateTime? runStamp) =>
        runStamp?.ToString("yyyyMMdd_HHmmss") ?? "";

    /// <param name="runTag">Identifies the run these rows belong to — see <see cref="RunTag"/>.
    /// This file is a per-DAY append shared by every run of the same task, and an auto-split run's
    /// chunks all write to it concurrently, so "which rows are this run's" is otherwise unanswerable:
    /// timestamps alone cannot separate two batches started a minute apart, which is a thing that
    /// demonstrably happens. Blank keeps the column empty rather than absent, so the header stays
    /// stable across a day.</param>
    public DetailCsvLogger(string path, string runTag = "")
    {
        _path = path;
        _runTag = runTag;
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Builds the detail-log path: <c>&lt;destinationFolder&gt;\&lt;task&gt;\&lt;yyyyMMdd&gt;_
    /// &lt;projectLog&gt;_&lt;task&gt;.csv</c> — one file per day (date-only stamp), so repeated runs
    /// the same day append to it.
    /// </summary>
    /// <param name="day">Which day's file. Defaults to today, which is what a writer wants; the
    /// run-end merge passes the run's own start instead, because a run that begins at 23:58 is still
    /// writing into yesterday's file when it finishes.</param>
    public static string PathFor(string destinationFolder, string projectLog, string task,
        DateTime? day = null)
    {
        string taskDir = System.IO.Path.Combine(destinationFolder, CsvFormat.SanitizeFileName(task));
        string safe = CsvFormat.SanitizeFileName($"{day ?? DateTime.Now:yyyyMMdd}_{projectLog}_{task}");
        return System.IO.Path.Combine(taskDir, $"{safe}.csv");
    }

    /// <summary>Append one row for one data item belonging to <paramref name="modelName"/>.</summary>
    public void Log(string modelName, IReadOnlyDictionary<string, string> fields) =>
        LogMany(modelName, new[] { fields });

    /// <summary>
    /// Append every row a model produced in ONE locked write.
    ///
    /// <para>This used to be a loop over <see cref="Log"/>, and each call took the cross-process
    /// <see cref="LogFileLock"/>, stat'd the file, then opened/appended/closed it. Cheap for an
    /// action that emits a handful of rows; ruinous for one that emits a row per element.
    /// MEASURED 2026-07-26: <c>OLD-BLDA-ELE.rvt</c> produced <b>99,126</b> text-note rows, so that
    /// one model performed 99,126 lock acquisitions and 99,126 open/append/close cycles on a file
    /// growing into the tens of megabytes — while seven sibling Revit instances contended for the
    /// same day-file lock. Type Mark by Text Note cost 74 minutes across the run, most of it here
    /// rather than in Revit.</para>
    ///
    /// <para>Each row still carries its own timestamp, taken as the row is formatted. The file's
    /// contents are otherwise byte-identical: same columns, same order, same values.</para>
    /// </summary>
    public void LogMany(string modelName, IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        try
        {
            var formatted = new List<string>();
            foreach (IReadOnlyDictionary<string, string> fields in rows)
            {
                // The column set comes from the first row, exactly as before — an action's rows all
                // carry the same keys — unless the day-file already exists, in which case its own
                // header wins. See ResolveColumns.
                _columns ??= ResolveColumns(fields);

                var row = new List<string>(_columns!.Count);
                foreach (string col in _columns!)
                {
                    if (col == "Datetime") row.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    else if (col == "Model Name") row.Add(modelName);
                    else if (col == RunColumn) row.Add(_runTag);
                    else row.Add(fields.TryGetValue(col, out string? v) ? v : "");
                }

                formatted.Add(string.Join(",", row.Select(CsvFormat.Escape)));
            }

            if (formatted.Count == 0) return;

            // Guarded by a cross-process lock: an auto-split batch runs several Revit instances
            // that append to this same day-file, so the new-file/header check and the append must
            // be atomic against other writers (see LogFileLock). Concurrent chunks run the same
            // action, so they agree on the column set — the header written once stays consistent.
            LogFileLock.Write(_path, () =>
            {
                bool newFile = !File.Exists(_path) || new FileInfo(_path).Length == 0;

                var sb = new StringBuilder();
                if (newFile)
                    sb.AppendLine(string.Join(",", _columns!.Select(CsvFormat.Escape)));
                foreach (string line in formatted)
                    sb.AppendLine(line);

                File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
            });
        }
        catch
        {
            // Fail-soft: never let a logging error abort the batch.
        }
    }

    /// <summary>
    /// The columns this logger will write: <c>Datetime, Model Name, Run</c> followed by the action's
    /// own, EXCEPT when the day-file already exists — then its header wins verbatim and rows are
    /// aligned to it (any column we have no value for is left blank).
    ///
    /// <para>Deferring to the existing header is what makes adding the <see cref="RunColumn"/> column
    /// safe. This file is appended to all day, so on the day a new version is installed the morning's
    /// rows were written by the old build with the old header. Writing our wider row into that file
    /// would shift every value one column to the right from that point on — a silently corrupt CSV,
    /// which is worse than a missing column. The merge that reads the column copes with its absence
    /// (see <see cref="Reporting.RunEndMatrix"/>); it cannot cope with rows that do not line up.</para>
    ///
    /// <para>Read outside <see cref="LogFileLock"/> deliberately: it happens once per logger and the
    /// alternative is holding a cross-process lock while formatting tens of thousands of rows, which
    /// is the contention this class was rewritten to avoid. The window is harmless — concurrent
    /// writers here are chunks of one run, i.e. the same build agreeing on the same columns.</para>
    /// </summary>
    private List<string> ResolveColumns(IReadOnlyDictionary<string, string> firstRow)
    {
        List<string>? existing = TryReadHeader();
        if (existing is { Count: > 0 }) return existing;

        return new List<string> { "Datetime", "Model Name", RunColumn }
            .Concat(firstRow.Keys)
            .ToList();
    }

    /// <summary>The day-file's header row, or null when there is no file (or it cannot be read).</summary>
    private List<string>? TryReadHeader()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length == 0) return null;
            using var reader = new StreamReader(_path, Encoding.UTF8);
            string? header = reader.ReadLine();
            return string.IsNullOrEmpty(header) ? null : CsvFormat.SplitLine(header!);
        }
        catch { return null; }
    }
}
