using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves Initiate-to-Forma input sets as JSON under
/// <c>%AppData%\KhBatchRunner\InitiateSets\*.json</c> (one file per named set) — mirrors
/// <see cref="Scripts.ScriptSetStore"/>. Additionally keeps a single LAST-USED snapshot at
/// <c>%AppData%\KhBatchRunner\initiate-lastused.json</c> (outside the sets folder so it never
/// shows up as a set), written on every successful Initiate commit and restored when the
/// dialog opens.
/// </summary>
public static class InitiateSetStore
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
            string dir = Settings.SettingsPaths.Folder("InitiateSets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string LastUsedPath => Settings.SettingsPaths.File("initiate-lastused.json");

    public static List<InitiateInputSet> LoadAll()
    {
        var sets = new List<InitiateInputSet>();
        foreach (string file in Directory.EnumerateFiles(SetsDirectory, "*.json"))
        {
            try
            {
                InitiateInputSet? set =
                    JsonSerializer.Deserialize<InitiateInputSet>(File.ReadAllText(file), JsonOptions);
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

    public static void Save(InitiateInputSet set) =>
        File.WriteAllText(PathFor(set.Name), JsonSerializer.Serialize(set, JsonOptions));

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(InitiateInputSet snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastUsedPath)!);
            File.WriteAllText(LastUsedPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch { /* best effort */ }
    }

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public static InitiateInputSet? TryLoadLastUsed()
    {
        try
        {
            if (!File.Exists(LastUsedPath)) return null;
            return JsonSerializer.Deserialize<InitiateInputSet>(File.ReadAllText(LastUsedPath), JsonOptions);
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
