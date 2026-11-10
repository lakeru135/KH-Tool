using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KhBatchRunner.Schedule;

public static partial class TaskSchedulerGateway
{
    /// <summary>
    /// Runs the launcher's own self-test against a harmless target. Returns null when the chain
    /// works, or the sentence to show the user.
    ///
    /// This is the check whose absence cost a release: every earlier launcher generation was
    /// registered on faith and only discovered to be unrunnable when a task fired on a PC nobody
    /// was watching. A launch that cannot work should fail at Save, naming itself.
    /// </summary>
    internal static string? VerifyLaunchChain()
    {
        // Field override. The preflight is a guess about the future: it says a launch WOULD fail.
        // If that guess is wrong on some PC — a probe blocked while the real launch is not, say —
        // 1.3.0 would have registered the task and this refuses to, which is strictly worse for the
        // person in front of it. One environment variable restores the old behaviour without a
        // rebuild, in the same style as KHBATCH_MAX_INSTANCES.
        if (IsTruthy(Environment.GetEnvironmentVariable("KHBATCH_SKIP_LAUNCH_SELFTEST")))
            return null;

        // SUCCESS is remembered, failure is not. Startup migration can re-register many jobs at
        // once and spawning a probe for each would put a visible pause into Revit's startup — but
        // a user who has just been told an antivirus rule blocked the launcher will go and fix it,
        // and should not have to restart Revit before the next Save can notice.
        if (_launchChainVerified) return null;
        string? verdict = VerifyLaunchChainCore();
        if (verdict == null) _launchChainVerified = true;
        return verdict;
    }

    private static bool _launchChainVerified;

    /// <summary>
    /// Tries each candidate copy in turn and keeps the first one this PC actually executes. The
    /// question is not "does a launcher exist" but "will Windows let THIS launcher run", and on a
    /// policy-managed desktop those have different answers per folder: the same executable is
    /// refused from %ProgramData% and permitted from %ProgramFiles% under the default SRP/AppLocker
    /// rules. Testing every copy turns that from an unfixable error into a working schedule.
    /// </summary>
    private static string? VerifyLaunchChainCore()
    {
        string probe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");

        var present = new List<string>();
        var refusals = new List<string>();

        foreach (string candidate in LauncherCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            present.Add(candidate);

            // No probe to run (a stripped Windows image): don't invent a failure, and don't keep
            // hunting either — this copy is as good as any.
            if (!File.Exists(probe)) { ResolvedLauncher = candidate; return null; }

            string? failure = SelfTestFailure(candidate, probe);
            if (failure == null)
            {
                ResolvedLauncher = candidate;
                return null;
            }
            refusals.Add($"  {candidate}\n      {failure}");
        }

        if (present.Count == 0)
            return "The scheduled-run launcher (" + LauncherExeName + ") was not found. Looked in:\n" +
                   string.Join("\n", LauncherCandidates()
                       .Select(p => Path.GetDirectoryName(p) ?? p)
                       .Distinct(StringComparer.OrdinalIgnoreCase)) +
                   "\n\nReinstall the add-in — a schedule cannot be registered without it.";

        return LaunchBlockedMessage(refusals);
    }

    /// <summary>Null when the copy ran the probe successfully, else why it did not.</summary>
    private static string? SelfTestFailure(string launcher, string probe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = launcher,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            SetArguments(psi, new List<string>
            {
                "--self-test", probe, "--log", LauncherLogPath("selftest")
            });

            using Process? p = Process.Start(psi);
            if (p == null) return "the launcher process could not be started";
            if (!p.WaitForExit(30_000)) return "the launcher did not finish within 30 seconds";
            return p.ExitCode == 0 ? null : $"the launcher returned {p.ExitCode} instead of 0";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value!.Trim().Equals("0", StringComparison.Ordinal) &&
        !value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What to tell someone whose PC refused every launcher copy.
    ///
    /// <para><b>Why it no longer names one culprit.</b> Until 1.5.1.3 this message asserted a
    /// Software Restriction Policy or AppLocker rule and prescribed the Program Files copy, because
    /// that is what the 2026-08-07 managed PC turned out to have. A second managed PC
    /// (2026-09-18) refused the launcher with AppLocker reporting <c>AllowedByDefault</c>, SRP
    /// unconfigured and the file's own ACL granting Full Control: the block came from Microsoft
    /// Defender's attack-surface-reduction rule <c>01443614-…</c>, "block executable files from
    /// running unless they meet a prevalence, age, or trusted list criterion", which refuses a
    /// freshly built in-house binary <b>wherever it sits</b>. Advice to move it to Program Files
    /// would have been confidently wrong, and the one useful fact — the Defender log entry naming
    /// the rule — was not mentioned at all.</para>
    ///
    /// <para>So the message now reports what was observed, lists the causes that produce it, and
    /// says where each one records itself. The reader is someone about to talk to IT: the rule id
    /// and the event log are what make that conversation short.</para>
    /// </summary>
    private static string LaunchBlockedMessage(IReadOnlyList<string> refusals) =>
        "This PC will not let the scheduled-run launcher start a program, so the schedule has NOT " +
        "been registered — it would have failed when it fired.\n\n" +
        "Every installed copy was tried:\n" + string.Join("\n", refusals) + "\n\n" +
        "This is a machine-security setting refusing an unsigned in-house program, not a fault in " +
        "the add-in. Three things cause it, and each leaves a record:\n\n" +
        "  •  Microsoft Defender attack surface reduction — the rule \"block executable files from " +
        "running unless they meet a prevalence, age, or trusted list criterion\" " +
        "(01443614-CD74-433A-B99E-2ECDC07BFC25) blocks a newly built program in EVERY folder, so " +
        "moving or reinstalling it changes nothing. Look in Event Viewer under " +
        "Applications and Services Logs → Microsoft → Windows → Windows Defender → Operational " +
        "for event 1121 naming " + LauncherExeName + ". IT can add an ASR exclusion for it.\n" +
        "  •  A Software Restriction Policy or AppLocker rule — the default rule sets permit " +
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + " and refuse " +
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + " and your " +
        "user profile. Installing with setup (which puts a copy in Program Files) usually settles " +
        "this one; AppLocker logs it under Microsoft → Windows → AppLocker → EXE and DLL.\n" +
        "  •  Antivirus quarantine or a file permission — the copy itself cannot be read or run.\n\n" +
        "Either way, ask IT to allow " + LauncherExeName + " by path or publisher. Schedules that " +
        "already exist keep running; only saving a new one is blocked.\n\n" +
        "Launcher log (empty when the program never started at all): " + LauncherLogPath("selftest");

    /// <summary>
    /// Records which registration generation this job's task belongs to. The launcher used to be a
    /// generated per-job script that could carry its version as a comment; the executable is shared
    /// by every job, so the per-job marker moved into a file of its own.
    /// </summary>
    /// <param name="shellFallback">True when this task starts Revit through cmd.exe because the
    /// launcher was refused. Recorded because the two shapes fail differently — a shell task logs
    /// one line and no exit code of its own — and a diagnostic report that could not tell them
    /// apart would send its reader hunting for a launcher log that was never going to exist.</param>
    private static void WriteRegistrationMarker(string path, string revitExePath, TaskActionKind action)
    {
        var text = new StringBuilder();
        text.AppendLine(RegistrationMarker);
        text.AppendLine(PreviousRegistrationMarker + " compatibility marker - prevents an older sibling edition from downgrading this task");
        text.AppendLine(OlderRegistrationMarker + " compatibility marker - prevents an older sibling edition from downgrading this task");
        text.AppendLine(OldestRegistrationMarker + " compatibility marker - prevents an older sibling edition from downgrading this task");
        text.AppendLine("registered=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        text.AppendLine("action=" + ActionName(action));
        text.AppendLine("launcher=" + action switch
        {
            TaskActionKind.RevitDirect => revitExePath,
            TaskActionKind.Shell => ShellExePath(),
            _ => LauncherExePath()
        });
        text.AppendLine("revit=" + revitExePath);
        if (action == TaskActionKind.RevitDirect)
            text.AppendLine("firedir=" + FireDirectory(JobIdFromMarkerPath(path)));
        File.WriteAllText(path, text.ToString());
    }

    /// <summary>What the marker calls each action, and what a diagnostic report reads back.</summary>
    internal static string ActionName(TaskActionKind action) => action switch
    {
        TaskActionKind.RevitDirect => "revit-direct",
        TaskActionKind.Shell => "cmd-shell",
        _ => "launcher"
    };

    private static string JobIdFromMarkerPath(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("launcher_", StringComparison.OrdinalIgnoreCase)
            ? name.Substring("launcher_".Length)
            : name;
    }

    /// <summary>
    /// The task action's argument string. Built separately from the XML so its shape can be
    /// regression-tested without registering or starting a real task — which is how the launcher
    /// generations before it were verified, and the only part of that machinery worth keeping.
    /// </summary>
    /// <summary>
    /// Where the per-job "fire" folders live: one empty folder per registered Revit-direct task.
    /// Under the schedule folder rather than %TEMP% for the reason every other schedule artefact is
    /// — a cleaner that empties %TEMP% overnight would silently un-name every schedule on the PC.
    /// </summary>
    internal static string FireRootDirectory() =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "fire");

    /// <summary>
    /// The working directory a Revit-direct task hands to Revit, which is how the run says which job
    /// fired.
    ///
    /// <para>A folder, not a file, because a working directory is the only channel Task Scheduler
    /// offers an <c>Exec</c> action besides its command line — and Revit's command line belongs to
    /// Revit, which treats a stray argument as a file to open. The folder is empty and stays
    /// empty: its NAME is the entire message.</para>
    /// </summary>
    internal static string FireDirectory(string jobId) =>
        Path.Combine(FireRootDirectory(), jobId);

    /// <summary>The job a fire folder names, or "" for any other folder. The exact inverse of
    /// <see cref="FireDirectory"/>, and the add-in side of the channel — see LaunchIdentity.</summary>
    internal static string JobIdFromFireDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        try
        {
            string full = Path.GetFullPath(directory!).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetDirectoryName(full) ?? "";
            string root = Path.GetFullPath(FireRootDirectory()).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(full)
                : "";
        }
        catch
        {
            return "";   // an unreadable path is simply not a fire folder
        }
    }

    private static void TryDeleteFireDirectory(string jobId)
    {
        try
        {
            string dir = FireDirectory(jobId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* an orphaned empty folder is harmless; a failed delete must not fail the delete */ }
    }

    /// <summary>
    /// Whether a Revit-direct task can be registered for this job: null when it can, a reason
    /// otherwise.
    ///
    /// <para>Unlike the launcher and the shell there is nothing to self-test — the thing it starts
    /// is the Revit this add-in is running inside, which by definition runs here. What CAN fail is
    /// the naming channel, so that is what is proved: the executable is where we think it is, and
    /// the per-job folder can be created. Both are checked now rather than at 06:00.</para>
    /// </summary>
    private static string? RevitDirectProblem(ScheduledJob job, string revitExePath)
    {
        if (string.IsNullOrWhiteSpace(revitExePath))
            return "this Revit did not report its own program path";
        if (!File.Exists(revitExePath))
            return "Revit was not found at " + revitExePath;
        try
        {
            Directory.CreateDirectory(FireDirectory(job.Id));
            return null;
        }
        catch (Exception ex)
        {
            return "the per-job launch folder could not be created (" + ex.Message + ")";
        }
    }

    /// <summary>What to tell someone whose schedule was registered to start Revit directly.</summary>
    private static string RevitDirectNote(string launchProblem) =>
        "\n\nNOTE: this PC refused to run " + LauncherExeName + ", so the task starts Revit " +
        "itself instead. The schedule WILL run, and nothing appears on screen when it does — no " +
        "console window. Revit works out which job fired from the folder the task hands it, so " +
        "that folder must not be deleted by hand; deleting the schedule removes it.\n\n" +
        "What this costs: the launcher's own log and its 0xFA1/0xFA2 failure codes. The run's " +
        "trail is surface.log and the run's tracking log instead.\n\n" +
        "That refusal, in full — worth giving to IT, because the launcher is still the better path " +
        "and this add-in goes back to it the moment it is allowed to:\n" +
        launchProblem;

    /// <summary>Windows' own command shell — the fallback task action. Signed by Microsoft and
    /// present on every Windows install, which is exactly the property the launcher lacks.</summary>
    internal static string ShellExePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>
    /// The fallback task action's arguments: set the job environment variables, note the launch in
    /// the job's log, and run Revit — the same three things <c>KhBatchLauncher.exe</c> does.
    ///
    /// <para><b>Why this exists again.</b> A shell action is what XML-v2 used and what 1.3.4
    /// abandoned, because a console window flashes on every run. It comes back as a FALLBACK, not a
    /// replacement, because a PC can refuse to run our launcher for a reason no amount of
    /// installing fixes: measured 2026-09-18, Microsoft Defender's attack-surface-reduction rule
    /// "block executable files from running unless they meet a prevalence, age, or trusted list
    /// criterion" refuses a freshly built in-house binary in every folder — %AppData%, Program
    /// Files, the build output, even %TEMP%. Against that, a visible console window is a cosmetic
    /// complaint about a schedule that runs, versus no schedule at all.</para>
    ///
    /// <para><b>cmd waits.</b> Revit is invoked directly rather than through <c>start</c>, so the
    /// shell does not exit until Revit does — the same synchronous shape the launcher has, which is
    /// what makes Task Scheduler's "Last Run Result" mean the run's outcome and
    /// <c>IgnoreNew</c> able to prevent an overlap.</para>
    ///
    /// <para><b>Every <c>set</c> is quoted</b> (<c>set "VAR=value"</c>). Unquoted, the space before
    /// the next <c>&amp;</c> lands INSIDE the value, and a job id with a trailing space matches no
    /// saved job — a failure that would look exactly like a missing job file.</para>
    /// </summary>
    internal static string ShellArguments(ScheduledJob job, string revitExePath)
    {
        string log = LauncherLogPath(job.Id);
        var sb = new StringBuilder();
        sb.Append("/c ");
        // %DATE%/%TIME% are expanded by cmd when the task fires, so the line carries the launch
        // time. The log is what tells a later reader whether the action ran at all.
        sb.Append("echo %DATE% %TIME% launch job=").Append(job.Id)
          .Append(" owner=").Append(EffectiveOwner(job))
          .Append(" shell=cmd revit=\"").Append(revitExePath).Append('"')
          .Append(">>\"").Append(log).Append('"');
        sb.Append("& set \"").Append(ScheduleEnvironment.Job).Append('=').Append(job.Id).Append('"');
        sb.Append("& set \"").Append(ScheduleEnvironment.Owner).Append('=').Append(EffectiveOwner(job)).Append('"');
        sb.Append("& \"").Append(revitExePath).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Whether the shell fallback can be registered: null when <c>cmd.exe</c> both exists and runs
    /// here, a reason otherwise.
    ///
    /// <para>Proved by running it, not by <c>File.Exists</c>, for the reason the launcher preflight
    /// exists at all — a PC that refuses one executable may refuse another, and finding that out
    /// when the task fires unattended is what this whole mechanism is built to avoid.</para>
    /// </summary>
    private static string? ShellSelfTestFailure()
    {
        string shell = ShellExePath();
        if (!File.Exists(shell)) return "cmd.exe was not found at " + shell;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            SetArguments(psi, new List<string> { "/c", "exit", "0" });
            using Process? p = Process.Start(psi);
            if (p == null) return "cmd.exe could not be started";
            if (!p.WaitForExit(30_000)) return "cmd.exe did not finish within 30 seconds";
            return p.ExitCode == 0 ? null : $"cmd.exe returned {p.ExitCode} instead of 0";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>What to tell someone whose schedule was registered through the shell fallback.</summary>
    private static string ShellFallbackNote(string launchProblem) =>
        "\n\nNOTE: this PC refused to run " + LauncherExeName + ", so the task starts Revit " +
        "through cmd.exe instead. The schedule WILL run; a console window flashes when it does, " +
        "and the launcher's own diagnostics are replaced by a launch line in " +
        LauncherLogPath("<job id>") + ".\n\n" +
        "That refusal, in full — worth giving to IT, because the launcher is the better path:\n" +
        launchProblem;

    internal static string LauncherArguments(ScheduledJob job, string revitExePath)
    {
        var sb = new StringBuilder();
        AppendArgument(sb, "--job");
        AppendArgument(sb, job.Id);
        AppendArgument(sb, "--owner");
        AppendArgument(sb, EffectiveOwner(job));
        AppendArgument(sb, "--revit");
        AppendArgument(sb, revitExePath);
        AppendArgument(sb, "--log");
        AppendArgument(sb, LauncherLogPath(job.Id));
        return sb.ToString();
    }

    private static string EffectiveOwner(ScheduledJob job) =>
        job.ScheduleOwner?.Trim() ?? "";
}
