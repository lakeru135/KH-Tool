using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using KhBatchRunner.Auth;

namespace KhBatchController.Auth;

/// <summary>One entry of the add-in's action registry, as reported by the broker.</summary>
/// <param name="Name">Registry name — the string a job's RunItem carries.</param>
/// <param name="Category">"Check" (read-only) or "Action" (writes to the model).</param>
/// <param name="ModifiesModel">Whether running it changes the model.</param>
/// <param name="Worksets">The action's declared workset posture as the broker named it, or "" when
/// the brokering Revit runs a build older than the field. Empty means UNKNOWN, never "narrow".</param>
public readonly record struct BrokerAction(
    string Name, string Category, bool ModifiesModel, string Worksets = "");

/// <summary>
/// The controller's <see cref="ITokenProvider"/>: launches a Revit whose add-in runs as a token
/// broker (<c>KhBatchRunner.Auth.TokenBrokerServer</c>), hides its window, and answers token
/// requests over a named pipe for as long as this process lives.
///
/// This is a workaround with its eyes open. <c>SSONET.dll</c> is bound to the Revit API assemblies
/// and Autodesk persists no token on disk, so a signed-in Revit is the only thing that can vend a
/// session token — the broker just makes it vend one to somebody else. A registered APS app is the
/// route that removes Revit from this picture; because this class implements the same
/// <see cref="ITokenProvider"/> the add-in already consumes, swapping to it later touches nothing
/// but the construction site.
///
/// Costs to remember: a Revit licence seat is held for the controller's lifetime, and the first
/// token costs a Revit cold start.
/// </summary>
public sealed class BrokerTokenProvider : ITokenProvider, IDisposable
{
    /// <summary>Revit cold start on a slow machine is minutes, not seconds — and an unsigned-add-in
    /// security prompt may be sitting in front of it waiting for a human.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(4);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a fetched token is reused before going back to the broker.
    /// <see cref="ITokenProvider"/> says "never cache", and inside Revit that is right — the fetch
    /// is a cheap in-process reflection call. Here every fetch is a cross-process round trip that
    /// waits on Revit's Idling tick (~1s), and <see cref="DataManagementClient"/> fetches once PER
    /// REST CALL — so honouring that contract literally froze the UI for seconds on every folder
    /// expand. Forma session tokens last on the order of an hour; a conservative few minutes keeps
    /// the window responsive while still following the session.
    /// </summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    private readonly string _pipeName = "KhBatchBroker-" + Guid.NewGuid().ToString("N");
    private readonly string _revitExe;
    private Process? _revit;
    private bool _hidden;
    // Process.MainWindowHandle becomes zero after SW_HIDE on some Revit builds.  Keep the HWND we
    // actually hid so shutdown can still send WM_CLOSE and persist an "Always Load" trust answer.
    private IntPtr _mainWindowHandle;

    private readonly object _tokenGate = new();
    private string _cachedToken = "";
    private DateTime _cachedAt = DateTime.MinValue;

    /// <summary>Reason the last call failed, for UI diagnostics — mirrors
    /// <c>SessionTokenProvider.LastError</c> so callers can treat the two the same.</summary>
    public string? LastError { get; private set; }

    public string RevitExePath => _revitExe;
    public string RevitVersion => RevitLocator.VersionOf(_revitExe);
    public int BrokerPid => _revit?.Id ?? 0;

    public BrokerTokenProvider(string revitExePath) => _revitExe = revitExePath;

    /// <summary>
    /// Starts the broker and blocks until it answers, then hides its window.
    /// </summary>
    /// <param name="progress">Called with human-readable status while waiting — a cold start is long
    /// enough that a silent wait looks like a hang.</param>
    public void Start(Action<string>? progress = null)
    {
        if (_revit is { HasExited: false }) return;

        var psi = new ProcessStartInfo
        {
            FileName = _revitExe,
            UseShellExecute = false, // required for EnvironmentVariables to be honoured
            WorkingDirectory = Path.GetDirectoryName(_revitExe) ?? Environment.CurrentDirectory
        };
        psi.EnvironmentVariables["KHBATCH_TOKEN_BROKER"] = _pipeName;
        // How the broker knows when to stop existing. A hidden Revit has no window to close, so
        // without this a crashed controller strands one holding a licence seat.
        psi.EnvironmentVariables["KHBATCH_BROKER_OWNER"] =
            Process.GetCurrentProcess().Id.ToString();
        // Revit gets a private per-session temp dir, so tell the broker to log where WE can read it.
        psi.EnvironmentVariables["KHBATCH_BROKER_LOG"] =
            Path.GetDirectoryName(ControllerLog.FilePath) ?? "";

        progress?.Invoke($"Starting Revit {RevitVersion} as token broker…");
        ControllerLog.Write($"launching {_revitExe}");
        ControllerLog.Write($"  KHBATCH_TOKEN_BROKER={_pipeName}");
        ControllerLog.Write($"  KHBATCH_BROKER_OWNER={psi.EnvironmentVariables["KHBATCH_BROKER_OWNER"]}");

        _revit = Process.Start(psi)
                 ?? throw new InvalidOperationException("Process.Start returned no process for " + _revitExe);

        ControllerLog.Write($"started pid {_revit.Id} ({_revit.ProcessName})");

        // Revit's unsigned-add-in security prompt appears BEFORE add-ins load, so the broker can
        // never answer it itself — the launcher has to, or the wait below just times out (which is
        // exactly what the first probe did). Runs only for the duration of the wait.
        using var securityPrompt = new AddInSecurityDialogHandler(_revit.Id, progress);
        securityPrompt.Start();

        WaitForBroker(progress);

        // Hidden only AFTER the handshake, never before. Until the add-in answers, Revit may be
        // showing something that needs a human: the Autodesk sign-in prompt, or the unsigned-add-in
        // "Always Load" security dialog that has silently blocked runs in this project before.
        // Hiding first would make those invisible and the wait unexplainable.
        Hide(progress);
    }

    private void WaitForBroker(Action<string>? progress)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        int announced = 0;

        while (DateTime.UtcNow < deadline)
        {
            if (_revit is { HasExited: true })
            {
                // A Revit that exits while we wait usually means it handed off to another instance
                // (or relaunched itself), which would take our environment variables with it — so
                // record what Revit processes exist now, not just that ours died.
                ControllerLog.Write($"child pid {_revit.Id} EXITED code {_revit.ExitCode}; " +
                                    $"live Revit pids now: {LiveRevitPids()}");
                throw new InvalidOperationException(
                    $"Revit exited (code {_revit.ExitCode}) before the token broker answered. " +
                    "Check %TEMP%\\KhBatchRunner\\broker.log — the add-in may not be installed for this release.");
            }

            if (TrySend("PING", out string reply, attempts: 1) && reply == "PONG")
            {
                ControllerLog.Write("handshake OK (PONG)");
                progress?.Invoke("Broker is up.");
                return;
            }

            int elapsed = (int)(StartTimeout - (deadline - DateTime.UtcNow)).TotalSeconds;
            if (elapsed / 15 > announced)
            {
                announced = elapsed / 15;
                ControllerLog.Write($"still waiting at {elapsed}s — last pipe error: {LastError}; " +
                                    $"live Revit pids: {LiveRevitPids()}");
                progress?.Invoke($"Waiting for Revit to finish starting… ({elapsed}s) — " +
                                 "if a Revit dialog is open, answer it.");
            }
            Thread.Sleep(1000);
        }

        ControllerLog.Write($"TIMEOUT after {StartTimeout.TotalMinutes:F0} min; " +
                            $"live Revit pids: {LiveRevitPids()}");
        throw new TimeoutException(
            $"The token broker did not answer within {StartTimeout.TotalMinutes:F0} minutes. " +
            "Revit's window was left visible on purpose — check it for a dialog, and see " +
            "%TEMP%\\KhBatchRunner\\broker.log.");
    }

    /// <summary>Every Revit pid on the machine — distinguishes "our Revit is slow" from "our Revit
    /// died and a different one is running", which is what an elevation relaunch or a single-instance
    /// hand-off looks like from out here.</summary>
    private static string LiveRevitPids()
    {
        try
        {
            int[] pids = Process.GetProcessesByName("Revit").Select(p => p.Id).ToArray();
            return pids.Length == 0 ? "(none)" : string.Join(", ", pids);
        }
        catch { return "(unavailable)"; }
    }

    public string GetAccessToken()
    {
        lock (_tokenGate)
        {
            if (_cachedToken.Length > 0 && DateTime.UtcNow - _cachedAt < TokenLifetime)
                return _cachedToken;
        }

        string fresh = FetchToken();
        if (fresh.Length > 0)
        {
            lock (_tokenGate)
            {
                _cachedToken = fresh;
                _cachedAt = DateTime.UtcNow;
            }
        }
        return fresh;
    }

    /// <summary>
    /// Drops the cached token so the next call goes back to the broker. Call this when Forma answers
    /// 401/403 — the session may have been refreshed on the Revit side, and a stale cached string
    /// would otherwise keep failing for the rest of the cache window.
    /// </summary>
    public void InvalidateToken()
    {
        lock (_tokenGate)
        {
            _cachedToken = "";
            _cachedAt = DateTime.MinValue;
        }
    }

    private string FetchToken()
    {
        LastError = null;
        EnsureRunning();

        if (!TrySend("TOKEN", out string reply))
        {
            LastError = "Could not reach the token broker: " + (LastError ?? "no response");
            return "";
        }
        if (reply.StartsWith("OK ", StringComparison.Ordinal))
            return reply.Substring(3);

        LastError = reply.StartsWith("ERR ", StringComparison.Ordinal) ? reply.Substring(4) : reply;
        return "";
    }

    /// <summary>
    /// The batch actions the running Revit's <c>ActionRegistry</c> offers. Asked rather than
    /// duplicated: the registry returns Revit-typed objects the controller cannot reference, and a
    /// hand-maintained copy here would silently drift from what the add-in actually runs.
    /// </summary>
    public List<string> GetActionNames() => GetActions().Select(a => a.Name).ToList();

    /// <summary>
    /// The registry's actions with their category, so the launcher can group them exactly as the
    /// ribbon does (Check → "Multi Model Check", Action → "Multi Model Action") without keeping its
    /// own copy of which action goes where.
    /// </summary>
    public List<BrokerAction> GetActions()
    {
        // Cached for the life of the broker. The registry is a fixed list compiled into the add-in
        // — it cannot change while that Revit is running — but each call is a pipe round-trip that
        // has to wait for the broker's Idling handler to pick the request up. MEASURED 2026-07-26:
        // Schedule Run took 15.5 SECONDS to open, essentially all of it here, because the window
        // asks for the list again through UI.HostServices after the launcher already had it.
        // Only a successful list is cached, so a failed first attempt still retries.
        if (_actionCache is { Count: > 0 }) return new List<BrokerAction>(_actionCache);

        EnsureRunning();
        var actions = new List<BrokerAction>();

        if (!TrySend("ACTIONS", out string reply) || !reply.StartsWith("OK ", StringComparison.Ordinal))
        {
            LastError = "The broker did not return an action list: " + (reply.Length > 0 ? reply : LastError);
            return actions;
        }

        foreach (string record in reply.Substring(3).Split(';'))
        {
            if (record.Length == 0) continue;
            string[] parts = record.Split('|'); // name|category|modifies|worksets
            actions.Add(new BrokerAction(
                parts[0],
                parts.Length > 1 ? parts[1] : "Check",
                parts.Length > 2 && bool.TryParse(parts[2], out bool m) && m,
                parts.Length > 3 ? parts[3] : ""));
        }

        if (actions.Count > 0) _actionCache = new List<BrokerAction>(actions);
        return actions;
    }

    /// <summary>The action list as the broker last reported it. See <see cref="GetActions"/>.</summary>
    private List<BrokerAction>? _actionCache;

    /// <summary>The Autodesk account the brokering Revit is signed in as — the human-readable proof
    /// that the token belongs to who the user thinks it does.</summary>
    public string GetLoginUser()
    {
        EnsureRunning();
        if (TrySend("WHOAMI", out string reply) && reply.StartsWith("OK ", StringComparison.Ordinal))
            return reply.Substring(3);
        return "";
    }

    private void EnsureRunning()
    {
        if (_revit is null || _revit.HasExited)
            throw new InvalidOperationException(
                "The token broker is not running — call Start() first (or it exited; see broker.log).");
    }

    /// <summary>
    /// Sends one request, retrying briefly. The broker serves a single connection at a time and
    /// re-creates the pipe after each one, so a request issued in that gap finds no pipe and fails —
    /// which is why the action list came back EMPTY the first time a window asked for it and was
    /// fine on the second. A few short retries close that window; a genuinely dead broker still
    /// fails fast.
    /// </summary>
    /// <param name="attempts">
    /// 1 for the startup handshake: there the pipe legitimately does not exist yet, the caller is
    /// already polling on its own schedule, and retrying inside would multiply the connect timeout.
    /// </param>
    private bool TrySend(string verb, out string reply, int attempts = 5)
    {
        for (int i = 1; i <= attempts; i++)
        {
            if (TrySendOnce(verb, out reply)) return true;
            if (i < attempts) Thread.Sleep(150);
        }
        reply = "";
        return false;
    }

    private bool TrySendOnce(string verb, out string reply)
    {
        reply = "";
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024, leaveOpen: true);

            writer.WriteLine(verb);
            reply = (reader.ReadLine() ?? "").Trim();
            return reply.Length > 0;
        }
        catch (Exception ex)
        {
            // Routine while Revit is still starting — the caller decides whether to retry.
            LastError = ex.Message;
            return false;
        }
    }

    // ---- window hiding ---------------------------------------------------------------------

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void Hide(Action<string>? progress)
    {
        if (_revit is null) return;

        // MainWindowHandle is cached on the Process object, so it needs a Refresh to appear.
        for (int i = 0; i < 20; i++)
        {
            _revit.Refresh();
            if (_revit.MainWindowHandle != IntPtr.Zero)
            {
                _mainWindowHandle = _revit.MainWindowHandle;
                ShowWindow(_mainWindowHandle, SW_HIDE);
                _hidden = true;
                progress?.Invoke("Revit hidden — it is now serving tokens in the background.");
                return;
            }
            Thread.Sleep(250);
        }
        // Not fatal: a visible broker still works, it is just untidy. Never fail the run over cosmetics.
        progress?.Invoke("Note: could not find Revit's main window to hide it; it will stay visible.");
    }

    /// <summary>Brings the hidden broker back into view — the escape hatch when Revit is suspected of
    /// sitting on a dialog nobody can see.</summary>
    public void ShowBrokerWindow()
    {
        if (_revit is null || _revit.HasExited) return;
        _revit.Refresh();
        IntPtr hwnd = _mainWindowHandle != IntPtr.Zero
            ? _mainWindowHandle
            : _revit.MainWindowHandle;
        if (hwnd != IntPtr.Zero)
        {
            _mainWindowHandle = hwnd;
            ShowWindow(hwnd, SW_SHOW);
            _hidden = false;
        }
    }

    public bool IsHidden => _hidden;

    public void Dispose()
    {
        // Revit only persists an "Always Load" answer after a normal shutdown. Ask the empty broker
        // session to close normally and never escalate to Process.Kill: if a dialog blocks the
        // close, show the broker and leave it available for the user to inspect.
        try
        {
            if (_revit is { HasExited: false })
            {
                _revit.Refresh();
                IntPtr hwnd = _mainWindowHandle != IntPtr.Zero
                    ? _mainWindowHandle
                    : _revit.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_SHOW);
                    _hidden = false;
                    ControllerLog.Write($"requesting clean shutdown of broker pid {_revit.Id}");
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    if (!_revit.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds))
                    {
                        ControllerLog.Write($"broker pid {_revit.Id} did not close in 30s; left visible and running for inspection");
                    }
                    else
                    {
                        ControllerLog.Write($"broker pid {_revit.Id} closed normally");
                    }
                }
                else
                {
                    ControllerLog.Write($"broker pid {_revit.Id} has no main window; left running because forced close is disabled");
                }
            }
        }
        catch { /* best effort */ }
        _revit?.Dispose();
        _revit = null;
        _mainWindowHandle = IntPtr.Zero;
    }
}
