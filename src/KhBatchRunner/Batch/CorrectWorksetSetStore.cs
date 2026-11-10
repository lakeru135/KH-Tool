using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves named Correct Workset input sets as JSON under
/// <c>%AppData%\KhBatchRunner\CorrectWorksetSets\*.json</c> plus a last-used snapshot at
/// <c>%AppData%\KhBatchRunner\correctworkset-lastused.json</c> — mirrors
/// <see cref="RelinkSetStore"/>. Best-effort throughout: a read/write failure never blocks a run.
/// </summary>
public static class CorrectWorksetSetStore
{
    private static readonly NamedJsonSetStore<CorrectWorksetInputSet> Store = new(
        "CorrectWorksetSets", "correctworkset-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<CorrectWorksetInputSet> LoadAll() => Store.LoadAll();

    public static void Save(CorrectWorksetInputSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(CorrectWorksetInputSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used inputs, or null when none saved yet (or the file is unreadable).</summary>
    public static CorrectWorksetInputSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
