using S = KhBatchRunner.Reporting.XlsxWriter.XlsxStyle;

namespace KhBatchRunner.Reporting;

/// <summary>
/// Builds the Design Option matrix workbook (a native port of the team's Colab "BIM Design Option
/// Matrix"): one sheet, <b>rows = a compact Design Set → Design Option hierarchy × columns = model</b>,
/// value = whether that option is <b>primary</b> in the model (a dark-green block where primary). A
/// group row (<c>⊟ Set</c>) precedes each set's options; a Grand Total row shows the per-model primary
/// count. The <c>*No Design Option</c> rows are treated as primary (highlighted) even when their flag
/// is 0 — but the Grand Total uses the TRUE primary counts computed before that override.
/// </summary>
public static class DesignOptionMatrix
{
    /// <summary>Writes the workbook. Rows are <c>(Model, Set, Option, IsPrimary)</c>. Returns false
    /// (writes nothing) if there are no rows.</summary>
    public static bool Write(string path, IReadOnlyList<(string Model, string Set, string Option, bool IsPrimary)> rows)
    {
        if (rows.Count == 0) return false;

        List<string> models = rows.Select(r => r.Model).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

        // True per-model primary totals, computed BEFORE the *No Design Option override.
        var trueTotals = models.ToDictionary(m => m, _ => 0);
        foreach (var r in rows)
            if (r.IsPrimary) trueTotals[r.Model]++;

        // value[(set,option)][model] = Σ primary, after the *No Design Option override.
        var value = new Dictionary<(string Set, string Option), Dictionary<string, int>>();
        foreach (var r in rows)
        {
            var key = (r.Set, r.Option);
            if (!value.TryGetValue(key, out var byModel))
                value[key] = byModel = models.ToDictionary(m => m, _ => 0);
            int prim = r.IsPrimary ? 1 : 0;
            if (prim == 0 && r.Option.IndexOf("*No Design Option", StringComparison.OrdinalIgnoreCase) >= 0)
                prim = 1;
            byModel[r.Model] += prim;
        }

        // Ordered Set → [Options].
        var sets = value.Keys.Select(k => k.Set).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        var wb = new XlsxWriter();
        XlsxWriter.Sheet sheet = wb.AddSheet("Design Options");

        // Header row. Column 0 = labels; columns 1..M = models (no grand-total COLUMN in this matrix).
        sheet.SetRowHeight(0, 140);
        sheet.WriteString(0, 0, "Sum of Primary\nRow Labels", S.TopLeftTitle);
        for (int c = 0; c < models.Count; c++)
        {
            sheet.WriteString(0, c + 1, models[c], S.HeaderBlue);
            sheet.SetColumnWidth(c + 1, 3);
        }

        var labels = new List<string> { "Sum of Primary Row Labels", "Grand Total" };
        int row = 1;
        foreach (string set in sets)
        {
            string groupLabel = string.IsNullOrWhiteSpace(set) ? "⊟ (Blank Set)" : $"⊟ {set}";
            labels.Add(groupLabel);
            sheet.WriteString(row, 0, groupLabel, S.GroupHeader);
            for (int c = 0; c < models.Count; c++) sheet.WriteBlank(row, c + 1, S.GroupHeader);
            row++;

            var options = value.Keys.Where(k => k.Set == set).Select(k => k.Option)
                .Distinct().OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string opt in options)
            {
                string optLabel = string.IsNullOrWhiteSpace(opt) ? "    (Blank Option)" : $"    {opt}";
                labels.Add(optLabel);
                sheet.WriteString(row, 0, optLabel, S.RowLabel);
                var byModel = value[(set, opt)];
                for (int c = 0; c < models.Count; c++)
                {
                    int v = byModel[models[c]];
                    if (v > 0) sheet.WriteNumber(row, c + 1, v, S.PrimaryBlock);
                    else sheet.WriteBlank(row, c + 1, S.Empty);
                }
                row++;
            }
        }

        // Grand Total row (TRUE primary counts, shown as visible numbers).
        sheet.WriteString(row, 0, "Grand Total", S.DesignTotalLabel);
        for (int c = 0; c < models.Count; c++)
        {
            int v = trueTotals[models[c]];
            if (v > 0) sheet.WriteNumber(row, c + 1, v, S.DesignTotalNumber);
            else sheet.WriteBlank(row, c + 1, S.DesignTotalNumber);
        }

        sheet.SetColumnWidth(0, Math.Max(25, labels.Max(l => l.Length) + 2));
        wb.Save(path);
        return true;
    }
}
