using System.Diagnostics;

using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

/// <summary>
/// The ONLY code allowed to end a Revit process, and it may do so for exactly two reasons — the
/// user's rule of 2026-11-24, "kill only what is finished or already dead":
///
/// <list type="number">
/// <item><b>Finished but stuck closing</b> — every model in its group has a row and it asked to close
/// <see cref="ChildLiveness.ForceCloseGrace"/> ago (<see cref="ChildLiveness.ShouldForceClose"/>).</item>
/// <item><b>Already crashed</b> — Autodesk's crash reporter is up for it
/// (<see cref="CrashReporterWatch"/>). The process is inside its crash handler; nothing in it can run
/// again.</item>
/// </list>
///
/// A Revit that is working, however slowly, is still never ended: <see cref="ChildLiveness"/>'s
/// wedged / over-ceiling verdicts set a group aside and leave the window open, as before.
///
/// <para><b>No WM_CLOSE first.</b> Both cases are a Revit whose message loop is already blocked —
/// by a modal dialog that cancelled its own close, or by a crash handler — so a polite close would
/// only spend a minute of the supervisor's thread proving it is ignored.</para>
/// </summary>
internal static class RevitTerminator
{
    /// <summary>Ends a process and waits briefly for it to be gone. True when it no longer exists.
    /// Overridable for the harness, which must never kill anything.</summary>
    internal static Func<int, bool> KillProbe { get; set; } = pid =>
    {
        try
        {
            // The variable name is what install\Verify-NoForcedRevitClose.ps1 allows, in this file
            // only — keep it, so a kill anywhere else still fails the gate.
            using Process finishedOrCrashed = Process.GetProcessById(pid);
            if (finishedOrCrashed.HasExited) return true;
            finishedOrCrashed.Kill();
            return finishedOrCrashed.WaitForExit(30_000);
        }
        catch (ArgumentException) { return true; }   // already gone
        catch { return false; }
    };

    /// <summary>Ends <paramref name="pid"/> and says why on the trail, before and after.</summary>
    public static bool Terminate(int pid, string reason)
    {
        if (pid <= 0) return false;
        RunTrace.Mark($"supervisor force-closing pid={pid}: {reason}");
        bool gone = KillProbe(pid);
        RunTrace.Mark(gone
            ? $"supervisor force-closed pid={pid}"
            : $"supervisor could not force-close pid={pid} — it is still running");
        return gone;
    }
}
