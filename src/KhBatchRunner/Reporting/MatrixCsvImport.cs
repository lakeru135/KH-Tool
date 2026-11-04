using System.IO;
using System.Text;

namespace KhBatchRunner.Reporting;

/// <summary>
/// The run-end matrices that can be rebuilt from a saved detail CSV.
///
/// <para><b>Link Workset was removed in 1.3.8.2.</b> 1.3.8.1 stopped it writing a matrix at the end
/// of a run but kept it rebuildable on demand; this release drops the second half too, by decision,
/// so the only matrices this product produces are the two it produces automatically. There is no
/// half-supported third. Its detail CSV is unchanged and still holds every value the workbook was
/// ever pivoted from.</para>
/// </summary>
public enum MatrixKind
{
    DesignOption,
    PhaseWorkset,
}

/// <summary>
/// Rebuilds a run-end matrix workbook (Design Option / Phase &amp; Workset)
/// from a detail CSV the add-in already exported — no Revit model reopened. The detail CSVs are
/// written by <see cref="Logging.DetailCsvLogger"/>, so the columns each matrix needs are known;
/// a CSV whose header is missing any required column is <b>rejected</b> (with an "expected vs
/// found" message) rather than producing a wrong matrix. This lets a matrix be regenerated after
/// the models are closed, or after the CSV has been hand-filtered, without re-running the batch.
/// </summary>
public static class MatrixCsvImport
{
    /// <summary>One rebuildable matrix: its kind, a friendly name, and the detail-CSV columns it
    /// consumes (matched case-insensitively; extra columns such as "Datetime" are ignored).</summary>
    public sealed record Descriptor(MatrixKind Kind, string DisplayName, IReadOnlyList<string> RequiredColumns)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>The known matrices, in the order they appear on the Multi Model Check panel.</summary>
    public static readonly IReadOnlyList<Descriptor> Kinds = new[]
    {
        new Descriptor(MatrixKind.PhaseWorkset, "Phase & Workset",
            new[] { "Model Name", "Category", "Name" }),
        new Descriptor(MatrixKind.DesignOption, "Design Option",
            new[] { "Model Name", "Design Set", "Design Option", "Is Primary" }),
    };

    public static Descriptor DescriptorFor(MatrixKind kind) => Kinds.First(k => k.Kind == kind);

    /// <summary>The single matrix kind whose required columns are all present in
    /// <paramref name="header"/>, or null when the header matches none (or, unlikely, more than one).
    /// Used to auto-select the matrix type when a CSV is chosen.</summary>
    public static MatrixKind? Detect(IReadOnlyList<string> header)
    {
        var matches = Kinds.Where(k => HasAllColumns(header, k.RequiredColumns)).ToList();
        return matches.Count == 1 ? matches[0].Kind : (MatrixKind?)null;
    }

    /// <summary>
    /// Reads <paramref name="csvPath"/>, verifies it carries the columns <paramref name="kind"/>
    /// needs, and writes the matrix workbook to <paramref name="outputPath"/>. Returns the number of
    /// data rows consumed. Throws <see cref="InvalidDataException"/> when a required column is missing
    /// (the reject path) or when the CSV has no usable data rows.
    /// </summary>
    public static int Build(MatrixKind kind, string csvPath, string outputPath)
    {
        (List<string> header, List<string[]> rows) = ReadCsv(csvPath);
        return Build(kind, header, rows, outputPath);
    }

    /// <summary>
    /// As <see cref="Build(MatrixKind, string, string)"/>, but over rows already in hand.
    ///
    /// Exists for the run-end merge (<see cref="RunEndMatrix"/>), which must FILTER a day-file down
    /// to one run's rows before pivoting. Writing that subset back out to a temp CSV just to read it
    /// in again would be the obvious way to do it and the wrong one — the rejection rules, the
    /// column matching and the workbook writers are all here, and this way the merged workbook and
    /// the in-process one come out of literally the same code.
    /// </summary>
    public static int Build(MatrixKind kind, List<string> header, List<string[]> rows, string outputPath)
    {
        Descriptor desc = DescriptorFor(kind);

        var missing = desc.RequiredColumns.Where(c => IndexOf(header, c) < 0).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException(
                $"This CSV does not have the columns needed for a {desc.DisplayName} matrix.\n\n" +
                $"Missing column(s): {string.Join(", ", missing)}\n" +
                $"Expected: {string.Join(", ", desc.RequiredColumns)}\n" +
                $"Found: {(header.Count == 0 ? "(no header row)" : string.Join(", ", header))}");

        bool wrote = kind switch
        {
            MatrixKind.DesignOption => BuildDesignOption(header, rows, outputPath),
            MatrixKind.PhaseWorkset => BuildPhaseWorkset(header, rows, outputPath),
            _ => throw new InvalidDataException("Unknown matrix kind."),
        };
        if (!wrote)
            throw new InvalidDataException("The CSV had no data rows to build a matrix from.");
        return rows.Count;
    }

    private static bool BuildDesignOption(List<string> header, List<string[]> rows, string outputPath)
    {
        int model = IndexOf(header, "Model Name");
        int set = IndexOf(header, "Design Set");
        int option = IndexOf(header, "Design Option");
        int primary = IndexOf(header, "Is Primary");

        var data = new List<(string Model, string Set, string Option, bool IsPrimary)>();
        foreach (string[] r in rows)
        {
            string m = Cell(r, model);
            if (m.Length == 0) continue;
            data.Add((m, Cell(r, set), Cell(r, option), IsYes(Cell(r, primary))));
        }
        return DesignOptionMatrix.Write(outputPath, data);
    }

    private static bool BuildPhaseWorkset(List<string> header, List<string[]> rows, string outputPath)
    {
        int model = IndexOf(header, "Model Name");
        int category = IndexOf(header, "Category");
        int name = IndexOf(header, "Name");

        var data = new List<(string Model, string Category, string Name)>();
        foreach (string[] r in rows)
        {
            string m = Cell(r, model);
            if (m.Length == 0) continue;
            data.Add((m, Cell(r, category), Cell(r, name)));
        }
        return PhaseWorksetMatrix.Write(outputPath, data);
    }

    private static bool HasAllColumns(IReadOnlyList<string> header, IReadOnlyList<string> required) =>
        required.All(c => IndexOf(header, c) >= 0);

    /// <summary>Case-insensitive, trimmed column lookup; -1 when absent.</summary>
    private static int IndexOf(IReadOnlyList<string> header, string name)
    {
        for (int i = 0; i < header.Count; i++)
            if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string StripBom(string text) =>
        text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;

    private static string Cell(string[] row, int index) =>
        index >= 0 && index < row.Length ? row[index].Trim() : "";

    private static bool IsYes(string value) =>
        value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("True", StringComparison.OrdinalIgnoreCase) ||
        value == "1";

    /// <summary>
    /// Reads a CSV into its header row + data rows, honoring RFC-4180 quoting (fields quoted when
    /// they contain a comma/quote/newline, embedded quotes doubled) — the exact escaping
    /// <see cref="Logging.CsvFormat.Escape"/> writes. A leading UTF-8 BOM and a trailing newline are
    /// tolerated. Blank lines (outside quotes) are skipped.
    /// </summary>
    public static (List<string> Header, List<string[]> Rows) ReadCsv(string path)
    {
        string text = StripBom(File.ReadAllText(path, Encoding.UTF8));

        var records = new List<string[]>();
        var field = new StringBuilder();
        var record = new List<string>();
        bool inQuotes = false;
        bool recordHasContent = false;

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
        }
        void EndRecord()
        {
            EndField();
            // Drop a record that is a single empty field (a blank line).
            if (recordHasContent || record.Count > 1)
                records.Add(record.ToArray());
            record = new List<string>();
            recordHasContent = false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    recordHasContent = true;
                    break;
                case ',':
                    recordHasContent = true;
                    EndField();
                    break;
                case '\r':
                    break; // fold CRLF into the LF case
                case '\n':
                    EndRecord();
                    break;
                default:
                    recordHasContent = true;
                    field.Append(c);
                    break;
            }
        }
        // Flush the final field/record if the file didn't end with a newline.
        if (field.Length > 0 || record.Count > 0 || recordHasContent)
            EndRecord();

        if (records.Count == 0)
            return (new List<string>(), new List<string[]>());

        var header = records[0].ToList();
        var rows = records.Skip(1).ToList();
        return (header, rows);
    }
}
