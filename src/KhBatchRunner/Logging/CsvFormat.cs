using System.Linq;

namespace KhBatchRunner.Logging;

/// <summary>Shared CSV field-escaping used by both the tracking and detail loggers.</summary>
internal static class CsvFormat
{
    public static string Escape(string field)
    {
        field ??= "";
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }

    /// <summary>
    /// Splits ONE csv line back into its fields — the inverse of <see cref="Escape"/>, honouring
    /// quoted fields and doubled quotes. Used to read back a detail log's existing header row so
    /// new rows can be aligned to it (<see cref="DetailCsvLogger"/>); a whole-file reader lives in
    /// <c>Reporting.MatrixCsvImport.ReadCsv</c>, which this deliberately does not depend on — a
    /// logger reaching into the reporting layer to read one line would be the wrong way round.
    /// </summary>
    public static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c != '"') { field.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else inQuotes = false;
                continue;
            }

            if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields;
    }

    /// <summary>Strips characters that are invalid in a Windows file name. Each segment around a
    /// stripped char is trimmed so a name like "DYN: X" can't leave a stray space ("DYN_ X").</summary>
    public static string SanitizeFileName(string name) =>
        string.Join("_", name.Split(System.IO.Path.GetInvalidFileNameChars())
            .Select(part => part.Trim()));
}
