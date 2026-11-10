using System.Text.Json;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Forma;

/// <summary>
/// Loads/saves Forma folder sets as JSON under <c>%AppData%\KhBatchRunner\FolderSets\*.json</c>
/// (one file per set). Mirrors <see cref="Presets.PresetStore"/> for model presets — used by
/// Check Sheet Publish so a folder selection can be reused across runs.
/// </summary>
public static class FolderSetStore
{
    // No last-used snapshot for folder sets (and so no Exists/SaveLastUsed/TryLoadLastUsed).
    private static readonly NamedJsonSetStore<FolderSet> Store = new(
        "FolderSets", null,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string Directory_ => Store.SetsDirectory;

    public static List<FolderSet> LoadAll() => Store.LoadAll();

    public static void Save(FolderSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);
}
