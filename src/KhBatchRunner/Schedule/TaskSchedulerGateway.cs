using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Creates/deletes Windows Task Scheduler tasks via the built-in <c>schtasks.exe</c>
/// (no extra dependency). Tasks are registered from XML rather than locale-sensitive command-line
/// date text. Each task runs <c>KhBatchLauncher.exe</c>, which sets the scheduled job and owner
/// environment variables and then waits synchronously for Revit, so Task Scheduler can enforce its
/// single-instance policy until Revit actually exits without flashing a blank terminal window.
///
/// The launcher was a generated <c>.vbs</c> run through <c>wscript.exe</c> up to and including
/// XML-v6. That failed on a second PC in a way the first could not reproduce: where Windows Script
/// Host is disabled by policy — or VBScript, a Feature-on-Demand since Windows 11 24H2, has been
/// removed — <c>wscript.exe //B</c> exits 1 without executing a line, so Revit never started, no
/// launcher log was written, and Task Scheduler reported a bare <c>0x1</c>. XML-v7 removes the
/// scripting host from the chain entirely; see <c>src\KhBatchLauncher</c>.
/// </summary>
/// <summary>
/// What a registered task actually starts. Three, in preference order, and the PC decides — see
/// <c>CreateCore</c>.
/// </summary>
public enum TaskActionKind
{
    /// <summary>KhBatchLauncher.exe: logs before anything can fail, sets the job variables, starts
    /// Revit and waits. The best answer wherever policy allows it to run.</summary>
    Launcher,

    /// <summary>Revit.exe itself, handed a per-job working directory. Nothing is drawn and nothing
    /// unsigned is involved, at the cost of the launcher's own diagnostics.</summary>
    RevitDirect,

    /// <summary>cmd.exe. Works anywhere, and leaves a console window up for the whole run.</summary>
    Shell
}

public static partial class TaskSchedulerGateway
{
    // V9: the action can now be Revit itself. A PC that refuses the launcher used to get a cmd.exe
    // action (V8), which works but leaves a console window on the desktop for the WHOLE run — cmd
    // waits for Revit on purpose, so it is not a flash. V9 starts Revit.exe directly instead and
    // lets the add-in work out which job fired (see LaunchIdentity), which draws nothing and needs
    // no executable this PC has not already trusted. cmd.exe remains the last fallback.
    internal const string RegistrationMarker = "KHBATCH_TASK_XML_V9";
    // Keep the last three markers in every registration. Full and DRW can be upgraded
    // independently; an older sibling must recognise the registration and never downgrade its
    // action/settings. V9 itself still requires its own marker, so V6/V7/V8 are upgraded.
    internal const string PreviousRegistrationMarker = "KHBATCH_TASK_XML_V8";
    internal const string OlderRegistrationMarker = "KHBATCH_TASK_XML_V7";
    internal const string OldestRegistrationMarker = "KHBATCH_TASK_XML_V6";

    /// <summary>The launcher executable, installed beside the add-in assembly by both editions'
    /// installers. Resolved from the running assembly first, so a dev-installed or side-by-side
    /// build uses its own copy; the machine-wide Addins folders are then searched as a fallback.
    ///
    /// The fallback is not decoration. Registration REFUSES to proceed when this file is missing,
    /// and an add-in loaded from a per-user %AppData% deploy (which shadows the machine install by
    /// Revit's manifest-precedence rule) sits in a folder no installer ever writes the launcher to.
    /// Without the fallback that PC could never save a schedule again, while a perfectly good
    /// launcher sat one folder away. Every edition and year ships the identical executable, so any
    /// copy found is the right one.</summary>
    /// <summary>
    /// The copy to register in a scheduled task. Prefers one this PC has been SEEN to execute, then
    /// the first that exists — never a path that merely sorts first, because since 1.3.9 the
    /// preferred order starts with %ProgramFiles%, which an older install may not have.
    /// </summary>
    internal static string LauncherExePath() =>
        ResolvedLauncher
        ?? LauncherCandidates().FirstOrDefault(File.Exists)
        ?? LauncherCandidates().First();

    /// <summary>The copy this PC actually executes: set by the preflight, and (since 1.3.9) by any
    /// caller that successfully starts one — see <see cref="NoteLauncherWorked"/>.</summary>
    private static string? ResolvedLauncher;

    /// <summary>
    /// Remember a copy that started. The preflight only runs when a schedule is registered, so
    /// before 1.3.9 every notification in every Revit re-discovered the working copy from scratch —
    /// and on a policy-managed PC that meant launching the denied copy first, every time.
    /// </summary>
    internal static void NoteLauncherWorked(string launcher)
    {
        if (!string.IsNullOrWhiteSpace(launcher)) ResolvedLauncher = launcher;
    }

    internal const string LauncherExeName = "KhBatchLauncher.exe";

    /// <summary>
    /// Every place a usable launcher may live, best first. The order encodes what a managed Windows
    /// desktop permits, not just where files were put:
    ///
    /// 1. Beside the loaded assembly — a dev or side-by-side build must use its own copy.
    /// 2. <c>%ProgramFiles%\Kanghun BIM</c> — the DEFAULT Software Restriction Policy and AppLocker
    ///    rule sets allow %ProgramFiles% and %WinDir% and deny everywhere else. A locked-down PC
    ///    reported exactly that: "This program is blocked by group policy" for the ProgramData copy,
    ///    which is the classic denied location. This is the copy that survives such a policy.
    /// 3. The per-year machine-wide Addins folders — where every release so far installed it, and
    ///    where an add-in loaded from a per-user %AppData% deploy has to look.
    ///
    /// Every edition and year ships the identical executable, so whichever copy runs is correct.
    /// </summary>
    internal static IEnumerable<string> LauncherCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A copy already proven to run here beats any static ordering.
        string? resolved = ResolvedLauncher;
        if (!string.IsNullOrWhiteSpace(resolved) && seen.Add(resolved!))
            yield return resolved!;

        string? dir = null;
        try { dir = Path.GetDirectoryName(typeof(TaskSchedulerGateway).Assembly.Location); }
        catch { /* fall through to the process directory */ }
        if (string.IsNullOrWhiteSpace(dir))
            dir = AppDomain.CurrentDomain.BaseDirectory;

        foreach (string path in OrderedLauncherPaths(
                     dir!,
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)))
            if (seen.Add(path))
                yield return path;
    }

    /// <summary>
    /// The ordering itself, as a pure function of three folders so the harness owns it (the same
    /// reason <c>ReopenLadder</c> and <c>WorksetVisibilityRule</c> are separate) — nothing here
    /// touches the filesystem or the loaded assembly.
    ///
    /// <para><b>The refinement that matters (1.3.9).</b> "Beside the loaded assembly" was
    /// unconditionally first, for the dev and side-by-side case. But on a normal machine-wide
    /// install that folder IS <c>%ProgramData%\Autodesk\Revit\Addins\&lt;year&gt;</c> — the classic
    /// SRP-denied location — so every Revit, on every start and every notification, launched a
    /// program group policy blocks and logged a paragraph about it. Assembly-adjacent still wins
    /// when it is a genuine side-by-side deploy; when it is just the Addins install,
    /// %ProgramFiles% (which the default SRP/AppLocker rules permit) is tried first and the Addins
    /// copies follow. Nothing is dropped — only reordered.</para>
    /// </summary>
    internal static IEnumerable<string> OrderedLauncherPaths(
        string assemblyDir, string programFiles, string commonAppData)
    {
        string addins = Path.Combine(commonAppData, "Autodesk", "Revit", "Addins");
        bool assemblyIsAnInstalledCopy = IsUnder(assemblyDir, addins);

        // De-duplicated HERE rather than in the caller, so this function returns exactly the
        // sequence callers act on and the harness is testing the real thing.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!assemblyIsAnInstalledCopy && seen.Add(Path.Combine(assemblyDir, LauncherExeName)))
            yield return Path.Combine(assemblyDir, LauncherExeName);

        string kanghun = Path.Combine(programFiles, "Kanghun BIM", LauncherExeName);
        if (seen.Add(kanghun)) yield return kanghun;

        foreach (string year in new[] { "2026", "2025", "2024" })
        {
            string yearCopy = Path.Combine(addins, year, LauncherExeName);
            if (seen.Add(yearCopy)) yield return yearCopy;
        }

        // A machine-wide install's own folder is one of the years above and de-duplicates away. An
        // Addins deploy that is NOT one of them (a future year, a hand-copied folder) gets its own
        // copy back here rather than being dropped — reordered, never removed.
        if (assemblyIsAnInstalledCopy)
        {
            string own = Path.Combine(assemblyDir, LauncherExeName);
            if (seen.Add(own)) yield return own;
        }
    }

    /// <summary>Path containment, textual and case-insensitive: this decides an ORDERING, so it must
    /// not touch a disk that may not have the folder yet.</summary>
    private static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        string p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string f = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private const string MigrationClaimEnvVar = "KHBATCH_SCHEDULES_MIGRATED";
    private static readonly XNamespace TaskNs =
        "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static (bool ok, string message) Create(ScheduledJob job, string revitExePath) =>
        CreateCore(job, revitExePath, allowPastOnce: false);

    /// <summary>
    /// Re-registers an existing task without turning off the schedule editor's past-date guard.
    /// A completed one-time task still needs its action upgraded: otherwise an on-demand run from
    /// Task Scheduler would continue to flash the legacy cmd window.
    /// </summary>
    internal static (bool ok, string message) CreateForMigration(
        ScheduledJob job, string revitExePath) =>
        CreateCore(job, revitExePath, allowPastOnce: true);

    private static (bool ok, string message) CreateCore(
        ScheduledJob job, string revitExePath, bool allowPastOnce)
    {
        string? xmlPath = null;
        string? markerPath = null;
        bool markerExisted = false;
        bool registrationSucceeded = false;
        try
        {
            DateTime now = DateTime.Now;
            DateTime start = allowPastOnce
                ? MigrationTriggerStartLocal(job, now)
                : TriggerStartLocal(job, now);
            if (!allowPastOnce && job.Recurrence == ScheduleRecurrence.Once && start <= now)
                throw new InvalidOperationException(
                    $"The selected run time ({DisplayLocal(start)}) is in the past. " +
                    "Choose a future date and time.");

            // PREFLIGHT. The whole reason XML-v7 exists is that the launch chain can be broken by
            // machine policy in a way nothing here can see until the task fires unattended. Prove
            // it works now, while someone is looking at a dialog that can report the problem.
            //
            // A refusal used to end the save. Since 1.5.1.3 it falls back to the shell action
            // instead: a PC can refuse our unsigned launcher for a reason no install fixes (see
            // ShellArguments), and refusing to schedule at all is a worse answer than scheduling
            // through cmd.exe and saying so. The fallback is still PROVEN before it is registered —
            // a PC that refuses both gets the original message, which is the honest one.
            string? launchProblem = VerifyLaunchChain();
            TaskActionKind action = TaskActionKind.Launcher;
            if (launchProblem != null)
            {
                // Revit before cmd.exe: both start the same run, and only one of them leaves a
                // console window across the desktop for the whole of it.
                string? directProblem = RevitDirectProblem(job, revitExePath);
                if (directProblem == null)
                {
                    action = TaskActionKind.RevitDirect;
                }
                else
                {
                    string? shellProblem = ShellSelfTestFailure();
                    if (shellProblem != null)
                        throw new InvalidOperationException(
                            launchProblem +
                            "\n\nStarting Revit directly is not possible here either (" +
                            directProblem + "), and the cmd.exe fallback was refused too (" +
                            shellProblem + "), so there is no way left to start a scheduled run " +
                            "on this PC.");
                    action = TaskActionKind.Shell;
                }
            }

            markerPath = RegistrationMarkerPath(job.Id);
            markerExisted = File.Exists(markerPath);
            WriteRegistrationMarker(markerPath, revitExePath, action);
            xmlPath = TaskXmlPath(job.Id);
            WriteTaskXml(xmlPath, action switch
            {
                TaskActionKind.RevitDirect => BuildTaskXmlWithAction(
                    job, revitExePath, "", start, FireDirectory(job.Id)),
                TaskActionKind.Shell => BuildTaskXmlWithAction(
                    job, ShellExePath(), ShellArguments(job, revitExePath), start),
                _ => BuildTaskXml(job, LauncherExePath(), revitExePath, start)
            });

            (int code, string output) = RunSchtasks(new List<string>
            {
                "/Create", "/TN", job.TaskName, "/XML", xmlPath, "/F"
            });

            // Only remove the superseded script launchers once Task Scheduler has accepted the
            // exe-backed registration. If registration failed, the existing task still needs them.
            if (code == 0)
            {
                registrationSucceeded = true;
                TryDelete(LauncherVbsPath(job.Id));
                TryDelete(LegacyLauncherCmdPath(job.Id));
            }

            string recurrence = job.Recurrence switch
            {
                ScheduleRecurrence.Daily => "DAILY",
                ScheduleRecurrence.Weekly => "WEEKLY on " +
                    string.Join(",", EffectiveWeeklyDays(job, start.DayOfWeek).Select(SchtasksDay)),
                _ => "ONCE"
            };
            string when = job.Recurrence == ScheduleRecurrence.Once
                ? $"{recurrence} on {DisplayLocal(start)}"
                : $"{recurrence} at {DisplayTime(start)}";

            if (code == 0)
                return (true, $"Scheduled task '{job.TaskName}' created ({when}, local PC time)." +
                              action switch
                              {
                                  TaskActionKind.RevitDirect => RevitDirectNote(launchProblem!),
                                  TaskActionKind.Shell => ShellFallbackNote(launchProblem!),
                                  _ => ""
                              });

            // Keep the rejected definition. schtasks reports "the task XML is malformed" and little
            // else, and the temp copy is deleted below — so without this the one artefact that could
            // explain the refusal is gone by the time anyone asks about it.
            string kept = KeepFailedTaskXml(xmlPath, job.Id);
            return (false,
                $"schtasks failed (exit {code}):\n{output}" +
                (kept.Length > 0 ? $"\nThe rejected task definition was kept at:\n{kept}" : ""));
        }
        catch (Exception ex)
        {
            return (false, "Could not create scheduled task:\n" + ex.Message);
        }
        finally
        {
            // A failed first registration must not leave a current marker that later migration
            // mistakes for an up-to-date task. A pre-existing marker belongs to a task that is
            // still registered, so it stays.
            if (!registrationSucceeded && !markerExisted && markerPath != null)
                TryDelete(markerPath);
            if (!string.IsNullOrWhiteSpace(xmlPath))
                try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Arms or pauses an ALREADY-REGISTERED task, leaving its definition, its triggers and its run
    /// history in place. The caller persists <see cref="ScheduledJob.Enabled"/> only when this
    /// succeeded, so the stored flag can never claim a state Windows did not accept.
    ///
    /// <para>Deliberately <c>schtasks /Change</c> rather than re-running <see cref="Create"/>. Create
    /// pays the <see cref="VerifyLaunchChain"/> preflight per job, and its past-date guard would
    /// refuse to re-arm a <see cref="ScheduleRecurrence.Once"/> job whose date has already gone by —
    /// which is a thing you are allowed to do (the row then reads "(ran once)", the honest answer).</para>
    /// </summary>
    public static (bool ok, string message) SetEnabled(ScheduledJob job, bool enabled)
    {
        try
        {
            (int code, string output) = RunSchtasks(new List<string>
            {
                "/Change", "/TN", job.TaskName, enabled ? "/ENABLE" : "/DISABLE"
            });
            return code == 0
                ? (true, $"Scheduled task '{job.TaskName}' {(enabled ? "enabled" : "disabled")}.")
                : (false, $"schtasks change failed (exit {code}):\n{output}");
        }
        catch (Exception ex)
        {
            return (false, "Could not change the scheduled task:\n" + ex.Message);
        }
    }

    public static (bool ok, string message) Delete(ScheduledJob job)
    {
        try
        {
            (int code, string output) = RunSchtasks(new List<string> { "/Delete", "/TN", job.TaskName, "/F" });
            TryDelete(RegistrationMarkerPath(job.Id));
            TryDelete(LauncherVbsPath(job.Id));
            TryDelete(LegacyLauncherCmdPath(job.Id));
            TryDeleteFireDirectory(job.Id);
            string xml = TaskXmlPath(job.Id);
            if (File.Exists(xml)) File.Delete(xml);
            return code == 0
                ? (true, $"Scheduled task '{job.TaskName}' deleted.")
                : (false, $"schtasks delete failed (exit {code}):\n{output}");
        }
        catch (Exception ex)
        {
            return (false, "Could not delete scheduled task:\n" + ex.Message);
        }
    }

    /// <summary>
    /// Upgrades tasks created by the old locale-sensitive /SD launcher or the console-backed XML-v2
    /// launcher, including XML-v3 tasks that permitted hard termination. Called only from a normal
    /// interactive Revit startup. Missing launchers are ignored
    /// (the schedule folder also contains test/job JSON that was never registered). Completed
    /// one-time registrations are upgraded too, so an on-demand run cannot retain the cmd window.
    /// A process-wide claim means Full and DRW cannot both rewrite the shared schedule store.
    /// </summary>
    internal static void MigrateLegacyRegistrations(string revitExePath)
    {
        string pid = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(Environment.GetEnvironmentVariable(MigrationClaimEnvVar), pid,
                StringComparison.Ordinal))
            return;
        Environment.SetEnvironmentVariable(MigrationClaimEnvVar, pid);

        foreach (ScheduledJob job in ScheduleStore.LoadAll())
        {
            string? registration = ExistingRegistrationText(job.Id);
            if (registration == null || !NeedsRegistrationUpgrade(registration))
                continue;

            // Keep a legacy blank owner blank. The process-wide claim still makes it run once,
            // while either edition can continue to service it if the other is later uninstalled.
            CreateForMigration(job, revitExePath);
        }
    }

    internal static bool NeedsRegistrationUpgrade(string? launcherText) =>
        launcherText == null ||
        launcherText.IndexOf(RegistrationMarker, StringComparison.Ordinal) < 0;
}
