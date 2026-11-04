#if !EDITION_DRW
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Mcp;

/// <summary>
/// KH-Tool's AI Bridge: serves <see cref="McpContract.Tools"/> to KhMcp.exe over a named pipe and
/// runs each call on the Revit API thread through an <see cref="ExternalEvent"/>.
///
/// <para>KH-Tool only (Full / FullUnlimited). DRW Helper's classes are compiled into KH-Tool too,
/// and KH-only tools are compiled out of DRW, so this is the one host that can offer both — and
/// with only one edition ever starting a bridge, two loaded add-ins can never fight over the pipe.</para>
///
/// <para>Off until someone turns it on (Setting → AI Bridge), and only for this Revit session.
/// Same pipe pattern as <see cref="Auth.TokenBrokerServer"/>: current-user ACL, one line in, one
/// line out. Unlike the broker it uses an ExternalEvent rather than Idling, because this Revit
/// is a person's interactive session and Idling only ticks while they move the mouse.</para>
/// </summary>
internal static class McpBridge
{
    /// <summary>Long: an Enlarge over many rooms legitimately takes minutes.</summary>
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromMinutes(15);

    private sealed class Request
    {
        public string Json = "";
        public string Reply = "";
        /// <summary>The caller gave up waiting; the API thread must not run it late.</summary>
        public volatile bool Abandoned;
        public readonly ManualResetEventSlim Done = new(false);
    }

    private sealed class Handler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            while (true)
            {
                Request? req;
                lock (Gate) { req = Pending.Count > 0 ? Pending.Dequeue() : null; }
                if (req == null) return;
                if (req.Abandoned) continue;
                try { req.Reply = McpTools.Invoke(app, req.Json); }
                catch (Exception ex) { req.Reply = McpTools.ErrorReply(ex); }
                finally { req.Done.Set(); }
            }
        }

        public string GetName() => "KH-Tool AI Bridge";
    }

    private static readonly object Gate = new();
    private static readonly Queue<Request> Pending = new();
    private static ExternalEvent? _event;
    private static CancellationTokenSource? _stop;
    private static volatile string _state = "Off";
    private static int _calls;

    public static bool IsRunning => _stop != null;

    /// <summary>One line for the toggle's dialog.</summary>
    public static string State => _state + (_calls > 0 ? $" — {_calls} call(s) served this session" : "");

    /// <summary>The ribbon button, so the toggle can say whether it is on. Set by <see cref="App"/>.</summary>
    public static PushButton? Button { get; set; }

    private const string OnText = "AI Bridge\nON";
    private const string OffText = "AI Bridge\nOFF";

    public static string CaptionFor(bool on) => on ? OnText : OffText;

    /// <summary>
    /// The KhMcp.exe Claude Code should start: the setup.exe's copy in Program Files (a managed PC's
    /// policy permits it), else the one beside this add-in (no-admin install, dev deploy).
    /// </summary>
    public static string ServerPath()
    {
        string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Kanghun BIM", "KhMcp", "KhMcp.exe");
        if (File.Exists(pf)) return pf;
        return Path.Combine(Path.GetDirectoryName(typeof(McpBridge).Assembly.Location) ?? "", "KhMcp", "KhMcp.exe");
    }

    /// <summary>The one-time line that registers the bridge with Claude Code.</summary>
    public static string SetupLine() => $"claude mcp add --scope user kh-tool -- \"{ServerPath()}\"";

    /// <summary>
    /// Turns the bridge on or off, like Sync View. Returns null on success, or why it could not
    /// start. Must be called in an API context (a command): ExternalEvent.Create needs one.
    /// </summary>
    public static string? Toggle()
    {
        if (IsRunning) Stop();
        else if (Start() is string why) return why;
        if (Button != null) Button.ItemText = CaptionFor(IsRunning);
        return null;
    }

    /// <summary>
    /// Test-rig hook: a Revit started with KHMCP_AUTOSTART=1 turns the bridge on at startup, so a
    /// headless/automated test needs no click. Without it the bridge stays off at every start.
    /// Called from OnStartup, which is an API context (ExternalEvent.Create needs one).
    /// </summary>
    public static void AutoStartIfAsked()
    {
        if (Environment.GetEnvironmentVariable("KHMCP_AUTOSTART") != "1" || IsRunning) return;
        if (Start() == null && Button != null) Button.ItemText = CaptionFor(true);
    }

    private static string? Start()
    {
        if (IsRunning) return null;
        // The first pipe instance is made here, on the API thread, so a refusal is known before the
        // button says ON: another Revit of this user already serving the pipe means one bridge per user.
        NamedPipeServerStream first;
        try { first = CreateServer(McpContract.PipeName()); }
        catch (IOException)
        {
            return "Another Revit window already has the AI Bridge on. Turn it off there first.";
        }
        _event ??= ExternalEvent.Create(new Handler());
        _stop = new CancellationTokenSource();
        _state = "On — waiting for the AI";
        CancellationToken token = _stop.Token;
        new Thread(() => PipeLoop(first, token)) { IsBackground = true, Name = "KhMcpBridge" }.Start();
        return null;
    }

    public static void Stop()
    {
        CancellationTokenSource? stop = _stop;
        _stop = null;
        stop?.Cancel();
        _state = "Off";
    }

    private static void PipeLoop(NamedPipeServerStream? first, CancellationToken token)
    {
        string pipeName = McpContract.PipeName();
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            if (first != null)
            {
                server = first;
                first = null;
            }
            else
            {
                try
                {
                    server = CreateServer(pipeName);
                }
                catch (IOException)
                {
                    // Lost the name between calls (another Revit grabbed it): stand down.
                    _state = "Stopped — another Revit window took over the AI Bridge.";
                    _stop = null;
                    return;
                }
            }

            _state = "On — waiting for the AI";
            try
            {
                using (server)
                {
                    server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                    var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                    var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                    string line = reader.ReadLine() ?? "";
                    if (line.Trim().Length == 0) continue;
                    _state = "On — running a call";
                    writer.WriteLine(Dispatch(line));
                    Interlocked.Increment(ref _calls);
                    try { server.WaitForPipeDrain(); } catch { /* client already gone */ }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                // A client that hung up mid-call is routine; keep serving.
                Thread.Sleep(200);
            }
        }
    }

    private static string Dispatch(string json)
    {
        var req = new Request { Json = json };
        lock (Gate) { Pending.Enqueue(req); }
        _event?.Raise();
        if (req.Done.Wait(ServiceTimeout)) return req.Reply;
        req.Abandoned = true;
        return new JsonObject
        {
            ["ok"] = false,
            ["error"] = "Timed out waiting for Revit. Is a dialog open, or is someone mid-command? " +
                        "Finish or cancel it in Revit, then try again."
        }.ToJsonString();
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        var security = new PipeSecurity();
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the current user SID.");
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
#if NETFRAMEWORK
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#endif
    }
}
#endif
