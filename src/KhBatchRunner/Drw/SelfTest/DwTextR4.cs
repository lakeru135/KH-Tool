#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.DoorWindow;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW TEXT R4", DW_DEV=1, 2026-10-22): the user's sizes, header text 3 mm and
/// table text 2 mm, in the bundled KH_DW families of the Revit year this runs in; the Value label's
/// box made 1000 mm wide, so Revit never wraps it (the builder writes the line breaks, by the cell's
/// width); and the type parameter <see cref="BundledFamilies.VersionParameter"/> = <see cref="LegendBuilder.FamilyVersion"/>,
/// which tells a build to reload older copies. Edits the copies in DW_BUILD_OUT in place.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void DwTextR4(Document doc)
    {
        string dir = Environment.GetEnvironmentVariable("DW_BUILD_OUT") is { Length: > 0 } o ? o : Path.GetDirectoryName(_report)!;
        Info($"Revit {doc.Application.VersionNumber}, folder {dir}");
        string F(string name) => Path.Combine(dir, name + ".rfa");

        DwEditFamily(doc, F(LegendBuilder.MarkFamily), null, fam => { DwTextType(fam, "3mm", 3.0, retype: true); DwVersion(fam); });
        DwEditFamily(doc, F(LegendBuilder.DescriptionFamily), null, fam => { DwTextType(fam, "3mm", 3.0, retype: true); DwVersion(fam); });
        DwEditFamily(doc, F(LegendBuilder.TitleFamily), null, fam => { DwTextType(fam, "2mm", 2.0, retype: true); DwWide(fam, 1000, noBreak: false); DwVersion(fam); });
        DwEditFamily(doc, F(LegendBuilder.HeaderFamily), null, DwVersion);

        // The row nests Label Center (its Label) and Label Left (its Value): the centre one edited
        // in place, the left one loaded from the file just fixed.
        DwEditFamily(doc, F(LegendBuilder.RowFamily), null, row =>
        {
            Family? centre = new FilteredElementCollector(row).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == "KH_DW Label Center");
            if (centre == null) throw new InvalidOperationException("KH_DW_Row nests no KH_DW Label Center");
            Document nested = row.EditFamily(centre);
            try
            {
                using (var t = new Transaction(nested, "2 mm"))
                {
                    t.Start();
                    DwTextType(nested, "2mm", 2.0, retype: true);
                    t.Commit();
                }
                nested.LoadFamily(row, new DwOverwrite());
            }
            finally { nested.Close(false); }
            Document left = doc.Application.OpenDocumentFile(F(LegendBuilder.TitleFamily));
            try { left.LoadFamily(row, new DwOverwrite()); }
            finally { left.Close(false); }
            using (var t = new Transaction(row, "version"))
            {
                t.Start();
                DwVersion(row);
                t.Commit();
            }
        }, transaction: false);
        DwTextR4Probe(doc, dir);
    }

    /// <summary>
    /// Dev-only (scenario "DW TEXT R5", DW_DEV=1, 2026-10-22): families v3. The Value / title label's
    /// box back to <paramref name="mm"/> narrow, with "Wrap between parameters only" on, so it never
    /// wraps a value by itself (the builder writes the line breaks) and the legend's extents end at
    /// its cells (v2's 1000 mm box pushed them a metre right). Reloaded into KH_DW_Row; every family
    /// at version 3. Edits the copies in DW_BUILD_OUT in place.
    /// </summary>
    private static void DwTextR5(Document doc)
    {
        string dir = Environment.GetEnvironmentVariable("DW_BUILD_OUT") is { Length: > 0 } o ? o : Path.GetDirectoryName(_report)!;
        Info($"Revit {doc.Application.VersionNumber}, folder {dir}");
        string F(string name) => Path.Combine(dir, name + ".rfa");
        // The header's mark and description too: their boxes (about 50 and 150 mm) reached past the
        // cells as well, 17 mm left of the first and 102 mm right of the last (DW legend, 2026-10-22).
        foreach (string name in new[] { LegendBuilder.TitleFamily, LegendBuilder.MarkFamily, LegendBuilder.DescriptionFamily })
            DwEditFamily(doc, F(name), null, fam => { DwWide(fam, 5, noBreak: true); DwVersion(fam); });
        DwEditFamily(doc, F(LegendBuilder.HeaderFamily), null, DwVersion);
        DwEditFamily(doc, F(LegendBuilder.RowFamily), null, row =>
        {
            Document left = doc.Application.OpenDocumentFile(F(LegendBuilder.TitleFamily));
            try { left.LoadFamily(row, new DwOverwrite()); }
            finally { left.Close(false); }
            // Its label column's centred label too: wider than the column, it reached 15 mm left of
            // the first cells (DW legend, 2026-10-22).
            Family centre = new FilteredElementCollector(row).OfClass(typeof(Family)).Cast<Family>().First(f => f.Name == "KH_DW Label Center");
            Document nested = row.EditFamily(centre);
            try
            {
                using (var tc = new Transaction(nested, "narrow"))
                {
                    tc.Start();
                    DwWide(nested, 5, noBreak: true);
                    tc.Commit();
                }
                nested.LoadFamily(row, new DwOverwrite());
            }
            finally { nested.Close(false); }
            using var t = new Transaction(row, "version");
            t.Start();
            DwVersion(row);
            t.Commit();
        }, transaction: false);
        DwTextR4Probe(doc, dir);
        foreach (string name in new[] { LegendBuilder.TitleFamily, LegendBuilder.MarkFamily, LegendBuilder.DescriptionFamily })
        {
            Document check = doc.Application.OpenDocumentFile(F(name));
            try
            {
                foreach (TextElement label in new FilteredElementCollector(check).OfClass(typeof(TextElement)).Cast<TextElement>().Where(t => t is not TextNote))
                    Check($"DW TEXT R5: {name} box narrow, no wrap inside a value",
                        label.Width * 304.8 < 10 && label.get_Parameter(BuiltInParameter.TAG_NO_BREAK_PARAM_STRINGS)?.AsInteger() == 1,
                        $"{label.Width * 304.8:0.#} mm");
            }
            finally { check.Close(false); }
        }
    }

    /// <summary>The label's box made <paramref name="mm"/> wide, the side its text is aligned to
    /// where it was (the left edge, or the middle of a centred one); <paramref name="noBreak"/> sets
    /// "Wrap between parameters only".</summary>
    private static void DwWide(Document fam, double mm, bool noBreak)
    {
        foreach (TextElement label in new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>().Where(t => t is not TextNote))
        {
            label.get_Parameter(BuiltInParameter.TAG_NO_BREAK_PARAM_STRINGS)?.Set(noBreak ? 1 : 0);
            BoundingBoxXYZ? before = label.get_BoundingBox(null);
            label.Width = mm / 304.8;
            fam.Regenerate();
            BoundingBoxXYZ? after = label.get_BoundingBox(null);
            double Anchor(BoundingBoxXYZ b) => label.HorizontalAlignment switch
            {
                HorizontalTextAlignment.Center => (b.Min.X + b.Max.X) / 2,
                HorizontalTextAlignment.Right => b.Max.X,
                _ => b.Min.X
            };
            if (before != null && after != null && Math.Abs(Anchor(after) - Anchor(before)) > 1e-6)
                ElementTransformUtils.MoveElement(fam, label.Id, new XYZ(Anchor(before) - Anchor(after), 0, 0));
            Info($"  {fam.Title}: label width {label.Width * 304.8:0} mm, left {(before?.Min.X ?? 0) * 304.8:0.00} mm kept");
        }
    }

    /// <summary>The type parameter a build reads to know the model's copy is older than the bundled one.</summary>
    private static void DwVersion(Document fam)
    {
        FamilyManager fm = fam.FamilyManager;
        FamilyParameter v = fm.get_Parameter(BundledFamilies.VersionParameter)
            ?? fm.AddParameter(BundledFamilies.VersionParameter, GroupTypeId.General, SpecTypeId.Int.Integer, false);
        if (fm.Types.Size == 0) fm.NewType(fam.Title.Replace(".rfa", ""));
        foreach (FamilyType t in fm.Types)
        {
            fm.CurrentType = t;
            fm.Set(v, LegendBuilder.FamilyVersion);
        }
        Info($"  {fam.Title}: {BundledFamilies.VersionParameter} = {LegendBuilder.FamilyVersion}");
    }

    private static void DwTextR4Probe(Document doc, string dir)
    {
        foreach (string name in LegendBuilder.Families)
        {
            Document fam = doc.Application.OpenDocumentFile(Path.Combine(dir, name + ".rfa"));
            try
            {
                FamilyManager fm = fam.FamilyManager;
                FamilyParameter? v = fm.get_Parameter(BundledFamilies.VersionParameter);
                Check($"DW TEXT R4: {name} carries version {LegendBuilder.FamilyVersion}",
                    v != null && fm.Types.Cast<FamilyType>().All(t => t.AsInteger(v) == LegendBuilder.FamilyVersion));
                var sizes = new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>().Where(t => t is not TextNote)
                    .Select(t => fam.GetElement(t.GetTypeId()).get_Parameter(BuiltInParameter.TEXT_SIZE).AsDouble() * 304.8).ToList();
                double want = name is LegendBuilder.MarkFamily or LegendBuilder.DescriptionFamily ? 3 : 2;
                if (sizes.Count > 0)
                    Check($"DW TEXT R4: {name} labels {want} mm", sizes.All(s => Math.Abs(s - want) < 1e-6), string.Join(", ", sizes.Select(s => s.ToString("0.00"))));
            }
            finally { fam.Close(false); }
        }
    }
}
#endif
