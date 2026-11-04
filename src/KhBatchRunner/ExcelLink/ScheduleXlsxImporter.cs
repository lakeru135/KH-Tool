using Autodesk.Revit.DB;
using KhBatchRunner.Reporting;

namespace KhBatchRunner.ExcelLink;

/// <summary>One pending cell write found by the analysis pass.</summary>
public sealed class ExcelLinkChange
{
    public required string SheetName { get; init; }
    public required ElementId ElementId { get; init; }
    public required ScheduleColumn Column { get; init; }
    public required string OldValue { get; init; }
    public required string NewValue { get; init; }
}

/// <summary>What <see cref="ScheduleXlsxImporter.Analyze"/> found — pending changes plus every
/// per-sheet/per-cell issue as a ready-made result row. Nothing has been written yet.</summary>
public sealed class ExcelLinkPlan
{
    public List<ExcelLinkChange> Changes { get; } = new();
    public List<IReadOnlyDictionary<string, string>> Issues { get; } = new();
    public List<string> SheetSummaries { get; } = new();
    public int SheetsMatched { get; set; }
}

/// <summary>
/// Excel Link — import: reads a workbook the exporter wrote (and the user edited/saved in Excel),
/// matches each worksheet back to its schedule by name, validates the sheet shape (legend
/// vocabulary, "ElementId" key column, headers equal to the schedule's CURRENT visible fields in
/// order — a changed schedule rejects its sheet, expected-vs-found, like
/// <see cref="MatrixCsvImport"/>), then re-classifies the live schedule and writes back ONLY cells
/// that changed in editable Instance columns. Edits to Type/Read-only columns are ignored and
/// reported per cell. Two phases: <see cref="Analyze"/> (read-only — feeds the confirm dialog) and
/// <see cref="Apply"/> (ONE transaction for the whole run, so a single Ctrl+Z reverts the import;
/// never saves/syncs — the active-model convention).
/// </summary>
public static class ScheduleXlsxImporter
{
    public static readonly IReadOnlyList<string> ResultColumns =
        new[] { "Schedule", "Element Id", "Column", "Old Value", "New Value", "Result" };

    // ---- phase 1: read + validate + diff (no writes) ---------------------------------------

    public static ExcelLinkPlan Analyze(Document doc, string path)
    {
        var plan = new ExcelLinkPlan();
        IReadOnlyList<XlsxSheet> sheets = XlsxReader.Read(path);

        Dictionary<string, ViewSchedule?> byName = MapSchedulesBySheetName(doc);

        foreach (XlsxSheet sheet in sheets)
        {
            if (!byName.TryGetValue(sheet.Name.Trim(), out ViewSchedule? schedule))
            {
                plan.Issues.Add(Row(sheet.Name, "", "", "", "",
                    "no schedule with this name in the model — sheet skipped"));
                continue;
            }
            if (schedule is null)
            {
                plan.Issues.Add(Row(sheet.Name, "", "", "", "",
                    "several schedules map to this sheet name — sheet skipped"));
                continue;
            }
            AnalyzeSheet(doc, schedule, sheet, plan);
        }
        return plan;
    }

    /// <summary>Sheet name → schedule, using the EXACT same name derivation the export used
    /// (<see cref="ScheduleWorkbookExporter.UniqueSheetName"/> over the supported schedules in the
    /// same deterministic order), so a truncation/de-collision that produced "Foo" and "Foo (2)" on
    /// export maps back the same way. A key that would still resolve to two schedules maps to null.</summary>
    private static Dictionary<string, ViewSchedule?> MapSchedulesBySheetName(Document doc)
    {
        var map = new Dictionary<string, ViewSchedule?>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach ((ViewSchedule vs, string? unsupported) in ScheduleWorkbookExporter.ListSchedules(doc))
        {
            if (unsupported != null) continue;
            string key = ScheduleWorkbookExporter.UniqueSheetName(vs.Name, i++, used).Trim();
            map[key] = map.ContainsKey(key) ? null : vs;
        }
        return map;
    }

    private static void AnalyzeSheet(Document doc, ViewSchedule schedule, XlsxSheet sheet, ExcelLinkPlan plan)
    {
        // Shape check 1: an Excel Link sheet has the legend vocabulary in row 1 and the key header.
        if (!sheet.Cell(1, 0).Trim().Equals(ExcelLinkFormat.HeaderElementId, StringComparison.OrdinalIgnoreCase))
        {
            plan.Issues.Add(Row(sheet.Name, "", "", "", "",
                $"not an Excel Link sheet (cell A2 is not '{ExcelLinkFormat.HeaderElementId}') — sheet skipped"));
            return;
        }
        var legendVocabulary = new[]
            { ExcelLinkFormat.LegendInstance, ExcelLinkFormat.LegendType, ExcelLinkFormat.LegendReadOnly };
        for (int c = 0; c <= sheet.MaxCol; c++)
        {
            string legend = sheet.Cell(0, c).Trim();
            if (legend.Length > 0 && !legendVocabulary.Contains(legend, StringComparer.OrdinalIgnoreCase))
            {
                plan.Issues.Add(Row(sheet.Name, "", "", "", "",
                    $"unexpected legend text '{legend}' in row 1 — sheet skipped"));
                return;
            }
        }

        // Shape check 2: headers must equal the schedule's CURRENT visible fields, in order — the
        // live classification is what decides writability, so a schedule edited since the export
        // (fields added/removed/reordered) rejects its sheet instead of writing the wrong columns.
        List<Element> elements = new FilteredElementCollector(doc, schedule.Id).ToElements().ToList();
        List<ScheduleColumn> columns = ScheduleFieldClassifier.Classify(doc, schedule, elements);

        var found = new List<string>();
        for (int c = 1; c <= sheet.MaxCol; c++) found.Add(sheet.Cell(1, c).Trim());
        while (found.Count > 0 && found[^1].Length == 0) found.RemoveAt(found.Count - 1);
        var expected = columns.Select(col => col.Heading.Trim()).ToList();
        if (!found.SequenceEqual(expected, StringComparer.Ordinal))
        {
            plan.Issues.Add(Row(sheet.Name, "", "", "", "",
                "the schedule's columns changed since this sheet was exported — sheet skipped. " +
                $"Expected: {string.Join(" | ", expected)}. Found: {string.Join(" | ", found)}"));
            return;
        }

        plan.SheetsMatched++;
        var memberIds = new HashSet<ElementId>(elements.Select(e => e.Id));
        int changes = 0, ignored = 0, missing = 0;

        for (int r = 2; r <= sheet.MaxRow; r++)
        {
            string idText = sheet.Cell(r, 0).Trim();
            if (idText.Length == 0)
            {
                // A fully blank row (Excel padding) is fine; content without a key is not.
                bool any = false;
                for (int c = 1; c <= sheet.MaxCol && !any; c++) any = sheet.Cell(r, c).Length > 0;
                if (any)
                    plan.Issues.Add(Row(sheet.Name, "", "", "", "",
                        $"row {r + 1} has values but no ElementId (added rows can't be imported) — row skipped"));
                continue;
            }
            if (!long.TryParse(idText, out long idValue))
            {
                plan.Issues.Add(Row(sheet.Name, idText, "", "", "", $"row {r + 1}: ElementId is not a number — row skipped"));
                continue;
            }
            var id = new ElementId(idValue);
            Element? element = doc.GetElement(id);
            if (element is null)
            {
                missing++;
                plan.Issues.Add(Row(sheet.Name, idText, "", "", "", "element no longer exists — row skipped"));
                continue;
            }
            if (!memberIds.Contains(id))
            {
                plan.Issues.Add(Row(sheet.Name, idText, "", "", "",
                    "element is no longer part of this schedule — row skipped"));
                continue;
            }

            for (int c = 0; c < columns.Count; c++)
            {
                ScheduleColumn column = columns[c];
                string text = sheet.Cell(r, c + 1);
                Parameter? p = ScheduleFieldClassifier.ResolveFor(doc, element, column);
                string current = p is null ? "" : ScheduleFieldClassifier.Format(p);
                if (text == current) continue; // unchanged — the overwhelmingly common case

                if (column.Kind != ColumnKind.Instance)
                {
                    ignored++;
                    plan.Issues.Add(Row(sheet.Name, idText, column.Heading, current, text,
                        $"column is {column.LegendText} — change ignored"));
                    continue;
                }
                if (p is null)
                {
                    plan.Issues.Add(Row(sheet.Name, idText, column.Heading, "", text,
                        "parameter not present on this element — change ignored"));
                    continue;
                }
                changes++;
                plan.Changes.Add(new ExcelLinkChange
                {
                    SheetName = sheet.Name,
                    ElementId = id,
                    Column = column,
                    OldValue = current,
                    NewValue = text,
                });
            }
        }

        plan.SheetSummaries.Add(
            $"{sheet.Name} → {schedule.Name}: {changes} change(s)" +
            (ignored > 0 ? $", {ignored} locked-cell edit(s) ignored" : "") +
            (missing > 0 ? $", {missing} missing element(s)" : ""));
    }

    // ---- phase 2: write (one transaction for the whole run) --------------------------------

    /// <summary>Applies the plan's changes. Returns per-change result rows; sets
    /// <paramref name="applied"/> to how many writes landed. No-op (and no transaction) when the
    /// plan has no changes.</summary>
    public static List<IReadOnlyDictionary<string, string>> Apply(
        Document doc, ExcelLinkPlan plan, out int applied)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        applied = 0;
        if (plan.Changes.Count == 0) return rows;

        using var t = new Transaction(doc, "Excel Link import");
        t.Start();
        foreach (ExcelLinkChange change in plan.Changes)
        {
            string idText = change.ElementId.Value.ToString();
            Element? element = doc.GetElement(change.ElementId);
            if (element is null)
            {
                rows.Add(Row(change.SheetName, idText, change.Column.Heading, change.OldValue, change.NewValue,
                    "ERROR: element vanished mid-run"));
                continue;
            }
            Parameter? p = ScheduleFieldClassifier.ResolveFor(doc, element, change.Column);
            if (p is null || p.IsReadOnly)
            {
                rows.Add(Row(change.SheetName, idText, change.Column.Heading, change.OldValue, change.NewValue,
                    "skipped — parameter is read-only on this element"));
                continue;
            }
            if (ScheduleFieldClassifier.TryApply(p, change.NewValue, out string error))
            {
                applied++;
                rows.Add(Row(change.SheetName, idText, change.Column.Heading, change.OldValue, change.NewValue,
                    "updated"));
            }
            else
            {
                rows.Add(Row(change.SheetName, idText, change.Column.Heading, change.OldValue, change.NewValue,
                    "ERROR: " + error));
            }
        }
        t.Commit();
        return rows;
    }

    private static IReadOnlyDictionary<string, string> Row(
        string schedule, string elementId, string column, string oldValue, string newValue, string result) =>
        new Dictionary<string, string>
        {
            ["Schedule"] = schedule,
            ["Element Id"] = elementId,
            ["Column"] = column,
            ["Old Value"] = oldValue,
            ["New Value"] = newValue,
            ["Result"] = result,
        };
}
