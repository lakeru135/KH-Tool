using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves Export-NWC option sets as JSON under
/// <c>%AppData%\KhBatchRunner\ExportNwcSets\*.json</c> (one file per named set) — the Export-NWC
/// twin of <see cref="CutModelSetStore"/>, down to the single LAST-USED snapshot kept at
/// <c>%AppData%\KhBatchRunner\exportnwc-lastused.json</c>, outside the sets folder so it never
/// shows up as a set.
/// </summary>
public static class ExportNwcSetStore
{
    private static readonly NamedJsonSetStore<ExportNwcInputSet> Store = new(
        "ExportNwcSets", "exportnwc-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<ExportNwcInputSet> LoadAll() => Store.LoadAll();

    public static void Save(ExportNwcInputSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(ExportNwcInputSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public static ExportNwcInputSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
