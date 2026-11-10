using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhBatchRunner.Batch;

/// <summary>
/// Loads/saves "Export IFC + Data" option sets as JSON under
/// <c>%AppData%\KhBatchRunner\ExportIfcSets\*.json</c> (one file per named set), with a single
/// LAST-USED snapshot at <c>%AppData%\KhBatchRunner\exportifc-lastused.json</c> — the twin of
/// <see cref="ExportNwcSetStore"/>.
/// </summary>
public static class ExportIfcSetStore
{
    private static readonly NamedJsonSetStore<ExportIfcInputSet> Store = new(
        "ExportIfcSets", "exportifc-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<ExportIfcInputSet> LoadAll() => Store.LoadAll();

    public static void Save(ExportIfcInputSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(ExportIfcInputSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public static ExportIfcInputSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
