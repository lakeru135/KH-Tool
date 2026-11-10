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
public static partial class ScheduledRunner
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
}
