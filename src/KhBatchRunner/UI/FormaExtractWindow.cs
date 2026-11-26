using System.IO;
using KhBatchRunner.Forma;
using KhBatchRunner.Batch;
using KhBatchRunner.Logging;
using KhBatchRunner.Reporting;
using KhBatchRunner.Schedule;

namespace KhBatchRunner.UI;

/// <summary>
/// The Forma <b>Extract</b> dialog (Get File Log): build a set of Forma folders, choose what to report
/// on, and Run — a pure Data Management REST crawl that opens no Revit document and writes
/// <b>one CSV per folder</b>.
///
/// <para>It is also the EDITOR for a saved <see cref="FileLogInputSet"/>. The same named set is what
/// Schedule Run points a job at, so the run you prove here by hand is the run that happens at 00:01
/// — the alternative, a schedule that re-derives its folders from somewhere else, is how the step
/// used to pull nothing at all without saying so.</para>
///
/// <para>Runs in TWO hosts — the Revit command and the standalone controller — so nothing here may
/// touch the Revit API.</para>
/// </summary>
public sealed class FormaExtractWindow : Form
{
    private readonly DataManagementClient _client;

    private readonly ListBox _folderList;
    private readonly Button _addButton;
    private readonly ComboBox _setCombo;
    private readonly InputSetActions<FileLogInputSet> _sets;
    /// <summary>True while <see cref="ApplySet"/> writes the set's name into the combo, so that does not re-load the set.</summary>
    private bool _syncingCombo;
    private readonly CheckBox _recurse;
    private readonly RadioButton _allFiles;
    private readonly RadioButton _revitOnly;
    private readonly CheckBox _latestOnly;
    private readonly TextBox _folderBox;
    private readonly Button _runButton;
    private readonly StatusLine _status;

    private FormaEntry? _selectedFolder;
    private string _selectedPath = "";
    /// <summary>Name of the set currently loaded, so Save can RENAME rather than leave a duplicate.</summary>
    private string? _loadedSetName;

    public FormaExtractWindow(DataManagementClient client)
    {
        _client = client;

        Text = "Forma Extract — Get File Log";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(940, 720);
        // Taller than the sibling extract dialogs on purpose: this one carries four more fixed rows
        // (the scope radios, two checkboxes) above the same folder list, and the list is the only
        // row that absorbs a shortfall. At 760x520 the layout gate measured it starved to ONE pixel
        // and overlapping the buttons beneath it — a dialog you could drag into uselessness.
        MinimumSize = new Size(780, 660);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 380, FixedPanel = FixedPanel.None };
        var tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        split.Panel1.Controls.Add(tree);

        Label note = FormLayout.NoteLabel(this,
            "Select a Forma folder on the left and add it to the set — one CSV is written per folder. " +
            "Save the set to run it again, or to point a schedule at it (Schedule Run → Get File Log set).");

        // MultiExtended so Shift/Ctrl range-select works and Remove can drop several at once.
        // A floor as well as a percentage row: percent alone gives the list whatever is left over,
        // which at the dialog's minimum size is nothing.
        _folderList = new ListBox
        {
            IntegralHeight = false,
            SelectionMode = SelectionMode.MultiExtended,
            MinimumSize = new Size(0, 90),
        };
        _addButton = new Button
        {
            Text = "Add selected folder", Enabled = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(140, 0)
        };
        _addButton.Click += (_, _) => AddSelectedFolder();
        var addManyButton = new Button
        {
            Text = "Add several…",
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 0)
        };
        addManyButton.Click += (_, _) => AddSeveralFolders();
        var removeButton = new Button
        {
            Text = "Remove",
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0)
        };
        removeButton.Click += (_, _) =>
        {
            foreach (object item in _folderList.SelectedItems.Cast<object>().ToList())
                _folderList.Items.Remove(item);
            UpdateRunEnabled();
        };

        // One editable combo + Save / Duplicate / Delete — the shared InputSetActions pattern (as
        // CutModelWindow). Picking a set in the combo loads it (this window's old Load button).
        _setCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _setCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingCombo || _setCombo.SelectedItem is not FileLogInputSet set) return;
            ApplySet(set.Clone());
            SetStatus($"Loaded set '{set.Name}' — {set.FoldersLabel}, {set.ScopeLabel}.");
        };
        _sets = new InputSetActions<FileLogInputSet>(this, _setCombo,
            FileLogSetStore.LoadAll, FileLogSetStore.Exists, FileLogSetStore.Save, FileLogSetStore.Delete,
            x => x.Name, x => x.Clone(), (x, name) => x.Name = name,
            x => x, o => o as FileLogInputSet, Warn, noun: "set", boxLabel: "Saved set");
        var saveSet = new Button { Text = "Save set", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        saveSet.Click += (_, _) => SaveSet();
        var dupSet = new Button { Text = "Duplicate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        dupSet.Click += (_, _) => DuplicateSet();
        var deleteSet = new Button { Text = "Delete", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(70, 0) };
        deleteSet.Click += (_, _) => DeleteSet();

        // Which files. All types is the default — the log's columns are generic version metadata
        // and were only ever restricted to .rvt by accident (see FileLogSpec).
        _allFiles = new RadioButton { Text = "All files", AutoSize = true, Checked = true, Margin = new Padding(16, 2, 3, 0) };
        _revitOnly = new RadioButton { Text = "Revit models only (.rvt)", AutoSize = true, Margin = new Padding(16, 2, 3, 0) };
        _latestOnly = new CheckBox { Text = "Latest version only (much faster)", AutoSize = true, Margin = new Padding(3, 6, 3, 2) };
        _recurse = new CheckBox { Text = "Include subfolders", AutoSize = true, Margin = new Padding(3, 2, 3, 4) };

        _folderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        browse.Click += (_, _) => BrowseForFolder();

        _runButton = new Button
        {
            Text = "Run extract", Enabled = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 0)
        };
        _runButton.Click += (_, _) => RunExtract();
        var close = new Button
        {
            Text = "Close", DialogResult = DialogResult.Cancel,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0)
        };
        var buttons = FormLayout.FlowRow(_runButton, close);
        buttons.Anchor = AnchorStyles.Right;

        var right = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(right, note);
        FormLayout.AddAutoRow(right, new Label { Text = "Folders in set:", AutoSize = true, Margin = new Padding(3, 4, 3, 2) });
        FormLayout.AddFillRow(right, _folderList, 100);
        FormLayout.AddAutoRow(right, FormLayout.FlowRow(_addButton, addManyButton, removeButton));
        FormLayout.AddAutoRow(right, FormLayout.LabeledRow("Saved set:", _setCombo, saveSet, dupSet, deleteSet));
        FormLayout.AddAutoRow(right, new Label { Text = "Include:", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        FormLayout.AddAutoRow(right, _allFiles);
        FormLayout.AddAutoRow(right, _revitOnly);
        FormLayout.AddAutoRow(right, _latestOnly);
        FormLayout.AddAutoRow(right, _recurse);
        FormLayout.AddAutoRow(right, FormLayout.LabeledRow("Destination folder:", _folderBox, browse));
        FormLayout.AddAutoRow(right, buttons);
        split.Panel2.Controls.Add(right);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(split);
        Controls.Add(_status);
        CancelButton = close;

        var controller = new FormaTreeController(tree, client, this, SetStatus);
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
            ApplySet(FileLogSetStore.TryLoadLastUsed(), restored: true);
            controller.LoadHubs();
        };
    }

    // ---- folders ---------------------------------------------------------------

    private void AddSelectedFolder()
    {
        if (_selectedFolder is null) return;
        AddFolder(FolderRef.FromEntry(_selectedFolder, _selectedPath));
    }

    /// <summary>
    /// Adds several folders in one visit to the picker, which keeps the (slow, lazily crawled) Forma
    /// tree loaded between additions instead of rebuilding it per folder.
    /// </summary>
    private void AddSeveralFolders()
    {
        using var picker = new FormaFolderPickerWindow(
            _client, "Get File Log — folders",
            "Select a Forma folder and click \"Add folder\" — repeat for more; \"Done\" when finished:",
            multiPick: true);
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        foreach (FolderRef folder in picker.PickedFolders) AddFolder(folder);
    }

    /// <summary>Appends one folder, de-duped by URN — the same folder twice would write its report
    /// twice, the second overwriting the first.</summary>
    private void AddFolder(FolderRef folder)
    {
        foreach (FolderRef existing in _folderList.Items.Cast<FolderRef>())
            if (string.Equals(existing.FolderUrn, folder.FolderUrn, StringComparison.OrdinalIgnoreCase))
                return;
        _folderList.Items.Add(folder);
        UpdateRunEnabled();
    }

    private List<FolderRef> CurrentFolders() => _folderList.Items.Cast<FolderRef>().ToList();

    private void UpdateRunEnabled() => _runButton.Enabled = _folderList.Items.Count > 0;

    // ---- sets ------------------------------------------------------------------

    /// <summary>The dialog's controls as a set — the one place control state becomes data.</summary>
    private FileLogInputSet Current(string name) => new()
    {
        Name = name,
        Folders = CurrentFolders(),
        IncludeSubfolders = _recurse.Checked,
        Spec = new FileLogSpec
        {
            Scope = _revitOnly.Checked ? FileLogScope.RevitOnly : FileLogScope.AllFiles,
            LatestVersionOnly = _latestOnly.Checked,
        },
        DestinationFolder = _folderBox.Text.Trim(),
        ProjectLogName = FileLogStep.DisplayName,
    };

    /// <summary>The reverse of <see cref="Current"/> — a set back into the controls.</summary>
    /// <summary>The name to treat as "the loaded set" after restoring the LAST-USED snapshot: only when a
    /// saved set of that name exists with identical content. Last-used records whatever name was typed,
    /// loaded or not, so trusting it would let a later Save overwrite an unrelated set without the prompt.</summary>
    private static string? LoadedNameFor(FileLogInputSet snapshot, FileLogInputSet? saved) =>
        saved != null && !string.IsNullOrWhiteSpace(snapshot.Name) &&
        System.Text.Json.JsonSerializer.Serialize(snapshot) == System.Text.Json.JsonSerializer.Serialize(saved)
            ? snapshot.Name : null;

    private void ApplySet(FileLogInputSet? set, bool restored = false)
    {
        if (set is null) return;
        _folderList.Items.Clear();
        foreach (FolderRef f in set.Folders) _folderList.Items.Add(f);
        _recurse.Checked = set.IncludeSubfolders;
        _revitOnly.Checked = set.Spec.Scope == FileLogScope.RevitOnly;
        _allFiles.Checked = !_revitOnly.Checked;
        _latestOnly.Checked = set.Spec.LatestVersionOnly;
        if (!string.IsNullOrWhiteSpace(set.DestinationFolder)) _folderBox.Text = set.DestinationFolder;
        // The combo's own text IS the set name now. Guarded: naming it selects the matching saved
        // set, which must not re-load that set over what was just applied (e.g. a last-used snapshot).
        _syncingCombo = true;
        try { _setCombo.Text = set.Name; }
        finally { _syncingCombo = false; }
        _loadedSetName = restored
            ? LoadedNameFor(set, FileLogSetStore.LoadAll().FirstOrDefault(x =>
                string.Equals(x.Name, set.Name, StringComparison.OrdinalIgnoreCase)))
            : string.IsNullOrWhiteSpace(set.Name) ? null : set.Name;
        UpdateRunEnabled();
    }

    private void SaveSet()
    {
        try
        {
            _sets.Save(ref _loadedSetName, Current,
                () => _folderList.Items.Count == 0 ? "Add at least one Forma folder before saving." : null);
        }
        catch (Exception ex) { Warn("Could not save the set: " + ex.Message.Split('\n')[0]); return; }
        // Only claim a save when one happened (a declined overwrite leaves the typed name unsaved).
        if (_loadedSetName != null && _setCombo.Text.Trim().Equals(_loadedSetName, StringComparison.OrdinalIgnoreCase))
            SetStatus($"Saved set '{_loadedSetName}'.");
    }

    /// <summary>Forks the selected set to a new "&lt;name&gt; (copy)" and loads the copy — see
    /// <see cref="InputSetActions{T}.Duplicate"/>, which leans on the combo's selection change to
    /// re-point <c>_loadedSetName</c> at the COPY (else the next Save would delete the original).</summary>
    private void DuplicateSet() => _sets.Duplicate();

    private void DeleteSet()
    {
        try { _sets.Delete(ref _loadedSetName); }
        catch (Exception ex) { Warn("Could not delete the set: " + ex.Message.Split('\n')[0]); }
    }

    // ---- run -------------------------------------------------------------------

    private void BrowseForFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(_folderBox.Text)
                ? _folderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _folderBox.Text = dlg.SelectedPath;
    }

    private void RunExtract()
    {
        if (_folderList.Items.Count == 0) { Warn("Add at least one Forma folder."); return; }
        if (string.IsNullOrWhiteSpace(_folderBox.Text)) { Warn("Choose a destination folder."); return; }

        FileLogInputSet set = Current(_setCombo.Text.Trim());
        string dest = set.DestinationFolder;
        // Remembered even when the set was never named, so reopening the dialog resumes where it
        // left off — the same courtesy Export NWC and Internal Relink already extend.
        Settings.UserSettings.LastDestinationFolder = _folderBox.Text.Trim();
        FileLogSetStore.SaveLastUsed(set);

        DateTime stamp = DateTime.Now;
        string trackingLog = TrackingCsvLogger.PathFor(
            dest, FileLogStep.DisplayName, FileLogStep.DisplayName, stamp);

        SetStatus("Extracting…");
        UseWaitCursor = true;
        _runButton.Enabled = false;
        try
        {
            // The same body the schedule runs, so an interactive proof is a proof of the scheduled
            // run too — including the tracking log, which this dialog never used to write.
            FileLogResult result = FileLogStep.RunSet(
                set, dest, FileLogStep.DisplayName, stamp, trackingLog, SetStatus);

            string summary =
                $"{result.ReportsWritten} report(s), {result.TotalRows} version(s) from " +
                $"{set.Folders.Count} folder(s)" +
                (result.FailedFolders > 0 ? $"\n{result.FailedFolders} folder(s) FAILED — see the log." : "") +
                (result.SkippedFiles > 0
                    ? $"\n{result.SkippedFiles} file(s) skipped after retries — those rows are " +
                      "missing from the report; the log names them."
                    : "");
            SetStatus(summary.Replace('\n', ' '));
            MessageBox.Show(this,
                summary + "\n\nReports: " + FileLogReportPaths.ReportFolder(dest, FileLogStep.DisplayName) +
                "\nLog: " + trackingLog,
                Text, MessageBoxButtons.OK,
                result.FailedFolders > 0 || result.SkippedFiles > 0
                    ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message.Split('\n')[0]);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            UpdateRunEnabled();
        }
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void SetStatus(string text) { _status.SetMessage(text); }
}
