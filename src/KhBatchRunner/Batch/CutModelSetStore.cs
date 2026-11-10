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
    private static readonly NamedJsonSetStore<CutModelInputSet> Store = new(
        "CutModelSets", "cutmodel-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<CutModelInputSet> LoadAll() => Store.LoadAll();

    public static void Save(CutModelInputSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(CutModelInputSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public static CutModelInputSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
