using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using KhBatchRunner.Schedule;

// Autodesk.Revit.UI also defines TextBox and Timer; this window means the WinForms ones.
using TextBox = System.Windows.Forms.TextBox;
using Timer = System.Windows.Forms.Timer;

namespace KhBatchRunner.UI;

/// <summary>
/// Overall progress for an AUTO-SPLIT run, shown in the Revit that launched it — the supervisor.
///
/// WHY THIS EXISTS. <see cref="BatchRunnerWindow"/>'s bar belongs to the Revit doing the opening, so
/// a split run had no progress display anywhere: the launching Revit fired off N children, showed a
/// "Launched N instances" dialog and went quiet, while each child worked headless with no window.
/// From the outside that is indistinguishable from a run that has died — which is exactly how it
/// read when several children stalled and the supervisor killed them for making no progress.
///
/// This window watches the ONE unified tracking CSV every child appends to, so it reports the whole
/// set, not any single instance.
///
/// MODELESS on purpose. The point of splitting a run across background Revits is that this one stays
/// usable; a modal progress dialog would take that away. It is safe to be modeless because it never
/// touches the Revit API — it reads a CSV and checks process ids, which is all the supervisor itself
/// does.
/// </summary>
public sealed class RunWatchWindow : Form
{
    private readonly RunManifest _manifest;
    private readonly int _expected;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Round numbers already written to the log, so each is announced once.</summary>
    private readonly HashSet<int> _roundsLogged = new();
    private readonly ProgressBar _bar;
    private readonly StatusLine _status;
    private readonly TextBox _log;
    private readonly Timer _timer;
    private readonly Button _close;
    private readonly bool _autoClose;
    private readonly bool _notifyOnFinish;
    private Timer? _closeTimer;
    private DateTime _lastProgress = DateTime.UtcNow;

    /// <summary>
    /// Set once the run is finished, so the window stops policing itself and the Close button
    /// becomes the only thing left to do. Also guards <see cref="Finish"/> against a second call.
    /// </summary>
    private bool _done;

    /// <summary>Rows whose status is not OK, counted as they are read, so the completion toast can
    /// say how the run actually went rather than just that it ended.</summary>
    private int _failed;

    /// <summary>"Close Revit when complete" was chosen, and this window is the acknowledgement that
    /// releases it. See <see cref="ShowFor"/>.</summary>
    private readonly bool _quitWhenAcknowledged;

    /// <summary>
    /// Opens the watcher for a launched split run, returning it so the supervisor can tell whether
    /// anyone is watching. Never throws into the caller: a failure to display progress must not
    /// fail the run it is reporting on — hence the null return rather than an exception.
    /// </summary>
    /// <param name="quitRevitWhenAcknowledged">
    /// True for "Close Revit when complete". The quit then happens when the user dismisses THIS
    /// window, not the moment the last model lands — otherwise the default disposition would
    /// destroy the only report of the run before it could be read.
    /// </param>
    public static RunWatchWindow? ShowFor(RunManifest manifest, bool quitRevitWhenAcknowledged)
    {
        try
        {
            var window = new RunWatchWindow(manifest, quitRevitWhenAcknowledged,
                autoClose: false, notifyOnFinish: true);
            window.Show();
            return window;
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("run watch window failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Scheduled-run watcher: visible while the unattended parent supervises its child Revits,
    /// then closes automatically. The persistent final notification is raised by the scheduled
    /// runner after it has the authoritative retry-limit result.
    /// </summary>
    public static RunWatchWindow? ShowForScheduled(RunManifest manifest)
    {
        try
        {
            var window = new RunWatchWindow(manifest, quitRevitWhenAcknowledged: false,
                autoClose: true, notifyOnFinish: false);
            window.Show();
            return window;
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("scheduled run watch window failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Internal rather than private so the render harness can lay it out without a run.
    /// Callers open it through <see cref="ShowFor"/>.</summary>
    internal RunWatchWindow(RunManifest manifest, bool quitRevitWhenAcknowledged = false,
        bool autoClose = false, bool notifyOnFinish = true)
    {
        _manifest = manifest;
        _quitWhenAcknowledged = quitRevitWhenAcknowledged;
        _autoClose = autoClose;
        _notifyOnFinish = notifyOnFinish;

        int passes = Math.Max(1, manifest.BaseJob?.RunItems.Count ?? 1);
        _expected = Math.Max(1, manifest.AllModels.Count * passes);

        Text = $"Batch progress — {manifest.Name}";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 420);
        MinimumSize = new Size(560, 320);
        ShowInTaskbar = true;

        _bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 22, Minimum = 0, Maximum = _expected };
        _status = new StatusLine { DetailsTitle = Text, BorderStyle = BorderStyle.None, Padding = new Padding(0) };
        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.5F),
            BorderStyle = BorderStyle.FixedSingle
        };

        // Disabled until the run ends. The window is the only place the overall result appears, so
        // it waits to be acknowledged rather than vanishing the moment the last model lands — you
        // are usually not looking at the screen when that happens.
        _close = new Button
        {
            Text = "Running…",
            Enabled = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 26),
            Anchor = AnchorStyles.Right
        };
        _close.Click += (_, _) => Close();
        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 6, 0, 0)
        };
        buttonRow.Controls.Add(_close);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        host.Controls.Add(_log);
        host.Controls.Add(_status);
        host.Controls.Add(_bar);
        host.Controls.Add(buttonRow);
        Controls.Add(host);
        AcceptButton = _close;

        Theme.Apply(this);

        int groups = manifest.CurrentRound?.Groups.Count ?? 0;
        Append($"{manifest.AllModels.Count} model(s) x {passes} task(s) in {groups} Revit instance(s).");
        Append($"Log: {manifest.TrackingLogPath}");
        Append("");
        _status.SetMessage($"0 of {_expected} — starting…");

        _timer = new Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        try
        {
            ReportNewRounds();

            foreach (string row in NewRows())
            {
                Append($"[{_seen.Count}/{_expected}] {row}");
                _lastProgress = DateTime.UtcNow;
            }

            _bar.Value = Math.Min(_seen.Count, _bar.Maximum);

            bool anyAlive = (_manifest.CurrentRound?.Groups ?? new List<RunGroup>())
                .Any(g => g.Pid != 0 && IsAlive(g.Pid));

            if (_manifest.State == RunState.Stopped)
            {
                string reason = string.IsNullOrWhiteSpace(_manifest.StopReason)
                    ? "A live Revit worker needs attention."
                    : _manifest.StopReason;
                Finish("Stopped — " + reason + " The worker was left open for inspection.", false);
            }
            else if (_manifest.Unrecoverable.Count > 0)
            {
                int stopped = _manifest.Unrecoverable.Count;
                Finish($"Stopped after {Schedule.RunSupervisor.MaxRounds} attempts — " +
                       $"{stopped} model(s) could not be completed.", false);
            }
            else if (_seen.Count >= _expected)
            {
                Finish($"Done — {_seen.Count} of {_expected} result(s).");
            }
            else if (_manifest.State == RunState.Completed)
            {
                Finish($"Done — {_seen.Count} of {_expected} result(s).");
            }
            else if (!anyAlive)
            {
                // Every instance gone with results missing. Not necessarily the end: the supervisor
                // reruns models that produced no row, and its next round starts new instances — so
                // this reports the lull rather than declaring failure.
                _status.SetMessage($"{_seen.Count} of {_expected} — no instance running; " +
                               "the supervisor reruns anything missing.");
            }
            else
            {
                int quiet = (int)(DateTime.UtcNow - _lastProgress).TotalMinutes;
                _status.SetMessage($"{_seen.Count} of {_expected} — running" +
                               (quiet >= 2 ? $", {quiet} min since the last result (a cold cloud model can take a while)" : "…"));
            }
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("run watch poll failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Logs each round as the supervisor opens it, with the shape of its split. Round 1 is the run
    /// you asked for; 2 and 3 are recoveries, and seeing "6 model(s) in 2 instance(s): 4 / 2" is
    /// what tells you the run hit trouble and what it is doing about it.
    ///
    /// Read from the manifest, which the supervisor updates in place, so no extra plumbing is
    /// needed between the two.
    /// </summary>
    private void ReportNewRounds()
    {
        foreach (RunRound round in _manifest.Rounds)
        {
            if (!_roundsLogged.Add(round.Round)) continue;

            var sizes = round.Groups.Select(g => g.Models.Count).ToList();
            int models = sizes.Sum();
            string shape = sizes.Count > 0 ? string.Join(" / ", sizes) : "—";
            string prefix = round.Round == 1 ? "" : "retry ";

            // Groups are not instances. This line used to call them instances, which is why a run
            // split into six could be read as six Revits when the machine's budget only ever allows
            // a few at once — the rest queue for a free slot, and saying so here is the difference
            // between "it is working" and "half of it never started".
            int cap = _manifest.MaxConcurrentInstances;
            string howMany = cap > 0 && sizes.Count > cap
                ? $"{sizes.Count} group(s), {cap} Revit at a time"
                : $"{sizes.Count} Revit instance(s)";
            Append($"— {prefix}round {round.Round} of {Schedule.RunSupervisor.MaxRounds}: " +
                   $"{models} model(s) in {howMany}  [{shape}]");
        }
    }

    private void Finish(string message, bool fillProgress = true)
    {
        if (_done) return;
        _done = true;

        _timer.Stop();
        if (fillProgress) _bar.Value = _bar.Maximum;
        _status.SetMessage(message);
        Append("");
        Append(message);

        if (_autoClose)
        {
            _close.Text = "Closing…";
            _close.Enabled = false;
            _closeTimer = new Timer { Interval = 1500 };
            _closeTimer.Tick += (_, _) =>
            {
                _closeTimer?.Stop();
                Close();
            };
            _closeTimer.Start();
        }
        else
        {
            // Hand the window over to the user: it now says what happened and waits to be dismissed.
            _close.Text = "OK";
            _close.Enabled = true;
            _close.Focus();
        }

        if (_notifyOnFinish)
        {
            // …and tell them even if this window is behind Revit, or behind everything.
            int failed = _failed;
            if (_manifest.State == RunState.Stopped)
                RunNotification.ShowRunNeedsAttention(_manifest.Name,
                    Math.Max(0, _seen.Count - failed), failed, _manifest.StopReason,
                    _manifest.TrackingLogPath);
            else if (_manifest.Unrecoverable.Count > 0)
                RunNotification.ShowRunStopped(_manifest.Name, Math.Max(0, _seen.Count - failed), failed,
                    Schedule.RunSupervisor.MaxRounds,
                    _manifest.UnrecoverableDisplayNames(), _manifest.TrackingLogPath);
            else
                RunNotification.ShowRunFinished(_manifest.Name, _seen.Count - failed, failed,
                    _manifest.TrackingLogPath);
        }
    }

    /// <summary>
    /// Rows added since the last poll, keyed by model + task so a run with several tasks reports each
    /// one. Opened share-all: several Revits are appending to this file right now.
    /// </summary>
    private IEnumerable<string> NewRows()
    {
        string path = _manifest.TrackingLogPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) yield break;

        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch { yield break; } // mid-write; next poll

        foreach (string raw in lines.Skip(1))
        {
            string line = raw.Trim('\r', ' ');
            if (line.Length == 0) continue;

            string[] cells = SplitCsvLine(line);
            if (cells.Length < 4) continue;

            string model = cells[1];
            string task = cells[2];
            // The launcher writes INFO rows with no model; they are notes, not results.
            if (model.Length == 0) continue;
            if (!_seen.Add(model + " " + task)) continue;
            if (!cells[3].Equals("OK", StringComparison.OrdinalIgnoreCase)) _failed++;

            string message = cells.Length > 4 ? cells[4] : "";
            yield return $"{cells[3],-7} {model}  [{task}]" +
                         (message.Length > 0 ? $"  — {message}" : "");
        }
    }

    /// <summary>Splits a CSV record, honouring the doubled-quote escaping <see cref="Logging.CsvFormat"/>
    /// writes — the Message column routinely contains commas.</summary>
    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c != '"') { current.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = false;
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    private void Append(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _closeTimer?.Stop();
        _closeTimer?.Dispose();
        base.OnFormClosed(e);

        // The user has seen the result, so "Close Revit when complete" can finally happen. Closing
        // this window BEFORE the run finished is not an acknowledgement — the run is still going,
        // and RunSupervisor takes the quit back (it checks IsDisposed).
        if (_done && _quitWhenAcknowledged && _manifest.State == RunState.Completed)
        {
            Diagnostics.RunTrace.Mark("quitting the supervising Revit (acknowledged by the user)");
            Schedule.RevitShutdown.RequestCurrent("split-run result acknowledged");
        }
    }
}
