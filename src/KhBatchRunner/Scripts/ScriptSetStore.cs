using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Scripts;

/// <summary>
/// Loads/saves DYN script sets as JSON under
/// <c>%AppData%\KhBatchRunner\ScriptSets\*.json</c> (one file per set).
/// Mirrors <see cref="Presets.PresetStore"/>.
/// </summary>
public static class ScriptSetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ScriptSetsDirectory
    {
        get
        {
            string dir = Settings.SettingsPaths.Folder("ScriptSets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static List<ScriptSet> LoadAll()
    {
        var sets = new List<ScriptSet>();
        foreach (string file in Directory.EnumerateFiles(ScriptSetsDirectory, "*.json"))
        {
            try
            {
                ScriptSet? set = JsonSerializer.Deserialize<ScriptSet>(File.ReadAllText(file), JsonOptions);
                if (set != null && !string.IsNullOrWhiteSpace(set.Name))
                {
                    // Back-fill a stable id for sets saved before ids existed, and persist it once so
                    // it stays constant across loads (schedules reference the id, not the name).
                    if (string.IsNullOrWhiteSpace(set.Id))
                    {
                        set.Id = Guid.NewGuid().ToString("N");
                        try { File.WriteAllText(file, JsonSerializer.Serialize(set, JsonOptions)); }
                        catch { /* read-only dir etc. — id still stable within this session */ }
                    }
                    sets.Add(set);
                }
            }
            catch
            {
                // Skip malformed files rather than failing the whole command.
            }
        }
        return sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Save(ScriptSet set) =>
        File.WriteAllText(PathFor(set.Name), JsonSerializer.Serialize(set, JsonOptions));

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(string name)
    {
        string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(ScriptSetsDirectory, safeName + ".json");
    }
}
