using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KhBatchRunner.Settings;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Update;

/// <summary>
/// Build-validity gate + daily update check. Each build compiles in a fallback
/// <see cref="Product.BuiltInExpiryUtc"/> (90 days from the release it shipped with); a once-a-day
/// background fetch of a tiny remote JSON caches what the team currently allows, and that remote
/// answer is AUTHORITATIVE — it can shorten the built-in date as well as extend it. One edit to the
/// manifest therefore retires every installed copy within a day, with no reinstall anywhere.
///
/// Resolution order, first rule that applies wins (see <see cref="EnsureGateLoaded"/>):
/// <list type="number">
/// <item><c>minVersion</c> above <see cref="Product.Version"/> → blocked outright, no date involved.
///       The emergency "everything below X stops today" switch.</item>
/// <item><c>versionExpiry</c> naming this exact version → that date. This is what lets 1.3.8 be
///       retired at the end of the month while 1.3.9 runs on — a single global date cannot, because
///       every version reads the same field.</item>
/// <item><c>allowedUntil</c> → that date; the default for any version not named above.</item>
/// <item>otherwise <see cref="Product.BuiltInExpiryUtc"/>.</item>
/// </list>
/// Every step FAILS OPEN independently: a value that will not parse is skipped and the next rule
/// applies, so an unreachable host, a malformed manifest or a typo'd date degrades to the built-in
/// date rather than locking an office out.
///
/// The gate itself (<see cref="EnsureNotExpired"/> / <see cref="IsExpired"/>) never blocks on the
/// network — it decides from the local cache and, if that cache is over a day old, kicks a
/// fire-and-forget refresh whose result lands on a later call. That opportunistic kick is what keeps
/// the daily promise for the two cases the one-shot Idling handler (<see cref="ArmOnIdling"/>,
/// modelled on <see cref="Diagnostics.SelfTest.ArmOnIdling"/> /
/// <see cref="Schedule.ScheduledRunner.ArmOnIdling"/>) misses: a headless scheduled run, which never
/// arms it at all, and a Revit session left open for days.
///
/// The cache lives in <c>usersettings.json</c> in plain text (see
/// <see cref="UserSettings"/>): the threat model is casual office users, so no secret is involved
/// and tamper-resistance is deliberately not a goal.
/// </summary>
public static class UpdateCheck
{
    // The team-owned release manifest — anonymously reachable. Fetched at most once a day.
    private const string LatestJsonUrl =
        "https://raw.githubusercontent.com/lakeru135/KH-Tool/main/latest.json";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    // One static HttpClient for the process (the recommended pattern — a per-call client leaks
    // sockets). Configured once with a short timeout and a self-identifying User-Agent.
    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static UIControlledApplication? _app;
    private static EventHandler<IdlingEventArgs>? _handler;

    // Per-process cache of the resolved gate state so every button click is a field read rather
    // than a JSON parse (UserSettings reads the file each time). Invalidated the moment a refresh
    // lands, so a payload fetched THIS session takes effect this session — a daily leash that
    // waited for a Revit restart would not be a daily leash.
    private static bool _gateLoaded;
    private static DateTime _effectiveExpiryUtc;
    private static string? _blockedBelowVersion;

    // 0/1 guard so concurrent commands can't stack background fetches.
    private static int _refreshInFlight;

    // Once per process: drop a cached answer that belongs to a previous build.
    private static bool _cacheOwnershipChecked;

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("User-Agent", "KhBatch-UpdateCheck/" + Product.Version);
        return http;
    }

    /// <summary>
    /// Arm the update check. Subscribes a one-shot Idling handler from <c>OnStartup</c> (a legal,
    /// non-event context) that detaches itself on first fire, then notifies from the cache and
    /// kicks a background refresh. Only armed on interactive sessions — a headless self-test or
    /// scheduled run must never pop a dialog (see <see cref="App"/>).
    /// </summary>
    public static void ArmOnIdling(UIControlledApplication app)
    {
        _app = app;
        _handler = OnIdling;
        app.Idling += _handler; // legal: OnStartup is not itself an Idling event
    }

    /// <summary>Defensive unsubscribe for <c>OnShutdown</c> — a no-op if the one-shot already detached.</summary>
    public static void Detach(UIControlledApplication app)
    {
        try
        {
            if (_handler != null)
                app.Idling -= _handler; // removing a handler is allowed even mid-event
        }
        catch { /* best effort */ }
        _handler = null;
        _app = null;
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        // One-shot: detach before doing any work so a slow notify can't re-enter, and so a later
        // OnShutdown Detach is a no-op.
        if (_app != null) Detach(_app);
        try { NotifyThenRefresh(); }
        catch { /* an update check must never disrupt Revit */ }
    }

    /// <summary>
    /// Once per process, before anything reads the cache: a freshly installed build must not
    /// inherit the allowance its predecessor was granted. Cheap and best-effort; a failure here
    /// simply leaves the cache as it was.
    /// </summary>
    private static void EnsureCacheBelongsToThisBuild()
    {
        if (_cacheOwnershipChecked) return;
        _cacheOwnershipChecked = true;
        try { UserSettings.ClearUpdateCacheIfBuildChanged(Product.Version); }
        catch { /* best effort */ }
    }

    private static void NotifyThenRefresh()
    {
        EnsureCacheBelongsToThisBuild();
        DateTime nowUtc = DateTime.UtcNow;

        // (a) Notify from cache, on this (UI) thread — at most once per day.
        try
        {
            if (System.Version.TryParse(UserSettings.CachedLatestVersion, out System.Version? latest)
                && System.Version.TryParse(Product.Version, out System.Version? installed)
                && latest > installed)
            {
                DateTime? lastNotice = UserSettings.LastUpdateNoticeUtc;
                if (lastNotice is null || nowUtc - lastNotice.Value >= CheckInterval)
                {
                    ShowUpdateDialog(latest.ToString());
                    UserSettings.LastUpdateNoticeUtc = nowUtc;
                }
            }
        }
        catch { /* a notification must never disrupt startup */ }

        // (b) Refresh from the remote, fully in the background — at most once per day. Never blocks
        // Revit. The notice above deliberately used the cache as it stood on entry; this session's
        // gate picks the new payload up as soon as it lands (see RefreshAsync).
        MaybeKickRefresh();
    }

    /// <summary>
    /// Kick a background refresh if the cache is over <see cref="CheckInterval"/> old. Fire and
    /// forget by design — the caller keeps deciding on the cache it already has, and the fresh
    /// answer applies from the next gate call. Safe to call from any thread, as often as you like.
    /// </summary>
    private static void MaybeKickRefresh()
    {
        try
        {
            DateTime? lastCheck = UserSettings.LastUpdateCheckUtc;
            if (lastCheck != null && DateTime.UtcNow - lastCheck.Value < CheckInterval) return;

            // Only one fetch in flight, however many commands ask at once.
            if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0) return;
            _ = Task.Run(RefreshAsync);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// The "new update available" notice. Uses a structured <see cref="TaskDialog"/> — a bold
    /// main instruction, the installed-vs-available line plus any short download note as content,
    /// and the (often longer) remote "what's new" message tucked into the expandable details — so it
    /// reads far better than the old single run-on string passed to <c>TaskDialog.Show</c>.
    /// </summary>
    private static void ShowUpdateDialog(string latest)
    {
        string? note = UserSettings.CachedDownloadNote;
        string? whatsNew = UserSettings.CachedUpdateMessage;

        string content = $"You have {Product.Version} installed.";
        if (!string.IsNullOrWhiteSpace(note))
            content += "\n\n" + note!.Trim();

        var dlg = new TaskDialog(Product.Name)
        {
            MainInstruction = $"Version {latest} is available",
            MainContent = content,
            CommonButtons = TaskDialogCommonButtons.Close,
            DefaultButton = TaskDialogResult.Close
        };
        if (!string.IsNullOrWhiteSpace(whatsNew))
            dlg.ExpandedContent = "What's new:\n" + whatsNew!.Trim();
        dlg.Show();
    }

    private static async Task RefreshAsync()
    {
        // Background thread only — this never touches the Revit API. Every failure (offline,
        // timeout, malformed JSON) is swallowed; the check time is stamped regardless so an
        // unreachable host isn't hammered before the next daily window.
        try
        {
            string json = await Http.GetStringAsync(LatestJsonUrl).ConfigureAwait(false);
            LatestInfo? info = JsonSerializer.Deserialize<LatestInfo>(json, JsonOptions);
            if (info != null)
                UserSettings.SetUpdateCache(info.LatestVersion, info.AllowedUntil, info.Message,
                                            info.DownloadNote, info.MinVersion, info.VersionExpiry,
                                            Product.Version);
        }
        catch { /* all failures are non-fatal by design */ }
        finally
        {
            try { UserSettings.LastUpdateCheckUtc = DateTime.UtcNow; } catch { /* best effort */ }
            // Drop the resolved gate so the next check re-reads what just landed.
            _gateLoaded = false;
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

#if NO_EXPIRY_GATE

    /// <summary>
    /// Unrestricted build (<c>-p:Edition=FullUnlimited</c>): the gate is compiled out entirely, so
    /// no date and no manifest can stop this copy running. The daily fetch and the "a newer version
    /// is available" notice above are deliberately left ON — being told a new build exists is useful
    /// even when you are not forced onto it.
    /// </summary>
    public static bool EnsureNotExpired() => true;

    /// <summary>Always <c>false</c> in the unrestricted build — see <see cref="EnsureNotExpired"/>.</summary>
    public static bool IsExpired() => false;

#else

    /// <summary>
    /// Interactive gate for every ribbon command: returns <c>true</c> when the build may still run,
    /// otherwise shows a "please update" dialog and returns <c>false</c>. Decides from the local
    /// cache and never waits on the network.
    /// </summary>
    public static bool EnsureNotExpired()
    {
        if (!IsExpired()) return true;

        EnsureGateLoaded();
        string? note = UserSettings.CachedDownloadNote;
        string? extra = UserSettings.CachedUpdateMessage;

        // A version retired by minVersion has no expiry date to quote, and saying it "expired on"
        // some unrelated date would send the user looking for the wrong problem.
        bool retired = _blockedBelowVersion != null;
        string instruction = retired ? "This version is no longer supported" : "This build has expired";
        string content = retired
            ? $"{Product.Name} {Product.Version} is below the minimum supported version " +
              $"({_blockedBelowVersion}). Please install the latest version to keep using it."
            : $"{Product.Name} {Product.Version} expired on {Iso(_effectiveExpiryUtc)}. " +
              "Please install the latest version to keep using it.";
        if (!string.IsNullOrWhiteSpace(note))
            content += "\n\n" + note!.Trim();

        var dlg = new TaskDialog(Product.Name)
        {
            MainInstruction = instruction,
            MainContent = content,
            CommonButtons = TaskDialogCommonButtons.Close,
            DefaultButton = TaskDialogResult.Close
        };
        if (!string.IsNullOrWhiteSpace(extra))
            dlg.ExpandedContent = "What's new:\n" + extra!.Trim();
        dlg.Show();
        return false;
    }

    /// <summary>Same logic as <see cref="EnsureNotExpired"/> but silent — for headless (scheduled) runs.</summary>
    public static bool IsExpired()
    {
        // Opportunistic and non-blocking: THIS call still answers from the cache it already has.
        // Without it a schedule-only PC — which never arms the Idling handler — would never refresh
        // at all, and a Revit left open all week would check once.
        MaybeKickRefresh();

        EnsureGateLoaded();
        return _blockedBelowVersion != null || DateTime.UtcNow.Date > _effectiveExpiryUtc.Date;
    }

#endif

    /// <summary>
    /// Why the gate is shut, phrased for a headless log line. Only meaningful once
    /// <see cref="IsExpired"/> has returned <c>true</c>.
    /// </summary>
    internal static string BlockReason
    {
        get
        {
            EnsureGateLoaded();
            return _blockedBelowVersion != null
                ? $"version {Product.Version} is below the minimum supported version {_blockedBelowVersion}"
                : $"build expired {Iso(_effectiveExpiryUtc)}";
        }
    }

    /// <summary>
    /// Resolve the remote manifest against this build, once per process (until a refresh lands).
    /// See the class summary for the rule order. Each rule fails open on its own: anything that will
    /// not parse is skipped, and with nothing usable cached the built-in date stands.
    /// </summary>
    private static void EnsureGateLoaded()
    {
        if (_gateLoaded) return;

        // A headless run never reaches NotifyThenRefresh, so this is also the scheduled path's
        // only chance to disown a previous build's cache.
        EnsureCacheBelongsToThisBuild();

        DateTime effective = Product.BuiltInExpiryUtc;
        string? blockedBelow = null;
        try
        {
            // (1) minVersion — a hard floor that outranks every date.
            if (System.Version.TryParse(UserSettings.CachedMinVersion, out System.Version? required)
                && System.Version.TryParse(Product.Version, out System.Version? mine)
                && mine < required)
                blockedBelow = required.ToString();

            // (2) versionExpiry naming this exact version, else (3) the global allowedUntil.
            string? date = null;
            Dictionary<string, string>? perVersion = UserSettings.CachedVersionExpiry;
            if (perVersion != null
                && perVersion.TryGetValue(Product.Version, out string? mineOnly)
                && !string.IsNullOrWhiteSpace(mineOnly))
                date = mineOnly;
            if (string.IsNullOrWhiteSpace(date))
                date = UserSettings.CachedAllowedUntil;

            // (4) Authoritative when it parses: the remote answer replaces the built-in date in
            // BOTH directions. Extend-only was the reason a stale manifest could not retire anyone.
            if (DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out DateTime remote))
                effective = remote;
        }
        catch { /* fail open: the built-in date stands */ }

        _effectiveExpiryUtc = effective;
        _blockedBelowVersion = blockedBelow;
        _gateLoaded = true;
    }

    /// <summary>
    /// Format a gate date the way the manifest writes it. Interpolation would use the PC's culture,
    /// and on a Thai-locale machine <c>yyyy</c> renders in the Buddhist calendar — so the same build
    /// that we retired on 2026-08-08 would tell the user it "expired on 2569-08-08", a year that
    /// matches nothing they were sent. Parsing is already invariant everywhere; only display was not.
    /// </summary>
    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Shape of <c>latest.json</c> — extra/absent fields are tolerated (case-insensitive).</summary>
    private sealed class LatestInfo
    {
        public string? LatestVersion { get; set; }
        public string? DownloadNote { get; set; }
        public string? AllowedUntil { get; set; }
        public string? MinVersion { get; set; }
        public string? Message { get; set; }

        /// <summary>Version string → last allowed date, for retiring one release while sparing another.</summary>
        public Dictionary<string, string>? VersionExpiry { get; set; }
    }
}
