using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves Cut-model input sets as JSON under
/// <c>%AppData%\KhBatchRunner\CutModelSets\*.json</c> (one file per named set) — the Cut-model
/// twin of <see cref="InitiateSetStore"/>. Additionally keeps a single LAST-USED snapshot at
/// <c>%AppData%\KhBatchRunner\cutmodel-lastused.json</c> (outside the sets folder so it never
/// shows up as a set), written on every successful commit and restored when the dialog opens.
/// </summary>
public static class CutModelSetStore
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
            string dir = Settings.SettingsPaths.Folder("CutModelSets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string LastUsedPath => Settings.SettingsPaths.File("cutmodel-lastused.json");

    public static List<CutModelInputSet> LoadAll()
    {
        var sets = new List<CutModelInputSet>();
        foreach (string file in Directory.EnumerateFiles(SetsDirectory, "*.json"))
        {
            try
            {
                CutModelInputSet? set =
                    JsonSerializer.Deserialize<CutModelInputSet>(File.ReadAllText(file), JsonOptions);
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

    public static void Save(CutModelInputSet set) =>
        File.WriteAllText(PathFor(set.Name), JsonSerializer.Serialize(set, JsonOptions));

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(CutModelInputSet snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastUsedPath)!);
            File.WriteAllText(LastUsedPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch { /* best effort */ }
    }

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public static CutModelInputSet? TryLoadLastUsed()
    {
        try
        {
            if (!File.Exists(LastUsedPath)) return null;
            return JsonSerializer.Deserialize<CutModelInputSet>(File.ReadAllText(LastUsedPath), JsonOptions);
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
