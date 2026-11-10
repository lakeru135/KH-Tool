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
    /// <summary>Held because the two list columns are re-laid out on resize, and this label is the
    /// right column's heading — it has to travel with it.</summary>
    private readonly Label _actionsLabel;
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

        Controls.Add(new Label { Text = "Model sets", Location = new Point(12, 12), AutoSize = true });
        // IntegralHeight keeps the box a whole number of rows tall. With it off, the last row was
        // sliced in half and read as a rendering fault rather than "scroll for more".
        _setsList = new CheckedListBox
        {
            Location = new Point(12, 32),
            Size = new Size(360, 154),
            CheckOnClick = true,
            IntegralHeight = true
        };
        foreach (ModelPreset s in _sets)
            _setsList.Items.Add(KhBatchRunner.UI.ModelSetAccess.Label(s, $"{s.Name}  ({s.Models.Count})"));
        Controls.Add(_setsList);

        _actionsLabel = new Label { Text = "Actions", Location = new Point(388, 12), AutoSize = true };
        Controls.Add(_actionsLabel);
        _actionsList = new CheckedListBox
        {
            Location = new Point(388, 32),
            Size = new Size(360, 154),
            CheckOnClick = true,
            IntegralHeight = true
        };
        Controls.Add(_actionsList);

        // Script sets sit in the same list as actions, so a run can interleave them the way the
        // add-in's RunItems list does — order in the list is order of execution.
        foreach (ScriptSet script in ScriptSetStore.LoadAll())
        {
            _actionsList.Items.Add(ScriptPrefix + script.Name);
            _scriptSets[ScriptPrefix + script.Name] = script;
        }

        // ---- options, mirroring the add-in's Batch Run dialog --------------------------------
        Controls.Add(new Label { Text = "Model treatment", Location = new Point(12, 192), AutoSize = true });
        _syncCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(12, 212),
            Size = new Size(230, 23)
        };
        _syncCombo.Items.Add("Sync changes to central");
        _syncCombo.Items.Add("Don't sync (dry run)");
        _syncCombo.SelectedIndex = 1; // Dry run (default), matching the add-in's dialogs.
        Controls.Add(_syncCombo);

        Controls.Add(new Label { Text = "When finished", Location = new Point(258, 192), AutoSize = true });
        _completionCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(258, 212),
            Size = new Size(230, 23)
        };
        _completionCombo.Items.Add("Close files");
        _completionCombo.Items.Add("Leave files open");
        _completionCombo.Items.Add("Close Revit when complete");
        _completionCombo.SelectedIndex = 2; // an unattended worker should quit, as in the add-in
        _completionCombo.SelectedIndexChanged += (_, _) => UpdateGroupEnabled();
        Controls.Add(_completionCombo);

        // No split-size box: the shape is derived from the model count and this PC, exactly as in
        // the add-in (InstanceBudget.AutoGroupSize). What is worth saying is that it happens at all,
        // and the one setting that turns it off.
        // Sized to the column, not to the sentence: at MinimumSize the window is 760 wide, so this
        // label has 760 - 504 - 12 = 244px and anything wider is silently cut off at the right edge.
        // The "Leave files open" caveat is carried by DIMMING the note (UpdateGroupEnabled) rather
        // than by more words, which is what makes it fit.
        _splitNote = new Label
        {
            // Two lines, not three: the row below starts at y=246, so a third line is clipped.
            // How many workers this PC allows is reported by BatchRun as "Limit : …" when the run
            // starts, which is where the number is actually useful.
            Text = "Large runs auto-split across\nseveral Revit workers.",
            Location = new Point(504, 192),
            Size = new Size(244, 40),
            AutoSize = false,
            UseMnemonic = false
        };
        Controls.Add(_splitNote);

        Controls.Add(new Label { Text = "Log destination", Location = new Point(12, 246), AutoSize = true });
        _destinationBox = new TextBox
        {
            Location = new Point(12, 266),
            Size = new Size(650, 23),
            Text = ScheduleStore.DefaultDestinationFolder(),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(_destinationBox);

        Controls.Add(new Label { Text = "Project log name", Location = new Point(12, 300), AutoSize = true });
        _projectLogBox = new TextBox
        {
            Location = new Point(12, 320),
            Size = new Size(230, 23)
        };
        Controls.Add(_projectLogBox);
        Controls.Add(new Label
        {
            Text = "(blank = the model set's name)",
            Location = new Point(250, 324),
            AutoSize = true
        });

        var browse = new Button
        {
            Text = "…",
            Location = new Point(668, 265),
            Size = new Size(36, 25),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _destinationBox.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _destinationBox.Text = dialog.SelectedPath;
        };
        Controls.Add(browse);

        _runButton = new Button
        {
            Text = "Run now",
            Location = new Point(12, 354),
            Size = new Size(120, 30)
        };
        _runButton.Click += OnRun;
        Controls.Add(_runButton);

        _status = new Label
        {
            Location = new Point(144, 360),
            Size = new Size(560, 20),
            Text = "Runs in a separate Revit; this window stays usable.",
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(_status);

        _output = new TextBox
        {
            Location = new Point(12, 394),
            Size = new Size(736, 200),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.5F),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };
        Controls.Add(_output);

        Theme.Apply(this);
        _status.ForeColor = Theme.Dim(_status);

        // Anchors cannot express "two equal columns": the right one's LEFT edge has to move, and an
        // anchor only pins edges to the form. So the pair is positioned in code, on every resize.
        Resize += (_, _) => LayoutListColumns();
        LayoutListColumns();

        LoadActions();
        UpdateGroupEnabled();
    }

    /// <summary>Splits the width between the Model sets and Actions lists, keeping the gutter fixed.</summary>
    private void LayoutListColumns()
    {
        const int margin = 12, gutter = 16, top = 32;
        int available = ClientSize.Width - (margin * 2) - gutter;
        if (available < 200) return;              // mid-construction or absurdly small; leave as built

        int column = available / 2;
        int rightX = margin + column + gutter;

        _setsList.Location = new Point(margin, top);
        _setsList.Size = new Size(column, _setsList.Height);

        _actionsLabel.Location = new Point(rightX, 12);
        _actionsList.Location = new Point(rightX, top);
        _actionsList.Size = new Size(column, _actionsList.Height);
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

            BeginInvoke(() =>
            {
                _runButton.Enabled = true;
                _status.Text = ok ? "Finished." : "Finished with errors — see the log.";
            });
        })
        { IsBackground = true, Name = "ControllerBatchRun" };

        worker.Start();
    }

    /// <summary>Appends a progress line, marshalling onto the UI thread — the run reports from a
    /// worker thread.</summary>
    private void Append(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Append(line)); return; }

        _output.AppendText(line + Environment.NewLine);
    }
}
