using System.IO;
using System.IO.Compression;
using System.Text;

namespace KhBatchRunner.Reporting;

/// <summary>
/// A tiny, self-contained OOXML (<c>.xlsx</c>) writer — a <c>.xlsx</c> is just a zip of XML parts,
/// which we build with <see cref="ZipArchive"/> (present on net48 + net8). Deliberately depends on
/// NOTHING external (no ClosedXML/EPPlus): those pull in DocumentFormat.OpenXml + SixLabors.Fonts,
/// which are an assembly-conflict / font-measuring hazard inside Revit's shared load context (the
/// same class of pain this add-in already hit with System.Text.Json on net48).
///
/// Only what the phase/workset/design-option matrices need: multiple sheets, per-column widths,
/// per-row heights, and cells written with a style picked from the fixed <see cref="XlsxStyle"/>
/// table (see <c>StylesXml</c>). Text cells use inline strings, so there's no shared-string table.
/// </summary>
public sealed class XlsxWriter
{
    /// <summary>Fixed style ids — the index into the <c>cellXfs</c> table in <c>StylesXml</c>.</summary>
    public enum XlsxStyle
    {
        Default = 0,
        Header = 1,          // rotated 90°, bold, grey fill, border (phase/workset model headers)
        PresentBlock = 2,    // green fill, number hidden — a phase/workset "present" cell
        Empty = 3,           // border only — an "absent" cell
        TotalNumber = 4,     // bold, light fill, border, centred — grand-total counts (visible)
        TotalLabel = 5,      // bold, light fill, border — "Grand Total" label
        RowLabel = 6,        // border, left — a phase/workset/option label
        HeaderBlue = 7,      // rotated 90°, bold, blue fill, border (design-option model headers)
        TopLeftTitle = 8,    // bold, blue fill, border, wrap — the top-left title cell
        GroupHeader = 9,     // bold, blue fill, border — a design "⊟ Set" group row
        PrimaryBlock = 10,   // dark-green fill+font, number hidden — a design "is primary" cell
        DesignTotalNumber = 11, // bold, blue fill, border, centred — design grand-total counts
        DesignTotalLabel = 12,  // bold, blue fill, border — design "Grand Total" label
        HeaderPlain = 13,    // bold, grey fill, border, NOT rotated (Excel Link column headers)
        LegendInstance = 14, // bold, light-green fill, border — legend "Instance (may edit)"
        LegendLocked = 15,   // bold, grey fill, border — legend "Type/Read-only (cannot edit)"
        CellLocked = 16,     // grey fill, border — a non-editable data cell (Excel Link)
    }

    public sealed class Sheet
    {
        public string Name { get; }
        internal readonly Dictionary<(int Row, int Col), Cell> Cells = new();
        internal readonly Dictionary<int, double> ColWidths = new();
        internal readonly Dictionary<int, double> RowHeights = new();
        internal readonly HashSet<int> HiddenCols = new();
        internal int FrozenRows;
        internal int MaxRow = -1;
        internal int MaxCol = -1;
        internal string? AutoFilterRef;

        internal Sheet(string name) => Name = name;

        public void SetColumnWidth(int col, double width) => ColWidths[col] = width;
        public void SetRowHeight(int row, double height) => RowHeights[row] = height;

        /// <summary>Freeze the top <paramref name="rows"/> rows (they stay visible while the data
        /// scrolls — e.g. a legend + header pair).</summary>
        public void FreezeTopRows(int rows) => FrozenRows = Math.Max(0, rows);

        /// <summary>Hide a column (it still round-trips — readers see its cells; users can unhide).</summary>
        public void HideColumn(int col) => HiddenCols.Add(col);

        /// <summary>Add an auto-filter over the (0-based, inclusive) cell range — Excel draws the
        /// header dropdowns across it. Stored as an A1 range and emitted right after <c>sheetData</c>
        /// (the order OOXML's CT_Worksheet requires). Reuses the writer's <see cref="ColLetter"/>.</summary>
        public void SetAutoFilter(int firstRow, int firstCol, int lastRow, int lastCol) =>
            AutoFilterRef = $"{ColLetter(firstCol)}{firstRow + 1}:{ColLetter(lastCol)}{lastRow + 1}";

        public void WriteString(int row, int col, string text, XlsxStyle style) =>
            Set(row, col, new Cell { Text = text ?? "", Style = style });

        public void WriteNumber(int row, int col, double value, XlsxStyle style) =>
            Set(row, col, new Cell { Number = value, Style = style });

        /// <summary>A styled but empty cell (e.g. a bordered "absent" square).</summary>
        public void WriteBlank(int row, int col, XlsxStyle style) =>
            Set(row, col, new Cell { Style = style });

        /// <summary>
        /// Writes text straight into the sheet XML, unescaped. There is no production caller and
        /// must never be one: it exists so the harness can hand <see cref="Validate"/> a part that
        /// really is malformed and prove the guard fires, names the sheet, and leaves no file —
        /// the same fault-verification the window-construction gate gets. Every real path goes
        /// through <see cref="WriteString"/>, and <see cref="Escape"/> makes malformed output
        /// unreachable from there.
        /// </summary>
        internal void WriteRawForTest(int row, int col, string rawXml, XlsxStyle style) =>
            Set(row, col, new Cell { Text = rawXml, Style = style, Raw = true });

        private void Set(int row, int col, Cell cell)
        {
            Cells[(row, col)] = cell;
            if (row > MaxRow) MaxRow = row;
            if (col > MaxCol) MaxCol = col;
        }
    }

    internal struct Cell
    {
        public string? Text;
        public double? Number;
        public XlsxStyle Style;
        /// <summary>Emit <see cref="Text"/> verbatim — harness only, see <c>WriteRawForTest</c>.</summary>
        public bool Raw;
    }

    private readonly List<Sheet> _sheets = new();

    public Sheet AddSheet(string name)
    {
        var s = new Sheet(SanitizeSheetName(name, _sheets.Count));
        _sheets.Add(s);
        return s;
    }

    /// <summary>
    /// Write the workbook.
    ///
    /// <para>Every part is built and CHECKED before the file is opened, so a part Excel would
    /// reject aborts with nothing on disk rather than leaving a truncated zip where a workbook
    /// used to be. See <see cref="Validate"/>.</para>
    /// </summary>
    public void Save(string path)
    {
        // (entry path, content, what to call it in an error)
        var parts = new List<(string Name, string Content, string Describe)>
        {
            ("[Content_Types].xml", ContentTypesXml(), "[Content_Types].xml"),
            ("_rels/.rels", RootRelsXml(), "_rels/.rels"),
            ("xl/workbook.xml", WorkbookXml(), "the workbook"),
            ("xl/_rels/workbook.xml.rels", WorkbookRelsXml(), "xl/_rels/workbook.xml.rels"),
            ("xl/styles.xml", StylesXml, "xl/styles.xml"),
        };
        for (int i = 0; i < _sheets.Count; i++)
            parts.Add(($"xl/worksheets/sheet{i + 1}.xml", SheetXml(_sheets[i]), _sheets[i].Name));

        foreach ((_, string content, string describe) in parts)
            Validate(content, describe);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach ((string name, string content, _) in parts)
            WriteEntry(zip, name, content);
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    /// <summary>
    /// Refuse to put a part into the zip unless it parses as XML.
    ///
    /// <para>A malformed part does not fail loudly: the zip is written, the file opens, and Excel
    /// offers to repair it and then silently drops whatever it could not read — which is how a
    /// two-sheet matrix reached a user as a one-sheet matrix with a scary prompt and no error
    /// anywhere in the logs. Throwing here instead routes the failure into the callers' existing
    /// visible error paths (<see cref="RunEndMatrix"/> notes it and points at Rebuild Matrix from
    /// CSV) and leaves no half-good workbook behind to be trusted.</para>
    ///
    /// <para>Costs one parse of a few MB per workbook — nothing against a run measured in hours,
    /// and only ever paid once per run.</para>
    /// </summary>
    private static void Validate(string xml, string part)
    {
        try
        {
            using var reader = System.Xml.XmlReader.Create(
                new StringReader(xml), new System.Xml.XmlReaderSettings { CloseInput = true });
            while (reader.Read()) { }
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException(
                $"The workbook could not be written: '{part}' produced XML that Excel would " +
                $"reject ({ex.Message}). No file was written — nothing was silently corrupted.", ex);
        }
    }

    // ---- part builders --------------------------------------------------------

    private string ContentTypesXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (int i = 0; i < _sheets.Count; i++)
            sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string RootRelsXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private string WorkbookXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (int i = 0; i < _sheets.Count; i++)
            sb.Append($"<sheet name=\"{Escape(_sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    private string WorkbookRelsXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 0; i < _sheets.Count; i++)
            sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
        sb.Append($"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string SheetXml(Sheet sheet)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        // Part order matters to Excel: sheetViews (freeze panes) must precede cols and sheetData.
        if (sheet.FrozenRows > 0)
        {
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            sb.Append($"<pane ySplit=\"{sheet.FrozenRows}\" topLeftCell=\"A{sheet.FrozenRows + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            sb.Append("<selection pane=\"bottomLeft\"/>");
            sb.Append("</sheetView></sheetViews>");
        }

        if (sheet.ColWidths.Count > 0 || sheet.HiddenCols.Count > 0)
        {
            sb.Append("<cols>");
            foreach (int col in sheet.ColWidths.Keys.Concat(sheet.HiddenCols).Distinct().OrderBy(c => c))
            {
                sb.Append($"<col min=\"{col + 1}\" max=\"{col + 1}\"");
                if (sheet.ColWidths.TryGetValue(col, out double width))
                    sb.Append($" width=\"{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" customWidth=\"1\"");
                if (sheet.HiddenCols.Contains(col))
                    sb.Append(" hidden=\"1\"");
                sb.Append("/>");
            }
            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        for (int r = 0; r <= sheet.MaxRow; r++)
        {
            // Only emit rows that have cells or an explicit height.
            bool hasCells = false;
            for (int c = 0; c <= sheet.MaxCol; c++)
                if (sheet.Cells.ContainsKey((r, c))) { hasCells = true; break; }
            bool hasHeight = sheet.RowHeights.ContainsKey(r);
            if (!hasCells && !hasHeight) continue;

            sb.Append($"<row r=\"{r + 1}\"");
            if (hasHeight)
                sb.Append($" ht=\"{sheet.RowHeights[r].ToString(System.Globalization.CultureInfo.InvariantCulture)}\" customHeight=\"1\"");
            sb.Append(">");
            for (int c = 0; c <= sheet.MaxCol; c++)
            {
                if (!sheet.Cells.TryGetValue((r, c), out Cell cell)) continue;
                string reference = $"{ColLetter(c)}{r + 1}";
                int s = (int)cell.Style;
                if (cell.Number.HasValue)
                    sb.Append($"<c r=\"{reference}\" s=\"{s}\"><v>{cell.Number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                else if (!string.IsNullOrEmpty(cell.Text))
                    sb.Append($"<c r=\"{reference}\" s=\"{s}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{(cell.Raw ? cell.Text! : Escape(cell.Text!))}</t></is></c>");
                else
                    sb.Append($"<c r=\"{reference}\" s=\"{s}\"/>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        // autoFilter must sit immediately after sheetData (we emit no mergeCells/conditionalFormatting).
        if (!string.IsNullOrEmpty(sheet.AutoFilterRef))
            sb.Append($"<autoFilter ref=\"{sheet.AutoFilterRef}\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    // Fixed styles table: numFmt 164 = ";;;" (hide the number, only the fill shows through).
    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\";;;\"/></numFmts>" +
        "<fonts count=\"3\">" +
          "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
          "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
          "<font><b/><color rgb=\"FF274E13\"/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
        "</fonts>" +
        "<fills count=\"8\">" +
          "<fill><patternFill patternType=\"none\"/></fill>" +
          "<fill><patternFill patternType=\"gray125\"/></fill>" +
          "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4F8A10\"/></patternFill></fill>" +
          "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/></patternFill></fill>" +
          "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9D9D9\"/></patternFill></fill>" +
          "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF274E13\"/></patternFill></fill>" +
          "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDCE6F1\"/></patternFill></fill>" +
          "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE2EFDA\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"2\">" +
          "<border><left/><right/><top/><bottom/><diagonal/></border>" +
          "<border><left style=\"thin\"/><right style=\"thin\"/><top style=\"thin\"/><bottom style=\"thin\"/><diagonal/></border>" +
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"17\">" +
          // 0 Default
          "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
          // 1 Header (rotated, bold, grey fill, border)
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment textRotation=\"90\" horizontal=\"center\" vertical=\"center\"/></xf>" +
          // 2 PresentBlock (green fill, hidden number, border)
          "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
          // 3 Empty (border only)
          "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\"/>" +
          // 4 TotalNumber (bold, light fill, border, centred)
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\"/></xf>" +
          // 5 TotalLabel (bold, light fill, border)
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"3\" borderId=\"1\" xfId=\"0\"/>" +
          // 6 RowLabel (border, left)
          "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\"/>" +
          // 7 HeaderBlue (rotated, bold, blue fill, border)
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"6\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment textRotation=\"90\" horizontal=\"center\" vertical=\"bottom\"/></xf>" +
          // 8 TopLeftTitle (bold, blue fill, border, wrap)
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"6\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"bottom\"/></xf>" +
          // 9 GroupHeader (bold, blue fill, border)
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"6\" borderId=\"1\" xfId=\"0\"/>" +
          // 10 PrimaryBlock (dark-green fill+font, hidden number, border, centred, bold)
          "<xf numFmtId=\"164\" fontId=\"2\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
          // 11 DesignTotalNumber (bold, blue fill, border, centred)
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"6\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\"/></xf>" +
          // 12 DesignTotalLabel (bold, blue fill, border)
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"6\" borderId=\"1\" xfId=\"0\"/>" +
          // 13 HeaderPlain (bold, grey fill, border, NOT rotated — Excel Link headers)
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" +
          // 14 LegendInstance (bold, light-green fill, border — 'Instance (may edit)')
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"7\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" +
          // 15 LegendLocked (bold, grey fill, border — 'Type/Read-only (cannot edit)')
          "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" +
          // 16 CellLocked (light-grey fill, border — a non-editable data cell)
          "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"3\" borderId=\"1\" xfId=\"0\"/>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    // ---- helpers --------------------------------------------------------------

    private static string ColLetter(int col)
    {
        var sb = new StringBuilder();
        col += 1; // 1-based for the A/AA math
        while (col > 0)
        {
            int rem = (col - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            col = (col - 1) / 26;
        }
        return sb.ToString();
    }

    /// <summary>
    /// XML-escape a cell value or sheet name, AND drop every character XML 1.0 cannot represent.
    ///
    /// <para>The dropping half is not defensive tidiness — it is the fix for a corrupt workbook.
    /// A Phase &amp; Workset matrix opened with Excel's "we found a problem with some content"
    /// prompt and, after repair, had lost its whole <c>Workset Report</c> sheet: one illegal
    /// character anywhere in the several thousand workset names made that one part unparseable, so
    /// Excel discarded the part and kept the rest of the workbook. Phase names come from the
    /// company template and are tame; workset names are free text somebody typed or pasted, and a
    /// name pasted out of Word carries <c>0x0B</c> where Word had a soft line break.</para>
    ///
    /// <para>Legal in XML 1.0 (and kept): tab, LF, CR, <c>U+0020</c>–<c>U+D7FF</c>,
    /// <c>U+E000</c>–<c>U+FFFD</c>, and <c>U+10000</c>–<c>U+10FFFF</c> written as a surrogate PAIR.
    /// Everything else goes, including a surrogate that has lost its partner — a half character
    /// that a truncated name can genuinely leave behind, and that no parser will accept.</para>
    /// </summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); continue;
                case '<': sb.Append("&lt;"); continue;
                case '>': sb.Append("&gt;"); continue;
                case '"': sb.Append("&quot;"); continue;
                case '\t':
                case '\n':
                case '\r': sb.Append(c); continue;
            }

            if (c < 0x20 || c == 0xFFFE || c == 0xFFFF) continue;   // C0 controls + the non-characters

            if (char.IsHighSurrogate(c))
            {
                // Keep the pair, or drop a high surrogate with nothing after it.
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;                   // an orphan tail

            sb.Append(c);
        }
        return sb.ToString();
    }

    // Public: Excel Link's importer must map workbook sheet names back to schedule names, so it
    // re-applies the exact same sanitation the export used.
    public static string SanitizeSheetName(string name, int index)
    {
        // Excel forbids : \ / ? * [ ] and >31 chars; also can't be empty.
        string cleaned = new string((name ?? "").Where(ch => ":\\/?*[]".IndexOf(ch) < 0).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = $"Sheet{index + 1}";
        return cleaned.Length > 31 ? cleaned.Substring(0, 31) : cleaned;
    }
}
