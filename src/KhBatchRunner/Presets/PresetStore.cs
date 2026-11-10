using System.Text.Json;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Presets;

/// <summary>
/// Loads/saves model presets as JSON files under
/// <c>%AppData%\KhBatchRunner\Presets\*.json</c> (one file per preset).
/// The Forma crawler (Phase 1-2) will write these; for now they can be hand-authored.
/// </summary>
public static class PresetStore
{
    private static readonly NamedJsonSetStore<ModelPreset> Store = new(
        "Presets", null,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        p => p.Name,
        BackFillId);

    public static string PresetsDirectory => Store.SetsDirectory;

    public static List<ModelPreset> LoadAll() => Store.LoadAll();

    public static void Save(ModelPreset preset) => Store.Save(preset);

    public static void Delete(string name) => Store.Delete(name);

    /// <summary>
    /// Back-fill a stable id for sets saved before ids existed; returning true makes the store
    /// persist it once so it stays constant across loads (schedules reference the id, not the name).
    /// A failed rewrite (read-only dir etc.) is ignored — the id still stays stable within this session.
    /// </summary>
    private static bool BackFillId(ModelPreset preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.Id)) return false;
        preset.Id = Guid.NewGuid().ToString("N");
        return true;
    }
}
