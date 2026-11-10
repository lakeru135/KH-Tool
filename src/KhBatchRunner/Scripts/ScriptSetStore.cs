using System.Text.Json;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Scripts;

/// <summary>
/// Loads/saves DYN script sets as JSON under
/// <c>%AppData%\KhBatchRunner\ScriptSets\*.json</c> (one file per set).
/// Mirrors <see cref="Presets.PresetStore"/>.
/// </summary>
public static class ScriptSetStore
{
    private static readonly NamedJsonSetStore<ScriptSet> Store = new(
        "ScriptSets", null,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name,
        BackFillId);

    public static string ScriptSetsDirectory => Store.SetsDirectory;

    public static List<ScriptSet> LoadAll() => Store.LoadAll();

    public static void Save(ScriptSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    /// <summary>
    /// Back-fill a stable id for sets saved before ids existed; returning true makes the store
    /// persist it once so it stays constant across loads (schedules reference the id, not the name).
    /// A failed rewrite (read-only dir etc.) is ignored — the id still stays stable within this session.
    /// </summary>
    private static bool BackFillId(ScriptSet set)
    {
        if (!string.IsNullOrWhiteSpace(set.Id)) return false;
        set.Id = Guid.NewGuid().ToString("N");
        return true;
    }
}
