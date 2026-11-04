using System.Drawing;
using System.Windows.Forms;
using KhBatchController.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

namespace KhBatchController;

/// <summary>
/// A progress view for a run whose parameters are already decided — Schedule Run's "Run now", where
/// the job has just been composed from the form and there is nothing left to pick.
///
/// Kept separate from <see cref="BatchRunWindow"/>, which owns the pickers: this one takes a job and
/// shows it happening. Both funnel into the same <see cref="BatchRun.Execute"/>.
/// </summary>
internal sealed class BatchProgressWindow : Form
{
    private readonly TextBox _output;
    private readonly Label _status;
    private readonly Button _close;

    public BatchProgressWindow(BrokerTokenProvider broker, string revitExe,
                               IReadOnlyList<ModelPreset> sets, IReadOnlyList<RunItem> runItems,
                               string destination, SyncMode sync, BatchCompletion completion,
                               int groupSize, string? projectLogName)
    {
        Text = "Running…";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(720, 420);
        MinimumSize = new Size(600, 320);

        _status = new Label
        {
            Location = new Point(12, 12),
            Size = new Size(696, 20),
            Text = "Starting the worker Revit…",
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(_status);

        _output = new TextBox
        {
            Location = new Point(12, 40),
            Size = new Size(696, 328),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.5F),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };
        Controls.Add(_output);

        _close = new Button
        {
            Text = "Close",
            Location = new Point(608, 380),
            Size = new Size(100, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.OK
        };
        Controls.Add(_close);
        CancelButton = _close;

        Theme.Apply(this);
        _status.ForeColor = Theme.Dim(_status);

        // Started from Load rather than the constructor so the window is on screen before the run
        // begins — the first thing Execute does is a cloud scan that can take a while.
        Load += (_, _) => Start(broker, revitExe, sets, runItems, destination, sync, completion,
                                groupSize, projectLogName);
    }

    private void Start(BrokerTokenProvider broker, string revitExe,
                       IReadOnlyList<ModelPreset> sets, IReadOnlyList<RunItem> runItems,
                       string destination, SyncMode sync, BatchCompletion completion,
                       int groupSize, string? projectLogName)
    {
        var worker = new Thread(() =>
        {
            bool ok;
            try
            {
                ok = BatchRun.Execute(broker, revitExe, sets, runItems, destination, Append,
                                      sync, completion, groupSize, projectLogName);
            }
            catch (Exception ex)
            {
                ControllerLog.Write("run now FAILED: " + ex);
                Append("ERROR: " + ex.Message);
                ok = false;
            }

            if (!IsDisposed)
                BeginInvoke(() =>
                {
                    Text = "Finished";
                    _status.Text = ok ? "Finished." : "Finished with errors — see the log.";
                });
        })
        { IsBackground = true, Name = "ControllerRunNow" };

        worker.Start();
    }

    private void Append(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Append(line)); return; }

        _output.AppendText(line + Environment.NewLine);
    }
}
