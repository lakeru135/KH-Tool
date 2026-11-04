using System.IO;
using System.Text;

namespace KhBatchRunner.Logging;

/// <summary>
/// Writes a one-shot CSV report (fresh file, header + rows) — used by the Forma Extract features
/// (File Log, Folder Inventory), which produce a whole dataset at once rather than appending a
/// row per model like <see cref="DetailCsvLogger"/>. Reuses <see cref="CsvFormat"/> escaping.
/// </summary>
public static class CsvReportWriter
{
    /// <summary>
    /// Write <paramref name="rows"/> under the given ordered <paramref name="columns"/> to a
    /// timestamped file <c>&lt;name&gt;_&lt;yyyyMMdd-HHmmss&gt;.csv</c> in
    /// <paramref name="destinationFolder"/>, and return the full path written. A timestamp keeps
    /// successive extracts of the same folder from overwriting each other.
    /// </summary>
    public static string Write(
        string destinationFolder, string name, IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        Directory.CreateDirectory(destinationFolder);
        string safe = CsvFormat.SanitizeFileName(name);
        string path = Path.Combine(
            destinationFolder, $"{safe}_{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        return WriteTo(path, columns, rows);
    }

    /// <summary>
    /// As <see cref="Write"/>, but to an EXACT path the caller has already decided on.
    ///
    /// <para>Exists because <see cref="Write"/>'s own naming cannot serve a run that writes several
    /// reports: it stamps each file with <c>DateTime.Now</c> to the second, so a loop finishing
    /// inside one second overwrites its own output. Get File Log names its per-folder reports
    /// through <see cref="Reporting.FileLogReportPaths"/> instead, and only the bytes are written
    /// here — one escaping rule, one writer, two naming schemes.</para>
    /// </summary>
    public static string WriteTo(
        string path, IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(CsvFormat.Escape)));
        foreach (IReadOnlyDictionary<string, string> row in rows)
        {
            var cells = columns.Select(c => row.TryGetValue(c, out string? v) ? v : "");
            sb.AppendLine(string.Join(",", cells.Select(CsvFormat.Escape)));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
