using System.Runtime.InteropServices;
using System.Threading;

namespace KhBatchRunner.Diagnostics;

/// <summary>
/// Live fault injection for the two recovery paths 2.1.1.1 added, so they are proven on a real
/// Revit rather than only in <c>tools\LivenessSim</c>. Inert unless BOTH gates hold:
///
/// <list type="bullet">
/// <item>this dll is a DEV install — <c>kh-dev-autostart.flag</c> beside it, which no installer
/// ships (<see cref="Mcp.McpBridge.DevAutoStart"/>); and</item>
/// <item><c>KHBATCH_INJECT</c> is set to one of:
///   <c>crash-run:&lt;model&gt;</c> — a native crash when an action starts on that model (every time,
///   so the retry-once-then-skip rule is exercised end to end);
///   <c>blocked-close</c> — after posting its close, the child never processes it and writes a dialog
///   line four seconds later: the exact trail shape of 2026-11-23 20:18.</item>
/// </list>
/// </summary>
internal static class FaultInjection
{
    public const string EnvVar = "KHBATCH_INJECT";
    private const string CrashRun = "crash-run:";
    private const string BlockedClose = "blocked-close";

    private static string? Spec
    {
        get
        {
            try
            {
                if (!Mcp.McpBridge.DevAutoStart) return null;
                string? s = Environment.GetEnvironmentVariable(EnvVar);
                return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
            }
            catch { return null; }
        }
    }

    /// <summary>Called as an action starts on <paramref name="model"/>.</summary>
    public static void BeforeRun(string model)
    {
        string? spec = Spec;
        if (spec == null || !spec.StartsWith(CrashRun, StringComparison.Ordinal)) return;
        string target = spec.Substring(CrashRun.Length).Trim();
        if (!Batch.BatchStages.SameModel(target, model) &&
            !Batch.BatchStages.SameModel(model, target)) return;

        RunTrace.Mark($"FAULT INJECTION: native crash while running {model}");
        // A thread that starts executing at address 1: an access violation in a frame with no
        // managed code on it, so it reaches the process's unhandled-exception filter — Revit's crash
        // handler and Autodesk's error report — exactly as a crash inside Revit's own code does.
        CreateThread(IntPtr.Zero, UIntPtr.Zero, new IntPtr(1), IntPtr.Zero, 0, out _);
        Thread.Sleep(60_000);
    }

    /// <summary>Called right after the child posts its own close.</summary>
    public static void AfterClosePosted()
    {
        if (!string.Equals(Spec, BlockedClose, StringComparison.Ordinal)) return;
        // Children only: the supervising parent must still be able to finish and close, or the test
        // would leave behind a Revit nothing is entitled to end.
        if (Schedule.ChunkContext.GroupNumber <= 0) return;
        RunTrace.Mark("FAULT INJECTION: holding this Revit open after its close");
        new Thread(() =>
        {
            Thread.Sleep(4_000);
            RunTrace.Mark("foreign dialog is blocking this Revit: INJECTED — close blocker");
        }) { IsBackground = true }.Start();
        Thread.Sleep(Timeout.Infinite);   // the API thread never returns, so the close is never processed
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateThread(IntPtr attributes, UIntPtr stackSize, IntPtr startAddress,
        IntPtr parameter, uint creationFlags, out uint threadId);
}
