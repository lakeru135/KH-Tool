using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Interop;

/// <summary>
/// Loads/saves named Model Checker checksets as JSON under
/// <c>%AppData%\KhBatchRunner\Checksets\*.json</c> (one file per named set) — mirrors
/// <see cref="Batch.InitiateSetStore"/> / <see cref="Forma.FolderSetStore"/>. Also keeps a single
/// last-used snapshot at <c>%AppData%\KhBatchRunner\checkset-lastused.json</c> (outside the sets
/// folder so it never shows up as a set), written on every Interop commit and restored when a
/// dialog opens. Best-effort throughout — a read/write failure never blocks a run.
/// </summary>
public static class ChecksetSetStore
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
            string dir = Settings.SettingsPaths.Folder("Checksets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string LastUsedPath => Settings.SettingsPaths.File("checkset-lastused.json");

    public static List<ChecksetSet> LoadAll()
    {
        var sets = new List<ChecksetSet>();
        foreach (string file in Directory.EnumerateFiles(SetsDirectory, "*.json"))
        {
            try
            {
                ChecksetSet? set =
                    JsonSerializer.Deserialize<ChecksetSet>(File.ReadAllText(file), JsonOptions);
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

    public static void Save(ChecksetSet set) =>
        File.WriteAllText(PathFor(set.Name), JsonSerializer.Serialize(set, JsonOptions));

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Persists the last-used checkset (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(ChecksetSet snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastUsedPath)!);
            File.WriteAllText(LastUsedPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch { /* best effort */ }
    }

    /// <summary>The last-used checkset, or null when none saved yet (or the file is unreadable).</summary>
    public static ChecksetSet? TryLoadLastUsed()
    {
        try
        {
            if (!File.Exists(LastUsedPath)) return null;
            return JsonSerializer.Deserialize<ChecksetSet>(File.ReadAllText(LastUsedPath), JsonOptions);
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
