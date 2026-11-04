using System.IO;
using System.Threading;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;

// Disambiguated from System.Diagnostics, which this file also uses indirectly.
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Watches the Revit instances an auto-split run launched, and makes the run actually finish.
///
/// Before this existed, <see cref="ChunkLauncher"/> started N instances and returned: nothing waited
/// on them, and a group whose Revit died or wedged simply took its models with it. The tracking CSV
/// records only models that FINISHED, so the loss was invisible — 18 models across 3 windows coming
/// back as 6/5/3 looked exactly like a completed run.
///
/// The supervisor closes that gap:
/// <list type="bullet">
/// <item>it holds the intended assignment (<see cref="RunManifest"/>) so intent can be diffed
/// against outcome;</item>
/// <item>if an instance stops progressing, it stops new launches and leaves that Revit open for
/// inspection (<see cref="ChildLiveness"/>);</item>
/// <item>when a round's instances have all exited it reruns whatever produced no row, in at most
/// <see cref="MaxRounds"/> rounds, then records what is beyond saving.</item>
/// </list>
///
/// It runs on a plain background <b>thread</b>, never the Revit API thread and never an Idling
/// handler: everything it touches is processes, files and the log, and the API thread is precisely
/// the one that may be blocked by the very hang it is meant to notice.
///
/// Retries cover MISSING rows only. An ERROR row is a result — the model opened and genuinely
/// failed — and rerunning it would only fail again three times more slowly.
/// </summary>
internal sealed class RunSupervisor
{
    /// <summary>
    /// Round 1 is the user's group size; 2 re-chunks the survivors at that SAME size; 3 re-chunks
    /// them to fit ONE wave of <see cref="InstanceBudget.MaxConcurrent"/> instances; 4 gives each
    /// remaining model an instance to itself.
    ///
    /// Round 3 was inserted after a 98-model run went 10 groups → 6 → <b>31</b>: jumping straight
    /// from the user's size to full isolation multiplies the instance count by the number of
    /// survivors, and on a laptop that starves every one of them. Re-shaping to the machine first
    /// gives a model that failed under contention a genuine second chance before the expensive
    /// one-at-a-time round.
    /// </summary>
    public const int MaxRounds = 4;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly RunManifest _manifest;
    private readonly RunTrace.Tail _tail = new();
    private readonly Dictionary<int, ChildLiveness.Watch> _watches = new();
    private readonly Action<RunManifest>? _onFinished;
    private readonly bool _showRoundNotifications;
    private Thread? _thread;

    private RunSupervisor(RunManifest manifest, Action<RunManifest>? onFinished,
        bool showRoundNotifications)
    {
        _manifest = manifest;
        _onFinished = onFinished;
        _showRoundNotifications = showRoundNotifications;
    }

    /// <summary>
    /// Begins supervising <paramref name="manifest"/>'s first round on a background thread.
    /// <paramref name="onFinished"/> runs on that same thread once the run is complete (every model
    /// has a row or has exhausted its attempts) — the caller uses it to apply its own post-run
    /// disposition, e.g. a headless parent quitting Revit only now rather than at launch time.
    /// </summary>
    public static RunSupervisor Start(RunManifest manifest, Action<RunManifest>? onFinished = null,
        bool showRoundNotifications = true)
    {
        var supervisor = new RunSupervisor(manifest, onFinished, showRoundNotifications);
        supervisor._thread = new Thread(supervisor.Loop)
        {
            IsBackground = true,   // never keep the process alive on its own; the caller decides that
            Name = "KhBatchRunner run supervisor"
        };
        supervisor._thread.Start();
        return supervisor;
    }

    /// <summary>Blocks until the run finishes or <paramref name="timeout"/> elapses. Used by a
    /// headless parent, whose only remaining job is to outlive its children.</summary>
    public bool WaitForCompletion(TimeSpan timeout) => _thread?.Join(timeout) ?? true;

    private void Loop()
    {
        try
        {
            RunTrace.Mark($"supervisor started run={_manifest.RunId} round=1 " +
                          $"groups={_manifest.CurrentRound?.Groups.Count ?? 0}");
            AdoptCurrentRound();
            if (_showRoundNotifications) AnnounceRound(1);

            while (true)
            {
                Thread.Sleep(PollInterval);
                _tail.Poll();                       // one read of the shared trail for all children

                PolicePace();
                if (_manifest.State == RunState.Stopped) break;
                FillFreeSlots();                    // start queued groups as running ones exit
                if (!RoundFinished()) continue;

                if (!StartNextRoundIfNeeded()) break;
                AdoptCurrentRound();
                if (_showRoundNotifications) AnnounceRound(_manifest.RoundNumber);
            }
        }
        catch (Exception ex)
        {
            // A supervisor that throws must not leave the manifest claiming to be Running forever,
            // and must not take the run's own result with it.
            RunTrace.Mark("supervisor FAILED: " + ex.Message);
            StopRun("Recovery supervisor failed: " + ex.Message);
            Note("ERROR", "Recovery supervisor failed: " + ex.Message +
                          " — any unfinished models were not retried.");
        }
        finally
        {
            CleanPendingChunks();
            if (_manifest.State == RunState.Running)
                RunManifestStore.Update(_manifest, m => m.State = RunState.Completed);

            // The one place a split run is known to be OVER. Each chunk saw only its own group, so
            // this is also the only place the whole run's matrix can be pivoted — the chunks
            // deliberately did not write their own (Reporting.MatrixUtil.WritesHere). Every split
            // launch path — Batch Run, the Multi Model buttons, InteractiveSplit and the scheduled
            // runner — arrives here, which is the same reason the run notifications were moved to a
            // single choke point in 1.3.7 rather than wired per entry point.
            BuildRunEndMatrices();

            RunTrace.Mark("supervisor finished run=" + _manifest.RunId);
            try { _onFinished?.Invoke(_manifest); } catch { /* caller's disposition is its own problem */ }
        }
    }

    /// <summary>
    /// Deletes jobs that were prepared but never launched. Live workers are never closed here: a
    /// stopped run intentionally preserves them for inspection, and a completed run reaches this
    /// point only after its workers have exited naturally.
    /// </summary>
    private void CleanPendingChunks()
    {
        foreach (RunGroup g in _manifest.Rounds.SelectMany(r => r.Groups))
        {
            // A group still queued when the run ends (the round was abandoned after an error) has a
            // chunk job on disk and no process. Drop the file now rather than leaving a job that a
            // future Revit could in principle pick up, and that the orphan sweep would only clear
            // twelve hours later.
            if (IsPending(g))
            {
                ChunkJobStore.Delete(g.ChunkId);
                continue;
            }
            if (g.Pid != 0 && ChildLiveness.IsAlive(g.Pid) && _manifest.State == RunState.Stopped)
                RunTrace.Mark($"supervisor left pid={g.Pid} open for inspection");
        }
    }

    /// <summary>
    /// Supervision for an INTERACTIVE launch. The parent Revit stays usable while its children run;
    /// when everything is done the children are closed, and the parent honours the user's own
    /// "When finished" choice — quitting only if they asked for "Close Revit when complete" (which
    /// is now the default for a parallel run).
    ///
    /// This is also where the run's overall progress window is opened, because "interactive" is
    /// exactly the condition that means somebody is there to read it.
    /// </summary>
    public static RunSupervisor StartInteractive(RunManifest manifest, Batch.BatchCompletion completion)
    {
        // The progress window belongs HERE, not at the call sites. There are four interactive
        // split launches — Batch Run, the Multi Model Check/Action buttons, Batch Interop, and
        // Schedule's "Run now" — and opening the window at one of them left the other three with
        // no progress display anywhere, which is the "the bar never appears" report. Every
        // interactive split reaches this method, so this is the one place it cannot be missed.
        // ShowFor never throws: failing to display progress must not fail the run.
        bool quitWhenDone = completion == Batch.BatchCompletion.CloseRevit;
        UI.RunWatchWindow? watch = UI.RunWatchWindow.ShowFor(manifest, quitWhenDone);

        return Start(manifest, finished =>
        {
            // A stopped run has a live worker that needs inspection. Do not close the supervising
            // Revit too; the user may need it to review the retained notification and run log.
            if (finished.State != RunState.Completed) return;
            if (!quitWhenDone) return;

            // The progress window owns completion while it remains open (scheduled mode closes it
            // automatically). If there is no window because it failed to open or the user closed
            // it mid-run, request the same normal Revit close here. Never terminate the process.
            if (watch is { IsDisposed: false }) return;

            RunTrace.Mark("supervisor requesting normal close of the launching Revit");
            RevitShutdown.RequestCurrent("interactive split run complete");
        });
        // Round notifications stay ON (suppressed here until 1.3.5, and the scheduled path was
        // fixed one version earlier while this one was missed). A retry round is the moment a run
        // stops going to plan; an interactive split has a progress window, but it is behind Revit
        // by the time anything interesting happens.
    }

    /// <summary>
    /// Toasts the start of a round with the shape of its split — "4 / 4 / 4". A retry round is the
    /// moment a run stops going to plan, and until now it happened silently: the only trace was a
    /// line in a CSV nobody reads while a batch is still going.
    ///
    /// Safe on this background thread because <see cref="UI.RunNotification.ShowAndWait"/> brings
    /// its own message pump; it blocks for about a second, against a 15-second poll interval.
    /// </summary>
    private void AnnounceRound(int round)
    {
        try
        {
            var sizes = (_manifest.CurrentRound?.Groups ?? new List<RunGroup>())
                .Select(g => g.Models.Count).ToList();
            if (sizes.Count == 0) return;
            UI.RunNotification.ShowRoundStarted(_manifest.Name, round, MaxRounds, sizes,
                                                EffectiveConcurrencyCap());
        }
        catch (Exception ex)
        {
            RunTrace.Mark("round notification failed: " + ex.Message);
        }
    }

    /// <summary>The groups of the round in flight.</summary>
    /// <summary>
    /// The current round's groups, READ-ONLY.
    ///
    /// <para>Read-only by type since 1.4.2.1, and that is the whole point. This used to hand back the
    /// live <see cref="List{T}"/>, and <see cref="RelaunchIfCrashed"/> appended a replacement to it
    /// while <see cref="PolicePace"/> was in the middle of <c>foreach</c>-ing it. Every run that lost
    /// a child then died with "Collection was modified; enumeration operation may not execute",
    /// which the supervisor's own catch turned into a stopped run — the exact outcome 1.4.2 existed
    /// to remove. Twice on 2026-08-18 (report <c>BKKDDCJ74R3_20260818_1015</c>).</para>
    ///
    /// <para>A comment saying "do not add to this" would have been worth nothing; the signature is
    /// worth something. Groups are added through <see cref="AddGroups"/>, which the caller can only
    /// reach after its loop has ended.</para>
    /// </summary>
    private IReadOnlyList<RunGroup> CurrentGroups() =>
        (IReadOnlyList<RunGroup>?)_manifest.CurrentRound?.Groups ?? Array.Empty<RunGroup>();

    /// <summary>Appends groups to the current round and persists it. The only way to grow a round —
    /// see <see cref="CurrentGroups"/> for what happened when there were two.</summary>
    private void AddGroups(IReadOnlyList<RunGroup> extra)
    {
        if (extra.Count == 0) return;
        List<RunGroup>? live = _manifest.CurrentRound?.Groups;
        if (live == null) return;
        RunManifestStore.Update(_manifest, _ => live.AddRange(extra));
    }

    /// <summary>Running = a process was started and has not been seen to exit.</summary>
    private static bool IsRunning(RunGroup g) => g.Pid != 0 && g.ExitedAt == null;

    /// <summary>Pending = prepared, queued for a slot, no process yet. See <see cref="RunGroup"/>.</summary>
    private static bool IsPending(RunGroup g) => g.Pid == 0 && g.ExitedAt == null && !g.LaunchFailed;

    /// <summary>Creates a liveness watch for one live instance.</summary>
    private void Adopt(RunGroup g)
    {
        if (!IsRunning(g)) return;
        _watches[g.Pid] = new ChildLiveness.Watch
        {
            Pid = g.Pid,
            RevitVersion = _manifest.RevitVersion,
            StartedAt = g.StartedAt,
            // The stagger this instance was told to observe. Excluded from its clocks, so a child
            // is never judged for waiting when waiting is the instruction.
            FirstOpenDelaySeconds = g.FirstOpenDelaySeconds
        };
    }

    /// <summary>Creates a liveness watch for each instance of the round that just started.</summary>
    private void AdoptCurrentRound()
    {
        _watches.Clear();
        foreach (RunGroup g in CurrentGroups()) Adopt(g);
    }

    /// <summary>
    /// Starts queued groups as running ones exit, keeping at most
    /// <see cref="InstanceBudget.MaxConcurrent"/> Revits alive at a time.
    ///
    /// This is the other half of the cap: <see cref="ChunkLauncher"/> starts the first wave, and
    /// everything after it starts here. Without it a round would simply never finish, because the
    /// pending groups it prepared would sit on disk forever.
    /// </summary>
    private void FillFreeSlots()
    {
        IReadOnlyList<RunGroup> groups = CurrentGroups();
        foreach (int i in SlotsToFill(groups, EffectiveConcurrencyCap(), g => !IsDoneBarExiting(g)))
        {
            RunGroup g = groups[i];
            bool ok = ChunkLauncher.StartPending(_manifest, g, i + 1, groups.Count, out string? err);
            RunManifestStore.Save(_manifest);   // Pid/StartedAt, or LaunchFailed, now matter
            if (ok)
                Adopt(g);
            else
                Note("ERROR", $"group {i + 1} of {groups.Count} could not start when a slot freed: " +
                              $"{err} — {g.Models.Count} model(s) did not run.");
        }
    }

    private int EffectiveConcurrencyCap() =>
        Math.Max(1, RecordedConcurrencyCap() - StrandedInstances());

    private int RecordedConcurrencyCap() => _manifest.MaxConcurrentInstances > 0
        ? _manifest.MaxConcurrentInstances
        // Fallback for a manifest written before the cap was recorded: re-derive it at the job's
        // own weight, so a retry round of an Interop run cannot start more instances than round 1.
        : InstanceBudget.MaxConcurrent(
            _manifest.AllModels, InstanceBudget.WeightFor(_manifest.BaseJob));

    /// <summary>
    /// Deferred workers from EARLIER rounds that are still alive — left open for inspection by
    /// <see cref="DeferForAttention"/> and still holding a whole Revit's worth of memory.
    ///
    /// <para>They are not in <see cref="CurrentGroups"/>, so the slot arithmetic cannot see them, and
    /// without this a run that set two children aside in round 1 would start a FULL wave in round 2
    /// on top of them. Deferring instead of stopping is only safe if the machine's budget still
    /// counts what was deferred; otherwise 1.4.2 would trade a cancelled run for an oversubscribed
    /// one, which is the thing <see cref="InstanceBudget"/> exists to prevent.</para>
    /// </summary>
    private int StrandedInstances() =>
        StrandedCount(_manifest.Rounds, _manifest.RoundNumber, g => ChildLiveness.IsAlive(g.Pid));

    /// <summary>
    /// Indices of the pending groups that should start right now: enough to bring the number of
    /// live instances up to <paramref name="cap"/>, in order, and no more.
    ///
    /// Pure and separate from the launching so it can be tested without starting processes — the
    /// arithmetic here is the part with the off-by-one that would either over-subscribe the machine
    /// (the bug being fixed) or leave a queue that never drains (the way to break the fix).
    /// </summary>
    /// <param name="occupiesSlot">Whether a running group still counts against the cap. The caller
    /// passes "not already shutting down", so an instance that has finished its models and is only
    /// slow to exit stops holding a queued group behind it — a Revit tearing down is releasing
    /// memory, not competing for it. Defaults to "every running group counts".</param>
    internal static List<int> SlotsToFill(IReadOnlyList<RunGroup> groups, int cap,
        Func<RunGroup, bool>? occupiesSlot = null)
    {
        var start = new List<int>();
        int running = groups.Count(g => IsRunning(g) && (occupiesSlot?.Invoke(g) ?? true));
        for (int i = 0; i < groups.Count && running + start.Count < Math.Max(1, cap); i++)
            if (IsPending(groups[i])) start.Add(i);
        return start;
    }

    /// <summary>
    /// Sets aside children that need attention, records natural process exits, and replaces the
    /// ones that crashed.
    ///
    /// <para>Replacements are COLLECTED here and appended after the loop, never inside it. Appending
    /// to the round mid-<c>foreach</c> is what broke every 1.4.2 run that lost a child — see
    /// <see cref="CurrentGroups"/>. The read-only return type stops it happening again; this shape is
    /// what makes the read-only type sufficient rather than merely annoying.</para>
    /// </summary>
    private void PolicePace()
    {
        var replacements = new List<RunGroup>();

        foreach (RunGroup group in CurrentGroups())
        {
            if (!IsRunning(group)) continue;

            if (!ChildLiveness.IsAlive(group.Pid))
            {
                ReportUnexpectedExit(group);
                // Before the replacement is planned: a model given up on here gets its ERROR row now,
                // so the replacement does not carry it, and a model retried here goes LAST in it.
                string? crashedOpening = HandleOpenCrash(group);
                RunManifestStore.Update(_manifest, _ => group.ExitedAt = DateTime.Now);
                _watches.Remove(group.Pid);
                PlanReplacement(group, replacements, crashedOpening);
                continue;
            }

            if (!_watches.TryGetValue(group.Pid, out ChildLiveness.Watch? watch)) continue;

            ChildLiveness.Judgement judgement = ChildLiveness.Check(watch, _tail, group.Models);
            switch (judgement.Verdict)
            {
                case ChildLiveness.Verdict.Wedged:
                case ChildLiveness.Verdict.OverCeiling:
                case ChildLiveness.Verdict.NeverStarted:
                    DeferForAttention(group, judgement);
                    continue;

                // Its models all ran; it is only slow to disappear. Say so once and carry on —
                // stopping the run here would cancel every queued group over an instance that has
                // nothing left to do, which is exactly what happened on 2026-08-11.
                case ChildLiveness.Verdict.ShutdownStalled:
                    if (!group.SlowExitReported)
                    {
                        RunManifestStore.Update(_manifest, _ => group.SlowExitReported = true);
                        RunTrace.Mark($"supervisor: pid {group.Pid} {judgement.Reason} — " +
                                      "continuing, its models are done");
                        Note("INFO", $"pid {group.Pid} {judgement.Reason}. Its models all ran and " +
                                     "are in this log; the run carries on.");
                    }
                    break;

                // Slow inside a document open. Also reported and carried on with, for the same
                // reason: on 2026-08-13 an instance stopped here at 10 minutes went on to open the
                // model at 15m55s and finish all six of its models. A genuinely hung open is caught
                // by the per-model ceiling, which does stop the run.
                case ChildLiveness.Verdict.OpenSlow:
                    if (!string.Equals(group.SlowOpenReportedFor, watch.LastStage,
                                       StringComparison.Ordinal))
                    {
                        string stage = watch.LastStage;
                        RunManifestStore.Update(_manifest, _ => group.SlowOpenReportedFor = stage);
                        RunTrace.Mark($"supervisor: pid {group.Pid} {judgement.Reason} — " +
                                      "continuing, an open is allowed to be slow");
                        Note("INFO", $"pid {group.Pid} {judgement.Reason}. Nothing has been stopped; " +
                                     "the run carries on and this model is still opening.");
                    }
                    break;

                // Slow inside an ACTION. Same treatment as a slow open, and the same evidence
                // behind it: on 2026-08-18 an instance stopped here at ten minutes finished the
                // action at 26 minutes and reported 36 ok, 0 failed — after its run had been
                // cancelled. The per-model ceiling still catches an action that never returns.
                case ChildLiveness.Verdict.ActionSlow:
                    if (!string.Equals(group.SlowActionReportedFor, watch.LastStage,
                                       StringComparison.Ordinal))
                    {
                        string actionStage = watch.LastStage;
                        RunManifestStore.Update(_manifest, _ => group.SlowActionReportedFor = actionStage);
                        RunTrace.Mark($"supervisor: pid {group.Pid} {judgement.Reason} — " +
                                      "continuing, an action is allowed to be slow");
                        Note("INFO", $"pid {group.Pid} {judgement.Reason}. Nothing has been stopped; " +
                                     "the run carries on and this action is still running.");
                    }
                    break;
            }
        }

        // Only now that nothing is enumerating the round.
        if (replacements.Count > 0)
        {
            AddGroups(replacements);
            FillFreeSlots();   // the crashed children freed the slots these need
        }
    }

    /// <summary>
    /// A group counts as finished for round bookkeeping once its child has asked to close, even
    /// while the process lingers.
    ///
    /// Without this a slow exit holds a concurrency slot — and therefore a queued group — for as
    /// long as the shutdown takes, which on the 2026-08-11 report was over ten minutes with nothing
    /// happening. The models are already logged, so nothing is lost by letting the next group start;
    /// the exiting Revit is releasing memory, not claiming it.
    ///
    /// <para>Deliberately NOT true of a group deferred by <see cref="DeferForAttention"/>, even
    /// though that group is also never going to finish. This predicate answers "may its slot be
    /// reused", and a deferred Revit is alive and still holding its memory, where an exiting one is
    /// releasing it. <see cref="RoundFinished"/> asks the other question and does count it.</para>
    /// </summary>
    private bool IsDoneBarExiting(RunGroup group) =>
        group.SlowExitReported ||
        (_watches.TryGetValue(group.Pid, out ChildLiveness.Watch? w) &&
         ChildLiveness.IsShuttingDown(w.LastStage));

    /// <summary>How many times one group may have its Revit replaced within a round.</summary>
    /// <remarks>
    /// ONE. The thing being recovered from is a crash inside the first cloud open, which is
    /// frequent — 10 of 31 first opens on 2026-08-18 — but also, for some models, perfectly
    /// repeatable: <c>SITE2A-BLDA-ELE.rvt</c> took its Revit down on all four attempts that night,
    /// including twice in isolation. Unbounded replacement would turn a reproducible crash into a
    /// launch loop. Past the bound the escalation ladder takes over, which is what it is for.
    /// </remarks>
    internal const int MaxRelaunchesPerGroup = 1;

    /// <summary>
    /// Which of a dead group's models a replacement should carry — the ones with no complete row —
    /// or an empty list when there is nothing to do or the group has used up its replacements.
    ///
    /// <para>Pure, and separate from the launching, for the same reason as <see cref="SlotsToFill"/>:
    /// the two ways to get this wrong are re-running models that already finished (duplicate rows in
    /// the tracking log the whole run is judged from) and launching forever against a model that
    /// crashes Revit every time. Neither belongs in code that can only be exercised by starting a
    /// Revit.</para>
    /// </summary>
    /// <param name="completed">Display names with a complete row set —
    /// <see cref="TrackingCsvReader.CompletedModels"/>, the same source <see cref="MissingModels"/>
    /// reads, so a replacement and a retry round can never disagree about what is left.</param>
    /// <param name="last">The model Revit died opening, when known: it goes at the END, so the
    /// models it was blocking finish before it is tried again.</param>
    internal static List<CloudModel> RelaunchModels(RunGroup group, ISet<string> completed,
        int maxRelaunches, string? last = null)
    {
        if (group.RelaunchCount >= maxRelaunches) return new List<CloudModel>();
        List<CloudModel> left = group.Models.Where(m => !completed.Contains(m.DisplayName)).ToList();
        if (last == null) return left;
        return left.Where(m => m.DisplayName != last)
                   .Concat(left.Where(m => m.DisplayName == last))
                   .ToList();
    }

    /// <summary>What to do about a model Revit died while opening.</summary>
    internal enum OpenCrashOutcome
    {
        /// <summary>Known workshared: today's retries, unchanged (its first-open crashes have been
        /// transient — on 2026-08-15 one ran clean the next round).</summary>
        Ladder,
        /// <summary>Once more, opened plain, last in a fresh Revit.</summary>
        Retry,
        /// <summary>It has already had its plain retry this run: an ERROR row, and the run moves on.</summary>
        GiveUp,
    }

    /// <summary>
    /// The user's rule (2026-11-03): a model that crashes Revit on open is retried ONCE in safe open —
    /// no workset configuration — and if it crashes again it is skipped with an ERROR while every other
    /// model carries on. Revit-free so the harness pins it.
    /// </summary>
    internal static OpenCrashOutcome OpenCrashPolicy(int priorCrashesThisRun, bool knownWorkshared) =>
        knownWorkshared ? OpenCrashOutcome.Ladder
        : priorCrashesThisRun == 0 ? OpenCrashOutcome.Retry
        : OpenCrashOutcome.GiveUp;

    /// <summary>
    /// A dead child whose last line was a model OPEN: record the crash against that model, and either
    /// arrange its plain retry or give it up — see <see cref="OpenCrashPolicy"/>. Returns the model's
    /// display name when it is retried (so the replacement carries it last), else null.
    /// </summary>
    private string? HandleOpenCrash(RunGroup group)
    {
        try
        {
            _watches.TryGetValue(group.Pid, out ChildLiveness.Watch? watch);
            string? lastStage = _tail.For(group.Pid)?.Stage ?? watch?.LastStage;
            string? name = Batch.BatchStages.OpeningModel(lastStage);
            if (name == null) return null;
            CloudModel? model = group.Models.FirstOrDefault(m => string.Equals(m.DisplayName, name, StringComparison.Ordinal));
            if (model == null || model.IsLocal) return null;

            string key = ModelKey.For(model);
            int prior = _manifest.OpenCrashes.TryGetValue(key, out int n) ? n : 0;
            RunManifestStore.Update(_manifest, x => x.OpenCrashes[key] = prior + 1);
            bool workshared = Batch.CloudOpenFacts.MarkCrash(model);
            bool wasPlain = Batch.BatchStages.IsPlainOpenStage(lastStage);
            RunTrace.Mark($"supervisor: pid {group.Pid} died opening {name} " +
                          $"({(wasPlain ? "plain open" : "workset configuration")}; crash {prior + 1} this run)");

            switch (OpenCrashPolicy(prior, workshared))
            {
                case OpenCrashOutcome.Retry:
                    Note("INFO", $"Revit died while opening {name}" +
                                 (wasPlain ? " (already a plain open)" : "") +
                                 ". It is retried once in a fresh Revit, after the other models, opened " +
                                 "plain — no workset configuration. If that crashes too it is skipped.");
                    return name;

                case OpenCrashOutcome.GiveUp:
                    RunManifestStore.Update(_manifest, x => { if (!x.Unrecoverable.Contains(key)) x.Unrecoverable.Add(key); });
                    RunNote.Write(_manifest.TrackingLogPath, _manifest.Name, name, "ERROR",
                        $"skipped — Revit crashed while opening this model {prior + 1} times this run, " +
                        "the last time in safe open (no workset configuration). The other models carried on. " +
                        $"See {RunTrace.LogPath} and the Revit journal ('captureTryCrash') for the cause.");
                    return null;

                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            RunTrace.Mark($"open-crash handling for pid {group.Pid} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Live workers set aside in EARLIER rounds — see <see cref="StrandedInstances"/> for why they
    /// have to be subtracted from the cap. Pure form, so the arithmetic that keeps 1.4.2 from
    /// trading a cancelled run for an oversubscribed one can be checked without processes.
    /// </summary>
    internal static int StrandedCount(IEnumerable<RunRound> rounds, int currentRound,
        Func<RunGroup, bool> isLive)
    {
        int stranded = 0;
        foreach (RunRound round in rounds)
        {
            if (round.Round == currentRound) continue;
            foreach (RunGroup g in round.Groups)
                if (g.NeedsAttention && IsRunning(g) && isLive(g)) stranded++;
        }
        return stranded;
    }

    /// <summary>
    /// Starts a replacement for a group whose Revit vanished, instead of leaving its models to wait
    /// for the round to end.
    ///
    /// <para><b>The wait was the cost.</b> On 2026-08-18 pid 87800 died inside its first cloud open
    /// at 00:53:47; its five models did not run again until the round ended and was re-chunked at
    /// 01:21:39 — <b>28 minutes</b> in which a slot sat empty and nothing was retried. 1.4.1's larger
    /// groups raised the stake from two models to five or ten.</para>
    ///
    /// <para>Only the models with no complete row are re-sent, read from the same tracking log
    /// <see cref="MissingModels"/> uses — a child that crashed on model 7 of 10 must not redo the
    /// first six.</para>
    /// </summary>
    /// <param name="into">Where the replacement goes. NOT the round — <see cref="PolicePace"/> is
    /// enumerating that, and appending to it here is the defect this signature exists to prevent.</param>
    private void PlanReplacement(RunGroup group, List<RunGroup> into, string? crashedOpening = null)
    {
        try
        {
            if (_manifest.State != RunState.Running) return;
            if (group.NeedsAttention) return;             // set aside on purpose, not a crash

            // Asked to close and then went: a finish, however abrupt. ReportUnexpectedExit applies
            // the same test before it calls anything a crash.
            _watches.TryGetValue(group.Pid, out ChildLiveness.Watch? watch);
            string? lastStage = _tail.For(group.Pid)?.Stage ?? watch?.LastStage;
            if (ChildLiveness.IsShuttingDown(lastStage)) return;

            if (group.RelaunchCount >= MaxRelaunchesPerGroup)
            {
                Note("INFO", $"the replacement for pid {group.Pid}'s group has also gone. Not " +
                             "starting another; its remaining models go to the next round.");
                return;
            }


            HashSet<string> done = TrackingCsvReader.CompletedModels(_manifest.TrackingLogPath);
            List<CloudModel> missing = RelaunchModels(group, done, MaxRelaunchesPerGroup, crashedOpening);
            if (missing.Count == 0) return;               // nothing left, or past the bound

            RunGroup? replacement = ChunkLauncher.PrepareReplacement(
                _manifest.BaseJob!, missing, _manifest.RunStamp,
                CurrentGroups().Count + into.Count + 1, out string? error);
            if (replacement == null)
            {
                Note("INFO", $"could not prepare a replacement for pid {group.Pid}'s group " +
                             $"({error}); its models go to the next round instead.");
                return;
            }

            replacement.RelaunchCount = group.RelaunchCount + 1;
            into.Add(replacement);
            RunTrace.Mark($"supervisor replacing pid {group.Pid}'s group: {missing.Count} model(s) " +
                          "still to run");
            Note("INFO", $"pid {group.Pid} is gone with {missing.Count} model(s) unfinished — a " +
                         "fresh Revit takes them now rather than at the end of the round: " +
                         string.Join("; ", missing.Select(m => m.DisplayName)));
        }
        catch (Exception ex)
        {
            RunTrace.Mark($"replacement for pid {group.Pid} could not be started: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets one group aside and records WHY, leaving its worker open for inspection — and lets the
    /// REST OF THE RUN CARRY ON.
    ///
    /// <para><b>It used to stop everything (until 1.4.2).</b> One child needing attention set
    /// <see cref="RunState.Stopped"/>, which cancelled every queued group and every retry round. On
    /// 2026-08-18 that policy fired twice in one night and <b>both times it was wrong</b>: once on a
    /// child blocked by a Revit startup dialog (a 2026-08-18 run — four of ten groups never launched), and once
    /// on a child that was simply mid-action and went on to report <c>36 ok, 0 failed</c> (Weekly
    /// SITE3A — its retry round cancelled). The evidence for "this instance is unwell" was never
    /// evidence about the other nine.</para>
    ///
    /// <para>What is deliberately NOT relaxed: the worker is still left open rather than closed, and
    /// it still <b>holds its concurrency slot</b>. It is alive and holding gigabytes; handing its
    /// slot to a queued group would oversubscribe the machine, which is the failure
    /// <see cref="InstanceBudget"/> exists to prevent. The run continues with one slot fewer.</para>
    ///
    /// <para>Its models simply have no complete rows, so <see cref="MissingModels"/> collects them
    /// for the next round with no extra bookkeeping — and
    /// <see cref="IsDoneBarExiting"/> is what lets the round reach its end at all, since this child
    /// will never exit on its own.</para>
    ///
    /// The reason comes from <see cref="ChildLiveness.Judgement"/> rather than being reconstructed
    /// here. The old version reported <c>now - LastProgressAt</c> for every verdict, including
    /// ceiling force-closes, which produced the uninterpretable "no progress for 0 sec" against instances
    /// that were demonstrably mid-<c>run</c> — and hid a bug that cost a whole test cycle.
    /// </summary>
    private void DeferForAttention(RunGroup group, ChildLiveness.Judgement judgement)
    {
        if (group.NeedsAttention) return;   // already set aside; say it once

        string label = judgement.Verdict switch
        {
            ChildLiveness.Verdict.OverCeiling => "over ceiling",
            ChildLiveness.Verdict.NeverStarted => "never started",
            _ => "wedged"
        };

        string reason = $"pid {group.Pid} needs attention ({label}): {judgement.Reason}";
        RunTrace.Mark("supervisor setting group aside; worker left open — " + reason);
        RunManifestStore.Update(_manifest, _ =>
        {
            group.NeedsAttention = true;
            group.AttentionReason = reason;
        });
        Note("ERROR", reason + ". Revit was left open for inspection and this group's models will " +
                      "be retried in the next round; the rest of the run carries on.");

        // The one case that IS about the machine rather than about one instance: nothing healthy is
        // left to carry on with. Stop, and say which children are holding it.
        List<RunGroup> live = CurrentGroups().Where(IsRunning).ToList();
        if (live.Count > 0 && live.All(g => g.NeedsAttention))
            StopRun("every running instance needs attention — the last was " + reason);
    }

    private void StopRun(string reason)
    {
        if (_manifest.State == RunState.Stopped) return;
        RunManifestStore.Update(_manifest, manifest =>
        {
            manifest.State = RunState.Stopped;
            manifest.StopReason = reason;
        });
    }

    /// <summary>
    /// True when nothing more will come out of this round: every group has either run and exited,
    /// or failed to launch at all.
    ///
    /// A PENDING group counts as unfinished. That is the whole difference from the old check, which
    /// read <c>Pid == 0</c> as "never started, ignore it" — correct when every group launched
    /// immediately, and a silent way to abandon a queue now that they do not.
    ///
    /// A group whose child has asked to close counts as FINISHED even before the process goes: its
    /// models are logged, and Revit's own shutdown can take longer than the round it belongs to.
    ///
    /// So does a group DEFERRED by <see cref="DeferForAttention"/> (1.4.2). That child is wedged, or
    /// stuck behind a dialog, or past its ceiling — whichever it is, it is never going to exit, and
    /// waiting for it would leave the round open forever and the retry that reruns its models never
    /// built. Unlike the case above its models are NOT logged, which is exactly why the round has to
    /// end: <see cref="MissingModels"/> is what picks them up.
    /// </summary>
    private bool RoundFinished() => CurrentGroups().All(g => GroupSettled(g, IsDoneBarExiting(g)));

    /// <summary>
    /// Whether one group can no longer hold its round open. Pure and separate from the manifest for
    /// the same reason <see cref="SlotsToFill"/> is: getting this wrong either abandons a queue or
    /// hangs a run forever, and neither is something to discover on a live overnight batch.
    /// </summary>
    /// <param name="doneBarExiting">The caller's <see cref="IsDoneBarExiting"/> answer, which needs
    /// per-child watch state this cannot see.</param>
    internal static bool GroupSettled(RunGroup g, bool doneBarExiting) =>
        g.LaunchFailed ||
        (g.Pid != 0 && (g.ExitedAt != null || g.NeedsAttention || doneBarExiting));

    /// <summary>
    /// Reconciles the round that just ended and starts the next one if anything is still missing.
    /// Returns false when the run is done — either everything has a row, or every survivor has
    /// exhausted <see cref="MaxRounds"/>.
    /// </summary>
    private bool StartNextRoundIfNeeded()
    {
        List<CloudModel> missing = MissingModels();
        if (missing.Count == 0)
        {
            Note("INFO", $"all {_manifest.AllModels.Count} model(s) accounted for after " +
                         $"round {_manifest.RoundNumber}.");
            return false;
        }

        if (_manifest.RoundNumber >= MaxRounds)
        {
            foreach (CloudModel m in missing)
            {
                RunManifestStore.Update(_manifest, x => x.Unrecoverable.Add(ModelKey.For(m)));
                RunNote.Write(_manifest.TrackingLogPath, _manifest.Name, m.DisplayName, "ERROR",
                    $"unrecoverable — no result after {MaxRounds} attempts; " +
                    $"see {RunTrace.LogPath} for the stage each attempt stopped at.");
            }
            return false;
        }

        int next = _manifest.RoundNumber + 1;

        // The escalation ladder, one rung per round:
        //
        //   2 — RE-CHUNK at the ORIGINAL group size. Round 2 used to put every survivor in a single
        //       instance, which undid the very thing the group size was chosen for: a run split
        //       10/10/10/10 that loses 2+3+4+8 would retry all 17 in ONE Revit — a heavier session
        //       than the original that just failed. It now reruns them as 10/7, the shape asked for.
        //       A run with no configured split (GroupSize 0) keeps the single-instance behaviour,
        //       since there is no original size to honour.
        //
        //   3 — RE-SHAPE TO THE MACHINE. Fit the survivors into exactly one wave of instances, so
        //       a model that failed under contention gets a run with the machine to itself before
        //       the expensive isolation round.
        //
        //   4 — ISOLATE, one model per instance: the best chance for a heavy model and the only way
        //       to identify which one is toxic.
        //
        // The original size comes from the MANIFEST, which the launcher stamped with the number it
        // actually split at. Asking BaseJob was wrong: interactive Batch Run never writes its
        // dialog's group size back onto the job, so that read returned 0 and round 2 collapsed into
        // one instance — precisely the behaviour this regrouping exists to prevent. BaseJob stays as
        // the fallback for a manifest written before GroupSize existed.
        int original = _manifest.GroupSize > 0 ? _manifest.GroupSize : (_manifest.BaseJob?.GroupSize ?? 0);
        int groupSize = RoundGroupSize(next, original, missing.Count, EffectiveConcurrencyCap());

        int groupCount = ChunkLauncher.GroupCount(missing.Count, groupSize);
        Note("INFO", $"round {next} of {MaxRounds} — rerunning {missing.Count} model(s) with no " +
                     $"result (or only a partial one) in {groupCount} group(s) of up to {groupSize}, " +
                     $"{InstanceBudget.Describe(_manifest.AllModels)}: " +
                     string.Join("; ", missing.Select(m => m.DisplayName)));

        if (next >= MaxRounds) PurgeCacheForLastChance(missing);

        // The manifest's executable, NOT this process's — the supervisor is Revit itself for an
        // in-add-in run, but it is KhBatchController.exe for a standalone one, and defaulting
        // there re-launched the controller as the worker. See RunManifest.RevitExePath.
        List<RunGroup> groups = ChunkLauncher.LaunchChunks(
            _manifest.BaseJob!, missing, groupSize, out string? error, _manifest.RunStamp,
            _manifest.RevitExePath, EffectiveConcurrencyCap());

        if (error != null) Note(groups.Count == 0 ? "ERROR" : "INFO", error);
        if (groups.Count == 0)
        {
            string reason = $"round {next} could not start; {missing.Count} model(s) left unprocessed.";
            StopRun(reason);
            Note("ERROR", reason + " No further retry was launched.");
            return false;
        }

        RunManifestStore.Update(_manifest, m =>
        {
            m.Rounds.Add(new RunRound { Round = next, StartedAt = DateTime.Now, Groups = groups });
            m.CountAttempts(missing);
        });
        return true;
    }

    /// <summary>
    /// Models per instance for <paramref name="nextRound"/> — the escalation ladder, as one pure
    /// function so it can be checked without launching anything.
    ///
    /// <list type="bullet">
    /// <item>round 2 — <paramref name="originalSize"/>, the shape the user asked for;</item>
    /// <item>round 3 — enough per instance that every survivor fits ONE wave of
    /// <see cref="InstanceBudget.MaxConcurrent"/> Revits: 31 survivors on a 4-instance machine
    /// become 4 groups of 8, not 31 groups of 1;</item>
    /// <item>round 4 — one model each.</item>
    /// </list>
    /// </summary>
    internal static int RoundGroupSize(int nextRound, int originalSize, int missingCount,
        int maxConcurrentInstances = 0)
    {
        if (nextRound >= MaxRounds) return 1;
        if (nextRound == MaxRounds - 1)
        {
            int cap = maxConcurrentInstances > 0
                ? maxConcurrentInstances
                : Math.Max(1, InstanceBudget.MaxConcurrent());
            return Math.Max(1, (int)Math.Ceiling(missingCount / (double)cap));
        }
        return originalSize > 0 ? originalSize : Math.Max(1, missingCount);
    }

    /// <summary>
    /// Models the run was asked to process that are not finished, and have not already been written
    /// off. Matching is by display name because that is what the tracking log records; a name that
    /// appears twice in the model list cannot be told apart from its twin, so it is treated as done
    /// rather than risk running the wrong file repeatedly.
    ///
    /// "Finished" means every action produced a row, not merely that some did — see
    /// <see cref="TrackingCsvReader.CompletedModels"/>. Under the former policy, a model whose
    /// instance was force-closed halfway through its action list counted as done and was silently
    /// shipped part-processed.
    /// </summary>
    private List<CloudModel> MissingModels()
    {
        HashSet<string> done = TrackingCsvReader.CompletedModels(_manifest.TrackingLogPath);
        var ambiguous = _manifest.AllModels
            .GroupBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new List<CloudModel>();
        foreach (CloudModel m in _manifest.AllModels)
        {
            if (done.Contains(m.DisplayName)) continue;
            if (_manifest.Unrecoverable.Contains(ModelKey.For(m))) continue;
            if (ambiguous.Contains(m.DisplayName))
            {
                Note("INFO", $"'{m.DisplayName}' appears more than once in this run's model list, so " +
                             "its result cannot be matched by name — not retried.");
                continue;
            }
            missing.Add(m);
        }
        return missing;
    }

    /// <summary>
    /// Before the last attempt, delete the model's local cache file: a half-written cache left by a
    /// crashed download is a plausible reason a retry keeps failing, and clearing the cloud cache is
    /// exactly what changed the behaviour when this was hit in the field.
    ///
    /// Only for a run that cannot have pending changes — a read-only Check, or an explicit dry run.
    /// A crashed instance of a sync-capable action may hold unsynced work in that cache, and deleting
    /// it would silently discard it.
    /// </summary>
    private void PurgeCacheForLastChance(IReadOnlyList<CloudModel> models)
    {
        // NoSync is the one state that guarantees nothing in that cache is owed to the central.
        // A Check writes nothing either, but the job doesn't record which actions are read-only,
        // and guessing wrong here loses a user's work — so err toward keeping the cache.
        if (_manifest.BaseJob?.Sync != Batch.SyncMode.NoSync) return;

        foreach (CloudModel m in models)
        {
            string? path = ChildLiveness.ResolveCachePath(m, _manifest.RevitVersion);
            if (path == null) continue;
            try
            {
                File.Delete(path);
                Note("INFO", $"cleared the local cloud cache for '{m.DisplayName}' before its final attempt.");
            }
            catch (Exception ex)
            {
                Note("INFO", $"could not clear the cache for '{m.DisplayName}': {ex.Message}");
            }
        }
    }

    private void Note(string status, string message) =>
        RunNote.Write(_manifest.TrackingLogPath, _manifest.Name, "", status, message);

    /// <summary>
    /// Says so when an instance disappears WITHOUT having asked to close — a crash, not a finish.
    ///
    /// <para>Until 1.3.8.8 this path was silent. On 2026-08-15 a Revit running
    /// <c>SITE2A-BLDE-FFE.rvt</c> took an access violation inside the model loader
    /// (<c>captureTryCrash 0xc0000005</c> in <c>LoadFromStorage</c>) with <b>569 MB</b> of RAM left,
    /// while its three neighbours held 44, 29 and 23 GB. The run recovered it in round 2 and finished
    /// clean — so the ONLY trace of a crashed Revit in the whole log was the round-2 line saying a
    /// model had "no result". A recovery that hides what it recovered from cannot be tuned.</para>
    ///
    /// <para>What it reports is what can be attributed with certainty: the pid's own last trail line
    /// (<see cref="RunTrace"/> is pid-tagged), and free RAM now. It does NOT go looking in Revit's
    /// journals — those are named sequentially rather than by pid, and guessing which one belonged to
    /// a dead child is exactly the attribution <see cref="ChildLiveness"/> declined to do. The
    /// journal is named as the place to look instead.</para>
    ///
    /// <para><b>The stage comes from a FRESH poll of the trail, not from the watch</b> (fixed
    /// 1.3.8.9). As shipped in 1.3.8.8 this read <c>watch.LastStage</c>, which only
    /// <see cref="ChildLiveness.Check"/> updates — and <see cref="PolicePace"/> takes the exit branch
    /// BEFORE calling it. The stage was therefore up to one poll stale and never included the burst
    /// of lines a child writes on its way out, so two instances that had written
    /// <c>batch end: 2 ok, 0 failed</c> and posted their close were both reported as crashes on
    /// 2026-08-15. A finished child read as a crash is the same error as
    /// <see cref="ChildLiveness.Verdict.ShutdownStalled"/> exists to prevent; the predicate was right
    /// and its input was wrong.</para>
    ///
    /// <para>INFO, not ERROR: the model has not failed yet — the next round is about to retry it, and
    /// on 2026-08-15 that retry succeeded. This is the sentence that says why there was a next
    /// round.</para>
    /// </summary>
    private void ReportUnexpectedExit(RunGroup group)
    {
        try
        {
            // One more incremental read before judging: the child's last lines are written between
            // the tick that saw it alive and the tick that finds it gone, so the poll at the top of
            // the loop is by definition too early to have them.
            _tail.Poll();

            _watches.TryGetValue(group.Pid, out ChildLiveness.Watch? watch);
            string? lastStage = _tail.For(group.Pid)?.Stage ?? watch?.LastStage;
            if (ChildLiveness.IsShuttingDown(lastStage)) return;   // asked to close: a finish

            string stage = string.IsNullOrEmpty(lastStage)
                ? "before it wrote any stage at all"
                : $"at '{lastStage}'";
            string free = InstanceBudget.FreeMemoryNote();
            string memory = free.Length == 0 ? "" : $" The machine has {free} now.";

            Note("INFO",
                $"the Revit running {group.Models.Count} model(s) (pid {group.Pid}) exited without " +
                $"asking to close — it stopped {stage}. That is a crash or a force-close, not a " +
                $"finished instance; its models have no result and a later round will rerun them." +
                memory + " Revit's own journal for that process holds the cause — look for " +
                "'captureTryCrash' near the end of the journal whose start time matches " +
                $"{group.StartedAt:yyyy-MM-dd HH:mm:ss}.");
        }
        catch (Exception ex)
        {
            RunTrace.Mark($"post-mortem for pid {group.Pid} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pivots the finished run into its matrix workbook(s) — see <see cref="Reporting.RunEndMatrix"/>.
    ///
    /// Wrapped rather than called inline because this runs in a <c>finally</c> whose remaining job is
    /// to hand the run's disposition to the caller: a report writer must not be able to prevent a
    /// completed run from being reported as completed, or a Revit that was told to close from
    /// closing.
    /// </summary>
    private void BuildRunEndMatrices()
    {
        try { Reporting.RunEndMatrix.BuildForRun(_manifest, Note); }
        catch (Exception ex) { RunTrace.Mark("run-end matrix failed: " + ex.Message); }
    }
}
