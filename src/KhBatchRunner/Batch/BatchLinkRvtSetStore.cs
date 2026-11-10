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
    private static readonly NamedJsonSetStore<BatchLinkRvtInputSet> Store = new(
        "BatchLinkSets", "batchlink-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<BatchLinkRvtInputSet> LoadAll() => Store.LoadAll();

    public static void Save(BatchLinkRvtInputSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(BatchLinkRvtInputSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used inputs, or null when none saved yet (or the file is unreadable).</summary>
    public static BatchLinkRvtInputSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
