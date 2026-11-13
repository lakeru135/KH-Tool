using System.Diagnostics;
using System.IO;
using KhBatchRunner.Interop;
using KhBatchRunner.Reporting;

namespace KhBatchRunner.UI;

/// <summary>
/// "Rebuild Report" (named Rebuild Matrix until 1.5.5): rebuilds a run-end workbook from what a run
/// already left on disk — no Revit model is opened. Two sources:
/// <list type="bullet">
/// <item>a matrix (Phase &amp; Workset / Design Option) from a detail CSV. When the CSV is chosen its
/// header is inspected and the matching type is auto-selected; Rebuild validates that the CSV carries
/// the columns that matrix needs and <b>rejects</b> a mismatch (naming the missing column) rather
/// than writing a wrong matrix;</item>
/// <item>the Batch Interop summary from the per-model report workbooks — the same
/// <see cref="InteropExcelReport.Merge"/> the run end uses, so a rebuilt summary and an automatic
/// one are the same file. For runs made before the summary existed, or a hand-picked set to send.</item>
/// </list>
/// Built in code (no designer file), resizable, themed to Revit like the other dialogs.
/// </summary>
public sealed class RebuildMatrixWindow : Form
{
    /// <summary>The combo entry for the Interop summary. A UI-only item rather than a
    /// <see cref="MatrixKind"/>: it is not a matrix and has no CSV, so CSV detection must never
    /// pick it and the run-end matrix code must never see it.</summary>
    private sealed class InteropSummaryItem
    {
        public override string ToString() => "Batch Interop summary";
    }

    private readonly ComboBox _kindCombo;
    private readonly Label _sourceLabel;
    private readonly TextBox _csvBox;
    private readonly TextBox _outFolderBox;
    private readonly TextBox _outNameBox;
    private readonly Button _rebuildButton;
    private readonly StatusLine _status;
    // The picked report workbooks — the source box can only show a count and a folder.
    private readonly List<string> _reportFiles = new();

    private bool InteropSelected => _kindCombo.SelectedItem is InteropSummaryItem;

    /// <param name="preselect">Which matrix the ribbon entry asked for, or null for the first.
    /// A STARTING POINT only: choosing a CSV re-detects the type from its header and overrides
    /// this, so picking the wrong dropdown entry cannot produce the wrong matrix.</param>
    /// <param name="interopSummary">Open on the Batch Interop summary instead (the ribbon's
    /// "Batch Interop Summary" entry); <paramref name="preselect"/> is then ignored.</param>
    public RebuildMatrixWindow(MatrixKind? preselect = null, bool interopSummary = false)
    {
        Text = "Rebuild Report";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(600, 300);
        MinimumSize = new System.Drawing.Size(520, 0);

        Label note = FormLayout.NoteLabel(this,
            "Rebuild a report workbook from files a run already wrote — no model is opened. A matrix " +
            "comes from a detail CSV (the per-item log a Get Phase & Workset or Get Design Option run " +
            "writes): choosing it auto-selects the matching matrix, and a CSV missing the required " +
            "columns is rejected." +
            " The Batch Interop summary merges the per-model report workbooks (<model>.xlsx) into " +
            "one file — select every .xlsx in a run's InteropReports folder." +
            "");

        _kindCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (MatrixCsvImport.Descriptor d in MatrixCsvImport.Kinds)
            _kindCombo.Items.Add(d);
        _kindCombo.Items.Add(new InteropSummaryItem());
        _kindCombo.SelectedIndex = 0;
        if (interopSummary)
        {
            for (int i = 0; i < _kindCombo.Items.Count; i++)
                if (_kindCombo.Items[i] is InteropSummaryItem) { _kindCombo.SelectedIndex = i; break; }
        }
        else if (preselect is { } kind)
            for (int i = 0; i < _kindCombo.Items.Count; i++)
                if (_kindCombo.Items[i] is MatrixCsvImport.Descriptor d && d.Kind == kind)
                { _kindCombo.SelectedIndex = i; break; }

        _csvBox = new TextBox();
        var csvBrowse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        csvBrowse.Click += (_, _) => { if (InteropSelected) BrowseForReports(); else BrowseForCsv(); };

        _outFolderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        var outBrowse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        outBrowse.Click += (_, _) => BrowseForFolder();

        _outNameBox = new TextBox { Text = "Matrix" };

        _rebuildButton = new Button { Text = "Rebuild", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(90, 0) };
        _rebuildButton.Click += (_, _) => Rebuild();
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        var buttons = FormLayout.FlowRow(_rebuildButton, close);
        buttons.Anchor = System.Windows.Forms.AnchorStyles.Right;

        TableLayoutPanel sourceRow = FormLayout.LabeledRow("Source CSV:", _csvBox, csvBrowse);
        _sourceLabel = (Label)sourceRow.Tag;

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Report type:", _kindCombo));
        FormLayout.AddAutoRow(root, sourceRow);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Output folder:", _outFolderBox, outBrowse));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Output name:", _outNameBox));
        FormLayout.AddAutoRow(root, buttons);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(root);
        Controls.Add(_status);
        AcceptButton = _rebuildButton;
        CancelButton = close;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);

        // Size the window to its content (plus the bottom status bar) so every field and button
        // is visible at the default size; it can still be grown, not shrunk below this.
        ClientSize = new System.Drawing.Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width),
            root.PreferredSize.Height + _status.Height);
        MinimumSize = Size;

        ShowSourceKind();
        _kindCombo.SelectedIndexChanged += (_, _) => ShowSourceKind();
    }

    /// <summary>Relabels the source row for the selected type and clears a source of the other
    /// kind — a CSV path is not a report list, and the reverse.</summary>
    private void ShowSourceKind()
    {
        bool interop = InteropSelected;
        string wanted = interop ? "Report files:" : "Source CSV:";
        if (_sourceLabel.Text == wanted) return;
        _sourceLabel.Text = wanted;
        _csvBox.Text = "";
        _csvBox.ReadOnly = interop;   // the list lives in _reportFiles; the box only describes it
        _reportFiles.Clear();
        if (_outNameBox.Text is "Matrix" or "Batch Interop Summary")
            _outNameBox.Text = interop ? "Batch Interop Summary" : "Matrix";
    }

    private void BrowseForReports()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select the Batch Interop report workbooks (Ctrl+A selects a whole run)",
            Filter = "Excel workbooks (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.FileNames.Length == 0) return;

        _reportFiles.Clear();
        _reportFiles.AddRange(dlg.FileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        string folder = Path.GetDirectoryName(_reportFiles[0]) ?? "";
        _csvBox.Text = $"{_reportFiles.Count} file(s) — {folder}";

        // Default the output beside the task's CSV — the folder above InteropReports_<stamp> —
        // named for the run folder, so a rebuilt summary sits where an automatic one would.
        try
        {
            _outFolderBox.Text = Path.GetDirectoryName(folder) ?? folder;
            _outNameBox.Text = Path.GetFileName(folder) + " Summary";
        }
        catch { /* keep the existing defaults */ }
        SetStatus($"{_reportFiles.Count} report workbook(s) selected.");
    }

    private void BrowseForCsv()
    {
        using var dlg = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _csvBox.Text = dlg.FileName;

        // Default the output beside the CSV, named "<csv> Matrix".
        try
        {
            _outFolderBox.Text = Path.GetDirectoryName(dlg.FileName) ?? _outFolderBox.Text;
            _outNameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName) + " Matrix";
        }
        catch { /* keep the existing defaults */ }

        // Peek the header and auto-select the matching matrix (or report a mismatch).
        try
        {
            (List<string> header, _) = MatrixCsvImport.ReadCsv(dlg.FileName);
            MatrixKind? detected = MatrixCsvImport.Detect(header);
            if (detected is MatrixKind kind)
            {
                SelectKind(kind);
                SetStatus($"Detected {MatrixCsvImport.DescriptorFor(kind).DisplayName} columns.");
            }
            else
            {
                SetStatus("These columns don't match any known matrix — pick the type and Rebuild to see what's missing.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Could not read the CSV header: " + ex.Message.Split('\n')[0]);
        }
    }

    private void SelectKind(MatrixKind kind)
    {
        for (int i = 0; i < _kindCombo.Items.Count; i++)
            if (_kindCombo.Items[i] is MatrixCsvImport.Descriptor d && d.Kind == kind)
            {
                _kindCombo.SelectedIndex = i;
                return;
            }
    }

    private void BrowseForFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(_outFolderBox.Text)
                ? _outFolderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _outFolderBox.Text = dlg.SelectedPath;
    }

    private void Rebuild()
    {
        bool interop = InteropSelected;
        MatrixCsvImport.Descriptor? desc = _kindCombo.SelectedItem as MatrixCsvImport.Descriptor;
        if (!interop && desc == null)
        { Warn("Pick a report type."); return; }
        if (interop && _reportFiles.Count == 0)
        { Warn("Choose the report workbooks to merge (Browse…, then Ctrl+A in the run's InteropReports folder)."); return; }
        if (!interop && (string.IsNullOrWhiteSpace(_csvBox.Text) || !File.Exists(_csvBox.Text)))
        { Warn("Choose an existing source CSV file."); return; }
        if (string.IsNullOrWhiteSpace(_outFolderBox.Text))
        { Warn("Choose an output folder."); return; }
        if (string.IsNullOrWhiteSpace(_outNameBox.Text))
        { Warn("Enter an output file name."); return; }

        string name = _outNameBox.Text.Trim();
        if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) name += ".xlsx";
        name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        string outPath = Path.Combine(_outFolderBox.Text.Trim(), name);

        UseWaitCursor = true;
        _rebuildButton.Enabled = false;
        try
        {
            Directory.CreateDirectory(_outFolderBox.Text.Trim());
            if (interop)
            {
                RebuildInteropSummary(outPath);
                return;
            }
            int rows = MatrixCsvImport.Build(desc!.Kind, _csvBox.Text, outPath);
            SetStatus($"Wrote {desc.DisplayName} matrix from {rows} row(s) → {outPath}");
            if (MessageBox.Show(this,
                    $"Rebuilt the {desc.DisplayName} matrix from {rows} row(s).\n\n{outPath}\n\nOpen it now?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                TryOpen(outPath);
        }
        catch (InvalidDataException ex) // the reject path: columns don't match / no data
        {
            SetStatus("Rejected: " + ex.Message.Split('\n')[0]);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message.Split('\n')[0]);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _rebuildButton.Enabled = true;
        }
    }

    /// <summary>The Interop branch of <see cref="Rebuild"/>; its caller owns the cursor, the button
    /// and the catch-all.</summary>
    private void RebuildInteropSummary(string outPath)
    {
        // The output itself, if it was selected among the inputs, is read before it is replaced —
        // which is what topping up an existing summary with a few more reports means.
        (int models, List<string> unreadable) = InteropExcelReport.Merge(_reportFiles.ToList(), outPath);
        string skipped = unreadable.Count == 0 ? "" :
            $"\n\n{unreadable.Count} file(s) are not Batch Interop reports and were left out:\n" +
            string.Join("\n", unreadable.Take(10)) + (unreadable.Count > 10 ? "\n…" : "");

        if (models == 0)
        {
            SetStatus("Rejected: none of the selected files is a Batch Interop report.");
            MessageBox.Show(this, "None of the selected files is a Batch Interop report workbook, so " +
                "nothing was written." + skipped, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetStatus($"Wrote Batch Interop summary of {models} model(s) → {outPath}" +
                  (unreadable.Count > 0 ? $" ({unreadable.Count} file(s) left out)" : ""));
        if (MessageBox.Show(this,
                $"Merged {models} model(s) into one Batch Interop summary.\n\n{outPath}{skipped}\n\nOpen it now?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            TryOpen(outPath);
    }

    private static void TryOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* no default handler for .xlsx — leave the path in the status line */ }
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void SetStatus(string text) { _status.SetMessage(text); }
}
