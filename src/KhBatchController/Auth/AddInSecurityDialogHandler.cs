using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace KhBatchController.Auth;

/// <summary>
/// Answers Revit's "The publisher of this add-in could not be verified" prompt
/// (<c>TaskDialog_Security_Unsigned_File_Loading</c>) on behalf of a Revit that is being started
/// unattended.
///
/// WHY THIS IS NEEDED. That dialog appears BEFORE add-ins load and re-appears every time the dll
/// changes — so during development it blocks every launch, and it blocked the first broker probe
/// for its full four-minute timeout. Revit's own API cannot dismiss it (the add-in is not loaded
/// yet, by definition), so the process that launched Revit has to do it from outside.
///
/// SCOPE, deliberately narrow: it only ever clicks <b>Always Load</b>, only on a dialog owned by
/// the Revit process this controller started itself, and it stops the moment the broker answers.
/// It is not a general "click OK on anything" watchdog — the equivalent of that inside Revit is
/// <c>DialogSuppressor</c>, which is a different tool for a different context. Every click is
/// reported to the caller rather than done silently: auto-answering a security prompt is a real
/// decision, and it should be visible in the log that it happened.
/// </summary>
internal sealed class AddInSecurityDialogHandler : IDisposable
{
    /// <summary>Matched against the button text. Revit localises this; the English build says
    /// "Always Load", and the fallback below catches the "Load Once" variant too.</summary>
    private static readonly string[] ButtonTexts = { "Always Load", "Load Once" };

    private readonly int _revitPid;
    private readonly Action<string>? _report;
    // A plain flag + event rather than a CancellationTokenSource. The CTS version disposed itself
    // while this thread was still sleeping, so the next loop touched a disposed object and the
    // resulting exception — on a background thread, where nothing catches it — killed the whole
    // process mid-session. A helper that answers a dialog must never be able to do that.
    private volatile bool _stop;
    private bool _warned;
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;

    public int ClickCount { get; private set; }

    public AddInSecurityDialogHandler(int revitPid, Action<string>? report)
    {
        _revitPid = revitPid;
        _report = report;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "AddInSecurityDialogHandler" };
        _thread.SetApartmentState(ApartmentState.STA); // UI Automation wants STA
        _thread.Start();
    }

    private void Loop()
    {
        // Belt and braces: the whole thread body is guarded, so no failure mode here — UI Automation
        // quirks, teardown races, anything — can escape onto a background thread and abort the app.
        try
        {
            while (!_stop)
            {
                try { TryDismiss(); }
                catch { /* UIA throws freely while windows come and go; never let it kill the wait */ }
                _wake.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch { /* never take the process down from here */ }
    }

    private void TryDismiss()
    {
        Process revit;
        try { revit = Process.GetProcessById(_revitPid); }
        catch (ArgumentException) { return; } // Revit is gone; nothing to answer
        if (revit.HasExited) return;

        // Only windows owned by the Revit instance we started — never a dialog belonging to some
        // other Revit the user happens to be working in.
        var scope = new PropertyCondition(AutomationElement.ProcessIdProperty, _revitPid);
        AutomationElementCollection windows =
            AutomationElement.RootElement.FindAll(TreeScope.Children, scope);

        foreach (AutomationElement window in windows)
        {
            for (int i = 0; i < ButtonTexts.Length; i++)
            {
                // DETECT with UI Automation…
                AutomationElement? button = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, ButtonTexts[i]));
                if (button is null) continue;

                // …but ACTIVATE with Win32: the command links expose no InvokePattern, so UIA can
                // find them and cannot click them.
                //
                // PROBED 2026-07-26 on Revit 2026. Each command link IS a real child window of
                // class "Button" with a usable HWND — which UIA hands over as NativeWindowHandle —
                // but its dialog control id is 0. That last detail is what broke the previous
                // version: it posted WM_COMMAND(1001) to the DIALOG, and with no control carrying
                // id 1001 the message went nowhere. PostMessage still returns success, so the
                // handler logged "Answered…" on every poll while the prompt sat there untouched,
                // and the caller waited out its whole timeout. BM_CLICK to the button's own HWND
                // dismisses it.
                //
                // The class check is not optional. Posting a click at a window that is not a
                // Button — the enclosing Revit frame, say — is how this once made Revit run
                // internal debug commands once a second.
                IntPtr hwnd = ButtonHandle(button);
                if (hwnd != IntPtr.Zero)
                {
                    PostMessage(hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    ClickCount++;
                    _report?.Invoke($"Answered Revit's unsigned-add-in security prompt: \"{ButtonTexts[i]}\".");
                    return;
                }

                // Found the link but not a clickable window behind it. Say so once: silence here
                // reads exactly like success, which is the trap the old code fell into.
                if (!_warned)
                {
                    _warned = true;
                    _report?.Invoke(
                        "Revit is showing \"The publisher of this add-in could not be verified\" and this " +
                        "build cannot reach the button. It appears when the SAME AddInId is installed " +
                        "twice — a dev build in %AppData% and the installed product in %ProgramData% — so " +
                        "Revit trusts the id but not the assembly. Remove one of them (elevated), then " +
                        "answer the prompt once and close Revit NORMALLY: the trust is only written on a " +
                        "clean shutdown.");
                }
                return;
            }
        }
    }

    /// <summary>
    /// The command link's own window handle, but only if it really is a Win32 <c>Button</c> and it
    /// sits inside a real dialog (class <c>#32770</c> — what a TaskDialog is). Returns
    /// <see cref="IntPtr.Zero"/> otherwise, which is the safe answer: better to leave the prompt
    /// for a human than to post a click at a window that will interpret it as something else.
    /// </summary>
    private static IntPtr ButtonHandle(AutomationElement button)
    {
        try
        {
            int raw = button.Current.NativeWindowHandle;
            if (raw == 0) return IntPtr.Zero;

            var hwnd = new IntPtr(raw);
            if (ClassOf(hwnd) != ButtonWindowClass) return IntPtr.Zero;

            // Confirm the ancestry, so a "Always Load" button belonging to something that merely
            // shares the caption can never be clicked by this.
            TreeWalker walker = TreeWalker.ControlViewWalker;
            AutomationElement? node = walker.GetParent(button);
            for (int depth = 0; node != null && depth < 12; depth++)
            {
                int parentRaw = node.Current.NativeWindowHandle;
                if (parentRaw != 0 && ClassOf(new IntPtr(parentRaw)) == DialogWindowClass) return hwnd;
                node = walker.GetParent(node);
            }
        }
        catch { /* the tree can change underneath us; treat as "not found" */ }

        return IntPtr.Zero;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>The Win32 class every standard dialog — including TaskDialog — is registered as.</summary>
    private const string DialogWindowClass = "#32770";

    /// <summary>The class the TaskDialog's command links actually are, despite UIA calling them Panes.</summary>
    private const string ButtonWindowClass = "Button";

    /// <summary>Clicks a button by its own handle. Unlike WM_COMMAND this needs no control id —
    /// which matters, because these carry id 0.</summary>
    private const int BM_CLICK = 0x00F5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int maxCount);

    /// <summary>
    /// TaskDialog command-link ids, as named in Revit's own journal for this dialog:
    /// <c>1001 : Always Load</c>, <c>1002 : Load Once</c>, <c>1003 : Do Not Load</c>.
    /// Index-aligned with <see cref="ButtonTexts"/>. 1003 is never sent.
    /// </summary>
    private static readonly int[] ButtonIds = { 1001, 1002 };   // kept for reference only — see ButtonHandle

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public void Dispose()
    {
        _stop = true;
        _wake.Set();

        bool stopped = false;
        try { stopped = _thread is null || _thread.Join(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }

        // Only dispose once the thread is provably finished. If it somehow outlived the join, leave
        // the event to the GC — disposing something still in use is what caused the crash this
        // method is now written to avoid.
        if (stopped)
        {
            try { _wake.Dispose(); } catch { /* best effort */ }
        }
    }
}
