using System.Diagnostics;
using System.IO;

namespace KhBatchRunner.UI;

/// <summary>
/// Starts <c>KhBatchLauncher.exe</c> on the first copy THIS PC will actually run.
///
/// <para><b>Why this is one place (1.4.0).</b> The search was pasted at three call sites in
/// <see cref="RunNotification"/> — the toast sweep, the direct toast and the tray notification —
/// each with its own copy of the candidate loop, the <c>File.Exists</c> guard, the
/// <see cref="ProcessStartInfo"/> boilerplate and the denial handling. 1.3.9 had to make the same
/// edit three times to change one policy, and 1.3.8.2 records the cost of them NOT being identical:
/// one of the three wrapped its try/catch around the whole loop instead of each candidate, so the
/// first blocked copy ended the search and 91 sweeps in a row silently did nothing on the one
/// machine the sweep was written for.</para>
///
/// <para><b>The policy, now stated once.</b> Try the copy already proven to run here, then
/// <c>%ProgramFiles%</c> before the <c>%ProgramData%</c> Addins copies — because the default
/// Software Restriction Policy and AppLocker rule sets permit the first and deny the second, and on
/// a managed desktop the same executable genuinely gets a different answer per folder. Remember
/// whichever copy starts, so the rest of the process goes straight to it. Report a refusal once per
/// process: it is a standing condition, not an event, and repeating it per call buried the log.</para>
///
/// <para>The ORDER itself lives in <see cref="Schedule.TaskSchedulerGateway.OrderedLauncherPaths"/>,
/// as a pure function of three folder paths, so the harness owns it — the bug it prevents is only
/// visible on a policy-managed PC, which is exactly the machine a test cannot assume.</para>
/// </summary>
internal static class LauncherRunner
{
    /// <summary>
    /// Starts the launcher, trying each installed copy until one runs.
    ///
    /// <para>Callers own everything that happens AFTER the start — how long to wait, whether to read
    /// the output, what an exit code means — because that is the part which genuinely differs
    /// between a sweep, a toast and a tray icon. What they no longer own is finding a copy Windows
    /// will let them execute.</para>
    /// </summary>
    /// <param name="what">Names the caller in the log when a copy is refused, e.g. "toast sweep".</param>
    /// <param name="configure">Sets the arguments and any environment variables. The file name,
    /// working directory and window/redirect defaults are already applied.</param>
    /// <param name="redirectOutput">Whether stdout/stderr are captured. Only set it when the caller
    /// actually reads them — a redirected stream nobody drains can fill its buffer and hang the
    /// child, which for a tray helper means an icon that never appears.</param>
    /// <returns>The started process, or null when no installed copy could be started at all.</returns>
    public static Process? Start(string what, Action<ProcessStartInfo> configure, bool redirectOutput = false)
    {
        foreach (string launcher in Schedule.TaskSchedulerGateway.LauncherCandidates())
        {
            if (!File.Exists(launcher)) continue;

            // A copy this PC has already refused is not tried again in this process. The refusal is
            // a standing condition — an ASR rule, AppLocker, an SRP — not an event, which is why it
            // was already only LOGGED once; what it was not was only ATTEMPTED once, and every
            // attempt after the first bought nothing while raising another Windows Security
            // "blocked" notification in front of the user. If policy changes, the next Revit picks
            // it up: this is a per-process memory, not a persisted verdict.
            lock (Denied)
                if (Denied.Contains(launcher)) continue;

            // GUARDED PER CANDIDATE, never around the loop: a copy this PC refuses must cost the
            // search one candidate, not all of them. See the class comment for what that cost was.
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = launcher,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = redirectOutput,
                    RedirectStandardError = redirectOutput,
                    WorkingDirectory = Path.GetDirectoryName(launcher) ?? Environment.CurrentDirectory
                };
                configure(start);

                Process? started = Process.Start(start);
                if (started == null) continue;

                Schedule.TaskSchedulerGateway.NoteLauncherWorked(launcher);
                return started;
            }
            catch (Exception ex)
            {
                lock (Denied) Denied.Add(launcher);
                RunNotification.MarkLauncherDenied(what, launcher, ex.Message);
            }
        }
        return null;
    }

    /// <summary>Copies this PC refused to start in this process, so none of them is asked twice.
    /// Locked because notifications are delivered from their own background threads and a parallel
    /// run has several of them in flight at once.</summary>
    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase);
}
