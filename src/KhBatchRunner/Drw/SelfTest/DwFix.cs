#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW FIX", 2026-10-20): the label families' '3mm' text type was 3/32" (2.38 mm)
/// renamed. Sets it to 3 mm in every KH_DW label family, makes 5 mm header copies (KH_DW Header
/// Center / Left) and reloads the fixed labels into KH_DW_Row. The user's originals are copied to
/// _orig\ first. Folder: DW_BUILD_OUT.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void DwFixFamilies(Document doc)
    {
        string dir = Environment.GetEnvironmentVariable("DW_BUILD_OUT") is { Length: > 0 } o ? o : Path.GetDirectoryName(_report)!;
        string orig = Path.Combine(dir, "_orig");
        Directory.CreateDirectory(orig);
        foreach (string f in Directory.GetFiles(dir, "*.rfa"))
        {
            string to = Path.Combine(orig, Path.GetFileName(f));
            if (!File.Exists(to)) File.Copy(f, to);
        }
        Info("originals kept in " + orig);

        string[] labels = { "KH_DW Label Center", "KH_DW Label Left", "KH_DW Label", "KH_DW Value" };
        foreach (string name in labels)
            DwEditFamily(doc, Path.Combine(dir, name + ".rfa"), null, fam => DwTextType(fam, "3mm", 3.0, retype: false));

        DwEditFamily(doc, Path.Combine(dir, "KH_DW Label Center.rfa"), Path.Combine(dir, "KH_DW Header Center.rfa"),
            fam => DwTextType(fam, "5mm", 5.0, retype: true));
        DwEditFamily(doc, Path.Combine(dir, "KH_DW Label Left.rfa"), Path.Combine(dir, "KH_DW Header Left.rfa"),
            fam => DwTextType(fam, "5mm", 5.0, retype: true));

        // The row nests Label Center (its Label) and Label Left (its Value): reload the fixed ones.
        DwEditFamily(doc, Path.Combine(dir, "KH_DW_Row.rfa"), null, fam =>
        {
            // Document.LoadFamily(path) returned false here and left the old copies in place; loading
            // FROM the open label family (Load into Project) is what reloads them.
            foreach (string name in new[] { "KH_DW Label Center", "KH_DW Label Left" })
            {
                Document label = doc.Application.OpenDocumentFile(Path.Combine(dir, name + ".rfa"));
                try
                {
                    Family loaded = label.LoadFamily(fam, new DwOverwrite());
                    Info($"  reloaded {name} into the row: {loaded?.Name}");
                }
                finally { label.Close(false); }
            }
        }, transaction: false);
    }

    /// <summary>Sets (making it if needed) a TH Sarabun New text type of <paramref name="mm"/> paper
    /// mm; with <paramref name="retype"/> every label in the family is switched to it.</summary>
    private static void DwTextType(Document fam, string name, double mm, bool retype)
    {
        var types = new FilteredElementCollector(fam).OfClass(typeof(TextElementType)).Cast<TextElementType>().ToList();
        // Only types of the labels' own kind: the 2024 families also hold a text NOTE type named
        // '3mm', which a label refuses ("The type typeId is not valid for this element").
        TextElement? label = new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>().FirstOrDefault(te => te is not TextNote);
        Type kind = label != null ? fam.GetElement(label.GetTypeId()).GetType() : typeof(TextElementType);
        TextElementType? t = types.FirstOrDefault(x => x.Name == name && x.GetType() == kind)
                             ?? types.FirstOrDefault(x => x.Name == name + " label" && x.GetType() == kind);
        if (t == null)
        {
            TextElementType from = label != null ? (TextElementType)fam.GetElement(label.GetTypeId()) : types.First(x => x.Name == "3mm");
            t = (TextElementType)from.Duplicate(types.Any(x => x.Name == name) ? name + " label" : name);
        }
        t.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(mm / 304.8);
        t.get_Parameter(BuiltInParameter.TEXT_FONT).Set("TH Sarabun New");
        // Transparent: an opaque label box masked the row or title beside it (legend export 2026-10-20).
        foreach (TextElementType any in new FilteredElementCollector(fam).OfClass(typeof(TextElementType)).Cast<TextElementType>())
            any.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1);
        if (retype)
            foreach (TextElement te in new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>())
                te.ChangeTypeId(t.Id);
        fam.Regenerate();
        var used = new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>()
            .Select(te => fam.GetElement(te.GetTypeId()) as TextElementType)
            .Select(x => $"{x?.Name} {(x?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0) * 304.8:0.00} mm");
        Info($"  {fam.Title}: '{name}' = {mm} mm; labels use " + string.Join(", ", used));
    }

    private static void DwEditFamily(Document doc, string path, string? saveAs, Action<Document> edit, bool transaction = true)
    {
        Document fam = doc.Application.OpenDocumentFile(path);
        try
        {
            if (transaction)
            {
                using var t = new Transaction(fam, "KH DW fix");
                t.Start();
                edit(fam);
                t.Commit();
            }
            else edit(fam);
            string to = saveAs ?? path;
            if (saveAs == null) fam.Save();
            else fam.SaveAs(to, new SaveAsOptions { OverwriteExistingFile = true });
            DwDropBackups(to);
            Info("saved " + Path.GetFileName(to));
        }
        catch (Exception ex) { Fail(_scenario, Path.GetFileName(path) + ": " + ex.GetType().Name + ": " + ex.Message); }
        finally { fam.Close(false); }
    }

    private sealed class DwOverwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return true;
        }
    }
}
#endif
