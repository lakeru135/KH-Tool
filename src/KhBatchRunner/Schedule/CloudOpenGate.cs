using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Keeps two sibling Revit instances from being inside a document OPEN at the same moment.
///
/// <para><b>Why this exists.</b> Report <c>BKKLH3C7QV3_20260813_1851</c>: a two-group Batch Interop
/// run, and one open of <c>SITE2A-BLDB-STR.rvt</c> took <b>15 min 55 s</b> against the 7–50 s every
/// other model in the same group needed. Revit's own journal names the cause, and it is not our
/// code — the <b>Steel Connections DB extension</b>, an Autodesk component that ships with Revit and
/// registers <c>DocumentOpening</c>/<c>DocumentOpened</c> handlers:</para>
///
/// <code>
///   ExceptionMessage:The wait completed due to an abandoned mutex.      (SteelDwgServices.cs:289)
///   Checking SQL instance SteelConnections2026 health...
///   Testing connection to Steel Connections databases...
///   ExceptionMessage:Object reference not set to an instance of an object.        (Main.cs:564)
///   The process cannot access the file '…\Temp\1\…\….dwg' because it is being used
///   by another process.                                          (SteelModelManager.cs:1813)
/// </code>
///
/// <para>An abandoned mutex, a machine-wide SQL instance and a shared temp file: three
/// machine-scoped resources, contended by two Revits that entered <c>DocumentOpening</c> together.
/// Nothing in this add-in can make that extension re-entrant, and disabling a component Autodesk
/// installs is not ours to do — but we can decline to ask it to do two things at once.</para>
///
/// <para><b>Deliberately narrow.</b> Only actions that ask for it
/// (<see cref="Actions.IBatchAction.SerialisesCloudOpens"/>, which today means Batch Interop) and
/// only in a process that is one of several (<see cref="ChunkContext.GroupNumber"/> &gt; 0). A
/// single-instance run has nothing to contend with, and serialising the opens of an ordinary split
/// run — where opening IS most of the work — would trade a rare stall for a guaranteed slowdown on
/// every run.</para>
///
/// <para><b>It fails open, like <see cref="FirstOpenGate"/>.</b> Every error path, and the timeout,
/// return a lease that opens anyway. A gate that jammed shut would cost a whole run; the worst a
/// missed gate costs is the contention we had before it existed.</para>
///
/// <para>Note the interaction with <see cref="ChildLiveness"/>: an instance waiting here is silent,
/// so the caller must already have written its open stage to the trail before acquiring. That is why
/// <c>BatchEngine.OpenForModel</c> traces first and acquires second — the wait is then covered by
/// <see cref="ChildLiveness.IsOpening"/> and cannot be read as a stall.</para>
/// </summary>
internal static class CloudOpenGate
{
    /// <summary>
    /// Longest an instance defers before opening anyway. Sized above the worst open this has been
    /// seen to protect against (16 min) with room to spare, and well under the per-model ceiling
    /// that would otherwise stop the run.
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(25);

    /// <summary>A wait shorter than this is not worth a trail line — siblings routinely pass through
    /// each other without really queuing.</summary>
    private static readonly TimeSpan WorthReporting = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Redirects the mutex name for tests, as <see cref="FirstOpenGate.StampPathOverride"/>
    /// redirects its stamp and for the same reason: without it the offline harness would take the
    /// real machine-wide gate and hold a genuine run's open behind it.
    /// </summary>
    internal static string? NameOverride { get; set; }

    private static string NameFor(string revitVersion) =>
        NameOverride ?? @"Global\KhBatchRunner_CloudOpenGate_" + revitVersion;

    /// <summary>
    /// Takes the gate for one open, or returns an already-open lease when this run does not need it.
    /// Dispose to release — always from the acquiring thread, which is why this is only ever used as
    /// a <c>using</c> inside the synchronous open.
    /// </summary>
    /// <param name="needed">Whether this action and this process want the gate at all.</param>
    /// <param name="revitVersion">Gates are per Revit year: the contended resources are too
    /// (<c>SteelConnections2026</c>), and two different years genuinely do not collide.</param>
    public static IDisposable Acquire(bool needed, string revitVersion)
    {
        if (!needed) return Lease.Open;

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, NameFor(revitVersion));
            DateTime asked = DateTime.UtcNow;
            bool held;
            try { held = mutex.WaitOne(MaxWait); }
            catch (AbandonedMutexException) { held = true; }   // a sibling died holding it; ours now

            TimeSpan waited = DateTime.UtcNow - asked;
            if (!held)
            {
                RunTrace.Mark($"cloud open gate: waited {(int)waited.TotalMinutes} min for another " +
                              "Revit and is opening anyway");
                mutex.Dispose();
                return Lease.Open;
            }

            if (waited >= WorthReporting)
                RunTrace.Mark($"cloud open gate: waited {(int)waited.TotalSeconds}s for another " +
                              "Revit to finish its open");

            return new Lease(mutex);
        }
        catch
        {
            // Never let the gate itself stop a run — see the class summary.
            try { mutex?.Dispose(); } catch { /* best effort */ }
            return Lease.Open;
        }
    }

    private sealed class Lease : IDisposable
    {
        /// <summary>A lease that holds nothing — the "gate not needed" and "gate failed" answer.</summary>
        public static readonly Lease Open = new(null);

        private Mutex? _mutex;

        public Lease(Mutex? mutex) => _mutex = mutex;

        public void Dispose()
        {
            Mutex? m = Interlocked.Exchange(ref _mutex, null);
            if (m == null) return;
            try { m.ReleaseMutex(); } catch { /* best effort */ }
            try { m.Dispose(); } catch { /* best effort */ }
        }
    }
}
