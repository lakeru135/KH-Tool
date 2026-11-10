using System.Text.Json;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Interop;

/// <summary>
/// Loads/saves named Model Checker checksets as JSON under
/// <c>%AppData%\KhBatchRunner\Checksets\*.json</c> (one file per named set) — mirrors
/// <see cref="Batch.InitiateSetStore"/> / <see cref="Forma.FolderSetStore"/>. Also keeps a single
/// last-used snapshot at <c>%AppData%\KhBatchRunner\checkset-lastused.json</c> (outside the sets
/// folder so it never shows up as a set), written on every Interop commit and restored when a
/// dialog opens. Best-effort throughout — a read/write failure never blocks a run.
/// </summary>
public static class ChecksetSetStore
{
    private static readonly NamedJsonSetStore<ChecksetSet> Store = new(
        "Checksets", "checkset-lastused.json",
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        },
        s => s.Name);

    public static string SetsDirectory => Store.SetsDirectory;

    public static List<ChecksetSet> LoadAll() => Store.LoadAll();

    public static void Save(ChecksetSet set) => Store.Save(set);

    public static void Delete(string name) => Store.Delete(name);

    public static bool Exists(string name) => Store.Exists(name);

    /// <summary>Persists the last-used checkset (best effort — a failed write never blocks a run).</summary>
    public static void SaveLastUsed(ChecksetSet snapshot) => Store.SaveLastUsed(snapshot);

    /// <summary>The last-used checkset, or null when none saved yet (or the file is unreadable).</summary>
    public static ChecksetSet? TryLoadLastUsed() => Store.TryLoadLastUsed();
}
