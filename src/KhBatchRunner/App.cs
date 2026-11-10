using System.Drawing;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

namespace KhBatchRunner;

/// <summary>
/// Add-in shell. Builds the ribbon on startup (Setting + Batch Run + Schedule + Multi Model
/// Check + Multi Model Action panels) and, when Revit was launched by a scheduled task, arms the
/// headless scheduled runner. The Revit-style ribbon icons follow Revit's light/dark theme
/// live — Revit's API has no theme-changed event, so we poll
/// <see cref="UIControlledApplication.Idling"/> and redraw the glyphs (cheap enum compare
/// per idle; a redraw only when the theme actually flips).
/// </summary>
public partial class App : IExternalApplication
{
    private const string TabName = Product.Tab;
    private const string SettingPanel = "Setting";
    private const string CheckPanel = "Multi Model Check";
    private const string ActionPanel = "Model Batch Action";
    // Everything Navisworks, on one panel since 1.5.1.4 (it was two: "NWC" and "KH-NWD Tool").
    // Export NWC left Multi Model Action in 1.5.1 — the only item there that never writes to a
    // model. Create Family from Navisworks is the Revit half of KH-NWD Tool (the Navisworks add-in
    // in the KH-NWD-Tool repo); it lives here, as a panel of this add-in, rather than as its own
    // add-in adding a panel to this tab: OnStartup creates the tab unconditionally, so a second
    // add-in creating it first would make this one fail to load.
    private const string NwdHelperPanel = "NWD Helper";
    private const string BatchRunPanel = "Batch Run";
    private const string SchedulePanel = "Schedule";
    private const string FormaPanel = "Forma Batch Action";
    private const string InteropPanel = "Interop";
    private const string ExcelPanel = "Excel Link";
    // Formula rules that write a calculated value into a real parameter (1.5.2). Full edition only.
    // Not "Schedule Helper": the Schedule panel next door is the Windows Task Scheduler runner.
    private const string ParameterPanel = "Parameter Helper";
    private const string DrwCreatePanel = "Create";
    private const string DrwAnnotationPanel = "Annotation";
    private static readonly string AlwaysAvailable = typeof(Commands.AlwaysAvailable).FullName!;
    private static readonly string DocumentAvailability = typeof(Commands.DocumentAvailable).FullName!;
    private static readonly string AnyDocumentAvailability = typeof(Commands.AnyDocumentAvailable).FullName!;

    /// <summary>
    /// One tooltip for the Import/Export button, because it is added to BOTH edition panels and a
    /// tooltip that says different things about the same button in two builds is how two editions
    /// end up disagreeing about what a feature does.
    ///
    /// <para>It states the exclusion up front. A backup that silently leaves schedules behind is
    /// worse than one that says so, and this is the last place to say it before the dialog does.</para>
    /// </summary>
    private const string SettingsTransferTip =
        "Copy saved settings to or from another PC as ONE zip file — model sets, script sets, and " +
        "every tool's saved input sets. Schedules are NOT included (a schedule is half-stored in " +
        "Windows Task Scheduler and must be re-created), and licence/update state is never carried. " +
        "Opens no model and changes no schedule.";

    // Ribbon buttons whose glyph must track the active theme, paired with the factory that
    // redraws that glyph in a given palette and size. RibbonButton rather than PushButton since
    // 1.5.1: a PulldownButton has a face glyph of its own, and RibbonButton is the base that declares
    // Image/LargeImage. SmallOnly marks stacked/dropdown entries, which show only the 16px Image.
    private readonly List<(RibbonButton Button, Func<IconPalette, int, BitmapSource> Icon, bool SmallOnly)> _themedButtons = new();
    private IconPalette _palette = IconPalette.Light;
    private bool _lastThemeDark;

#if NETFRAMEWORK
    // Revit 2024 (net48) shares one AppDomain across all add-ins and ships no
    // System.Text.Json — hook AssemblyResolve to our own folder before any code touches it.
    // A static ctor runs when Revit's add-in manager first instantiates App, ahead of
    // OnStartup and any member that could trigger an STJ bind.
    static App() => Compat.AddinDirectoryResolver.Register();
#endif

    public Result OnStartup(UIControlledApplication application)
    {
        // The working directory this Revit was started in, recorded before anything can change it.
        // A scheduled task can hand a per-job folder to the program it starts but cannot set an
        // environment variable, so for the Revit-direct task action (see TaskSchedulerGateway) this
        // is how the run says which job fired. Logged unconditionally because "which folder did
        // Revit actually inherit" is unanswerable after the fact, and it is one line — and it is
        // the line that would diagnose a future Revit that changes the directory on us.
        try { Diagnostics.RunTrace.Mark("launch cwd=" + Environment.CurrentDirectory); }
        catch { /* a breadcrumb must never stop the add-in loading */ }

        // FIRST, and before ScheduleEnvironment.IsUnattendedLaunch is read below: turn that folder
        // into the same KHBATCH_JOB/KHBATCH_OWNER pair every other launch shape arrives with, so
        // nothing downstream has to know there is more than one way to start a scheduled run.
        // 2.0.0: carry a 1.x install's settings and schedules into the renamed folders, once,
        // before LaunchIdentity (below) or anything else reads a store. The 1.x folders are kept.
        Settings.LegacyMigration.CopySettingsOnce();
        Schedule.LaunchIdentity.Resolve();

        application.CreateRibbonTab(TabName);

        string asm = Assembly.GetExecutingAssembly().Location;
        _lastThemeDark = Theme.IsDark();
        _palette = IconPalette.ForCurrentTheme();

        BuildRibbon(application, asm);

        // Install the Revit-backed implementations of the services shared dialogs need. Inside
        // Revit the action list comes from the registry and confirmations use Revit's own
        // TaskDialog, exactly as before; the standalone controller installs its own equivalents.
        UI.HostServices.ListActions = () => Actions.ActionRegistry.All()
            .Select(a => new UI.ActionInfo(a.Name, a.Category, a.ModifiesModel, a.Worksets))
            .ToList();
        // Which Revit is hosting, so the Forma model lists can grey out a model this one must not
        // open. Blank outside Revit, where it would be a guess rather than a fact.
        UI.HostServices.RunningRevitVersion = application.ControlledApplication.VersionNumber;
        UI.HostServices.Confirm = (title, message) =>
        {
            // Fully qualified: net8's WinForms also defines TaskDialog, and this add-in means Revit's.
            var confirm = new Autodesk.Revit.UI.TaskDialog(title)
            {
                MainInstruction = title,
                MainContent = message,
                CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Yes
                              | Autodesk.Revit.UI.TaskDialogCommonButtons.No,
                DefaultButton = Autodesk.Revit.UI.TaskDialogResult.No
            };
            return confirm.Show() == Autodesk.Revit.UI.TaskDialogResult.Yes;
        };

        // Create the ExternalEvent used to surface "leave files open" models after a batch
        // (activating documents is illegal from the Idling loop that drives the batch).
        Batch.LeftOpenDocsActivator.Initialize();

        // Repaint the glyphs whenever Revit's theme changes (no dedicated API event exists).
        application.Idling += OnThemeIdling;

        // Any supervised run whose watching Revit is gone (closed mid-recovery, or crashed) is
        // recorded as Abandoned rather than left claiming to be in progress — otherwise a run that
        // was cut short looks identical to one still going.
        try { Schedule.RunManifestStore.MarkAbandoned(); } catch { /* never block startup */ }

        // Clear notifications whose retention window passed while nothing was running. Each raised
        // toast tidies the older ones, so this only matters for the LAST notification of a run that
        // finished unattended — which is exactly the one still on screen the next morning.
        try { UI.RunNotification.SweepExpiredToasts(); } catch { /* never block startup */ }

        // UNATTENDED ONLY: start the dialog watchdog NOW, on its own thread, before anything that a
        // modal dialog could block. Not when the batch begins — a batch begins from Idling, and a
        // dialog raised during Revit's own startup stops Idling from ever running. See
        // ForeignDialogBreaker.StartShared for the 2026-08-18 run this cost.
        //
        // Keyed to the launch environment rather than to a job this edition claimed: when Full and
        // DRW are both loaded, only one of them accepts the job, but BOTH are in the process that
        // the dialog would freeze. StartShared is idempotent, so the loser arming it is harmless.
#if !EDITION_DRW
        // Parameter Helper's live updater (optional, so a model never asks for it). Interactive
        // sessions only: a scheduled batch must do exactly what its actions say and nothing else.
        if (!ScheduleEnvironment.IsUnattendedLaunch) ParamFormula.LiveFormulas.Startup(application);
        // Parameter Helper's headless self-test: PARAMHELPER_SELFTEST=<report> + PARAMHELPER_SELFTEST_DIR=<work folder>.
        if (ParamFormula.ParamHelperSelfTest.IsRequested(out string phReport, out string phDir))
            ParamFormula.ParamHelperSelfTest.ArmOnIdling(application, phReport, phDir);
#endif
        if (ScheduleEnvironment.IsUnattendedLaunch)
        {
            try { Batch.ForeignDialogBreaker.StartShared(); }
            catch (Exception ex) { Diagnostics.RunTrace.Mark("dialog watchdog not started: " + ex.Message); }
        }

        // Diagnostic: if launched with KHBATCH_SELFTEST=<logpath>, probe the Forma REST stack
        // headlessly against the signed-in session and self-terminate (dev/test only).
        if (Diagnostics.SelfTest.IsRequested(out string selfTestLog))
            Diagnostics.SelfTest.ArmOnIdling(application, selfTestLog);
#if EDITION_DRW
        // Enlarge Door & Window's live update (optional updater). Interactive sessions only, as
        // Parameter Helper's: a scheduled batch must do exactly what its actions say.
        if (!ScheduleEnvironment.IsUnattendedLaunch) Drw.DoorWindow.LiveDoorWindow.Startup(application);
        // DRW's own headless self-test: DRW_SELFTEST=<report> + DRW_SELFTEST_MODEL=<.rvt>.
        if (Drw.SelfTest.DrwSelfTest.IsRequested(out string drwReport, out string drwModel))
            Drw.SelfTest.DrwSelfTest.ArmOnIdling(application, drwReport, drwModel);
#endif
        // If launched with KHBATCH_TOKEN_BROKER=<pipeName>, serve the signed-in session's Forma
        // token to the standalone controller over that pipe and stay alive (hidden) until the
        // owning process exits. Opens no document; must sit above the UpdateCheck branch so a
        // broker — which has no visible window — can never be blocked behind a dialog.
        else if (Auth.TokenBrokerServer.IsRequested(out string brokerPipe))
            Auth.TokenBrokerServer.ArmOnIdling(application, brokerPipe);
        // If launched by a scheduled task — or as one group of an auto-split run — run the job
        // headless once Revit is idle. The breadcrumb is the child half of the launch record: an
        // auto-split parent logs a line per group it started, and a group that never gets this far
        // (Revit failed to start, sat on a sign-in prompt, ran out of memory) leaves the gap visible.
        else if (ScheduledRunner.TryGetPendingJob(out ScheduledJob? job) && job != null)
        {
            Diagnostics.RunTrace.Mark(
                $"armed {(job.IsEphemeralChunk ? "group " + job.Id : "schedule '" + job.Name + "'")}");
            ScheduledRunner.ArmOnIdling(application, job);
        }
        // Interactive session only (neither headless path armed): daily build-validity check + a
        // one-shot "update available" notice. Headless self-test/scheduled runs must never pop a
        // dialog, so they deliberately skip this — they refresh from the gate instead.
#if EDITION_DRW
        // DRW Helper has no update manifest of its own yet. The batch runner's latest.json would
        // report its 1.5.x as "newer" than DRW 0.x every day, and the cache it keeps is shared, so
        // DRW stays off that channel entirely. It also has no legacy schedule registrations.
#else
        else
        {
            // Upgrade existing schedule registrations once after installing this build. Older
            // launchers used locale-sensitive /SD text and returned immediately after START;
            // recreating them from XML fixes local time and overlapping execution without asking
            // the user to delete/re-save every schedule manually.
            try
            {
                string revitExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                TaskSchedulerGateway.MigrateLegacyRegistrations(revitExe);
                // ...then retire each 1.x task whose 2.0.0 task is now registered.
                Settings.LegacyMigration.RemoveLegacyTasks();
            }
            catch { /* schedule repair must never block Revit startup */ }
            Update.UpdateCheck.ArmOnIdling(application);
        }
#endif

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.Idling -= OnThemeIdling;
#if !EDITION_DRW
        ParamFormula.LiveFormulas.Shutdown(application);
        Mcp.McpBridge.Stop();
#endif
#if EDITION_DRW
        application.ViewActivated -= Drw.SyncView.SyncViewService.OnViewActivated;
        Drw.DoorWindow.LiveDoorWindow.Shutdown(application);
#endif
        Batch.LeftOpenDocsActivator.Shutdown();
        Batch.ForeignDialogBreaker.StopShared();
        Update.UpdateCheck.Detach(application);
        return Result.Succeeded;
    }

    private void OnThemeIdling(object? sender, IdlingEventArgs e)
    {
        CaptureSignedInUser(sender);

        bool dark = Theme.IsDark();
        if (dark == _lastThemeDark) return;   // no change → nothing to redraw.
        _lastThemeDark = dark;
        _palette = IconPalette.ForCurrentTheme();
        foreach ((RibbonButton button, Func<IconPalette, int, BitmapSource> icon, bool smallOnly) in _themedButtons)
            Paint(button, icon, smallOnly);
    }

    /// <summary>
    /// Keeps <see cref="UI.HostServices.SignedInUserId"/> current with the Autodesk account Revit
    /// is signed in as.
    ///
    /// <para>Read here rather than in <c>OnStartup</c> because <c>LoginUserId</c> lives on
    /// <c>Application</c>, which <c>UIControlledApplication</c> does not expose — the same reason
    /// <see cref="Auth.TokenBrokerServer"/> captures it from Idling. Read on EVERY tick, not once:
    /// a user can sign out and back in as someone else without restarting Revit, and that is the
    /// exact case the account-aware model lists exist for. The read is a property access; the
    /// snapshot behind it is only recomputed when the value actually changes.</para>
    /// </summary>
    private static void CaptureSignedInUser(object? sender)
    {
        if (sender is not UIApplication ui) return;
        string user;
        try { user = ui.Application.LoginUserId ?? ""; }
        catch { return; /* non-fatal: an unknown account is Unknown, never "no access" */ }

        if (string.Equals(user, UI.HostServices.SignedInUserId, StringComparison.Ordinal)) return;
        UI.HostServices.SignedInUserId = user;
        Presets.AccountAccess.Invalidate();
    }
}
