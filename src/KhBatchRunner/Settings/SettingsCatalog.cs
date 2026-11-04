using System.IO;

namespace KhBatchRunner.Settings;

/// <summary>What kind of thing a settings category is on disk.</summary>
public enum SettingsCategoryKind
{
    /// <summary>A folder of one-JSON-file-per-named-set under <see cref="SettingsPaths.Root"/>.</summary>
    SetFolder,

    /// <summary>A single JSON file sitting directly in <see cref="SettingsPaths.Root"/>.</summary>
    SingleFile
}

/// <summary>One exportable group of settings.</summary>
/// <param name="Key">Stable identifier used inside a bundle. NEVER renamed — it is what an older
/// bundle is matched against on import.</param>
/// <param name="DisplayName">What the transfer dialog calls it, in the words the ribbon uses.</param>
/// <param name="PathName">The folder name, or the file name, under <see cref="SettingsPaths.Root"/>.</param>
/// <param name="Kind">Folder-of-sets or single file.</param>
public sealed record SettingsCategory(
    string Key,
    string DisplayName,
    string PathName,
    SettingsCategoryKind Kind)
{
    /// <summary>Absolute path of the folder or file this category names.</summary>
    public string FullPath => Kind == SettingsCategoryKind.SetFolder
        ? SettingsPaths.Folder(PathName)
        : SettingsPaths.File(PathName);

    /// <summary>How many saved items it currently holds — set files, or 1/0 for a single file.</summary>
    public int Count()
    {
        try
        {
            return Kind == SettingsCategoryKind.SetFolder
                ? Directory.EnumerateFiles(FullPath, "*.json").Count()
                : System.IO.File.Exists(FullPath) ? 1 : 0;
        }
        catch { return 0; }
    }
}

/// <summary>
/// Every group of settings this product stores, as ONE list.
///
/// <para><b>This list is the feature.</b> Export and import walk it and contain no literal folder
/// names of their own, so adding a twelfth saved-set store means adding one row here rather than
/// remembering to touch two other files. The same reasoning as
/// <c>ThemePreview.Windows()</c> and <see cref="UI.SetDuplication"/> — and the reason
/// <see cref="Diagnostics.SelfTest"/> asserts that every <c>*Store</c> directory in the assembly
/// resolves to a row below, so the list cannot quietly fall behind the stores it claims to
/// describe.</para>
///
/// <para><b>What is deliberately absent.</b> Schedules (<c>Scheduled\</c>) are excluded from
/// export and import: half of a schedule lives in Windows Task Scheduler, not in its JSON, so a
/// bundle could carry the file and still not carry the schedule — and a restored backup that
/// silently re-armed an unattended 2am run on a PC nobody expected it on is a worse outcome than
/// retyping it. Runtime state (<c>chunks\</c>, <c>runs\</c>, <c>logs\</c>, the first-open stamp,
/// and everything under <see cref="SettingsPaths.LocalRoot"/>) is not settings. And
/// <c>usersettings.json</c> is absent as a FILE because it mixes one real preference with the
/// licensing cache — see <see cref="SettingsBundle"/>, which copies the preference across by
/// itself.</para>
/// </summary>
public static class SettingsCatalog
{
    /// <summary>
    /// Folders written by a <c>*Store</c> that are NOT settings, listed so the catalog gate can
    /// tell "deliberately excluded" from "forgotten". Naming them is the point: an empty exclusion
    /// list would make the gate's silence meaningless.
    ///
    /// <list type="bullet">
    /// <item><c>Scheduled</c> — schedules. Half of one lives in Windows Task Scheduler, so a bundle
    /// could carry the JSON and still not carry the schedule; see the class remarks.</item>
    /// <item><c>chunks</c> — <c>ChunkJobStore</c>'s ephemeral per-group jobs, written and swept
    /// within a single run.</item>
    /// <item><c>runs</c> — <c>RunManifestStore</c>'s recovery manifests, meaningful only to the PC
    /// and the run that wrote them.</item>
    /// </list>
    /// </summary>
    public static readonly string[] NonSettingsFolders = { "Scheduled", "chunks", "runs" };

    private static readonly SettingsCategory[] Categories =
    {
        new("presets",         "Model sets",              "Presets",            SettingsCategoryKind.SetFolder),
        new("scriptsets",      "Dynamo script sets",      "ScriptSets",         SettingsCategoryKind.SetFolder),
        new("foldersets",      "Forma folder sets",         "FolderSets",         SettingsCategoryKind.SetFolder),
        new("cutmodel",        "Cut model input sets",    "CutModelSets",       SettingsCategoryKind.SetFolder),
        new("initiate",        "Initiate to Forma sets",    "InitiateSets",       SettingsCategoryKind.SetFolder),
        new("relink",          "Internal Relink sets",    "RelinkSets",         SettingsCategoryKind.SetFolder),
        new("correctworkset",  "Correct Workset rules",   "CorrectWorksetSets", SettingsCategoryKind.SetFolder),
        new("batchlink",       "Batch Link RVT sets",     "BatchLinkSets",      SettingsCategoryKind.SetFolder),
        new("exportnwc",       "Export NWC option sets",  "ExportNwcSets",      SettingsCategoryKind.SetFolder),
        new("filelog",         "Forma file-log sets",       "FileLogSets",        SettingsCategoryKind.SetFolder),
        new("dataextract",     "Forma data-extract sets",   "DataExtractSets",    SettingsCategoryKind.SetFolder),
        new("checksets",       "Model Checker checksets", "Checksets",          SettingsCategoryKind.SetFolder),

        // The nine last-used snapshots. They sit at the ROOT of %AppData%\KhBatchRunner rather
        // than inside their sets folder — deliberately, so they never appear as a set — which means
        // a bundle built by globbing "*Sets\*.json" would leave every one of them behind and a
        // restored PC would open each dialog blank.
        new("cutmodel-last",       "Cut model — last used",       "cutmodel-lastused.json",       SettingsCategoryKind.SingleFile),
        new("initiate-last",       "Initiate — last used",        "initiate-lastused.json",       SettingsCategoryKind.SingleFile),
        new("relink-last",         "Internal Relink — last used", "relink-lastused.json",         SettingsCategoryKind.SingleFile),
        new("correctworkset-last", "Correct Workset — last used", "correctworkset-lastused.json", SettingsCategoryKind.SingleFile),
        new("batchlink-last",      "Batch Link RVT — last used",  "batchlink-lastused.json",      SettingsCategoryKind.SingleFile),
        new("exportnwc-last",      "Export NWC — last used",      "exportnwc-lastused.json",      SettingsCategoryKind.SingleFile),
        new("filelog-last",        "Forma file log — last used",    "filelog-lastused.json",        SettingsCategoryKind.SingleFile),
        new("dataextract-last",    "Forma data extract — last used", "dataextract-lastused.json",   SettingsCategoryKind.SingleFile),
        new("checkset-last",       "Checkset — last used",        "checkset-lastused.json",       SettingsCategoryKind.SingleFile),
    };

    /// <summary>Every category, in the order the transfer dialog should list them.</summary>
    public static IReadOnlyList<SettingsCategory> All() => Categories;

    /// <summary>The categories this edition's ribbon actually surfaces tools for.</summary>
    public static IReadOnlyList<SettingsCategory> ForThisEdition() =>
        Categories;

    /// <summary>The category with this key, or null — used when reading a bundle written elsewhere.</summary>
    public static SettingsCategory? ByKey(string key) =>
        Categories.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="pathName"/> is a folder or file the catalog covers.</summary>
    public static bool Covers(string pathName) =>
        Categories.Any(c => string.Equals(c.PathName, pathName, StringComparison.OrdinalIgnoreCase));
}
