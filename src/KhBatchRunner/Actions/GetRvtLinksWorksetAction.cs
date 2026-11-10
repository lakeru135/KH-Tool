using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

/// <summary>
/// Multi Model Check: report external references placed on USER worksets — Revit links (RVT and
/// IFC), linked/imported CAD (DWG/DXF/DGN/SKP), and point clouds. PDF and raster image instances
/// are intentionally excluded. One detail row per qualifying instance includes Kind (Link vs
/// Import), Type, the reference's user workset, load status, and pin state. References placed on a
/// system workset are omitted rather than displayed as "(system)". Nested RVT links are skipped
/// (their workset is owned by the parent link and can't be fixed in the host model); only their
/// count is surfaced. Read-only — no transaction.
///
/// ONE WORKSET COLUMN, THE INSTANCE'S. Until 1.3.5 this reported the link TYPE's workset beside it,
/// which was noise dressed as data: the type's workset has no direct UI in Revit — you cannot
/// select a RevitLinkType and change it — while the instance's is the "Workset" shown in Properties
/// whenever a link is selected. That is the one a person manages, so it is the one reported, and it
/// is simply called "Workset". Correct Workset moves a type and its instances together as one unit
/// (see CorrectWorksetAction.PlanLinks), so after a fix the two agreed anyway.
///
/// The action class is still named <c>GetRvtLinksWorksetAction</c> for continuity, but its display
/// <see cref="Name"/> is "Get Link Workset" (was "Get RVT Links Workset").
///
/// <para>NO MATRIX AT ALL since 1.3.8.2. Four checks used to pivot themselves into a workbook
/// automatically, which is three more than anybody asked for and four more files per run to sift
/// through; 1.3.8.1 left only Phase &amp; Workset and Design Option doing it, and kept this one
/// rebuildable on demand. 1.3.8.2 drops that too, by decision — a matrix this product will build if
/// asked but never builds on its own is a third state nobody was tracking. Nothing is lost from the
/// DATA: this check's detail CSV is unchanged and still carries every value the workbook was ever
/// pivoted from.</para>
/// </summary>
public sealed class GetRvtLinksWorksetAction : BatchActionBase
{
    public override string Name => "Get Link Workset";

    public override IReadOnlyList<string> Columns { get; } =
        new[] { "Link Name", "Kind", "Type", "Workset", "Load Status", "Pinned" };

    public override WorksetPosture Worksets => WorksetPosture.PartialByDesign;

    public override string WorksetPostureReason =>
        "ReadLinks collects RevitLinkInstance, ImportInstance and PointCloudInstance — INSTANCES, " +
        "which a closed workset does not load — so the matrix lists only links whose instances " +
        "happened to be loaded and silently omits the rest. Note the irony: the Workset column is " +
        "read from the instance, so the very thing this reports is the thing hiding rows from it. " +
        "Sister action RelinkLinksAction avoids this by reading RevitLinkType instead, but a TYPE " +
        "says a link exists, never which workset its instance sits in — which is this report's " +
        "whole subject. Found and ACCEPTED by the user on 2026-08-16; the fix is PlansOwn with the " +
        "link types' worksets, accepting that an instance moved away from its type's workset would " +
        "still be missed.";

    private string _perModelNote = "";

    public override string PerModelNote => _perModelNote;

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        (List<LinkRow> links, int nested) = ReadLinks(doc);
        return new Dictionary<string, string>
        {
            ["Workshared"] = doc.IsWorkshared ? "Yes" : "No",
            ["Reference Count"] = links.Count.ToString(),
            ["Links"] = links.Count(l => l.Kind == "Link").ToString(),
            ["Imports"] = links.Count(l => l.Kind == "Import").ToString(),
            ["Nested Links (excluded)"] = nested.ToString(),
        };
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc)
    {
        (List<LinkRow> links, int nested) = ReadLinks(doc);
        _perModelNote = nested > 0 ? $"{nested} nested link instance(s) excluded" : "";

        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (LinkRow link in links)
        {
            rows.Add(new Dictionary<string, string>
            {
                ["Link Name"] = link.Name,
                ["Kind"] = link.Kind,
                ["Type"] = link.TypeLabel,
                ["Workset"] = link.Workset,
                ["Load Status"] = link.LoadStatus,
                ["Pinned"] = link.Pinned,
            });
        }
        return rows;
    }

    private readonly struct LinkRow
    {
        public string Name { get; init; }
        public string Kind { get; init; }        // "Link" or "Import"
        public string TypeLabel { get; init; }   // RVT / IFC / CAD / Image / PointCloud
        public string Workset { get; init; }     // the INSTANCE's user workset — see the class remarks
        public string LoadStatus { get; init; }
        public string Pinned { get; init; }
    }

    private static (List<LinkRow> Links, int NestedSkipped) ReadLinks(Document doc)
    {
        var links = new List<LinkRow>();
        int nested = 0;

        // --- Revit links (RVT + IFC) ------------------------------------------------------------
        foreach (RevitLinkInstance inst in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var type = doc.GetElement(inst.GetTypeId()) as RevitLinkType;
            if (type != null && type.IsNestedLink) { nested++; continue; }
            if (!TryGetUserWorksetName(doc, inst.WorksetId, out string instanceWorkset)) continue;

            // Type name works even when the link is unloaded (GetLinkDocument() would be null).
            string name = type?.Name ?? inst.Name;

            string loadStatus = "";
            try { loadStatus = type?.GetLinkedFileStatus().ToString() ?? ""; } catch { /* leave blank */ }

            links.Add(new LinkRow
            {
                Name = name,
                Kind = "Link",
                TypeLabel = IsIfc(doc, type, name) ? "IFC" : "RVT",
                Workset = instanceWorkset,
                LoadStatus = loadStatus,
                Pinned = inst.Pinned ? "Yes" : "No",
            });
        }

        // --- CAD (linked + imported) — ImportInstance.IsLinked distinguishes the two -------------
        foreach (ImportInstance inst in new FilteredElementCollector(doc)
                     .OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
        {
            if (!TryGetUserWorksetName(doc, inst.WorksetId, out string instanceWorkset)) continue;
            string name = inst.get_Parameter(BuiltInParameter.IMPORT_SYMBOL_NAME)?.AsString()
                          ?? inst.Category?.Name ?? "Unnamed CAD";

            links.Add(new LinkRow
            {
                Name = name,
                Kind = inst.IsLinked ? "Link" : "Import",
                TypeLabel = "CAD",
                Workset = instanceWorkset,
                LoadStatus = "",
                Pinned = inst.Pinned ? "Yes" : "No",
            });
        }

        // --- Point clouds (RCP / RCS) — always links ------------------------------------------
        foreach (PointCloudInstance inst in new FilteredElementCollector(doc)
                     .OfClass(typeof(PointCloudInstance)).Cast<PointCloudInstance>())
        {
            if (!TryGetUserWorksetName(doc, inst.WorksetId, out string instanceWorkset)) continue;
            var pcType = doc.GetElement(inst.GetTypeId()) as PointCloudType;
            links.Add(new LinkRow
            {
                Name = pcType?.Name ?? inst.Name,
                Kind = "Link",
                TypeLabel = "PointCloud",
                Workset = instanceWorkset,
                LoadStatus = "",
                Pinned = inst.Pinned ? "Yes" : "No",
            });
        }

        return (links, nested);
    }

    /// <summary>True when a Revit link is IFC-backed (Revit caches an IFC as a hidden <c>*.ifc.rvt</c>
    /// but the link name/path still carries the <c>.ifc</c> extension). Best-effort — defaults RVT.</summary>
    private static bool IsIfc(Document doc, RevitLinkType? type, string name)
    {
        if (name.IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        try
        {
            ExternalFileReference? ext = type?.GetExternalFileReference();
            if (ext != null)
            {
                string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetPath());
                return path.IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { /* fall through to RVT */ }
        return false;
    }

    /// <summary>Returns true and the workset name only for a user workset. Non-workshared models,
    /// system worksets, and unreadable ids return false so their references are omitted.</summary>
    private static bool TryGetUserWorksetName(Document doc, WorksetId id, out string name)
    {
        name = "";
        if (!doc.IsWorkshared) return false;
        try
        {
            Workset workset = doc.GetWorksetTable().GetWorkset(id);
            if (workset.Kind != WorksetKind.UserWorkset) return false;
            name = workset.Name;
            return true;
        }
        catch { return false; }
    }
}
