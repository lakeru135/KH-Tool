using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves Get Data Extract input sets as JSON under
/// <c>%AppData%\KhBatchRunner\DataExtractSets\*.json</c> (one file per named set) — the Data
/// Connector twin of <see cref="FileLogSetStore"/>, down to the single LAST-USED snapshot kept at
/// <c>%AppData%\KhBatchRunner\dataextract-lastused.json</c>, outside the sets folder so it never
/// shows up as a set.
/// </summary>
public static class DataExtractSetStore
{
    private static readonly NamedJsonSetStore<DataExtractInputSet> Store = new(
        "DataExtractSets", "dataextract-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<DataExtractInputSet> LoadAll() => Store.LoadAll();

    public static void Save(DataExtractInputSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(DataExtractInputSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public static DataExtractInputSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
