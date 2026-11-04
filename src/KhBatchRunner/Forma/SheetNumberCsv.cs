using System.IO;

namespace KhBatchRunner.Forma;

/// <summary>
/// Reads a list of sheet numbers from a local CSV — the first column of each row. Tolerates a
/// header row (a first cell of "sheet number"/"sheet"/"number" is skipped), quoted cells, blank
/// lines, and dedupes while preserving order. Kept deliberately small — no CSV library needed for
/// a one-column list.
/// </summary>
public static class SheetNumberCsv
{
    public static List<string> Read(string path)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool first = true;
        foreach (string rawLine in File.ReadLines(path))
        {
            string cell = FirstCell(rawLine).Trim();
            if (first)
            {
                first = false;
                string lower = cell.ToLowerInvariant();
                if (lower is "sheet number" or "sheet" or "number" or "sheet no" or "sheet_no")
                    continue; // header row
            }
            if (cell.Length == 0) continue;
            if (seen.Add(cell)) result.Add(cell);
        }
        return result;
    }

    /// <summary>First CSV field of a line, honoring a leading quoted field.</summary>
    private static string FirstCell(string line)
    {
        if (line.Length == 0) return "";
        if (line[0] == '"')
        {
            int end = line.IndexOf('"', 1);
            return end > 0 ? line.Substring(1, end - 1).Replace("\"\"", "\"") : line.Substring(1);
        }
        int comma = line.IndexOf(',');
        return comma >= 0 ? line.Substring(0, comma) : line;
    }
}
