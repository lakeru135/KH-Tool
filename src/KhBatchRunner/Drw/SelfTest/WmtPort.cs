#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "WMT PORT", DW_DEV=1, 2026-10-22): Wall Mat Tag's bundled family
/// "KH_Anno_Wall Mat tag" for the Revit year this runs in. A family cannot be saved back to an older
/// Revit and the API cannot create a label, so this starts from that year's library
/// "M_Material Tag.rfa" (one label of the material's Description) and copies its label into the
/// 2026 family's shape, as probed: four Yes/No type parameters (Down / Up / Left / Right), each
/// switching on one label and one triangle, and one type per direction. Folder: DW_BUILD_OUT.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void WmtPortFamily(Document doc)
    {
        string dir = Environment.GetEnvironmentVariable("DW_BUILD_OUT") is { Length: > 0 } o ? o : Path.GetDirectoryName(_report)!;
        string year = doc.Application.VersionNumber;
        string source = $@"C:\ProgramData\Autodesk\RVT {year}\Libraries\English\US\Annotations\Architectural\M_Material Tag.rfa";
        if (!File.Exists(source))
        {
            Fail(_scenario, "no library family " + source);
            return;
        }
        Directory.CreateDirectory(dir);
        string to = Path.Combine(dir, "KH_Anno_Wall Mat tag.rfa");
        File.Copy(source, to, true);
        File.SetAttributes(to, FileAttributes.Normal);
        Info($"Revit {year}: {source} -> {to}");
        DwEditFamily(doc, to, null, WmtPortEdit);
        WmtPortProbe(doc, to);
    }

    /// <summary>Each direction's label centre and triangle (mm), as the 2026 family draws them; the
    /// triangle's tip is at the origin, where the tag points.</summary>
    private static readonly (string Name, double X, double Y, (double X, double Y)[] Triangle)[] WmtShapes =
    {
        ("Up", 0.006574257, -2.922779512, new[] { (0.0, 0.0), (-2.309401077, -4.0), (2.309401077, -4.0) }),
        ("Down", 0.006574257, 3.077154512, new[] { (0.0, 0.0), (-2.309401077, 4.0), (2.309401077, 4.0) }),
        ("Left", 2.583389852, -0.04, new[] { (-0.026889660, -0.046574257), (4.0, -2.371500086), (4.0, 2.278351572) }),
        ("Right", -2.557169171, -0.04, new[] { (-0.026889660, -0.046574257), (-4.0, -2.340450581), (-4.0, 2.247302068) })
    };

    private static void WmtPortEdit(Document fam)
    {
        var labels = new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>()
            .Where(te => te is not TextNote).ToList();
        if (labels.Count != 1) throw new InvalidOperationException($"{fam.Title}: {labels.Count} labels, want exactly 1");
        TextElement first = labels[0];
        View view = (View)fam.GetElement(first.OwnerViewId);

        // The label's type as 2026's "3mm": 1.2 mm Trebuchet MS bold, width factor 0.8, transparent.
        var type = (TextElementType)fam.GetElement(first.GetTypeId());
        type.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(1.2 / 304.8);
        type.get_Parameter(BuiltInParameter.TEXT_FONT).Set("Trebuchet MS");
        type.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD).Set(1);
        type.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE).Set(0.8);
        type.get_Parameter(BuiltInParameter.TEXT_BACKGROUND).Set(1);
        if (type.Name != "3mm") type.Name = "3mm";

        FamilyManager fm = fam.FamilyManager;
        var switches = new Dictionary<string, FamilyParameter>();
        foreach (var shape in WmtShapes)
        {
            switches[shape.Name] = fm.get_Parameter(shape.Name)
                ?? fm.AddParameter(shape.Name, new ForgeTypeId(string.Empty), SpecTypeId.Boolean.YesNo, false);

            TextElement label = shape.Name == WmtShapes[0].Name ? first
                : (TextElement)fam.GetElement(ElementTransformUtils.CopyElement(fam, first.Id, XYZ.Zero).First());
            label.HorizontalAlignment = HorizontalTextAlignment.Center;
            label.VerticalAlignment = VerticalTextAlignment.Middle;
            try { label.Width = 3.5 / 304.8; }
            catch (Exception ex) { Info($"  label width not set by the API ({ex.Message})"); }
            fam.Regenerate();
            ElementTransformUtils.MoveElement(fam, label.Id, new XYZ(shape.X / 304.8, shape.Y / 304.8, 0) - label.Coord);
            WmtShow(fm, label, switches[shape.Name]);

            for (int i = 0; i < 3; i++)
            {
                (double ax, double ay) = shape.Triangle[i];
                (double bx, double by) = shape.Triangle[(i + 1) % 3];
                DetailCurve line = fam.FamilyCreate.NewDetailCurve(view,
                    Line.CreateBound(new XYZ(ax / 304.8, ay / 304.8, 0), new XYZ(bx / 304.8, by / 304.8, 0)));
                WmtShow(fm, line, switches[shape.Name]);
            }
        }

        // One type per direction, named for the way its triangle points.
        FamilyType? blank = fm.CurrentType;
        foreach (var shape in WmtShapes)
        {
            FamilyType t = fm.Types.Cast<FamilyType>().FirstOrDefault(x => x.Name == shape.Name)
                ?? (blank != null && fm.Types.Size == 1 && blank.Name != shape.Name && !WmtShapes.Any(s => s.Name == blank.Name)
                    ? WmtRename(fm, shape.Name)
                    : fm.NewType(shape.Name));
            fm.CurrentType = t;
            foreach (var s in WmtShapes) fm.Set(switches[s.Name], s.Name == shape.Name ? 1 : 0);
        }
    }

    private static FamilyType WmtRename(FamilyManager fm, string name)
    {
        fm.RenameCurrentType(name);
        return fm.CurrentType;
    }

    private static void WmtShow(FamilyManager fm, Element e, FamilyParameter yes)
    {
        Parameter visible = e.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)
            ?? throw new InvalidOperationException(e.GetType().Name + " has no Visible parameter");
        fm.AssociateElementParameterToFamilyParameter(visible, yes);
    }

    /// <summary>The saved family read back: four labels and twelve lines, each switched by its
    /// direction, and the four types.</summary>
    private static void WmtPortProbe(Document doc, string path)
    {
        Document fam = doc.Application.OpenDocumentFile(path);
        try
        {
            FamilyManager fm = fam.FamilyManager;
            var labels = new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>().Where(te => te is not TextNote).ToList();
            var lines = new FilteredElementCollector(fam).OfClass(typeof(CurveElement)).ToElements();
            string By(Element e) => fm.GetAssociatedFamilyParameter(e.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM))?.Definition.Name ?? "";
            Check("WMT PORT: category Material Tags", fam.OwnerFamily.FamilyCategory?.Id.Equals(new ElementId(BuiltInCategory.OST_MaterialTags)) == true,
                fam.OwnerFamily.FamilyCategory?.Name ?? "none");
            Check("WMT PORT: one label per direction", labels.Count == 4 && WmtShapes.All(s => labels.Count(l => By(l) == s.Name) == 1),
                string.Join(", ", labels.Select(l => $"{By(l)} @ {l.Coord.X * 304.8:0.00},{l.Coord.Y * 304.8:0.00}")));
            Check("WMT PORT: three lines per direction", lines.Count == 12 && WmtShapes.All(s => lines.Count(l => By(l) == s.Name) == 3),
                string.Join(", ", lines.Select(By)));
            Check("WMT PORT: the four types", WmtShapes.All(s => fm.Types.Cast<FamilyType>().Any(t => t.Name == s.Name)) && fm.Types.Size == 4,
                string.Join(", ", fm.Types.Cast<FamilyType>().Select(t => t.Name)));
            foreach (FamilyType t in fm.Types)
                Check($"WMT PORT: type {t.Name} switches on only its own direction",
                    WmtShapes.All(s => t.AsInteger(fm.get_Parameter(s.Name)) == (s.Name == t.Name ? 1 : 0)));
            var type = (TextElementType)fam.GetElement(labels[0].GetTypeId());
            Check("WMT PORT: label type 3mm = 1.2 mm Trebuchet MS bold, transparent",
                type.Name == "3mm" && Math.Abs(type.get_Parameter(BuiltInParameter.TEXT_SIZE).AsDouble() * 304.8 - 1.2) < 1e-6 &&
                type.get_Parameter(BuiltInParameter.TEXT_FONT).AsString() == "Trebuchet MS" &&
                type.get_Parameter(BuiltInParameter.TEXT_BACKGROUND).AsInteger() == 1,
                $"{type.Name} {type.get_Parameter(BuiltInParameter.TEXT_SIZE).AsDouble() * 304.8:0.00} mm {type.get_Parameter(BuiltInParameter.TEXT_FONT).AsString()}");
        }
        finally { fam.Close(false); }
    }
}
#endif
