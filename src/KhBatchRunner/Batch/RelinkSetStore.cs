using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves named Internal Relink input sets as JSON under
/// <c>%AppData%\KhBatchRunner\RelinkSets\*.json</c> (one file per named set) plus a single
/// last-used snapshot at <c>%AppData%\KhBatchRunner\relink-lastused.json</c> — mirrors
/// <see cref="InitiateSetStore"/> / <see cref="CutModelSetStore"/> / <see cref="Interop.ChecksetSetStore"/>.
/// Best-effort throughout: a read/write failure never blocks a run.
/// </summary>
public static class RelinkSetStore
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
            string dir = Settings.SettingsPaths.Folder("RelinkSets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string LastUsedPath => Settings.SettingsPaths.File("relink-lastused.json");

    public static List<RelinkInputSet> LoadAll()
    {
        var sets = new List<RelinkInputSet>();
        foreach (string file in Directory.EnumerateFiles(SetsDirectory, "*.json"))
        {
            try
            {
                RelinkInputSet? set =
                    JsonSerializer.Deserialize<RelinkInputSet>(File.ReadAllText(file), JsonOptions);
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

    public static void Save(RelinkInputSet set) =>
        File.WriteAllText(PathFor(set.Name), JsonSerializer.Serialize(set, JsonOptions));

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Persists the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(RelinkInputSet snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastUsedPath)!);
            File.WriteAllText(LastUsedPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch { /* best effort */ }
    }

    /// <summary>The last-used inputs, or null when none saved yet (or the file is unreadable).</summary>
    public static RelinkInputSet? TryLoadLastUsed()
    {
        try
        {
            if (!File.Exists(LastUsedPath)) return null;
            return JsonSerializer.Deserialize<RelinkInputSet>(File.ReadAllText(LastUsedPath), JsonOptions);
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
