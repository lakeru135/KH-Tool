using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves "Export IFC + Data" option sets as JSON under
/// <c>%AppData%\KhBatchRunner\ExportIfcSets\*.json</c> (one file per named set), with a single
/// LAST-USED snapshot at <c>%AppData%\KhBatchRunner\exportifc-lastused.json</c> — the twin of
/// <see cref="ExportNwcSetStore"/>.
/// </summary>
public static class ExportIfcSetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SetsDirectory
    {
        get
        {
            string dir = Settings.SettingsPaths.Folder("ExportIfcSets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string LastUsedPath => Settings.SettingsPaths.File("exportifc-lastused.json");

    public static List<ExportIfcInputSet> LoadAll()
    {
        var sets = new List<ExportIfcInputSet>();
        foreach (string file in Directory.EnumerateFiles(SetsDirectory, "*.json"))
        {
            try
            {
                ExportIfcInputSet? set =
                    JsonSerializer.Deserialize<ExportIfcInputSet>(File.ReadAllText(file), JsonOptions);
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

    public static void Save(ExportIfcInputSet set) =>
        File.WriteAllText(PathFor(set.Name), JsonSerializer.Serialize(set, JsonOptions));

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Best effort — a failed write never blocks a run.</summary>
    public static void SaveLastUsed(ExportIfcInputSet snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastUsedPath)!);
            File.WriteAllText(LastUsedPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch { /* best effort */ }
    }

    public static ExportIfcInputSet? TryLoadLastUsed()
    {
        try
        {
            if (!File.Exists(LastUsedPath)) return null;
            return JsonSerializer.Deserialize<ExportIfcInputSet>(File.ReadAllText(LastUsedPath), JsonOptions);
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
