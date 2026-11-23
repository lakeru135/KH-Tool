using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace KhBatchRunner.Diagnostics;

/// <summary>
/// Where one model's time went, phase by phase, written to
/// <c>%LOCALAPPDATA%\KhBatchRunner\timings.csv</c> and as one line on the <see cref="RunTrace"/>
/// trail when the model closes.
///
/// <para><b>Why.</b> A model used to leave three lumped numbers on the trail (<c>opened in</c>,
/// <c>ran in</c>, <c>closed in</c>). That cannot say whether a slow Export NWC run is the first
/// open, the workset reopen, the contents probe, the export call or the wait for a slot, and those
/// need different fixes. This measures; it changes nothing about what a run does.</para>
///
/// <para><b>Not in the destination folder</b>, deliberately: the run's own reports stay exactly as
/// they were, so a before/after comparison of outputs is not disturbed by the measurement.</para>
///
/// <para><b>No trail marks between phases.</b> The supervisor reads the LAST trail line of each
/// child to decide whether it is inside an open or an action
/// (<see cref="Batch.BatchStages.IsOpenStage"/>, <see cref="Batch.BatchStages.IsActionRunStage"/>).
/// A mark written mid-open would replace that line and take the open's grace away, so phases are
/// accumulated in memory and written once, after the model's <c>closed in</c> mark.</para>
///
/// <para>One row per phase (long format): <c>Timestamp, Pid, Group, Action, Model, Phase, Ms,
/// Count, PeakWorkingSetMB, FreeRamMB, Note</c>. Phases nested inside the action are named
/// <c>run/…</c>; they are part of <c>run</c>, not additional to it. Run-level events (start-up,
/// waits) have a blank Model. Never throws: a measurement must not be able to fail a run.</para>
///
/// <para><b>The one thing a later run reads.</b> While a model is in hand the process's working set
/// is sampled once a second, and the highest sample is the model's own peak (the
/// <c>PeakWorkingSetMB</c> column is the process's lifetime peak, which every later model of the same
/// Revit inherits). For a whole-view export that peak goes to <see cref="Schedule.ModelMemory"/>,
/// which is what lets <see cref="Schedule.InstanceBudget"/> budget a set of known-light models by
/// what they measured.</para>
/// </summary>
internal static class PhaseTimings
{
    private const long MaxCsvBytes = 5 * 1024 * 1024;
    private const string Header =
        "Timestamp,Pid,Group,Action,Model,Phase,Ms,Count,PeakWorkingSetMB,FreeRamMB,Note";

    private static readonly object Gate = new();
    private static bool _rollChecked;

    private sealed class Entry
    {
        public string Action = "";
        public string Phase = "";
        public long Ms;
        public int Count;
    }

    // The model in hand. Models are processed one at a time on the Revit API thread, so one slot
    // is enough; the lock only guards against a stray background caller.
    private static string? _model;
    private static string _action = "";
    private static Stopwatch? _total;
    private static string _modelKey = "";
    private static long _modelPeakBytes;
    private static System.Threading.Timer? _sampler;
    private static readonly List<Entry> Entries = new();

    public static string CsvPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KhBatchRunner", "timings.csv");

    /// <summary>Starts collecting for <paramref name="model"/>. A model still in hand (its close
    /// never came) is written out first rather than dropped.</summary>
    public static void Begin(string action, string model)
    {
        try
        {
            lock (Gate)
            {
                if (_model != null) FlushLocked("superseded before close");
                _model = model;
                _action = action;
                _total = Stopwatch.StartNew();
                Entries.Clear();
                _modelKey = Batch.ModelContext.CurrentKey ?? "";
                _modelPeakBytes = WorkingSetBytes();
                // One timer for the life of the process; it does nothing while no model is in hand.
                _sampler ??= new System.Threading.Timer(_ => Sample(), null, 1000, 1000);
            }
        }
        catch { /* measuring must never throw */ }
    }

    private static void Sample()
    {
        try
        {
            if (_model == null) return;          // unlocked peek: a missed tick costs nothing
            long now = WorkingSetBytes();
            lock (Gate) { if (_model != null && now > _modelPeakBytes) _modelPeakBytes = now; }
        }
        catch { /* measuring must never throw */ }
    }

    private static long WorkingSetBytes()
    {
        try { using Process p = Process.GetCurrentProcess(); return p.WorkingSet64; }
        catch { return 0; }
    }

    /// <summary>Whether <paramref name="action"/> is one whose memory the budget is sized by — the
    /// same two <see cref="Schedule.InstanceBudget.WeightFor"/> calls a whole-view export.</summary>
    private static bool IsWholeViewExport(string action) =>
        action == Actions.ActionNames.ExportNwc || action == Actions.ActionNames.ExportIfcData;

    /// <summary>Names the action later phases belong to: a pass that joins a document an earlier
    /// pass opened is a different action on the same model.</summary>
    public static void SetAction(string action)
    {
        lock (Gate) { if (_model != null) _action = action; }
    }

    /// <summary>Times a phase of the model in hand: <c>using (PhaseTimings.Time("first-open"))</c>.
    /// Repeats of the same phase add up, and <c>Count</c> says how many there were. A no-op when no
    /// model is in hand.</summary>
    public static IDisposable Time(string phase) => new Scope(phase);

    private sealed class Scope : IDisposable
    {
        private readonly string _phase;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private bool _done;

        public Scope(string phase) => _phase = phase;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Add(_phase, _watch.ElapsedMilliseconds);
        }
    }

    public static void Add(string phase, long ms)
    {
        try
        {
            lock (Gate)
            {
                if (_model == null) return;
                Entry? e = Entries.Find(x => x.Phase == phase && x.Action == _action);
                if (e == null)
                {
                    e = new Entry { Action = _action, Phase = phase };
                    Entries.Add(e);
                }
                e.Ms += ms;
                e.Count++;
            }
        }
        catch { /* measuring must never throw */ }
    }

    /// <summary>Writes the model in hand: its rows to the CSV and one summary line to the trail.
    /// Called after the model's close. A no-op when nothing was begun.</summary>
    public static void End()
    {
        try { lock (Gate) FlushLocked(""); }
        catch { /* measuring must never throw */ }
    }

    /// <summary>A run-level duration that belongs to no model — start-up, a stagger, a wait for a
    /// slot. Written straight away.</summary>
    public static void RunEvent(string phase, long ms, string note = "")
    {
        try
        {
            var sb = new StringBuilder();
            AppendRow(sb, "", "", phase, ms, 1, "", "", note);
            WriteRows(sb.ToString());
            RunTrace.Mark($"timing: {phase} {ms} ms" + (note.Length > 0 ? $" ({note})" : ""));
        }
        catch { /* measuring must never throw */ }
    }

    /// <summary>Milliseconds since this Revit process started, or -1 when that cannot be read.</summary>
    public static long SinceProcessStartMs()
    {
        try
        {
            using Process p = Process.GetCurrentProcess();
            return (long)(DateTime.Now - p.StartTime).TotalMilliseconds;
        }
        catch { return -1; }
    }

    private static void FlushLocked(string note)
    {
        if (_model == null) return;
        string model = _model;
        long total = _total?.ElapsedMilliseconds ?? 0;
        (string peak, string free) = Memory();
        long modelPeakMb = Math.Max(_modelPeakBytes, WorkingSetBytes()) / (1024 * 1024);
        string modelKey = _modelKey;
        // Only a model whose export actually ran says anything about what the export costs.
        bool exported = Entries.Exists(e => e.Phase == "run" && IsWholeViewExport(e.Action));
        if (modelPeakMb > 0)
            note = (note.Length > 0 ? note + "; " : "") + $"model peak {modelPeakMb} MB";

        var sb = new StringBuilder();
        var line = new StringBuilder();
        foreach (Entry e in Entries)
        {
            AppendRow(sb, e.Action, model, e.Phase, e.Ms, e.Count, "", "", "");
            if (line.Length > 0) line.Append(", ");
            line.Append(e.Phase).Append(' ').Append(e.Ms.ToString(CultureInfo.InvariantCulture));
            if (e.Count > 1) line.Append(" (x").Append(e.Count).Append(')');
        }
        AppendRow(sb, _action, model, "total", total, 1, peak, free, note);

        _model = null;
        _total = null;
        _modelKey = "";
        _modelPeakBytes = 0;
        Entries.Clear();
        if (exported) Schedule.ModelMemory.Record(modelKey, modelPeakMb);

        WriteRows(sb.ToString());
        // Shaped like every other model line ("action | model | stage") so the supervisor reads it
        // as work on this model, and deliberately not an open or a run stage.
        RunTrace.Mark($"{_action} | {model} | timings ms: {line}; total {total}" +
                      (modelPeakMb > 0 ? $"; model peak {modelPeakMb} MB" : "") +
                      (peak.Length > 0 ? $"; peak working set {peak} MB" : "") +
                      (free.Length > 0 ? $"; free RAM {free} MB" : ""));
    }

    private static (string PeakMb, string FreeMb) Memory()
    {
        string peak = "", free = "";
        try
        {
            using Process p = Process.GetCurrentProcess();
            peak = (p.PeakWorkingSet64 / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        }
        catch { /* unreadable: leave blank */ }
        try
        {
            long mb = Schedule.InstanceBudget.FreeMemoryMb();
            if (mb >= 0) free = mb.ToString(CultureInfo.InvariantCulture);
        }
        catch { /* unreadable: leave blank */ }
        return (peak, free);
    }

    private static readonly int Pid = GetPid();

    private static int GetPid()
    {
        try { using Process p = Process.GetCurrentProcess(); return p.Id; }
        catch { return 0; }
    }

    private static void AppendRow(StringBuilder sb, string action, string model, string phase,
        long ms, int count, string peakMb, string freeMb, string note)
    {
        int group = Schedule.ChunkContext.GroupNumber;
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',')
          .Append(Pid.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(group > 0 ? group.ToString(CultureInfo.InvariantCulture) : "").Append(',')
          .Append(Csv(action)).Append(',')
          .Append(Csv(model)).Append(',')
          .Append(Csv(phase)).Append(',')
          .Append(ms.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(count.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(peakMb).Append(',')
          .Append(freeMb).Append(',')
          .Append(Csv(note)).Append(Environment.NewLine);
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    private static void WriteRows(string rows)
    {
        string path = CsvPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Several Revit instances of a split run write this one file, same as the trail.
        Logging.LogFileLock.Write(path, () =>
        {
            RollIfHuge(path);
            if (!File.Exists(path)) File.AppendAllText(path, Header + Environment.NewLine);
            File.AppendAllText(path, rows);
        });
    }

    private static void RollIfHuge(string path)
    {
        if (_rollChecked) return;
        _rollChecked = true;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxCsvBytes) return;
            string prev = Path.Combine(Path.GetDirectoryName(path)!, "timings.prev.csv");
            if (File.Exists(prev)) File.Delete(prev);
            File.Move(path, prev);
        }
        catch { /* best effort */ }
    }
}
