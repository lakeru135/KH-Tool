using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves Get File Log input sets as JSON under
/// <c>%AppData%\KhBatchRunner\FileLogSets\*.json</c> (one file per named set) — the file-log twin
/// of <see cref="ExportNwcSetStore"/>, down to the single LAST-USED snapshot kept at
/// <c>%AppData%\KhBatchRunner\filelog-lastused.json</c>, outside the sets folder so it never shows
/// up as a set.
/// </summary>
public static class FileLogSetStore
{
    private static readonly NamedJsonSetStore<FileLogInputSet> Store = new(
        "FileLogSets", "filelog-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<FileLogInputSet> LoadAll() => Store.LoadAll();

    public static void Save(FileLogInputSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(FileLogInputSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public static FileLogInputSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
