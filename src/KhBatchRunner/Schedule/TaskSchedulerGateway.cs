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

public static class TaskSchedulerGateway
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

    /// <summary>
    /// Builds a locale-independent task definition. A StartBoundary with no UTC offset is
    /// intentionally a local wall-clock value: Windows applies the timezone and daylight-saving
    /// rules of the PC registering the task, rather than parsing a culture-specific /SD string.
    /// </summary>
    /// <summary>The launcher-backed task definition — the normal shape.</summary>
    internal static XDocument BuildTaskXml(
        ScheduledJob job, string launcherPath, string revitExePath, DateTime start) =>
        BuildTaskXmlWithAction(job, launcherPath, LauncherArguments(job, revitExePath), start);

    /// <summary>
    /// The same definition with an explicit command and arguments, so the shell fallback
    /// (<see cref="ShellExePath"/> + <see cref="ShellArguments"/>) registers through one builder
    /// rather than a copy of it. Everything except the two <c>Exec</c> values is identical between
    /// the two shapes, and a second copy is where settings quietly diverge.
    /// </summary>
    internal static XDocument BuildTaskXmlWithAction(
        ScheduledJob job, string command, string arguments, DateTime start,
        string? workingDirectory = null)
    {
        string owner = EffectiveOwner(job);
        string userId;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            userId = identity.User?.Value ?? identity.Name;

        XElement trigger = BuildTrigger(job, start);

        return new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(TaskNs + "Task", new XAttribute("version", "1.4"),
                new XElement(TaskNs + "RegistrationInfo",
                    new XElement(TaskNs + "Date", LocalBoundary(DateTime.Now)),
                    new XElement(TaskNs + "Author", userId),
                    new XElement(TaskNs + "Description",
                        $"{Product.Name} scheduled job: {job.Name} ({owner})")),
                new XElement(TaskNs + "Triggers", trigger),
                new XElement(TaskNs + "Principals",
                    new XElement(TaskNs + "Principal", new XAttribute("id", "Author"),
                        new XElement(TaskNs + "UserId", userId),
                        new XElement(TaskNs + "LogonType", "InteractiveToken"),
                        new XElement(TaskNs + "RunLevel", "LeastPrivilege"))),
                new XElement(TaskNs + "Settings",
                    new XElement(TaskNs + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNs + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNs + "StopIfGoingOnBatteries", "false"),
                    // Never let Task Scheduler escalate an End/timeout into forced process
                    // termination. Revit shutdown is requested normally by ScheduledRunner.
                    new XElement(TaskNs + "AllowHardTerminate", "false"),
                    new XElement(TaskNs + "StartWhenAvailable", "true"),
                    new XElement(TaskNs + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(TaskNs + "IdleSettings",
                        new XElement(TaskNs + "StopOnIdleEnd", "false"),
                        new XElement(TaskNs + "RestartOnIdle", "false")),
                    new XElement(TaskNs + "AllowStartOnDemand", "true"),
                    // THE task-level arm/disarm flag, and it must follow the job (1.4.7).
                    // Hardcoding "true" here is not cosmetic: MigrateLegacyRegistrations re-registers
                    // through this builder on every interactive Revit startup, and ScheduleSaver
                    // deletes and re-creates the task on every save -- so a paused schedule would
                    // quietly re-arm itself the next time Revit started or the job was edited.
                    // The two TRIGGER-level <Enabled> elements below stay true: mixing the two makes
                    // the state unreadable in Task Scheduler, which drives only this one.
                    new XElement(TaskNs + "Enabled", job.Enabled ? "true" : "false"),
                    new XElement(TaskNs + "Hidden", "false"),
                    new XElement(TaskNs + "RunOnlyIfIdle", "false"),
                    new XElement(TaskNs + "WakeToRun", "false"),
                    // PT0S means no scheduler-imposed timeout. The liveness supervisor reports and
                    // stops launching work while leaving a questionable Revit open for inspection.
                    new XElement(TaskNs + "ExecutionTimeLimit", "PT0S"),
                    new XElement(TaskNs + "Priority", "7")),
                new XElement(TaskNs + "Actions", new XAttribute("Context", "Author"),
                    new XElement(TaskNs + "Exec",
                        new XElement(TaskNs + "Command", command),
                        // Everything the old generated script baked into its own source is an
                        // argument now, so the launcher binary is identical on every machine and
                        // only the task XML varies per job.
                        // OMITTED rather than left empty when there are none: the Revit-direct
                        // action passes Revit nothing at all, and a stray argument is the kind of
                        // thing Revit tries to open as a file.
                        arguments.Length > 0 ? new XElement(TaskNs + "Arguments", arguments) : null,
                        // Usually just a stable folder. For the Revit-direct action it IS the
                        // message: a scheduled task cannot set an environment variable, but it can
                        // hand its program a working directory, and that is how the run says which
                        // job fired. See LaunchIdentity.
                        new XElement(TaskNs + "WorkingDirectory",
                            workingDirectory ?? ScheduleStore.ScheduledDirectory)))));
    }

    private static void WriteTaskXml(string path, XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Encoding.Unicode,
            Indent = true,
            OmitXmlDeclaration = false
        };
        using XmlWriter writer = XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    private static XElement BuildTrigger(ScheduledJob job, DateTime start)
    {
        if (job.Recurrence == ScheduleRecurrence.Once)
            return new XElement(TaskNs + "TimeTrigger",
                new XElement(TaskNs + "StartBoundary", LocalBoundary(start)),
                new XElement(TaskNs + "Enabled", "true"));

        XElement schedule = job.Recurrence == ScheduleRecurrence.Daily
            ? new XElement(TaskNs + "ScheduleByDay",
                new XElement(TaskNs + "DaysInterval", "1"))
            : new XElement(TaskNs + "ScheduleByWeek",
                new XElement(TaskNs + "WeeksInterval", "1"),
                new XElement(TaskNs + "DaysOfWeek",
                    EffectiveWeeklyDays(job, start.DayOfWeek).Select(d => new XElement(TaskNs + XmlDay(d)))));

        return new XElement(TaskNs + "CalendarTrigger",
            new XElement(TaskNs + "StartBoundary", LocalBoundary(start)),
            new XElement(TaskNs + "Enabled", "true"),
            schedule);
    }

    internal static DateTime TriggerStartLocal(ScheduledJob job, DateTime now)
    {
        TimeSpan time = StoredTime(job.Time);
        if (job.Recurrence == ScheduleRecurrence.Once)
            return (job.StartDate?.Date ?? now.Date) + time;

        if (job.Recurrence == ScheduleRecurrence.Daily)
        {
            DateTime candidate = now.Date + time;
            return candidate > now ? candidate : candidate.AddDays(1);
        }

        HashSet<DayOfWeek> days = new(EffectiveWeeklyDays(job, now.DayOfWeek));
        for (int offset = 0; offset <= 7; offset++)
        {
            DateTime candidate = now.Date.AddDays(offset) + time;
            if (days.Contains(candidate.DayOfWeek) && candidate > now)
                return candidate;
        }
        throw new InvalidOperationException("Could not calculate the next weekly run time.");
    }

    /// <summary>
    /// Recurring migration always targets the next occurrence. For a legacy one-time job whose
    /// JSON predates StartDate, LastRunAt is the closest persisted record of its original date;
    /// retaining that past boundary upgrades the action without scheduling another run.
    /// </summary>
    internal static DateTime MigrationTriggerStartLocal(ScheduledJob job, DateTime now)
    {
        if (job.Recurrence != ScheduleRecurrence.Once)
            return TriggerStartLocal(job, now);

        TimeSpan time = StoredTime(job.Time);
        DateTime date = job.StartDate?.Date ?? job.LastRunAt?.Date ?? now.Date;
        return date + time;
    }

    private static IReadOnlyList<DayOfWeek> EffectiveWeeklyDays(
        ScheduledJob job, DayOfWeek fallbackDay) =>
        job.WeeklyDays.Count > 0
            ? job.WeeklyDays.Distinct().OrderBy(d => d).ToList()
            : new List<DayOfWeek> { fallbackDay };

    internal static string LocalBoundary(DateTime value) =>
        value.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);

    private static TimeSpan StoredTime(string? storedTime)
    {
        if (!TimeSpan.TryParse(storedTime, CultureInfo.InvariantCulture, out TimeSpan time) ||
            time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            throw new FormatException(
                $"The schedule time '{storedTime}' is invalid. Edit the schedule and choose a time again.");
        return new TimeSpan(time.Hours, time.Minutes, 0);
    }

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

    internal static string RegistrationMarkerPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "launcher_" + id + ".marker");

    private static string LauncherVbsPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "run_" + id + ".vbs");

    internal static string LauncherLogPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "launcher_" + id + ".log");

    private static string LegacyLauncherCmdPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "run_" + id + ".cmd");

    /// <summary>
    /// The text that says which registration generation a job's task belongs to, or null when the
    /// job was never registered. XML-v7 keeps it in a marker file because the launcher is now a
    /// shared executable with nowhere to carry a per-job comment; the two script generations it
    /// replaces are still read in place so their tasks can be recognised and upgraded.
    /// </summary>
    internal static string? ExistingRegistrationText(string id)
    {
        foreach (string path in new[]
                 { RegistrationMarkerPath(id), LauncherVbsPath(id), LegacyLauncherCmdPath(id) })
        {
            try { if (File.Exists(path)) return File.ReadAllText(path); }
            catch { /* an unreadable marker is treated as absent, and re-registered */ }
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string TaskXmlPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "task_" + id + ".xml");

    /// <summary>Preserves a rejected task definition for diagnosis. Best effort: a failure to keep
    /// the evidence must never turn into a second, more confusing error.</summary>
    private static string KeepFailedTaskXml(string? xmlPath, string id)
    {
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath)) return "";
        try
        {
            string kept = Path.Combine(ScheduleStore.ScheduledDirectory, "task_" + id + ".rejected.xml");
            File.Copy(xmlPath!, kept, overwrite: true);
            return kept;
        }
        catch { return ""; }
    }

    private static string SchtasksDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "SUN"
    };

    private static string XmlDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Monday",
        DayOfWeek.Tuesday => "Tuesday",
        DayOfWeek.Wednesday => "Wednesday",
        DayOfWeek.Thursday => "Thursday",
        DayOfWeek.Friday => "Friday",
        DayOfWeek.Saturday => "Saturday",
        _ => "Sunday"
    };

    private static string DisplayLocal(DateTime value) =>
        value.ToString("g", CultureInfo.CurrentCulture);

    private static string DisplayTime(DateTime value) =>
        value.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern,
            CultureInfo.CurrentCulture);

    /// <summary>
    /// What Windows recorded as each of our tasks' "Last Run Result", keyed by task name.
    ///
    /// Read in ONE <c>schtasks /Query</c> rather than one per row: a machine can carry hundreds of
    /// tasks and the schedule grid refreshes on every save. Any failure yields an empty map and the
    /// column simply shows nothing — a diagnostic must never be the reason a window fails to open.
    /// </summary>
    internal static Dictionary<string, int> LastRunResults()
    {
        var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            (int code, string output) = RunSchtasks(new List<string> { "/Query", "/FO", "CSV", "/V" });
            if (code != 0) return results;

            string[] lines = output.Split('\n');
            if (lines.Length < 2) return results;

            List<string> header = ParseCsvLine(lines[0]);
            int nameAt = header.FindIndex(h => h.Equals("TaskName", StringComparison.OrdinalIgnoreCase));
            int resultAt = header.FindIndex(h => h.Equals("Last Result", StringComparison.OrdinalIgnoreCase));
            if (nameAt < 0 || resultAt < 0) return results;

            foreach (string line in lines.Skip(1))
            {
                List<string> cells = ParseCsvLine(line);
                if (cells.Count <= Math.Max(nameAt, resultAt)) continue;
                // schtasks reports the full path ("\KhBatchRunner_<id>"); jobs know the leaf name.
                string name = cells[nameAt].TrimStart('\\');
                if (!name.StartsWith("KhBatchRunner_", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(cells[resultAt], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int value))
                    results[name] = value;
            }
        }
        catch { /* the grid shows nothing rather than failing to open */ }
        return results;
    }

    /// <summary>
    /// Leaf names of every registered task whose name starts with <paramref name="prefix"/>. Reads
    /// column 0 of the non-verbose CSV, which is the task name whatever the Windows display
    /// language calls its header. Any failure yields an empty list.
    /// </summary>
    internal static List<string> RegisteredTaskNames(string prefix)
    {
        var names = new List<string>();
        try
        {
            (int code, string output) = RunSchtasks(new List<string> { "/Query", "/FO", "CSV", "/NH" });
            if (code != 0) return names;
            foreach (string line in output.Split('\n'))
            {
                List<string> cells = ParseCsvLine(line);
                if (cells.Count == 0) continue;
                string name = cells[0].TrimStart('\\');
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !names.Contains(name))
                    names.Add(name);
            }
        }
        catch { /* nothing found is the safe answer */ }
        return names;
    }

    /// <summary>Deletes one task by its full leaf name; used only to retire 1.x task names.</summary>
    internal static (bool ok, string message) DeleteTaskByName(string taskName)
    {
        try
        {
            (int code, string output) = RunSchtasks(new List<string> { "/Delete", "/TN", taskName, "/F" });
            return code == 0 ? (true, "deleted") : (false, $"schtasks exit {code}: {output.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Task Scheduler's result code in words. Nobody should have to know that 0x1 is a child
    /// process's exit code rather than a scheduler error — that confusion is what sent the 1.3.4
    /// launcher investigation looking in the wrong place first.
    /// </summary>
    internal static string DescribeLastResult(int code) => code switch
    {
        0 => "OK",
        1 => "Revit exited with an error (1) — see the launcher log",
        4001 => "launcher: bad arguments — re-save this schedule",
        4002 => "launcher: could not start Revit — see the launcher log",
        267009 => "still running",           // 0x41301
        267011 => "has not run yet",         // 0x41303
        267014 => "stopped before it finished",  // 0x41306
        _ => $"0x{code:X}"
    };

    /// <summary>Minimal RFC-4180 splitter — enough for <c>schtasks /FO CSV</c>, whose fields are
    /// all quoted and may contain commas (task paths, descriptions, dates).</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c != '\r') cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>Argument encoding that works on both target frameworks: net8 has ArgumentList,
    /// net48 (Revit 2024) needs the same quoting done by hand.</summary>
    private static void SetArguments(ProcessStartInfo psi, List<string> args)
    {
#if NETFRAMEWORK
        var sb = new StringBuilder();
        foreach (string a in args) AppendArgument(sb, a);
        psi.Arguments = sb.ToString();
#else
        foreach (string a in args) psi.ArgumentList.Add(a);
#endif
    }

    private static (int code, string output) RunSchtasks(List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        SetArguments(psi, args);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (stdout + stderr).Trim());
    }

    // Ported from .NET's PasteArguments.AppendArgument (the encoding ArgumentList uses on net8).
    // Compiled on BOTH frameworks: net48 needs it to build a command line at all, and the task
    // XML's Arguments element is a single string on either, so LauncherArguments needs the same
    // quoting rules Windows will use to take it apart again.
    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (sb.Length != 0) sb.Append(' ');

        if (argument.Length != 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        int idx = 0;
        while (idx < argument.Length)
        {
            char c = argument[idx++];
            if (c == '\\')
            {
                int slashes = 1;
                while (idx < argument.Length && argument[idx] == '\\') { idx++; slashes++; }
                if (idx == argument.Length) sb.Append('\\', slashes * 2);
                else if (argument[idx] == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); idx++; }
                else sb.Append('\\', slashes);
            }
            else if (c == '"') { sb.Append('\\'); sb.Append('"'); }
            else sb.Append(c);
        }
        sb.Append('"');
    }
}
