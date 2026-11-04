#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW PROBE"): reads the Enlarge Door &amp; Window families as the user saved
/// them — parameters, text types, nested families and which host parameter drives each nested
/// one's parameters — so the builder is written against what is really there. Opens each family
/// file, never saves it. Folder: DW_BUILD_OUT.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void DwProbeFamilies(Document doc)
    {
        string dir = Environment.GetEnvironmentVariable("DW_BUILD_OUT") is { Length: > 0 } o ? o : Path.GetDirectoryName(_report)!;
        foreach (string path in Directory.GetFiles(dir, "*.rfa").Where(f => !Path.GetFileName(f).Contains(".0")).OrderBy(f => f))
        {
            Info("");
            Info("=== " + Path.GetFileName(path));
            Document fam = doc.Application.OpenDocumentFile(path);
            try
            {
                FamilyManager fm = fam.FamilyManager;
                Info($"category {fam.OwnerFamily.FamilyCategory?.Name}; types {fm.Types.Size}");
                foreach (FamilyParameter p in fm.GetParameters())
                {
                    string val = fm.CurrentType == null ? "" : p.StorageType switch
                    {
                        StorageType.Double => SpMm(fm.CurrentType.AsDouble(p) ?? 0) + " mm",
                        StorageType.Integer => (fm.CurrentType.AsInteger(p) ?? 0).ToString(),
                        StorageType.String => "'" + fm.CurrentType.AsString(p) + "'",
                        _ => ""
                    };
                    Info($"  param '{p.Definition.Name}' {(p.IsInstance ? "inst" : "type")} {p.Definition.GetDataType().TypeId.Split(':').Last()}" +
                         $"{(p.Formula is { Length: > 0 } f ? " = " + f : "")} {val}");
                }
                foreach (TextElementType tt in new FilteredElementCollector(fam).OfClass(typeof(TextElementType)).Cast<TextElementType>())
                    Info($"  text type '{tt.Name}': {tt.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString()} " +
                         $"{SpMm(tt.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0)} mm");
                foreach (Element e in new FilteredElementCollector(fam).WhereElementIsNotElementType()
                             .Where(e => e.GetType().Name is "TextElement" or "TextNote" || e.Category?.Name == "Generic Annotations"))
                {
                    string more = "";
                    if (e is TextElement te)
                    {
                        var type = fam.GetElement(te.GetTypeId()) as TextElementType;
                        double size = type?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                        more = $" type '{type?.Name}' {size * 304.8:0.00} mm {type?.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString()}, " +
                               $"box width {te.Width * 304.8:0.0} mm, align {te.HorizontalAlignment}/{te.VerticalAlignment}, at ({te.Coord.X * 304.8:0.0}, {te.Coord.Y * 304.8:0.0})" +
                               (te.get_BoundingBox(null) is { } bb ? $", box ({bb.Min.X * 304.8:0.0}, {bb.Min.Y * 304.8:0.0})..({bb.Max.X * 304.8:0.0}, {bb.Max.Y * 304.8:0.0})" : "") +
                               $", class {te.GetType().Name}";
                    }
                    Info($"  element {e.GetType().Name} #{e.Id} cat {e.Category?.Name}{more}");
                }

                foreach (FamilyInstance fi in new FilteredElementCollector(fam).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    LocationPoint? lp = fi.Location as LocationPoint;
                    Info($"  nested '{fi.Symbol.FamilyName} : {fi.Symbol.Name}' at ({SpMm(lp?.Point.X ?? 0)}, {SpMm(lp?.Point.Y ?? 0)}) mm");
                    Document inner = fam.EditFamily(fi.Symbol.Family);
                    try
                    {
                        foreach (TextElement te in new FilteredElementCollector(inner).OfClass(typeof(TextElement)).Cast<TextElement>())
                        {
                            var tt = inner.GetElement(te.GetTypeId()) as TextElementType;
                            Info($"    inside: label type '{tt?.Name}' {(tt?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0) * 304.8:0.00} mm");
                        }
                    }
                    finally { inner.Close(false); }
                    foreach (Parameter p in fi.Parameters)
                    {
                        FamilyParameter? host = fm.GetAssociatedFamilyParameter(p);
                        if (host != null || p.StorageType == StorageType.String && p.Definition.Name.StartsWith("KH", StringComparison.OrdinalIgnoreCase))
                            Info($"    '{p.Definition.Name}' <- {(host == null ? "NOT associated" : "'" + host.Definition.Name + "'")}");
                    }
                }
            }
            catch (Exception ex) { Fail(_scenario, Path.GetFileName(path) + ": " + ex.Message); }
            finally { fam.Close(false); }
        }
        Check("probe finished", true);
    }
}
#endif
