using KhBatchRunner.Schedule;

using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

using Timer = System.Windows.Forms.Timer;

namespace KhBatchLauncher;

/// <summary>
/// Notification mode: <c>KhBatchLauncher.exe --notify</c>, with the content passed in environment
/// variables (never on the command line — a run name or a log path carries spaces and quotes, and a
/// quoting bug here would silently truncate the message).
///
/// WHY A SEPARATE PROCESS. This is the fallback used when the preferred Notification Center toast
/// cannot be raised — on a PC whose policy puts Windows PowerShell in ConstrainedLanguage mode, the
/// WinRT notification API cannot be loaded at all (see UI.RunNotification). A plain window needs none
/// of that, but it has to outlive the Revit that raised it:
///
///  * A scheduled run set to "Close Revit when complete" would otherwise report its result and
///    destroy the report in the same breath — the case where the notification matters most.
///  * CLICKING IT NEEDS A MESSAGE LOOP. Revit's own loop is gone by then, and the add-in's delivery
///    thread only pumps for a couple of seconds. This process runs a real
///    <see cref="Application.Run()"/> loop for the whole retention window, which is what makes
///    "click it to open the log" work at all.
///
/// ONE THING APPEARS. Until 1.3.5.1 this also raised a tray balloon and left a tray icon behind, from
/// before the card existed; the balloon caught the eye for three seconds and the icon was the thing
/// nobody found. Two of them announcing one run meant the message arrived twice and waited nowhere.
/// The card is the notification now.
///
/// Retention is deliberately shaped like the toast policy it stands in for: progress news clears
/// itself after an hour, a failure waits to be acknowledged.
/// </summary>
internal static class NotifyHost
{
    /// <summary>A failure waits to be acknowledged rather than expiring — but not forever. Left
    /// unbounded, an unattended PC would collect one idle process per failed overnight run until
    /// somebody logged in. A working day is long enough for "still there in the morning" to hold.
    /// </summary>
    private const int FailureHoldMinutes = 8 * 60;

    private const int DefaultHoldMinutes = 60;

    private const string DefaultAppName = "KH Batch Script Runner";

    internal static int Run()
    {
        string title = Env(ScheduleEnvironment.NotifyTitle);
        string message = Env(ScheduleEnvironment.NotifyMessage);
        if (title.Length == 0 && message.Length == 0) return Program.ExitBadArguments;

        string app = Env(ScheduleEnvironment.NotifyApp);
        if (app.Length == 0) app = DefaultAppName;

        bool failed = Env(ScheduleEnvironment.NotifyFailed) == "1";
        string log = Env(ScheduleEnvironment.NotifyLog);
        bool canOpenLog = log.Length > 0 && Program.CanOpenLog(log);

        return Show(app, title, message, failed, log, canOpenLog, HoldMinutes(failed));
    }

    /// <summary>
    /// <c>--notify-demo</c>: the card with a sample run behind it, so the look can be reviewed
    /// without waiting for a batch to finish. It passes NO log path, so it cannot open anything —
    /// this adds no reach to an executable that is already callable from a web page through the
    /// <c>khbatchlog:</c> handler.
    /// </summary>
    internal static int Demo(bool failed, string? savePath)
    {
        string title = failed ? "Batch run finished with errors" : "Batch run finished";
        string message = failed
            ? "9 of 12 models OK, 3 failed — Run Nightly MEP check"
            : "12 of 12 models OK — Run Nightly MEP check";

        if (savePath != null) return Save(DefaultAppName, title, message, failed, savePath);

        return Show(DefaultAppName, title, message, failed, log: "", canOpenLog: false,
                    holdMinutes: DefaultHoldMinutes);
    }

    /// <summary>Writes what the card looks like to a PNG and exits, for reviewing the design where
    /// it cannot be seen on screen.</summary>
    private static int Save(string app, string title, string message, bool failed, string path)
    {
        try
        {
            // canOpenLog: true so the two-button layout is what gets reviewed — no log is opened,
            // because nothing is clicked and the card is never shown.
            using var card = new NotifyCard(app, title, message, failed, logPath: "", canOpenLog: true);
            using Bitmap image = card.Render();
            image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return 0;
        }
        catch (Exception ex)
        {
            Record(ex);
            return Program.ExitCouldNotStart;
        }
    }

    private static int Show(string app, string title, string message, bool failed,
                            string log, bool canOpenLog, int holdMinutes)
    {
        try
        {
            var expiry = new Timer { Interval = Math.Max(1, holdMinutes) * 60_000 };
            expiry.Tick += (_, _) =>
            {
                expiry.Stop();
                expiry.Dispose();
                Application.ExitThread();
            };
            expiry.Start();

            // Closing the card ends the process — the message has been dealt with.
            var card = new NotifyCard(app, title, message, failed, log, canOpenLog);
            card.FormClosed += (_, _) => Application.ExitThread();
            card.Show();
            Trace(card);

            Application.Run();
            return 0;
        }
        catch (Exception ex)
        {
            // A notification that fails silently is the exact bug this whole feature exists to end,
            // and this process has no console and no --log to fall back on. One line in a known file
            // costs nothing and is the difference between "it did not appear" and knowing why; the
            // diagnostic collector picks the file up.
            Record(ex);
            return Program.ExitCouldNotStart;
        }
    }

    /// <summary>
    /// Where the card actually landed, written only when <c>KHBATCH_NOTIFY_TRACE=1</c> is set. "The
    /// notification did not appear" has more than one cause — off the working area, sized to
    /// nothing, clipped away by its own region — and they are indistinguishable from a chair. This
    /// is the same kind of switch as KHBATCH_FORCE_BALLOON, and off by default for the same reason.
    /// </summary>
    private static void Trace(Form card)
    {
        if (Env("KHBATCH_NOTIFY_TRACE") != "1") return;
        try
        {
            string region = card.Region == null
                ? "(none)"
                : card.Region.GetBounds(card.CreateGraphics()).ToString();
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "khbatch-notify-trace.log"),
                string.Format(CultureInfo.InvariantCulture,
                    "{0} bounds={1} visible={2} region={3} screen={4}{5}",
                    DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    card.Bounds, card.Visible, region,
                    Screen.PrimaryScreen?.WorkingArea, Environment.NewLine));
        }
        catch { /* a trace is never worth failing a notification over */ }
    }

    private static void Record(Exception ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "khbatch-notify-error.log");
            File.AppendAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                " notification could not be shown: " + ex + Environment.NewLine);
        }
        catch { /* nothing left to report it to */ }
    }

    private static int HoldMinutes(bool failed)
    {
        if (failed) return FailureHoldMinutes;

        // Honours the same KHBATCH_NOTIFY_RETAIN_MINUTES the toast path uses, passed through by the
        // add-in, so both mechanisms answer to one setting. A non-positive value means "as long as
        // this fallback can" rather than "forever", which it has no way to offer.
        string raw = Env(ScheduleEnvironment.NotifyMinutes);
        if (raw.Length > 0 &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
            return minutes > 0 ? minutes : FailureHoldMinutes;
        return DefaultHoldMinutes;
    }

    private static string Env(string name)
    {
        try { return Environment.GetEnvironmentVariable(name) ?? ""; }
        catch { return ""; }
    }
}
