using System.Diagnostics;
using System.IO;
using KhBatchRunner.Presets;

// Disambiguated from System.Diagnostics, which this file also uses (Process).
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Decides whether a child Revit instance is <b>working</b> or <b>wedged</b>.
///
/// The distinction matters more than it looks. A production model is gigabytes, and on a cold cloud
/// cache every one is a full download before Revit even begins parsing it:
///
/// <code>
///            100 Mbps    20 Mbps    10 Mbps     5 Mbps
///     1 GB      ~1.5m       ~7m       ~14m       ~27m
///     2 GB        ~3m      ~14m       ~27m       ~55m
///     4 GB        ~6m      ~27m       ~55m      ~110m
/// </code>
///
/// …plus another 5–20 minutes of open/parse for a heavy model with links. So a flat wall-clock limit
/// short enough to catch a real hang would misclassify healthy work on any ordinary office
/// connection. The former force-close design made a too-short limit worse than none at all: it
/// turned one slow model into three interrupted attempts and a guaranteed "unrecoverable".
///
/// The failure being hunted is <b>no progress</b>, not "taking a while". A child counts as alive if
/// ANY independent signal moved since the last poll. It needs attention only when they are ALL
/// flat for <see cref="NoProgressGrace"/> — except inside the two stages that are a single call
/// between two breadcrumbs, a document open (<see cref="OpenGrace"/>, <see cref="IsOpening"/>) and an
/// action (<see cref="ActionGrace"/>, <see cref="IsRunningAction"/>), each of which gets a longer
/// grace and a verdict that can never stop a run. The supervisor then defers that child and leaves
/// it open, and carries on with the rest:
///
/// <list type="number">
/// <item><b>Stage</b> — a new PID-tagged line in the shared trail (<see cref="RunTrace.Tail"/>).
/// Definitive when it moves, but a single "open" stage can span an entire download, so its silence
/// proves nothing on its own.</item>
/// <item><b>Cache growth</b> — the model's file under <c>CollaborationCache</c> getting bigger.
/// Direct evidence of a download in flight.</item>
/// <item><b>CPU time</b> — <see cref="Process.TotalProcessorTime"/>. This is the signal that makes
/// the scheme safe for GB models: while Revit parses a finished download the cache is static and no
/// stage is written, yet the process is busy. A network-wedged Revit sits at essentially zero.</item>
/// </list>
///
/// (A fourth signal, journal growth, was considered and dropped: Revit names journals sequentially
/// rather than by pid, so attributing one to a specific child needs open-handle enumeration — heavy,
/// and redundant next to CPU time.)
///
/// <para><b>Three clock rules, all learned the hard way on 2026-07-26</b> (a 98-model run that lost
/// 14 models to this file's own decisions — see <see cref="PerModelCeiling"/>,
/// <see cref="IsWorkMark"/> and <see cref="Watch.NotBefore"/>):</para>
/// <list type="number">
/// <item>Ceilings are measured against the model in hand, not the process. An instance with ten
/// models is entitled to ten models' worth of time.</item>
/// <item>A lifecycle breadcrumb ("armed group …") is not evidence that work started. Only a
/// model-level stage ends the startup grace.</item>
/// <item>Time the child was TOLD to wait (the first-open stagger) is not time it failed to
/// progress.</item>
/// </list>
/// </summary>
internal static class ChildLiveness
{
    /// <summary>
    /// All signals flat for this long ⇒ needs attention. This now stops launches and retains the
    /// worker for inspection; it never terminates Revit.
    ///
    /// Overridable with <c>KHBATCH_STALL_GRACE_SEC</c>. These thresholds are reasoned from model
    /// size and link speed rather than measured on any particular site, so an environment where
    /// they are wrong needs a way to correct them that isn't "wait for a new build" — and the
    /// automated tests need to reach the stall path in seconds rather than minutes.
    /// </summary>
    public static readonly TimeSpan NoProgressGrace =
        EnvSpan("KHBATCH_STALL_GRACE_SEC", TimeSpan.FromSeconds(1), TimeSpan.FromHours(2),
                TimeSpan.FromMinutes(10));

    /// <summary>CPU consumed within a poll window that counts as "doing something". Low enough to
    /// catch a mostly-IO-bound open, high enough to ignore idle housekeeping threads.</summary>
    public static readonly TimeSpan CpuBusyThreshold = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a freshly launched instance is left alone before its signals mean anything.
    ///
    /// None of the three signals exist yet while Revit itself is starting: no model is open so no
    /// cache is growing, and startup is I/O-bound enough to dip under
    /// <see cref="CpuBusyThreshold"/> for a stretch. Judging a child in that window reads a
    /// perfectly healthy instance as wedged. The former implementation then force-closed an
    /// innocent group ~60s after launch. An instance that never finishes starting is still
    /// caught, by the ceiling.
    ///
    /// Measured from <see cref="Watch.NotBefore"/>, and ended by the first WORK mark
    /// (<see cref="IsWorkMark"/>) — not by any mark at all. See <see cref="Check"/>.
    /// </summary>
    public static readonly TimeSpan StartupGrace =
        EnvSpan("KHBATCH_STARTUP_GRACE_SEC", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5));

    /// <summary>
    /// How long a child that has posted its own close is left to disappear before the trail says so.
    ///
    /// Generous on purpose, and generous in a way that costs nothing: this instance's models are
    /// already logged, so the only thing still at stake is a process handle. Revit's shutdown can
    /// genuinely take minutes with a large model and a dozen add-ins unloading, and the alternative
    /// reading — "wedged" — is the one that used to cancel the rest of the run.
    /// Overridable with <c>KHBATCH_SHUTDOWN_GRACE_SEC</c>.
    /// </summary>
    public static readonly TimeSpan ShutdownGrace =
        EnvSpan("KHBATCH_SHUTDOWN_GRACE_SEC", TimeSpan.FromSeconds(30), TimeSpan.FromHours(4),
                TimeSpan.FromMinutes(30));

    /// <summary>
    /// How long a child may sit inside a document OPEN with every signal flat before the trail says
    /// so — replacing <see cref="NoProgressGrace"/> for that one stage. Overridable with
    /// <c>KHBATCH_OPEN_GRACE_SEC</c>.
    ///
    /// <para>An open is a single blocking API call that emits no further breadcrumb until it
    /// returns, and while it blocks on a mutex, a database or the network the process burns almost
    /// no CPU — so all three signals go flat on a perfectly healthy instance. The ten-minute
    /// <see cref="NoProgressGrace"/> was never long enough for that stage.</para>
    ///
    /// <para>MEASURED 2026-08-13 (report <c>BKKLH3C7QV3_20260813_1851</c>): pid 75764 began
    /// <c>SITE2A-BLDB-STR.rvt | open (background, detached)</c> at 18:35:36 and was declared
    /// <see cref="Verdict.Wedged"/> at 18:45:53 on <c>cpu +0.3s</c>. The open returned at 18:51:32 —
    /// <b>15 min 55 s</b> — and the instance went on to report <c>6 ok, 0 failed</c> and post its own
    /// close. The run had already been stopped over it. Revit's own journal names the cause: the
    /// Steel Connections DB extension hitting an abandoned mutex and a machine-wide
    /// <c>SteelConnections2026</c> SQL instance while a sibling Revit held them.</para>
    ///
    /// <para>This is a grace, not a licence: <see cref="PerModelCeiling"/> still bounds a genuinely
    /// hung open, and the verdict it produces (<see cref="Verdict.OpenSlow"/>) does not stop the
    /// run.</para>
    /// </summary>
    public static readonly TimeSpan OpenGrace =
        EnvSpan("KHBATCH_OPEN_GRACE_SEC", TimeSpan.FromSeconds(1), TimeSpan.FromHours(4),
                TimeSpan.FromMinutes(30));

    /// <summary>
    /// How long a child may sit inside an ACTION with every signal flat before the trail says so —
    /// replacing <see cref="NoProgressGrace"/> for that stage, exactly as <see cref="OpenGrace"/>
    /// does for a document open. Overridable with <c>KHBATCH_ACTION_GRACE_SEC</c>.
    ///
    /// <para>The same argument, for the same shape of stage. An action is one call bracketed by
    /// <c>run</c> and <c>ran</c> with nothing written between them, and one that spends its time
    /// walking a model's sheets and views rather than burning CPU — so stage, cache and CPU all go
    /// flat on an instance that is working perfectly.</para>
    ///
    /// <para>MEASURED 2026-08-18 (report <c>BKKLH3C7QV3_20260818_0801</c>): pid 81908 entered
    /// <c>Drawing Progress | SITE3A-BLDC-STR.rvt | run (reusing open document)</c> at 03:22:13, was
    /// declared <see cref="Verdict.Wedged"/> at 03:32:26 on <c>cpu +1.2s</c> — and the call returned
    /// at 03:48:24, <b>26 min 11 s</b>, after which the instance reported <c>36 ok, 0 failed</c> and
    /// posted its own close. The run had already been stopped over it, cancelling its retry round.
    /// The 17 Aug bundle holds a worse one: Drawing Progress on <c>SITE2A-BLDB-ELE.rvt</c> took
    /// <b>45 min 12 s</b>.</para>
    ///
    /// <para><b>60 minutes, not 45.</b> 45 is the largest pass anyone has measured, so a 45-minute
    /// grace would have fired on it. This sits above the evidence and below
    /// <see cref="DefaultCeiling"/> (90 min), which remains the backstop that does stop a run —
    /// this is a grace, not a licence.</para>
    /// </summary>
    public static readonly TimeSpan ActionGrace =
        EnvSpan("KHBATCH_ACTION_GRACE_SEC", TimeSpan.FromSeconds(1), TimeSpan.FromHours(4),
                TimeSpan.FromMinutes(60));

    /// <summary>Per-model backstop when no size is known — a model that has produced no progress
    /// signal for this long is finished regardless of what the other signals claim. Overridable
    /// with <c>KHBATCH_CEILING_MIN</c>; see <see cref="NoProgressGrace"/> for why.</summary>
    public static readonly TimeSpan DefaultCeiling =
        EnvSpan("KHBATCH_CEILING_MIN", TimeSpan.FromMinutes(1), TimeSpan.FromHours(12),
                TimeSpan.FromMinutes(90), minutes: true);

    /// <summary>Set <c>KHBATCH_TRACE_LIVENESS=1</c> to write one trail line per instance per poll
    /// with all three signal values. Off by default (it is one line every 15s per child); the point
    /// is that a run which stops can be asked WHY afterwards instead of guessed at.</summary>
    private static bool TraceEnabled =>
        Environment.GetEnvironmentVariable("KHBATCH_TRACE_LIVENESS") == "1";

    /// <summary>Reads a duration override from the environment, clamped to a sane range. Anything
    /// unparseable or out of range falls back to <paramref name="fallback"/> rather than failing —
    /// a typo in an env var must not change how aggressively a run is stopped.</summary>
    private static TimeSpan EnvSpan(string name, TimeSpan min, TimeSpan max, TimeSpan fallback,
        bool minutes = false)
    {
        try
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (!double.TryParse(raw, out double v)) return fallback;
            TimeSpan span = minutes ? TimeSpan.FromMinutes(v) : TimeSpan.FromSeconds(v);
            return span < min || span > max ? fallback : span;
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Whether a trail line means the child is actually PROCESSING A MODEL, as opposed to merely
    /// having got far enough to say hello.
    ///
    /// <see cref="Batch.BatchEngine"/> writes every model-level stage as
    /// <c>"&lt;action&gt; | &lt;model&gt; | &lt;stage&gt;"</c>, so the pipe separator is the marker.
    /// Everything else on the trail is lifecycle: <c>"armed group &lt;id&gt;"</c> from
    /// <c>App.OnStartup</c>, <c>"staggered start: …"</c>, <c>"surface: …"</c>.
    ///
    /// This distinction is the whole of RC2. <c>App.OnStartup</c> writes "armed group …" before
    /// Revit can open anything, and the old check asked only whether ANY mark existed — so the
    /// startup grace was skipped and a ten-minute no-progress clock started against a Revit that
    /// had not yet reached its first Idling tick. MEASURED 2026-07-26: nine instances were
    /// force-closed by the former policy at
    /// exactly that stage, two in round 1 and seven in round 3, none of which had done anything
    /// wrong beyond starting on a machine running 31 Revits.
    /// </summary>
    // IndexOf rather than Contains(string, StringComparison): the overload with a comparison is
    // .NET Core only, and this assembly also targets .NET Framework 4.8 for Revit 2024/2025.
    public static bool IsWorkMark(string? stage) =>
        !string.IsNullOrEmpty(stage) && stage!.IndexOf(" | ", StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Whether the child's last trail line says it has ASKED TO CLOSE
    /// (<see cref="RevitShutdown.ClosePostedMark"/>) — its models are done and it is on its way out.
    ///
    /// Every signal this class reads goes flat during a Revit shutdown: no new stage is written, no
    /// cache grows, and the CPU spent tearing down journals and add-ins does not reach
    /// <see cref="CpuBusyThreshold"/>. That is indistinguishable from a hang, so before 1.3.8.1 a
    /// slow exit was reported as one. MEASURED 2026-08-11: pid 39128 finished all nine of its
    /// models, posted its close, took over ten minutes to disappear, and was declared
    /// <see cref="Verdict.Wedged"/> at the stage <c>'normal close posted pid=39128:
    /// scheduled/headless batch complete'</c> — a sentence that describes a completed instance. The
    /// run was stopped and its queued groups deleted.
    ///
    /// <para>A child here has already done its work, so there is nothing left to rescue and no
    /// reason to stop the run. It is bounded by <see cref="ShutdownGrace"/> and never worse than
    /// <see cref="Verdict.ShutdownStalled"/>.</para>
    /// </summary>
    public static bool IsShuttingDown(string? stage) =>
        !string.IsNullOrEmpty(stage) &&
        stage!.IndexOf(RevitShutdown.ClosePostedMark, StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Whether the child's last trail line says it is INSIDE A DOCUMENT OPEN
    /// (<see cref="Batch.BatchStages.IsOpenStage"/> — background, activated, probe or workset
    /// reopen alike) — the one stage where flat signals prove nothing.
    ///
    /// Every other model stage is bracketed by breadcrumbs a few seconds apart, so silence there is
    /// real evidence. An open is one blocking call that can legitimately run for a quarter of an
    /// hour — a cold cloud cache is a full download before parsing even starts, and a third-party
    /// extension contending for a machine-wide resource inside <c>DocumentOpening</c> adds more —
    /// while the process sits at essentially zero CPU because it is blocked, not dead.
    ///
    /// <para>Same shape as <see cref="IsShuttingDown"/> and for the same reason: a signal that
    /// cannot distinguish "blocked but fine" from "hung" must not be read as the latter. See
    /// <see cref="OpenGrace"/> for the measurement that forced this.</para>
    /// </summary>
    public static bool IsOpening(string? stage) =>
        IsWorkMark(stage) && Batch.BatchStages.IsOpenStage(stage);

    /// <summary>
    /// Whether the child's last trail line says it is INSIDE AN ACTION
    /// (<see cref="Batch.BatchStages.IsActionRunStage"/>) — the second stage whose silence proves
    /// nothing, for the same reason as <see cref="IsOpening"/>: one call, two breadcrumbs, and a
    /// process blocked rather than dead.
    ///
    /// <para>The claim in <see cref="IsOpening"/>'s summary — "every other model stage is bracketed
    /// by breadcrumbs a few seconds apart" — was simply not true of this one, and 2026-08-18 is
    /// where that cost a run. See <see cref="ActionGrace"/>.</para>
    /// </summary>
    public static bool IsRunningAction(string? stage) =>
        IsWorkMark(stage) && Batch.BatchStages.IsActionRunStage(stage);

    /// <summary>Per-child state carried between polls.</summary>
    public sealed class Watch
    {
        public int Pid { get; init; }
        public string RevitVersion { get; init; } = "";
        public DateTime StartedAt { get; init; } = DateTime.Now;

        /// <summary>
        /// Seconds this instance was TOLD to idle before its first cloud open
        /// (<see cref="ScheduledJob.FirstOpenDelaySeconds"/>, set by
        /// <see cref="ChunkLauncher.FirstOpenStaggerSeconds"/>).
        /// </summary>
        public int FirstOpenDelaySeconds { get; init; }

        /// <summary>
        /// When this instance's clocks legitimately start: launch plus the stagger it was asked to
        /// observe. Counting the stagger as failure to progress is judging a child for obeying an
        /// instruction — and the delay reaches 360 s once a round has 31 groups, over half the
        /// no-progress budget.
        /// </summary>
        public DateTime NotBefore => StartedAt.AddSeconds(FirstOpenDelaySeconds);

        /// <summary>Last moment ANY signal moved. The clock the grace period is measured against.</summary>
        public DateTime LastProgressAt { get; set; } = DateTime.Now;

        /// <summary>What the child was last seen doing, included in the retained attention notice.</summary>
        public string LastStage { get; set; } = "";

        internal DateTime LastMarkAt { get; set; }

        /// <summary>When the first (and latest) MODEL-level stage arrived. Default means this
        /// instance has never processed anything, so it is still in its startup window.</summary>
        internal DateTime LastWorkMarkAt { get; set; }

        /// <summary>When the child moved on to the model it is working now — the clock the per-model
        /// ceiling is measured against. Default until the first work mark.</summary>
        internal DateTime CurrentModelStartedAt { get; set; }

        /// <summary>When this child was first seen to have asked to close, so a slow exit is timed
        /// from the request rather than from its last unit of work. Default until then.</summary>
        internal DateTime ShutdownSince { get; set; }

        internal TimeSpan LastCpu { get; set; }
        internal bool CpuReadable { get; set; } = true;
        internal long LastCacheBytes { get; set; }
        internal string? CachePath { get; set; }
        internal CloudModel? CacheModel { get; set; }
    }

    /// <summary>Outcome of one poll of one child.</summary>
    public enum Verdict
    {
        /// <summary>Progress seen, or too early to judge.</summary>
        Working,

        /// <summary>No progress for the grace period. The run stops and leaves the worker open.</summary>
        Wedged,

        /// <summary>Past the absolute ceiling. The run stops and leaves the worker open.</summary>
        OverCeiling,

        /// <summary>Never produced a single WORK mark within <see cref="StartupGrace"/> — Revit
        /// itself failed to get going (crashed on load, stuck on a dialog, out of memory). Nothing
        /// has been processed, so the run needs attention.</summary>
        NeverStarted,

        /// <summary>Asked to close and is still here <see cref="ShutdownGrace"/> later. Worth a line
        /// in the trail and nothing more: this instance's models all ran, so there is nothing to
        /// retry and no reason to stop the run. Unlike every other non-Working verdict, this one
        /// must NOT reach the supervisor's stop path — see <see cref="IsShuttingDown"/>.</summary>
        ShutdownStalled,

        /// <summary>Still inside a document open <see cref="OpenGrace"/> later. Like
        /// <see cref="ShutdownStalled"/> this is worth a line in the trail and nothing more, and for
        /// the same reason: the evidence does not support stopping a run. A genuinely hung open is
        /// still caught, by <see cref="PerModelCeiling"/>, which reports
        /// <see cref="Verdict.OverCeiling"/> and does stop it. See <see cref="IsOpening"/>.</summary>
        OpenSlow,

        /// <summary>Still inside an ACTION <see cref="ActionGrace"/> later. The third verdict that
        /// must never reach the supervisor's stop path, alongside <see cref="ShutdownStalled"/> and
        /// <see cref="OpenSlow"/>, and for the identical reason: on 2026-08-18 an instance stopped
        /// here at ten minutes went on to finish the action at 26 minutes and report
        /// <c>36 ok, 0 failed</c>. <see cref="PerModelCeiling"/> still catches an action that really
        /// never returns. See <see cref="IsRunningAction"/>.</summary>
        ActionSlow
    }

    /// <summary>
    /// A verdict plus the evidence behind it.
    ///
    /// The reason exists because the old log could not be read back. Every forced close said "no
    /// progress for N min" against the no-progress clock — even a ceiling verdict, which has
    /// nothing to do with progress. MEASURED 2026-07-26: three instances interrupted mid-<c>open</c>
    /// and mid-<c>run</c> were
    /// reported as "no progress for 0 sec", which is self-evidently not a stall, and the real cause
    /// (a per-model ceiling applied to a ten-model instance) went undiagnosed for a whole test
    /// cycle. A needs-attention verdict must be able to justify itself.
    /// </summary>
    public readonly record struct Judgement(Verdict Verdict, string Reason)
    {
        public static Judgement Working() => new(Verdict.Working, "");
    }

    /// <summary>
    /// Re-evaluates one child. <paramref name="tail"/> must already have been polled this tick (once
    /// for all children — it reads one shared file). <paramref name="groupModels"/> is the whole
    /// assignment, whose COUNT sizes the instance-wide backstop.
    /// </summary>
    public static Judgement Check(Watch watch, RunTrace.Tail tail, IReadOnlyList<CloudModel> groupModels)
    {
        DateTime now = DateTime.Now;
        bool progressed = false;
        var moved = new List<string>(3);

        // 1. Stage — also tells us WHICH model this child is on, which is what makes the cache
        //    signal possible at all. A lifecycle mark still counts as a sign of life; only a WORK
        //    mark ends the startup window and starts the per-model clock.
        RunTrace.StageMark? mark = tail.For(watch.Pid);
        if (mark != null && mark.At > watch.LastMarkAt)
        {
            watch.LastMarkAt = mark.At;
            watch.LastStage = mark.Stage;
            progressed = true;
            moved.Add("stage");
            if (IsWorkMark(mark.Stage)) watch.LastWorkMarkAt = mark.At;
        }

        // 2. Cache growth for the model named in the current stage.
        CloudModel? current = CurrentModel(watch.LastStage, groupModels);
        if (current != null && !ReferenceEquals(current, watch.CacheModel))
        {
            watch.CacheModel = current;
            watch.CachePath = ResolveCachePath(current, watch.RevitVersion);
            watch.LastCacheBytes = -1;              // re-baseline on the new model
            watch.CurrentModelStartedAt = now;      // and restart the per-model ceiling with it
        }
        long bytes = CacheBytes(watch.CachePath);
        if (bytes >= 0 && bytes != watch.LastCacheBytes)
        {
            if (watch.LastCacheBytes >= 0) { progressed = true; moved.Add("cache"); }
            watch.LastCacheBytes = bytes;
        }

        // 3. CPU time — the parse-phase signal. A null reading means the counter could not be read
        //    (access denied, or the process is gone), which is NOT the same as "zero CPU": the old
        //    code collapsed both to TimeSpan.Zero and then skipped the signal entirely, so an
        //    unreadable counter silently removed the one signal that keeps a busy parse alive.
        TimeSpan? cpu = CpuTime(watch.Pid);
        watch.CpuReadable = cpu.HasValue;
        TimeSpan cpuDelta = TimeSpan.Zero;
        if (cpu.HasValue)
        {
            cpuDelta = cpu.Value - watch.LastCpu;
            if (cpuDelta >= CpuBusyThreshold) { progressed = true; moved.Add("cpu"); }
            watch.LastCpu = cpu.Value;
        }

        if (progressed) watch.LastProgressAt = now;

        // ---- already on its way out ------------------------------------------------------------
        // Checked BEFORE the ceilings as well as before the no-progress clock: an instance that
        // spent its whole allowance working and then asked to close is not "over ceiling" in any
        // sense that should stop a run, and a group of ten models can easily reach its close with
        // the instance clock nearly spent.
        if (IsShuttingDown(watch.LastStage))
        {
            if (watch.ShutdownSince == default) watch.ShutdownSince = now;
            TimeSpan closing = now - watch.ShutdownSince;
            return closing <= ShutdownGrace
                ? Judgement.Working()
                : new Judgement(Verdict.ShutdownStalled,
                    $"finished its models and asked to close {Mins(closing)} ago but has not exited");
        }

        // ---- ceilings ------------------------------------------------------------------------
        // Both are measured from NotBefore (launch + the stagger the child was told to observe),
        // never from raw launch time.
        TimeSpan perModel = PerModelCeiling(watch.LastCacheBytes);
        DateTime modelClock = watch.CurrentModelStartedAt == default
            ? watch.NotBefore                       // nothing opened yet: the instance clock is all we have
            : watch.CurrentModelStartedAt;
        TimeSpan modelAge = now - modelClock;
        TimeSpan instanceCeiling = InstanceCeiling(perModel, groupModels.Count);
        TimeSpan instanceAge = now - watch.NotBefore;

        if (TraceEnabled)
            RunTrace.Mark($"liveness pid={watch.Pid} moved=[{string.Join(",", moved)}] " +
                          $"cpu+{cpuDelta.TotalSeconds:0.0}s{(watch.CpuReadable ? "" : " (unreadable)")} " +
                          $"cache={watch.LastCacheBytes} " +
                          $"flat={(now - watch.LastProgressAt).TotalSeconds:0}s " +
                          $"model={modelAge.TotalMinutes:0}/{perModel.TotalMinutes:0}min " +
                          $"inst={instanceAge.TotalMinutes:0}/{instanceCeiling.TotalMinutes:0}min " +
                          $"work={(watch.LastWorkMarkAt == default ? "none" : "yes")}");

        if (modelAge > perModel)
            return new Judgement(Verdict.OverCeiling,
                $"one model has been in progress for {Mins(modelAge)}, past its {Mins(perModel)} ceiling");

        if (instanceAge > instanceCeiling)
            return new Judgement(Verdict.OverCeiling,
                $"the instance has run {Mins(instanceAge)}, past the {Mins(instanceCeiling)} " +
                $"ceiling for {groupModels.Count} model(s)");

        TimeSpan flat = now - watch.LastProgressAt;

        // Until this instance has written its first MODEL-level stage, none of the signals are
        // meaningful — Revit is still starting and cannot produce most of them.
        //
        // Be deliberately generous here: BOTH the startup grace must have expired AND the process
        // must have gone completely silent for the no-progress period. A Revit that is merely slow
        // to start keeps resetting the second clock via CPU, and one that died on load exits, which
        // the IsAlive check above catches without needing any verdict at all. What is left is the
        // narrow case this is for: alive, but never going to do anything.
        //
        // Generosity matters because a starved instance and a wedged one are genuinely
        // indistinguishable from outside. A machine deep in swap blocks on page faults, which is
        // I/O wait, not processor time — so even CPU goes flat, and no threshold can tell the two
        // apart. Not over-subscribing the machine (InstanceBudget) is the actual fix; this clock's
        // job is only to avoid making a bad situation worse.
        if (watch.LastWorkMarkAt == default)
        {
            if (instanceAge <= StartupGrace + NoProgressGrace || flat < NoProgressGrace)
                return Judgement.Working();

            return new Judgement(Verdict.NeverStarted,
                $"alive but silent for {Mins(flat)}, and no model opened in the " +
                $"{Mins(instanceAge)} since it started" +
                (watch.FirstOpenDelaySeconds > 0 ? $" (after a {watch.FirstOpenDelaySeconds}s stagger)" : "") +
                $"; last trail line was '{Describe(watch.LastStage)}'");
        }

        // An open, and an action, each get their own much longer grace: they are the two stages
        // whose silence is expected, because each is a single call with no breadcrumb until it
        // returns. Every other stage still answers to the ten-minute clock.
        bool opening = IsOpening(watch.LastStage);
        bool running = !opening && IsRunningAction(watch.LastStage);
        TimeSpan grace = opening ? OpenGrace : running ? ActionGrace : NoProgressGrace;
        if (flat < grace) return Judgement.Working();

        string why = $"every signal flat for {Mins(flat)} at '{Describe(watch.LastStage)}' " +
                     $"(cpu {(watch.CpuReadable ? $"+{cpuDelta.TotalSeconds:0.0}s" : "unreadable")}, " +
                     $"cache {(watch.LastCacheBytes < 0 ? "not found" : watch.LastCacheBytes + " bytes")})";

        if (opening)
            return new Judgement(Verdict.OpenSlow,
                $"has been opening one model for {Mins(flat)} — {why}");

        if (running)
            return new Judgement(Verdict.ActionSlow,
                $"has been running one action for {Mins(flat)} — {why}");

        return new Judgement(Verdict.Wedged, why);
    }

    private static string Mins(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalSeconds} sec";

    private static string Describe(string stage) =>
        string.IsNullOrEmpty(stage) ? "startup" : stage;

    /// <summary>
    /// Time budget for ONE model, scaled by its size when we can see it: a bigger model
    /// legitimately takes longer, so a fixed cap would be simultaneously too tight for a 4 GB model
    /// and too loose for a 200 MB one. Size comes from the cached file if a previous run already
    /// pulled it (the common case on a warm cache); otherwise the flat default applies.
    ///
    /// <para>Two things were wrong here and both caused healthy work to be force-closed by the
    /// former supervisor on 2026-07-26.</para>
    ///
    /// <para>First, this budget was compared against the whole INSTANCE's age — see
    /// <see cref="InstanceCeiling"/>. A group of ten models cannot finish inside one model's
    /// allowance, so three instances were terminated at 45m10s while actively working, one of them
    /// mid-<c>run</c>.</para>
    ///
    /// <para>Second, the old floor of 45 minutes was BELOW the 90-minute unknown-size default, so an
    /// instance whose cache file could be found got half the budget of one whose could not. Being
    /// observable made it die sooner. The known-size result may now only ever extend the default,
    /// never shorten it.</para>
    /// </summary>
    public static TimeSpan PerModelCeiling(long cacheBytes)
    {
        if (cacheBytes <= 0) return DefaultCeiling;
        double gb = cacheBytes / (1024d * 1024d * 1024d);
        double minutes = 30 + (15 * gb);
        return TimeSpan.FromMinutes(Math.Min(240, Math.Max(DefaultCeiling.TotalMinutes, minutes)));
    }

    /// <summary>
    /// Absolute backstop for a whole instance: its startup allowance plus one model's budget for
    /// every model it was given. This is deliberately enormous (a ten-model group gets over twelve
    /// hours) because it is not the mechanism that catches a hang — <see cref="NoProgressGrace"/>
    /// is, at ten minutes. This exists only for the pathological case of a child that keeps
    /// emitting progress signals forever without ever finishing, and it must never be the thing
    /// that ends a run which is merely large.
    /// </summary>
    public static TimeSpan InstanceCeiling(TimeSpan perModel, int modelCount) =>
        StartupGrace + TimeSpan.FromTicks(perModel.Ticks * Math.Max(1, modelCount));

    /// <summary>The model whose name appears in the current stage line. Stages are written as
    /// "&lt;action&gt; | &lt;model&gt; | &lt;stage&gt;" by BatchEngine, so a contains-match against the
    /// group's own models is exact enough without re-parsing the format.</summary>
    private static CloudModel? CurrentModel(string stage, IReadOnlyList<CloudModel> groupModels)
    {
        if (string.IsNullOrEmpty(stage)) return null;
        foreach (CloudModel m in groupModels)
            if (!string.IsNullOrEmpty(m.DisplayName) &&
                stage.IndexOf(m.DisplayName, StringComparison.OrdinalIgnoreCase) >= 0)
                return m;
        return null;
    }

    /// <summary>
    /// Locates a cloud model's local cache file:
    /// <c>%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit &lt;year&gt;\CollaborationCache\&lt;account&gt;\&lt;ProjectGuid&gt;\&lt;ModelGuid&gt;.rvt</c>.
    /// The account segment is an opaque key, so the (few) account folders are scanned for the one
    /// holding this project. Null for a local model, or when the model has not been cached yet —
    /// in which case the cache signal simply doesn't vote.
    /// </summary>
    public static string? ResolveCachePath(CloudModel model, string revitVersion)
    {
        if (model.IsLocal || string.IsNullOrWhiteSpace(revitVersion)) return null;
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Autodesk", "Revit", "Autodesk Revit " + revitVersion, "CollaborationCache");
            if (!Directory.Exists(root)) return null;

            string leaf = model.ModelGuid.ToString() + ".rvt";
            foreach (string account in Directory.EnumerateDirectories(root))
            {
                string candidate = Path.Combine(account, model.ProjectGuid.ToString(), leaf);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* cache layout not as expected — this signal just abstains */ }
        return null;
    }

    /// <summary>Size of the cache file, or -1 when there is nothing to measure.</summary>
    private static long CacheBytes(string? path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : -1;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Total processor time for a pid, or <c>null</c> when the counter cannot be read at all — the
    /// process has exited, or opening it was denied. The distinction matters: a genuine zero is
    /// evidence of a stall, while an unreadable counter is no evidence either way and must not be
    /// mistaken for one.
    ///
    /// Overridable for tests (see <c>tools/LivenessSim</c>), which cannot conjure real processes
    /// with scripted CPU curves.
    /// </summary>
    internal static Func<int, TimeSpan?> CpuProbe { get; set; } = pid =>
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.HasExited ? null : p.TotalProcessorTime;
        }
        catch { return null; }
    };

    private static TimeSpan? CpuTime(int pid) => CpuProbe(pid);

    /// <summary>Whether a pid is still running. Overridable for tests, as with
    /// <see cref="CpuProbe"/>.</summary>
    internal static Func<int, bool> AliveProbe { get; set; } = pid =>
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    };

    public static bool IsAlive(int pid) => AliveProbe(pid);

}
