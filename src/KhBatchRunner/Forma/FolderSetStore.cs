using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Forma;

/// <summary>
/// Loads/saves Forma folder sets as JSON under <c>%AppData%\KhBatchRunner\FolderSets\*.json</c>
/// (one file per set). Mirrors <see cref="Presets.PresetStore"/> for model presets — used by
/// Check Sheet Publish so a folder selection can be reused across runs.
/// </summary>
public static class FolderSetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string Directory_
    {
        get
        {
            string dir = Settings.SettingsPaths.Folder("FolderSets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static List<FolderSet> LoadAll()
    {
        var sets = new List<FolderSet>();
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory_, "*.json"))
        {
            try
            {
                FolderSet? set = JsonSerializer.Deserialize<FolderSet>(File.ReadAllText(file), JsonOptions);
                if (set != null && !string.IsNullOrWhiteSpace(set.Name))
                    sets.Add(set);
            }
            catch
            {
                // Skip a malformed file rather than failing the whole load.
            }
        }
        return sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Save(FolderSet set) =>
        File.WriteAllText(PathFor(set.Name), JsonSerializer.Serialize(set, JsonOptions));

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(string name)
    {
        string safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Directory_, safe + ".json");
    }
}
