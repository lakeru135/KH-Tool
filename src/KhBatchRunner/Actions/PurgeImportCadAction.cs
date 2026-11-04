using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

/// <summary>
/// Multi Model Action: delete every non-linked imported CAD instance (DWG/DXF/DGN/SAT
/// import, not a link) — a batch port of the team's "Purge CAD2.dyn" Dynamo graph.
/// Write-capable (<see cref="ActionCategory.Action"/>), so <see cref="Batch.BatchEngine"/>
/// syncs/saves the model afterwards per its persistence rule; this action only owns its
/// own transaction, never the document's open/close/save lifecycle.
/// </summary>
public sealed class PurgeImportCadAction : BatchActionBase
{
    public override string Name => "Purge Import CAD";

    public override ActionCategory Category => ActionCategory.Action;

    public override IReadOnlyList<string> Columns { get; } = new[] { "Deleted Element", "Category", "Workset", "Creator" };

    public override WorksetPosture Worksets => WorksetPosture.PartialByDesign;

    public override string WorksetPostureReason =>
        "DoPurge collects ImportInstance — INSTANCES, which a closed workset does not load — so it " +
        "deletes only the imports that happened to be loaded and reports that count as the job " +
        "done. The most consequential of the four found on 2026-08-16, because this action WRITES: " +
        "a model reported as purged can still hold imported CAD, and the row count gives no hint. " +
        "ACCEPTED by the user that day; the fix is OpensAll (there is no view or type to derive a " +
        "smaller set from — a CADLinkType's workset is not reliably its instance's).";

    // The engine's ProcessModel only ever calls RunDetailRows per model (Run() exists for
    // callers that want a single summary row instead), so the actual deletion must happen
    // in exactly one of the two — never both, or the same element could be double-deleted.
    // DoPurge() does the real work once; both members just read its result.
    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc) =>
        DoPurge(doc).Rows;

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        (List<IReadOnlyDictionary<string, string>> rows, int skipped) = DoPurge(doc);
        var summary = new Dictionary<string, string> { ["Deleted Count"] = rows.Count.ToString() };
        if (skipped > 0)
            summary["Skipped Count"] = skipped.ToString();
        return summary;
    }

    /// <summary>
    /// Collects every non-linked <see cref="ImportInstance"/> and deletes it inside one
    /// transaction, per-element try/catch so a single stubborn import (e.g. pinned in a way
    /// that resists unpinning) just gets skipped instead of aborting the whole model.
    /// </summary>
    private static (List<IReadOnlyDictionary<string, string>> Rows, int Skipped) DoPurge(Document doc)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        int skipped = 0;

        List<ImportInstance> instances = new FilteredElementCollector(doc)
            .OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>()
            .Where(i => !i.IsLinked)
            .ToList();

        using var t = new Transaction(doc, "Purge Import CAD");
        t.Start();
        foreach (ImportInstance i in instances)
        {
            try
            {
                string name = i.get_Parameter(BuiltInParameter.IMPORT_SYMBOL_NAME)?.AsString() ?? "Unnamed CAD";
                string category = i.Category?.Name ?? "";

                string workset = "";
                string creator = "";
                if (doc.IsWorkshared)
                {
                    try { workset = doc.GetWorksetTable().GetWorkset(i.WorksetId).Name; } catch { /* leave blank */ }
                    try { creator = WorksharingUtils.GetWorksharingTooltipInfo(doc, i.Id).Creator; } catch { /* leave blank */ }
                }

                // A pinned import instance can't be deleted until it's unpinned.
                if (i.Pinned)
                    i.Pinned = false;

                doc.Delete(i.Id);
                rows.Add(new Dictionary<string, string>
                {
                    ["Deleted Element"] = name,
                    ["Category"] = category,
                    ["Workset"] = workset,
                    ["Creator"] = creator
                });
            }
            catch
            {
                skipped++;
            }
        }
        t.Commit();

        return (rows, skipped);
    }
}
