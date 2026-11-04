using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhBatchRunner.Settings;

namespace KhBatchRunner.Drw.Common;

/// <summary>A saved DRW Helper preset: anything with a name that round-trips through JSON.</summary>
public interface IDrwPreset
{
    string Name { get; set; }
}

/// <summary>A preset whose older files need translating to today's fields; the store calls
/// <see cref="Migrate"/> on every preset it reads, so no reader can see the old shape.</summary>
public interface IDrwMigrating
{
    void Migrate();
}

/// <summary>
/// One folder of named presets under <c>%AppData%\DrwHelper\</c>, plus a "last used" snapshot
/// beside it. The same shape as the batch runner's <c>*SetStore</c> classes, written once as a
/// generic because DRW has four preset kinds that differ only in their type and folder.
///
/// <para>The "last used" file sits in the DRW root rather than inside the folder so that listing
/// the folder only ever returns presets someone chose to save.</para>
/// </summary>
public sealed class DrwPresetStore<T> where T : class, IDrwPreset, new()
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _folder;

    public DrwPresetStore(string folder) => _folder = folder;

    private string Dir => SettingsPaths.DrwFolder(_folder);
    private string LastUsedPath => Path.Combine(SettingsPaths.DrwFolder(""), _folder + "-lastused.json");

    public List<T> LoadAll()
    {
        var presets = new List<T>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    T? p = Read(File.ReadAllText(file));
                    if (p != null && !string.IsNullOrWhiteSpace(p.Name)) presets.Add(p);
                }
                catch { /* skip a malformed file rather than losing the whole list */ }
            }
        }
        catch { /* an unreadable folder lists nothing */ }
        return presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public T? Load(string name) =>
        LoadAll().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Save(T preset) =>
        File.WriteAllText(PathFor(preset.Name), JsonSerializer.Serialize(preset, JsonOptions));

    public void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    public void SaveLastUsed(T preset)
    {
        try { File.WriteAllText(LastUsedPath, JsonSerializer.Serialize(preset, JsonOptions)); }
        catch { /* best effort */ }
    }

    public T? TryLoadLastUsed()
    {
        try
        {
            return File.Exists(LastUsedPath)
                ? Read(File.ReadAllText(LastUsedPath))
                : null;
        }
        catch { return null; }
    }

    /// <summary>A deep copy, so editing the dialog's working preset never edits a saved one.</summary>
    public static T Clone(T preset) =>
        Read(JsonSerializer.Serialize(preset, JsonOptions)) ?? new T();

    /// <summary>The one place a preset is read from JSON, so an old file is always migrated.</summary>
    private static T? Read(string json)
    {
        T? p = JsonSerializer.Deserialize<T>(json, JsonOptions);
        (p as IDrwMigrating)?.Migrate();
        return p;
    }

    private string PathFor(string name)
    {
        string safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Dir, safe + ".json");
    }
}
