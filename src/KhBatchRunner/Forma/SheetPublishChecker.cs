namespace KhBatchRunner.Forma;

/// <summary>
/// Runs the Check Sheet Publish report: every <c>.rvt</c> across the folder set is fetched once,
/// then each expected sheet number is looked up in the models' published (translated) 2D sheet
/// views. A sheet is "published" when any 2D viewable name <b>contains</b> the sheet number
/// (case-insensitive) — Revit's published sheet viewables are typically named
/// "&lt;number&gt; - &lt;title&gt;". Output rows: one "Yes" row per model the sheet was found in;
/// one "Not found in any Models" row for a sheet found nowhere; one trailing "Model not
/// processed" row per model with no completed translation. Pure REST (Data Management + Model
/// Derivative); opens no model.
/// </summary>
public static class SheetPublishChecker
{
    public static List<SheetPublishRow> Run(
        DataManagementClient dm, ModelDerivativeClient md,
        IReadOnlyList<FolderRef> folders, IReadOnlyList<string> sheetNumbers, bool includeSubfolders)
    {
        // Collect the models once across the whole folder set — overlapping folders (or
        // subfolder recursion) can surface the same model twice, which would double-report
        // matches — and fetch each model's published sheet views once.
        var models = new List<(string Name, IReadOnlyList<string> Views)>();
        var unprocessed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderRef fr in folders)
        {
            foreach (FormaModelRef m in dm.GetRevitItemRefs(fr.ToEntry(), includeSubfolders))
            {
                if (!seen.Add(m.VersionUrn)) continue;
                IReadOnlyList<string>? sheetViews = md.GetPublishedSheetViewNames(m.VersionUrn, m.Region);
                if (sheetViews is null) unprocessed.Add(m.DisplayName);
                else models.Add((m.DisplayName, sheetViews));
            }
        }

        // No models at all — return empty so the caller's "no .rvt models found" path fires
        // instead of a report of all-not-found rows.
        if (models.Count == 0 && unprocessed.Count == 0) return new List<SheetPublishRow>();

        var rows = new List<SheetPublishRow>();
        foreach (string sn in sheetNumbers)
        {
            bool found = false;
            foreach ((string name, IReadOnlyList<string> views) in models)
            {
                if (!views.Any(v => v.IndexOf(sn, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                found = true;
                rows.Add(new SheetPublishRow { ModelName = name, SheetNumber = sn, IsMatch = "Yes" });
            }
            if (!found)
                rows.Add(new SheetPublishRow
                {
                    ModelName = SheetPublishRow.NotFoundModel,
                    SheetNumber = sn,
                    IsMatch = "No",
                });
        }

        foreach (string name in unprocessed)
            rows.Add(new SheetPublishRow { ModelName = name, IsMatch = SheetPublishRow.NotProcessed });

        return rows;
    }
}
