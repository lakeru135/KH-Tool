using System.Text.Json.Serialization;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Schedule;

/// <summary>How far a supervised run got.</summary>
public enum RunState
{
    /// <summary>Groups are running (or being retried) and a supervisor is watching them.</summary>
    Running,

    /// <summary>Every model produced a row, or exhausted its attempts. Nothing more will run.</summary>
    Completed,

    /// <summary>The supervising process disappeared mid-run (its Revit was closed, or it crashed).
    /// Recorded rather than deleted: an abandoned manifest is the only evidence that a run was cut
    /// short, and which models never got their chance.</summary>
    Abandoned,

    /// <summary>A live worker needs human attention. No queued group or retry is started, and the
    /// worker is deliberately left open for inspection instead of being force-closed.</summary>
    Stopped
}

/// <summary>
/// One Revit instance and the models it was given.
///
/// A group is in one of THREE states, and the combination of <see cref="Pid"/> and
/// <see cref="ExitedAt"/> is what tells them apart:
/// <list type="table">
/// <item><term>Pending</term><description><c>Pid == 0</c>, <c>ExitedAt == null</c>,
/// <see cref="LaunchFailed"/> false — queued behind <see cref="InstanceBudget.MaxConcurrent"/>,
/// waiting for a slot. Its chunk job is already saved; no Revit exists yet.</description></item>
/// <item><term>Running</term><description><c>Pid != 0</c>, <c>ExitedAt == null</c>.</description></item>
/// <item><term>Finished</term><description><c>ExitedAt != null</c>, or
/// <see cref="LaunchFailed"/> — nothing more will come from it.</description></item>
/// </list>
/// The pending state is new in the concurrency-capped launcher. Anything asking "is this round
/// over?" must treat pending as NOT over — see <c>RunSupervisor.AllExited</c>, which previously
/// read <c>Pid == 0</c> as "never started, ignore it".
/// </summary>
public sealed class RunGroup
{
    /// <summary>Id of the ephemeral <see cref="ChunkJobStore"/> job this instance was (or will be)
    /// launched with. Saved before the launch, so a pending group already has one.</summary>
    public string ChunkId { get; set; } = "";

    /// <summary>OS process id of the launched Revit — the handle the supervisor watches. 0 while the
    /// group is still pending, and 0 permanently if the launch itself failed
    /// (<see cref="LaunchFailed"/> distinguishes those two).</summary>
    public int Pid { get; set; }

    /// <summary>When the Revit was actually started — NOT when the round began. A group that waited
    /// in the queue must have its liveness clocks measured from its own launch.
    /// <c>default</c> while pending.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Seconds this instance was told to idle before its first cloud open. Carried here so
    /// the supervisor can exclude it from the no-progress clock — the child cannot be blamed for
    /// waiting when waiting is what it was asked to do. See <c>ChildLiveness.Watch.NotBefore</c>.</summary>
    public int FirstOpenDelaySeconds { get; set; }

    /// <summary>True when <c>Process.Start</c> threw for this group. Terminal, and distinct from
    /// "pending": no Revit will ever exist, so the round must not wait for it.</summary>
    public bool LaunchFailed { get; set; }

    /// <summary>Set once the process is gone; null while it is still running
    /// OR still pending.</summary>
    public DateTime? ExitedAt { get; set; }

    /// <summary>Compatibility field for manifests created by older builds that force-terminated a
    /// worker. Version 1.3.4 and later never set this.</summary>
    public bool Killed { get; set; }

    /// <summary>The liveness monitor found a condition that needs a person to inspect this still-live
    /// Revit. Since 1.4.2 this sets ONE GROUP aside rather than stopping the run: its models go to
    /// the next round, the worker is left open, and it keeps holding its concurrency slot because it
    /// is still holding its memory. See <c>RunSupervisor.DeferForAttention</c>.</summary>
    public bool NeedsAttention { get; set; }

    /// <summary>
    /// This instance finished its models, asked to close, and took longer than
    /// <c>ChildLiveness.ShutdownGrace</c> to go. Recorded so the observation is made once rather
    /// than every 15-second poll — and, unlike <see cref="NeedsAttention"/>, it does NOT stop the
    /// run: there is nothing left to retry in a group whose work is already logged.
    /// </summary>
    public bool SlowExitReported { get; set; }

    /// <summary>
    /// The stage line of the last slow OPEN already reported for this instance, so a 16-minute open
    /// is noted once rather than at every 15-second poll — while a second model that also opens
    /// slowly still gets its own line. Empty until one is reported.
    ///
    /// Like <see cref="SlowExitReported"/> this does NOT stop the run: an open that is merely slow
    /// is still an open, and the instance that produced this observation on 2026-08-13 went on to
    /// finish every model it had. A genuinely hung open is caught by the per-model ceiling instead.
    /// </summary>
    public string SlowOpenReportedFor { get; set; } = "";

    /// <summary>
    /// The same idea for a slow ACTION (1.4.2) — the stage line of the last one already reported, so
    /// a 26-minute Drawing Progress is noted once rather than at every poll, while the next model's
    /// slow action still gets its own line.
    ///
    /// Separate from <see cref="SlowOpenReportedFor"/> rather than shared: a model that opens slowly
    /// AND then runs slowly must produce both observations, and one field would let the second
    /// overwrite the first.
    /// </summary>
    public string SlowActionReportedFor { get; set; } = "";

    /// <summary>
    /// How many times this group's Revit has been replaced after exiting without asking to close
    /// (1.4.2). Bounded, because a model that crashes Revit on open crashes it every time —
    /// <c>SITE2A-BLDA-ELE.rvt</c> did it four times running on 2026-08-18 — and an unbounded
    /// relaunch would turn that into a loop instead of a retry.
    /// </summary>
    public int RelaunchCount { get; set; }

    /// <summary>The concrete liveness evidence that set this group aside.</summary>
    public string AttentionReason { get; set; } = "";

    /// <summary>The models handed to this instance, in order.</summary>
    public List<CloudModel> Models { get; set; } = new();
}

/// <summary>One attempt at the whole (remaining) model list.</summary>
public sealed class RunRound
{
    /// <summary>
    /// 1-based. The escalation ladder is:
    /// <list type="number">
    /// <item>the user's group size — run it as asked;</item>
    /// <item>the SAME size again over whatever is missing — a transient failure wants the shape it
    /// asked for, not a heavier one (see <see cref="RunManifest.GroupSize"/>);</item>
    /// <item>a size derived from the machine, so the survivors fit one wave of
    /// <see cref="InstanceBudget.MaxConcurrent"/> instances;</item>
    /// <item>one model per instance — the best chance a heavy model gets, and the only way to
    /// identify which model is toxic.</item>
    /// </list>
    /// Round 3 was added after a run went 10 groups → 6 → 31 and starved the machine; jumping
    /// straight from the user's size to full isolation is what produced the 31.
    /// </summary>
    public int Round { get; set; }

    public DateTime StartedAt { get; set; }

    public List<RunGroup> Groups { get; set; } = new();
}

/// <summary>
/// The record of a parallel run: which Revit instance was given which models, what has been retried,
/// and what is beyond saving. Written by whichever process launches the groups and read by
/// <see cref="RunSupervisor"/>.
///
/// It exists because nothing else can answer "did the whole batch actually run?". The launcher
/// returns immediately, each child instance quits when it is done, and the shared tracking CSV only
/// records models that FINISHED — so a group whose Revit dies mid-way is invisible: its models
/// simply never appear. Holding the intended assignment in a file means the supervisor can diff
/// intent against outcome, and a manifest left behind by a killed supervisor still shows what was
/// lost.
/// </summary>
public sealed class RunManifest
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The run stamp every round shares, so all their rows land in ONE tracking CSV
    /// (see <see cref="ScheduledJob.LogRunStamp"/>).</summary>
    public DateTime RunStamp { get; set; } = DateTime.Now;

    /// <summary>Full path of the unified tracking CSV this run's rows go to — what the supervisor
    /// reads back to decide which models finished.</summary>
    public string TrackingLogPath { get; set; } = "";

    /// <summary>Friendly name for log rows (the job / command name).</summary>
    public string Name { get; set; } = "";

    public RunState State { get; set; } = RunState.Running;

    /// <summary>Process id of the supervising Revit. Lets a later session tell an abandoned run
    /// (supervisor gone) from one that is genuinely still in progress.</summary>
    public int SupervisorPid { get; set; }

    /// <summary>Revit release of the launching process ("2026") — locates the per-version
    /// CollaborationCache and Journals folders the liveness check reads.</summary>
    public string RevitVersion { get; set; } = "";

    /// <summary>The job every round rebuilds its chunks from. Carries the actions and their
    /// configuration snapshots, so a retry runs exactly what the first attempt did.</summary>
    public ScheduledJob? BaseJob { get; set; }

    /// <summary>
    /// Models per instance in round 1 — the shape the user actually asked for, and the shape a
    /// retry round has to reproduce.
    ///
    /// Recorded HERE rather than read back from <see cref="BaseJob"/>, because the group size does
    /// not reliably live on the job. Interactive Batch Run takes it from its dialog and passes it
    /// straight to the launcher without ever writing it to the job it builds, so
    /// <c>BaseJob.GroupSize</c> is 0 for those runs. MEASURED 2026-07-26: a 14-model run split
    /// 5/5/4 in round 1 then retried all 10 survivors in ONE instance, because the supervisor
    /// asked the job and got 0. The launcher is given the real number, so the launcher is what
    /// records it.
    /// </summary>
    public int GroupSize { get; set; }

    /// <summary>The worker-process cap selected when round 1 launched. Persisting it keeps every
    /// retry under the same cloud-safe policy even if the environment changes during the run.</summary>
    public int MaxConcurrentInstances { get; set; }

    /// <summary>
    /// The Revit executable this run's instances are launched from.
    ///
    /// Recorded for the same reason as <see cref="GroupSize"/>: a retry round has to launch the
    /// same thing round 1 did, and it cannot work that out for itself. <c>ChunkLauncher</c> defaults
    /// to the CURRENT process's executable, which is right when the supervisor lives inside Revit
    /// and catastrophically wrong when it lives in the standalone controller — there it re-launched
    /// KhBatchController.exe as the "worker". MEASURED 2026-07-26: rounds 2 and 3 dutifully started
    /// 15 processes that were not Revit, produced no rows, and the run wrote off all 14 models as
    /// unrecoverable. Empty means "this process's own executable", the in-Revit default.
    /// </summary>
    public string RevitExePath { get; set; } = "";

    /// <summary>Every model the run was asked to process — the authority on what "complete" means.
    /// Already de-duplicated by <see cref="ModelPresetCombiner"/>.</summary>
    public List<CloudModel> AllModels { get; set; } = new();

    public List<RunRound> Rounds { get; set; } = new();

    /// <summary><see cref="ModelKey"/> → how many instances have been given this model.</summary>
    public Dictionary<string, int> Attempts { get; set; } = new();

    /// <summary><see cref="ModelKey"/>s that exhausted their attempts without ever producing a row.</summary>
    public List<string> Unrecoverable { get; set; } = new();

    /// <summary><see cref="ModelKey"/> → how many times Revit died while OPENING this model in this
    /// run — <see cref="RunSupervisor.OpenCrashPolicy"/>'s input. Per run on purpose: a model that
    /// crashed last week gets a fresh retry today.</summary>
    public Dictionary<string, int> OpenCrashes { get; set; } = new();

    /// <summary>Why the run entered <see cref="RunState.Stopped"/>. Empty for completed and
    /// abandoned runs.</summary>
    public string StopReason { get; set; } = "";

    [JsonIgnore]
    public RunRound? CurrentRound => Rounds.Count == 0 ? null : Rounds[Rounds.Count - 1];

    [JsonIgnore]
    public int RoundNumber => CurrentRound?.Round ?? 0;

    /// <summary>Every model of the current round's groups, flattened.</summary>
    public IEnumerable<CloudModel> ModelsInFlight =>
        CurrentRound?.Groups.SelectMany(g => g.Models) ?? Enumerable.Empty<CloudModel>();

    public void CountAttempts(IEnumerable<CloudModel> models)
    {
        foreach (CloudModel m in models)
        {
            string key = ModelKey.For(m);
            Attempts[key] = Attempts.TryGetValue(key, out int n) ? n + 1 : 1;
        }
    }

    /// <summary>Friendly names for the models that exhausted every retry.</summary>
    public List<string> UnrecoverableDisplayNames() => AllModels
        .Where(m => Unrecoverable.Contains(ModelKey.For(m)))
        .Select(m => m.DisplayName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
