using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace KhBatchRunner.Auth;

/// <summary>
/// Serves the signed-in Revit session's Forma token to a SEPARATE process over a named pipe, so a
/// standalone controller (<c>KhBatchController.exe</c>) can call the Forma REST APIs without being
/// a Revit add-in itself.
///
/// WHY THIS EXISTS. <see cref="SessionTokenProvider"/> can only work inside Revit:
/// <c>SSONET.dll</c> references <c>RevitAPI</c>/<c>RevitAPIFoundation</c>/<c>UtilityAPI</c>, so it
/// cannot be loaded by a plain .exe, and Autodesk persists no OAuth token on disk to read instead
/// (<c>LoginState.xml</c> holds only settings; <c>WebServicesCache.xml</c> only UserInfo +
/// entitlements). A registered APS app with a real 3-legged flow is the clean long-term answer;
/// until that exists, the only way off-Revit code can authenticate as the signed-in user is to ask
/// a Revit that IS signed in. This class is that Revit.
///
/// Activated only when Revit is launched with <c>KHBATCH_TOKEN_BROKER=&lt;pipeName&gt;</c>, armed
/// from <c>OnStartup</c> exactly like <see cref="Diagnostics.SelfTest"/> and
/// <see cref="Schedule.ScheduledRunner"/>. A broker instance opens NO document, so none of the
/// cloud-open failure modes that plague the batch paths apply here.
///
/// THREADING. The pipe is served on a background thread, but the token is NOT fetched there:
/// requests are queued and serviced from a persistent <c>Idling</c> handler on the Revit API
/// thread. SSONET's thread affinity is undocumented, and marshalling onto the API thread via
/// Idling is already this codebase's standard answer to exactly that question.
///
/// LIFETIME. The controller passes its own pid as <c>KHBATCH_BROKER_OWNER</c>; a watchdog thread
/// terminates this Revit when that process disappears. Without it a crashed controller would strand
/// an INVISIBLE Revit holding a licence seat, with no window for the user to close.
/// </summary>
public static class TokenBrokerServer
{
    private const string PipeEnvVar = "KHBATCH_TOKEN_BROKER";
    private const string OwnerEnvVar = "KHBATCH_BROKER_OWNER";

    /// <summary>
    /// Where to write breadcrumbs. The controller sets this to its OWN log folder, because Revit
    /// runs with a private per-session temp directory (<c>%TEMP%\&lt;guid&gt;</c>) — so a broker
    /// logging to "%TEMP%" lands somewhere the launcher cannot find, which is exactly how this
    /// system's first real failure stayed invisible. Falls back to this process's temp.
    /// </summary>
    private const string LogDirEnvVar = "KHBATCH_BROKER_LOG";

    /// <summary>Backstop only — applies when no owner pid was supplied. An owned broker lives as
    /// long as its owner, so an idle controller never has to send a heartbeat to stay served.</summary>
    private static readonly TimeSpan OrphanIdleTimeout = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan WatchdogPoll = TimeSpan.FromSeconds(3);

    /// <summary>How long a pipe client waits for the API thread to service its request. Generous:
    /// Idling can be starved briefly while Revit finishes its own startup work.</summary>
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(60);

    private static UIControlledApplication? _app;
    private static EventHandler<IdlingEventArgs>? _handler;
    private static string _pipeName = "";

    private static readonly SessionTokenProvider _tokens = new();
    private static readonly object _gate = new();
    /// <summary>
    /// Queued requests awaiting the Revit API thread. A QUEUE, not a single slot: with one slot a
    /// second request arriving before the Idling tick overwrote the first, and that caller then sat
    /// out the full service timeout for an answer that was never coming.
    /// </summary>
    private static readonly Queue<Request> _pending = new();
    private static volatile string _loginUser = "";
    private static long _lastRequestTicks = DateTime.UtcNow.Ticks;

    /// <summary>One queued pipe request, handed to the API thread and waited on by the pipe thread.</summary>
    private sealed class Request
    {
        public string Verb = "";
        public string Reply = "";
        public readonly ManualResetEventSlim Done = new(false);
    }

    public static bool IsRequested(out string pipeName)
    {
        pipeName = Environment.GetEnvironmentVariable(PipeEnvVar) ?? "";
        return !string.IsNullOrWhiteSpace(pipeName);
    }

    /// <summary>
    /// Subscribes the persistent Idling servicer and starts the pipe + watchdog threads.
    /// Called from <c>OnStartup</c>, which is not itself an Idling event, so <c>Idling +=</c> is legal
    /// here (subscribing from inside an Idling tick throws "Can not subscribe to an event during
    /// execution of that event" — the trap the headless runner already documents).
    /// </summary>
    public static void ArmOnIdling(UIControlledApplication app, string pipeName)
    {
        _app = app;
        _pipeName = pipeName;
        _handler = OnIdling;
        app.Idling += _handler;

        Log($"broker armed on pipe '{pipeName}' (owner pid {Environment.GetEnvironmentVariable(OwnerEnvVar) ?? "none"})");

        var pipeThread = new Thread(PipeLoop) { IsBackground = true, Name = "KhBatchTokenBroker" };
        pipeThread.Start();

        var watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "KhBatchBrokerWatchdog" };
        watchdog.Start();
    }

    // ---- Revit API thread ------------------------------------------------------------------

    /// <summary>
    /// Services at most one queued request per tick. Deliberately does no work when the queue is
    /// empty — this handler stays subscribed for the broker's whole life, so it must stay cheap.
    /// </summary>
    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        // Captured once for the WHOAMI reply; only readable from the API thread.
        if (_loginUser.Length == 0 && sender is UIApplication ui)
        {
            try { _loginUser = ui.Application.LoginUserId ?? ""; } catch { /* non-fatal */ }
        }

        // Drain the whole queue each tick: Idling ticks are the only chance to service anything, so
        // leaving requests queued for later ticks just adds latency to a UI thread already waiting.
        while (true)
        {
            Request? req;
            lock (_gate) { req = _pending.Count > 0 ? _pending.Dequeue() : null; }
            if (req == null) return;
            Service(req);
        }
    }

    private static void Service(Request req)
    {
        try
        {
            req.Reply = req.Verb switch
            {
                "TOKEN" => FetchToken(),
                "WHOAMI" => _loginUser.Length > 0 ? "OK " + _loginUser : "ERR login user not available",
                "ACTIONS" => ListActions(),
                _ => "ERR unknown verb '" + req.Verb + "'"
            };
        }
        catch (Exception ex)
        {
            req.Reply = "ERR " + OneLine((ex.InnerException ?? ex).Message);
        }
        finally
        {
            req.Done.Set();
        }
    }

    /// <summary>
    /// The batch actions this build offers, as <c>name|category|modifies|worksets</c> records
    /// separated by
    /// <c>;</c>. Served from here because <see cref="Actions.ActionRegistry"/> hands back
    /// <see cref="Actions.IBatchAction"/> instances — Revit-typed, so the controller cannot
    /// enumerate them itself. Asking the broker keeps ONE definition of the action list: whatever
    /// this Revit's add-in offers is exactly what the controller shows, with no parallel list to
    /// drift.
    /// </summary>
    private static string ListActions()
    {
        var parts = new List<string>();
        foreach (Actions.IBatchAction action in Actions.ActionRegistry.All())
            parts.Add($"{action.Name}|{action.Category}|{action.ModifiesModel}|{action.Worksets}");
        return "OK " + string.Join(";", parts);
    }

    /// <summary>Re-fetched per request, never cached — the <see cref="ITokenProvider"/> contract, and
    /// the reason a long-lived broker keeps handing out valid tokens as the session refreshes.</summary>
    private static string FetchToken()
    {
        string token = _tokens.GetAccessToken();
        if (string.IsNullOrEmpty(token))
            return "ERR " + OneLine(_tokens.LastError ?? "empty token, no reason reported");
        return "OK " + token;
    }

    // ---- pipe thread -----------------------------------------------------------------------

    /// <summary>
    /// One connection per request, serially. A broker serves a single controller, so there is no
    /// reason to accept concurrent clients — and a single instance keeps the ACL story simple.
    /// </summary>
    private static void PipeLoop()
    {
        while (true)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer(_pipeName);
            }
            catch (IOException ex)
            {
                // The pipe is already served — this Revit has more than one edition of the add-in
                // loaded (the journal reports a duplicate .addin when a dev build in %AppData% sits
                // alongside an installed one in %ProgramData%). Whichever armed first owns the pipe;
                // this instance stands down rather than spinning on a name it can never bind.
                Log("pipe already in use by another add-in instance — standing down: " + OneLine(ex.Message));
                return;
            }

            try
            {
                using (server)
                {
                    server.WaitForConnection();
                    Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);

                    using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

                    string verb = (reader.ReadLine() ?? "").Trim().ToUpperInvariant();
                    if (verb.Length > 0)
                    {
                        // PING is answered here, not on the API thread: it exists to prove the pipe
                        // is up (the controller's handshake before it hides the window), and routing
                        // it through Idling would make the handshake wait on Revit's startup work.
                        writer.WriteLine(verb == "PING" ? "PONG" : Dispatch(verb));

                        // Don't tear the pipe down before the client has taken the reply — closing a
                        // named pipe discards whatever is still in flight.
                        try { server.WaitForPipeDrain(); } catch { /* client may have gone already */ }
                    }
                }
            }
            catch (Exception ex)
            {
                // A broken client connection is routine; log and keep serving.
                Log("pipe error: " + OneLine(ex.Message));
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>Queues a request for the API thread and waits for the answer.</summary>
    private static string Dispatch(string verb)
    {
        var req = new Request { Verb = verb };
        lock (_gate) { _pending.Enqueue(req); }

        if (!req.Done.Wait(ServiceTimeout))
            return "ERR timed out waiting for the Revit API thread (is Revit busy or showing a dialog?)";
        return req.Reply;
    }

    /// <summary>
    /// Pipe restricted to the current user. The payload is a bearer token, so the default DACL —
    /// which is more generous than this — is not good enough.
    /// </summary>
    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        var security = new PipeSecurity();
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the current user SID.");
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

#if NETFRAMEWORK
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
#else
        // .NET (Core) moved the security-carrying constructor out to this factory.
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
#endif
    }

    // ---- watchdog thread -------------------------------------------------------------------

    /// <summary>
    /// Requests a normal close when nobody is left to serve. If shutdown is blocked, the broker is
    /// intentionally left alive rather than force-terminating a Revit process.
    /// </summary>
    private static void WatchdogLoop()
    {
        int ownerPid = 0;
        int.TryParse(Environment.GetEnvironmentVariable(OwnerEnvVar), out ownerPid);

        while (true)
        {
            Thread.Sleep(WatchdogPoll);

            if (ownerPid > 0)
            {
                if (!IsAlive(ownerPid))
                {
                    Log($"owner pid {ownerPid} is gone — requesting normal broker close");
                    Terminate();
                    return;
                }
                // An owned broker never times out: the controller may legitimately sit idle for
                // hours, and forcing it to send heartbeats just to stay alive buys nothing.
                continue;
            }

            var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastRequestTicks));
            if (idle > OrphanIdleTimeout)
            {
                Log($"no owner pid and idle for {idle.TotalMinutes:F0} min — requesting normal broker close");
                Terminate();
                return;
            }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }  // no such process
        catch { return true; }                        // can't tell — never kill on a guess
    }

    private static void Terminate()
    {
        try { if (_app != null && _handler != null) _app.Idling -= _handler; } catch { /* shutting down */ }
        Schedule.RevitShutdown.RequestCurrent("token broker no longer has an owner");
    }

    // ---- diagnostics -----------------------------------------------------------------------

    /// <summary>Replies are one line, so a multi-line exception message would desynchronise the pipe.</summary>
    private static string OneLine(string s) =>
        (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    /// <summary>
    /// Breadcrumbs for a process with no visible window — following the <c>surface.log</c>
    /// precedent, since a hidden broker that fails leaves no other user-visible trail.
    /// </summary>
    private static void Log(string line)
    {
        try
        {
            string dir = Environment.GetEnvironmentVariable(LogDirEnvVar) is { Length: > 0 } d
                ? d
                : Path.Combine(Path.GetTempPath(), "KhBatchRunner");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "broker.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Process.GetCurrentProcess().Id}] {line}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { /* logging must never throw */ }
    }
}
