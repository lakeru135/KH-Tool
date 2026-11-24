using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Finds Autodesk's crash reporter (<c>senddmp.exe</c> — "Revit 20xx Error Report") belonging to a
/// child Revit this run launched, so a crashed child neither lingers nor leaves windows behind.
///
/// <para><b>Why it is needed.</b> A crashed Revit is reported through CER: the crash handler starts
/// <c>senddmp.exe</c> and the "A software problem has caused Revit to close unexpectedly" window
/// waits for a click. While it waits, the Revit process may still exist — every liveness signal
/// flat, mid-open or mid-action — so <see cref="ChildLiveness"/> would give it the 30–60 minute
/// grace of a slow open or action and then leave it open. Even when Revit does exit, the reporter's
/// window stays on an unattended PC until somebody closes it (the user's report, 2026-11-24).</para>
///
/// <para><b>Matched by parent pid, never by window title or name alone.</b> The reporter of the
/// user's OWN Revit, or of any other program, must never be touched. Only a <c>senddmp</c> whose
/// parent is a pid this run launched counts.</para>
/// </summary>
internal static class CrashReporterWatch
{
    public const string ReporterProcessName = "senddmp";

    /// <summary>What to do about one reporter process.</summary>
    internal enum Action
    {
        /// <summary>Not ours — its parent is not a child of this run.</summary>
        Ignore,
        /// <summary>Its child Revit is still alive inside the crash handler: end the Revit, then the
        /// reporter.</summary>
        EndChildAndReporter,
        /// <summary>The child is already gone; only the reporter window is left behind.</summary>
        EndReporter,
    }

    /// <summary>One running reporter and the process that started it.</summary>
    internal readonly record struct Reporter(int Pid, int ParentPid);

    /// <summary>
    /// The decision, pure: <paramref name="launched"/> is every child pid this run has launched,
    /// alive or not; <paramref name="isAlive"/> asks whether one is still running.
    /// </summary>
    internal static Action Decide(Reporter reporter, ISet<int> launched, Func<int, bool> isAlive)
    {
        if (reporter.ParentPid <= 0 || !launched.Contains(reporter.ParentPid)) return Action.Ignore;
        return isAlive(reporter.ParentPid) ? Action.EndChildAndReporter : Action.EndReporter;
    }

    /// <summary>Every running <c>senddmp.exe</c> with its parent pid. Overridable for the
    /// harness.</summary>
    internal static Func<IReadOnlyList<Reporter>> Probe { get; set; } = FindReporters;

    private static IReadOnlyList<Reporter> FindReporters()
    {
        var found = new List<Reporter>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return found;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return found;
            do
            {
                string exe = entry.szExeFile ?? "";
                if (exe.Equals(ReporterProcessName + ".exe", StringComparison.OrdinalIgnoreCase))
                    found.Add(new Reporter((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
            }
            while (Process32Next(snap, ref entry));
        }
        catch { /* a snapshot that cannot be walked finds nothing this poll */ }
        finally { CloseHandle(snap); }
        return found;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
