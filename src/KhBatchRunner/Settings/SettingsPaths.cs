using System.IO;

namespace KhBatchRunner.Settings;

/// <summary>
/// The one computation of where this product keeps its settings.
///
/// <para><b>Why it exists.</b> Thirteen classes each wrote
/// <c>Path.Combine(GetFolderPath(ApplicationData), "KhBatchRunner", …)</c> inline. That was
/// harmless while every one of them only had to find its own folder — and stopped being harmless
/// the moment something had to enumerate them all, because the enumeration would then be a
/// fourteenth hand-written list, free to drift from the thirteen it describes. Import/export walks
/// <see cref="SettingsCatalog"/>, and the catalog is built from this.</para>
///
/// <para>The roaming/local split is meaningful and is preserved: <see cref="Root"/> holds what the
/// user authored and would want on another PC, <see cref="LocalRoot"/> holds machine-local runtime
/// traces (logs, ledgers, caches) that mean nothing anywhere else.</para>
/// </summary>
public static class SettingsPaths
{
    /// <summary>The folder name under both roots. One literal, not thirteen.</summary>
    public const string ProductFolder = "KhBatchRunner";

    /// <summary>
    /// Overrides <see cref="Root"/> when set. Exists so the import/export round trip can be proven
    /// against real files without writing into the tester's own settings — the same
    /// environment-variable-as-test-seam the product already uses for
    /// <c>KHBATCH_FORCE_THEME</c>, <c>KHBATCH_MAX_INSTANCES</c> and <c>KHBATCH_DEV_ACTIONS</c>.
    ///
    /// <para>Read on every access rather than cached, so a harness can point several runs at
    /// different roots in one process. Unset in every normal run, which is the only reason it is
    /// safe: a stray value would silently move where every saved set is looked for.</para>
    /// </summary>
    public const string RootOverrideVariable = "KHBATCH_SETTINGS_ROOT";

    /// <summary><c>%AppData%\KhBatchRunner</c> — user-authored settings. Created on demand.</summary>
    public static string Root
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);
            return Ensure(!string.IsNullOrWhiteSpace(overridden)
                ? overridden!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               ProductFolder));
        }
    }

    /// <summary><c>%LocalAppData%\KhBatchRunner</c> — machine-local runtime state, never exported.</summary>
    public static string LocalRoot => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder));

    /// <summary>A named sub-folder of <see cref="Root"/> (e.g. <c>"Presets"</c>), created on demand.</summary>
    public static string Folder(string name) => Ensure(Path.Combine(Root, name));

    /// <summary>A file sitting directly in <see cref="Root"/> (e.g. a <c>*-lastused.json</c>).</summary>
    public static string File(string fileName) => Path.Combine(Root, fileName);

    /// <summary>The folder name DRW Helper keeps its own presets under.</summary>
    public const string DrwProductFolder = "DrwHelper";

    /// <summary>
    /// <c>%AppData%\DrwHelper\&lt;name&gt;</c>: DRW Helper's own presets (enlarge, grid dimension,
    /// stair annotation), created on demand. Kept out of <see cref="Root"/> on purpose: DRW shares
    /// the batch runner's MODEL SETS (those stay under <see cref="Root"/>), but its drawing
    /// presets are its own and must not appear in, or be carried by, the batch runner's settings
    /// transfer.
    /// </summary>
    public static string DrwFolder(string name) => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DrwProductFolder, name));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
