using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace KhBatchRunner.Reporting;

/// <summary>One worksheet read back from a workbook — every non-empty cell as display text.</summary>
public sealed class XlsxSheet
{
    public required string Name { get; init; }
    public required IReadOnlyDictionary<(int Row, int Col), string> Cells { get; init; }
    public int MaxRow { get; init; } = -1;
    public int MaxCol { get; init; } = -1;

    /// <summary>The cell's text, or "" when the cell is absent/empty (0-based row/col).</summary>
    public string Cell(int row, int col) => Cells.TryGetValue((row, col), out string? v) ? v : "";
}

/// <summary>
/// A tiny, self-contained OOXML (<c>.xlsx</c>) reader — the read-side sibling of
/// <see cref="XlsxWriter"/>, hand-rolled for the same reason (no ClosedXML/EPPlus inside Revit's
/// shared load context). Reads every sheet's cells as TEXT; the caller parses semantics.
///
/// Handles what a workbook we wrote looks like AFTER a user edits and saves it in real Excel:
/// Excel converts inline strings to a sharedStrings table (each <c>&lt;si&gt;</c> may be rich-text
/// runs — all descendant <c>&lt;t&gt;</c> are concatenated), booleans become <c>t="b"</c>, and
/// formulas are ignored in favour of their cached <c>&lt;v&gt;</c>. Elements are matched by
/// LocalName so a prefixed (<c>x:</c>) or strict-namespace producer still parses.
/// </summary>
public static class XlsxReader
{
    public static IReadOnlyList<XlsxSheet> Read(string path)
    {
        // ReadWrite share: the workbook is typically still open in Excel when the user hits Import.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        XDocument workbook = LoadXml(zip, "xl/workbook.xml")
            ?? throw new InvalidDataException("Not a workbook: xl/workbook.xml is missing.");
        Dictionary<string, string> rels = ReadWorkbookRels(zip);
        List<string> shared = ReadSharedStrings(zip);

        var sheets = new List<XlsxSheet>();
        foreach (XElement sheet in workbook.Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            string name = (string?)sheet.Attribute("name") ?? "";
            string rid = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "";
            if (!rels.TryGetValue(rid, out string? part)) continue;
            XDocument? doc = LoadXml(zip, part);
            if (doc is null) continue;
            sheets.Add(ReadSheet(name, doc, shared));
        }
        return sheets;
    }

    private static XDocument? LoadXml(ZipArchive zip, string entryName)
    {
        ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using Stream s = entry.Open();
        return XDocument.Load(s);
    }

    /// <summary>r:id → worksheet part path ("xl/worksheets/sheet1.xml"), target-form tolerant
    /// (relative to xl/, or package-absolute "/xl/…").</summary>
    private static Dictionary<string, string> ReadWorkbookRels(ZipArchive zip)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        XDocument? rels = LoadXml(zip, "xl/_rels/workbook.xml.rels");
        if (rels is null) return map;
        foreach (XElement rel in rels.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            string id = (string?)rel.Attribute("Id") ?? "";
            string target = (string?)rel.Attribute("Target") ?? "";
            if (id.Length == 0 || target.Length == 0) continue;
            target = target.Replace('\\', '/');
            map[id] = target.StartsWith("/", StringComparison.Ordinal)
                ? target.TrimStart('/')
                : "xl/" + target;
        }
        return map;
    }

    /// <summary>Each &lt;si&gt; as one string — rich-text runs (multiple &lt;r&gt;/&lt;t&gt;) concatenated.</summary>
    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        XDocument? doc = LoadXml(zip, "xl/sharedStrings.xml");
        if (doc?.Root is null) return list;
        foreach (XElement si in doc.Root.Elements().Where(e => e.Name.LocalName == "si"))
            list.Add(string.Concat(si.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)));
        return list;
    }

    private static XlsxSheet ReadSheet(string name, XDocument doc, List<string> shared)
    {
        var cells = new Dictionary<(int Row, int Col), string>();
        int maxRow = -1, maxCol = -1;

        XElement? sheetData = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        int implicitRow = -1;
        foreach (XElement row in sheetData?.Elements().Where(e => e.Name.LocalName == "row")
                                 ?? Enumerable.Empty<XElement>())
        {
            // r attributes are optional in the schema — fall back to sequential positions.
            int r = int.TryParse((string?)row.Attribute("r"), out int rr) ? rr - 1 : implicitRow + 1;
            implicitRow = r;

            int implicitCol = -1;
            foreach (XElement c in row.Elements().Where(e => e.Name.LocalName == "c"))
            {
                int col;
                string? cellRef = (string?)c.Attribute("r");
                if (cellRef != null && TryParseCellRef(cellRef, out int refRow, out int refCol))
                {
                    r = refRow;
                    col = refCol;
                }
                else col = implicitCol + 1;
                implicitCol = col;

                string value = CellText(c, shared);
                if (value.Length == 0) continue;
                cells[(r, col)] = value;
                if (r > maxRow) maxRow = r;
                if (col > maxCol) maxCol = col;
            }
        }

        return new XlsxSheet { Name = name, Cells = cells, MaxRow = maxRow, MaxCol = maxCol };
    }

    private static string CellText(XElement c, List<string> shared)
    {
        string type = (string?)c.Attribute("t") ?? "";
        if (type == "inlineStr")
        {
            XElement? isEl = c.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
            return isEl is null
                ? ""
                : string.Concat(isEl.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
        }

        // Everything else carries its (cached, for formulas) value in <v>; <f> is deliberately ignored.
        string v = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
        return type switch
        {
            "s" => int.TryParse(v, out int i) && i >= 0 && i < shared.Count ? shared[i] : "",
            "b" => v == "1" ? "TRUE" : v.Length == 0 ? "" : "FALSE",
            _ => v, // "str" (formula string), "e" (error text), numbers, dates-as-serials
        };
    }

    /// <summary>"C7" → (row 6, col 2), both 0-based. False for a malformed reference.</summary>
    private static bool TryParseCellRef(string cellRef, out int row, out int col)
    {
        row = -1; col = -1;
        int i = 0, c = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
        {
            c = c * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1);
            i++;
        }
        if (i == 0 || i == cellRef.Length) return false;
        if (!int.TryParse(cellRef[i..], out int r) || r < 1) return false;
        row = r - 1;
        col = c - 1;
        return true;
    }
}
