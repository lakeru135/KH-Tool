#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW LOCK", 2026-10-20): the user's KH_DW_Row nests its two labels at fixed
/// model positions, so the row read right only at the scale it was drawn at (at 1:50 the value
/// overlapped the label column). This locks them to parameter-driven planes — label column centre
/// (Label Width / 2), value left (Label Width + Text Inset), text middle (Row Height / 2; the labels
/// hang from their middle) — so the add-in's paper-mm sizes place them at any scale. Flexes, then
/// saves; the file before is kept in _orig\.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void DwLockRow(Document doc)
    {
        string dir = Environment.GetEnvironmentVariable("DW_BUILD_OUT") is { Length: > 0 } o ? o : Path.GetDirectoryName(_report)!;
        string path = Path.Combine(dir, "KH_DW_Row.rfa");
        string keep = Path.Combine(dir, "_orig", "KH_DW_Row (before lock).rfa");
        if (!File.Exists(keep)) File.Copy(path, keep);

        Document fam = doc.Application.OpenDocumentFile(path);
        try
        {
            FamilyManager fm = fam.FamilyManager;
            View view = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<View>().First(v => !v.IsTemplate);
            var planes = new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
            Info("planes: " + string.Join(", ", planes.Select(p => $"'{p.Name}'")));
            ReferencePlane left = planes.First(p => p.Name == "Center (Left/Right)");
            ReferencePlane top = planes.First(p => p.Name == "Center (Front/Back)");
            var nested = new FilteredElementCollector(fam).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
            FamilyInstance labelInst = nested.First(i => i.Symbol.FamilyName == "KH_DW Label Center");
            FamilyInstance valueInst = nested.First(i => i.Symbol.FamilyName == "KH_DW Label Left");

            foreach (FamilyInstance fi in new[] { labelInst, valueInst })
                Info($"{fi.Symbol.FamilyName} refs: " + string.Join(", ", Enum.GetValues(typeof(FamilyInstanceReferenceType)).Cast<FamilyInstanceReferenceType>()
                    .Select(rt => (rt, n: fi.GetReferences(rt).Count)).Where(x => x.n > 0).Select(x => $"{x.rt}×{x.n}")));
            Document inner = fam.EditFamily(valueInst.Symbol.Family);
            foreach (ReferencePlane rp in new FilteredElementCollector(inner).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                Info($"  inside label: plane '{rp.Name}' dir {rp.Direction} isRef {rp.get_Parameter(BuiltInParameter.ELEM_REFERENCE_NAME)?.AsValueString()} ({rp.get_Parameter(BuiltInParameter.ELEM_REFERENCE_NAME)?.AsInteger()})");
            inner.Close(false);
            using (var t = new Transaction(fam, "lock labels"))
            {
                t.Start();
                FamilyParameter P(string name) => fm.get_Parameter(name)
                    ?? fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, true);
                FamilyParameter inset = P("Text Inset");
                if ((fm.CurrentType.AsDouble(inset) ?? 0) <= 0) fm.Set(inset, 20 / 304.8);
                FamilyParameter labelCentre = P("Label Centre");
                fm.SetFormula(labelCentre, "Label Width / 2");
                FamilyParameter valueLeft = P("Value Left");
                fm.SetFormula(valueLeft, "Label Width + Text Inset");
                FamilyParameter textMiddle = P("Text Middle");
                fm.SetFormula(textMiddle, "Row Height / 2");
                fam.Regenerate();

                double lc = fm.CurrentType.AsDouble(labelCentre) ?? 0, vl = fm.CurrentType.AsDouble(valueLeft) ?? 0;
                double tm = fm.CurrentType.AsDouble(textMiddle) ?? 0;
                ReferencePlane Plane(XYZ a, XYZ b, string name)
                {
                    ReferencePlane rp = fam.FamilyCreate.NewReferencePlane(a, b, XYZ.BasisZ, view);
                    rp.Name = name;
                    fam.Regenerate();
                    return rp;
                }
                void Label(ReferencePlane a, ReferencePlane b, XYZ at, XYZ along, FamilyParameter fp)
                {
                    var refs = new ReferenceArray();
                    refs.Append(a.GetReference());
                    refs.Append(b.GetReference());
                    fam.FamilyCreate.NewDimension(view, Line.CreateBound(at, at + along), refs).FamilyLabel = fp;
                }
                ReferencePlane pLc = Plane(new XYZ(lc, 1, 0), new XYZ(lc, -1, 0), "Label Centre");
                Label(left, pLc, new XYZ(0, 0.3, 0), XYZ.BasisX, labelCentre);
                ReferencePlane pVl = Plane(new XYZ(vl, 1, 0), new XYZ(vl, -1, 0), "Value Left");
                Label(left, pVl, new XYZ(0, 0.45, 0), XYZ.BasisX, valueLeft);
                ReferencePlane pTm = Plane(new XYZ(-1, -tm, 0), new XYZ(5, -tm, 0), "Text Middle");
                Label(top, pTm, new XYZ(-0.5, 0, 0), XYZ.BasisY, textMiddle);

                // Each label onto its planes (its own origin = its centre planes), then locked there.
                void Lock(FamilyInstance fi, ReferencePlane x, double atX)
                {
                    XYZ at = ((LocationPoint)fi.Location).Point;
                    ElementTransformUtils.MoveElement(fam, fi.Id, new XYZ(atX - at.X, -tm - at.Y, 0));
                    fam.Regenerate();
                    // The labels' own planes are Weak References (no Center references), one each way:
                    // which is which is found by trying — aligning across directions throws.
                    var weak = fi.GetReferences(FamilyInstanceReferenceType.WeakReference).ToList();
                    foreach (ReferencePlane target in new[] { x, pTm })
                    {
                        bool done = false;
                        foreach (Reference r in weak)
                        {
                            using var st = new SubTransaction(fam);
                            st.Start();
                            try { fam.FamilyCreate.NewAlignment(view, target.GetReference(), r); fam.Regenerate(); st.Commit(); done = true; break; }
                            catch { st.RollBack(); }
                        }
                        if (!done) throw new InvalidOperationException($"{fi.Symbol.FamilyName} could not be locked to '{target.Name}'");
                    }
                }
                Lock(labelInst, pLc, lc);
                Lock(valueInst, pVl, vl);
                t.Commit();
            }

            // Flex: label column doubled, three lines tall.
            using (var t = new Transaction(fam, "flex"))
            {
                t.Start();
                FamilyParameter lw = fm.get_Parameter("Label Width"), lines = fm.get_Parameter("Lines"), lh = fm.get_Parameter("Line Height");
                double w2 = 2 * (fm.CurrentType.AsDouble(lw) ?? 0), inset = fm.CurrentType.AsDouble(fm.get_Parameter("Text Inset")) ?? 0;
                fm.Set(lw, w2);
                fm.Set(lines, 3);
                fam.Regenerate();
                double mid = 3 * (fm.CurrentType.AsDouble(lh) ?? 0) / 2;
                XYZ a = ((LocationPoint)labelInst.Location).Point, b = ((LocationPoint)valueInst.Location).Point;
                Check("label stays centred in a doubled label column, mid-height of 3 lines",
                    Math.Abs(a.X - w2 / 2) < 1e-6 && Math.Abs(a.Y + mid) < 1e-6, $"({SpMm(a.X)}, {SpMm(a.Y)}) want ({SpMm(w2 / 2)}, {SpMm(-mid)})");
                Check("value follows the column edge + inset, mid-height of 3 lines",
                    Math.Abs(b.X - (w2 + inset)) < 1e-6 && Math.Abs(b.Y + mid) < 1e-6, $"({SpMm(b.X)}, {SpMm(b.Y)}) want ({SpMm(w2 + inset)}, {SpMm(-mid)})");
                t.RollBack();
            }
            fam.Save();
            DwDropBackups(path);
            Info("saved " + path);
        }
        catch (Exception ex) { Fail(_scenario, ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
        finally { fam.Close(false); }
    }
}
#endif
