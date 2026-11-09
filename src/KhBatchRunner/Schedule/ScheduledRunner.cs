using System.Diagnostics;
using System.IO;
using System.Globalization;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;

// Disambiguated from System.Diagnostics, which this file also uses (Process).
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Runs a <see cref="ScheduledJob"/>'s built-in actions over the union of its model sets +
/// ad-hoc models (one <see cref="BatchEngine"/> pass per action, since each action names its
/// own log files). When launched by a scheduled task, Revit sets <c>KHBATCH_JOB</c>; the
/// add-in arms a one-shot Idling handler (<see cref="ArmOnIdling"/>) that hands the job to
/// <see cref="BatchIdlingDriver"/> — one model per Idling tick — then quits Revit when the
/// queue drains (unless <see cref="ScheduledJob.Completion"/> is <c>LeaveOpen</c>, in which case
/// the models are surfaced instead and Revit stays running). Both the headless path
/// (<see cref="StartHeadless"/>) and the dialog's
/// "Run now" (<see cref="TryRunInteractive"/>) go through that same driver rather than opening
/// models synchronously inside a single Idling tick / command: doing so blocks the message
/// pump <c>OpenAndActivateDocument</c> needs and deadlocks Revit (the wedge that used to force
/// a headless run to hang after the first model opened).
///
/// DYN script sets execute via <see cref="RunScriptSetAction"/> (the Dynamo runner). Dynamo
/// has two hard constraints (spike-verified): its <c>ExecuteCommand</c> HANGS when invoked
/// from inside an Idling event, and a graph binds to the ACTIVE document. So a headless job
/// containing script sets never enters the Idling loop below — <see cref="OnHeadlessIdling"/>'s
/// first tick escapes to an <see cref="ExternalEvent"/> (a legal command-class context) and
/// runs the whole job through <see cref="UI.BatchRunnerWindow.RunModal"/>, whose window
/// dismisses itself and which opens script-pass models activated. Action-only jobs keep the
/// proven one-model-per-tick Idling path unchanged.
/// </summary>
public static class ScheduledRunner
{
    private const string JobEnvVar = ScheduleEnvironment.Job;
    internal const string JobOwnerEnvVar = ScheduleEnvironment.Owner;
    internal const string JobClaimEnvVar = ScheduleEnvironment.Claim;

    /// <summary>
    /// True if this Revit was launched for a scheduled job (env var present + job found). Resolves a
    /// normal saved job first, then falls back to an ephemeral auto-split chunk job
    /// (<see cref="ChunkJobStore"/>) — the child instances an auto-split run launches carry a chunk
    /// job id in the same <c>KHBATCH_JOB</c> variable.
    /// </summary>
    public static bool TryGetPendingJob(out ScheduledJob? job)
    {
        job = null;
        string? id = Environment.GetEnvironmentVariable(JobEnvVar);
        if (string.IsNullOrWhiteSpace(id)) return false;

        // Full and DRW can intentionally coexist in one Revit process. A launcher created by a
        // current build names its owner; the other edition must ignore it. Old launchers carry no
        // owner, so they fall through to the process claim below and are still executed once.
        string? launchOwner = Environment.GetEnvironmentVariable(JobOwnerEnvVar);
        if (!OwnerMatches(launchOwner))
        {
            RunTrace.Mark($"scheduled launch rejected: owner '{launchOwner}' does not match this edition");
            return false;
        }

        job = ScheduleStore.GetById(id) ?? ChunkJobStore.GetById(id);
        if (job == null)
        {
            RunTrace.Mark($"scheduled launch rejected: job '{id}' was not found in this user profile");
            return false;
        }
        if (!OwnerMatches(job.ScheduleOwner))
        {
            RunTrace.Mark($"scheduled launch rejected: saved job owner '{job.ScheduleOwner}' does not match this edition");
            job = null;
            return false;
        }

        // A paused schedule is paused in Windows too (Settings/Enabled false), so this should be
        // unreachable — which is exactly why it is worth a trace when it is not. It catches a task
        // re-armed by hand in Task Scheduler, and a stale launcher firing after the pause. Chunk
        // jobs are always Enabled (ChunkLauncher forces it), so a split run passes through.
        if (!job.Enabled)
        {
            RunTrace.Mark($"scheduled launch rejected: job '{id}' is disabled");
            job = null;
            return false;
        }

        // Both assemblies see process environment variables. Claiming here makes an ownerless
        // legacy job single-consumer too. The PID in the token matters because split children
        // inherit their parent's environment: a different child PID is allowed to replace it.
        if (!TryClaimForCurrentProcess(id))
        {
            RunTrace.Mark($"scheduled launch rejected: job '{id}' was already claimed in this Revit process");
            job = null;
            return false;
        }
        RunTrace.Mark($"scheduled job accepted: '{job.Name}' id={id} owner={job.ScheduleOwner}");
        return true;
    }

    internal static bool OwnerMatches(string? owner) =>
        string.IsNullOrWhiteSpace(owner) ||
        string.Equals(ScheduledJob.NormalizeOwner(owner), Product.ScheduleOwner, StringComparison.OrdinalIgnoreCase);

    internal static bool TryClaimForCurrentProcess(string jobId)
    {
        int pid = Process.GetCurrentProcess().Id;
        string prefix = pid.ToString(CultureInfo.InvariantCulture) + ":";
        string? existing = Environment.GetEnvironmentVariable(JobClaimEnvVar);
        if (!string.IsNullOrWhiteSpace(existing) && existing.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        Environment.SetEnvironmentVariable(JobClaimEnvVar,
            prefix + jobId + ":" + Product.ScheduleOwner);
        return true;
    }

    // ---- headless driver state (one job per Revit process, so plain static fields) --------
    // The headless run drives one model per Idling tick from a *persistent* handler that stays
    // subscribed for the whole run. It deliberately does NOT reuse BatchIdlingDriver: that
    // driver subscribes its own Idling handler inside TryStart, and the headless path first
    // reaches code from *inside* an Idling event (the arming tick) — Revit throws "Can not
    // subscribe to an event during execution of that event" if you call `Idling +=` there.
    // Subscribing once from OnStartup (a non-event context, via ArmOnIdling) sidesteps that,
    // and removing a handler *is* allowed mid-event, so the run can unsubscribe itself when done.
    private static UIControlledApplication? _hlApp;
    private static EventHandler<IdlingEventArgs>? _hlHandler;
    private static DialogSuppressor? _hlSuppressor;
    private static ScheduledJob? _hlJob;
    private static List<(BatchEngine Engine, ModelPreset Preset)>? _hlPasses;
    private static BatchRunState[]? _hlStates;   // one per pass, begun up front (model-major run)
    private static int _hlModelIndex;            // outer cursor: which model
    private static int _hlPassIndex;             // inner cursor: which pass within the current model
    private static int _hlModelCount;
    // The one open document shared by the current model's passes — see ModelDocumentCursor.
    private static readonly Batch.ModelDocumentCursor _hlCursor = new();
    private static bool _hlStarted;

    /// <summary>
    /// When this instance first reached Idling — the moment it became ABLE to open a model, and
    /// therefore the only honest zero for the staggered first open.
    ///
    /// It used to be timed from arming instead (Revit's <c>OnStartup</c>), which quietly disabled
    /// the stagger: on the 2026-08-11 report the gap between arming and the first tick was ~56s,
    /// longer than any stagger the launcher assigns, so every child skipped its wait and three of
    /// them opened within one millisecond of each other. All three died. Siblings launch together
    /// and finish starting together, so a clock started at startup separates nothing.
    /// </summary>
    private static DateTime _hlFirstIdlingAt;
    private static bool _hlDelayReported;
    private static bool _hlGateReported;

    /// <summary>
    /// Arm the headless driver. Subscribes a persistent Idling handler from
    /// <c>OnStartup</c> (a legal, non-event context); the first tick builds the run plan, each
    /// subsequent tick opens/runs exactly one model, and the last tick stamps
    /// <see cref="ScheduledJob.LastRunAt"/> and quits Revit. One model per tick keeps every
    /// <c>OpenAndActivateDocument</c> in a genuinely idle context — opening models synchronously
    /// in one tick blocks the message pump the open needs and deadlocks (the wedge that used to
    /// hang a scheduled run right after the first model loaded).
    /// </summary>
    public static void ArmOnIdling(UIControlledApplication app, ScheduledJob job)
    {
        _hlApp = app;
        _hlJob = job;
        _hlStarted = false;
        _hlFirstIdlingAt = default;     // stamped on the first tick — see the field
        _hlDelayReported = false;
        _hlGateReported = false;
        _hlHandler = OnHeadlessIdling;
        // Publish "which group am I" before anything runs: actions that write per-run files read it
        // from ConfigureOutput, which is handed a folder and nothing else. See ChunkContext.
        ChunkContext.Publish(job);
        app.Idling += _hlHandler;   // legal: OnStartup is not itself an Idling event
        RunTrace.Mark($"scheduled job armed: '{job.Name}' id={job.Id}" +
                      (ChunkContext.GroupNumber > 0 ? $" group={ChunkContext.GroupNumber}" : ""));
    }

    private static void OnHeadlessIdling(object? sender, IdlingEventArgs e)
    {
        if (sender is not UIApplication uiApp || _hlJob == null) { EndHeadless(); return; }
        try
        {
            if (_hlFirstIdlingAt == default) _hlFirstIdlingAt = DateTime.UtcNow;

            // Staggered start (see ChunkLauncher.FirstOpenStaggerSeconds). Waiting by RETURNING
            // from Idling rather than sleeping is the whole point: the API thread stays free, so
            // Revit finishes its own start-up during the wait instead of being blocked through it.
            //
            // Two waits, and they answer different questions. The per-group delay keeps THIS run's
            // groups in order; the gate keeps this instance clear of every other Revit on the PC,
            // including children of a different run started a minute ago — which the per-group
            // delay cannot see and which is how instances collided on 2026-08-07.
            if (!_hlStarted && _hlJob.FirstOpenDelaySeconds > 0)
            {
                double waited = (DateTime.UtcNow - _hlFirstIdlingAt).TotalSeconds;
                if (waited < _hlJob.FirstOpenDelaySeconds)
                {
                    if (!_hlDelayReported)
                    {
                        _hlDelayReported = true;
                        RunTrace.Mark($"staggered start: waiting {_hlJob.FirstOpenDelaySeconds}s " +
                                      "before the first cloud open");
                    }
                    return;
                }
            }

            if (!_hlStarted && !FirstOpenGate.TryClaim(_hlFirstIdlingAt, out int gateWait))
            {
                if (!_hlGateReported)
                {
                    _hlGateReported = true;
                    RunTrace.Mark($"first-open gate: another Revit opened moments ago, waiting " +
                                  $"~{gateWait}s before the first cloud open");
                }
                return;
            }

            if (!_hlStarted)
            {
                _hlStarted = true;

                // Expiry gate (no dialog — nobody is watching a headless run): if the remote
                // manifest, or this build's own fallback date, says it may no longer run, skip the
                // whole job and note WHY to the schedule's error log, then quit. Decides from the
                // local cache; the check itself also refreshes that cache in the background, which
                // is the only way a schedule-only PC ever hears about a retirement.
                if (Update.UpdateCheck.IsExpired())
                {
                    string expDest = string.IsNullOrWhiteSpace(_hlJob.DestinationFolder)
                        ? DefaultDestinationFolder() : _hlJob.DestinationFolder;
                    string expProjectLog = string.IsNullOrWhiteSpace(_hlJob.ProjectLogName) ? _hlJob.Name : _hlJob.ProjectLogName;
                    WriteNote(expDest, expProjectLog, _hlJob.Name,
                        $"skipped: {Update.UpdateCheck.BlockReason} — install the latest version");
                    EndHeadless();
                    return;
                }

                // Which Forma projects this Autodesk account can reach, read ONCE for the whole run.
                // Nobody is watching a scheduled run, so the preflight in ModelPresetCombiner is
                // the only chance to say "this account cannot open these models" before the split
                // commits to a Revit start-up per group. Blocking is fine here — this is a headless
                // Idling tick with no user waiting on it — and a failure just leaves every model
                // Unknown, which changes nothing.
                WarmAccountAccess();

                // Get File Log and Get Data Extract are pure-REST steps that open no model. A job
                // made only of those never enters the model Idling loop: run them now, stamp, and
                // quit. (Mixed jobs run theirs inside the action/script builder instead — see
                // BeginHeadless / ScriptJobHandler — so they aren't run twice.)
                if (RestOnlySteps.IsOnly(_hlJob))
                {
                    string flDest = string.IsNullOrWhiteSpace(_hlJob.DestinationFolder)
                        ? DefaultDestinationFolder() : _hlJob.DestinationFolder;
                    string flLog = string.IsNullOrWhiteSpace(_hlJob.ProjectLogName) ? _hlJob.Name : _hlJob.ProjectLogName;
                    RestOnlyOutcome restOnly =
                        RestOnlySteps.RunWithOutcome(_hlJob, flDest, flLog, StampOf(_hlJob));
                    foreach (string n in restOnly.Notes)
                        WriteNote(flDest, flLog, _hlJob.Name, n, "INFO");
                    RecordRunFinished(_hlJob);
                    // BEFORE EndHeadless, and waited for. This job opens no model, so it is over in
                    // seconds with nobody watching and Revit about to close: the toast is the only
                    // trace it leaves on screen, and a fire-and-forget one would be destroyed by the
                    // shutdown that follows. Until 1.5.1.3 there was no notification here at all, so
                    // a schedule that had silently stopped firing and one working perfectly looked
                    // exactly alike.
                    NotifyRestOnlyFinished(_hlJob.Name, restOnly);
                    EndHeadless();
                    return;
                }

                // Auto-split orchestration: a real (non-chunk) job with a group size prepares one
                // fresh Revit per group of models, launches them under the run's concurrency cap,
                // and supervises them WITHOUT running any model itself. Chunk jobs
                // (IsEphemeralChunk) and jobs whose model count already fits one group fall through
                // to the normal single-process run below.
                if (!_hlJob.IsEphemeralChunk && _hlJob.GroupSize != 0)
                {
                    string sd = string.IsNullOrWhiteSpace(_hlJob.DestinationFolder)
                        ? DefaultDestinationFolder() : _hlJob.DestinationFolder;
                    string sp = string.IsNullOrWhiteSpace(_hlJob.ProjectLogName) ? _hlJob.Name : _hlJob.ProjectLogName;
                    ModelPreset splitModels = ModelPresetCombiner.Combine(
                        _hlJob.Name, _hlJob.ModelSetIds, _hlJob.ModelSetNames, _hlJob.AdHocModels, out _);
                    // GroupSizeAuto means "split, shape decided now" — the model count is only real
                    // once folder-backed sets have been re-scanned, which Combine has just done.
                    int splitSize = InteractiveSplit.SupervisedGroupSize(
                        splitModels.Models.Count,
                        InstanceBudget.EffectiveGroupSize(
                            _hlJob.GroupSize, splitModels.Models, InstanceBudget.WeightFor(_hlJob)),
                        alwaysSupervise: RunsInterop(_hlJob));

                    // Count first: CanSplit writes a note, and a run too small to split shouldn't
                    // produce one.
                    if (splitSize > 0 &&
                        CanSplit(_hlJob, splitModels.Models, sd, sp, uiApp.Application.VersionNumber))
                    {
                        // Any REST-only step runs ONCE here (the child chunk jobs strip them, so
                        // nothing is pulled per group) before the groups launch.
                        foreach (string n in RestOnlySteps.Run(_hlJob, sd, sp, StampOf(_hlJob)))
                            WriteNote(sd, sp, _hlJob.Name, n, "INFO");

                        ScheduledJob splitJob = _hlJob;
                        RunManifest? manifest = ChunkLauncher.LaunchSupervised(
                            splitJob, splitModels.Models, splitSize,
                            uiApp.Application.VersionNumber, out string? splitErr);
                        if (splitErr != null)
                            WriteNote(sd, sp, splitJob.Name, splitErr, manifest != null ? "INFO" : "ERROR");

                        if (manifest == null)
                        {
                            RecordRunFinished(splitJob);
                            EndHeadless();
                            return;
                        }

                        // This process must OUTLIVE its children now: it is the only thing that can
                        // notice a group dying with models still to do and rerun them. Unhook the
                        // Idling loop (it has no models of its own to run) but keep Revit alive, and
                        // quit only once the supervisor says the whole run is accounted for.
                        UI.RunWatchWindow.ShowForScheduled(manifest);
                        EndHeadlessKeepAlive();
                        RunSupervisor.Start(manifest, m =>
                        {
                            RecordRunFinished(splitJob);
                            // A scheduled run has nobody to click OK, so it announces itself and
                            // goes — the toast stays in the notification centre to be read later.
                            NotifyFinishedThenQuit(splitJob.Name, m);
                        });
                        // Round notifications stay ON here (they were suppressed until 1.3.4.5).
                        // This is the LEAST attended run there is: an unattended split parent whose
                        // progress window is on a machine nobody is looking at. "Attempt 2 of 4 —
                        // 3 models in 2 instances" arriving at 03:00 is how a retry becomes visible
                        // at all; without it the whole run is one silent gap ending in a verdict.
                        return;
                    }
                }

                // The scheduled PARENT escapes to an ExternalEvent (a command-class context) and
                // runs through the self-dismissing modal runner. This gives every ordinary
                // scheduled run the same visible progress bar/log as Batch Run, regardless of
                // whether its models are local or cloud. Auto-split CHILDREN remain hidden and use
                // this route only when cloud/Dynamo requires the modal message pump.
                //
                //  * SCRIPT SETS — Dynamo's ExecuteCommand hangs inside an Idling event, and its
                //    graphs need the model ACTIVE (spike-verified).
                //
                //  * ANY CLOUD MODEL — opening one from an Idling event makes Revit's own
                //    CloudWorksharing.UI fail during DocumentOpening and then hang or
                //    access-violate; that is the entire reason UI.BatchRunnerWindow exists for the
                //    interactive path. This loop got away with it for years only because a WARM
                //    collaboration cache needs no download UI. MEASURED 2026-07-25: with the cache
                //    cleared, a headless cloud run died inside the very first open — one instance
                //    or three, every time, leaving a trail ending at "open" with no "opened" and
                //    not a single row in the log. Warm cache: 14/14 fine. That is the "it stops
                //    after N models" report, and it is why the disposition below routes cloud work
                //    into the same modal message loop the interactive path uses.
                ModelPreset resolvedForContext = ModelPresetCombiner.Combine(
                    _hlJob.Name, _hlJob.ModelSetIds, _hlJob.ModelSetNames, _hlJob.AdHocModels, out _);
                bool hasCloudModel = resolvedForContext.Models.Any(m => !m.IsLocal);

                // "It fired." The one fact an overnight schedule cannot otherwise supply: until the
                // run finishes there is nothing on screen (this Revit's window is hidden) and
                // nothing in Notification Center, so a schedule that never triggered and a schedule
                // still working look identical. Only the real job announces itself — an auto-split
                // group is an implementation detail of a run that already said it started.
                if (!_hlJob.IsEphemeralChunk)
                    UI.RunNotification.ShowRunStarted(_hlJob.Name, resolvedForContext.Models.Count);

                if (!_hlJob.IsEphemeralChunk || _hlJob.ScriptSetNames.Count > 0 || hasCloudModel)
                {
                    ScheduledJob job = _hlJob;
                    RunTrace.Mark(!_hlJob.IsEphemeralChunk
                        ? "routing scheduled parent to the visible modal progress runner"
                        : hasCloudModel
                        ? "routing to the modal runner (cloud models cannot be opened from Idling)"
                        : "routing to the modal runner (script sets)");
                    EndHeadlessTeardown(); // unhook this Idling loop; the modal runner brings its own suppressor
                    _hlScriptHandler = new ModalJobHandler(job);
                    _hlScriptEvent = ExternalEvent.Create(_hlScriptHandler);
                    _hlScriptEvent.Raise();
                    return;
                }
                // Auto-dismiss any Revit pop-up for the unattended run (a blocked dialog would
                // hang the process with no one to click it); disposed in EndHeadless.
                _hlSuppressor = new DialogSuppressor(uiApp);
                if (!BeginHeadless(uiApp, _hlJob))   // false → terminal condition already noted
                    EndHeadless();
                return;                               // open the first model on the next tick
            }

            // Model-major: one (model, pass) unit per tick — all passes for the current model,
            // then advance to the next model. At most one background open per tick, and usually
            // none: the cursor keeps the model's document open across its passes, so only the
            // model's FIRST pass opens anything (see ModelDocumentCursor). Every open still lands
            // on its own tick, in a genuinely idle context — the reason this loop is one unit/tick.
            if (_hlModelIndex < _hlModelCount)
            {
                (BatchEngine Engine, ModelPreset Preset) pass = _hlPasses![_hlPassIndex];
                RunTrace.Mark($"tick model {_hlModelIndex + 1}/{_hlModelCount}, pass {_hlPassIndex + 1}/{_hlPasses.Count}");
                if (_hlModelIndex < pass.Preset.Models.Count)
                    _hlCursor.Run(pass.Engine, pass.Preset.Models[_hlModelIndex], _hlStates![_hlPassIndex]);

                _hlPassIndex++;
                if (_hlPassIndex >= _hlPasses.Count)   // this model ran every pass — next model
                {
                    _hlPassIndex = 0;
                    _hlModelIndex++;
                    _hlCursor.Release();               // nothing else will run against this model
                }
                return;
            }

            // Every model ran every pass — let each pass write any run-level artifact (e.g. the
            // Phase/Workset/Design-Option matrix), then finish the job.
            RunTrace.Mark("headless finalize");
            foreach ((BatchEngine Engine, ModelPreset Preset) p in _hlPasses!)
                p.Engine.FinalizeRun();

            // Written completion marker. No dialog exists on this path, and for an auto-split run
            // this is the ONLY way to tell "this group finished" from "this group's Revit died":
            // the parent logged a line per group it launched, and each group answers with this one.
            for (int i = 0; i < _hlPasses.Count; i++)
                RunNote.Write(_hlPasses[i].Engine.TrackingLogPath, _hlPasses[i].Engine.ActionName, "", "SUMMARY",
                    $"{(_hlJob.IsEphemeralChunk ? "group " + _hlJob.Id : _hlJob.Name)} finished — succeeded: " +
                    $"{_hlStates![i].Result.SucceededCount}, failed: {_hlStates[i].Result.FailedCount}");

            RecordRunFinished(_hlJob);

            // Unattended, non-split run: same announcement as the split path. A chunk stays silent —
            // its parent supervisor speaks for the whole run, and five groups would mean five toasts.
            if (!_hlJob.IsEphemeralChunk && _hlJob.Completion != BatchCompletion.LeaveOpen)
            {
                int ok = _hlStates!.Sum(s => s.Result.SucceededCount);
                int failed = _hlStates.Sum(s => s.Result.FailedCount);
                // AndWait, because this Revit is about to kill itself: the call returns only once
                // the shell has the toast, and it then survives the process exiting.
                // Every pass in a headless run writes to the same tracking CSV, so any state's
                // logger names the file the notification should open.
                string trackingLog = _hlStates.Length > 0 ? _hlStates[0].Tracking.Path : "";
                try { UI.RunNotification.ShowRunFinishedAndWait(_hlJob.Name, ok, failed, trackingLog); }
                catch { /* never fatal */ }
            }

            if (_hlJob.Completion == BatchCompletion.LeaveOpen)
            {
                // "Leave open": surface the models (re-open with a window, via the same
                // ExternalEvent the interactive Batch Run uses) and keep Revit running —
                // capture the model union before teardown clears _hlPasses. All passes
                // share the same combined preset, so any pass's models is the full set.
                // There is no ShowSummary on the headless path, so no modal to deadlock.
                IReadOnlyList<CloudModel> models = _hlPasses![0].Preset.Models;
                EndHeadlessKeepAlive();
                LeftOpenDocsActivator.Surface(models);
            }
            else
            {
                EndHeadless();
            }
        }
        catch (Exception ex)
        {
            // Any failure here (e.g. a later pass's BeginRun hitting an unreachable folder) is
            // fatal to an unattended run: note it and quit rather than leave Revit up.
            string destFolder = string.IsNullOrWhiteSpace(_hlJob.DestinationFolder)
                ? DefaultDestinationFolder() : _hlJob.DestinationFolder;
            string projectLog = string.IsNullOrWhiteSpace(_hlJob.ProjectLogName) ? _hlJob.Name : _hlJob.ProjectLogName;
            RunTrace.Mark($"scheduled run failed: '{_hlJob.Name}' {ex}");
            WriteNote(destFolder, projectLog, _hlJob.Name, "Scheduled run failed: " + ex.Message);
            EndHeadless();
        }
    }

    /// <summary>
    /// Interactive counterpart to <see cref="StartHeadless"/>: builds one <see cref="BatchEngine"/>
    /// pass per action — using the job's own destination and post-run
    /// <see cref="ScheduledJob.Completion"/>, never a dialog's defaults — and hands
    /// the whole queue to <see cref="BatchIdlingDriver"/> instead of running synchronously
    /// inside the calling command. That synchronous call (this method replaces it at both
    /// "Run now" call sites — <see cref="Commands.ScheduleRunCommand"/> and
    /// <see cref="Commands.BatchRunCommand"/>'s save-as-schedule branch) was reachable from
    /// the UI thread and is the same journal wedge <see cref="BatchIdlingDriver"/> exists to
    /// avoid for the built-in Multi Model Check/Action buttons.
    /// </summary>
    /// <param name="error">
    /// Set when the job can't be run at all (no models resolved, no built-in action selected).
    /// <see cref="ScheduledJob.LastRunAt"/> is deliberately NOT stamped in that case — nothing
    /// ran. If this returns <c>false</c> with <paramref name="error"/> still <c>null</c>, a
    /// batch is already running (<see cref="BatchIdlingDriver.TryStart"/>'s own guard).
    /// </param>
    /// <exception cref="Exception">
    /// Propagates whatever the first pass's <c>BeginRun</c> throws (see
    /// <see cref="BatchIdlingDriver.TryStart"/>) — the caller should catch this the same way
    /// <see cref="Commands.BatchActionCommandBase"/> does.
    /// </exception>
    public static bool TryRunInteractive(UIApplication uiApp, ScheduledJob job, string title, out string? error)
    {
        error = null;
        string destFolder = string.IsNullOrWhiteSpace(job.DestinationFolder)
            ? DefaultDestinationFolder()
            : job.DestinationFolder;
        string projectLog = string.IsNullOrWhiteSpace(job.ProjectLogName) ? job.Name : job.ProjectLogName;

        // As the headless path: one account read for the whole run, so the combiner's preflight has
        // something to answer with. Usually already cached — any window that lists model sets warms
        // it in the background — in which case this returns immediately.
        WarmAccountAccess();

        // The REST-only steps (Get File Log, Get Data Extract) open no model — run them first, then
        // continue with any model passes. A job made only of those has no passes and finishes here.
        if (RestOnlySteps.IsRequested(job))
        {
            RestOnlyOutcome restOnly =
                RestOnlySteps.RunWithOutcome(job, destFolder, projectLog, StampOf(job));
            foreach (string n in restOnly.Notes)
                WriteNote(destFolder, projectLog, job.Name, n, "INFO");
            if (RestOnlySteps.IsOnly(job))
            {
                job.LastRunAt = DateTime.Now;
                ScheduleStore.Save(job);
                // A dialog, not a toast: this run was started by hand and somebody is standing in
                // front of it. Until 1.5.1.3 "Run now" on a REST-only job returned having shown
                // nothing whatsoever, which reads exactly like a button that does not work.
                ShowRestOnlySummary(title, restOnly);
                return true;
            }
        }

        ModelPreset combined = ModelPresetCombiner.Combine(
            job.Name, job.ModelSetIds, job.ModelSetNames, job.AdHocModels, out _);
        if (combined.Models.Count == 0)
        {
            error = $"No models resolved for job '{job.Name}'.";
            return false;
        }

        // Parallel auto-split: run-now honours the job's group size the same way a scheduled fire
        // does — launch one fresh Revit per group, all at once, instead of the single-process modal
        // runner below. Confirmed first (this opens several Revit instances). A small run
        // (count <= group size) falls through to the normal single-process run.
        // Count first: CanSplit writes a note, and a run too small to split shouldn't produce one.
        // A job that runs Batch Interop goes to a supervised child even when it is too small to split,
        // so a native crash there cannot close this Revit (InteractiveSplit.SupervisedGroupSize).
        int splitSize = InteractiveSplit.SupervisedGroupSize(
            combined.Models.Count,
            InstanceBudget.EffectiveGroupSize(job.GroupSize, combined.Models, InstanceBudget.WeightFor(job)),
            alwaysSupervise: RunsInterop(job));
        if (splitSize > 0 &&
            CanSplit(job, combined.Models, destFolder, projectLog, uiApp.Application.VersionNumber))
        {
            int groups = ChunkLauncher.GroupCount(combined.Models.Count, splitSize);
            if (groups > 1)
            {
                var confirm = new Autodesk.Revit.UI.TaskDialog("Run now — parallel groups")
                {
                    MainInstruction = $"Run {combined.Models.Count} models as {groups} group(s) of up to {splitSize}?",
                    MainContent =
                        "Each group opens in its own Revit instance and closes when it finishes. " +
                        "Concurrency: " + InstanceBudget.Describe(combined.Models) + ".\n\nContinue?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };
                if (confirm.Show() != TaskDialogResult.Yes)
                    return true; // user declined the split; nothing ran (LastRunAt not stamped)
            }

            RunManifest? manifest = ChunkLauncher.LaunchSupervised(
                job, combined.Models, splitSize, uiApp.Application.VersionNumber, out string? chunkErr);
            if (manifest == null)
            {
                error = chunkErr ?? "Could not launch any group instances.";
                return false;
            }
            RunSupervisor.StartInteractive(manifest, job.Completion);
            int launched = manifest.CurrentRound!.Groups.Count;
            job.LastRunAt = DateTime.Now;
            ScheduleStore.Save(job);

            // Overall progress is opened by RunSupervisor.StartInteractive above, so every split
            // launch gets it rather than only the ones that remembered to ask.
            if (chunkErr != null)
                Autodesk.Revit.UI.TaskDialog.Show(title, chunkErr);
            return true;
        }

        List<UI.BatchRunnerWindow.Pass> passes =
            BuildJobPasses(uiApp, job, destFolder, projectLog, combined, interactive: true, out List<string> missingSets);
        if (missingSets.Count > 0)
            WriteNote(destFolder, projectLog, job.Name,
                "Script set(s) or preset(s) not found, skipped: " + string.Join(", ", missingSets));
        if (passes.Count == 0)
        {
            // A REST-only step already ran above (its files are written); nothing else to open.
            if (RestOnlySteps.IsRequested(job)) { job.LastRunAt = DateTime.Now; ScheduleStore.Save(job); return true; }
            error = "Nothing to run — no built-in action or script set resolved for this job.";
            return false;
        }

        // Interactive "Run now" runs in a modal message-loop runner, NOT the
        // Idling driver — opening Forma cloud models from an Idling event hung/crashed. The runner
        // is synchronous (it blocks here until the run finishes), honours the job's disposition
        // (Close files / Leave files open / Close Revit), and shows its own completion summary.
        // Stamp LastRunAt only once it actually ran (Results non-empty). NOTE: this is the
        // INTERACTIVE path only — the headless scheduled path (OnHeadlessIdling) must not show a
        // modal, so it keeps its own Idling loop.
        IReadOnlyList<UI.BatchRunnerWindow.PassResult> results =
            UI.BatchRunnerWindow.RunModal(uiApp, passes, title, job.Completion);
        if (results.Count > 0)
        {
            job.LastRunAt = DateTime.Now;
            ScheduleStore.Save(job);
        }
        return true;
    }

    /// <summary>
    /// First-tick setup for the headless run: resolves the models, builds one
    /// <see cref="BatchEngine"/> pass per action (job's own destination; open mode fixed by
    /// category), and opens the first pass. Returns <c>false</c> — after writing an explanatory note — for a
    /// terminal condition (no models resolved, or no built-in action), so the caller ends the run.
    /// </summary>
    private static bool BeginHeadless(UIApplication uiApp, ScheduledJob job)
    {
        string destFolder = string.IsNullOrWhiteSpace(job.DestinationFolder)
            ? DefaultDestinationFolder()
            : job.DestinationFolder;
        string projectLog = string.IsNullOrWhiteSpace(job.ProjectLogName) ? job.Name : job.ProjectLogName;

        // Mixed job (REST-only steps + model actions): run the REST pulls up front. A job made only
        // of them never reaches here — OnHeadlessIdling handles it before the model loop.
        foreach (string n in RestOnlySteps.Run(job, destFolder, projectLog, StampOf(job)))
            WriteNote(destFolder, projectLog, job.Name, n, "INFO");

        ModelPreset combined = ModelPresetCombiner.Combine(
            job.Name, job.ModelSetIds, job.ModelSetNames, job.AdHocModels, out List<string> setNotes);
        foreach (string n in setNotes) WriteNote(destFolder, projectLog, job.Name, n);
        if (combined.Models.Count == 0)
        {
            WriteNote(destFolder, projectLog, job.Name, $"No models resolved for job '{job.Name}'.");
            return false;
        }
        // Script-set jobs never reach here (OnHeadlessIdling routes them to the ExternalEvent /
        // modal-runner path), so an empty action list means the job selects nothing at all.
        if (job.ActionNames.Count == 0)
        {
            WriteNote(destFolder, projectLog, job.Name,
                "Nothing to run — the job selects no built-in action or script set.");
            return false;
        }

        // Same pass-building as the interactive path (handles the Initiate special-case), then
        // mapped to the headless tuple list. Script-set jobs never reach here.
        List<UI.BatchRunnerWindow.Pass> built =
            BuildJobPasses(uiApp, job, destFolder, projectLog, combined, interactive: false, out List<string> missingSets);
        if (missingSets.Count > 0)
            WriteNote(destFolder, projectLog, job.Name, "Script set(s) or preset(s) not found, skipped: " + string.Join(", ", missingSets));
        if (built.Count == 0)
        {
            WriteNote(destFolder, projectLog, job.Name, "Nothing to run — no action or script set resolved.");
            return false;
        }
        _hlPasses = built.Select(p => (p.Engine, p.Preset)).ToList();

        // Begin every pass up front (model-major runs interleave passes, so each pass's loggers
        // must stay open for the whole run). Any BeginRun throwing here (e.g. an unreachable
        // destination folder) is caught by OnHeadlessIdling before a single model opens.
        _hlStates = _hlPasses.Select(p => p.Engine.BeginRun()).ToArray();
        _hlModelCount = _hlPasses.Max(p => p.Preset.Models.Count);
        _hlModelIndex = 0;
        _hlPassIndex = 0;

        // Liveness watch for an UNATTENDED run: nobody is watching this Revit, so a cloud open
        // that never returns would hold the process (and its RAM, and its license) forever —
        // and in an auto-split run, one wedged group silently shrinks the batch with no marker
        // in the shared log. Here the watchdog may quit: there is no user mid-anything, and
        // everything already processed is flushed to CSV row by row.
        string watchLog = _hlPasses[0].Engine.TrackingLogPath;
        string watchName = job.Name;
        RunTrace.StartWatchdog(RunTrace.DefaultStallAfter, (stage, stuck) =>
        {
            RunNote.Write(watchLog, watchName, "", "ERROR",
                $"Stalled {(int)stuck.TotalMinutes} min at: {stage} — quitting this Revit instance. " +
                $"Remaining models in this run did not run. Trail: {RunTrace.LogPath}");
            RunTrace.Mark("quit (watchdog)");
            QuitRevit();
        });
        return true;
    }

    /// <summary>
    /// Shared teardown for both <see cref="EndHeadless"/> and <see cref="EndHeadlessKeepAlive"/>:
    /// unsubscribes the persistent headless handler (allowed mid-event), disposes the dialog
    /// suppressor, and clears the run's static state. Deciding whether to quit Revit is left to
    /// the caller.
    /// </summary>
    private static void EndHeadlessTeardown()
    {
        // First: the run may be ending mid-model (an error, or the last model just finished its
        // passes), and a document the cursor still holds would otherwise stay open — which matters
        // for "leave files open", whose caller re-opens the models right after this returns and
        // must not find one already open.
        _hlCursor.Release();
        RunTrace.StopWatchdog();   // the run is over; a late stall report would be a false alarm
        if (_hlApp != null && _hlHandler != null)
            _hlApp.Idling -= _hlHandler;
        _hlSuppressor?.Dispose();
        _hlSuppressor = null;
        _hlApp = null;
        _hlHandler = null;
        _hlJob = null;
        _hlPasses = null;
        _hlStates = null;
    }

    /// <summary>Unsubscribes the persistent headless handler (allowed mid-event) and requests
    /// Revit's normal close path.</summary>
    private static void EndHeadless()
    {
        EndHeadlessTeardown();
        QuitRevit();
    }

    /// <summary>
    /// Same teardown as <see cref="EndHeadless"/> but leaves Revit running — used for the "leave
    /// files open" disposition, whose caller surfaces the models (<see cref="LeftOpenDocsActivator.Surface"/>)
    /// right after this returns instead of quitting the process.
    /// </summary>
    private static void EndHeadlessKeepAlive() => EndHeadlessTeardown();

    /// <summary>
    /// Records a finished run: an ephemeral auto-split chunk deletes its own chunk file (it isn't a
    /// saved schedule, so there's nothing to stamp and nothing should appear in the schedule store),
    /// while a real saved job stamps <see cref="ScheduledJob.LastRunAt"/> and re-saves.
    /// </summary>
    private static void RecordRunFinished(ScheduledJob job)
    {
        if (job.IsEphemeralChunk)
        {
            ChunkJobStore.Delete(job.Id);
            return;
        }
        job.LastRunAt = DateTime.Now;
        ScheduleStore.Save(job);
    }

    private static void QuitRevit()
    {
        // One shared implementation for scheduled, interactive, broker, and diagnostic shutdown.
        // It posts WM_CLOSE only. If a modal dialog or genuine hang blocks that request, Revit is
        // intentionally left open for inspection; there is no timeout that terminates it.
        RevitShutdown.RequestCurrent("scheduled/headless batch complete");
    }

    /// <summary>
    /// Completion toast for an UNATTENDED run, then quit. The counts come from the manifest's own
    /// tracking log, which is the same file the supervisor reconciled against.
    ///
    /// <see cref="UI.RunNotification.ShowAndWait"/> verifies that Windows retained the item before
    /// this process requests its normal close, so the notification survives after Revit exits.
    /// </summary>
    private static void NotifyFinishedThenQuit(string runName, RunManifest manifest)
    {
        try
        {
            (int ok, int failed) = TrackingCsvReader.CountByStatus(manifest.TrackingLogPath);
            if (manifest.State == RunState.Stopped)
            {
                UI.RunNotification.ShowRunNeedsAttention(runName, ok, failed,
                    manifest.StopReason, manifest.TrackingLogPath);
            }
            else if (manifest.Unrecoverable.Count > 0)
            {
                UI.RunNotification.ShowRunStopped(runName, ok, failed,
                    RunSupervisor.MaxRounds, manifest.UnrecoverableDisplayNames(),
                    manifest.TrackingLogPath);
            }
            else
            {
                UI.RunNotification.ShowRunFinishedAndWait(runName, ok, failed,
                    manifest.TrackingLogPath);
            }
        }
        catch (Exception ex)
        {
            RunTrace.Mark("completion notification failed: " + ex.Message);
        }
        QuitRevit();
    }

    // ---- script-set jobs (Dynamo runner) ----------------------------------------------------
    // Kept alive in statics for the duration of the ExternalEvent round-trip, like the
    // self-test's driver probe.
    private static ExternalEvent? _hlScriptEvent;
    private static IExternalEventHandler? _hlScriptHandler;

    /// <summary>
    /// Builds the job's pass queue: one <see cref="BatchEngine"/> per <see cref="RunItem"/>, in the
    /// job's stored order — actions and script sets stay interleaved exactly as arranged (schema v3;
    /// the old shape stored two separate lists and always ran actions first). Script sets that no
    /// longer exist are returned in <paramref name="missingSets"/> for the caller to report rather
    /// than silently skipping.
    /// </summary>
    private static List<UI.BatchRunnerWindow.Pass> BuildJobPasses(
        UIApplication uiApp, ScheduledJob job, string destFolder, string projectLog,
        ModelPreset combined, bool interactive, out List<string> missingSets)
    {
        var actions = new List<IBatchAction>();
        missingSets = new List<string>();
        List<Scripts.ScriptSet>? sets = null; // loaded lazily, only if a script set is used

        foreach (RunItem item in job.RunItems)
        {
            if (item.Kind == RunItemKind.ScriptSet)
            {
                sets ??= Scripts.ScriptSetStore.LoadAll();
                // Resolve by stable id first (survives a rename), then by name for older jobs that
                // carry no RefId. Only report missing when neither matches.
                Scripts.ScriptSet? set =
                    (!string.IsNullOrWhiteSpace(item.RefId)
                        ? sets.FirstOrDefault(s => s.Id == item.RefId) : null)
                    ?? sets.FirstOrDefault(s => s.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (set == null)
                {
                    missingSets.Add(item.Name);
                    continue;
                }
                actions.Add(new RunScriptSetAction(set));
                continue;
            }

            // The REST-only steps are handled outside the per-model pipeline (see RestOnlySteps and
            // the callers above) — never turn one into a BatchEngine action.
            if (RestOnlySteps.IsRestOnlyItem(item)) continue;

            IBatchAction action;
            if (item.Name == InitiateToFormaAction.DisplayName)
            {
                // Built from the job's OWN snapshot (never the live input-set store) so a
                // headless run is self-contained. A malformed/absent snapshot is fatal here —
                // let it throw so the caller logs an ERROR rather than silently skipping publish.
                if (job.InitiatePublish is null)
                    throw new InvalidOperationException(
                        "Job selects 'Initiate to Forma' but carries no publish target snapshot.");
                action = new InitiateToFormaAction(job.InitiatePublish.ToPublishTarget());
            }
            else if (item.Name == CutModelAction.DisplayName)
            {
                // Same self-contained reasoning as Initiate — build from the job's own snapshot.
                if (job.CutModelPublish is null)
                    throw new InvalidOperationException(
                        "Job selects 'Cut model' but carries no publish target snapshot.");
                action = new CutModelAction(job.CutModelPublish.ToPublishTarget(), job.CutModelPublish.DeleteOtherLocations);
            }
            else if (item.Name == ExportNwcAction.DisplayName)
            {
                // Same self-contained reasoning as Initiate. Note what the ELSE at the bottom of
                // this chain would do without this branch: ActionRegistry.Create falls back to the
                // first registered action, so a scheduled Export NWC would quietly run Get Site
                // Location & PBP and report success.
                if (job.ExportNwcSpec is null)
                    throw new InvalidOperationException(
                        "Job selects 'Export NWC' but carries no options snapshot.");
                action = new ExportNwcAction(job.ExportNwcSpec, uiApp.Application.VersionNumber);
            }
            else if (item.Name == ExportIfcDataAction.DisplayName)
            {
                // Same as Export NWC above: without this branch the registry fallback would run the
                // first registered action instead and report success.
                if (job.ExportIfcSpec is null)
                    throw new InvalidOperationException(
                        "Job selects 'Export IFC + Data' but carries no options snapshot.");
                action = new ExportIfcDataAction(job.ExportIfcSpec);
            }
            else if (item.Name == BatchInteropAction.DisplayName)
            {
                // Built from the job's own snapshot, same reasoning as Initiate above — a
                // headless run must be self-contained. Missing AIT on the machine that runs
                // this is not fatal here: BatchEngine.ProcessModel's per-model catch turns it
                // into an ERROR row instead of aborting the whole job.
                if (string.IsNullOrWhiteSpace(job.InteropChecksetPath))
                    throw new InvalidOperationException(
                        "Job selects 'Batch Interop' but carries no checkset path.");
                action = new BatchInteropAction(
                    job.InteropChecksetPath, job.InteropChecksetExaminesLinks);
            }
            else if (item.Name == RelinkLinksAction.DisplayName)
            {
                if (job.RelinkSpec is null)
                    throw new InvalidOperationException(
                        "Job selects 'Internal Relink' but carries no relink input snapshot.");
                // Scan the spec's target folders now (headless-safe — token works headless) so the
                // pass is self-contained. Hosts are the job's models, not the spec's host fields.
                var client = new Forma.DataManagementClient(new Auth.SessionTokenProvider());
                Dictionary<string, List<RelinkTarget>> targets =
                    Commands.RelinkModelCommand.BuildTargetMap(client, job.RelinkSpec, out _);
                if (targets.Count == 0)
                    throw new InvalidOperationException(
                        "Internal Relink: no target models found in the spec's target folders.");
                action = new RelinkLinksAction(job.RelinkSpec.OldCode, job.RelinkSpec.NewCode,
                    job.RelinkSpec.Separator, job.RelinkSpec.MatchByExactName, targets);
            }
            else if (item.Name == CorrectWorksetAction.DisplayName)
            {
                if (job.CorrectWorksetSpec is null)
                    throw new InvalidOperationException(
                        "Job selects 'Correct Workset' but carries no input snapshot.");
                CorrectWorksetInputSet spec = job.CorrectWorksetSpec;
                var cw = new CorrectWorksetAction(spec.Rules, spec.ApplyLinkRule, spec.LinkKeyword,
                    spec.Fix, spec.ApplyCadRule, spec.CadWorksetName)
                {
                    // Interactive Run-now prompts per model; a headless/scheduled run has nobody to
                    // approve, so the callback stays null and every rename auto-approves (logged).
                    ApprovalCallback = interactive
                        ? (doc, proposals) =>
                        {
                            using var approval = new UI.WorksetRenameApprovalDialog(doc.Title, proposals);
                            approval.ShowDialog();
                        }
                        : null
                };
                action = cw;
            }
            else
            {
#if EDITION_DRW
                // A DRW action is a saved preset; one deleted since the schedule was saved is
                // reported and skipped like a missing script set, and the other items still run.
                try { action = ActionRegistry.Create(item.Name); }
                catch (InvalidOperationException)
                {
                    missingSets.Add(item.Name);
                    continue;
                }
#else
                action = ActionRegistry.Create(item.Name);
#endif
            }
            actions.Add(action);
        }

        // Build one engine per task now the count is known, so each stamps its "Task" column
        // "k of N". All passes share ONE run timestamp so their tracking rows go to a single
        // <dest>\Log\<stamp>_<project>_<schedule name>.csv file. For an auto-split run every sibling
        // chunk carries the SAME LogRunStamp, so all their instances resolve to that one unified file.
        DateTime runStamp = job.LogRunStamp ?? DateTime.Now;
        var passes = new List<UI.BatchRunnerWindow.Pass>();
        for (int i = 0; i < actions.Count; i++)
        {
            var engine = new BatchEngine(uiApp, actions[i], destFolder, projectLog, job.Sync,
                logNameRun: job.Name, taskIndex: i + 1, taskCount: actions.Count, runStamp: runStamp);
            passes.Add(new UI.BatchRunnerWindow.Pass(engine, combined));
        }
        return passes;
    }

    /// <summary>
    /// Headless run of a job that contains script sets — executes from an ExternalEvent's
    /// <c>Execute</c> (Dynamo hangs inside Idling) via the self-dismissing modal runner with the
    /// summary suppressed (no one to click it). The runner opens script-pass models ACTIVATED,
    /// which Dynamo needs. Completion mapping: LeaveOpen keeps Revit up with the models open;
    /// anything else runs as Close files (so LastRunAt can be stamped) and then quits Revit —
    /// the same "headless always exits" behaviour as the action-only path.
    /// </summary>
    private sealed class ModalJobHandler : IExternalEventHandler
    {
        private readonly ScheduledJob _job;
        public ModalJobHandler(ScheduledJob job) => _job = job;
        public string GetName() => "KhBatchRunner scheduled modal job";

        public void Execute(UIApplication app)
        {
            bool leaveOpen = _job.Completion == BatchCompletion.LeaveOpen;
            string destFolder = string.IsNullOrWhiteSpace(_job.DestinationFolder)
                ? DefaultDestinationFolder() : _job.DestinationFolder;
            string projectLog = string.IsNullOrWhiteSpace(_job.ProjectLogName) ? _job.Name : _job.ProjectLogName;
            try
            {
                // Mixed job (REST-only steps + script sets): run the REST pulls up front.
                foreach (string n in RestOnlySteps.Run(_job, destFolder, projectLog, StampOf(_job)))
                    WriteNote(destFolder, projectLog, _job.Name, n, "INFO");

                ModelPreset combined = ModelPresetCombiner.Combine(
                    _job.Name, _job.ModelSetIds, _job.ModelSetNames, _job.AdHocModels, out _);
                if (combined.Models.Count == 0)
                {
                    WriteNote(destFolder, projectLog, _job.Name, $"No models resolved for job '{_job.Name}'.");
                    return;
                }
                List<UI.BatchRunnerWindow.Pass> passes =
                    BuildJobPasses(app, _job, destFolder, projectLog, combined, interactive: false, out List<string> missingSets);
                if (missingSets.Count > 0)
                    WriteNote(destFolder, projectLog, _job.Name,
                        "Script set(s) or preset(s) not found, skipped: " + string.Join(", ", missingSets));
                if (passes.Count == 0)
                {
                    WriteNote(destFolder, projectLog, _job.Name, "Nothing to run — no action or script set resolved.");
                    return;
                }

                IReadOnlyList<UI.BatchRunnerWindow.PassResult> results = UI.BatchRunnerWindow.RunModal(
                    app, passes, $"Scheduled: {_job.Name}",
                    leaveOpen ? BatchCompletion.LeaveOpen : BatchCompletion.CloseDocuments,
                    showSummary: false,
                    // The scheduled parent gets the same visible bar/log as a normal batch and it
                    // closes itself. Auto-split child Revits stay invisible; their parent shows one
                    // unified RunWatchWindow instead of one dialog per group.
                    showProgress: !_job.IsEphemeralChunk);
                if (results.Count > 0)
                {
                    RecordRunFinished(_job);
                    if (!_job.IsEphemeralChunk && !leaveOpen)
                        UI.RunNotification.ShowRunFinished(_job.Name,
                            results.Sum(r => r.Result.SucceededCount),
                            results.Sum(r => r.Result.FailedCount),
                            // Every pass of one run shares a tracking file; the first names it.
                            results[0].TrackingLogPath);
                }
            }
            catch (Exception ex)
            {
                WriteNote(destFolder, projectLog, _job.Name, "Scheduled run failed: " + ex.Message);
            }
            finally
            {
                try { _hlScriptEvent?.Dispose(); } catch { /* best effort */ }
                _hlScriptEvent = null;
                _hlScriptHandler = null;
                if (!leaveOpen) QuitRevit();
            }
        }
    }

    /// <summary>
    /// Whether this job may be auto-split into parallel group instances. Beyond "a group size is
    /// set", the one hard incompatibility is <see cref="BatchCompletion.LeaveOpen"/>: every group
    /// is a separate Revit that must quit when its models are done, so a split run can never leave
    /// anything open. The dialogs now grey the box out for that disposition, but a schedule saved
    /// before that rule can still carry both — such a job used to split anyway and have its
    /// disposition silently overwritten per chunk. Run it single-process instead, and say so.
    /// </summary>
    /// <summary>Whether <paramref name="job"/> runs Batch Interop — the action that always gets a
    /// supervised child Revit, however few models it has.</summary>
    private static bool RunsInterop(ScheduledJob job) =>
        !string.IsNullOrWhiteSpace(job.InteropChecksetPath) &&
        job.ActionNames.Contains(BatchInteropAction.DisplayName);

    private static bool CanSplit(ScheduledJob job, IReadOnlyList<CloudModel> hosts,
        string destFolder, string projectLog, string revitVersionNumber)
    {
        if (job.GroupSize == 0) return false;

        // Batch Interop splits as of 1.3.7, but only where the Model Checker bridge is verified.
        string? interopReason = Interop.InteropSplitPolicy.BlockedReason(job, revitVersionNumber);
        if (interopReason != null)
        {
            WriteNote(destFolder, projectLog, job.Name,
                "Parallel groups skipped — " + interopReason +
                " Running all models in this one process instead.", "INFO");
            return false;
        }

        if (job.Completion == BatchCompletion.LeaveOpen)
        {
            WriteNote(destFolder, projectLog, job.Name,
                "Parallel groups skipped — 'Leave files open' needs a single Revit instance. " +
                "Running all models in this one process instead.", "INFO");
            return false;
        }

        // Internal Relink is safe to split unless one host is also one of its target link models.
        // The dedicated interactive command checks this before launch; scheduled/general Batch Run
        // must enforce the same rule here or the safety policy changes with the entry point.
        if (job.RelinkSpec != null && job.ActionNames.Contains(RelinkLinksAction.DisplayName))
        {
            try
            {
                var client = new Forma.DataManagementClient(new Auth.SessionTokenProvider());
                Dictionary<string, List<RelinkTarget>> targets =
                    Commands.RelinkModelCommand.BuildTargetMap(client, job.RelinkSpec, out _);
                string? reason = RelinkSplitGuard.SelfLinkingHost(hosts, targets);
                if (reason != null)
                {
                    WriteNote(destFolder, projectLog, job.Name,
                        "Parallel groups skipped — " + reason +
                        " Running all models in this one process instead.", "INFO");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Do not multiply a target-scan/auth failure across child processes. The ordinary
                // single-process path will report the same configuration error through its normal
                // action-building/logging flow.
                WriteNote(destFolder, projectLog, job.Name,
                    "Parallel groups skipped because Internal Relink targets could not be checked: " +
                    ex.Message + " Running in one Revit instead.", "INFO");
                return false;
            }
        }

        return true;
    }

    /// <summary>Fallback destination when a job names none. Internal so <see cref="ChunkLauncher"/>
    /// resolves the SAME folder the child instances will, and its group notes land in their log.</summary>
    /// <summary>Kept as a thin alias so this file's many call sites read unchanged; the definition
    /// now lives on <see cref="ScheduleStore"/>, which non-Revit hosts can reach.</summary>
    internal static string DefaultDestinationFolder() => ScheduleStore.DefaultDestinationFolder();

    /// <summary>
    /// Makes sure this run has an answer to "what can the signed-in Autodesk account open?", so
    /// <see cref="Presets.ModelPresetCombiner"/> can name inaccessible models before the run pays
    /// to discover them. Cheap when a window already warmed it; never throws; never blocks a run.
    /// </summary>
    private static void WarmAccountAccess()
    {
        try
        {
            if (Presets.AccountAccess.Cached is { Reachable: true }) return;
            Presets.AccountAccess.Refresh(new Forma.DataManagementClient(new Auth.SessionTokenProvider()));
        }
        catch { /* an unknown account is Unknown everywhere — the run proceeds exactly as before */ }
    }

    /// <summary>The run's shared stamp — the same value every pass of this run uses, so a step's
    /// rows land in the run's own tracking log rather than a file of their own. A chunk carries
    /// its parent's stamp; an ordinary run stamps itself. Mirrors the derivation in RunPasses.</summary>
    private static DateTime StampOf(ScheduledJob job) => job.LogRunStamp ?? DateTime.Now;

    /// <summary>
    /// The end of a job whose every step was REST-only (Get File Log, Get Data Extract).
    ///
    /// <para>Deliberately the SAME mechanism a model run uses — <c>ShowAndWait</c>, so Windows has
    /// accepted the item before Revit is allowed to exit, and the same retention split: a run that
    /// delivered expires on the hour, a run that did not waits to be dismissed. What differs is the
    /// body, which names what each step produced rather than a model-task count, because there were
    /// no model tasks.</para>
    /// </summary>
    private static void NotifyRestOnlyFinished(string runName, RestOnlyOutcome outcome)
    {
        try
        {
            UI.RunNotification.ShowAndWait(
                UI.RunNotification.FinishedTitle(runName, outcome.StepsFailed),
                outcome.Body,
                outcome.Failed,
                outcome.TrackingLog);
        }
        catch (Exception ex)
        {
            // A notification must never take down the run it reports on — and this one runs with
            // Revit's shutdown already requested, which is the worst possible moment to throw.
            RunTrace.Mark("REST-only finish notification failed: " + ex.Message);
        }
    }

    /// <summary>The same summary for an interactive "Run now", where a dialog is the right shape.</summary>
    private static void ShowRestOnlySummary(string title, RestOnlyOutcome outcome)
    {
        try
        {
            var dialog = new Autodesk.Revit.UI.TaskDialog(title)
            {
                MainInstruction = outcome.Failed ? "Finished — with something to look at." : "Finished.",
                MainContent = string.Join("\n\n", outcome.Summaries)
            };
            if (outcome.TrackingLog.Length > 0)
                dialog.ExpandedContent = "Log: " + outcome.TrackingLog;
            dialog.Show();
        }
        catch (Exception ex)
        {
            RunTrace.Mark("REST-only summary dialog failed: " + ex.Message);
        }
    }
    private static void WriteNote(string destFolder, string projectLog, string model, string message, string status = "ERROR")
    {
        string path = TrackingCsvLogger.PathFor(destFolder, projectLog, "ScheduleError", DateTime.Now);
        var logger = new TrackingCsvLogger(path, "ScheduleError") { CurrentModel = model };
        logger.Log(new Dictionary<string, string> { ["Status"] = status, ["Message"] = message });
    }
}
