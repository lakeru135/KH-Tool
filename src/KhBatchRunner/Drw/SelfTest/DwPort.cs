#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW PORT", DW_DEV=1, 2026-10-20): Enlarge Door &amp; Window's families for the
/// Revit year this runs in, from ONE hand-made label family. A family cannot be saved back to an
/// older Revit and the API cannot create a label, so for 2024 the user makes "KH_DW Label.rfa"
/// (a Generic Annotation holding one label of the instance text parameter KH_DW Label) and this
/// does the rest, the way the 2026 set was finished: the four label variants (3 / 5 mm, centre /
/// left, TH Sarabun New, transparent, hung from their middle as the 2026 ones are), KH_DW_Header
/// and KH_DW_Row built (DW BUILD), the row's two labels nested and driven by its Label / Value,
/// locked to their planes (DW LOCK), line weights (DW WEIGHT) and the seed project. Run it with
/// the year's sample model (it needs a legend with a component for the seed). Folder: DW_BUILD_OUT.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void DwPortFamilies(Document doc)
    {
        string dir = Environment.GetEnvironmentVariable("DW_BUILD_OUT") is { Length: > 0 } o ? o : Path.GetDirectoryName(_report)!;
        string source = Path.Combine(dir, "KH_DW Label.rfa");
        if (!File.Exists(source))
        {
            Fail(_scenario, "make " + source + " first: a Generic Annotation with one label of the instance text parameter 'KH_DW Label'");
            return;
        }
        Directory.CreateDirectory(Path.Combine(dir, "_orig"));
        Info($"Revit {doc.Application.VersionNumber}, folder {dir}");

        // The 2026 labels as probed: hung from their middle 0.3 mm below the origin, centred at
        // x 0.6 mm or left-aligned from x 2.2 mm; the row's Value label wraps at 50 mm.
        foreach ((string name, string type, double mm, HorizontalTextAlignment align, double x) in new[]
                 {
                     ("KH_DW Label Center", "3mm", 3.0, HorizontalTextAlignment.Center, 0.6),
                     ("KH_DW Label Left", "3mm", 3.0, HorizontalTextAlignment.Left, 2.2),
                     ("KH_DW Header Center", "5mm", 5.0, HorizontalTextAlignment.Center, 0.6),
                     ("KH_DW Header Left", "5mm", 5.0, HorizontalTextAlignment.Left, 2.2)
                 })
            DwEditFamily(doc, source, Path.Combine(dir, name + ".rfa"), fam => DwPortLabel(fam, type, mm, align, x));

        // DW BUILD makes only what is missing; a port starts clean.
        foreach (string f in new[] { "KH_DW_Header.rfa", "KH_DW_Row.rfa", "KH_DW_Seed.rvt" })
            if (File.Exists(Path.Combine(dir, f))) File.Delete(Path.Combine(dir, f));
        DwBuildFamilies(doc);
        DwNestRowLabels(doc, dir);
        DwLockRow(doc);
        DwWeight(doc);
        DwProbeFamilies(doc);
    }

    /// <summary>One label variant: its text type, alignment and place, as the 2026 families have them.</summary>
    private static void DwPortLabel(Document fam, string type, double mm, HorizontalTextAlignment align, double x)
    {
        var labels = new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>()
            .Where(te => te is not TextNote).ToList();
        if (labels.Count != 1) throw new InvalidOperationException($"{fam.Title}: {labels.Count} labels, want exactly 1");
        TextElement label = labels[0];
        // Only types of the label's own kind: the user's 2024 family had a text NOTE type named '3mm'
        // too, and a label refuses it ("The type typeId is not valid for this element").
        var own = (TextElementType)fam.GetElement(label.GetTypeId());
        TextElementType t = new FilteredElementCollector(fam).OfClass(typeof(TextElementType)).Cast<TextElementType>()
                                .FirstOrDefault(tt => tt.Name == type && tt.GetType() == own.GetType() && label.IsValidType(tt.Id))
                            ?? (TextElementType)own.Duplicate(type + (new FilteredElementCollector(fam).OfClass(typeof(TextElementType)).Any(tt => tt.Name == type) ? " label" : ""));
        t.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(mm / 304.8);
        t.get_Parameter(BuiltInParameter.TEXT_FONT).Set("TH Sarabun New");
        foreach (TextElementType any in new FilteredElementCollector(fam).OfClass(typeof(TextElementType)).Cast<TextElementType>())
            any.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1);
        label.ChangeTypeId(t.Id);
        label.HorizontalAlignment = align;
        label.VerticalAlignment = VerticalTextAlignment.Middle;
        if (align == HorizontalTextAlignment.Left && mm < 4)   // the row's Value label wraps at 50 mm
            try { label.Width = 50 / 304.8; }
            catch (Exception ex) { Info($"  {fam.Title}: label width not set by the API ({ex.Message}) — set it to 50 mm by hand"); }
        fam.Regenerate();
        XYZ to = new XYZ(x / 304.8, -0.3 / 304.8, 0);
        ElementTransformUtils.MoveElement(fam, label.Id, to - label.Coord);
        // The hand-made family's instruction note, if left in.
        foreach (TextNote n in new FilteredElementCollector(fam).OfClass(typeof(TextNote)).Cast<TextNote>().ToList()) fam.Delete(n.Id);
        fam.Regenerate();
        Info($"  {fam.Title} -> {type} {mm} mm {align}/Middle at ({label.Coord.X * 304.8:0.0}, {label.Coord.Y * 304.8:0.0}), width {label.Width * 304.8:0.0} mm");
    }

    /// <summary>
    /// The row's two labels, as the user nested them in 2026: KH_DW Label Center shows the row's
    /// KH_DW Label, KH_DW Label Left its KH_DW Value. Placed near their planes here; DW LOCK then
    /// moves and locks them.
    /// </summary>
    private static void DwNestRowLabels(Document doc, string dir)
    {
        string path = Path.Combine(dir, "KH_DW_Row.rfa");
        DwEditFamily(doc, path, null, fam =>
        {
            foreach (string name in new[] { "KH_DW Label Center", "KH_DW Label Left" })
            {
                Document label = doc.Application.OpenDocumentFile(Path.Combine(dir, name + ".rfa"));
                try { label.LoadFamily(fam, new DwOverwrite()); }
                finally { label.Close(false); }
            }
            FamilyManager fm = fam.FamilyManager;
            View view = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<View>().First(v => !v.IsTemplate);
            using var t = new Transaction(fam, "nest labels");
            t.Start();
            double labelWidth = fm.CurrentType.AsDouble(fm.get_Parameter("Label Width")) ?? 0;
            foreach ((string family, string host, double x) in new[] { ("KH_DW Label Center", "KH_DW Label", labelWidth / 2), ("KH_DW Label Left", "KH_DW Value", labelWidth + 20 / 304.8) })
            {
                FamilySymbol sym = new FilteredElementCollector(fam).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().First(s => s.FamilyName == family);
                if (!sym.IsActive) sym.Activate();
                FamilyInstance fi = fam.FamilyCreate.NewFamilyInstance(new XYZ(x, -0.1, 0), sym, view);
                fm.AssociateElementParameterToFamilyParameter(fi.LookupParameter("KH_DW Label"), fm.get_Parameter(host));
                Info($"  nested {family}, its KH_DW Label <- {host}");
            }
            fm.Set(fm.get_Parameter("KH_DW Label"), "Label / หัวข้อ");
            fm.Set(fm.get_Parameter("KH_DW Value"), "Value / ค่า");
            t.Commit();
        }, transaction: false);
    }
}
#endif
