using System.Runtime.InteropServices;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Schedule;

/// <summary>
/// How many Revit instances this machine may run at once.
///
/// Nothing used to bound this. <see cref="ChunkLauncher"/> launched one Revit per group, all at
/// the same moment, and the group COUNT is a consequence of the model count divided by the group
/// size — a number nobody chooses directly. MEASURED 2026-07-26: a 98-model run at group size 10
/// started 10 instances in round 1, and when round 3 fell back to one model per instance it
/// started <b>31</b>. On a 32 GB laptop that is roughly 190 GB of Revit trying to fit in 24, and
/// the machine spent the next thirteen minutes swapping.
///
/// The damage was not the slowness. Starved instances produce no stage marks, no cache growth and
/// almost no CPU, which is indistinguishable from wedged — so the former supervisor force-closed
/// nine of them and pushed their models into the next round, which made the next round bigger,
/// which made the starvation worse. The recovery mechanism amplified the failure it existed to
/// absorb. A cap is what breaks that loop.
///
/// The group size and this cap answer different questions and are deliberately separate:
/// <b>group size</b> is how many models one Revit works through; <b>this</b> is how many Revits
/// work at once. Groups beyond the cap are queued and launched as earlier ones exit.
/// </summary>
internal static class InstanceBudget
{
    /// <summary>Reserved for Windows itself plus the SUPERVISING Revit — which is a full instance
    /// with a model list of its own on the interactive path, not a bookkeeping thread.</summary>
    private const double ReserveGb = 8;

    /// <summary>Working set to budget per child instance. Sized for this project's cloud models
    /// (gigabyte-scale, heavily linked), not for a small local file: over-estimating costs some
    /// parallelism, while under-estimating costs the whole run.</summary>
    private const double PerInstanceGb = 6;

    /// <summary>
    /// Per-instance budget when the run includes <b>Batch Interop</b>. A checking instance is a
    /// whole Revit PLUS the AIT Model Checker: its managed API loaded by reflection, the checkset,
    /// and a full ReportRun held in memory until the HTML and Excel exports are written. None of
    /// that was in view when <see cref="PerInstanceGb"/> was measured against cloud model opens.
    ///
    /// This is the mundane explanation the 1.3.4 crash never ruled out. That release recorded only
    /// "a multi-process Batch Interop test crashed the PC" — on a machine where the ordinary budget
    /// would have authorised up to <see cref="HardCeiling"/> instances. A budget that ignores the
    /// heaviest thing in the process is how a machine gets oversubscribed, and oversubscription is
    /// already the documented failure mode here (see this class's summary: 31 instances, thirteen
    /// minutes of swapping, and a supervisor that mistook starvation for a wedge).
    ///
    /// Deliberately a guess with a stated basis rather than a measurement: nobody has measured a
    /// checking instance's peak working set. It is set high because the cost of being wrong in this
    /// direction is a slower run, and the cost of being wrong in the other direction is the thing
    /// this whole exercise exists to avoid. Replace it with a measured number when one exists.
    ///
    /// <para><b>Raised from 10 to 24 at 1.3.8.8</b>, because a measurement did arrive for the half of
    /// this that is not the Model Checker. <see cref="Actions.BatchInteropAction"/> also sets
    /// <c>OpensAllWorksets</c>, so a checking instance carries the same fully-loaded model and link
    /// tree that measured 18–49 GB on 2026-08-15, plus the checker on top. 10 GB was under-counting
    /// the Revit before the checker was even considered.</para>
    /// </summary>
    private const double InteropPerInstanceGb = 24;

    /// <summary>
    /// Ceiling for an Interop run regardless of how much RAM the machine reports, until live
    /// acceptance says otherwise. Starts at the smallest number that is still parallel: proving two
    /// concurrent checking instances are safe is the whole question, and a machine with 128 GB
    /// should not be the one that answers it by launching eight.
    /// </summary>
    private const int InteropHardCeiling = 2;

    /// <summary>
    /// Per-instance budget when the run's action opens every workset, or nearly all of them — Export
    /// NWC, Batch Interop. An ordinary open loads a fraction of the model; this one loads all of it,
    /// plus every link, and holds it for the export.
    ///
    /// <para><b>MEASURED 2026-08-15, and it replaces a figure that was never measured at all.</b>
    /// This constant was 12 GB, justified by "eight instances left about 10 GB free on a 64 GB
    /// machine". That observation was taken during the run whose worksets NEVER OPENED — 0 of 26, 0
    /// of 64, 0 of 45 — so it budgeted for instances that had loaded nothing. The number was
    /// measured against the bug.</para>
    ///
    /// <para>Once 1.3.8.7 made the open work, the Revit journals gave the real figures for peak
    /// working set on the same eight models: <b>49,765 MB</b> and <b>43,946 MB</b>, then 29.0, 23.2,
    /// 21.4 and 18.4 GB. Four instances at once left the machine with <b>12 MB free</b> and killed
    /// one Revit with an access violation in the model loader. 24 GB sits above the middle of that
    /// spread rather than at its peak — the ceiling below is what covers the 48 GB tail.</para>
    /// </summary>
    private const double AllWorksetsPerInstanceGb = 24;

    /// <summary>
    /// Ceiling for an all-worksets run. TWO, down from four: two instances that both hit the observed
    /// 48.6 GB peak still cannot fit in 64 GB, so the ceiling is not what makes this safe — the
    /// per-instance budget is, and this stops a 128 GB machine from being the one that finds the next
    /// limit. On the office PC the RAM term gives 2 on its own; on a 32 GB laptop it gives 1.
    ///
    /// <para>Revisit when the view-derived workset set of 1.3.8.8 has been measured live: an export
    /// that no longer loads seventeen linked models should peak far lower, and this whole weight may
    /// then be worth relaxing.</para>
    /// </summary>
    private const int AllWorksetsHardCeiling = 2;

    /// <summary>
    /// Logical processors per instance. <see cref="Environment.ProcessorCount"/> counts hardware
    /// threads, not cores, so an i9's hyperthreads inflate it — the divisor is 4 rather than 2 so
    /// that on a big-core machine RAM stays the binding constraint, which is what actually runs
    /// out. A cloud open is dominated by download and single-threaded parse anyway.
    /// </summary>
    private const int ThreadsPerInstance = 4;

    private const int HardFloor = 1;

    /// <summary>
    /// Ceiling for an ordinary run. <b>Lowered from 8 to 6 at 1.4.1</b>, on the one comparison that
    /// held everything else still: the SAME 64 models through the SAME six actions, twice.
    ///
    /// <para>2026-08-16 ran them as ONE job — 8 instances, wall clock <b>61.8 min</b>. 2026-08-17 ran
    /// them as TWO jobs of 32 that overlapped, so the machine carried about 16 children plus two
    /// supervisors, and the same work took <b>135.8 min</b>. Nothing was slower per instance because
    /// of a code change; everything was slower because the machine was oversubscribed. A cloud open
    /// went from 0.7 min mean (max 4.6) to <b>2.4 min mean (max 15.1)</b>, and a Drawing Progress pass
    /// from 1.7 min mean (max 9.5) to <b>7.9 min mean (max 45.2)</b>.</para>
    ///
    /// <para>8 was never measured — it was the biggest number the RAM term allowed on a 64 GB PC.
    /// This is the first evidence about the cap itself, and it says 8 is past the knee. Note what
    /// this ceiling does NOT do: it is per RUN, so two overlapping jobs may still start 12. A
    /// machine-wide budget is the open item; until it exists, schedules have to be spaced apart.</para>
    /// </summary>
    private const int HardCeiling = 6;

    /// <summary>
    /// Most models one Revit instance is given, which is the other half of the 1.4.1 change and the
    /// half that answers "why did 30 models take as long as 60?".
    ///
    /// <para><see cref="AutoGroupSize"/> used to aim for TWO WAVES of instances, so the group COUNT
    /// was <c>2 × cap</c> — sixteen — <b>whatever the model count was</b>. Halving the models halved
    /// the group SIZE and left the count alone: 64 models ran as 16 × 4 and 32 models as 16 × 2, and
    /// both paid <b>23 child Revit startups</b>. The work halved and the startup bill did not.</para>
    ///
    /// <para>A startup is expensive and MEASURED: group 15 of the 2026-08-17 run (pid 50884) was
    /// launched at 14:06:57, armed at 14:09:02 and reached <c>batch begin</c> at 14:16:27 — <b>9.5
    /// minutes before its first open</b>, for two models. Worse, only 8 of the 16 groups could start
    /// at once, so groups 9–16 waited for a free slot and THEN paid their own 9.5 minutes: round 1's
    /// launch ramp alone spanned <b>39 minutes</b> (13:32:26 → 14:11:43).</para>
    ///
    /// <para>So the rule inverts. Fill one wave of <see cref="HardCeiling"/> instances and give each
    /// up to this many models; only a run too big for that queues a second wave. 45 models become
    /// 8/8/8/7/7/7 in one wave; 100 become ten groups of ten, six now and four as slots free.</para>
    ///
    /// <para>The cost of dropping the second wave is stated so it can be recognised: a wave finishes
    /// when its slowest group does, and one 45-minute Drawing Progress model now sits inside a group
    /// of ten rather than a group of two, so the tail is longer and slots can idle through it.
    /// <see cref="ChunkSplitter"/>'s round-robin dealing is what keeps the heavy models from landing
    /// in the same group. The measurements above say a startup costs more than that tail — but if a
    /// future run shows otherwise, THIS is the constant to revisit, not the ceiling.</para>
    /// </summary>
    private const int MaxModelsPerInstance = 10;

    /// <summary>Override for a machine the formula reads wrong, or to force serial execution while
    /// diagnosing. Clamped to 1..16; anything unparseable is ignored rather than fatal.</summary>
    public const string EnvVar = "KHBATCH_MAX_INSTANCES";

    /// <summary>Override for <see cref="FirstOpenStaggerSeconds"/>, in seconds. Clamped to 0..120.
    /// Exists so a machine whose cold cloud opens still collide can be tuned without a rebuild —
    /// the alternative during the 1.3.4 investigation was reverting to serial cloud runs.</summary>
    public const string StaggerEnvVar = "KHBATCH_FIRST_OPEN_STAGGER";

    private const int DefaultFirstOpenStaggerSeconds = 12;

    /// <summary>
    /// Seconds of separation between one group's FIRST cloud open and the next group's.
    ///
    /// MEASURED over 519 opens on 2026-07-26: a freshly started Revit fails its FIRST cloud open
    /// 22.7% of the time (17 of 75), its second 7.4%, and essentially never after that — 71% of all
    /// lost instances died on open #1. Revit takes an access violation inside its own loader
    /// (<c>loadSelectedEpochs</c> → <c>SelectedPartitionsForEdit</c>), which no managed handler can
    /// catch, so the process is simply gone. The failures also cluster in time across instances —
    /// four within four seconds at 11:29:34, two within three at 18:16 — which points at several
    /// cold Revits initialising the shared CollaborationCache at once rather than at any one model
    /// (every model in the set has both deaths and successes).
    ///
    /// The groups still LAUNCH together, so Revit startup overlaps as before; only the risky first
    /// open is spread out. Later opens are unaffected, so a long run costs this once.
    /// </summary>
    public static int FirstOpenStaggerSeconds
    {
        get
        {
            try
            {
                string? raw = Environment.GetEnvironmentVariable(StaggerEnvVar);
                if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int v) && v >= 0 && v <= 120)
                    return v;
            }
            catch { /* fall through to the default */ }
            return DefaultFirstOpenStaggerSeconds;
        }
    }

    /// <summary>
    /// Instances that may run concurrently. Never zero — a cap of 1 means "serial", which is the
    /// correct answer for a small machine rather than a reason to refuse the run.
    /// </summary>
    public static int MaxConcurrent() => MaxConcurrent(RunWeight.Normal);

    /// <summary>
    /// Instances that may run concurrently for a run of the given weight. An explicit
    /// <see cref="EnvVar"/> override still wins for every weight — it exists so a machine the
    /// formula reads wrong can be corrected in the field, including downward to 1.
    /// </summary>
    public static int MaxConcurrent(RunWeight weight)
    {
        int? forced = Forced();
        if (forced.HasValue) return forced.Value;
        return CapFor(weight, TotalMemoryGb(), Environment.ProcessorCount);
    }

    /// <summary>
    /// The cap arithmetic alone, for a stated machine — no environment, no P/Invoke.
    ///
    /// <para>Separated at 1.3.8.8 for the same reason <see cref="RunSupervisor.RoundGroupSize"/> is:
    /// the numbers here decide whether a run oversubscribes the machine, and until now they could
    /// only be checked by running on a machine that happened to have the right amount of RAM. The
    /// harness can now assert that 64 GB gives 2 and 32 GB gives 1 from any PC.</para>
    /// </summary>
    /// <param name="totalGb">Installed physical memory. 0 = unreadable, which lets the CPU term
    /// decide rather than pinning the answer to 1.</param>
    internal static int CapFor(RunWeight weight, double totalGb, int processorCount)
    {
        double perInstance = weight switch
        {
            RunWeight.Interop => InteropPerInstanceGb,
            RunWeight.AllWorksets => AllWorksetsPerInstanceGb,
            _ => PerInstanceGb,
        };
        int ceiling = weight switch
        {
            RunWeight.Interop => InteropHardCeiling,
            RunWeight.AllWorksets => AllWorksetsHardCeiling,
            _ => HardCeiling,
        };

        int byRam = totalGb > 0
            ? (int)Math.Floor((totalGb - ReserveGb) / perInstance)
            : ceiling;                           // memory unreadable: let the CPU term decide
        int byCpu = Math.Max(1, processorCount / ThreadsPerInstance);

        // Math.Clamp is .NET Core only; this assembly also targets .NET Framework 4.8 for Revit
        // 2024/2025.
        return Math.Max(HardFloor, Math.Min(ceiling, Math.Min(byRam, byCpu)));
    }

    /// <summary>
    /// Cap for one concrete model list. Cloud and local runs get the same resource-derived answer.
    ///
    /// This overload once returned 1 for any run containing a cloud model. Windows crash evidence
    /// from 2026-08-03 showed four workers failing inside
    /// <c>RevitApplication.openDocumentFile</c> before their actions ran, and serializing cloud
    /// opens was the immediate response. It also removed parallelism from every Forma run there is —
    /// the groups were still built and then queued one behind the other, so a "split" scheduled run
    /// was indistinguishable from a serial one, which is not what anyone asked the split for.
    ///
    /// The crash is addressed where it happens instead: <see cref="ChunkLauncher"/> separates each
    /// group's FIRST cloud open by <c>FirstOpenStaggerSeconds</c> (12s by default, tunable through
    /// <see cref="StaggerEnvVar"/>), which is the open that fails — measured over 519 opens, 71% of
    /// all lost instances died on open #1, and the failures clustered in time across instances
    /// rather than around any one model. <see cref="RunSupervisor"/>'s four bounded rounds then
    /// rerun whatever an instance still took with it.
    ///
    /// Kept as an overload rather than folded into <see cref="MaxConcurrent()"/>: every launch site
    /// already passes its model list, so the seam for a future per-run rule costs nothing to keep.
    /// <see cref="EnvVar"/> = 1 forces the old serial behaviour on a machine that needs it.
    /// </summary>
    public static int MaxConcurrent(IReadOnlyList<CloudModel> models) => MaxConcurrent();

    /// <summary>Cap for one concrete model list at the given weight.</summary>
    public static int MaxConcurrent(IReadOnlyList<CloudModel> models, RunWeight weight) =>
        MaxConcurrent(weight);

    /// <summary>
    /// Models per Revit instance, derived rather than typed.
    ///
    /// <para>Fills <b>one wave</b> of instances, each holding at most
    /// <see cref="MaxModelsPerInstance"/> models:
    /// <c>min(10, ceil(count / cap))</c>. 45 models on a 6-instance machine give six groups sized
    /// 8/8/8/7/7/7, all started together; 100 give ten groups of ten, six started now and four
    /// queued for a slot. A run only makes more groups than the cap once it exceeds
    /// <c>cap × 10</c> models.</para>
    ///
    /// <para><b>Superseded reasoning (1.3.8.8 – 1.4.0), kept because it explains the shape being
    /// replaced.</b> This aimed for about TWO waves — <c>waves = 2 × cap</c>, size
    /// <c>ceil(count / waves)</c> — so that a second wave could absorb groups finishing at wildly
    /// different times. MEASURED 2026-07-26: a hand-picked size of 10 made a 98-model run into 10
    /// groups for 8 slots (a wave of 8 then a wave of 2, six slots idle through the tail) and those
    /// groups ranged from 27 to 61 model-minutes because contiguous chunks put the heavy models
    /// together. The two-wave rule answered the second complaint; <see cref="ChunkSplitter"/>'s
    /// dealing answered the first.</para>
    ///
    /// <para>What it missed is that pinning the group COUNT to <c>2 × cap</c> pins the number of
    /// Revit STARTUPS, and a startup measured 9.5 minutes before its first open on 2026-08-17. A
    /// 32-model run and a 64-model run both cost 23 child startups, which is why halving a set did
    /// not halve its wall clock. See <see cref="MaxModelsPerInstance"/> for the full measurement.</para>
    ///
    /// <para>Still derived rather than typed, for the original reason: it depends on the model count
    /// AND the machine, so the right number for 98 models on a 32-thread desktop is wrong for 32 on
    /// a laptop. The box is gone from the dialogs and this is the answer.</para>
    /// </summary>
    public static int AutoGroupSize(int modelCount)
        => AutoGroupSize(modelCount, MaxConcurrent());

    /// <summary>Cloud-aware form used once the concrete model list has been resolved.</summary>
    public static int AutoGroupSize(IReadOnlyList<CloudModel> models) =>
        AutoGroupSize(models, RunWeight.Normal);

    /// <summary>Weight-aware form: a heavier run gets fewer slots, so it also gets bigger groups —
    /// the same total work through fewer Revits.</summary>
    public static int AutoGroupSize(IReadOnlyList<CloudModel> models, RunWeight weight) =>
        AutoGroupSize(models.Count, MaxConcurrent(models, weight));

    private static int AutoGroupSize(int modelCount, int maxConcurrent)
    {
        if (modelCount <= 1) return 1;
        int perWave = (int)Math.Ceiling(modelCount / (double)Math.Max(1, maxConcurrent));
        return Math.Max(1, Math.Min(MaxModelsPerInstance, perWave));
    }

    /// <summary>
    /// Resolves a persisted <see cref="ScheduledJob.GroupSize"/> into the concrete size a launcher
    /// can use. Keeping this rule here prevents scheduled, Run-now, and preview paths from treating
    /// <see cref="ScheduledJob.GroupSizeAuto"/> differently.
    /// </summary>
    public static int EffectiveGroupSize(int configuredGroupSize, int modelCount)
    {
        if (configuredGroupSize > 0) return configuredGroupSize;
        if (configuredGroupSize == ScheduledJob.GroupSizeAuto) return AutoGroupSize(modelCount);
        return 0;
    }

    /// <summary>Resolves an auto group size using the cloud-safe cap for the actual model list.</summary>
    public static int EffectiveGroupSize(int configuredGroupSize, IReadOnlyList<CloudModel> models) =>
        EffectiveGroupSize(configuredGroupSize, models, RunWeight.Normal);

    /// <inheritdoc cref="EffectiveGroupSize(int, IReadOnlyList{CloudModel})"/>
    public static int EffectiveGroupSize(
        int configuredGroupSize, IReadOnlyList<CloudModel> models, RunWeight weight)
    {
        if (configuredGroupSize > 0) return configuredGroupSize;
        if (configuredGroupSize == ScheduledJob.GroupSizeAuto) return AutoGroupSize(models, weight);
        return 0;
    }

    /// <summary>
    /// The weight of a job — whether its per-instance cost is an ordinary Revit or a Revit plus the
    /// AIT Model Checker. Central so every launch path (interactive, scheduled, retry round) reads
    /// the same rule; a cap that differs by entry point is how a policy quietly stops applying.
    /// </summary>
    /// <remarks>
    /// Interop wins when a job asks for both: the rule has to pick the heaviest thing in the process,
    /// not the last one checked. Since 1.3.8.8 the two weights carry the same per-instance budget and
    /// the same ceiling — a checking instance opens every workset too — so the order only matters if
    /// they diverge again. Keep it: whichever branch is added next must still be the heavier one.
    /// </remarks>
    public static RunWeight WeightFor(ScheduledJob? job)
    {
        if (InteropStep.IsRequested(job)) return RunWeight.Interop;
        if (job != null && (job.ActionNames.Contains(Actions.ActionNames.ExportNwc) ||
                            job.ActionNames.Contains(Actions.ActionNames.ExportIfcData)))
            return RunWeight.AllWorksets;   // both open the worksets of a whole 3D view
        return RunWeight.Normal;
    }

    /// <summary>One line for the run log, so a run that quietly serialises on a small machine says
    /// why instead of just being slow.</summary>
    public static string Describe() => Describe(RunWeight.Normal);

    /// <inheritdoc cref="Describe()"/>
    public static string Describe(RunWeight weight)
    {
        int cap = MaxConcurrent(weight);
        string basis = Forced().HasValue
            ? $"forced by {EnvVar}"
            : $"{TotalMemoryGb():0} GB RAM, {Environment.ProcessorCount} logical processor(s)";
        if (!Forced().HasValue && weight == RunWeight.Interop)
            basis += $", capped at {InteropHardCeiling} for Model Checker runs";
        if (!Forced().HasValue && weight == RunWeight.AllWorksets)
            basis += $", capped at {AllWorksetsHardCeiling} because these runs load whole models " +
                     $"and their links ({AllWorksetsPerInstanceGb:0} GB budgeted each)";
        return $"at most {cap} Revit instance(s) at once ({basis})";
    }

    /// <summary>
    /// Free physical RAM, as a phrase for a log row — "3.2 GB RAM free", or "" when it cannot be
    /// read.
    ///
    /// <para>Exists because of one line in the 2026-08-15 journals: a Revit took an access violation
    /// inside the model loader with <b>569 MB</b> free while its three neighbours held 44, 29 and
    /// 23 GB, and nothing the runner wrote recorded how close the machine had come to empty. A budget
    /// set before the run is a prediction; this is the measurement that checks it.</para>
    /// </summary>
    public static string FreeMemoryNote()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref status)) return "";
            double freeGb = status.ullAvailPhys / (1024d * 1024d * 1024d);
            return freeGb < 1
                ? $"{status.ullAvailPhys / (1024d * 1024d):N0} MB RAM free"
                : $"{freeGb:0.0} GB RAM free";
        }
        catch { return ""; }
    }

    /// <summary>Describes the effective cap for a resolved run, naming the cloud stagger when the
    /// run has cloud models — that delay is visible as a slow start and should not read as a
    /// stall.</summary>
    public static string Describe(IReadOnlyList<CloudModel> models) =>
        Describe(models, RunWeight.Normal);

    /// <inheritdoc cref="Describe(IReadOnlyList{CloudModel})"/>
    public static string Describe(IReadOnlyList<CloudModel> models, RunWeight weight)
    {
        string text = Describe(weight);
        if (models.Any(m => !m.IsLocal))
            text += $", each starting its first cloud open {FirstOpenStaggerSeconds}s after the last";
        return text;
    }

    private static int? Forced()
    {
        try
        {
            string? raw = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!int.TryParse(raw, out int v)) return null;
            return v < 1 || v > 16 ? null : v;
        }
        catch { return null; }
    }

    /// <summary>
    /// Installed physical memory, in GB. Returns 0 when it cannot be read, which the caller treats
    /// as "don't let RAM be the constraint".
    ///
    /// Deliberately <c>GlobalMemoryStatusEx</c> rather than <c>GC.GetGCMemoryInfo</c>: the latter is
    /// .NET Core only (this assembly also targets .NET Framework 4.8, for Revit 2024/2025) and
    /// reports the GC's available-memory limit, which is not the same thing as the RAM the machine
    /// has for running several Revits.
    /// </summary>
    private static double TotalMemoryGb()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref status)) return 0;
            return status.ullTotalPhys / (1024d * 1024d * 1024d);
        }
        catch { return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
