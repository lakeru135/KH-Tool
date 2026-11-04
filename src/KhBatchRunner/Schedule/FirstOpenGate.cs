using System.IO;
using System.Globalization;
using System.Threading;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Keeps any two Revit instances on this PC from making their FIRST cloud open at the same moment.
///
/// <para><b>Why this exists.</b> MEASURED over 519 opens on 2026-07-26: a freshly started Revit
/// fails its first cloud open 22.7% of the time — an access violation inside Revit's own loader that
/// no managed handler can catch, so the process simply disappears — and the failures cluster in
/// time across instances, which points at several cold Revits initialising the shared
/// CollaborationCache at once. <see cref="InstanceBudget.FirstOpenStaggerSeconds"/> was the answer:
/// launch together, spread the risky first open out.</para>
///
/// <para><b>Why the per-job delay was not enough.</b> Two holes, both visible in the 2026-08-11
/// diagnostics. (1) The delay was measured from the moment the job was ARMED — Revit's
/// <c>OnStartup</c> — while the first Idling tick arrives up to a minute later, so the wait was
/// already over before anything could have opened; the three children of that run reached their
/// first open at 13:12:56.099, .100 and .100, and all three died. Sibling instances launched
/// together also finish starting together, so a clock that starts at startup separates nothing.
/// (2) A per-job delay knows nothing about OTHER runs. Three supervised runs started within four
/// minutes of each other on 2026-08-07; their children collided across runs, which no amount of
/// within-run staggering can prevent.</para>
///
/// <para>So the gate is machine-wide and lives outside any one run: a timestamp file, guarded by a
/// named mutex, holding when the last first-open was claimed. An instance may go when the gate has
/// been quiet for the stagger, and stamps it on the way through.</para>
///
/// <para><b>It never blocks.</b> <see cref="TryClaim"/> returns false and is retried on the next
/// Idling tick, exactly as the original stagger waited — by returning, so the API thread stays free
/// and Revit can finish its own start-up during the wait. Every failure path opens the gate rather
/// than closing it: an unreadable stamp, an unavailable mutex or a stamp from the future all let the
/// caller proceed. A collision costs one model, which the supervisor retries; a gate that jammed
/// shut would cost the whole run.</para>
/// </summary>
internal static class FirstOpenGate
{
    /// <summary>Longest an instance will defer to the gate before going anyway. Past this, whatever
    /// is holding the gate is not a healthy sibling about to release it, and waiting longer only
    /// converts a possible crash into a certain stall.</summary>
    private static readonly TimeSpan MaxDeferral = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for the mutex. Holders keep it for a file read and a file write.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Redirects the stamp for tests, as <c>ChildLiveness.CpuProbe</c> redirects its
    /// probes. Without it the offline harness would claim the real gate and make the next genuine
    /// run wait — a test that can change how production behaves is not a test.</summary>
    internal static string? StampPathOverride { get; set; }

    private static string StampPath =>
        StampPathOverride ?? Path.Combine(ScheduleStore.ScheduledDirectory, "first-open.stamp");

    /// <summary>
    /// Asks to make a first cloud open now.
    ///
    /// Returns true — and records the claim — when the last claim is at least
    /// <see cref="InstanceBudget.FirstOpenStaggerSeconds"/> old, or when
    /// <paramref name="waitingSince"/> shows this instance has already deferred for
    /// <see cref="MaxDeferral"/>. Returns false to be asked again shortly.
    /// </summary>
    /// <param name="waitingSince">When this instance first asked, so a permanently blocked gate
    /// cannot hold it forever. Pass the same value on every retry.</param>
    /// <param name="waitSeconds">How much longer the gate would like it to wait — for the trail
    /// line, so a report can show the gate working rather than leaving it to be inferred.</param>
    public static bool TryClaim(DateTime waitingSince, out int waitSeconds)
    {
        waitSeconds = 0;
        int stagger = InstanceBudget.FirstOpenStaggerSeconds;
        if (stagger <= 0) return true;

        if (DateTime.UtcNow - waitingSince >= MaxDeferral) { Stamp(); return true; }

        Mutex? mutex = null;
        bool held = false;
        try
        {
            mutex = new Mutex(false, @"Global\KhBatchRunner_FirstOpenGate");
            try { held = mutex.WaitOne(LockTimeout); }
            catch (AbandonedMutexException) { held = true; }    // prior holder died mid-claim; ours now
            if (!held) return true;                             // contended past the timeout — go

            DateTime? last = ReadStamp();
            if (last.HasValue)
            {
                double since = (DateTime.UtcNow - last.Value).TotalSeconds;
                // A stamp in the future means a clock change, not a sibling; treat it as stale.
                if (since >= 0 && since < stagger)
                {
                    waitSeconds = (int)Math.Ceiling(stagger - since);
                    return false;
                }
            }

            WriteStamp();
            return true;
        }
        catch
        {
            // Never let the gate itself stop a run — see the class summary.
            return true;
        }
        finally
        {
            if (mutex != null)
            {
                try { if (held) mutex.ReleaseMutex(); } catch { /* best effort */ }
                mutex.Dispose();
            }
        }
    }

    /// <summary>Records a claim without asking — used when the deferral cap is reached, so the next
    /// instance still measures its wait from this one rather than from a stale stamp.</summary>
    private static void Stamp()
    {
        try { WriteStamp(); } catch { /* best effort */ }
    }

    private static DateTime? ReadStamp()
    {
        try
        {
            if (!File.Exists(StampPath)) return null;
            string text = File.ReadAllText(StampPath).Trim();
            return DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime at) ? at : (DateTime?)null;
        }
        catch { return null; }
    }

    private static void WriteStamp() =>
        File.WriteAllText(StampPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
}
