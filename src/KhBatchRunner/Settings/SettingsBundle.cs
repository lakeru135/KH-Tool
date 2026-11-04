using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace KhBatchRunner.Settings;

/// <summary>What the importer should do when a bundle names something that already exists here.</summary>
public enum ImportCollision
{
    /// <summary>Leave what is on this PC alone.</summary>
    Skip,

    /// <summary>Replace it with the bundle's version.</summary>
    Overwrite,

    /// <summary>Keep both, giving the incoming one a free name via <see cref="UI.SetDuplication"/>.</summary>
    KeepBoth
}

/// <summary>What a bundle says about itself, read before anything is written.</summary>
public sealed class SettingsManifest
{
    /// <summary>Bundle format version. Bumped only for a change an older reader could misread.</summary>
    public int SchemaVersion { get; set; } = SettingsBundle.CurrentSchemaVersion;

    public string ProductName { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public string Edition { get; set; } = "";
    public string SourceMachine { get; set; } = "";
    public string SourceWindowsUser { get; set; } = "";

    /// <summary>The Autodesk account whose sets these are — the thing worth knowing before
    /// importing model sets onto a PC signed in as someone else.</summary>
    public string SourceAutodeskUser { get; set; } = "";

    public DateTime ExportedAt { get; set; } = DateTime.Now;

    /// <summary>Category key → number of files carried.</summary>
    public Dictionary<string, int> Counts { get; set; } = new();
}

/// <summary>One category's outcome, for the report shown after an import or export.</summary>
public sealed record TransferResult(string DisplayName, int Written, int Skipped, string? Error = null);

/// <summary>
/// Reads and writes a settings bundle — a plain <c>.zip</c> holding a manifest and one folder per
/// <see cref="SettingsCatalog"/> category.
///
/// <para><b>Why a zip written in-process.</b> The diagnostics collector has to shell out to
/// <c>tar</c> because it runs as PowerShell and <c>Compress-Archive</c> is a module, which
/// ConstrainedLanguage refuses on the locked-down office PCs. None of that applies here:
/// <see cref="ZipFile"/> is a framework call inside the add-in, so the group policy that breaks
/// PowerShell cannot reach it.</para>
///
/// <para><b>Schedules are not in a bundle</b> — see <see cref="SettingsCatalog"/> for why. Neither
/// is the licensing cache inside <c>usersettings.json</c>: the ONE field worth carrying from that
/// file is the last destination folder, and it is copied by name rather than by shipping the file,
/// because an inherited <c>CachedAllowedUntil</c> would let a PC run a build on an allowance
/// granted to a different one — precisely what
/// <see cref="UserSettings.ClearUpdateCacheIfBuildChanged"/> exists to prevent.</para>
/// </summary>
public static class SettingsBundle
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The file extension offered in the save/open dialogs.</summary>
    public const string FileFilter = "KH Batch settings bundle (*.zip)|*.zip";

    private const string ManifestEntry = "manifest.json";
    private const string PreferenceEntry = "preferences.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The one preference worth carrying out of usersettings.json.</summary>
    private sealed class PortablePreferences
    {
        public string? LastDestinationFolder { get; set; }
        public bool HideInaccessibleModelSets { get; set; }
    }

    // ---- export ---------------------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="categories"/> to a new zip at <paramref name="zipPath"/>, replacing
    /// any file already there. Returns one row per category for the report.
    /// </summary>
    public static List<TransferResult> Export(string zipPath, IEnumerable<SettingsCategory> categories,
        bool includePreferences)
    {
        var results = new List<TransferResult>();
        var manifest = new SettingsManifest
        {
            ProductName = Product.Name,
            ProductVersion = Product.Version,
            Edition = Product.ScheduleOwner,   // "Full" / "DRW" — the existing edition discriminator
            SourceMachine = Environment.MachineName,
            SourceWindowsUser = Environment.UserName,
            SourceAutodeskUser = UI.HostServices.SignedInUserId,
        };

        // Written to a temp file and moved into place, so an export interrupted half way cannot
        // leave a truncated zip sitting where the user expects a good one.
        string temp = Path.Combine(Path.GetTempPath(),
            "khbatch-settings-" + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            using (var zip = new ZipArchive(System.IO.File.Create(temp), ZipArchiveMode.Create))
            {
                foreach (SettingsCategory category in categories)
                {
                    try
                    {
                        int written = WriteCategory(zip, category);
                        manifest.Counts[category.Key] = written;
                        results.Add(new TransferResult(category.DisplayName, written, 0));
                    }
                    catch (Exception ex)
                    {
                        results.Add(new TransferResult(category.DisplayName, 0, 0, FirstLine(ex)));
                    }
                }

                if (includePreferences)
                    WriteEntry(zip, PreferenceEntry, JsonSerializer.Serialize(new PortablePreferences
                    {
                        LastDestinationFolder = UserSettings.LastDestinationFolder,
                        HideInaccessibleModelSets = UserSettings.HideInaccessibleModelSets,
                    }, JsonOptions));

                WriteEntry(zip, ManifestEntry, JsonSerializer.Serialize(manifest, JsonOptions));
            }

            if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
            System.IO.File.Move(temp, zipPath);
        }
        finally
        {
            try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch { /* best effort */ }
        }

        return results;
    }

    private static int WriteCategory(ZipArchive zip, SettingsCategory category)
    {
        if (category.Kind == SettingsCategoryKind.SingleFile)
        {
            if (!System.IO.File.Exists(category.FullPath)) return 0;
            zip.CreateEntryFromFile(category.FullPath, category.Key + "/" + category.PathName);
            return 1;
        }

        int count = 0;
        foreach (string file in Directory.EnumerateFiles(category.FullPath, "*.json"))
        {
            zip.CreateEntryFromFile(file, category.Key + "/" + Path.GetFileName(file));
            count++;
        }
        return count;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    // ---- read ------------------------------------------------------------------------------

    /// <summary>
    /// Reads a bundle's manifest without writing anything, so the dialog can show what is inside
    /// before the user commits. Returns null (with <paramref name="error"/> set) for a file that is
    /// not a bundle.
    /// </summary>
    public static SettingsManifest? ReadManifest(string zipPath, out string? error)
    {
        error = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            ZipArchiveEntry? entry = zip.GetEntry(ManifestEntry);
            if (entry is null)
            {
                error = "This zip has no manifest.json — it was not written by " + Product.Name + ".";
                return null;
            }
            using var reader = new StreamReader(entry.Open());
            SettingsManifest? manifest = JsonSerializer.Deserialize<SettingsManifest>(reader.ReadToEnd(), JsonOptions);
            if (manifest is null) { error = "The bundle's manifest could not be read."; return null; }
            if (manifest.SchemaVersion > CurrentSchemaVersion)
            {
                error = $"This bundle was written by a newer version (format {manifest.SchemaVersion}; " +
                        $"this build reads {CurrentSchemaVersion}). Update {Product.Name} first.";
                return null;
            }
            return manifest;
        }
        catch (Exception ex)
        {
            error = FirstLine(ex);
            return null;
        }
    }

    // ---- import ----------------------------------------------------------------------------

    /// <summary>
    /// Copies the chosen categories out of a bundle into this PC's settings folders, applying
    /// <paramref name="collision"/> to anything already there. Never throws for one bad category —
    /// its row carries the error and the rest still import.
    /// </summary>
    public static List<TransferResult> Import(string zipPath, IEnumerable<SettingsCategory> categories,
        ImportCollision collision, bool includePreferences)
    {
        var results = new List<TransferResult>();
        using var zip = ZipFile.OpenRead(zipPath);

        foreach (SettingsCategory category in categories)
        {
            int written = 0, skipped = 0;
            try
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (!IsEntryOf(entry, category)) continue;
                    if (ImportOne(entry, category, collision)) written++; else skipped++;
                }
                results.Add(new TransferResult(category.DisplayName, written, skipped));
            }
            catch (Exception ex)
            {
                results.Add(new TransferResult(category.DisplayName, written, skipped, FirstLine(ex)));
            }
        }

        if (includePreferences) ImportPreferences(zip, results);
        return results;
    }

    private static bool IsEntryOf(ZipArchiveEntry entry, SettingsCategory category) =>
        entry.FullName.StartsWith(category.Key + "/", StringComparison.OrdinalIgnoreCase) &&
        entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
        entry.Name.Length > 0;

    /// <summary>Writes one entry, honouring the collision choice. Returns whether it was written.</summary>
    private static bool ImportOne(ZipArchiveEntry entry, SettingsCategory category, ImportCollision collision)
    {
        // The entry name is used as a FILE NAME and comes from a file the user was handed, so it is
        // stripped to its leaf and re-sanitised. Without this, an entry called "..\..\something"
        // would be written outside the settings folder entirely.
        string safeName = SafeFileName(Path.GetFileName(entry.Name));
        if (safeName.Length == 0) return false;

        string targetDir = category.Kind == SettingsCategoryKind.SetFolder
            ? category.FullPath
            : SettingsPaths.Root;
        string target = category.Kind == SettingsCategoryKind.SetFolder
            ? Path.Combine(targetDir, safeName)
            : category.FullPath;

        if (System.IO.File.Exists(target))
        {
            switch (collision)
            {
                case ImportCollision.Skip:
                    return false;
                case ImportCollision.KeepBoth when category.Kind == SettingsCategoryKind.SetFolder:
                    // Same rule the nine Duplicate buttons use, so "(copy)" means one thing product-wide.
                    string stem = Path.GetFileNameWithoutExtension(safeName);
                    IEnumerable<string> taken = Directory.EnumerateFiles(targetDir, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => n != null)!;
                    target = Path.Combine(targetDir, SafeFileName(UI.SetDuplication.UniqueName(stem, taken!)) + ".json");
                    break;
                case ImportCollision.KeepBoth:
                    // A single last-used file has no name to vary, so there is no third option for
                    // it — keeping both would mean inventing a second "last used", which is not a
                    // thing. Treated as Skip rather than silently overwriting.
                    return false;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, overwrite: true);
        return true;
    }

    private static void ImportPreferences(ZipArchive zip, List<TransferResult> results)
    {
        ZipArchiveEntry? entry = zip.GetEntry(PreferenceEntry);
        if (entry is null) return;
        try
        {
            using var reader = new StreamReader(entry.Open());
            PortablePreferences? prefs =
                JsonSerializer.Deserialize<PortablePreferences>(reader.ReadToEnd(), JsonOptions);
            if (prefs is null) return;
            if (!string.IsNullOrWhiteSpace(prefs.LastDestinationFolder))
                UserSettings.LastDestinationFolder = prefs.LastDestinationFolder;
            UserSettings.HideInaccessibleModelSets = prefs.HideInaccessibleModelSets;
            results.Add(new TransferResult("Preferences", 1, 0));
        }
        catch (Exception ex)
        {
            results.Add(new TransferResult("Preferences", 0, 0, FirstLine(ex)));
        }
    }

    /// <summary>The same sanitising every store applies when it turns a set NAME into a file name.</summary>
    private static string SafeFileName(string name) =>
        string.Join("_", (name ?? "").Split(Path.GetInvalidFileNameChars())).Trim();

    private static string FirstLine(Exception ex) => (ex.Message ?? "").Split('\n')[0].Trim();
}
