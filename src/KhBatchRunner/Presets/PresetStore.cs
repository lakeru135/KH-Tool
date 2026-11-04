using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Presets;

/// <summary>
/// Loads/saves model presets as JSON files under
/// <c>%AppData%\KhBatchRunner\Presets\*.json</c> (one file per preset).
/// The Forma crawler (Phase 1-2) will write these; for now they can be hand-authored.
/// </summary>
public static class PresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string PresetsDirectory
    {
        get
        {
            string dir = Settings.SettingsPaths.Folder("Presets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static List<ModelPreset> LoadAll()
    {
        var presets = new List<ModelPreset>();
        foreach (string file in Directory.EnumerateFiles(PresetsDirectory, "*.json"))
        {
            try
            {
                ModelPreset? preset = JsonSerializer.Deserialize<ModelPreset>(
                    File.ReadAllText(file), JsonOptions);
                if (preset != null && !string.IsNullOrWhiteSpace(preset.Name))
                {
                    // Back-fill a stable id for sets saved before ids existed, and persist it once so
                    // it stays constant across loads (schedules reference the id, not the name).
                    if (string.IsNullOrWhiteSpace(preset.Id))
                    {
                        preset.Id = Guid.NewGuid().ToString("N");
                        try { File.WriteAllText(file, JsonSerializer.Serialize(preset, JsonOptions)); }
                        catch { /* read-only dir etc. — id still stable within this session */ }
                    }
                    presets.Add(preset);
                }
            }
            catch
            {
                // Skip malformed preset files rather than failing the whole command.
            }
        }
        return presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Save(ModelPreset preset)
    {
        File.WriteAllText(PathFor(preset.Name), JsonSerializer.Serialize(preset, JsonOptions));
    }

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(string name)
    {
        string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(PresetsDirectory, safeName + ".json");
    }
}
