using System.Runtime.InteropServices;
using System.Text;
using KhBatchRunner.Diagnostics;

namespace KhBatchRunner.Batch;

/// <summary>
/// Answers modal dialogs that Revit does not own, so a third-party add-in cannot freeze an
/// unattended run.
///
/// <para><b>Why this exists.</b> <see cref="DialogSuppressor"/> handles
/// <c>UIApplication.DialogBoxShowing</c>, which fires for Revit's own dialogs. It does NOT fire for
/// a plain Win32 message box raised inside another add-in's DLL. MEASURED 2026-08-15: the
/// Navisworks exporter put up "No suitable geometry found" inside <c>Document.Export</c> in FOUR
/// parallel Revits at once and every one of them stopped dead — the journals record nothing at all
/// between 00:24:06 and 00:32:45, which is what a modal message box looks like from inside the
/// process. They resumed only because a person was at the machine to click OK four times. A split
/// run is unattended by definition; without this, that run would still be sitting there.</para>
///
/// <para><b>It reads before it clicks.</b> The dialog's caption and text go to the trail and into
/// <see cref="LastMessage"/>, which the engine attaches to the model's log row — so the run reports
/// "the exporter said: No suitable geometry found…" instead of an unexplained failure. A dialog
/// dismissed without keeping its words teaches nobody anything, and that text was the entire
/// finding on 2026-08-15.</para>
///
/// <para><b>It waits before it clicks.</b> <see cref="Grace"/> (45 s by default) is long enough for
/// an interactive user to read and answer a dialog themselves, and short enough that an unattended
/// run loses a minute rather than a night.</para>
///
/// <para>Deliberately Win32 and nothing else — the same p/invoke set already used by
/// <see cref="Schedule.RevitShutdown"/>. The controller's <c>AddInSecurityDialogHandler</c> solves
/// the same shape of problem with UI Automation, but it runs OUTSIDE the Revit it is answering; in
/// process, the main thread is blocked, so this must not depend on anything that needs it.</para>
/// </summary>
public sealed class ForeignDialogBreaker : IDisposable
{
    /// <summary>How long a dialog may sit before it is answered. Override with
    /// <c>KHBATCH_DIALOG_GRACE_SEC</c> (0–3600; 0 means answer on the next poll).</summary>
    public static TimeSpan Grace => TimeSpan.FromSeconds(GraceSeconds());

    public const string GraceEnvVar = "KHBATCH_DIALOG_GRACE_SEC";
    private const int DefaultGraceSeconds = 45;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Caption and text of the last dialog this closed, or empty. Read (and cleared) by the engine
    /// when a model fails, so the failure carries the words the user would have seen. Static
    /// because the dialog is raised deep inside a blocking API call, with no other way back to the
    /// action that provoked it.
    /// </summary>
    public static string LastMessage { get; private set; } = "";

    /// <summary>Returns <see cref="LastMessage"/> and clears it, so one dialog is reported against
    /// one model and never smeared across the models that follow.</summary>
    public static string TakeLastMessage()
    {
        string text = LastMessage;
        LastMessage = "";
        return text;
    }

    /// <summary>How many dialogs this instance has closed — surfaced by the harness and the
    /// offline fault-injection check, so a watchdog that never fires cannot pass for one that
    /// works.</summary>
    public int ClosedCount { get; private set; }

    private static ForeignDialogBreaker? _shared;
    private static readonly object SharedLock = new();

    /// <summary>The process-wide breaker, or null in an interactive session where none is running.</summary>
    public static ForeignDialogBreaker? Shared => _shared;

    /// <summary>
    /// Starts the process-wide breaker, once. Called from <c>App.OnStartup</c> for an UNATTENDED
    /// Revit and nowhere else.
    ///
    /// <para><b>Why startup and not batch start.</b> Until 1.4.2 the only breakers were the ones
    /// <see cref="BatchIdlingDriver"/> and <c>BatchRunnerWindow</c> create when a batch begins — and
    /// a batch begins from Idling, which a modal dialog blocks. MEASURED 2026-08-18 (report
    /// <c>BKKLH3C7QV3_20260818_0801</c>, journal.1309): a child armed its group at 03:10:20 and at
    /// <b>03:10:30.088</b> Revit's own startup warning flush raised
    /// <c>TaskDialog_Default_Family_Template_File_Invalid</c>. Idling never ticked again, so the
    /// batch never began, so nothing that could answer the dialog was ever constructed. The
    /// supervisor called the child "never started" fifteen minutes later and — under the policy of
    /// the day — cancelled the whole run, four of ten groups unlaunched.</para>
    ///
    /// <para>A watchdog that can only be armed by the code it protects is not a watchdog. This one
    /// runs on its own thread from the moment the add-in loads.</para>
    ///
    /// <para><b>Unattended only.</b> Auto-answering dialogs in a Revit somebody is sitting at would
    /// take decisions out of their hands. The caller's test is the presence of a job to run headless,
    /// not anything about this add-in's own state.</para>
    /// </summary>
    public static void StartShared()
    {
        lock (SharedLock) _shared ??= new ForeignDialogBreaker();
    }

    /// <summary>Stops the process-wide breaker. <c>App.OnShutdown</c> only.</summary>
    public static void StopShared()
    {
        lock (SharedLock)
        {
            _shared?.Dispose();
            _shared = null;
        }
    }

    /// <summary>
    /// A breaker for the duration of one batch: the process-wide one when it is already running,
    /// otherwise a new one the caller's <c>using</c> owns and disposes. Two breakers in one process
    /// would both answer the same dialog and double every trail line.
    ///
    /// <para>Either way it discards <see cref="LastMessage"/> first. A dialog answered during Revit's
    /// startup — before any model existed — must not be attached to the first model that later fails;
    /// the words belong to the trail, and the trail already has them.</para>
    /// </summary>
    public static IDisposable ForBatch()
    {
        LastMessage = "";
        // Never hand the shared instance to a `using`: the batch would dispose the watchdog that has
        // to outlive it. An owned one is disposed; a borrowed one is not.
        return _shared != null ? BorrowedScope.Instance : new ForeignDialogBreaker();
    }

    private sealed class BorrowedScope : IDisposable
    {
        public static readonly BorrowedScope Instance = new();
        public void Dispose() { }
    }

    private readonly Action<string>? _report;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _stop;

    /// <param name="report">Where to send a line when a dialog is seen and when one is closed.
    /// Defaults to the run trail; the offline check passes its own.</param>
    public ForeignDialogBreaker(Action<string>? report = null)
    {
        _report = report ?? (line => RunTrace.Mark(line));
        _thread = new Thread(Loop) { IsBackground = true, Name = "ForeignDialogBreaker" };
        _thread.Start();
    }

    private void Loop()
    {
        // The whole body is guarded: this runs on a background thread, where an escaping exception
        // takes the process down. A watchdog must never be able to kill what it is watching — the
        // same rule the controller's dialog handler is written to.
        try
        {
            var seenAt = new Dictionary<IntPtr, DateTime>();
            var announced = new Dictionary<IntPtr, string>();
            while (!_stop)
            {
                Sweep(seenAt, announced);
                _wake.Wait(PollInterval);
            }
        }
        catch (Exception ex)
        {
            try { _report?.Invoke("foreign dialog breaker stopped: " + ex.Message); } catch { }
        }
    }

    private void Sweep(Dictionary<IntPtr, DateTime> seenAt, Dictionary<IntPtr, string> announced)
    {
        List<IntPtr> dialogs = OwnDialogs();
        DateTime now = DateTime.Now;

        // Forget windows that have gone (answered by the user, or closed by their owner).
        foreach (IntPtr gone in seenAt.Keys.Where(h => !dialogs.Contains(h)).ToList())
        {
            // If we announced it, say how it ended. Otherwise the trail carries a line saying a
            // dialog is BLOCKING this Revit and nothing at all afterwards, which reads as an
            // unresolved freeze whether or not one happened.
            //
            // MEASURED, bundle BKKDDCJ74R3_20260819_1645: two "Model Upgrade — This linked file is
            // being upgraded … From: Autodesk Revit 2025" dialogs were announced, never answered,
            // and the process carried on regardless — Revit almost certainly closed them itself
            // when each link finished upgrading. "Almost certainly" is the problem. This line is
            // what settles it next time; the alternative reading, that the grace never expired
            // because the sweep stopped running, has completely different consequences.
            if (announced.TryGetValue(gone, out string? what) && seenAt.TryGetValue(gone, out DateTime at))
                _report?.Invoke(
                    $"foreign dialog closed itself after {(int)(now - at).TotalSeconds}s, unanswered: {what}");

            seenAt.Remove(gone);
            announced.Remove(gone);
        }

        foreach (IntPtr hwnd in dialogs)
        {
            if (!seenAt.TryGetValue(hwnd, out DateTime first))
            {
                seenAt[hwnd] = now;
                continue;
            }

            // Say it is there as soon as it is there. A run that pauses for the grace period should
            // not look like a stall on the trail — the same reason an open is traced before it
            // blocks (see BatchEngine.OpenForModel).
            if (!announced.ContainsKey(hwnd))
            {
                string seen = Describe(hwnd);
                announced[hwnd] = seen;   // kept so the disappearance above can still name it
                _report?.Invoke("foreign dialog is blocking this Revit: " + seen);
            }

            if (!ShouldAct(first, now, Grace)) continue;

            string description = Describe(hwnd);
            LastMessage = description;
            _report?.Invoke($"foreign dialog answered after {(int)(now - first).TotalSeconds}s: {description}");
            Answer(hwnd);
            ClosedCount++;
            seenAt.Remove(hwnd);
            announced.Remove(hwnd);
        }
    }

    /// <summary>
    /// Clicks the dialog's affirmative button, and falls back to closing the window.
    ///
    /// <para><b>BM_CLICK on the button, not WM_COMMAND on the dialog.</b> The obvious
    /// <c>PostMessage(dialog, WM_COMMAND, IDOK, 0)</c> is ignored by a <c>MessageBox</c>: its dialog
    /// procedure wants the clicked control's handle in <c>lParam</c>, and without it the message is
    /// dropped. VERIFIED by fault injection (<c>tools\ThemePreview -- dialog</c>) — the first
    /// version of this method found the box, read its text, and left it on screen for the full
    /// 30-second timeout, which is precisely the failure this class exists to prevent.</para>
    /// </summary>
    private static void Answer(IntPtr dialog)
    {
        IntPtr button = IntPtr.Zero;
        try
        {
            EnumChildWindows(dialog, (child, _) =>
            {
                try
                {
                    if (!ClassOf(child).Equals("Button", StringComparison.OrdinalIgnoreCase)) return true;
                    // "&Yes" → "yes". The accelerator ampersand is part of the control's text.
                    string label = TextOf(child).Replace("&", "").Trim().ToLowerInvariant();
                    if (AffirmativeButtons.Contains(label))
                    {
                        button = child;
                        return false; // first affirmative wins; OK/Yes precede Cancel in a message box
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        if (button != IntPtr.Zero)
        {
            PostMessage(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        // No button we recognise (a localised build, or a dialog with only a title bar X): closing
        // the window is the only remaining way to let the blocked thread continue.
        PostMessage(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>The "keep going" answers, in the order a message box lays them out. Deliberately
    /// not "Yes to All"/"Delete"/"Overwrite": this closes a blocking dialog, it does not make
    /// decisions about a model.</summary>
    private static readonly HashSet<string> AffirmativeButtons =
        new(StringComparer.Ordinal) { "ok", "yes", "close", "continue" };

    /// <summary>Whether a dialog first seen at <paramref name="firstSeen"/> has waited long enough.
    /// Pure, so the harness can pin the rule that decides how long a run is allowed to freeze.</summary>
    public static bool ShouldAct(DateTime firstSeen, DateTime now, TimeSpan grace) =>
        now - firstSeen >= grace;

    /// <summary>Caption and body as one line: <c>Caption — text</c>, whitespace collapsed and
    /// clipped, because this ends up in a CSV cell.</summary>
    public static string Compose(string caption, string body)
    {
        string one = string.Join(" ", (caption + " — " + body)
            .Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0));
        one = one.Trim(' ', '—', '-');
        return one.Length > 400 ? one.Substring(0, 400) + "…" : one;
    }

    /// <summary>Visible <c>#32770</c> dialogs owned by THIS process. Our own windows are WinForms
    /// classes, so they are never candidates; Revit's own dialogs are, which is correct — one still
    /// on screen after the grace has already defeated <see cref="DialogSuppressor"/>.</summary>
    private static List<IntPtr> OwnDialogs()
    {
        var found = new List<IntPtr>();
        // Process.GetCurrentProcess().Id, not Environment.ProcessId: the 2024 build targets net48,
        // which does not have the latter.
        uint mine;
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
            mine = (uint)self.Id;
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != mine) return true;
                if (ClassOf(hwnd) != DialogWindowClass) return true;
                found.Add(hwnd);
            }
            catch { /* a window can vanish mid-enumeration; skip it */ }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string Describe(IntPtr hwnd)
    {
        var body = new StringBuilder();
        try
        {
            EnumChildWindows(hwnd, (child, _) =>
            {
                try
                {
                    // Static text only: buttons would add "OK Cancel" noise to the sentence that
                    // matters, and an edit box may hold something private.
                    if (!ClassOf(child).Equals("Static", StringComparison.OrdinalIgnoreCase)) return true;
                    string text = TextOf(child);
                    if (text.Length > 0) body.Append(text).Append(' ');
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return Compose(TextOf(hwnd), body.ToString());
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        int length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : "";
    }

    private static string TextOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(1024);
        // WM_GETTEXT rather than GetWindowText: it reads a control's text even when the window
        // belongs to a thread that is blocked inside a modal loop, which is exactly this case.
        IntPtr length = SendMessage(hwnd, WM_GETTEXT, (IntPtr)buffer.Capacity, buffer);
        return length == IntPtr.Zero ? "" : buffer.ToString().Trim();
    }

    private static int GraceSeconds()
    {
        try
        {
            string? raw = Environment.GetEnvironmentVariable(GraceEnvVar);
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int seconds)
                && seconds >= 0 && seconds <= 3600)
                return seconds;
        }
        catch { /* fall through to the default */ }
        return DefaultGraceSeconds;
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _wake.Set();
        try { _thread.Join(TimeSpan.FromSeconds(5)); } catch { }
        _wake.Dispose();
    }

    private const string DialogWindowClass = "#32770";
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_CLOSE = 0x0010;
    private const uint BM_CLICK = 0x00F5;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
