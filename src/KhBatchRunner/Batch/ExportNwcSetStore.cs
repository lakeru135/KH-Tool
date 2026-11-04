using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves Export-NWC option sets as JSON under
/// <c>%AppData%\KhBatchRunner\ExportNwcSets\*.json</c> (one file per named set) — the Export-NWC
/// twin of <see cref="CutModelSetStore"/>, down to the single LAST-USED snapshot kept at
/// <c>%AppData%\KhBatchRunner\exportnwc-lastused.json</c>, outside the sets folder so it never
/// shows up as a set.
/// </summary>
public static class ExportNwcSetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string SetsDirectory
    {
        get
        {
            string dir = Settings.SettingsPaths.Folder("ExportNwcSets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string LastUsedPath => Settings.SettingsPaths.File("exportnwc-lastused.json");

    public static List<ExportNwcInputSet> LoadAll()
    {
        var sets = new List<ExportNwcInputSet>();
        foreach (string file in Directory.EnumerateFiles(SetsDirectory, "*.json"))
        {
            try
            {
                ExportNwcInputSet? set =
                    JsonSerializer.Deserialize<ExportNwcInputSet>(File.ReadAllText(file), JsonOptions);
                if (set != null && !string.IsNullOrWhiteSpace(set.Name))
                    sets.Add(set);
            }
            catch
            {
                // Skip malformed files rather than failing the whole command.
            }
        }
        return sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Save(ExportNwcInputSet set) =>
        File.WriteAllText(PathFor(set.Name), JsonSerializer.Serialize(set, JsonOptions));

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write
    /// never blocks a run).</summary>
    public static void SaveLastUsed(ExportNwcInputSet snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastUsedPath)!);
            File.WriteAllText(LastUsedPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch { /* best effort */ }
    }

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public static ExportNwcInputSet? TryLoadLastUsed()
    {
        try
        {
            if (!File.Exists(LastUsedPath)) return null;
            return JsonSerializer.Deserialize<ExportNwcInputSet>(File.ReadAllText(LastUsedPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string PathFor(string name)
    {
        string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(SetsDirectory, safeName + ".json");
    }
}
