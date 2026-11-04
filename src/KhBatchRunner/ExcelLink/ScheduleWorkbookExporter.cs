using Autodesk.Revit.DB;
using KhBatchRunner.Reporting;

namespace KhBatchRunner.ExcelLink;

/// <summary>
/// Excel Link — export: writes the selected schedules of the ACTIVE document into one workbook,
/// one worksheet per schedule, via the dependency-free <see cref="XlsxWriter"/>. Sheet layout
/// (the contract <see cref="ScheduleXlsxImporter"/> validates on the way back):
/// <list type="bullet">
/// <item>Row 1 — legend: per column "Instance (may edit)" / "Type (cannot edit)" /
/// "Read-only (cannot edit)" (see <see cref="ScheduleFieldClassifier"/>).</item>
/// <item>Row 2 — headers: "ElementId", then each visible field's column heading.</item>
/// <item>Both rows frozen; non-editable columns grey (NO sheet protection — Excel stays fully
/// sortable/filterable; the import simply never writes non-Instance columns).</item>
/// <item>Column A — the element id (hidden, the import's row → element key).</item>
/// </list>
/// One row per ELEMENT (<see cref="FilteredElementCollector"/> scoped to the schedule view),
/// whatever the schedule's itemize/grouping settings — grouped display rows can't map 1:1 back to
/// elements, and the workbook exists to write values back. Read-only pass; opens no transaction.
/// </summary>
public static class ScheduleWorkbookExporter
{
    /// <summary>Why a schedule can't be exported, or null when it can. Shown greyed in the picker.</summary>
    public static string? UnsupportedReason(ViewSchedule schedule)
    {
        if (schedule.IsTemplate) return "view template";
        if (schedule.Definition.IsKeySchedule) return "key schedule — rows are keys, not elements";
        if (schedule.IsTitleblockRevisionSchedule) return "titleblock revision schedule";
        if (schedule.IsInternalKeynoteSchedule) return "keynote legend";
        return null;
    }

    /// <summary>All schedules of the document (name-sorted) with their unsupported reason (null = exportable).</summary>
    public static List<(ViewSchedule Schedule, string? UnsupportedReason)> ListSchedules(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => (v, UnsupportedReason(v)))
            .ToList();

    /// <summary>Exports <paramref name="schedules"/> into one workbook at <paramref name="path"/>.
    /// Returns per-schedule (sheet name, element-row count) for the summary.</summary>
    public static List<(string SheetName, int Rows)> Export(
        Document doc, IReadOnlyList<ViewSchedule> schedules, string path)
    {
        var writer = new XlsxWriter();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summary = new List<(string, int)>();

        for (int i = 0; i < schedules.Count; i++)
        {
            ViewSchedule vs = schedules[i];
            string name = UniqueSheetName(vs.Name, i, used);
            int rows = WriteSheet(writer.AddSheet(name), doc, vs);
            summary.Add((name, rows));
        }

        writer.Save(path);
        return summary;
    }

    /// <summary>The sheet name a schedule maps to — sanitized like the writer, then de-collided
    /// with " (n)" inside Excel's 31-char limit. The import re-derives the same base sanitation.</summary>
    internal static string UniqueSheetName(string scheduleName, int index, HashSet<string> used)
    {
        string baseName = XlsxWriter.SanitizeSheetName(scheduleName, index);
        string name = baseName;
        int n = 2;
        while (!used.Add(name))
        {
            string suffix = $" ({n++})";
            name = (baseName.Length + suffix.Length > 31 ? baseName[..(31 - suffix.Length)] : baseName) + suffix;
        }
        return name;
    }

    private static int WriteSheet(XlsxWriter.Sheet sheet, Document doc, ViewSchedule vs)
    {
        List<Element> elements = new FilteredElementCollector(doc, vs.Id).ToElements().ToList();
        List<ScheduleColumn> columns = ScheduleFieldClassifier.Classify(doc, vs, elements);

        // Row 0: legend. Row 1: headers. Column 0: the hidden ElementId key.
        sheet.WriteString(0, 0, ExcelLinkFormat.LegendReadOnly, XlsxWriter.XlsxStyle.LegendLocked);
        sheet.WriteString(1, 0, ExcelLinkFormat.HeaderElementId, XlsxWriter.XlsxStyle.HeaderPlain);
        for (int c = 0; c < columns.Count; c++)
        {
            ScheduleColumn col = columns[c];
            sheet.WriteString(0, c + 1, col.LegendText,
                col.Kind == ColumnKind.Instance
                    ? XlsxWriter.XlsxStyle.LegendInstance
                    : XlsxWriter.XlsxStyle.LegendLocked);
            sheet.WriteString(1, c + 1, col.Heading, XlsxWriter.XlsxStyle.HeaderPlain);
            sheet.SetColumnWidth(c + 1, Math.Max(14, Math.Min(40, col.Heading.Length + 6)));
        }
        sheet.SetColumnWidth(0, 12);
        sheet.HideColumn(0);
        sheet.FreezeTopRows(2);

        int r = 2;
        foreach (Element e in elements)
        {
            sheet.WriteString(r, 0, e.Id.Value.ToString(), XlsxWriter.XlsxStyle.CellLocked);
            for (int c = 0; c < columns.Count; c++)
            {
                ScheduleColumn col = columns[c];
                Parameter? p = ScheduleFieldClassifier.ResolveFor(doc, e, col);
                string text = p is null ? "" : ScheduleFieldClassifier.Format(p);
                sheet.WriteString(r, c + 1, text,
                    col.Kind == ColumnKind.Instance ? XlsxWriter.XlsxStyle.Empty : XlsxWriter.XlsxStyle.CellLocked);
            }
            r++;
        }

        // Auto-filter the header row + all data rows (r - 1 is the last row written; with no data
        // that's the header row itself — a harmless one-row range). The legend row 0 is excluded so
        // its text never turns into a filter dropdown; hidden col A is kept in to stay contiguous.
        sheet.SetAutoFilter(1, 0, r - 1, columns.Count);
        return elements.Count;
    }
}
