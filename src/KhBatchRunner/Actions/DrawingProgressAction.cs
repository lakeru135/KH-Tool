using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

/// <summary>
/// Multi Model Check: report every sheet in the model — one detail row per
/// <see cref="ViewSheet"/> with its number, name, current revision (sequence, date and
/// description), whether it appears in the sheet list, and whether it's a placeholder. The row
/// then carries the same annotation/publish-set figures the office Dynamo "Sheet Annotation And
/// Publish Set Export" produced (placed-view and annotation counts, plus which cloud Publish
/// Settings print sets the sheet is ticked / unticked for) so that script is no longer needed.
/// Placeholder sheets are included (they're real <see cref="ViewSheet"/> elements) but have no
/// placed views or publish-set membership. Read-only — no transaction needed.
///
/// <para>NO MATRIX AT RUN END since 1.3.8.1. This check produced the largest of the four automatic
/// matrices and was the only one that could not be rebuilt from its own CSV afterwards; the sheet
/// data lives in the detail CSV, which is what the Power BI side reads anyway. Only Phase &amp;
/// Workset and Design Option still pivot themselves at run end.</para>
/// </summary>
public sealed class DrawingProgressAction : BatchActionBase
{
    public override string Name => "Drawing Progress";

    public override WorksetPosture Worksets => WorksetPosture.ReadsNoInstances;

    public override string WorksetPostureReason =>
        "Collects ViewSheet and counts ANNOTATION-category elements per view. Sheets and views are " +
        "not in user worksets, and annotation is owned by its view — so CountAnnotations returns " +
        "the same number open or closed. It never counts model elements, which is exactly why it " +
        "is unaffected. Audited 2026-08-16 and CLEARED.";

    public override IReadOnlyList<string> Columns { get; } =
        new[]
        {
            // Original per-sheet columns (kept as-is).
            "Sheet Number", "Sheet Name", "Current Revision", "Revision Date", "Revision Description", "In Sheet List", "Placeholder",
            // Ported from the Dynamo script — names must match its headers exactly for Power BI.
            "Total Views Placed", "Total Annotate Elements on Views",
            "Sheet Publish Sets - True", "Sheet Publish Sets - False",
            "Count Sheet Publish Sets - True", "Count Sheet Publish Sets - False",
            "Total Sheet Publish Sets",
        };

    private string _perModelNote = "";

    public override string PerModelNote => _perModelNote;

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        ModelScan scan = ScanModel(doc);
        return new Dictionary<string, string>
        {
            ["Sheet Count"] = scan.Sheets.Count.ToString(),
            ["With Revision"] = scan.Sheets.Count(s => s.Revision.Length > 0).ToString(),
            ["In Sheet List"] = scan.Sheets.Count(s => s.InSheetList == "Yes").ToString(),
            ["Placeholders"] = scan.Sheets.Count(s => s.Placeholder).ToString(),
            ["Publish Sets"] = scan.Publish.ViewSheetSetCount.ToString(),
            ["Sheets In Publish Set"] = scan.Sheets.Count(s => s.TrueSets.Count > 0).ToString(),
            ["Publish Settings"] = scan.Publish.Found ? "Found" : "Not found",
        };
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc)
    {
        ModelScan scan = ScanModel(doc);
        List<SheetRow> sheets = scan.Sheets;
        int placeholders = sheets.Count(s => s.Placeholder);

        // Per-model roll-up: the existing sheet note, plus a flag when the cloud Publish Settings
        // storage was missing/unreadable — so the blank True/False columns read as "state unknown"
        // rather than "no sheet is in any set".
        var noteParts = new List<string>();
        if (sheets.Count == 0) noteParts.Add("no sheets");
        else if (placeholders > 0) noteParts.Add($"{placeholders} placeholder sheet(s)");
        if (sheets.Count > 0 && !scan.Publish.Found) noteParts.Add(scan.Publish.Note);
        noteParts.Add(Timing(scan));   // temporary — see Timing
        _perModelNote = string.Join("; ", noteParts);

        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (SheetRow sheet in sheets)
        {
            int countTrue = sheet.TrueSets.Count;
            int countFalse = sheet.FalseSets.Count;
            int totalPublishSets = countTrue + countFalse;

            // Match the Dynamo blank rules: no memberships => every publish cell empty; otherwise a
            // name list / count shows only when non-empty, and the total always shows.
            string trueText = "", falseText = "", countTrueText = "", countFalseText = "", totalText = "";
            if (totalPublishSets > 0)
            {
                trueText = countTrue > 0 ? string.Join(" | ", sheet.TrueSets) : "";
                falseText = countFalse > 0 ? string.Join(" | ", sheet.FalseSets) : "";
                countTrueText = countTrue > 0 ? countTrue.ToString() : "";
                countFalseText = countFalse > 0 ? countFalse.ToString() : "";
                totalText = totalPublishSets.ToString();
            }

            rows.Add(new Dictionary<string, string>
            {
                ["Sheet Number"] = sheet.Number,
                ["Sheet Name"] = sheet.Name,
                ["Current Revision"] = sheet.Revision,
                ["Revision Date"] = sheet.RevisionDate,
                ["Revision Description"] = sheet.RevisionDescription,
                ["In Sheet List"] = sheet.InSheetList,
                ["Placeholder"] = sheet.Placeholder ? "Yes" : "No",
                ["Total Views Placed"] = sheet.TotalViews.ToString(),
                ["Total Annotate Elements on Views"] = sheet.TotalAnnotations.ToString(),
                ["Sheet Publish Sets - True"] = trueText,
                ["Sheet Publish Sets - False"] = falseText,
                ["Count Sheet Publish Sets - True"] = countTrueText,
                ["Count Sheet Publish Sets - False"] = countFalseText,
                ["Total Sheet Publish Sets"] = totalText,
            });
        }
        return rows;
    }

    private readonly struct SheetRow
    {
        public string Number { get; init; }
        public string Name { get; init; }
        public string Revision { get; init; }
        public string RevisionDate { get; init; }
        public string RevisionDescription { get; init; }
        public string InSheetList { get; init; } // "Yes"/"No" — SHEET_SCHEDULED
        public bool Placeholder { get; init; }
        public int TotalViews { get; init; }
        public int TotalAnnotations { get; init; }
        public IReadOnlyList<string> TrueSets { get; init; }  // selected publish sets, sorted case-insensitively
        public IReadOnlyList<string> FalseSets { get; init; } // present-but-unselected publish sets, sorted
    }

    /// <summary>Everything the summary, detail rows and note need for one model, read in a single pass.</summary>
    private sealed class ModelScan
    {
        public List<SheetRow> Sheets { get; init; } = new();
        public PublishSettingsReader.PublishSetData Publish { get; init; } = new();

        /// <summary>Where this model's time went, in milliseconds — see <see cref="Timing"/>.</summary>
        public long PublishMs { get; set; }
        public long SheetsMs { get; set; }
        public long AnnotationsMs { get; set; }
        public int ViewsVisited { get; set; }
    }

    /// <summary>
    /// TEMPORARY DIAGNOSTIC, and deliberately in the tracking log's Message cell rather than the
    /// trail file, because that is the artefact that actually reaches whoever is debugging a run.
    ///
    /// This action was 43% of a 98-model run's processing time (176 of 413 model-minutes), and the
    /// cause was ATTRIBUTED BY READING THE CODE, not measured — the annotation loop looked like the
    /// obvious culprit. A controlled 14-model rerun then showed roughly 6 seconds of FIXED per-model
    /// cost before any sheet work (12 sheets → 7s, 26 → 8s, 55 → 13s), which no amount of annotation
    /// optimising can touch and which the guess never accounted for. So: measure the three phases
    /// separately and let the next production run say which one it is.
    ///
    /// Remove once the answer is known. It adds nothing to the detail CSV — the report data is
    /// untouched — only to the per-model note.
    /// </summary>
    private static string Timing(ModelScan scan) =>
        $"[dp {scan.PublishMs}ms publish, {scan.SheetsMs}ms sheets " +
        $"(of which {scan.AnnotationsMs}ms annotations over {scan.ViewsVisited} view(s))]";

    private static ModelScan ScanModel(Document doc)
    {
        var publishWatch = System.Diagnostics.Stopwatch.StartNew();
        // Read the cloud Publish Settings once per model, then join it per sheet below.
        PublishSettingsReader.PublishSetData publish = PublishSettingsReader.Build(doc);
        publishWatch.Stop();

        // Built once per model and reused for every view — see CountAnnotations.
        ElementFilter? annotationFilter = BuildAnnotationFilter(doc);

        var sheetWatch = System.Diagnostics.Stopwatch.StartNew();
        var annotationWatch = new System.Diagnostics.Stopwatch();
        int viewsVisited = 0;

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .OrderBy(vs => vs.SheetNumber, StringComparer.OrdinalIgnoreCase);

        var rows = new List<SheetRow>();
        foreach (ViewSheet vs in sheets)
        {
            bool placeholder = vs.IsPlaceholder;
            int totalViews = 0;
            int totalAnnotations = 0;
            IReadOnlyList<string> trueSets = Array.Empty<string>();
            IReadOnlyList<string> falseSets = Array.Empty<string>();

            // Placeholder sheets carry no placed views (GetAllPlacedViews is meaningless / can throw)
            // and never belong to a publish set — leave the views/annotations at 0 and publish blank.
            if (!placeholder)
            {
                try
                {
                    ICollection<ElementId> placedViews = vs.GetAllPlacedViews();
                    totalViews = placedViews.Count;
                    annotationWatch.Start();
                    foreach (ElementId viewId in placedViews)
                    {
                        totalAnnotations += CountAnnotations(doc, viewId, annotationFilter);
                        viewsVisited++;
                    }
                    annotationWatch.Stop();
                }
                catch
                {
                    // Count what we can — a view that refuses enumeration leaves the totals as-is.
                }

                long sheetId = vs.Id.Value;
                trueSets = publish.SortedTrueSetsFor(sheetId);
                falseSets = publish.SortedFalseSetsFor(sheetId);
            }

            rows.Add(new SheetRow
            {
                Number = vs.SheetNumber ?? "",
                Name = vs.Name ?? "",
                Revision = Param(vs, BuiltInParameter.SHEET_CURRENT_REVISION),
                RevisionDate = Param(vs, BuiltInParameter.SHEET_CURRENT_REVISION_DATE),
                RevisionDescription = Param(vs, BuiltInParameter.SHEET_CURRENT_REVISION_DESCRIPTION),
                // SHEET_SCHEDULED = "Appears In Sheet List" (1 = in the list); default No if absent.
                InSheetList = (vs.get_Parameter(BuiltInParameter.SHEET_SCHEDULED)?.AsInteger() ?? 0) == 1 ? "Yes" : "No",
                Placeholder = placeholder,
                TotalViews = totalViews,
                TotalAnnotations = totalAnnotations,
                TrueSets = trueSets,
                FalseSets = falseSets,
            });
        }
        sheetWatch.Stop();
        return new ModelScan
        {
            Sheets = rows,
            Publish = publish,
            PublishMs = publishWatch.ElapsedMilliseconds,
            SheetsMs = sheetWatch.ElapsedMilliseconds,
            AnnotationsMs = annotationWatch.ElapsedMilliseconds,
            ViewsVisited = viewsVisited
        };
    }

    /// <summary>
    /// Every category in the document whose <see cref="CategoryType"/> is
    /// <see cref="CategoryType.Annotation"/>, as a filter Revit can evaluate natively.
    ///
    /// This is the same predicate <see cref="CountAnnotations"/> used to apply in managed code, just
    /// moved to the side of the API boundary that can answer it cheaply. Null when no usable filter
    /// can be built, which makes the caller fall back to the original element-by-element loop —
    /// counting nothing would silently corrupt the report, so an unbuildable filter must never be
    /// treated as "no annotations".
    /// </summary>
    private static ElementFilter? BuildAnnotationFilter(Document doc)
    {
        var ids = new List<ElementId>();
        try
        {
            foreach (Category category in doc.Settings.Categories)
            {
                try
                {
                    if (category.CategoryType == CategoryType.Annotation) ids.Add(category.Id);
                }
                catch { /* a category that won't answer is simply not added */ }
            }
        }
        catch { return null; }

        if (ids.Count == 0) return null;

        try { return new ElementMulticategoryFilter(ids); }
        catch
        {
            // Not every category can back a filter. Keep the ones that can rather than abandoning
            // the fast path for the whole model because of one awkward entry.
            var usable = new List<ElementId>();
            foreach (ElementId id in ids)
            {
                try
                {
                    _ = new ElementCategoryFilter(id);
                    usable.Add(id);
                }
                catch { /* not filterable */ }
            }
            if (usable.Count == 0) return null;
            try { return new ElementMulticategoryFilter(usable); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Count the annotation-category elements placed on one view. Per-view and per-element
    /// exceptions are swallowed (count what we can) — a single bad view must not sink the sheet.
    ///
    /// <para>MEASURED 2026-07-26: this action was <b>43% of a 98-model run's entire processing
    /// time</b> — 176 of 413 model-minutes, up to 13 minutes on one model. The cost was here. The
    /// view-specific collector is unavoidable (it is what makes the count per-view), but the old
    /// code then pulled <i>every element visible in the view</i> across the API boundary and asked
    /// each one for its <c>Category</c>, on models with tens of thousands of elements per view and
    /// hundreds of views per model.</para>
    ///
    /// <para>Now Revit applies the category predicate itself and returns a count, so not one
    /// element is materialised. Same collector, same views, same predicate — the number is
    /// unchanged, and a cell-for-cell diff of the detail CSV against a previous run is the gate
    /// that proves it.</para>
    /// </summary>
    private static int CountAnnotations(Document doc, ElementId viewId, ElementFilter? annotationFilter)
    {
        try
        {
            if (annotationFilter != null)
                return new FilteredElementCollector(doc, viewId)
                    .WhereElementIsNotElementType()
                    .WherePasses(annotationFilter)
                    .GetElementCount();
        }
        catch
        {
            // Fall through to the original loop rather than report zero.
        }

        int count = 0;
        try
        {
            foreach (Element element in new FilteredElementCollector(doc, viewId).WhereElementIsNotElementType())
            {
                try
                {
                    Category? category = element.Category;
                    if (category is null) continue;
                    if (category.CategoryType == CategoryType.Annotation) count++;
                }
                catch
                {
                    // Ignore an element whose category can't be read.
                }
            }
        }
        catch
        {
            // Ignore a view that can't be collected.
        }
        return count;
    }

    /// <summary>The sheet parameter's string value, or "" when the parameter is absent — keeps the
    /// revision cells null-safe on placeholder sheets that may not carry every revision field.</summary>
    private static string Param(ViewSheet vs, BuiltInParameter bip) =>
        vs.get_Parameter(bip)?.AsString() ?? "";
}
