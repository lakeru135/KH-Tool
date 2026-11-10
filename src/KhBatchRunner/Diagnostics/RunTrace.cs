using System.Diagnostics;
using System.IO;

// MUST be the threading timer, not System.Windows.Forms.Timer (UseWindowsForms makes both visible).
// A WinForms timer ticks on the UI message pump — precisely what a wedged Revit stops pumping — so
// it could never fire in the one situation this watchdog exists for.
using Timer = System.Threading.Timer;

namespace KhBatchRunner.Diagnostics;

/// <summary>
/// Append-only breadcrumb trail for a batch run, at
/// <c>%LOCALAPPDATA%\KhBatchRunner\surface.log</c> (NOT %TEMP% — see <see cref="LogPath"/>).
///
/// A cloud open can hang forever (Revit's native "loading file" progress sitting there with no
/// sign of finishing) or access-violate deep in Revit's worksets load — neither is something a C#
/// try/catch or the tracking CSV can capture, because the CSV row is only written AFTER the model
/// finishes. So every blocking stage is bracketed here: a stage marked with no matching completion
/// mark is a positive identification of the model and the phase that wedged (the Revit journal for
/// the same PID is the other half of the evidence).
///
/// Every line carries the PID because an auto-split run has several Revit instances writing this
/// same file at once (see <see cref="Schedule.ChunkLauncher"/>).
///
/// <see cref="StartWatchdog"/> turns the same state into a liveness check: a background timer that
/// notices when a stage has been running impossibly long and hands the stalled stage back to the
/// caller, which decides what to do about it (log it, and for an unattended run, quit).
/// </summary>
internal static class RunTrace
{
    private const long MaxLogBytes = 5 * 1024 * 1024;

    /// <summary>
    /// How long one stage may run before this in-process watchdog calls it stalled. Deliberately
    /// generous, and a pure BACKSTOP: it is wall-clock only, so it cannot tell a 3 GB model that is
    /// still downloading from one that is wedged. The real decision belongs to
    /// <see cref="Schedule.ChildLiveness"/>, which watches actual progress; this exists for runs
    /// with no supervisor watching them. Sized for the worst case a batch should tolerate rather
    /// than the common one — a false positive here quits an unattended run that was still working.
    /// </summary>
    public static readonly TimeSpan DefaultStallAfter = TimeSpan.FromMinutes(90);

    private static readonly object Gate = new();
    private static bool _rollChecked;

    private static string _stage = "";
    private static DateTime _stageStartedAt = DateTime.Now;

    private static Timer? _watchdog;
    private static TimeSpan _stallAfter;
    private static Action<string, TimeSpan>? _onStall;
    private static bool _stallReported;

    /// <summary>
    /// The trail's full path — quoted in messages so a user can find it.
    ///
    /// Deliberately under %LOCALAPPDATA% rather than %TEMP%. <b>Revit gives every process its own
    /// GUID-named temp directory</b> (<c>…\Local\Temp\&lt;guid&gt;\</c>), so a path built from
    /// <c>Path.GetTempPath()</c> is PRIVATE TO ONE INSTANCE — verified the hard way: an auto-split
    /// run produced four separate surface.logs in four unrelated folders, and the supervising
    /// parent could not see a single mark from its children. Since the whole point of the PID tag
    /// is that several instances share one trail, the location has to be one they agree on.
    /// </summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KhBatchRunner", "surface.log");

    /// <summary>
    /// Records that <paramref name="stage"/> has just begun (or just ended). Best-effort and never
    /// throws — tracing must never be the thing that breaks a run. Also resets the watchdog's
    /// stall clock: progress of any kind means the process is still alive.
    /// </summary>
    public static void Mark(string stage)
    {
        lock (Gate)
        {
            _stage = stage;
            _stageStartedAt = DateTime.Now;
            _stallReported = false;
        }

        try
        {
            string path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={CurrentPid} {stage}{Environment.NewLine}";
            // Several Revit instances append to this one file during an auto-split run, so the
            // append is serialized the same way the CSV loggers are. Without it, two instances
            // writing at the same moment can interleave and produce a line the tail reader
            // silently skips — losing exactly the progress signal it exists to provide.
            Logging.LogFileLock.Write(path, () =>
            {
                RollIfHuge(path);
                File.AppendAllText(path, line);
            });
        }
        catch { /* tracing must never throw */ }
    }

    /// <summary>Cached: <see cref="Process.GetCurrentProcess"/> allocates, and Mark is called on
    /// every stage boundary of every model.</summary>
    private static readonly int CurrentPid = GetPidSafe();

    private static int GetPidSafe()
    {
        try { return Process.GetCurrentProcess().Id; }
        catch { return 0; }
    }

    /// <summary>
    /// Starts the stall watchdog. <paramref name="onStall"/> is invoked ONCE per stage that runs
    /// longer than <paramref name="stallAfter"/>, with the stage text and how long it has been
    /// stuck, and runs on a background thread — the Revit API thread is precisely the one that's
    /// blocked, so the callback must never touch the Revit API. Writing a log row or requesting a
    /// normal window close is safe; terminating the process is deliberately forbidden.
    /// </summary>
    public static void StartWatchdog(TimeSpan stallAfter, Action<string, TimeSpan> onStall)
    {
        StopWatchdog();
        _stallAfter = stallAfter;
        _onStall = onStall;
        lock (Gate)
        {
            _stageStartedAt = DateTime.Now;
            _stallReported = false;
        }
        _watchdog = new Timer(_ => Check(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public static void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        _onStall = null;
    }

    private static void Check()
    {
        string stage;
        TimeSpan stuck;
        lock (Gate)
        {
            if (_stallReported || string.IsNullOrEmpty(_stage)) return;
            stuck = DateTime.Now - _stageStartedAt;
            if (stuck < _stallAfter) return;
            _stallReported = true;      // one report per stage, not one per minute
            stage = _stage;
        }

        Mark($"STALLED {(int)stuck.TotalMinutes}min in: {stage}");
        try { _onStall?.Invoke(stage, stuck); }
        catch { /* the watchdog is a last resort — never let it throw on a pool thread */ }
    }

    /// <summary>One parsed breadcrumb: which instance, what it was doing, and when it said so.</summary>
    public sealed class StageMark
    {
        public int Pid { get; set; }
        public string Stage { get; set; } = "";
        public DateTime At { get; set; }
    }

    /// <summary>
    /// Incremental reader for the shared trail, used by a SUPERVISING process to watch its children.
    /// Every line is PID-tagged, so a parent can learn what each child Revit is doing without any
    /// cross-process channel — the log file is the channel.
    ///
    /// Reads only the bytes appended since the last poll (the file is shared by several writers and
    /// grows for the whole run), keeping the cost independent of how long the run has been going.
    /// A stage transition is a definitive sign of life; the absence of one is NOT a sign of death,
    /// which is why <see cref="Schedule.ChildLiveness"/> weighs it against other signals — a single
    /// "open" stage legitimately spans an entire multi-GB download with no further marks.
    /// </summary>
    public sealed class Tail
    {
        private readonly Dictionary<int, StageMark> _last = new();
        private readonly string _path;
        private long _offset;

        public Tail() : this(LogPath) { }

        /// <summary>
        /// Reads a trail from somewhere other than <see cref="LogPath"/>. For the liveness harness
        /// (<c>tools/LivenessSim</c>), which scripts a fake trail of stage marks rather than running
        /// Revit — and which must not append to the developer's own log to do it.
        /// </summary>
        internal Tail(string path) => _path = path;

        /// <summary>Most recent mark per pid, accumulated across every <see cref="Poll"/>.</summary>
        public IReadOnlyDictionary<int, StageMark> Latest => _last;

        public StageMark? For(int pid) => _last.TryGetValue(pid, out StageMark? m) ? m : null;

        /// <summary>Consumes newly appended lines. Never throws: a tick that can't read the file
        /// simply reports no new marks, and the next tick picks up where this one left off.</summary>
        public void Poll()
        {
            try
            {
                string path = _path;
                if (!File.Exists(path)) return;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                // Rotation (or a cleared log) makes the file shorter than our cursor — restart.
                if (fs.Length < _offset) _offset = 0;
                fs.Seek(_offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                string? line;
                while ((line = sr.ReadLine()) != null) Consume(line);
                _offset = fs.Position;
            }
            catch { /* unreadable this tick; try again next time */ }
        }

        /// <summary>Parses "yyyy-MM-dd HH:mm:ss.fff pid=NNNN &lt;stage&gt;" — the exact shape
        /// <see cref="Mark"/> writes. Anything else is ignored rather than guessed at.</summary>
        private void Consume(string line)
        {
            const string pidToken = " pid=";
            int p = line.IndexOf(pidToken, StringComparison.Ordinal);
            if (p < 0) return;

            int idStart = p + pidToken.Length;
            int idEnd = line.IndexOf(' ', idStart);
            if (idEnd < 0) return;
            if (!int.TryParse(line.Substring(idStart, idEnd - idStart), out int pid)) return;

            if (!DateTime.TryParse(line.Substring(0, p), out DateTime at)) at = DateTime.Now;
            _last[pid] = new StageMark { Pid = pid, Stage = line.Substring(idEnd + 1), At = at };
        }
    }

    /// <summary>Keeps the trail from growing without bound across months of runs: once per process,
    /// an oversized log is rotated to <c>surface.prev.log</c>. Best-effort — if a sibling instance
    /// rotates at the same moment, the loser just keeps appending.</summary>
    private static void RollIfHuge(string path)
    {
        if (_rollChecked) return;
        _rollChecked = true;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxLogBytes) return;
            string prev = Path.Combine(Path.GetDirectoryName(path)!, "surface.prev.log");
            if (File.Exists(prev)) File.Delete(prev);
            File.Move(path, prev);
        }
        catch { /* best effort */ }
    }
}
