using System.IO;
using KhBatchRunner.Forma;
using KhBatchRunner.Logging;

namespace KhBatchRunner.UI;

/// <summary>
/// Check Sheet Publish in Forma: build (and optionally save/load) a set of Forma folders, provide a
/// local CSV of expected sheet numbers, and report for each model whether each sheet is present
/// among its published (translated) 2D sheet views (Model Derivative). Pure REST — opens no Revit
/// document. Browse the tree on the left; manage the folder set + inputs on the right; Run writes
/// a timestamped CSV.
/// </summary>
public sealed class CheckSheetPublishWindow : Form
{
    private readonly DataManagementClient _dm;
    private readonly ModelDerivativeClient _md;

    private readonly ListBox _folderList;
    private readonly ComboBox _setCombo;
    private readonly InputSetActions<FolderSet> _sets;
    /// <summary>Name of the folder set currently loaded, so Save can RENAME rather than leave a twin.</summary>
    private string? _loadedSetName;
    private readonly TextBox _csvBox;
    private readonly CheckBox _recurse;
    private readonly TextBox _folderBox;
    private readonly TextBox _reportNameBox;
    private readonly Button _addButton;
    private readonly Button _runButton;
    private readonly StatusLine _status;

    private FormaEntry? _selectedFolder;
    private string _selectedPath = "";

    public CheckSheetPublishWindow(DataManagementClient dm, ModelDerivativeClient md)
    {
        _dm = dm;
        _md = md;

        Text = "Check Sheet Publish in Forma";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(920, 620);
        // 520 high was never enough for this column and nothing had ever checked: the row of
        // buttons under the folder list sat ON TOP of it. 620 is the measured floor at which the
        // layout gate reports nothing; 640 leaves a little for a larger system font.
        MinimumSize = new Size(760, 640);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 380, FixedPanel = FixedPanel.None };
        var tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        split.Panel1.Controls.Add(tree);

        // --- right: folder set + inputs ---
        Label note = FormLayout.NoteLabel(this,
            "Select a Forma folder on the left and add it to the set (save the set to reuse it). " +
            "Provide a CSV of sheet numbers (first column). A sheet counts as published when it " +
            "appears among a model's translated 2D sheet views in Forma.");

        // MultiExtended so Shift/Ctrl range-select works and Remove can drop several folders at once.
        // MinimumSize, because this is the only percent-sized row in a column of AutoSize ones: at
        // the form's minimum height the auto rows take everything and the list collapses to 1px,
        // overlapping the buttons under it. Found by the layout gate the first time this window was
        // put in front of it (1.4.7) — it had never been in that list. Same fix as the other
        // fill-row lists (FormaExtractWindow, ExportNwcWindow).
        _folderList = new ListBox
        {
            IntegralHeight = false,
            SelectionMode = SelectionMode.MultiExtended,
            MinimumSize = new Size(0, 90),
        };
        _addButton = new Button { Text = "Add selected folder", Enabled = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(140, 0) };
        _addButton.Click += (_, _) => AddSelectedFolder();
        var removeButton = new Button { Text = "Remove", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        removeButton.Click += (_, _) =>
        {
            foreach (object item in _folderList.SelectedItems.Cast<object>().ToList())
                _folderList.Items.Remove(item);
        };

        // One editable combo + Save / Duplicate / Delete — the shared InputSetActions pattern (as
        // CutModelWindow). Picking a set in the combo loads it (this window's old Load button).
        _setCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _setCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_setCombo.SelectedItem is FolderSet set) ApplySet(set.Clone());
        };
        _sets = new InputSetActions<FolderSet>(this, _setCombo,
            FolderSetStore.LoadAll,
            n => FolderSetStore.LoadAll().Any(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)),
            FolderSetStore.Save, FolderSetStore.Delete,
            f => f.Name, f => f.Clone(), (f, name) => f.Name = name,
            f => f, o => o as FolderSet, Warn, noun: "folder set", boxLabel: "Saved set");
        var saveSet = new Button { Text = "Save set", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        saveSet.Click += (_, _) => SaveSet();
        var dupSet = new Button { Text = "Duplicate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        dupSet.Click += (_, _) => DuplicateSet();
        var deleteSet = new Button { Text = "Delete", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(70, 0) };
        deleteSet.Click += (_, _) => DeleteSet();

        _csvBox = new TextBox();
        var csvBrowse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        csvBrowse.Click += (_, _) => BrowseForCsv();

        _recurse = new CheckBox { Text = "Include subfolders", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };

        _folderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        var destBrowse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        destBrowse.Click += (_, _) => BrowseForFolder();
        _reportNameBox = new TextBox { Text = "SheetPublish" };

        _runButton = new Button { Text = "Run check", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 0) };
        _runButton.Click += (_, _) => RunCheck();
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        var runRow = FormLayout.FlowRow(_runButton, close);
        runRow.Anchor = AnchorStyles.Right;

        var right = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(right, note);
        FormLayout.AddAutoRow(right, new Label { Text = "Folders in set:", AutoSize = true, Margin = new Padding(3, 4, 3, 2) });
        FormLayout.AddFillRow(right, _folderList, 100);
        FormLayout.AddAutoRow(right, FormLayout.FlowRow(_addButton, removeButton));
        FormLayout.AddAutoRow(right, FormLayout.LabeledRow("Saved set:", _setCombo, saveSet, dupSet, deleteSet));
        FormLayout.AddAutoRow(right, FormLayout.LabeledRow("Sheet numbers CSV:", _csvBox, csvBrowse));
        FormLayout.AddAutoRow(right, _recurse);
        FormLayout.AddAutoRow(right, FormLayout.LabeledRow("Destination folder:", _folderBox, destBrowse));
        FormLayout.AddAutoRow(right, FormLayout.LabeledRow("Report name:", _reportNameBox));
        FormLayout.AddAutoRow(right, runRow);
        split.Panel2.Controls.Add(right);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(split);
        Controls.Add(_status);
        CancelButton = close;

        var controller = new FormaTreeController(tree, _dm, this, SetStatus);
        controller.FolderSelected += folder =>
        {
            _selectedFolder = folder;
            _selectedPath = folder != null ? tree.SelectedNode?.FullPath ?? "" : "";
            _addButton.Enabled = folder != null;
        };

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);
        Load += (_, _) =>
        {
            FormLayout.FitSplit(this, split, right, _status);
            _sets.Reload(selectName: null);
            controller.LoadHubs();
        };
    }

    private void AddSelectedFolder()
    {
        if (_selectedFolder is null) return;
        var fr = FolderRef.FromEntry(_selectedFolder, _selectedPath);
        // De-dupe by URN so the same folder isn't added twice.
        foreach (FolderRef existing in _folderList.Items.Cast<FolderRef>())
            if (string.Equals(existing.FolderUrn, fr.FolderUrn, StringComparison.OrdinalIgnoreCase))
                return;
        _folderList.Items.Add(fr);
    }

    private List<FolderRef> CurrentFolders() => _folderList.Items.Cast<FolderRef>().ToList();

    /// <summary>A folder set back into the list; marks it as the currently-loaded NAMED set.</summary>
    private void ApplySet(FolderSet set)
    {
        _folderList.Items.Clear();
        foreach (FolderRef fr in set.Folders) _folderList.Items.Add(fr);
        _loadedSetName = set.Name;
        SetStatus($"Loaded folder set '{set.Name}' ({set.Folders.Count} folder(s)).");
    }

    private void SaveSet()
    {
        try
        {
            _sets.Save(ref _loadedSetName,
                name => new FolderSet { Name = name, Folders = CurrentFolders() },
                () => _folderList.Items.Count == 0 ? "Add at least one folder before saving." : null);
        }
        catch (Exception ex) { Warn("Could not save set: " + ex.Message); return; }
        // Only claim a save when one happened (a declined overwrite leaves the typed name unsaved).
        if (_loadedSetName != null && _setCombo.Text.Trim().Equals(_loadedSetName, StringComparison.OrdinalIgnoreCase))
            SetStatus($"Saved folder set '{_loadedSetName}'.");
    }

    /// <summary>Forks the selected folder set to "&lt;name&gt; (copy)" and loads the copy — see
    /// <see cref="InputSetActions{T}.Duplicate"/>.</summary>
    private void DuplicateSet() => _sets.Duplicate();

    private void DeleteSet()
    {
        try { _sets.Delete(ref _loadedSetName); }
        catch (Exception ex) { Warn("Could not delete set: " + ex.Message); }
    }

    private void BrowseForCsv()
    {
        using var dlg = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _csvBox.Text = dlg.FileName;
    }

    private void BrowseForFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(_folderBox.Text)
                ? _folderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dlg.SelectedPath;
    }

    private void RunCheck()
    {
        List<FolderRef> folders = CurrentFolders();
        if (folders.Count == 0) { Warn("Add at least one Forma folder to check."); return; }
        if (string.IsNullOrWhiteSpace(_csvBox.Text) || !File.Exists(_csvBox.Text))
        { Warn("Choose an existing sheet-numbers CSV file."); return; }
        if (string.IsNullOrWhiteSpace(_folderBox.Text)) { Warn("Choose a destination folder."); return; }
        Settings.UserSettings.LastDestinationFolder = _folderBox.Text.Trim();

        List<string> sheets;
        try { sheets = SheetNumberCsv.Read(_csvBox.Text); }
        catch (Exception ex) { Warn("Could not read the CSV: " + ex.Message); return; }
        if (sheets.Count == 0) { Warn("The CSV has no sheet numbers in its first column."); return; }

        string reportName = string.IsNullOrWhiteSpace(_reportNameBox.Text) ? "SheetPublish" : _reportNameBox.Text.Trim();

        SetStatus($"Checking {sheets.Count} sheet number(s) across {folders.Count} folder(s)…");
        UseWaitCursor = true;
        _runButton.Enabled = false;
        try
        {
            List<SheetPublishRow> rows = SheetPublishChecker.Run(_dm, _md, folders, sheets, _recurse.Checked);
            if (rows.Count == 0)
            {
                SetStatus("No .rvt models found in the selected folder(s).");
                MessageBox.Show(this, "No .rvt models were found in the selected folder(s).", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            (IReadOnlyList<string> columns, List<IReadOnlyDictionary<string, string>> mapped) =
                FormaExtractReport.SheetPublish(rows);
            string path = CsvReportWriter.Write(_folderBox.Text.Trim(), reportName, columns, mapped);

            int matched = rows.Count(r => r.IsMatch == "Yes");
            int notFound = rows.Count(r => r.ModelName == SheetPublishRow.NotFoundModel);
            int notProcessed = rows.Count(r => r.IsMatch == SheetPublishRow.NotProcessed);
            string summary = $"{matched} matched, {notFound} sheet(s) not found in any model" +
                             (notProcessed > 0 ? $", {notProcessed} model(s) not processed" : "");
            SetStatus($"Wrote {rows.Count} row(s): {summary} → {path}");
            MessageBox.Show(this, $"Checked {sheets.Count} sheet number(s): {summary}.\n\n{path}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message.Split('\n')[0]);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _runButton.Enabled = true;
        }
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void SetStatus(string text) { _status.SetMessage(text); }
}
