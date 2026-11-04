using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Requests a normal Revit shutdown without ever terminating the process.
/// Revit has no public API Quit method, but its top-level Windows window honours WM_CLOSE.
/// If a dialog or genuine hang blocks that close, Revit remains open for inspection.
/// </summary>
internal static class RevitShutdown
{
    /// <summary>
    /// The trail line written when a close has been accepted. Shared with
    /// <see cref="ChildLiveness"/>, which reads it to tell "this instance is SHUTTING DOWN" from
    /// "this instance has stopped responding" — the two look identical from outside (no new stage,
    /// no cache growth, no CPU) and confusing them cost a whole run on 2026-08-11: a child that had
    /// finished all nine of its models and posted this line took longer than the no-progress grace
    /// to actually exit, was judged wedged, and the supervisor cancelled every group still queued.
    /// </summary>
    public const string ClosePostedMark = "normal close posted";

    /// <summary>Posts a close after the caller returns to Revit's message loop.</summary>
    public static void RequestCurrent(string reason)
    {
        int pid;
        try { pid = Process.GetCurrentProcess().Id; }
        catch { return; }

        try
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(100);
                RequestNow(pid, reason);
            })
            {
                IsBackground = true,
                Name = "KhBatchRunner normal Revit shutdown"
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            RunTrace.Mark("normal close could not be queued: " + ex.Message);
        }
    }

    /// <summary>
    /// Posts a close to a specific Revit immediately. Returns whether a top-level window accepted
    /// the request; false leaves the process untouched.
    /// </summary>
    public static bool RequestNow(int pid, string reason)
    {
        if (pid <= 0) return false;
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited) return true;
            process.Refresh();

            bool requested = process.CloseMainWindow();
            if (!requested)
                requested = PostCloseToTopLevelWindows(pid);

            RunTrace.Mark(requested
                ? $"{ClosePostedMark} pid={pid}: {reason}"
                : $"normal close found no top-level window pid={pid}: {reason}");
            return requested;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex)
        {
            RunTrace.Mark($"normal close failed pid={pid}: {ex.Message}");
            return false;
        }
    }

    private static bool PostCloseToTopLevelWindows(int pid)
    {
        bool posted = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint windowPid);
            if (windowPid == (uint)pid && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
                posted |= PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return posted;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private const uint WM_CLOSE = 0x0010;
    private const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
