using System.Drawing;
using System.Windows.Forms;
using KhBatchRunner.Forma;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// "Edit Model Set" window. Manage saved model sets (top-right list: New / Load /
/// Delete), browse the Forma tree (left) and/or add local .rvt files, and Save the set.
/// Left: lazy-loaded Forma tree (hubs → projects → folders). Right: models in the
/// selected folder (multi-check), the models accumulated for the set, and Save.
/// (Class name kept as FormaBrowserWindow for continuity; the button/title read "Edit Model Set".)
/// </summary>
public sealed class FormaBrowserWindow : Form
{
    private const string Placeholder = "__loading__";

    private readonly DataManagementClient _client;
    private readonly TreeView _tree;
    private readonly ComboBox _setsCombo;
    private readonly ModelCheckGrid _modelsList;
    private readonly ListBox _folderList;
    private readonly ListBox _presetList;
    private readonly TextBox _nameBox;
    private readonly CheckBox _folderSubfoldersCheck;
    private readonly StatusLine _status;

    // The name of the currently-loaded saved set (null for a brand-new set). If Save changes
    // the name, the old file is deleted so it's a rename, not a duplicate — presets are keyed
    // by their file name (see PresetStore).
    private string? _loadedSetName;
    // Stable id of the set currently loaded for editing — preserved across a rename so schedules
    // that reference this set by id keep pointing at it (see ModelPreset.Id / ScheduledJob.ModelSetIds).
    private string? _loadedSetId;

    /// <summary>The Forma folder whose models are currently listed — what "Copy scan details" reads.
    /// Null until a folder is picked in the tree.</summary>
    private FormaEntry? _selectedFolder;

    /// <summary>This Revit's own version number, or empty outside Revit. Empty blocks nothing on
    /// version — see <see cref="FormaModelItem.BlockedReason"/>.</summary>
    private string RunningRevitVersion => HostServices.RunningRevitVersion;

    public FormaBrowserWindow(DataManagementClient client)
    {
        _client = client;

        Text = "Edit Model Set — build & manage saved model sets";
        StartPosition = FormStartPosition.CenterScreen;
        // Scale the whole dialog with Revit's DPI (positions, sizes, fonts together).
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(880, 620);
        MinimumSize = new Size(760, 560);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 380,
            FixedPanel = FixedPanel.None
        };

        // --- Left: navigation tree ---
        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        _tree.BeforeExpand += OnBeforeExpand;
        _tree.AfterSelect += OnAfterSelect;
        split.Panel1.Controls.Add(_tree);

        // --- Right: saved sets + models editor ---
        // A docked, single-column TableLayoutPanel so rows auto-size to their content and
        // never overlap or clip regardless of DPI, font size or window width (the previous
        // absolute + Right-anchor layout could push the buttons past the panel edge).
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(8),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var setsLabel = new Label { Text = "Saved model sets:", AutoSize = true, Margin = new Padding(3, 6, 3, 2) };
        _setsCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        ModelSetAccess.AttachGreying(_setsCombo, i => (i as SetItem)?.Preset,
            // Rebuild, not just repaint: the ORDER depends on the verdict too, and it is fixed at
            // populate time. RefreshSetsCombo keeps the current selection.
            onSnapshotReady: () => RefreshSetsCombo());
        var newSetBtn = new Button { Text = "New", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(56, 0) };
        newSetBtn.Click += (_, _) => NewSet();
        var loadSetBtn = new Button { Text = "Load", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(56, 0) };
        loadSetBtn.Click += (_, _) => LoadSelectedSet();
        var dupSetBtn = new Button { Text = "Duplicate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        dupSetBtn.Click += (_, _) => DuplicateSelectedSet();
        var deleteSetBtn = new Button { Text = "Delete", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(64, 0) };
        deleteSetBtn.Click += (_, _) => DeleteSelectedSet();
        // combo (stretch) + New/Load/Duplicate/Delete, on one auto-height row.
        var setsRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 5, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0)
        };
        setsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        setsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setsRow.Controls.Add(_setsCombo, 0, 0);
        setsRow.Controls.Add(newSetBtn, 1, 0);
        setsRow.Controls.Add(loadSetBtn, 2, 0);
        setsRow.Controls.Add(dupSetBtn, 3, 0);
        setsRow.Controls.Add(deleteSetBtn, 4, 0);

        var modelsLabel = new Label
        {
            Text = "Models in selected Forma folder (.rvt) — check to add:",
            AutoSize = true, Margin = new Padding(3, 8, 3, 2)
        };
        _modelsList = new ModelCheckGrid();   // Shift+click range toggling is built in
        var addButton = new Button { Text = "Add checked  ↓", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(120, 0) };
        addButton.Click += (_, _) => AddChecked();
        var addLocalButton = new Button { Text = "Add local .rvt…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(120, 0) };
        addLocalButton.Click += (_, _) => AddLocalModels();
        // A FOLDER-backed set: add the selected Forma folder(s) as sources that re-scan their models
        // at run time (models added to the folders later are picked up automatically). Multiple
        // folders union into one set; "incl. subfolders" applies to all of them.
        _folderSubfoldersCheck = new CheckBox { Text = "incl. subfolders", AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
        var addFolderButton = new Button { Text = "Add folder  ↓", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(120, 0) };
        addFolderButton.Click += (_, _) => AddFolderSource();
        // Diagnostic, and deliberately in reach: "what does Forma actually report for this folder?"
        // is the question behind every surprise in this list (see CopyScanDetails).
        var scanDetailsButton = new Button
        {
            Text = "Copy scan details", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(130, 0)
        };
        scanDetailsButton.Click += (_, _) => CopyScanDetails();
        var modelsButtons = FormLayout.FlowRow(
            addButton, addLocalButton, addFolderButton, _folderSubfoldersCheck, scanDetailsButton);
        // Five controls on one line are wider than this column, and a FlowRow does not wrap by
        // default — "incl. subfolders" and "Copy scan details" were drawn past its right edge and
        // were simply not there. Fill the width so there is something to wrap against, then wrap.
        modelsButtons.Dock = DockStyle.Fill;
        modelsButtons.WrapContents = true;

        // Folder sources: when non-empty the set is FOLDER-backed (re-scanned at run time); the
        // "Set models" list below then shows the last snapshot as a preview.
        // A NoteLabel, not a plain AutoSize one: this is a sentence, and a plain label loses its
        // right-hand end rather than wrapping when the column is narrower than the text.
        Label folderLabel = FormLayout.NoteLabel(this,
            "Folder sources — re-scanned at run time (optional, unioned):");
        folderLabel.Margin = new Padding(3, 8, 3, 2);
        _folderList = new ListBox
        {
            // 60, not 44: below about three rows a docked list stops being usable, and the rows
            // under it grow with the font and the DPI.
            Dock = DockStyle.Fill, MinimumSize = new Size(0, 60),
            IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended
        };
        var removeFolderButton = new Button { Text = "Remove folder", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(120, 0) };
        removeFolderButton.Click += (_, _) => RemoveSelectedFolders();
        var folderButtons = FormLayout.FlowRow(removeFolderButton);

        var presetLabel = new Label { Text = "Set models:", AutoSize = true, Margin = new Padding(3, 8, 3, 2) };
        _presetList = new ListBox
        {
            Dock = DockStyle.Fill, MinimumSize = new Size(0, 60),
            IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended
        };
        var removeButton = new Button { Text = "Remove selected", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(120, 0) };
        removeButton.Click += (_, _) => RemoveSelected();
        var moveUpButton = new Button { Text = "Move ▲", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(70, 0) };
        moveUpButton.Click += (_, _) => MoveSelected(-1);
        var moveDownButton = new Button { Text = "Move ▼", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(70, 0) };
        moveDownButton.Click += (_, _) => MoveSelected(1);
        var presetButtons = FormLayout.FlowRow(removeButton, moveUpButton, moveDownButton);

        // Set name (the project/log name is chosen at run time — Batch Run / Schedule / a Check —
        // so the set itself no longer carries a project code).
        var nameLabel = new Label { Text = "Set name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        _nameBox = new TextBox { Width = 240, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 3) };
        var nameCodeGrid = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = new Padding(0)
        };
        nameCodeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameCodeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameCodeGrid.Controls.Add(nameLabel, 0, 0);
        nameCodeGrid.Controls.Add(_nameBox, 1, 0);

        var saveButton = new Button { Text = "Save set", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(110, 0) };
        saveButton.Click += (_, _) => SavePreset();
        var closeButton = new Button
        {
            Text = "Close", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(84, 0), DialogResult = DialogResult.OK
        };
        // Save + Close, right-aligned on their row.
        var bottomButtons = FormLayout.FlowRow(saveButton, closeButton);
        bottomButtons.Anchor = AnchorStyles.Right;

        // Assemble rows top-to-bottom; the two lists share the leftover height 50/50.
        FormLayout.AddAutoRow(right,setsLabel);
        FormLayout.AddAutoRow(right,setsRow);
        FormLayout.AddAutoRow(right,modelsLabel);
        FormLayout.AddFillRow(right,_modelsList, 40);
        FormLayout.AddAutoRow(right,modelsButtons);
        FormLayout.AddAutoRow(right,folderLabel);
        FormLayout.AddFillRow(right,_folderList, 20);
        FormLayout.AddAutoRow(right,folderButtons);
        FormLayout.AddAutoRow(right,presetLabel);
        FormLayout.AddFillRow(right,_presetList, 40);
        FormLayout.AddAutoRow(right,presetButtons);
        FormLayout.AddAutoRow(right,nameCodeGrid);
        FormLayout.AddAutoRow(right,bottomButtons);

        split.Panel2.Controls.Add(right);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(split);
        Controls.Add(_status);
        CancelButton = closeButton;

        // Follow Revit's light/dark theme.
        Theme.Apply(this);

        // Fit to content and never below it. This dialog is a SplitContainer rather than a single
        // root table, so it is the RIGHT pane — the stack of three lists and their button rows —
        // that decides the height; the left pane is a tree that is happy at any size. Height only:
        // the width belongs to the splitter, which the user is meant to drag.
        ClientSize = new Size(ClientSize.Width,
            Math.Max(ClientSize.Height, right.PreferredSize.Height + _status.Height));
        MinimumSize = Size;

        RefreshSetsCombo();

        Load += (_, _) =>
        {
            try { split.SplitterDistance = 380; } catch { /* clamped by min sizes */ }
            LoadHubs();
        };
    }

    // ---- saved-set management ------------------------------------------------

    private void RefreshSetsCombo()
    {
        string? keep = (_setsCombo.SelectedItem as SetItem)?.Preset.Name;
        _setsCombo.Items.Clear();
        foreach (ModelPreset p in ModelSetAccess.Ordered(PresetStore.LoadAll()))
            _setsCombo.Items.Add(new SetItem(p));
        if (keep != null)
        {
            for (int i = 0; i < _setsCombo.Items.Count; i++)
                if (((SetItem)_setsCombo.Items[i]).Preset.Name == keep) { _setsCombo.SelectedIndex = i; break; }
        }
    }

    private void NewSet()
    {
        _presetList.Items.Clear();
        _folderList.Items.Clear();
        _folderSubfoldersCheck.Checked = false;
        _nameBox.Text = "";
        _setsCombo.SelectedIndex = -1;
        _loadedSetName = null;
        _loadedSetId = null;
        SetStatus("New set — add Forma and/or local models (or folder sources), name it, then Save set.");
    }

    private void LoadSelectedSet()
    {
        if (_setsCombo.SelectedItem is not SetItem item)
        {
            SetStatus("Pick a saved set in the dropdown first.");
            return;
        }
        _presetList.Items.Clear();
        foreach (CloudModel m in item.Preset.Models)
            _presetList.Items.Add(FormaModelItem.FromCloudModel(m));
        _folderList.Items.Clear();
        foreach (Forma.FolderRef f in item.Preset.AllSourceFolders)
            _folderList.Items.Add(new FolderItem(f));
        _folderSubfoldersCheck.Checked = item.Preset.IncludeSubfolders;
        _nameBox.Text = item.Preset.Name;
        _loadedSetName = item.Preset.Name;
        _loadedSetId = item.Preset.Id;
        SetStatus(item.Preset.IsFolderBacked
            ? $"Loaded FOLDER set '{item.Preset.Name}' ({_folderList.Items.Count} folder source(s)). " +
              "Members re-scan at run time; 'Set models' shows the last snapshot. Save set re-scans and updates it."
            : $"Loaded '{item.Preset.Name}' ({item.Preset.Models.Count} models). Edit and Save set.");
    }

    /// <summary>Clones the selected saved set to a new uniquely-named copy, then loads it for editing.</summary>
    private void DuplicateSelectedSet()
    {
        if (_setsCombo.SelectedItem is not SetItem item)
        {
            SetStatus("Pick a saved set to duplicate.");
            return;
        }
        string newName = UniqueSetName(item.Preset.Name);
        var copy = new ModelPreset
        {
            Name = newName,
            SourceFolders = new List<Forma.FolderRef>(item.Preset.AllSourceFolders),
            IncludeSubfolders = item.Preset.IncludeSubfolders,
            Models = new List<CloudModel>(item.Preset.Models)
        };
        try
        {
            PresetStore.Save(copy);
        }
        catch (Exception ex)
        {
            SetStatus("Could not duplicate set: " + ex.Message.Split('\n')[0]);
            return;
        }
        RefreshSetsCombo();
        for (int i = 0; i < _setsCombo.Items.Count; i++)
            if (((SetItem)_setsCombo.Items[i]).Preset.Name == newName) { _setsCombo.SelectedIndex = i; break; }
        LoadSelectedSet();
        SetStatus($"Duplicated '{item.Preset.Name}' as '{newName}'. Rename and Save set if you like.");
    }

    /// <summary>The shared "&lt;name&gt; (copy)" rule — see <see cref="SetDuplication.UniqueName"/>.</summary>
    private static string UniqueSetName(string baseName) =>
        SetDuplication.UniqueName(baseName, PresetStore.LoadAll().Select(p => p.Name));

    private void DeleteSelectedSet()
    {
        if (_setsCombo.SelectedItem is not SetItem item)
        {
            SetStatus("Pick a saved set to delete.");
            return;
        }
        if (MessageBox.Show(this, $"Delete model set '{item.Preset.Name}'?", "Delete set",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        PresetStore.Delete(item.Preset.Name);
        if (string.Equals(_loadedSetName, item.Preset.Name, StringComparison.OrdinalIgnoreCase))
        {
            _loadedSetName = null;
            _loadedSetId = null;
        }
        RefreshSetsCombo();
        SetStatus($"Deleted '{item.Preset.Name}'.");
    }

    // ---- tree loading --------------------------------------------------------

    private void LoadHubs()
    {
        RunGuarded("Loading hubs…", () =>
        {
            _tree.Nodes.Clear();
            foreach (FormaEntry hub in _client.GetHubs())
                _tree.Nodes.Add(MakeNode(hub));
            SetStatus(_tree.Nodes.Count == 0
                ? "No hubs found. Are you signed in to Forma in Revit?"
                : $"{_tree.Nodes.Count} hub(s).");
        });
    }

    private TreeNode MakeNode(FormaEntry entry)
    {
        var node = new TreeNode(entry.Name) { Tag = entry };
        // A placeholder child makes the [+] expander appear; real children load on expand.
        node.Nodes.Add(new TreeNode(Placeholder));
        return node;
    }

    private void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node is null) return;
        // Only load once: a node still holding the placeholder hasn't been expanded yet.
        if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Text != Placeholder) return;
        if (e.Node.Tag is not FormaEntry entry) return;

        RunGuarded($"Loading {entry.Name}…", () =>
        {
            e.Node.Nodes.Clear();
            List<FormaEntry> children = entry.Kind switch
            {
                FormaKind.Hub => _client.GetProjects(entry),
                FormaKind.Project => _client.GetTopFolders(entry),
                FormaKind.Folder => _client.GetSubFolders(entry),
                _ => new List<FormaEntry>()
            };
            foreach (FormaEntry child in children)
                e.Node.Nodes.Add(MakeNode(child));
            SetStatus($"{entry.Name}: {children.Count} item(s).");
        });
    }

    private void OnAfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node?.Tag is not FormaEntry entry || entry.Kind != FormaKind.Folder) return;

        _selectedFolder = entry;
        RunGuarded($"Loading models in {entry.Name}…", () =>
        {
            _modelsList.SetModels(_client.GetRevitModels(entry), RunningRevitVersion);
            SetStatus($"{entry.Name}: {_client.LastScanNote ?? $"{_modelsList.Count} .rvt model(s)."}");
        });
    }

    /// <summary>
    /// Writes everything Forma reports about the selected folder to a CSV and puts the path on the
    /// clipboard — the "what does the API actually say here?" answer.
    ///
    /// <para>It exists because that question could not be answered without a code change, and
    /// guessing at it produced two shipped defects in one release: <c>revitProjectVersion</c> read
    /// as a string when Forma sends a number (so every Revit year showed as "—"), and copies treated
    /// as originals because <c>extension.type</c> is identical for both. The columns that matter
    /// are Item URN vs Original Item URN, and Extension Data Keys — the latter lists fields nobody
    /// is reading yet.</para>
    /// </summary>
    private void CopyScanDetails()
    {
        if (_selectedFolder is null)
        {
            SetStatus("Select a Forma folder in the tree first.");
            return;
        }

        FormaEntry folder = _selectedFolder;
        RunGuarded($"Reading what Forma reports for {folder.Name}…", () =>
        {
            List<FormaScanDetailRow> rows = _client.GetScanDetails(folder, RunningRevitVersion);
            if (rows.Count == 0)
            {
                SetStatus($"{folder.Name}: Forma returned no items at all.");
                return;
            }

            (IReadOnlyList<string> columns, List<IReadOnlyDictionary<string, string>> mapped) =
                FormaExtractReport.ScanDetails(rows);
            string dest = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "KhBatchRunner");
            string path = Logging.CsvReportWriter.Write(dest, "ScanDetails_" + folder.Name, columns, mapped);

            // Best effort: a failed clipboard write must not lose the file that was just produced.
            try { Clipboard.SetText(path); } catch { /* the path is in the status line either way */ }
            SetStatus($"{rows.Count} item(s) written to {path} (path copied to clipboard).");
        });
    }

    // ---- set building --------------------------------------------------------

    private void AddChecked()
    {
        int added = AddItems(_modelsList.CheckedModels);
        SetStatus(added == 0 ? "Nothing new to add." : $"Added {added} model(s).");
    }

    private void AddLocalModels()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add local Revit models",
            Filter = "Revit models (*.rvt)|*.rvt|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int added = AddItems(dlg.FileNames.Select(FormaModelItem.Local));
        SetStatus(added == 0 ? "Nothing new to add." : $"Added {added} local model(s).");
    }

    /// <summary>Add items to the set, skipping ones already present (by Key).</summary>
    private int AddItems(IEnumerable<FormaModelItem> items)
    {
        int added = 0;
        var existing = _presetList.Items.Cast<FormaModelItem>().Select(i => i.Key).ToHashSet();
        foreach (FormaModelItem item in items)
        {
            if (existing.Add(item.Key))
            {
                _presetList.Items.Add(item);
                added++;
            }
        }
        return added;
    }

    private void RemoveSelected()
    {
        foreach (object sel in _presetList.SelectedItems.Cast<object>().ToList())
            _presetList.Items.Remove(sel);
    }

    /// <summary>Moves the single selected model up (-1) or down (+1) — run order matters.</summary>
    private void MoveSelected(int delta)
    {
        int i = _presetList.SelectedIndex;
        if (i < 0 || _presetList.SelectedItems.Count != 1)
        {
            SetStatus("Select exactly one model in 'Set models' to reorder.");
            return;
        }
        int j = i + delta;
        if (j < 0 || j >= _presetList.Items.Count) return;

        object item = _presetList.Items[i];
        _presetList.Items.RemoveAt(i);
        _presetList.Items.Insert(j, item);
        _presetList.SelectedIndex = j;
    }

    private void SavePreset()
    {
        string name = _nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a set name.", "Save set",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool folderBacked = _folderList.Items.Count > 0;
        if (!folderBacked && _presetList.Items.Count == 0)
        {
            MessageBox.Show(this, "Add at least one model, or a folder source, to the set.", "Save set",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ModelPreset preset;
        if (folderBacked)
        {
            // FOLDER-backed: store the folder identities + a freshly-scanned snapshot (offline
            // fallback / preview). Members are re-scanned and unioned at run time.
            var folders = _folderList.Items.Cast<FolderItem>().Select(i => i.Folder).ToList();
            bool subfolders = _folderSubfoldersCheck.Checked;
            var snapshot = new List<CloudModel>();
            var seen = new HashSet<Guid>();
            RunGuarded($"Scanning {folders.Count} folder source(s) for models…", () =>
            {
                foreach (Forma.FolderRef f in folders)
                    foreach (CloudModel m in Batch.FolderHosts.BuildPreset(_client, f, subfolders).Models)
                        if (m.ModelGuid == Guid.Empty || seen.Add(m.ModelGuid))
                            snapshot.Add(m);
            });
            preset = new ModelPreset
            {
                // Keep the loaded set's id across a rename; a brand-new set gets a fresh one.
                Id = _loadedSetId ?? Guid.NewGuid().ToString("N"),
                Name = name,
                SourceFolders = folders,
                IncludeSubfolders = subfolders,
                Models = snapshot
            };
        }
        else
        {
            preset = new ModelPreset
            {
                Id = _loadedSetId ?? Guid.NewGuid().ToString("N"),
                Name = name,
                Models = _presetList.Items.Cast<FormaModelItem>().Select(i => i.ToCloudModel()).ToList()
            };
        }

        bool renaming = _loadedSetName != null && !string.Equals(_loadedSetName, name, StringComparison.OrdinalIgnoreCase);

        // Renaming onto the name of a different existing set would overwrite it — confirm first.
        if (renaming && PresetStore.LoadAll().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            if (MessageBox.Show(this,
                    $"A set named '{name}' already exists. Overwrite it?", "Rename set",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
        }

        try
        {
            PresetStore.Save(preset);
            // Presets are keyed by file name, so a name change must delete the old file or it
            // lingers as a duplicate — this is what makes "load, change the name, Save" a rename.
            if (renaming) PresetStore.Delete(_loadedSetName!);
            _loadedSetName = name;
            _loadedSetId = preset.Id;

            // Reflect the freshly-scanned snapshot in the preview list after a folder-set save.
            if (folderBacked)
            {
                _presetList.Items.Clear();
                foreach (CloudModel m in preset.Models)
                    _presetList.Items.Add(FormaModelItem.FromCloudModel(m));
            }

            RefreshSetsCombo();
            string verb = renaming ? "Renamed to" : "Saved set";
            string kind = folderBacked
                ? $"folder set ({_folderList.Items.Count} folder source(s), {preset.Models.Count} model(s) now)"
                : $"{preset.Models.Count} model(s)";
            SetStatus($"{verb} '{name}' — {kind} in {PresetStore.PresetsDirectory}.");
            MessageBox.Show(this,
                $"Saved '{name}' — {kind}.\n\n{PresetStore.PresetsDirectory}",
                "Save set", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save set:\n" + ex.Message, "Save set",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Adds the Forma folder selected in the tree as a source of this set (deduped by URN).
    /// Multiple folders union at run time; "incl. subfolders" applies to all of them.</summary>
    private void AddFolderSource()
    {
        if (_tree.SelectedNode?.Tag is not FormaEntry entry || entry.Kind != FormaKind.Folder)
        {
            SetStatus("Select a Forma FOLDER in the tree first, then Add folder.");
            return;
        }
        var folderRef = FolderRef.FromEntry(entry, _tree.SelectedNode.FullPath);
        if (_folderList.Items.Cast<FolderItem>().Any(i =>
                string.Equals(i.Folder.FolderUrn, folderRef.FolderUrn, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"Folder '{folderRef.DisplayPath}' is already a source of this set.");
            return;
        }
        _folderList.Items.Add(new FolderItem(folderRef));
        SetStatus($"Added folder source '{folderRef.DisplayPath}'. Name the set and Save set (it re-scans at run time).");
    }

    private void RemoveSelectedFolders()
    {
        foreach (object sel in _folderList.SelectedItems.Cast<object>().ToList())
            _folderList.Items.Remove(sel);
    }

    // ---- shared plumbing -----------------------------------------------------

    private void RunGuarded(string busy, Action work)
    {
        SetStatus(busy);
        UseWaitCursor = true;
        try
        {
            work();
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message.Split('\n')[0]);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void SetStatus(string text)
    {
        _status.SetMessage(text);
    }

    /// <summary>Combo item that shows a set's name + model count but keeps the object.</summary>
    private sealed class SetItem
    {
        public ModelPreset Preset { get; }
        public SetItem(ModelPreset preset) => Preset = preset;
        // NOT filtered in this window, only labelled: the set editor is where an inaccessible set
        // gets fixed or deleted, so hiding it here would remove the only place it can be dealt with.
        public override string ToString() => ModelSetAccess.Label(Preset, Preset.IsFolderBacked
            ? $"{Preset.Name}  [{Preset.AllSourceFolders.Count} folder(s)]"
            : $"{Preset.Name}  ({Preset.Models.Count} models)");
    }

    /// <summary>List item for a folder source: shows the folder path, keeps the FolderRef.</summary>
    private sealed class FolderItem
    {
        public FolderRef Folder { get; }
        public FolderItem(FolderRef folder) => Folder = folder;
        public override string ToString() => Folder.DisplayPath;
    }
}
