using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// The one implementation behind every "named JSON set" store: one file per named set under
/// <c>%AppData%\KhBatchRunner\&lt;folder&gt;\*.json</c>, plus (optionally) a single last-used
/// snapshot at <c>%AppData%\KhBatchRunner\&lt;lastUsedFileName&gt;</c> — outside the sets folder so
/// it never shows up as a set.
///
/// <para>The public stores (<see cref="InitiateSetStore"/>, <see cref="CutModelSetStore"/>, …) stay
/// as thin static facades over an instance of this, each keeping its own public static
/// <c>…Directory</c> property: the ThemePreview <c>catalog</c> gate discovers settings folders by
/// reflecting over exactly those properties on <c>*Store</c> types.</para>
///
/// <para>Behavior is that of the stores it replaced: the sets folder is created on every directory
/// access; <see cref="LoadAll"/> skips null, nameless and malformed files and sorts by name
/// (ordinal, ignoring case); <see cref="Save"/>/<see cref="Delete"/> let IO errors propagate; the
/// last-used snapshot is best effort. JSON options are per store (Export IFC adds an enum
/// converter).</para>
/// </summary>
internal sealed class NamedJsonSetStore<T> where T : class
{
    private readonly string _folderName;
    private readonly string? _lastUsedFileName;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Func<T, string> _nameOf;
    private readonly Func<T, bool>? _backFillOnLoad;

    /// <param name="folderName">Sub-folder of the settings root holding one JSON file per set.</param>
    /// <param name="lastUsedFileName">File directly in the settings root for the last-used snapshot,
    /// or null for a store without one.</param>
    /// <param name="jsonOptions">Serializer options, used for every read and write.</param>
    /// <param name="nameOf">The set's name: the file name (sanitised) and the sort key.</param>
    /// <param name="backFillOnLoad">Optional fix-up run on each loaded, named set; returning true
    /// rewrites that set's file once (best effort).</param>
    public NamedJsonSetStore(string folderName, string? lastUsedFileName, JsonSerializerOptions jsonOptions,
                             Func<T, string> nameOf, Func<T, bool>? backFillOnLoad = null)
    {
        _folderName = folderName;
        _lastUsedFileName = lastUsedFileName;
        _jsonOptions = jsonOptions;
        _nameOf = nameOf;
        _backFillOnLoad = backFillOnLoad;
    }

    public string SetsDirectory
    {
        get
        {
            string dir = Settings.SettingsPaths.Folder(_folderName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private string LastUsedPath => Settings.SettingsPaths.File(_lastUsedFileName!);

    public List<T> LoadAll()
    {
        var sets = new List<T>();
        foreach (string file in Directory.EnumerateFiles(SetsDirectory, "*.json"))
        {
            try
            {
                T? set = JsonSerializer.Deserialize<T>(File.ReadAllText(file), _jsonOptions);
                if (set != null && !string.IsNullOrWhiteSpace(_nameOf(set)))
                {
                    if (_backFillOnLoad != null && _backFillOnLoad(set))
                    {
                        try { File.WriteAllText(file, JsonSerializer.Serialize(set, _jsonOptions)); }
                        catch { /* read-only dir etc. — the fix-up still holds within this session */ }
                    }
                    sets.Add(set);
                }
            }
            catch
            {
                // Skip malformed files rather than failing the whole command.
            }
        }
        return sets.OrderBy(s => _nameOf(s), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Save(T set) =>
        File.WriteAllText(PathFor(_nameOf(set)), JsonSerializer.Serialize(set, _jsonOptions));

    public void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Persists the given snapshot as the last-used inputs (best effort — a failed write never blocks a run).</summary>
    public void SaveLastUsed(T snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastUsedPath)!);
            File.WriteAllText(LastUsedPath, JsonSerializer.Serialize(snapshot, _jsonOptions));
        }
        catch { /* best effort */ }
    }

    /// <summary>The last-used inputs, or null when none were saved yet (or the file is unreadable).</summary>
    public T? TryLoadLastUsed()
    {
        try
        {
            if (!File.Exists(LastUsedPath)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(LastUsedPath), _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private string PathFor(string name)
    {
        string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(SetsDirectory, safeName + ".json");
    }
}
