using System.Diagnostics;
using System.IO;
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

    /// <summary>
    /// The "Revit 20xx Error Report" WINDOW. senddmp writes the report, starts this, and exits within
    /// a second — so on 2026-11-25 (live test C) two crashed children's dialogs stayed on screen for
    /// 26 minutes while the run recovered without them: matching senddmp alone never saw them.
    /// It is matched by the "crashed-process-id" in its own options file, not by parentage.
    /// </summary>
    public const string DialogProcessName = "cer_dialog";

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

    /// <summary>One running reporter process and the Revit whose crash it reports (senddmp: its parent;
    /// cer_dialog: the "crashed-process-id" of its options file; 0 when unknown, which is ignored).</summary>
    internal readonly record struct Reporter(int Pid, int CrashedPid);

    /// <summary>
    /// The decision, pure: <paramref name="launched"/> is every child pid this run has launched,
    /// alive or not; <paramref name="isAlive"/> asks whether one is still running.
    /// </summary>
    internal static Action Decide(Reporter reporter, ISet<int> launched, Func<int, bool> isAlive)
    {
        if (reporter.CrashedPid <= 0 || !launched.Contains(reporter.CrashedPid)) return Action.Ignore;
        return isAlive(reporter.CrashedPid) ? Action.EndChildAndReporter : Action.EndReporter;
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

    /// <summary>
    /// The crashed Revit's pid named by a running cer_dialog, or 0 when it cannot be read. First its
    /// options file; that file is temporary (none were left in %TEMP% after the 2026-11-25 dialogs),
    /// so then senddmp's own log, which records the crashed pid and, a few lines later,
    /// "cer_dialog.exe is launched with pid N".
    /// </summary>
    private static int DialogCrashedPid(int dialogPid)
    {
        try
        {
            string? file = OptionFileFromCommandLine(CommandLineOf(dialogPid));
            if (file != null && File.Exists(file))
            {
                int fromOptions = CrashedPidFromOptions(File.ReadAllText(file));
                if (fromOptions > 0) return fromOptions;
            }
            return CrashedPidFromSenddmpLog(ReadTail(SenddmpLogPath, 1 << 20), dialogPid);
        }
        catch { return 0; }
    }

    private static string SenddmpLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autodesk", "CER", "senddmp.log");

    /// <summary>
    /// From senddmp.log text: the "crashed-process-id" of the report whose dialog was launched as
    /// <paramref name="dialogPid"/> — the last crashed pid written BEFORE that launch line. 0 when the
    /// launch line is not there. Pure, for the harness.
    /// </summary>
    internal static int CrashedPidFromSenddmpLog(string logText, int dialogPid)
    {
        if (string.IsNullOrEmpty(logText) || dialogPid <= 0) return 0;
        System.Text.RegularExpressions.MatchCollection launches = System.Text.RegularExpressions.Regex.Matches(
            logText, "cer_dialog\\.exe is launched with pid (\\d+)");
        for (int i = launches.Count - 1; i >= 0; i--)
        {
            if (launches[i].Groups[1].Value != dialogPid.ToString(System.Globalization.CultureInfo.InvariantCulture))
                continue;
            string before = logText.Substring(0, launches[i].Index);
            System.Text.RegularExpressions.MatchCollection crashed = System.Text.RegularExpressions.Regex.Matches(
                before, "\"crashed-process-id\"\\s*:\\s*(\\d+)");
            return crashed.Count > 0 && int.TryParse(crashed[crashed.Count - 1].Groups[1].Value, out int pid)
                ? pid : 0;
        }
        return 0;
    }

    /// <summary>The last <paramref name="maxBytes"/> of a file another process may be writing.</summary>
    private static string ReadTail(string path, int maxBytes)
    {
        if (!File.Exists(path)) return "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    /// <summary>The <c>--option-file</c> argument of a cer_dialog command line, quoted or not.
    /// Pure, for the harness.</summary>
    internal static string? OptionFileFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(commandLine,
            "--option-file\\s+(?:\"(?<p>[^\"]+)\"|(?<p>\\S+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["p"].Value : null;
    }

    /// <summary>The "crashed-process-id" in a cer_dialog options file (JSON), or 0. Pure.</summary>
    internal static int CrashedPidFromOptions(string json)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(json ?? "",
            "\"crashed-process-id\"\\s*:\\s*\"?(\\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out int pid) ? pid : 0;
    }

    /// <summary>Another process's command line (same user), via ProcessCommandLineInformation.</summary>
    private static string? CommandLineOf(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buffer, needed, out _) != 0)
                    return null;
                // UNICODE_STRING: ushort Length (bytes), ushort MaximumLength, then the pointer at the
                // next pointer-aligned offset — which is IntPtr.Size on both x64 and x86.
                int bytes = (ushort)Marshal.ReadInt16(buffer);
                IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(h); }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length,
        out int returnLength);

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
