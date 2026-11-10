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
    private static readonly NamedJsonSetStore<RelinkInputSet> Store = new(
        "RelinkSets", "relink-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<RelinkInputSet> LoadAll() => Store.LoadAll();

    public static void Save(RelinkInputSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(RelinkInputSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used inputs, or null when none saved yet (or the file is unreadable).</summary>
    public static RelinkInputSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
