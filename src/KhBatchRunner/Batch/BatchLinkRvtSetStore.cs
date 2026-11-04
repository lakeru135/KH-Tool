using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves named Batch Link RVT input sets as JSON under
/// <c>%AppData%\KhBatchRunner\BatchLinkSets\*.json</c> (one file per named set) plus a single
/// last-used snapshot at <c>%AppData%\KhBatchRunner\batchlink-lastused.json</c> — mirrors
/// <see cref="RelinkSetStore"/> / <see cref="InitiateSetStore"/> / <see cref="CutModelSetStore"/>.
/// Best-effort throughout: a read/write failure never blocks a run.
/// </summary>
public static class BatchLinkRvtSetStore
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
            string dir = Settings.SettingsPaths.Folder("BatchLinkSets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string LastUsedPath => Settings.SettingsPaths.File("batchlink-lastused.json");

    public static List<BatchLinkRvtInputSet> LoadAll()
    {
        var sets = new List<BatchLinkRvtInputSet>();
        foreach (string file in Directory.EnumerateFiles(SetsDirectory, "*.json"))
        {
            try
            {
                BatchLinkRvtInputSet? set =
                    JsonSerializer.Deserialize<BatchLinkRvtInputSet>(File.ReadAllText(file), JsonOptions);
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

    public static void Save(BatchLinkRvtInputSet set) =>
        File.WriteAllText(PathFor(set.Name), JsonSerializer.Serialize(set, JsonOptions));

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Persists the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(BatchLinkRvtInputSet snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastUsedPath)!);
            File.WriteAllText(LastUsedPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch { /* best effort */ }
    }

    /// <summary>The last-used inputs, or null when none saved yet (or the file is unreadable).</summary>
    public static BatchLinkRvtInputSet? TryLoadLastUsed()
    {
        try
        {
            if (!File.Exists(LastUsedPath)) return null;
            return JsonSerializer.Deserialize<BatchLinkRvtInputSet>(File.ReadAllText(LastUsedPath), JsonOptions);
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
