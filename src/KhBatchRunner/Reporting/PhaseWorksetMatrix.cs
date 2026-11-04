using S = KhBatchRunner.Reporting.XlsxWriter.XlsxStyle;

namespace KhBatchRunner.Reporting;

/// <summary>
/// Builds the Phase &amp; Workset matrix workbook (a native port of the team's Colab "BIM Phase &amp;
/// Workset Report Generator"): two sheets, "Phase Report" and "Workset Report". Each pivots the run's
/// detail rows into <b>rows = phase/workset name × columns = model</b>, cell = presence (a green block
/// where the model has that phase/workset, empty otherwise; the count is hidden), with a Grand Total
/// row (per-model counts) and Grand Total column (per-name counts). Model headers are rotated 90°.
/// </summary>
public static class PhaseWorksetMatrix
{
    /// <summary>Writes the workbook. Rows are <c>(Model, Category, Name)</c> where Category is
    /// "Phase" or "Workset". Returns false (writes nothing) if there's no phase and no workset data.
    ///
    /// <para>BOTH sheets are always present. They used to be added only when their half had rows,
    /// which made a one-tab workbook mean two different things — "no model was workshared" and
    /// "the sheet was written and Excel threw it away during repair" — and a real report of the
    /// second was read as the first. An empty half now says so on the sheet, so a MISSING tab can
    /// only ever mean the writer failed.</para></summary>
    public static bool Write(string path, IReadOnlyList<(string Model, string Category, string Name)> rows)
    {
        var phase = rows.Where(r => r.Category.Equals("Phase", StringComparison.OrdinalIgnoreCase)).ToList();
        var workset = rows.Where(r => r.Category.Equals("Workset", StringComparison.OrdinalIgnoreCase)).ToList();
        if (phase.Count == 0 && workset.Count == 0) return false;

        var wb = new XlsxWriter();
        BuildOrExplain(wb.AddSheet("Phase Report"), "Phase", phase,
            "No phases were recorded for this run.");
        BuildOrExplain(wb.AddSheet("Workset Report"), "Workset", workset,
            "No worksets were recorded for this run — no model reported as workshared.");
        wb.Save(path);
        return true;
    }

    private static void BuildOrExplain(
        XlsxWriter.Sheet sheet, string indexLabel,
        List<(string Model, string Category, string Name)> rows, string whenEmpty)
    {
        if (rows.Count > 0) { BuildSheet(sheet, indexLabel, rows); return; }
        sheet.SetColumnWidth(0, 80);
        sheet.WriteString(0, 0, whenEmpty, S.RowLabel);
    }

    private static void BuildSheet(
        XlsxWriter.Sheet sheet, string indexLabel,
        List<(string Model, string Category, string Name)> rows)
    {
        List<string> names = rows.Select(r => r.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        List<string> models = rows.Select(r => r.Model).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

        // count[name][model]
        var count = new Dictionary<string, Dictionary<string, int>>();
        foreach (string n in names) count[n] = models.ToDictionary(m => m, _ => 0);
        foreach (var r in rows)
            if (count.TryGetValue(r.Name, out var byModel) && byModel.ContainsKey(r.Model))
                byModel[r.Model]++;

        int totalCol = models.Count + 1; // 0 = labels, 1..M = models, M+1 = Grand Total

        // Header row.
        sheet.SetRowHeight(0, 120);
        sheet.SetColumnWidth(0, 30);
        sheet.WriteString(0, 0, indexLabel, S.TotalLabel);
        for (int c = 0; c < models.Count; c++)
        {
            sheet.WriteString(0, c + 1, models[c], S.Header);
            sheet.SetColumnWidth(c + 1, 4);
        }
        sheet.WriteString(0, totalCol, "Grand Total", S.Header);
        sheet.SetColumnWidth(totalCol, 12);

        // Data rows.
        var colTotals = models.ToDictionary(m => m, _ => 0);
        int grand = 0;
        for (int i = 0; i < names.Count; i++)
        {
            int row = i + 1;
            sheet.WriteString(row, 0, names[i], S.RowLabel);
            int rowTotal = 0;
            for (int c = 0; c < models.Count; c++)
            {
                int v = count[names[i]][models[c]];
                if (v > 0) sheet.WriteNumber(row, c + 1, v, S.PresentBlock);
                else sheet.WriteBlank(row, c + 1, S.Empty);
                rowTotal += v;
                colTotals[models[c]] += v;
            }
            sheet.WriteNumber(row, totalCol, rowTotal, S.TotalNumber);
            grand += rowTotal;
        }

        // Grand Total row.
        int totalRow = names.Count + 1;
        sheet.WriteString(totalRow, 0, "Grand Total", S.TotalLabel);
        for (int c = 0; c < models.Count; c++)
            sheet.WriteNumber(totalRow, c + 1, colTotals[models[c]], S.TotalNumber);
        sheet.WriteNumber(totalRow, totalCol, grand, S.TotalNumber);
    }
}
