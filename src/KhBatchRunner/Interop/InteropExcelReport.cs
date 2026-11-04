using System.IO;
using KhBatchRunner.Reporting;
using Style = KhBatchRunner.Reporting.XlsxWriter.XlsxStyle;

namespace KhBatchRunner.Interop;

/// <summary>
/// The Model Checker's Excel report, written by us instead of by AIT's <c>ReportExporterExcel</c>.
///
/// <para><b>Why.</b> MEASURED 2026-10-30 on the interopbench Snowdon model with a client Model
/// Health Check: AIT's Excel export took 3.0–3.9 s of a 7.2 s check action, and HTML took 13 ms.
/// Almost none of that is the report. AIT copies an embedded template whose Checks and Elements
/// sheets declare all 1,048,576 rows, then opens and re-saves it with EPPlus — 4.9 s for a bare
/// open+save of that workbook outside Revit, whatever the data. The same 31 checks and 2,381 element
/// rows written here take a fraction of that.</para>
///
/// <para><b>Same content, not same styling.</b> Sheets, headers, column order, row order and cell
/// text follow AIT's exporter exactly (decompiled, AIT 10.0.9): <c>Files</c>, <c>Checks</c>,
/// <c>Elements</c>, every value a string as AIT writes it, element rows in the order of AIT's own
/// sorted list view, and the ACAD-shaped Elements sheet when no Revit element rows exist. The
/// template's fonts and colours are not reproduced.</para>
///
/// <para><b>Two halves, on purpose.</b> <see cref="Snapshot"/> must run on Revit's thread: AIT keeps
/// each check's element list behind a WPF <c>CollectionViewSource</c>, and its view can't be read
/// from any other thread. <see cref="Write"/> touches only plain data.</para>
/// </summary>
internal static class InteropExcelReport
{
    public sealed record FileRow(string[] Cells, List<CheckRow> Checks);
    public sealed record CheckRow(string[] Cells, List<string[]> Elements);

    private static readonly string[] FileHeaders =
    {
        "Report Date", "Revit File", "Checkset Path", "Checkset Title", "Checkset Author",
        "Checkset Description", "Overall Result", "Pass Percent", "Pass Count", "Fail Count",
        "Report Count", "Error Count", "Not Run Count",
    };
    private static readonly string[] CheckHeaders =
    {
        "Revit File", "Check ID", "Name", "Description", "Result", "Failure Message",
        "Result Message", "Error", "Count",
    };
    private static readonly string[] RevitElementHeaders =
    {
        "Check ID", "Element ID", "Category", "Family", "Type", "Name", "Value", "Count", "Revit File",
    };
    // AIT's Elements_ACAD layout. A Revit document has no ACAD rows, so this sheet only ever appears
    // empty — when no check listed any Revit element — but it is what AIT writes then.
    private static readonly string[] AcadElementHeaders =
    {
        "Check ID", "File Name", "File Path", "Property Set Name", "Property Name", "Property Value",
        "Software Object Type", "Object Type", "Object Name", "Object Handle", "Item Count", "ACAD File",
    };

    // Column widths from AIT's template, so the sheet opens readable.
    private static readonly double[] FileWidths = { 11, 8.8, 12.8, 12.8, 15, 18.8, 18.8, 11.4, 10.2, 9.3, 12.2, 10.4, 13.3 };
    private static readonly double[] CheckWidths = { 10.6, 10.2, 8, 12.5, 8.2, 16.5, 16, 7.2, 8.1 };
    private static readonly double[] RevitElementWidths = { 10.2, 12.2, 10.5, 8.5, 7.1, 8, 7.8, 8.1, 10.6 };
    private static readonly double[] AcadElementWidths = { 10.2, 11.3, 10.2, 18.8, 15.8, 15.5, 21.2, 13.1, 14, 14, 14, 12.4 };

    /// <summary>
    /// Reads AIT's <c>ReportRun</c> into plain rows — Revit thread only (see the class summary).
    /// Member names are the ones AIT's own exporter reads; <paramref name="totalDisplay"/> is AIT's
    /// <c>Utility.GetTotalDisplay</c>, so the Count column is formatted by AIT itself.
    /// </summary>
    public static List<FileRow> Snapshot(object reportRun, Func<object, string> totalDisplay)
    {
        var files = new List<FileRow>();
        foreach (object file in Enumerate(Get(reportRun, "FileResults")))
        {
            object? checkSet = Get(file, "CheckSet");
            string revitFile = Str(Get(file, "RevitFile"));
            var fileCells = new[]
            {
                Str(Get(file, "DateDisplay")), revitFile, Str(Get(file, "CheckSetFile")),
                Str(checkSet == null ? null : Get(checkSet, "Title")),
                Str(checkSet == null ? null : Get(checkSet, "Author")),
                Str(checkSet == null ? null : Get(checkSet, "Description")),
                Str(Get(file, "OverallResult")),
                Math.Round((double)Get(file, "PassPercent")!, 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Str(Get(file, "PassCount")), Str(Get(file, "FailCount")), Str(Get(file, "ReportCount")),
                Str(Get(file, "ErrorCount")), Str(Get(file, "NotRunCount")),
            };

            var checks = new List<CheckRow>();
            object allChecks = file.GetType().GetMethod("GetAllChecks")!.Invoke(file, null)!;
            foreach (object check in Enumerate(allChecks))
            {
                object result = Get(check, "CheckResult")!;
                string checkId = Str(Get(check, "Id"));
                var checkCells = new[]
                {
                    revitFile, checkId, Str(Get(check, "Name")), Str(Get(check, "Description")),
                    Str(Get(result, "Result")), Str(Get(check, "FailureMessage")),
                    Str(Get(result, "ResultCustomValue")), Str(Get(result, "ErrorMessage")),
                    totalDisplay(Get(result, "ListItems")!),
                };

                var elements = new List<string[]>();
                if ((bool)Get(result, "ShowList")!)
                {
                    // The VIEW, not ListItems: it carries AIT's sort, and the sort is the row order.
                    foreach (object item in Enumerate(Get(result, "ListItemsView")))
                    {
                        if (item.GetType().Name != "ElementDataRevit") continue; // AIT: OfType<ElementDataRevit>()
                        elements.Add(new[]
                        {
                            checkId, Str(Get(item, "ID")), Str(Get(item, "CategoryName")),
                            Str(Get(item, "FamilyName")), Str(Get(item, "TypeName")), Str(Get(item, "Name")),
                            Str(Get(item, "Value")), Str(Get(item, "ItemsCount")), revitFile,
                        });
                    }
                }
                checks.Add(new CheckRow(checkCells, elements));
            }
            files.Add(new FileRow(fileCells, checks));
        }
        return files;
    }

    /// <summary>Writes the workbook. Plain data only — no Revit, no AIT.</summary>
    /// <param name="summarySheet">Put a check-by-model <c>Summary</c> sheet first — for the run
    /// workbook (<see cref="Merge"/>); a one-model report gains nothing from a one-column grid.</param>
    public static void Write(IReadOnlyList<FileRow> files, string path, bool summarySheet = false)
    {
        bool revitElements = files.Any(f => f.Checks.Any(c => c.Elements.Count > 0));
        var book = new XlsxWriter();

        if (summarySheet) WriteSummary(book.AddSheet("Summary"), files);
        XlsxWriter.Sheet fileSheet = book.AddSheet("Files");
        XlsxWriter.Sheet checkSheet = book.AddSheet("Checks");
        XlsxWriter.Sheet elementSheet = book.AddSheet("Elements");

        string[] fileHeaders = (string[])FileHeaders.Clone();
        string[] checkHeaders = (string[])CheckHeaders.Clone();
        if (!revitElements)
        {
            // What AIT does when it falls back to the ACAD layout.
            fileHeaders[1] = "ACAD File";
            checkHeaders[0] = "ACAD File";
        }
        Header(fileSheet, fileHeaders, FileWidths);
        Header(checkSheet, checkHeaders, CheckWidths);
        Header(elementSheet,
            revitElements ? RevitElementHeaders : AcadElementHeaders,
            revitElements ? RevitElementWidths : AcadElementWidths);

        int fileRow = 1, checkRow = 1, elementRow = 1;
        foreach (FileRow file in files)
        {
            Row(fileSheet, fileRow++, file.Cells);
            foreach (CheckRow check in file.Checks)
            {
                Row(checkSheet, checkRow++, check.Cells);
                if (!revitElements) continue;
                foreach (string[] element in check.Elements)
                    Row(elementSheet, elementRow++, element);
            }
        }

        book.Save(path);
    }

    /// <summary>
    /// One workbook for a whole run: every model's Files, Checks and Elements rows in AIT's own
    /// layout — which is what AIT itself writes when it checks several files at once — plus the
    /// Summary grid. Built from the per-model workbooks already on disk, because the process that
    /// knows the run is over (the supervisor of a split run) never held any model's results.
    ///
    /// <para>A model reported twice — a supervisor retry that got as far as writing its report —
    /// keeps its first position and its LATER report: the retry is the run's answer for it. Models
    /// are keyed by Revit File (the full <c>Autodesk Docs://project/model.rvt</c> path, so same-named
    /// models in two projects stay apart) and checkset.</para>
    /// </summary>
    /// <returns>How many models the workbook holds, and the reports that could not be read (named,
    /// so the caller can say which). Nothing is written when no report could be read.</returns>
    public static (int Models, List<string> Unreadable) Merge(IEnumerable<string> reports, string outPath)
    {
        var byModel = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var unreadable = new List<string>();
        foreach (string report in reports)
        {
            List<FileRow> read;
            try { read = Read(report); }
            catch (Exception) { unreadable.Add(Path.GetFileName(report)); continue; }

            foreach (FileRow file in read)
            {
                string key = file.Cells[1] + "|" + file.Cells[2];
                if (!byModel.ContainsKey(key)) order.Add(key);
                byModel[key] = file;
            }
        }

        if (order.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            Write(order.Select(k => byModel[k]).ToList(), outPath, summarySheet: true);
        }
        return (order.Count, unreadable);
    }

    /// <summary>
    /// Reads back a workbook <see cref="Write"/> produced — its inverse, so <see cref="Merge"/> needs
    /// no Revit, document or AIT. Element rows are re-attached to their check by Check ID and Revit
    /// File, which together name one check in one model.
    /// </summary>
    public static List<FileRow> Read(string path)
    {
        IReadOnlyList<XlsxSheet> sheets = XlsxReader.Read(path);
        XlsxSheet files = Sheet(sheets, "Files");
        XlsxSheet checks = Sheet(sheets, "Checks");
        XlsxSheet? elements = sheets.FirstOrDefault(s => s.Name == "Elements");
        bool revitElements = elements != null && elements.Cell(0, 1) == RevitElementHeaders[1];

        var elementsByCheck = new Dictionary<(string CheckId, string RevitFile), List<string[]>>();
        if (revitElements)
        {
            foreach (string[] cells in Rows(elements!, RevitElementHeaders.Length))
            {
                var key = (cells[0], cells[cells.Length - 1]);
                if (!elementsByCheck.TryGetValue(key, out List<string[]>? list))
                    elementsByCheck[key] = list = new List<string[]>();
                list.Add(cells);
            }
        }

        var checksByFile = new Dictionary<string, List<CheckRow>>(StringComparer.Ordinal);
        foreach (string[] cells in Rows(checks, CheckHeaders.Length))
        {
            var key = (cells[1], cells[0]);
            // Taken, not looked up: a check ID listed twice must not show its elements twice.
            if (elementsByCheck.TryGetValue(key, out List<string[]>? mine)) elementsByCheck.Remove(key);
            if (!checksByFile.TryGetValue(cells[0], out List<CheckRow>? list))
                checksByFile[cells[0]] = list = new List<CheckRow>();
            list.Add(new CheckRow(cells, mine ?? new List<string[]>()));
        }

        return Rows(files, FileHeaders.Length)
            .Select(cells => new FileRow(cells,
                checksByFile.TryGetValue(cells[1], out List<CheckRow>? list) ? list : new List<CheckRow>()))
            .ToList();
    }

    /// <summary>
    /// The grid the per-model workbooks can't give: one row per check, one column per model, each
    /// cell that check's result in that model — so "is this one file or the whole project" is one
    /// glance. Each model's overall result and pass percent sit above the checks. Checks are listed
    /// in first-seen order; a check missing from a model's checkset is left blank, never a pass.
    /// </summary>
    private static void WriteSummary(XlsxWriter.Sheet sheet, IReadOnlyList<FileRow> files)
    {
        bool severalChecksets = files.Select(f => f.Cells[2])
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        sheet.WriteString(0, 0, "Check", Style.HeaderPlain);
        sheet.WriteString(0, 1, "Check ID", Style.HeaderPlain);
        sheet.WriteString(1, 0, "Overall Result", Style.TotalLabel);
        sheet.WriteString(2, 0, "Pass Percent", Style.TotalLabel);
        sheet.SetColumnWidth(0, 40);
        sheet.SetColumnWidth(1, 12);

        var rowOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int nextRow = 3;
        for (int f = 0; f < files.Count; f++)
        {
            int col = 2 + f;
            string[] file = files[f].Cells;
            string name = ModelName(file[1]);
            if (severalChecksets) name += " — " + file[3];
            sheet.WriteString(0, col, name, Style.Header);
            sheet.WriteString(1, col, file[6], Style.TotalLabel);
            sheet.WriteString(2, col, file[7].Length > 0 ? file[7] + "%" : "", Style.TotalLabel);
            sheet.SetColumnWidth(col, 9);

            foreach (CheckRow check in files[f].Checks)
            {
                string id = check.Cells[1];
                if (!rowOf.TryGetValue(id, out int row))
                {
                    rowOf[id] = row = nextRow++;
                    sheet.WriteString(row, 0, check.Cells[2], Style.RowLabel);
                    sheet.WriteString(row, 1, id, Style.RowLabel);
                }
                sheet.WriteString(row, col, check.Cells[4], Style.RowLabel);
            }
        }
        sheet.FreezeTopRows(3);
    }

    /// <summary>"Autodesk Docs://SAMPLE-PROJECT/SAMPLE-ARC-SITE-0001.rvt" → "SAMPLE-ARC-SITE-0001.rvt": the
    /// column header has a model's width, and the full path is one sheet over in Files.</summary>
    private static string ModelName(string revitFile)
    {
        int cut = revitFile.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 ? revitFile.Substring(cut + 1) : revitFile;
    }

    private static XlsxSheet Sheet(IReadOnlyList<XlsxSheet> sheets, string name) =>
        sheets.FirstOrDefault(s => s.Name == name)
        ?? throw new InvalidDataException($"Not a Batch Interop report: no '{name}' sheet.");

    /// <summary>Every data row (below the header) as exactly <paramref name="width"/> cells — the
    /// writer skips empty cells, so the reader pads them back — skipping wholly empty rows.</summary>
    private static IEnumerable<string[]> Rows(XlsxSheet sheet, int width)
    {
        for (int r = 1; r <= sheet.MaxRow; r++)
        {
            var cells = new string[width];
            for (int c = 0; c < width; c++) cells[c] = sheet.Cell(r, c);
            if (cells.Any(v => v.Length > 0)) yield return cells;
        }
    }

    private static void Header(XlsxWriter.Sheet sheet, string[] headers, double[] widths)
    {
        for (int c = 0; c < headers.Length; c++)
        {
            sheet.WriteString(0, c, headers[c], Style.HeaderPlain);
            sheet.SetColumnWidth(c, widths[c]);
        }
    }

    private static void Row(XlsxWriter.Sheet sheet, int row, string[] cells)
    {
        for (int c = 0; c < cells.Length; c++)
            if (cells[c].Length > 0) sheet.WriteString(row, c, cells[c], Style.Default);
    }

    /// <summary>Most-derived public property of that name. Walked by hand rather than
    /// <c>GetProperty(name)</c>, which throws AmbiguousMatchException when a type hides a base
    /// property with <c>new</c> — and AIT's data model is a deep hierarchy we don't control.</summary>
    private static object? Get(object target, string property)
    {
        for (Type? t = target.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(property, System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            if (p != null) return p.GetValue(target);
        }
        throw new MissingMemberException(target.GetType().FullName, property);
    }

    private static string Str(object? value) => value?.ToString() ?? "";

    private static IEnumerable<object> Enumerate(object? collection) =>
        collection is System.Collections.IEnumerable e ? e.Cast<object>() : Enumerable.Empty<object>();
}
