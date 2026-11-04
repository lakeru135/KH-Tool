#if EDITION_DRW
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.DoorWindow;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW PROBE R4", DW_DEV=1, 2026-10-22): what the fourth round of fixes rests on.
/// A legend component's host-length parameter; whether a KH_DW_Row Value label shows a line break
/// written into its text parameter (exported as PNG to look at); the viewport types and their
/// Show Title; the sheet browser organization's folder parameters. Reads, then rolls back.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void DwProbeR4(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc, door: true);
        LegendBuilder.Run(doc, p);
        List<(Element E, DwTagData T)> made = DwTag.All(doc, p.SetKey);

        Element? plan = made.FirstOrDefault(m => m.T.Role == DwRole.Plan).E;
        if (plan != null)
            foreach (Parameter q in plan.Parameters.Cast<Parameter>().OrderBy(q => q.Definition.Name))
                Info($"  plan component: '{q.Definition.Name}' bip={(q.Definition as InternalDefinition)?.BuiltInParameter} " +
                     $"{q.StorageType} ro={q.IsReadOnly} = {(q.StorageType == StorageType.Double ? (q.AsDouble() * 304.8).ToString("0.#") + " mm" : q.AsValueString() ?? q.AsString())}");

        var rows = made.Where(m => m.T.Role == DwRole.Row).Select(m => (FamilyInstance)m.E).Take(3).ToList();
        View? legend = made.FirstOrDefault(m => m.T.Role == DwRole.Legend).E as View;
        if (rows.Count == 3 && legend != null)
        {
            using (var t = new Transaction(doc, "probe breaks"))
            {
                t.Start();
                rows[0].LookupParameter("KH_DW Value")?.Set("LF one\nLF two");
                rows[1].LookupParameter("KH_DW Value")?.Set("CRLF one\r\nCRLF two");
                rows[2].LookupParameter("KH_DW Value")?.Set("CR one\rCR two");
                t.Commit();
            }
            Info($"  value read back: {string.Join(" | ", rows.Select(r => (r.LookupParameter("KH_DW Value")?.AsString() ?? "").Replace("\r", "\\r").Replace("\n", "\\n")))}");
            DwExport(doc, legend, "dw-probe-r4-breaks");
        }

        foreach (ElementType vt in new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                     .Where(e => e.Category?.Id.Value == (long)BuiltInCategory.OST_Viewports || e.FamilyName == "Viewport"))
            Info($"  viewport type '{vt.FamilyName}: {vt.Name}' show title={vt.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.AsInteger()}");

        BrowserOrganization org = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc);
        Info($"  sheet browser organization: '{org?.Name}'");
        foreach (ViewSheet s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Take(3))
            if (org != null)
                foreach (FolderItemInfo fi in org.GetFolderItems(s.Id))
                    Info($"  sheet {s.SheetNumber}: folder '{fi.Name}' by param {fi.ElementId} ({(fi.ElementId.Value < 0 ? ((BuiltInParameter)fi.ElementId.Value).ToString() : doc.GetElement(fi.ElementId)?.Name)})");
    }
}
#endif
