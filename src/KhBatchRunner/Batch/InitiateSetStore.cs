using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves Initiate-to-Forma input sets as JSON under
/// <c>%AppData%\KhBatchRunner\InitiateSets\*.json</c> (one file per named set) — mirrors
/// <see cref="Scripts.ScriptSetStore"/>. Additionally keeps a single LAST-USED snapshot at
/// <c>%AppData%\KhBatchRunner\initiate-lastused.json</c> (outside the sets folder so it never
/// shows up as a set), written on every successful Initiate commit and restored when the
/// dialog opens.
/// </summary>
public static class InitiateSetStore
{
    private static readonly NamedJsonSetStore<InitiateInputSet> Store = new(
        "InitiateSets", "initiate-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<InitiateInputSet> LoadAll() => Store.LoadAll();

    public static void Save(InitiateInputSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(InitiateInputSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public static InitiateInputSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
