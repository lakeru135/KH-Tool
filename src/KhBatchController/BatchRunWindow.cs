using System.Linq;
using System.Windows.Forms;
using KhBatchController.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;
using KhBatchRunner.Scripts;
using KhBatchRunner.UI;

namespace KhBatchController;

/// <summary>
/// Batch Run, controller-side.
///
/// The add-in's own <c>UI/BatchRunWindow</c> could not come across: it fills its action checklist by
/// constructing <c>ActionRegistry</c> entries, which are Revit-typed. Nothing else about batch
/// running needs Revit in THIS process though — the worker is a separate Revit either way — so this
/// window asks the broker for the action list instead and reuses the same
/// <see cref="BatchRun.Execute"/> the console flow uses. Same job, same launch path, same log.
///
/// Deliberately not a port: a port would have meant decoupling the registry from
/// <c>IBatchAction</c> across the whole add-in. This is the smaller honest version, and the add-in's
/// window keeps working unchanged for people running inside Revit.
/// </summary>
internal sealed class BatchRunWindow : Form
{
    private readonly BrokerTokenProvider _broker;
    private readonly string _revitExe;

    /// <summary>Marks a script-set entry in the shared run list, so one checklist can carry both
    /// kinds in the user's chosen order — the same thing <c>RunItem.Kind</c> records in the job.</summary>
    private const string ScriptPrefix = "DYN: ";

    private readonly CheckedListBox _setsList;
    private readonly CheckedListBox _actionsList;
    private readonly ComboBox _syncCombo;
    private readonly ComboBox _completionCombo;
    private readonly Label _splitNote;
    private readonly TextBox _destinationBox;
    private readonly TextBox _projectLogBox;
    private readonly TextBox _output;
    private readonly Button _runButton;
    private readonly Label _status;

    private readonly List<ModelPreset> _sets;
    private readonly Dictionary<string, ScriptSet> _scriptSets = new();
    private readonly string? _preselectAction;

    /// <param name="preselectAction">
    /// Ticked on open. The launcher's per-action buttons pass this, so a ribbon-style
    /// "Get Phase &amp; Workset" button lands you in Batch Run with only that action chosen —
    /// which is exactly what the ribbon button does, a single-action run over a chosen set.
    /// </param>
    public BatchRunWindow(BrokerTokenProvider broker, string revitExe, string? preselectAction = null)
    {
        _broker = broker;
        _revitExe = revitExe;
        _preselectAction = preselectAction;
        // Same order the add-in uses: sets this account cannot open sink to the bottom.
        _sets = KhBatchRunner.UI.ModelSetAccess.Ordered(PresetStore.LoadAll()).ToList();

        Text = "Batch Run — run actions over model sets";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(760, 610);
        MinimumSize = new Size(760, 560);

        // Laid out with tables rather than fixed coordinates, so the combos, the destination box and
        // the output pane follow the window when it is resized. Two list columns share the width
        // equally (the old code re-positioned them in a Resize handler to do the same thing).
        Button Mk(string text, int minW)
        {
            return new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(minW, 0), Margin = new Padding(3, 2, 3, 2)
            };
        }
        Label Heading(string text) =>
            new Label { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 2) };

        // IntegralHeight keeps the box a whole number of rows tall. With it off, the last row was
        // sliced in half and read as a rendering fault rather than "scroll for more".
        // Dock=Fill with a floor, so the lists grow with the window but never collapse.
        _setsList = new CheckedListBox
        {
            Dock = DockStyle.Fill, MinimumSize = new Size(0, 154), CheckOnClick = true,
            IntegralHeight = true, Margin = new Padding(3, 0, 8, 3)
        };
        foreach (ModelPreset s in _sets)
            _setsList.Items.Add(KhBatchRunner.UI.ModelSetAccess.Label(s, $"{s.Name}  ({s.Models.Count})"));

        _actionsList = new CheckedListBox
        {
            Dock = DockStyle.Fill, MinimumSize = new Size(0, 154), CheckOnClick = true,
            IntegralHeight = true, Margin = new Padding(8, 0, 3, 3)
        };
        // Script sets sit in the same list as actions, so a run can interleave them the way the
        // add-in's RunItems list does — order in the list is order of execution.
        foreach (ScriptSet script in ScriptSetStore.LoadAll())
        {
            _actionsList.Items.Add(ScriptPrefix + script.Name);
            _scriptSets[ScriptPrefix + script.Name] = script;
        }

        var lists = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0),
            // The row holds a heading plus the 154px list floor; a percent row does not report its
            // children's minimums to the parent table, so say it here or the window can be fitted too short.
            MinimumSize = new Size(0, 190)
        };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lists.Controls.Add(Heading("Model sets"), 0, 0);
        lists.Controls.Add(Heading("Actions"), 1, 0);
        lists.Controls.Add(_setsList, 0, 1);
        lists.Controls.Add(_actionsList, 1, 1);

        // ---- options, mirroring the add-in's Batch Run dialog --------------------------------
        _syncCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _syncCombo.Items.Add("Sync changes to central");
        _syncCombo.Items.Add("Don't sync (dry run)");
        _syncCombo.SelectedIndex = 1; // Dry run (default), matching the add-in's dialogs.

        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _completionCombo.Items.Add("Close files");
        _completionCombo.Items.Add("Leave files open");
        _completionCombo.Items.Add("Close Revit when complete");
        _completionCombo.SelectedIndex = 2; // an unattended worker should quit, as in the add-in
        _completionCombo.SelectedIndexChanged += (_, _) => UpdateGroupEnabled();

        // No split-size box: the shape is derived from the model count and this PC, exactly as in
        // the add-in (InstanceBudget.AutoGroupSize). What is worth saying is that it happens at all,
        // and the one setting that turns it off. Two short lines so it fits the third column at the
        // minimum width; the "Leave files open" caveat is carried by DIMMING the note
        // (UpdateGroupEnabled) rather than by more words. How many workers this PC allows is reported
        // by BatchRun as "Limit : …" when the run starts, which is where the number is actually useful.
        _splitNote = new Label
        {
            Text = "Large runs auto-split across\nseveral Revit workers.",
            AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.Left, Margin = new Padding(12, 0, 3, 3)
        };

        // Heading row above, combo row below, three equal columns; the note sits under the third heading.
        var options = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, RowCount = 2, Margin = new Padding(0)
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4f));
        options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        options.Controls.Add(Heading("Model treatment"), 0, 0);
        options.Controls.Add(Heading("When finished"), 1, 0);
        options.Controls.Add(_syncCombo, 0, 1);
        options.Controls.Add(_completionCombo, 1, 1);
        options.Controls.Add(_splitNote, 2, 1);

        _destinationBox = new TextBox
        {
            Dock = DockStyle.Fill, Text = ScheduleStore.DefaultDestinationFolder()
        };
        // A real button: the old 36px "…" was easy to miss and gave no hint what it did.
        Button browse = Mk("Browse…", 80);
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _destinationBox.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _destinationBox.Text = dialog.SelectedPath;
        };
        var destinationRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = new Padding(0)
        };
        destinationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destinationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destinationRow.Controls.Add(_destinationBox, 0, 0);
        destinationRow.Controls.Add(browse, 1, 0);

        _projectLogBox = new TextBox { Width = 230 };
        var projectLogRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0)
        };
        projectLogRow.Controls.Add(_projectLogBox);
        projectLogRow.Controls.Add(new Label
        {
            Text = "(blank = the model set's name)", AutoSize = true, Margin = new Padding(8, 6, 3, 0)
        });

        _runButton = Mk("Run now", 120);
        _runButton.Click += OnRun;
        _status = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
            Text = "Runs in a separate Revit; this window stays usable.", Margin = new Padding(8, 0, 3, 0)
        };
        var runRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 0)
        };
        runRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        runRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        runRow.Controls.Add(_runButton, 0, 0);
        runRow.Controls.Add(_status, 1, 0);

        _output = new TextBox
        {
            Dock = DockStyle.Fill, MinimumSize = new Size(0, 90), Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 8.5F),
            Margin = new Padding(3, 6, 3, 3)
        };

        // Close, bottom-right: the window had no way out but the title-bar X.
        var closeButton = Mk("Close", 90);
        closeButton.DialogResult = DialogResult.Cancel;
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 6, 0, 0)
        };
        footer.Controls.Add(closeButton);
        CancelButton = closeButton;

        // One column. The lists and the output pane are the two rows that flex (55 / 45 of what the
        // fixed rows leave); every other row is auto-height so nothing overlaps at high DPI.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Auto(Control c) { root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.Controls.Add(c, 0, root.RowStyles.Count - 1); }
        void Fill(Control c, float pct) { root.RowStyles.Add(new RowStyle(SizeType.Percent, pct)); root.Controls.Add(c, 0, root.RowStyles.Count - 1); }
        Fill(lists, 55);
        Auto(options);
        Auto(Heading("Log destination"));
        Auto(destinationRow);
        Auto(Heading("Project log name"));
        Auto(projectLogRow);
        Auto(runRow);
        Fill(_output, 45);
        Auto(footer);
        Controls.Add(root);

        Theme.Apply(this);
        _status.ForeColor = Theme.Dim(_status);

        // Fit to the content and refuse to be dragged below it: the fill rows only flex ABOVE their
        // minimums, so a window smaller than the content would paint the lists over the options.
        ClientSize = new Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width),
            Math.Max(ClientSize.Height, root.PreferredSize.Height));
        MinimumSize = Size;
        FormLayout.KeepFitted(this, root);

        LoadActions();
        UpdateGroupEnabled();
    }

    /// <summary>
    /// Auto-split only makes sense when each instance closes itself: several Revits told to leave
    /// their files open would bury the user in windows, and the add-in blocks the same combination.
    /// </summary>
    private bool SplitAllowed => _completionCombo.SelectedIndex == 2; // Close Revit when complete

    private void UpdateGroupEnabled() =>
        _splitNote.Enabled = SplitAllowed;   // dims when the disposition rules the split out

    /// <summary>
    /// The action list comes from the brokering Revit's registry, so it is always exactly what this
    /// build of the add-in can run — there is no second list here to fall out of step.
    /// </summary>
    private void LoadActions()
    {
        try
        {
            foreach (string name in _broker.GetActionNames())
                _actionsList.Items.Add(name);

            if (_actionsList.Items.Count == 0)
                Append("No actions returned by the broker — is the hidden Revit still running? "
                     + (_broker.LastError ?? ""));

            if (!string.IsNullOrWhiteSpace(_preselectAction))
            {
                int index = _actionsList.Items.IndexOf(_preselectAction!);
                if (index >= 0) _actionsList.SetItemChecked(index, true);
            }
        }
        catch (Exception ex)
        {
            Append("Could not load the action list: " + ex.Message);
        }
    }

    private void OnRun(object? sender, EventArgs e)
    {
        List<ModelPreset> sets = _setsList.CheckedIndices.Cast<int>().Select(i => _sets[i]).ToList();

        // Checked order is list order, which is what the user sees — so visible order is run order,
        // the property the add-in's v3 RunItems list exists to preserve.
        List<RunItem> runItems = _actionsList.CheckedItems.Cast<string>()
            .Select(entry => _scriptSets.TryGetValue(entry, out ScriptSet? script)
                ? new RunItem(script.Name, RunItemKind.ScriptSet, script.Id)
                : new RunItem(entry, RunItemKind.Action))
            .ToList();

        if (sets.Count == 0 || runItems.Count == 0)
        {
            MessageBox.Show(this, "Pick at least one model set and one action or script set.",
                            "Batch Run", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string destination = _destinationBox.Text.Trim();
        if (destination.Length == 0)
        {
            MessageBox.Show(this, "Choose where the logs should go.",
                            "Batch Run", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SyncMode sync = _syncCombo.SelectedIndex == 1 ? SyncMode.NoSync : SyncMode.Sync;
        BatchCompletion completion = _completionCombo.SelectedIndex switch
        {
            0 => BatchCompletion.CloseDocuments,
            1 => BatchCompletion.LeaveOpen,
            _ => BatchCompletion.CloseRevit
        };
        // Sized from the models this run actually resolves to, in BatchRun.Execute — the count is
        // not known here (folder-backed sets re-scan). 0 means "do not split at all".
        int groupSize = SplitAllowed ? ScheduledJob.GroupSizeAuto : 0;
        string projectLog = _projectLogBox.Text.Trim();

        _runButton.Enabled = false;
        _status.Text = "Running…";
        _output.Clear();

        // Off the UI thread: Execute blocks for the whole run (minutes to hours), and the add-in's
        // habit of doing this work inline is exactly what makes its windows look frozen.
        var worker = new Thread(() =>
        {
            bool ok;
            try
            {
                ok = BatchRun.Execute(_broker, _revitExe, sets, runItems, destination, Append,
                                      sync, completion, groupSize, projectLog);
            }
            catch (Exception ex)
            {
                ControllerLog.Write("batch run FAILED: " + ex);
                Append("ERROR: " + ex.Message);
                ok = false;
            }

            // The window may have been closed mid-run; marshalling onto a dead handle would throw
            // on this background thread and take the launcher down with it.
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    _runButton.Enabled = true;
                    _status.Text = ok ? "Finished." : "Finished with errors — see the log.";
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        })
        { IsBackground = true, Name = "ControllerBatchRun" };

        worker.Start();
    }

    /// <summary>Appends a progress line, marshalling onto the UI thread — the run reports from a
    /// worker thread.</summary>
    private void Append(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            // Closed mid-run: the handle may be gone between the check above and this call.
            try { if (IsHandleCreated) BeginInvoke(() => Append(line)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            return;
        }

        _output.AppendText(line + Environment.NewLine);
    }
}
