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
internal sealed partial class RunSupervisor
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
}
