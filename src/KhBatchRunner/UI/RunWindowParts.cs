using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace KhBatchRunner.UI;

/// <summary>
/// What a run window asks before it starts — the text that used to be a Revit TaskDialog
/// ("Cut 6 model(s)…? … Continue?"). <see cref="Heading"/> is the old MainInstruction,
/// <see cref="Details"/> the old MainContent without its trailing "Continue?", and
/// <see cref="StartText"/> the label of the button that goes ahead ("Start", "Publish", …).
/// A command builds ONE of these and hands it to whichever path runs (single process or split),
/// so the user answers once, inside the window that then shows the progress.
/// </summary>
public sealed record RunConfirmation(string Heading, string Details, string StartText = "Start");

/// <summary>Where a run's output lives, derived from its tracking log so nothing else is plumbed.</summary>
internal static class RunResultPaths
{
    /// <summary>
    /// <c>&lt;dest&gt;\Log\x.csv</c> → <c>&lt;dest&gt;</c>. Every tracking log is written under a
    /// <c>Log</c> folder of the run's destination (see TrackingCsvLogger), so the destination is the
    /// parent of that folder. Null when the path is not shaped that way — better no "Open result"
    /// button than one that opens the wrong folder.
    /// </summary>
    internal static string? ResultFolderFor(string? trackingLogPath)
    {
        if (string.IsNullOrWhiteSpace(trackingLogPath)) return null;
        try
        {
            string? logDir = Path.GetDirectoryName(Path.GetFullPath(trackingLogPath));
            if (logDir == null) return null;
            if (!string.Equals(Path.GetFileName(logDir), "Log", StringComparison.OrdinalIgnoreCase))
                return null;
            return Path.GetDirectoryName(logDir);
        }
        catch
        {
            return null;
        }
    }

    internal static bool CanOpenLog(string? logPath)
    {
        try { return !string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath); }
        catch { return false; }
    }

    internal static bool CanOpenResult(string? logPath)
    {
        string? folder = ResultFolderFor(logPath);
        try { return folder != null && Directory.Exists(folder); }
        catch { return false; }
    }

    /// <summary>Opens the CSV in whatever the shell associates with it. Never throws.</summary>
    internal static void OpenLog(string? logPath)
    {
        if (!CanOpenLog(logPath)) return;
        try { Process.Start(new ProcessStartInfo(logPath!) { UseShellExecute = true }); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("open log failed: " + ex.Message); }
    }

    /// <summary>Opens the destination folder in Explorer, passing the path as an argument rather
    /// than shell-executing it. Never throws.</summary>
    internal static void OpenResult(string? logPath)
    {
        string? folder = ResultFolderFor(logPath);
        if (folder == null || !Directory.Exists(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + folder + "\"")
            {
                UseShellExecute = false
            });
        }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("open result failed: " + ex.Message); }
    }
}

/// <summary>
/// The type sizes of the two run windows (progress and watch) — one notch above the 9 pt the rest of
/// the add-in uses, because these are the windows people read from across the room while a batch
/// runs. Scaled by <see cref="Theme.PreviewScale"/> so the layout harness's high-DPI check still
/// sees them grow.
/// </summary>
internal static class RunWindowFonts
{
    internal static Font Body() => new("Segoe UI", 10f * Theme.PreviewScale);

    internal static Font Heading() => new("Segoe UI Semibold", 11f * Theme.PreviewScale);

    /// <summary>Consolas where installed. A missing family would silently become Microsoft Sans
    /// Serif, which is proportional and ruins the column alignment of the log, so check first.</summary>
    internal static Font Log()
    {
        float size = 10f * Theme.PreviewScale;
        try
        {
            using var probe = new FontFamily("Consolas");
            return new Font(probe, size);
        }
        catch (ArgumentException)
        {
            return new Font(FontFamily.GenericMonospace, size);
        }
    }
}

/// <summary>
/// The Confirm-state body of a run window: a heading and the wrapped details, replacing the log
/// pane until the user presses Start.
/// </summary>
internal sealed class RunConfirmPanel : Panel
{
    private readonly Label _heading;
    private readonly Label _details;

    internal RunConfirmPanel()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(4, 4, 4, 0);

        _details = new Label
        {
            Dock = DockStyle.Fill,
            UseMnemonic = false,
            Font = RunWindowFonts.Body(),
            Padding = new Padding(0, 8, 0, 0)
        };
        _heading = new Label
        {
            Dock = DockStyle.Top,
            UseMnemonic = false,
            Font = RunWindowFonts.Heading(),
            Height = 30
        };
        // Fill first, Top last: the Top label docks before the Fill one takes what remains.
        Controls.Add(_details);
        Controls.Add(_heading);
        Resize += (_, _) => FitHeading();
    }

    internal void Set(RunConfirmation confirm)
    {
        _heading.Text = confirm.Heading;
        _details.Text = confirm.Details;
        FitHeading();
    }

    /// <summary>The heading wraps (a long run label can); grow its strip to the wrapped height.</summary>
    private void FitHeading()
    {
        int width = Math.Max(50, ClientSize.Width - Padding.Horizontal);
        try
        {
            int h = TextRenderer.MeasureText(_heading.Text, _heading.Font, new Size(width, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            _heading.Height = Math.Max(30, h + 6);
        }
        catch { /* no device context (off-screen render): keep the default strip */ }
    }
}

/// <summary>
/// The right-aligned button row shared by the run windows. Its buttons change with the window's
/// state: Confirm = [Cancel] [Start]; Running = [Running…] (disabled); Done = [Open result] [Open
/// log] [Close]. The owner subscribes to the events and calls the three Show* methods.
///
/// Cancel is the default and focused button in Confirm, the same safe default the old TaskDialogs
/// had (<c>DefaultButton = No</c>): pressing Enter by reflex must not start a run that publishes
/// new cloud models.
/// </summary>
internal sealed class RunFooter : FlowLayoutPanel
{
    private enum Mode { Confirm, Running, Done }

    private readonly Form _owner;
    // Added right-to-left, so on screen they read tertiary | secondary | primary.
    private readonly Button _primary;      // Start / Running… / Close
    private readonly Button _secondary;    // Cancel / Open log
    private readonly Button _tertiary;     // Open result (Done only)
    private Mode _mode = Mode.Running;

    internal event EventHandler? StartClicked;
    internal event EventHandler? CancelClicked;
    internal event EventHandler? CloseClicked;

    internal RunFooter(Form owner)
    {
        _owner = owner;
        Dock = DockStyle.Bottom;
        FlowDirection = FlowDirection.RightToLeft;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0, 6, 0, 0);

        _primary = NewButton();
        _secondary = NewButton();
        _tertiary = NewButton();
        Controls.Add(_primary);
        Controls.Add(_secondary);
        Controls.Add(_tertiary);

        _primary.Click += (_, _) =>
        {
            if (_mode == Mode.Confirm) StartClicked?.Invoke(this, EventArgs.Empty);
            else if (_mode == Mode.Done) CloseClicked?.Invoke(this, EventArgs.Empty);
        };
        _secondary.Click += (_, _) =>
        {
            if (_mode == Mode.Confirm) CancelClicked?.Invoke(this, EventArgs.Empty);
            else if (_mode == Mode.Done) OpenLogRequested?.Invoke(this, EventArgs.Empty);
        };
        _tertiary.Click += (_, _) =>
        {
            if (_mode == Mode.Done) OpenResultRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    internal event EventHandler? OpenLogRequested;
    internal event EventHandler? OpenResultRequested;

    private static Button NewButton() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(104, 32),
        Font = RunWindowFonts.Body(),
        Margin = new Padding(6, 3, 0, 3)
    };

    internal void ShowConfirm(string startText)
    {
        _mode = Mode.Confirm;
        _primary.Text = string.IsNullOrWhiteSpace(startText) ? "Start" : startText;
        _primary.Enabled = true;
        _primary.Visible = true;
        _secondary.Text = "Cancel";
        _secondary.Visible = true;
        _tertiary.Visible = false;
        _owner.AcceptButton = _secondary;
        _owner.CancelButton = _secondary;
        _owner.ActiveControl = _secondary;
    }

    internal void ShowRunning(string text = "Running…")
    {
        _mode = Mode.Running;
        _primary.Text = text;
        _primary.Enabled = false;
        _primary.Visible = true;
        _secondary.Visible = false;
        _tertiary.Visible = false;
        _owner.AcceptButton = null;
        _owner.CancelButton = null;
    }

    /// <summary>Done: Close always; Open log / Open result only when there is something to open.</summary>
    internal void ShowDone(bool canOpenLog, bool canOpenResult)
    {
        _mode = Mode.Done;
        _primary.Text = "Close";
        _primary.Enabled = true;
        _primary.Visible = true;
        _secondary.Text = "Open log";
        _secondary.Visible = canOpenLog;
        _tertiary.Text = "Open result";
        _tertiary.Visible = canOpenResult;
        _owner.AcceptButton = _primary;
        _owner.CancelButton = _primary;
        _owner.ActiveControl = _primary;
    }
}
