using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Settings;

/// <summary>
/// Small persisted bag of cross-session UI preferences, stored at
/// <c>%AppData%\KhBatchRunner\usersettings.json</c>. Currently just the last destination folder
/// a run wrote to, so every run/schedule window can default its "Destination folder" box to it
/// instead of the Desktop. Best-effort throughout — a read/write failure never blocks a run.
/// </summary>
public static class UserSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string SettingsPath => SettingsPaths.File("usersettings.json");

    private sealed class Data
    {
        public string? LastDestinationFolder { get; set; }

        // Account awareness (1.5.0). The filter is a plain preference; LastSignedInUserId exists
        // only so a CHANGE of Autodesk account can be noticed once and reported, rather than being
        // discovered by a nightly run whose every model failed to open.
        public bool HideInaccessibleModelSets { get; set; }
        public string? LastSignedInUserId { get; set; }

        // Update-check bookkeeping (see KhBatchRunner.Update.UpdateCheck). Persisted in plain
        // text like everything else in this bag — the threat model is casual office users, so no
        // secret is involved and tamper-resistance is deliberately not a goal.
        public DateTime? LastUpdateCheckUtc { get; set; }
        public DateTime? LastUpdateNoticeUtc { get; set; }
        public string? CachedLatestVersion { get; set; }
        public string? CachedAllowedUntil { get; set; }
        public string? CachedUpdateMessage { get; set; }
        public string? CachedDownloadNote { get; set; }
        public string? CachedMinVersion { get; set; }

        // Which build fetched the cache below. A newly installed build must NOT inherit an
        // allowance granted to a different one — see ClearUpdateCacheIfBuildChanged.
        public string? CachedForVersion { get; set; }

        // The raw per-version retirement map from latest.json, cached verbatim rather than resolved
        // against Product.Version at fetch time. The case that matters is a PC upgrading on the
        // deadline day: a pre-resolved date would keep applying the OLD version's deadline to the
        // freshly installed build until the next fetch.
        public Dictionary<string, string>? CachedVersionExpiry { get; set; }
    }

    private static Data Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(SettingsPath), JsonOptions) ?? new Data();
        }
        catch { /* fall through to defaults */ }
        return new Data();
    }

    private static void Save(Data data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch { /* best effort */ }
    }

    /// <summary>The last destination folder a run used, or null if none saved / unreadable.</summary>
    public static string? LastDestinationFolder
    {
        get
        {
            string? folder = Load().LastDestinationFolder;
            return string.IsNullOrWhiteSpace(folder) ? null : folder;
        }
        set
        {
            Data data = Load();
            data.LastDestinationFolder = value;
            Save(data);
        }
    }

    /// <summary>
    /// Leave saved model sets this Autodesk account provably cannot open out of the pick lists.
    /// Defaults to FALSE, and deliberately so: a set that disappears looks deleted, and the check
    /// is inconclusive for every model-list set saved before 1.5.0. Only a proven
    /// <see cref="Presets.AccessVerdict.NoAccess"/> is ever hidden.
    /// </summary>
    public static bool HideInaccessibleModelSets
    {
        get => Load().HideInaccessibleModelSets;
        set
        {
            Data data = Load();
            data.HideInaccessibleModelSets = value;
            Save(data);
        }
    }

    /// <summary>
    /// The Autodesk account the last session ran under, or null before 1.5.0 / on a fresh profile.
    ///
    /// <para>Only ever compared, never acted on: it is how the product notices that the account
    /// changed while the schedules — which are Windows tasks keyed to the WINDOWS user — carried on
    /// firing unchanged.</para>
    /// </summary>
    public static string? LastSignedInUserId
    {
        get => Load().LastSignedInUserId;
        set
        {
            Data data = Load();
            data.LastSignedInUserId = value;
            Save(data);
        }
    }

    /// <summary>The folder a "Destination folder" box should default to: last-used, else the Desktop.</summary>
    public static string DefaultDestinationFolder =>
        LastDestinationFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    /// <summary>UTC time of the last remote update check (success or failure), or null if never checked.</summary>
    public static DateTime? LastUpdateCheckUtc
    {
        get => Load().LastUpdateCheckUtc;
        set
        {
            Data data = Load();
            data.LastUpdateCheckUtc = value;
            Save(data);
        }
    }

    /// <summary>UTC time the "update available" notice was last shown (throttled to once a week), or null.</summary>
    public static DateTime? LastUpdateNoticeUtc
    {
        get => Load().LastUpdateNoticeUtc;
        set
        {
            Data data = Load();
            data.LastUpdateNoticeUtc = value;
            Save(data);
        }
    }

    /// <summary>Latest version string cached from the last remote check, or null if none cached.</summary>
    public static string? CachedLatestVersion => Load().CachedLatestVersion;

    /// <summary>Remote <c>allowedUntil</c> date string from the last check (overrides the built-in expiry), or null.</summary>
    public static string? CachedAllowedUntil => Load().CachedAllowedUntil;

    /// <summary>Remote <c>minVersion</c> from the last check — builds below it are blocked outright, or null.</summary>
    public static string? CachedMinVersion => Load().CachedMinVersion;

    /// <summary>Remote <c>versionExpiry</c> map (version string → date) from the last check, or null.</summary>
    public static Dictionary<string, string>? CachedVersionExpiry => Load().CachedVersionExpiry;

    /// <summary>Free-text message cached from the last remote check, or null.</summary>
    public static string? CachedUpdateMessage => Load().CachedUpdateMessage;

    /// <summary>Download note cached from the last remote check (shown on the update/expiry dialogs), or null.</summary>
    public static string? CachedDownloadNote => Load().CachedDownloadNote;

    /// <summary>Store every cached remote-update field in one write (called from the background refresh).</summary>
    public static void SetUpdateCache(string? latestVersion, string? allowedUntil, string? message,
                                      string? downloadNote, string? minVersion,
                                      Dictionary<string, string>? versionExpiry, string forVersion)
    {
        Data data = Load();
        data.CachedLatestVersion = latestVersion;
        data.CachedAllowedUntil = allowedUntil;
        data.CachedUpdateMessage = message;
        data.CachedDownloadNote = downloadNote;
        data.CachedMinVersion = minVersion;
        data.CachedVersionExpiry = versionExpiry;
        data.CachedForVersion = forVersion;
        Save(data);
    }

    /// <summary>
    /// Discard a cached remote answer that a DIFFERENT build fetched, and forget when it was
    /// fetched so the new build re-checks at once instead of waiting out the daily throttle.
    /// Returns whether anything was cleared.
    ///
    /// Installing a new version must never inherit the previous one's allowance: an
    /// <c>allowedUntil</c> still in the future would silently override the incoming build's own
    /// expiry date, so a build that should have stopped on day one would run on someone else's
    /// permission. Doing this in the add-in rather than the installer is deliberate — setup runs
    /// elevated, possibly under a different account, so it cannot reliably reach each user's
    /// <c>%AppData%</c>; this also covers dev copies and hand-placed builds, which no installer sees.
    /// The user's own preferences (destination folder) are untouched.
    /// </summary>
    public static bool ClearUpdateCacheIfBuildChanged(string currentVersion)
    {
        Data data = Load();
        if (string.Equals(data.CachedForVersion, currentVersion, StringComparison.Ordinal)) return false;

        data.CachedForVersion = currentVersion;
        data.LastUpdateCheckUtc = null;
        data.LastUpdateNoticeUtc = null;
        data.CachedLatestVersion = null;
        data.CachedAllowedUntil = null;
        data.CachedUpdateMessage = null;
        data.CachedDownloadNote = null;
        data.CachedMinVersion = null;
        data.CachedVersionExpiry = null;
        Save(data);
        return true;
    }
}
