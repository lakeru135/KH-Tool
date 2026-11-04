using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;

namespace KhBatchRunner.Drw.Annotation;

/// <summary>
/// Families that come with the add-in (embedded, one copy per Revit year) and are loaded into a model
/// the first time a tool needs one the model lacks. A family already in the model is never
/// overwritten: an office's or a project's edited copy wins.
/// </summary>
internal static class BundledFamilies
{
    /// <summary>The Wall Mat Tag family: a material tag with one type per side the tag sits on.</summary>
    public const string WallMatTag = "KH_Anno_Wall Mat tag";

    /// <summary>The Wall Mat Tag family's type for each side the tag sits on beside the face. Its types
    /// are named for the way the triangle points: a tag above the face points down at it.</summary>
    public static string WallMatType(TagPosition sits) => !Has(WallMatTag) ? "" : WallMatTag + " : " + sits switch
    {
        TagPosition.Above => "Down",
        TagPosition.Below => "Up",
        TagPosition.Left => "Right",
        _ => "Left"
    };

    /// <summary>The Wall Mat Tag family's type for elevations: upright, the triangle pointing up at
    /// the surface.</summary>
    public static string WallMatElevationType() => !Has(WallMatTag) ? "" : WallMatTag + " : Up";

    /// <summary>The Wall Mat Tag family's "Family : Type" labels (none when this year has no copy).</summary>
    public static List<string> WallMatTypes() =>
        new[] { TagPosition.Above, TagPosition.Below, TagPosition.Left, TagPosition.Right }
            .Select(WallMatType).Where(l => l.Length > 0).ToList();

    private static string Resource(string family) => "Families." + family + ".rfa";

    /// <summary>This build carries <paramref name="family"/> (for its Revit year).</summary>
    public static bool Has(string family) =>
        typeof(BundledFamilies).Assembly.GetManifestResourceNames().Contains(Resource(family));

    /// <summary>Writes the bundled file <paramref name="fileName"/> (e.g. a seed .rvt) into
    /// <paramref name="folder"/>; null when this build carries none for its Revit year.</summary>
    public static string? Extract(string fileName, string folder)
    {
        using Stream? s = typeof(BundledFamilies).Assembly.GetManifestResourceStream("Families." + fileName);
        if (s == null) return null;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, fileName);
        using (FileStream f = File.Create(path)) s.CopyTo(f);
        return path;
    }

    /// <summary>The bundled families that <paramref name="labels"/> ("Family : Type") name and the model
    /// lacks.</summary>
    public static List<string> Missing(Document doc, IEnumerable<string> labels)
    {
        var loaded = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(Family)).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        return labels.Select(l => (l ?? "").Split(new[] { " : " }, 2, StringSplitOptions.None)[0].Trim())
            .Where(f => f.Length > 0 && !loaded.Contains(f) && Has(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A type parameter some bundled families carry: their version. A model's copy with a
    /// lower one (or none: version 1) is older than the bundled copy.</summary>
    public const string VersionParameter = "KH_DW Version";

    /// <summary>The model's version of <paramref name="family"/> (1 when it carries none), or null
    /// when the model lacks it.</summary>
    public static int? VersionIn(Document doc, string family)
    {
        FamilySymbol? s = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(x => string.Equals(x.FamilyName, family, StringComparison.OrdinalIgnoreCase));
        if (s == null) return null;
        return s.LookupParameter(VersionParameter) is { StorageType: StorageType.Integer } p ? p.AsInteger() : 1;
    }

    /// <summary>
    /// Loads the bundled <paramref name="families"/> the model lacks, and reloads those whose copy in
    /// the model is older than <paramref name="version"/> (overwriting it: the user chose, 2026-10-22,
    /// that a newer bundled copy replaces the model's), in one transaction (none open). Returns the
    /// families loaded and those updated.
    /// </summary>
    public static (List<string> Loaded, List<string> Updated) LoadMissingOrOlder(Document doc, IEnumerable<string> families, int version)
    {
        var loaded = new List<string>();
        var updated = new List<string>();
        var todo = families.Where(Has).Select(f => (Family: f, Was: VersionIn(doc, f))).Where(x => x.Was == null || x.Was < version).ToList();
        if (todo.Count == 0) return (loaded, updated);
        string folder = Path.Combine(Path.GetTempPath(), "DrwHelper-Families-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var t = new Transaction(doc, "Load DRW Helper families");
            t.Start();
            foreach (var (family, was) in todo)
            {
                string path = Path.Combine(folder, family + ".rfa");
                using (Stream s = typeof(BundledFamilies).Assembly.GetManifestResourceStream(Resource(family))!)
                using (FileStream f = File.Create(path))
                    s.CopyTo(f);
                bool done = doc.LoadFamily(path, new Overwrite(), out Family _);
                // Revit skips a family identical to the one loaded: then only the version number in
                // the model was off (edited, or an older build's), and is put right.
                if (!done && was != null)
                    foreach (FamilySymbol s in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                                 .Where(x => string.Equals(x.FamilyName, family, StringComparison.OrdinalIgnoreCase)))
                        if (s.LookupParameter(VersionParameter) is { IsReadOnly: false } v) v.Set(version);
                if (done) (was == null ? loaded : updated).Add(family);
            }
            t.Commit();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { /* temp: left for Windows to clear */ }
        }
        return (loaded, updated);
    }

    /// <summary>A newer bundled copy replaces the model's, its type values too (the version).</summary>
    private sealed class Overwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }

    /// <summary>Loads the bundled families <paramref name="labels"/> name that the model lacks, in one
    /// transaction (none open). Returns the families loaded.</summary>
    public static List<string> LoadMissing(Document doc, IEnumerable<string> labels)
    {
        List<string> missing = Missing(doc, labels);
        if (missing.Count == 0) return missing;
        string folder = Path.Combine(Path.GetTempPath(), "DrwHelper-Families-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var loaded = new List<string>();
        try
        {
            using var t = new Transaction(doc, "Load DRW Helper families");
            t.Start();
            foreach (string family in missing)
            {
                string path = Path.Combine(folder, family + ".rfa");
                using (Stream s = typeof(BundledFamilies).Assembly.GetManifestResourceStream(Resource(family))!)
                using (FileStream f = File.Create(path))
                    s.CopyTo(f);
                if (doc.LoadFamily(path, out Family _)) loaded.Add(family);
            }
            t.Commit();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { /* temp: left for Windows to clear */ }
        }
        return loaded;
    }
}
