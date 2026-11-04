#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW WEIGHT", DW_DEV=1, 2026-10-20): the user asked for the cell outline and
/// the box dividers at line weight 3, the table's inner lines thin. KH_DW_Header: every line on a
/// new Detail Items subcategory "KH_DW Outline" (weight 3). KH_DW_Row: its sides on Outline, the
/// row lines and label-column line on "KH_DW Lines" (weight 1), and a second, thick bottom line
/// shown only when the new Yes/No "Is Last Row" is ticked (the builder ticks a cell's last row), so
/// the cell's bottom border is thick and the lines between rows are not. Both files are kept in
/// _orig\ first; the weights stay editable in the project's Object Styles.
/// </summary>
internal static partial class DrwSelfTest
{
    private const string OutlineStyle = "KH_DW Outline", ThinStyle = "KH_DW Lines";

    private static void DwWeight(Document doc)
    {
        string dir = Environment.GetEnvironmentVariable("DW_BUILD_OUT") is { Length: > 0 } o ? o : Path.GetDirectoryName(_report)!;
        foreach (string name in new[] { "KH_DW_Header", "KH_DW_Row" })
        {
            string path = Path.Combine(dir, name + ".rfa");
            string keep = Path.Combine(dir, "_orig", name + " (before weight).rfa");
            if (!File.Exists(keep)) File.Copy(path, keep);
            Document fam = doc.Application.OpenDocumentFile(path);
            try
            {
                using (var t = new Transaction(fam, "line weights"))
                {
                    t.Start();
                    GraphicsStyle outline = DwSubcategory(fam, OutlineStyle, 3);
                    GraphicsStyle thin = DwSubcategory(fam, ThinStyle, 1);
                    var curves = new FilteredElementCollector(fam).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                        .Where(c => c.CurveElementType == CurveElementType.DetailCurve).ToList();
                    if (name == "KH_DW_Header")
                        foreach (CurveElement c in curves) c.LineStyle = outline;
                    else
                        DwWeightRow(fam, curves, outline, thin);
                    t.Commit();
                }
                fam.Save();
                DwDropBackups(path);
                Info("saved " + path);
            }
            catch (Exception ex) { Fail(_scenario, name + ": " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
            finally { fam.Close(false); }
        }
    }

    private static GraphicsStyle DwSubcategory(Document fam, string name, int weight)
    {
        Category parent = fam.OwnerFamily.FamilyCategory;
        Category sub = parent.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == name)
                       ?? fam.Settings.Categories.NewSubcategory(parent, name);
        sub.SetLineWeight(weight, GraphicsStyleType.Projection);
        return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
    }

    private static void DwWeightRow(Document fam, List<CurveElement> curves, GraphicsStyle outline, GraphicsStyle thin)
    {
        FamilyManager fm = fam.FamilyManager;
        View view = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<View>().First(v => !v.IsTemplate);
        double rowWidth = fm.CurrentType.AsDouble(fm.get_Parameter("Row Width")) ?? 0;
        double rowHeight = fm.CurrentType.AsDouble(fm.get_Parameter("Row Height")) ?? 0;
        bool Vertical(CurveElement c) => Math.Abs(c.GeometryCurve.GetEndPoint(0).X - c.GeometryCurve.GetEndPoint(1).X) < 1e-6;
        double At(CurveElement c, bool x) => x ? c.GeometryCurve.GetEndPoint(0).X : c.GeometryCurve.GetEndPoint(0).Y;

        CurveElement? bottom = null;
        foreach (CurveElement c in curves)
        {
            bool side = Vertical(c) && (Math.Abs(At(c, true)) < 1e-6 || Math.Abs(At(c, true) - rowWidth) < 1e-6);
            c.LineStyle = side ? outline : thin;
            if (!Vertical(c) && Math.Abs(At(c, false) + rowHeight) < 1e-6) bottom = c;
        }
        if (bottom == null) throw new InvalidOperationException("the row's bottom line was not found");
        Info($"row: {curves.Count} lines; sides on {OutlineStyle}, the rest on {ThinStyle}");

        // Is Last Row: the thick bottom shows, the thin one hides.
        FamilyParameter isLast = fm.get_Parameter("Is Last Row")
            ?? fm.AddParameter("Is Last Row", GroupTypeId.Graphics, SpecTypeId.Boolean.YesNo, true);
        FamilyParameter notLast = fm.get_Parameter("Not Last Row")
            ?? fm.AddParameter("Not Last Row", GroupTypeId.Graphics, SpecTypeId.Boolean.YesNo, true);
        fm.SetFormula(notLast, "not(Is Last Row)");
        fm.AssociateElementParameterToFamilyParameter(bottom.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), notLast);

        // The thick bottom: the same line, locked to the same planes.
        var planes = new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
        ReferencePlane left = planes.First(p => p.Name == "Center (Left/Right)");
        ReferencePlane right = planes.First(p => p.Name == "Right");
        ReferencePlane bottomPlane = planes.First(p => p.Name == "Row Height");
        DetailCurve thick = fam.FamilyCreate.NewDetailCurve(view, (Line)bottom.GeometryCurve.Clone());
        thick.LineStyle = outline;
        fam.Regenerate();
        fam.FamilyCreate.NewAlignment(view, bottomPlane.GetReference(), thick.GeometryCurve.Reference);
        fam.FamilyCreate.NewAlignment(view, left.GetReference(), thick.GeometryCurve.GetEndPointReference(0));
        fam.FamilyCreate.NewAlignment(view, right.GetReference(), thick.GeometryCurve.GetEndPointReference(1));
        fm.AssociateElementParameterToFamilyParameter(thick.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), isLast);
        fm.Set(isLast, 0);
        fam.Regenerate();

        // Flex: wider and 3 lines tall, then last — the thick line must follow and switch.
        using var st = new SubTransaction(fam);
        st.Start();
        fm.Set(fm.get_Parameter("Row Width"), 2 * rowWidth);
        fm.Set(fm.get_Parameter("Lines"), 3);
        fm.Set(isLast, 1);
        fam.Regenerate();
        double h3 = fm.CurrentType.AsDouble(fm.get_Parameter("Row Height")) ?? 0;
        Line g = (Line)thick.GeometryCurve;
        Check("thick bottom follows the row's width and height",
            Math.Abs(g.Length - 2 * rowWidth) < 1e-6 && Math.Abs(g.GetEndPoint(0).Y + h3) < 1e-6, $"{SpMm(g.Length)} at {SpMm(g.GetEndPoint(0).Y)}");
        Check("Is Last Row shows the thick bottom and hides the thin one",
            thick.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM).AsInteger() == 1 && bottom.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM).AsInteger() == 0);
        st.RollBack();
    }
}
#endif
