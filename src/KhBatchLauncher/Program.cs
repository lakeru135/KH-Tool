using System.Diagnostics;
using System.Globalization;
using System.Text;

using KhBatchRunner.Schedule;

namespace KhBatchLauncher;

/// <summary>
/// Starts Revit for one scheduled batch job, waits for it, and returns its exit code to Task
/// Scheduler. See the .csproj for why this exists as a real executable instead of a VBScript.
///
/// The whole contract is: set <see cref="ScheduleEnvironment.Job"/> and
/// <see cref="ScheduleEnvironment.Owner"/> on the child, start Revit, wait. Waiting is not
/// incidental — the task action must stay alive for the entire run or Task Scheduler's
/// <c>IgnoreNew</c> multiple-instances policy cannot stop an overlapping trigger from starting a
/// second Revit on the same job.
///
/// This process NEVER terminates Revit. It waits, records, and exits. A run that hangs is left
/// open for inspection, which is the same rule the add-in and the release gate enforce.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Our own failures use codes far away from the small integers a child process realistically
    /// returns, so Task Scheduler's "Last Run Result" alone tells you which half of the chain
    /// broke: 0xFA1/0xFA2 is the launcher, anything small is Revit's own exit code.
    /// </summary>
    internal const int ExitBadArguments = 4001;   // 0xFA1
    internal const int ExitCouldNotStart = 4002;  // 0xFA2

    /// <summary>
    /// Protocol-handler mode: <c>KhBatchLauncher.exe --open-log "khbatchlog:&lt;encoded path&gt;"</c>,
    /// registered by the installers as the handler for the <c>khbatchlog:</c> scheme.
    ///
    /// It exists because a Notification Center toast CANNOT open a file directly. A toast whose
    /// activation target is a <c>file:</c> URI is accepted, shown, and then silently dropped from
    /// Notification Center by Windows — measured, not assumed: of three otherwise identical toasts,
    /// the <c>file:</c> one vanished within six seconds while an <c>https:</c> one and a plain one
    /// stayed. A registered custom scheme is retained, so the log link goes through here.
    ///
    /// Being reachable from any web page, this is an attack surface, and it is deliberately a
    /// narrow one: it opens an EXISTING file with one of a few text/report extensions, through the
    /// shell's normal handler for that type. It will not start a program, and it is not a general
    /// "run this path" entry point.
    /// </summary>
    private static readonly string[] OpenableExtensions = { ".csv", ".txt", ".log", ".xlsx", ".html" };

    internal static int OpenLog(string? argument)
    {
        try
        {
            string? path = ResolveOpenable(argument);
            if (path == null) return ExitBadArguments;

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return 0;
        }
        catch
        {
            return ExitCouldNotStart;
        }
    }

    /// <summary>Whether this path would be opened, without opening it — so the tray notification can
    /// decide whether to offer an "Open log" action at all rather than offering one that does
    /// nothing.</summary>
    internal static bool CanOpenLog(string? argument) => ResolveOpenable(argument) != null;

    /// <summary>
    /// <c>--toast-sweep</c>: remove the toasts whose retention window has passed, and nothing else.
    ///
    /// Always exits 0, including when nothing could be removed. The caller is Revit's startup, which
    /// launches this fire-and-forget inside a band that must never block or fail — a non-zero code
    /// here would only invite someone to start treating tidying as an error. What actually happened
    /// goes to stdout for the caller to record.
    /// </summary>
    private static int ToastSweep()
    {
        try
        {
            string summary = ToastHistory.SweepExpired();
            Console.Out.Write(summary.Length > 0 ? summary : "toast sweep: nothing due");
        }
        catch (Exception ex)
        {
            Console.Error.Write(ex.ToString());
        }
        return 0;
    }

    /// <summary>The single gate both callers go through: an EXISTING file with one of a few
    /// text/report extensions. Null means "not something this program will open".</summary>
    private static string? ResolveOpenable(string? argument)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(argument)) return null;

            string value = argument!.Trim();
            // khbatchlog: since 2.0.0. The 1.x scheme (legacy-names allow-list) still arrives from
            // links in CSVs and notifications written before the upgrade; the installer registers
            // it as an alias for this same handler.
            foreach (string scheme in new[] { "khbatchlog:", "accbatchlog:" })
                if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(scheme.Length);
                    break;
                }
            value = Uri.UnescapeDataString(value).Trim('/', '"');

            string path = Path.GetFullPath(value);
            string extension = Path.GetExtension(path);
            bool allowed = false;
            foreach (string candidate in OpenableExtensions)
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase)) allowed = true;

            return allowed && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Handled before anything else: these modes share only the executable with the launcher's
        // real job, and must never touch Revit, a schedule, or a log file of its own.
        string? openLog = Value(args, "--open-log");
        if (openLog != null) return OpenLog(openLog);

        // The real Notification Center toast, called straight into Windows with no PowerShell in
        // the path — the mechanism a ConstrainedLanguage PC cannot refuse. Checked before --notify
        // because it is the preferred one; --notify draws the fallback card.
        // --toast-sweep is checked BEFORE --toast: Has() compares whole arguments so the parser does
        // not care, but reading it the other way round invites the wrong assumption later.
        // Removes toasts whose retention window has passed and does nothing else — no Revit, no
        // schedule, no toast raised. Revit runs this at startup to catch the last notification of a
        // run that finished overnight and raised nothing afterwards; --toast sweeps for itself.
        if (Has(args, "--toast-sweep")) return ToastSweep();

        if (Has(args, "--toast")) return ToastWinRt.Run();

        // --notify-demo is checked first: it contains "--notify" as a prefix only by coincidence,
        // but Has() compares whole arguments, so the order is for the reader rather than the parser.
        if (Has(args, "--notify-demo"))
            return NotifyHost.Demo(Has(args, "--failed"), Value(args, "--save"));
        if (Has(args, "--notify")) return NotifyHost.Run();

        string? job = Value(args, "--job");
        string? owner = Value(args, "--owner");
        string? target = Value(args, "--revit");
        string? logPath = Value(args, "--log");
        string? selfTest = Value(args, "--self-test");

        // --self-test runs the identical path against a harmless target. TaskSchedulerGateway calls
        // it before registering a task, so a machine that cannot launch anything says so when the
        // schedule is SAVED rather than silently at 2am. It is also what the loop test drives.
        if (selfTest != null)
        {
            target = selfTest;
            job ??= "self-test";
            owner ??= "";
            logPath ??= Path.Combine(Path.GetTempPath(), "khbatch-launcher-selftest.log");
        }

        // net48's reference assemblies do not annotate IsNullOrWhiteSpace, so the guard alone does
        // not convince the nullable analysis — hence the two locals rather than a suppression.
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(job))
        {
            Log(logPath, $"bad arguments: --job and --revit are both required (got job='{job}' revit='{target}')");
            return ExitBadArguments;
        }
        string jobId = job!;
        string exe = target!;

        // Logged BEFORE anything that can fail, so "no log file" has exactly one meaning left:
        // Task Scheduler never started this process at all. Under the old VBScript launcher an
        // empty log was ambiguous — it was also what a disabled script host looked like.
        Log(logPath, "launch " + Facts(jobId, owner, exe));

        if (!File.Exists(exe))
        {
            Log(logPath, $"could not start: '{exe}' does not exist on this PC");
            return ExitCouldNotStart;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            psi.EnvironmentVariables[ScheduleEnvironment.Job] = jobId;
            psi.EnvironmentVariables[ScheduleEnvironment.Owner] = owner ?? "";

            using Process? child = Process.Start(psi);
            if (child == null)
            {
                Log(logPath, "could not start: the process handle came back null");
                return ExitCouldNotStart;
            }

            Log(logPath, $"started pid={child.Id.ToString(CultureInfo.InvariantCulture)}");
            child.WaitForExit();
            int code = child.ExitCode;
            Log(logPath, $"exited pid={child.Id.ToString(CultureInfo.InvariantCulture)} " +
                         $"exitCode={code.ToString(CultureInfo.InvariantCulture)} " +
                         "(Task Scheduler shows this as the task's Last Run Result)");
            return code;
        }
        catch (Exception ex)
        {
            Log(logPath, $"could not start '{exe}': {ex.GetType().Name}: {ex.Message}");
            return ExitCouldNotStart;
        }
    }

    /// <summary>Everything needed to compare a working PC with a failing one, on one line.</summary>
    private static string Facts(string job, string? owner, string target)
    {
        var sb = new StringBuilder();
        sb.Append("local=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append(" utc=").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('Z');
        try { sb.Append(" tz=").Append(TimeZoneInfo.Local.Id); } catch { sb.Append(" tz=unavailable"); }
        try { sb.Append(" user=").Append(Environment.UserDomainName).Append('\\').Append(Environment.UserName); }
        catch { sb.Append(" user=unavailable"); }
        sb.Append(" job=").Append(job);
        sb.Append(" owner=").Append(string.IsNullOrEmpty(owner) ? "(blank)" : owner);
        sb.Append(" launcher=").Append(ExecutablePath());
        sb.Append(" target=").Append(target);
        return sb.ToString();
    }

    private static string ExecutablePath()
    {
        try { return Process.GetCurrentProcess().MainModule?.FileName ?? "unknown"; }
        catch { return "unknown"; }
    }

    /// <summary>
    /// Best-effort append. Logging must never be the reason a scheduled run fails, so every failure
    /// here is swallowed — but the log is the only witness this process has, so it is attempted for
    /// every state change rather than buffered to the end.
    /// </summary>
    private static void Log(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            File.AppendAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                " " + message + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* a scheduled run is worth more than its log */ }
    }

    private static string? Value(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    /// <summary>A flag with no value of its own; its content arrives in environment variables.</summary>
    private static bool Has(string[] args, string name)
    {
        foreach (string a in args)
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
