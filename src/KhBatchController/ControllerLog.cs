using System.Diagnostics;
using System.IO;
using System.Text;

namespace KhBatchController;

/// <summary>
/// Controller-side breadcrumbs, written next to the add-in's own <c>broker.log</c> in
/// <c>%TEMP%\KhBatchRunner</c>.
///
/// Exists because the two halves of this system fail in different processes: when the broker never
/// answers, the add-in may not have reached the point of logging anything at all, so the only
/// account of what happened is the launcher's. Console output is not good enough for that job — a
/// double-clicked window can be closed, scrolled away, or simply not read back accurately.
/// </summary>
internal static class ControllerLog
{
    private static readonly string Path_ = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "KhBatchRunner", "controller.log");

    public static string FilePath => Path_;

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);

            // APPENDS a run header rather than truncating. Truncating looked tidier until two
            // controllers ran at once — a short render pass wiped the log of a batch that was still
            // running, destroying the record of it. Several processes share this file; none of them
            // owns it. Old runs are trimmed below instead.
            Trim();
            File.AppendAllText(Path_,
                $"{Environment.NewLine}=== controller run {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                $"(pid {Process.GetCurrentProcess().Id}) ==={Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { /* diagnostics must never break the run */ }
    }

    /// <summary>Keeps the log to a readable length without ever discarding a run in flight.</summary>
    private static void Trim()
    {
        try
        {
            if (!File.Exists(Path_)) return;
            var info = new FileInfo(Path_);
            if (info.Length < 512 * 1024) return;

            string[] lines = File.ReadAllLines(Path_);
            File.WriteAllLines(Path_, lines.Skip(Math.Max(0, lines.Length - 2000)), Encoding.UTF8);
        }
        catch { /* trimming is best-effort */ }
    }

    public static void Write(string line)
    {
        try
        {
            File.AppendAllText(Path_,
                $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* diagnostics must never break the run */ }
    }
}
