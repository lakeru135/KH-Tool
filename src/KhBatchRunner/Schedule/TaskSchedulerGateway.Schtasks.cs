using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace KhBatchRunner.Schedule;

public static partial class TaskSchedulerGateway
{
    internal static string RegistrationMarkerPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "launcher_" + id + ".marker");

    private static string LauncherVbsPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "run_" + id + ".vbs");

    internal static string LauncherLogPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "launcher_" + id + ".log");

    private static string LegacyLauncherCmdPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "run_" + id + ".cmd");

    /// <summary>
    /// The text that says which registration generation a job's task belongs to, or null when the
    /// job was never registered. XML-v7 keeps it in a marker file because the launcher is now a
    /// shared executable with nowhere to carry a per-job comment; the two script generations it
    /// replaces are still read in place so their tasks can be recognised and upgraded.
    /// </summary>
    internal static string? ExistingRegistrationText(string id)
    {
        foreach (string path in new[]
                 { RegistrationMarkerPath(id), LauncherVbsPath(id), LegacyLauncherCmdPath(id) })
        {
            try { if (File.Exists(path)) return File.ReadAllText(path); }
            catch { /* an unreadable marker is treated as absent, and re-registered */ }
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string TaskXmlPath(string id) =>
        Path.Combine(ScheduleStore.ScheduledDirectory, "task_" + id + ".xml");

    /// <summary>Preserves a rejected task definition for diagnosis. Best effort: a failure to keep
    /// the evidence must never turn into a second, more confusing error.</summary>
    private static string KeepFailedTaskXml(string? xmlPath, string id)
    {
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath)) return "";
        try
        {
            string kept = Path.Combine(ScheduleStore.ScheduledDirectory, "task_" + id + ".rejected.xml");
            File.Copy(xmlPath!, kept, overwrite: true);
            return kept;
        }
        catch { return ""; }
    }

    private static string SchtasksDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "SUN"
    };

    private static string XmlDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Monday",
        DayOfWeek.Tuesday => "Tuesday",
        DayOfWeek.Wednesday => "Wednesday",
        DayOfWeek.Thursday => "Thursday",
        DayOfWeek.Friday => "Friday",
        DayOfWeek.Saturday => "Saturday",
        _ => "Sunday"
    };

    private static string DisplayLocal(DateTime value) =>
        value.ToString("g", CultureInfo.CurrentCulture);

    private static string DisplayTime(DateTime value) =>
        value.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern,
            CultureInfo.CurrentCulture);

    /// <summary>
    /// What Windows recorded as each of our tasks' "Last Run Result", keyed by task name.
    ///
    /// Read in ONE <c>schtasks /Query</c> rather than one per row: a machine can carry hundreds of
    /// tasks and the schedule grid refreshes on every save. Any failure yields an empty map and the
    /// column simply shows nothing — a diagnostic must never be the reason a window fails to open.
    /// </summary>
    internal static Dictionary<string, int> LastRunResults()
    {
        var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            (int code, string output) = RunSchtasks(new List<string> { "/Query", "/FO", "CSV", "/V" });
            if (code != 0) return results;

            string[] lines = output.Split('\n');
            if (lines.Length < 2) return results;

            List<string> header = ParseCsvLine(lines[0]);
            int nameAt = header.FindIndex(h => h.Equals("TaskName", StringComparison.OrdinalIgnoreCase));
            int resultAt = header.FindIndex(h => h.Equals("Last Result", StringComparison.OrdinalIgnoreCase));
            if (nameAt < 0 || resultAt < 0) return results;

            foreach (string line in lines.Skip(1))
            {
                List<string> cells = ParseCsvLine(line);
                if (cells.Count <= Math.Max(nameAt, resultAt)) continue;
                // schtasks reports the full path ("\KhBatchRunner_<id>"); jobs know the leaf name.
                string name = cells[nameAt].TrimStart('\\');
                if (!name.StartsWith("KhBatchRunner_", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(cells[resultAt], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int value))
                    results[name] = value;
            }
        }
        catch { /* the grid shows nothing rather than failing to open */ }
        return results;
    }

    /// <summary>
    /// Leaf names of every registered task whose name starts with <paramref name="prefix"/>. Reads
    /// column 0 of the non-verbose CSV, which is the task name whatever the Windows display
    /// language calls its header. Any failure yields an empty list.
    /// </summary>
    internal static List<string> RegisteredTaskNames(string prefix)
    {
        var names = new List<string>();
        try
        {
            (int code, string output) = RunSchtasks(new List<string> { "/Query", "/FO", "CSV", "/NH" });
            if (code != 0) return names;
            foreach (string line in output.Split('\n'))
            {
                List<string> cells = ParseCsvLine(line);
                if (cells.Count == 0) continue;
                string name = cells[0].TrimStart('\\');
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !names.Contains(name))
                    names.Add(name);
            }
        }
        catch { /* nothing found is the safe answer */ }
        return names;
    }

    /// <summary>Deletes one task by its full leaf name; used only to retire 1.x task names.</summary>
    internal static (bool ok, string message) DeleteTaskByName(string taskName)
    {
        try
        {
            (int code, string output) = RunSchtasks(new List<string> { "/Delete", "/TN", taskName, "/F" });
            return code == 0 ? (true, "deleted") : (false, $"schtasks exit {code}: {output.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Task Scheduler's result code in words. Nobody should have to know that 0x1 is a child
    /// process's exit code rather than a scheduler error — that confusion is what sent the 1.3.4
    /// launcher investigation looking in the wrong place first.
    /// </summary>
    internal static string DescribeLastResult(int code) => code switch
    {
        0 => "OK",
        1 => "Revit exited with an error (1) — see the launcher log",
        4001 => "launcher: bad arguments — re-save this schedule",
        4002 => "launcher: could not start Revit — see the launcher log",
        267009 => "still running",           // 0x41301
        267011 => "has not run yet",         // 0x41303
        267014 => "stopped before it finished",  // 0x41306
        _ => $"0x{code:X}"
    };

    /// <summary>Minimal RFC-4180 splitter — enough for <c>schtasks /FO CSV</c>, whose fields are
    /// all quoted and may contain commas (task paths, descriptions, dates).</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c != '\r') cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>Argument encoding that works on both target frameworks: net8 has ArgumentList,
    /// net48 (Revit 2024) needs the same quoting done by hand.</summary>
    private static void SetArguments(ProcessStartInfo psi, List<string> args)
    {
#if NETFRAMEWORK
        var sb = new StringBuilder();
        foreach (string a in args) AppendArgument(sb, a);
        psi.Arguments = sb.ToString();
#else
        foreach (string a in args) psi.ArgumentList.Add(a);
#endif
    }

    private static (int code, string output) RunSchtasks(List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        SetArguments(psi, args);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (stdout + stderr).Trim());
    }

    // Ported from .NET's PasteArguments.AppendArgument (the encoding ArgumentList uses on net8).
    // Compiled on BOTH frameworks: net48 needs it to build a command line at all, and the task
    // XML's Arguments element is a single string on either, so LauncherArguments needs the same
    // quoting rules Windows will use to take it apart again.
    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (sb.Length != 0) sb.Append(' ');

        if (argument.Length != 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        int idx = 0;
        while (idx < argument.Length)
        {
            char c = argument[idx++];
            if (c == '\\')
            {
                int slashes = 1;
                while (idx < argument.Length && argument[idx] == '\\') { idx++; slashes++; }
                if (idx == argument.Length) sb.Append('\\', slashes * 2);
                else if (argument[idx] == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); idx++; }
                else sb.Append('\\', slashes);
            }
            else if (c == '"') { sb.Append('\\'); sb.Append('"'); }
            else sb.Append(c);
        }
        sb.Append('"');
    }
}
