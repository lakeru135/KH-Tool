using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Windows.Forms;

namespace KhBatchRunner.UI;

/// <summary>
/// Sends batch status to the desktop, by whichever of two mechanisms this PC will accept.
///
/// PREFERRED — a real Windows reminder toast retained in Notification Center, raised through the
/// inbox Windows notification API from a hidden Windows PowerShell process (one implementation that
/// serves both the .NET Framework 4.8 and .NET 8 Revit builds without shipping another runtime).
/// This is the only path that can be retained, expired on a schedule, or carry a clickable link to
/// the run log, so it is always tried first.
///
/// FALLBACK — a tray balloon, which Windows 10/11 renders as a real toast, hosted by
/// <c>KhBatchLauncher.exe --notify</c>. It needs nothing the preferred path needs: no registered
/// AppUserModelID, no Start-menu shortcut, no scripting host. RESTORED IN 1.3.4.6 after being
/// deliberately removed in 1.3.4.3, because "deliberately no fallback" turned out to mean "silence"
/// on a managed PC: the preferred path depends on Windows PowerShell being able to load WinRT
/// types, and a PC under AppLocker/SRP puts PowerShell in ConstrainedLanguage mode where it cannot.
/// That is the same policy that refused to run the scheduled launcher from %ProgramData% (see
/// TaskSchedulerGateway) — one locked-down PC, two features silently dead.
///
/// WHY THE FALLBACK IS HOSTED OUT OF PROCESS. Raised inside Revit it was worth very little: the
/// tray icon owns the message, so it died with Revit (a run that closes Revit when it finishes
/// destroyed its own report), and with no lasting message loop it could not be clicked. In the
/// launcher it outlives the run, stays until acknowledged or expired, and opens the log on a click.
/// A third tier — the same balloon in-process — remains only so a checkout with no installed
/// launcher is not silent.
///
/// RETENTION IS THE POINT OF THE <c>failed</c> FLAG. Progress news — an attempt starting, a run
/// finishing cleanly — is worth seeing and not worth keeping, so it carries a one-hour
/// <c>ExpirationTime</c> and Windows removes it on the hour whether or not anyone looked. Anything
/// that failed carries no expiration and stays until it is dismissed by hand, because a run that
/// went wrong overnight must still be there in the morning. Both are removable by the user at any
/// time via the system Dismiss action; the app itself never deletes a history item. The hosted
/// fallback honours the same split with the only currency it has — how long its tray icon stays.
/// </summary>
public static class RunNotification
{
    // Installed shortcuts carry these explicit IDs (see the Inno Setup scripts). A fixed, valid
    // identity is important: Windows may display a banner for an ad-hoc desktop identity but then
    // omit it from Notification Center history. Existing Revit/controller and PowerShell Start-menu
    // identities remain runtime fallbacks for dev deploys and older installers.
    internal const string FullToastAppId = "Kanghun.KhBatchRunner";
    internal const string DrwToastAppId = "Kanghun.DrwHelper";
    internal const string ControllerToastAppId = "Kanghun.KhBatchController";
    internal const string RetainedScenario = "reminder";

    private const string PowerShellToastAppName = "Windows PowerShell";
    private const string ToastGroup = "KhBatch";

    // Budget for ONE notification, split so that a failing preferred path still leaves room for the
    // fallback inside the same window. Before 1.3.4.6 the toast attempt owned nearly all of it,
    // which is fine when it succeeds and useless when it does not.
    internal const int DeliveryTimeoutMs = 15_000;   // what a waiting caller will sit for
    internal const int ToastTimeoutMs = 9_000;       // the PowerShell helper's share
    internal const int BalloonRegisterMs = 500;      // let the shell register the tray icon
    internal const int BalloonHandoffMs = 2_500;     // and let it take the balloon

    /// <summary>How long a non-failure toast stays in Notification Center before Windows removes
    /// it. Overridable with KHBATCH_NOTIFY_RETAIN_MINUTES — the same field-tunable style as
    /// KHBATCH_MAX_INSTANCES, and what makes this behaviour testable in minutes rather than
    /// hours. A non-positive value means "keep it", matching the pre-1.3.4.4 behaviour.</summary>
    internal const int DefaultRetainMinutes = 60;

    internal static int RetainMinutes()
    {
        string? raw = Environment.GetEnvironmentVariable("KHBATCH_NOTIFY_RETAIN_MINUTES");
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw!.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int minutes))
            return minutes;
        return DefaultRetainMinutes;
    }

    /// <summary>The moment Windows should drop this toast from history, or null to keep it until
    /// the user dismisses it. Failures are always kept.</summary>
    internal static DateTimeOffset? ExpiryFor(bool failed, DateTimeOffset now)
    {
        if (failed) return null;
        int minutes = RetainMinutes();
        return minutes > 0 ? now.AddMinutes(minutes) : (DateTimeOffset?)null;
    }

    /// <summary>Queues a retained Notification Center toast without blocking the caller.</summary>
    public static void Show(string title, string message, bool failed = false, string logPath = "") =>
        StartDeliveryThread(title, message, failed, logPath, waitForShell: false);

    /// <summary>Sends a retained toast and waits for Windows to accept it before Revit may exit.</summary>
    public static void ShowAndWait(string title, string message, bool failed = false, string logPath = "") =>
        StartDeliveryThread(title, message, failed, logPath, waitForShell: true);

    private static void StartDeliveryThread(
        string title, string message, bool failed, string logPath, bool waitForShell)
    {
        try
        {
            var thread = new System.Threading.Thread(() => Deliver(title, message, failed, logPath))
            {
                IsBackground = true,
                Name = "KhBatchRunner retained notification"
            };
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();

            if (waitForShell && !thread.Join(DeliveryTimeoutMs))
                Diagnostics.RunTrace.Mark("retained notification handoff timed out: " + title);
            else if (!waitForShell)
                Diagnostics.RunTrace.Mark("retained notification queued: " + title);
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("retained notification failed to start: " + ex.Message);
        }
    }

    private static void Deliver(string title, string message, bool failed, string logPath)
    {
        DateTimeOffset? expiry = ExpiryFor(failed, DateTimeOffset.Now);

        if (!ForceBalloon() && TryDeliverRetainedToast(title, message, expiry, logPath))
        {
            string retention = expiry.HasValue
                ? "expires " + expiry.Value.ToString("HH:mm")
                : "kept until dismissed";
            Diagnostics.RunTrace.Mark(
                $"notification delivered via retained toast ({retention}): {title} - {message}");
            return;
        }

        // The same real toast, asked for WITHOUT PowerShell. This is the path that works on a PC
        // whose policy put PowerShell in ConstrainedLanguage — the machine where the one above can
        // never succeed and where, until now, every message fell through to the drawn card. It is
        // second rather than first because the PowerShell path can also set an expiry and this one
        // cannot (see ToastWinRt), so an ordinary PC keeps the retention behaviour it already had.
        if (!ForceBalloon() && TryDeliverLauncherToast(title, message, expiry, logPath))
        {
            Diagnostics.RunTrace.Mark(
                $"notification delivered via direct toast, no PowerShell (kept until dismissed): " +
                $"{title} - {message}");
            return;
        }

        // Preferred path refused. Which mechanism actually carried a message is the first thing
        // asked when someone reports seeing nothing, so it is stated plainly rather than inferred
        // from the absence of an error line.
        if (TryDeliverHostedBalloon(title, message, failed, logPath))
        {
            Diagnostics.RunTrace.Mark(
                $"notification delivered via tray balloon ({RetainMinutes()} min, clickable): " +
                $"{title} - {message}");
            return;
        }

        if (TryDeliverTrayBalloon(title, message, failed))
        {
            Diagnostics.RunTrace.Mark(
                $"notification delivered via in-process tray balloon (brief, not clickable): " +
                $"{title} - {message}");
            return;
        }

        Diagnostics.RunTrace.Mark($"notification NOT delivered by any mechanism: {title} - {message}");
    }

    /// <summary>
    /// Clears Notification Center of toasts whose retention window has passed, by running
    /// <c>KhBatchLauncher.exe --toast-sweep</c>.
    ///
    /// WHY REVIT'S STARTUP CALLS THIS. Every raised toast already sweeps before adding itself, which
    /// covers a working day: notification N tidies away notification N-2. What it cannot cover is the
    /// LAST one — a scheduled run that finishes at 2am and raises nothing afterwards leaves its
    /// "finished" toast with nothing to trigger the tidy-up. Startup is the natural second trigger:
    /// it is the moment the machine is being used again, and anything left from a previous session is
    /// by definition older than this one.
    ///
    /// Only the direct-WinRT path's toasts are ever recorded, so on a PC where the PowerShell path
    /// works this finds an empty ledger and does nothing. Fire-and-forget and fully fail-soft: the
    /// caller is Revit's startup, where a notification housekeeping task must never block or throw.
    /// </summary>
    /// <summary>
    /// Launcher copies whose denial has already been recorded in THIS process.
    ///
    /// <para>On a policy-managed PC the refusal is a standing condition, not an event: the same copy
    /// is denied on every call, so three call sites — and the toast loop's two app-id attempts —
    /// turned one true fact into a paragraph per notification. In
    /// <c>BKKLH3C7QV3_20260815_2347</c> that text was roughly a third of <c>surface.log</c>, which
    /// is a real cost: the log is how a run is diagnosed, and volume hides the lines that matter.
    /// Said once per process it is still evidence — it is what the diagnostic report's
    /// "NOTIFICATIONS: POWERSHELL LOCKED DOWN" verdict cites — without being noise.</para>
    /// </summary>
    private static readonly HashSet<string> ReportedLauncherDenials =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Same rule for the PowerShell notification helper, whose ConstrainedLanguage failure
    /// is likewise a standing condition rather than an event.</summary>
    private static bool _retainedHelperFailureReported;

    /// <summary>Records a copy this PC refused to start, at most once per process per path.
    /// Internal because <see cref="LauncherRunner"/> reports on every caller's behalf.</summary>
    internal static void MarkLauncherDenied(string what, string launcher, string detail)
    {
        lock (ReportedLauncherDenials)
            if (!ReportedLauncherDenials.Add(launcher)) return;

        Diagnostics.RunTrace.Mark($"{what} skipped {launcher}: {detail}");
    }

    public static void SweepExpiredToasts()
    {
        // NOTHING RECORDED MEANS NOTHING TO SWEEP — and, crucially, nothing to START.
        //
        // Only the direct-WinRT path ever records a toast, so on a PC where the PowerShell path
        // delivers (the ordinary case) this ledger is empty for the life of the install, and the
        // helper that was started to discover that had no work to do either way.
        //
        // On a PC whose policy BLOCKS the launcher, starting it to ask an empty question is not
        // merely wasteful, it is the loudest thing this add-in does. MEASURED 2026-09-18: Defender's
        // attack-surface-reduction rule refused the launcher on every Revit start, one event 1121
        // per start, ALL of them from this call — and each refusal raises a Windows Security
        // "blocked" notification. The user's report was that a scheduled run notified them of the
        // block and not of the run; the run's own toast was there, underneath. A question that can
        // be answered by looking at a file must not be asked by launching a process.
        try
        {
            string ledger = Schedule.ToastLedger.DefaultPath;
            if (!File.Exists(ledger) || new FileInfo(ledger).Length == 0) return;
        }
        catch { /* an unreadable ledger is not an answer; fall through and let the helper try */ }

        // GUARDED PER CANDIDATE, not around the whole loop. Until 1.3.8.2 one try/catch wrapped the
        // foreach, so the FIRST candidate throwing ended the search — and on a PC with software
        // restriction policies the first candidate (%ProgramData%) is exactly the one Windows
        // refuses to start ("This program is blocked by group policy"), while the %ProgramFiles%
        // copy 1.3.4.3 installed for that very reason was never reached. MEASURED in report
        // BKKLH3C7QV3_20260813_1851: 91 sweeps in a row failed that way, so nothing was ever tidied
        // on the one machine the tidy-up was written for. This is the same shape
        // TryDeliverLauncherToast already used, which is why toasts got through and sweeps did not.
        using Process? sweeper = LauncherRunner.Start(
            "toast sweep", s => s.Arguments = "--toast-sweep", redirectOutput: true);
        if (sweeper == null)
        {
            Diagnostics.RunTrace.Mark("toast sweep could not run from any installed copy.");
            return;
        }

        // Waited for, briefly: the whole job is a few string calls into Windows, and the summary is
        // only useful if it is read. A helper that hangs is killed rather than left holding its own
        // image open — the same rule the toast helper follows, and the reason 1.3.5 setups failed
        // with "DeleteFile failed; code 5".
        if (!sweeper.WaitForExit(ToastTimeoutMs))
        {
            try { sweeper.Kill(); } catch { /* helper only; never touch Revit here */ }
            Diagnostics.RunTrace.Mark("toast sweep timed out");
            return;         // it ran; a timeout is not a reason to try another copy
        }

        string summary = sweeper.StandardOutput.ReadToEnd().Trim();
        string sweepError = sweeper.StandardError.ReadToEnd().Trim();
        if (summary.Length > 0) Diagnostics.RunTrace.Mark(summary);
        if (sweepError.Length > 0) Diagnostics.RunTrace.Mark("toast sweep error: " + sweepError);
    }

    /// <summary>Test-only override: skip the preferred path so the fallback can be exercised on a
    /// PC where the toast works. Nothing in the product sets it.</summary>
    private static bool ForceBalloon()
    {
        string? raw = Environment.GetEnvironmentVariable("KHBATCH_FORCE_BALLOON");
        return !string.IsNullOrWhiteSpace(raw) &&
               (raw!.Trim() == "1" || raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The real Notification Center toast, raised by <c>KhBatchLauncher.exe --toast</c> calling
    /// Windows directly (see KhBatchLauncher.ToastWinRt).
    ///
    /// WHY A SECOND MECHANISM FOR THE SAME THING. The toast above is unreachable on a PC whose
    /// policy runs Windows PowerShell in ConstrainedLanguage mode — not because Windows refuses the
    /// notification, but because the interpreter we route it through may not call WinRT at all. The
    /// launcher is compiled code and already runs on that PC (it is what draws the fallback card
    /// there), so the same API is available to it with none of that policy in the way.
    ///
    /// Unlike the card, this is NOT a stand-in: what appears is a genuine Windows notification,
    /// kept in Notification Center, indistinguishable from any other because it IS one.
    ///
    /// It is waited for, briefly and to completion — unlike the card helper, which deliberately
    /// outlives the call. This process only talks to Windows and exits, so its exit code is the
    /// answer to "did the toast go out", and that answer is needed before deciding to fall back.
    /// </summary>
    private static bool TryDeliverLauncherToast(
        string title, string message, DateTimeOffset? expiry, string logPath)
    {
        string xml = BuildToastXml(title, message, logPath);

        // Identity is the outer loop now that finding a runnable copy is LauncherRunner's job: the
        // first success records the working copy, so later identities go straight to it instead of
        // re-walking the list. Same attempts, fewer of them.
        foreach (string appId in ToastAppIdCandidates())
        {
            using Process? toastHelper = LauncherRunner.Start("direct toast helper", s =>
            {
                s.Arguments = "--toast";
                s.EnvironmentVariables[Schedule.ScheduleEnvironment.NotifyToastXml] = Base64(xml);
                s.EnvironmentVariables[Schedule.ScheduleEnvironment.NotifyToastTag] =
                    Guid.NewGuid().ToString("N").Substring(0, 16);
                s.EnvironmentVariables[Schedule.ScheduleEnvironment.NotifyToastAppId] = appId;
                s.EnvironmentVariables[Schedule.ScheduleEnvironment.NotifyToastExpiry] =
                    expiry.HasValue
                        ? expiry.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                        : "";
            }, redirectOutput: true);

            if (toastHelper == null) return false;   // no copy runs here at all; the next id cannot help

            if (!toastHelper.WaitForExit(ToastTimeoutMs))
            {
                // KhBatchLauncher.exe only; never Revit — a wedged helper would otherwise hold its
                // own image open, which is what stopped 1.3.5 setups with "DeleteFile failed; code 5".
                try { toastHelper.Kill(); } catch { /* helper only; never touch Revit here */ }
                Diagnostics.RunTrace.Mark("direct toast helper timed out as " + appId);
                continue;
            }

            string error = toastHelper.StandardError.ReadToEnd().Trim();
            if (toastHelper.ExitCode == 0)
            {
                Diagnostics.RunTrace.Mark("direct toast identity: " + appId);
                return true;
            }

            Diagnostics.RunTrace.Mark(
                $"direct toast helper failed ({toastHelper.ExitCode}) as {appId}: {error}");
        }
        return false;
    }

    /// <summary>
    /// The fallback proper: hand the tray notification to <c>KhBatchLauncher.exe --notify</c> and
    /// return at once.
    ///
    /// A tray balloon raised INSIDE Revit has two defects that make it nearly useless, and both are
    /// fixed by moving it out of process:
    ///
    ///  * It dies with its process. The tray icon owns the message, so a run that closes Revit when
    ///    it finishes would destroy its own report — the case where the report matters most.
    ///  * It is not clickable without a message loop, and the delivery thread here only pumps for a
    ///    couple of seconds. The helper runs a real loop for the whole retention window, which is
    ///    what lets a failure be clicked open on the run log.
    ///
    /// The launcher is the right host for it: it already ships beside the add-in AND under Program
    /// Files (the location a managed PC's policy actually permits — the same reason the scheduled
    /// launcher moved there), and it already owns the narrow "open this log file" gate. Every
    /// candidate copy is tried, because which one this PC will run is a per-folder question.
    ///
    /// Content travels in environment variables, never on the command line: run names and log paths
    /// contain spaces and quotes, and a quoting bug would truncate the message silently.
    /// </summary>
    private static bool TryDeliverHostedBalloon(string title, string message, bool failed, string logPath)
    {
        // Output is NOT redirected here, unlike the sweep and the toast: this helper is meant to
        // outlive the call, and a redirected stream nobody drains fills its buffer and blocks the
        // child — which for a tray icon means a notification that never appears.
        using Process? helper = LauncherRunner.Start("tray notification helper", s =>
        {
            s.Arguments = "--notify";
            s.EnvironmentVariables[Schedule.ScheduleEnvironment.NotifyTitle] = title ?? "";
            s.EnvironmentVariables[Schedule.ScheduleEnvironment.NotifyMessage] = message ?? "";
            s.EnvironmentVariables[Schedule.ScheduleEnvironment.NotifyFailed] = failed ? "1" : "0";
            s.EnvironmentVariables[Schedule.ScheduleEnvironment.NotifyLog] = logPath ?? "";
            s.EnvironmentVariables[Schedule.ScheduleEnvironment.NotifyMinutes] =
                RetainMinutes().ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

        if (helper == null) return false;

        // Deliberately NOT waited for — it lives for the whole retention window, which is the
        // point. A helper that exits immediately failed to start its loop; anything else means the
        // icon is up. Half a second is enough to tell those apart without making the caller wait
        // for a notification it has already handed off.
        if (helper.WaitForExit(500) && helper.ExitCode != 0)
        {
            Diagnostics.RunTrace.Mark($"tray notification helper refused ({helper.ExitCode})");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Last resort, for a checkout with no installed launcher beside it: the same balloon raised
    /// in-process. It is brief and cannot be clicked — the delivery thread has no lasting message
    /// loop and the icon dies with Revit — so it exists only so that a dev deploy is not silent.
    ///
    /// Recovered from the pre-1.3.4.3 implementation rather than rewritten, because it encodes two
    /// things that are easy to lose and silent when lost:
    ///
    ///  * The shell registers a tray icon ASYNCHRONOUSLY. Setting <c>Visible</c> and calling
    ///    <c>ShowBalloonTip</c> in the same breath is the classic no-op — the call succeeds and no
    ///    toast is ever drawn. Hence the pump between the two.
    ///  * The icon must outlive the balloon. Disposing it straight away removes it from the tray
    ///    and takes the balloon with it.
    ///
    /// Always runs on the caller's STA delivery thread, which has no message loop of its own, so it
    /// turns the queue over by hand. No WinForms Timer variant is needed (the pre-1.3.4.3 code had
    /// one for callers on the UI thread) because every notification now arrives here the same way.
    /// </summary>
    private static bool TryDeliverTrayBalloon(string title, string message, bool failed)
    {
        NotifyIcon? icon = null;
        try
        {
            icon = new NotifyIcon
            {
                Icon = SystemIcons.Information,
                BalloonTipTitle = Trim(title, 63),      // Windows truncates past these lengths
                BalloonTipText = Trim(message, 255),
                BalloonTipIcon = failed ? ToolTipIcon.Warning : ToolTipIcon.Info,
                Text = Trim(title, 63),
                Visible = true
            };
            Pump(BalloonRegisterMs);
            icon.ShowBalloonTip(BalloonHandoffMs);
            Pump(BalloonHandoffMs);
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("tray balloon failed: " + ex.Message);
            return false;
        }
        finally
        {
            try { if (icon != null) { icon.Visible = false; icon.Dispose(); } }
            catch { /* a notification must never take down the run it reports on */ }
        }
    }

    /// <summary>Turns the message queue over for <paramref name="ms"/>, so the shell's asynchronous
    /// tray plumbing gets its chance on a thread that has no loop of its own.</summary>
    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(25);
        }
    }

    /// <summary>Windows silently truncates a balloon title past 63 characters and its text past 255.
    /// Cutting them here instead makes the loss visible (an ellipsis) rather than a sentence that
    /// just stops — run names carry project prefixes and reach these limits in practice.</summary>
    internal static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

    /// <summary>
    /// Uses Windows.UI.Notifications from the inbox Windows PowerShell host. The process is started
    /// with no console and exits after confirming the item exists in Notification Center history.
    /// The reminder scenario plus its system-owned Dismiss action keeps the banner present until
    /// the user removes it. An <c>ExpirationTime</c> is assigned for non-failures only, so Windows
    /// itself drops those from history on the hour; failures get none and persist. The app never
    /// deletes a history item either way. Windows still owns the outer retention limit (local
    /// notifications have an OS maximum of three days).
    /// </summary>
    private static bool TryDeliverRetainedToast(
        string title, string message, DateTimeOffset? expiry, string logPath)
    {
        try
        {
            string shell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(shell)) return false;

            const string script =
                "$ErrorActionPreference='Stop';" +
                "[void][Windows.UI.Notifications.ToastNotificationManager," +
                "Windows.UI.Notifications,ContentType=WindowsRuntime];" +
                "[void][Windows.UI.Notifications.ToastNotification," +
                "Windows.UI.Notifications,ContentType=WindowsRuntime];" +
                "[void][Windows.Data.Xml.Dom.XmlDocument," +
                "Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime];" +
                "$x=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(" +
                "$env:KHBATCH_TOAST_XML));" +
                "$apps=Get-StartApps;" +
                "$ids=New-Object Collections.Generic.List[string];" +
                "if(-not [string]::IsNullOrWhiteSpace($env:KHBATCH_TOAST_APP_ID)){" +
                "[void]$ids.Add($env:KHBATCH_TOAST_APP_ID)};" +
                "if(-not [string]::IsNullOrWhiteSpace($env:KHBATCH_TOAST_APP_NAME)){" +
                "$hostId=($apps|Where-Object {$_.Name -eq $env:KHBATCH_TOAST_APP_NAME}|" +
                "Select-Object -First 1).AppID;if($hostId){[void]$ids.Add($hostId)}};" +
                "$fallbackId=($apps|Where-Object {$_.Name -eq '" + PowerShellToastAppName + "'}|" +
                "Select-Object -First 1).AppID;if($fallbackId){[void]$ids.Add($fallbackId)};" +
                "$sent=$false;$last='no registered toast identity was found';" +
                "foreach($a in ($ids|Select-Object -Unique)){try{" +
                "$d=[Windows.Data.Xml.Dom.XmlDocument]::new();$d.LoadXml($x);" +
                "$n=[Windows.UI.Notifications.ToastNotification]::new($d);" +
                "$n.Tag=$env:KHBATCH_TOAST_TAG;$n.Group='" + ToastGroup + "';" +
                "if($n.PSObject.Properties.Name -contains 'ExpiresOnReboot'){$n.ExpiresOnReboot=$false};" +
                // Set only for non-failures. Windows removes the item from Notification Center at
                // this moment on its own, so nothing has to run later to tidy up — which matters
                // because the Revit that raised it has usually exited by then.
                "if(-not [string]::IsNullOrWhiteSpace($env:KHBATCH_TOAST_EXPIRY)){" +
                "$n.ExpirationTime=[DateTimeOffset]::Parse($env:KHBATCH_TOAST_EXPIRY," +
                "[Globalization.CultureInfo]::InvariantCulture," +
                "[Globalization.DateTimeStyles]::RoundtripKind)};" +
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($a).Show($n);" +
                "Start-Sleep -Milliseconds 750;" +
                "$history=@([Windows.UI.Notifications.ToastNotificationManager]::History.GetHistory($a));" +
                "$found=$false;foreach($item in $history){" +
                "if($item.Tag -eq $n.Tag -and $item.Group -eq $n.Group){" +
                "$found=$true;break}};" +
                "if($found){[Console]::Out.Write($a);$sent=$true;break};" +
                "$last='Windows displayed but did not retain the toast under '+$a" +
                "}catch{$last=$_.Exception.Message}};" +
                "if(-not $sent){throw ('No retained Notification Center item: '+$last)};";

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.EnvironmentVariables["KHBATCH_TOAST_XML"] =
                Base64(BuildToastXml(title, message, logPath));
            start.EnvironmentVariables["KHBATCH_TOAST_TAG"] =
                Guid.NewGuid().ToString("N").Substring(0, 16);
            start.EnvironmentVariables["KHBATCH_TOAST_APP_NAME"] = ToastAppName();
            start.EnvironmentVariables["KHBATCH_TOAST_APP_ID"] = PreferredToastAppId();
            start.EnvironmentVariables["KHBATCH_TOAST_EXPIRY"] = expiry.HasValue
                ? expiry.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                : "";

            using Process process = Process.Start(start)!;
            if (!process.WaitForExit(ToastTimeoutMs))
            {
                try { process.Kill(); } catch { /* helper only; never touch Revit here */ }
                Diagnostics.RunTrace.Mark("retained notification helper timed out");
                return false;
            }

            string output = process.StandardOutput.ReadToEnd().Trim();
            string error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0)
            {
                Diagnostics.RunTrace.Mark("retained notification identity: " + output);
                return true;
            }

            // Once per process. This helper is PowerShell, and on a locked-down PC it fails the same
            // way every single time — ConstrainedLanguage will not load the Windows notification
            // API. Repeating a standing condition per notification says nothing new; the first line
            // is what the diagnostic report's "POWERSHELL LOCKED DOWN" verdict cites, and the
            // fallback that follows is the one that actually delivers.
            if (!_retainedHelperFailureReported)
            {
                _retainedHelperFailureReported = true;
                bool constrainedLanguage = error.IndexOf("CLIXML", StringComparison.Ordinal) >= 0;
                Diagnostics.RunTrace.Mark(
                    $"retained notification helper failed ({process.ExitCode}), " +
                    "not retried this session" +
                    (constrainedLanguage
                        ? " — PowerShell is in ConstrainedLanguage mode (AppLocker/SRP), so the "
                          + "notification API cannot load here; the direct toast is the path that works."
                        : ": " + error));
            }
            return false;
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("retained notification failed: " + ex.Message);
            return false;
        }
    }

    private static string Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));

    /// <summary>
    /// Pure/testable toast payload. The system Dismiss action removes the reminder without
    /// launching Revit, the controller, PowerShell, or any custom popup window.
    ///
    /// When a log path is supplied, the whole toast AND an explicit "Open log" button activate it
    /// by PROTOCOL — a <c>file:</c> URI handed to the shell. Protocol activation is the only kind
    /// available here: foreground activation would need a registered COM activator, which an add-in
    /// hosted inside Revit cannot offer, and by the time a run finishes the Revit that raised the
    /// toast has usually exited anyway. The shell opens the CSV in whatever handles it.
    /// The path also stays in the body text, so a PC whose policy blocks the click can still be
    /// read from.
    /// </summary>
    internal static string BuildToastXml(string title, string message, string logPath = "")
    {
        string safeTitle = SecurityElement.Escape(title ?? "") ?? "";
        string safeMessage = SecurityElement.Escape(message ?? "") ?? "";
        string uri = LogUri(logPath);

        string launch = uri.Length == 0
            ? ""
            : $" launch=\"{SecurityElement.Escape(uri)}\" activationType=\"protocol\"";
        string openAction = uri.Length == 0
            ? ""
            : $"<action activationType=\"protocol\" arguments=\"{SecurityElement.Escape(uri)}\" " +
              "content=\"Open log\"/>";

        return $"<toast scenario=\"{RetainedScenario}\" duration=\"long\"{launch}>" +
               "<visual><binding template=\"ToastGeneric\">" +
               $"<text>{safeTitle}</text><text>{safeMessage}</text>" +
               "</binding></visual>" +
               "<actions>" + openAction +
               "<action activationType=\"system\" arguments=\"dismiss\" " +
               "content=\"Dismiss\"/></actions></toast>";
    }

    /// <summary>The scheme the installers register for <c>KhBatchLauncher.exe --open-log</c>.</summary>
    internal const string LogUriScheme = "khbatchlog";

    /// <summary>
    /// A clickable URI for the log, or "" when there is nothing safe to offer.
    ///
    /// NOT a <c>file:</c> URI, though that is the obvious choice. Windows accepts a toast whose
    /// activation target is <c>file:</c>, shows it, and then SILENTLY DROPS IT from Notification
    /// Center — measured here: of three otherwise identical toasts, the <c>file:</c> one was gone
    /// within six seconds while an <c>https:</c> one and a plain one stayed. Making the log
    /// clickable the obvious way would therefore have destroyed the very message it was meant to
    /// improve, and the failure mode is invisible: delivery reports success.
    ///
    /// So the link goes through a registered scheme instead. If that scheme is NOT registered (a
    /// dev deploy, or an installer older than 1.3.4.4), this returns "" and the toast ships with no
    /// launch target at all — degraded, but never dropped.
    /// </summary>
    internal static string LogUri(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return "";
        try
        {
            string full = Path.GetFullPath(logPath);
            if (!File.Exists(full)) return "";
            if (!LogSchemeRegistered()) return "";
            return LogUriScheme + ":" + Uri.EscapeDataString(full);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>True when this PC has a handler for the log scheme. Checked per call rather than
    /// cached: the add-in can outlive the install that registers it.</summary>
    private static bool LogSchemeRegistered()
    {
        foreach (string root in new[] { @"HKEY_CLASSES_ROOT\", @"HKEY_CURRENT_USER\Software\Classes\" })
        {
            try
            {
                object? value = Microsoft.Win32.Registry.GetValue(
                    root + LogUriScheme + @"\shell\open\command", null, null);
                if (value != null && !string.IsNullOrWhiteSpace(value.ToString())) return true;
            }
            catch { /* an unreadable hive just means "not proven registered" */ }
        }
        return false;
    }

    /// <summary>
    /// The Revit release hosting this add-in ("2026"), or "" when nothing identifies one — the
    /// standalone controller, or a host whose path does not name a year.
    ///
    /// Read from the host executable's path because this class is static and deliberately has no
    /// Revit API reference: it is called from the controller too, which has no
    /// <c>Application.VersionNumber</c> to offer.
    /// </summary>
    private static string RevitYear()
    {
        try
        {
            string executable = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            foreach (string year in new[] { "2024", "2025", "2026" })
                if (executable.IndexOf("Revit " + year, StringComparison.OrdinalIgnoreCase) >= 0)
                    return year;
        }
        catch { /* an unidentified host falls back to the bare identity */ }
        return "";
    }

    /// <summary>The bare, year-less identity for this edition — what every release before 1.3.5.4
    /// registered, and still the fallback for a PC whose shortcuts predate the per-year ones.
    /// </summary>
    private static string BaseToastAppId()
    {
        try
        {
            if (Process.GetCurrentProcess().ProcessName.Equals(
                    "KhBatchController", StringComparison.OrdinalIgnoreCase))
                return ControllerToastAppId;
        }
        catch { /* use the edition identity */ }

#if EDITION_DRW
        return DrwToastAppId;
#else
        return FullToastAppId;
#endif
    }

    /// <summary>
    /// The identity a toast is raised under, per Revit year — <c>Kanghun.KhBatchRunner.2026</c>.
    ///
    /// WHY PER YEAR. Windows takes a notification's HEADING from the Start-menu shortcut carrying
    /// the identity, and every year's shortcut used to carry the same one. With three installed,
    /// Windows picked whichever it found first, so a batch run from Revit 2026 announced itself as
    /// "KH Batch Script Runner (Revit 2024)" — reported from a real run, and exactly the kind of
    /// wrong that matters here, because which Revit ran a batch is the thing you need to know.
    ///
    /// The cost is that Notification Center groups history per year rather than as one app, which
    /// is the honest grouping: those are different runs by different programs.
    /// </summary>
    private static string PreferredToastAppId()
    {
        string year = RevitYear();
        string bare = BaseToastAppId();

        // The controller is one program, not one per year, and keeps its single identity.
        if (year.Length == 0 || bare == ControllerToastAppId) return bare;
        return bare + "." + year;
    }

    /// <summary>Identities to try in order: this year's, then the bare one an older installer
    /// registered. An identity with no Start-menu shortcut behind it may be shown by Windows and
    /// then quietly dropped from Notification Center, so the fallback is what keeps a new add-in
    /// working against shortcuts an older setup left behind.</summary>
    private static IEnumerable<string> ToastAppIdCandidates()
    {
        string preferred = PreferredToastAppId();
        yield return preferred;

        string bare = BaseToastAppId();
        if (!string.Equals(bare, preferred, StringComparison.OrdinalIgnoreCase))
            yield return bare;
    }

    private static string ToastAppName()
    {
        try
        {
            if (Process.GetCurrentProcess().ProcessName.Equals(
                    "KhBatchController", StringComparison.OrdinalIgnoreCase))
                return "KH Batch Controller";
        }
        catch { /* the registered PowerShell identity remains a safe fallback */ }

        string year = RevitYear();
        return year.Length > 0 ? "Revit " + year : "";
    }

    /// <summary>The end of a run. A clean finish is news for an hour; a finish WITH ERRORS is the
    /// one message that must survive until someone acknowledges it, so it says so in the title
    /// rather than hiding the difference in the body text.</summary>
    public static void ShowRunFinished(string runName, int succeeded, int failed, string logPath = "")
    {
        string title = FinishedTitle(runName, failed);
        string body = failed == 0
            ? $"{succeeded} model-task(s) completed. Click to open the run log."
            : $"{succeeded} completed, {failed} failed. Click to open the run log.";
        if (!string.IsNullOrWhiteSpace(logPath)) body += " " + logPath;

        // EVERY finish carries its log, not just failures. Until 1.3.5 a clean finish had no link,
        // on the reasoning that it needs no follow-up — but "it finished, what did it actually do?"
        // is the ordinary next question, and the answer is the file this message is already about.
        // Nothing opens by itself: a click is still a click, on a message the user went looking for.
        ShowAndWait(title, body, failed > 0, logPath);
    }

    public static void ShowRunFinishedAndWait(
        string runName, int succeeded, int failed, string logPath = "") =>
        ShowRunFinished(runName, succeeded, failed, logPath);

    public static void ShowRunStopped(string runName, int succeeded, int failed,
        int maxAttempts, IReadOnlyList<string> incompleteModels, string logPath)
    {
        string models = incompleteModels.Count == 0
            ? ""
            : " Not completed: " + string.Join(", ", incompleteModels);
        string log = string.IsNullOrWhiteSpace(logPath) ? "" : " Log: " + logPath;
        string body = $"Stopped after {maxAttempts} attempts. No further retry will run. " +
                      $"Completed: {succeeded}; failed: {failed}." + models + log;
        ShowAndWait($"Batch stopped - {runName}", body, failed: true, logPath: logPath);
    }

    public static void ShowRunNeedsAttention(string runName, int succeeded, int failed,
        string reason, string logPath)
    {
        string why = string.IsNullOrWhiteSpace(reason)
            ? "A Revit worker stopped making reliable progress."
            : reason;
        string log = string.IsNullOrWhiteSpace(logPath) ? "" : " Log: " + logPath;
        string body = why + " The worker was left open for inspection. No queued group or retry " +
                      $"was started. Completed: {succeeded}; failed: {failed}." + log;
        ShowAndWait($"Batch needs attention - {runName}", body, failed: true, logPath: logPath);
    }

    /// <summary>
    /// A run beginning. "Tell me at each attempt" has two shapes: a split run retries in rounds and
    /// reports them through <see cref="ShowRoundStarted"/>; every other run — an interactive batch,
    /// a "Run now", a single-instance scheduled job — has exactly one attempt, and this is it.
    ///
    /// Fire-and-forget, unlike every other message here: nothing should sit for up to fifteen
    /// seconds waiting for a banner before the first model opens. A start toast that arrives late,
    /// or not at all, costs nothing; a delayed run costs the run.
    /// </summary>
    public static void ShowRunStarted(string runName, int modelTasks) =>
        Show(StartedTitle(runName), $"{modelTasks} model-task(s) queued.");

    /// <summary>Titles are what a glance at Notification Center actually reads, so they are built
    /// here and pinned by the harness. "with errors" must survive in the TITLE: a stack of finished
    /// runs is scanned, not read, and that phrase is the only thing distinguishing the one worth
    /// opening — it is also what explains why that item alone is still there hours later.</summary>
    internal static string StartedTitle(string runName) => $"Batch started - {runName}";

    internal static string FinishedTitle(string runName, int failed) => failed == 0
        ? $"Batch finished - {runName}"
        : $"Batch finished with errors - {runName}";

    /// <param name="concurrencyCap">How many of those groups run AT ONCE. Reported because the
    /// difference between "six groups" and "six Revits" is the single most confusing thing about a
    /// split run — "it says seven and I can only see three" was a bug report, and the answer was
    /// that the machine's budget allows three at a time and the rest queue. Zero or more than the
    /// group count means no queueing, and the wording stays as it was.</param>
    public static void ShowRoundStarted(string runName, int round, int maxRounds,
        IReadOnlyList<int> groupSizes, int concurrencyCap = 0)
    {
        int models = groupSizes.Sum();
        string shape = groupSizes.Count == 0 ? "" : string.Join(" / ", groupSizes);
        bool queues = concurrencyCap > 0 && groupSizes.Count > concurrencyCap;
        string body = queues
            ? $"{models} model(s) in {groupSizes.Count} group(s), {concurrencyCap} Revit at a time: {shape}"
            : $"{models} model(s) in {groupSizes.Count} Revit instance(s): {shape}";
        ShowAndWait($"Attempt {round} of {maxRounds} - {runName}", body);
    }
}
