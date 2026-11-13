using System.IO;
using KhBatchRunner.Forma;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Dialog for Multi Model Action → Batch Link RVT: pick the HOST model set (the models the links are
/// placed into), the SOURCE models to link (Forma cloud or local .rvt), and where each link lands —
/// one shared workset, or one workset per link under the same <c>&lt;keyword&gt;_&lt;name&gt;</c>
/// convention Correct Workset enforces. All inputs save/load as named
/// <see cref="BatchLinkRvtInputSet"/>s, with the last committed inputs auto-restored. A sibling of
/// <see cref="RelinkModelWindow"/>, which does the opposite job on links that already exist.
/// </summary>
public sealed class BatchLinkRvtWindow : Form
{
    private readonly DataManagementClient _client;
    private readonly ComboBox _setCombo;
    private readonly ComboBox _presetCombo;
    private readonly ListBox _sourcesList;
    private readonly RadioButton _sharedRadio;
    private readonly RadioButton _perLinkRadio;
    private readonly TextBox _sharedNameBox;
    private readonly TextBox _keywordBox;
    private readonly ComboBox _placementCombo;
    private readonly CheckBox _attachmentCheck;
    private readonly CheckBox _pinCheck;
    private readonly CheckBox _skipCheck;
    private readonly TextBox _folderBox;
    private readonly TextBox _projectLogBox;
    private readonly ComboBox _completionCombo;
    private readonly Button _okButton;
    private readonly CheckBox? _activeModelCheck;

    // Name of the currently-loaded input set (null for unsaved inputs) — rename-on-save semantics.
    private string? _loadedSetName;

    private readonly InputSetActions<BatchLinkRvtInputSet> _sets;

    /// <summary>The chosen host model set (may itself be a folder-backed set, resolved at run time).</summary>
    public ModelPreset? SelectedPreset { get; private set; }

    /// <summary>Everything the dialog collected, committed.</summary>
    public BatchLinkRvtInputSet? Inputs { get; private set; }

    public BatchCompletion SelectedCompletion { get; private set; } = BatchCompletion.CloseDocuments;

    /// <summary>True when the user chose to link into the model open in Revit rather than a host
    /// model set — nothing is synced, saved or logged. Set only when the window was given an active
    /// model name. Deliberately NOT part of the saved input set: a set saved with it on could not
    /// be run as a batch.</summary>
    public bool RunOnActiveModel { get; private set; }

    /// <param name="activeModelName">Title of a runnable project model open in Revit, or null.</param>
    public BatchLinkRvtWindow(IReadOnlyList<ModelPreset> presets, DataManagementClient client,
        string? activeModelName = null)
    {
        _client = client;

        Text = "Batch Link RVT — Multi Model Action";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(640, 520);
        MinimumSize = new System.Drawing.Size(600, 0);

        // --- Input set (save/load every field below as a named set) ---
        _setCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _setCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_setCombo.SelectedItem is SetItem item) ApplyInputs(item.Set, loaded: true);
        };
        _sets = new InputSetActions<BatchLinkRvtInputSet>(this, _setCombo,
            BatchLinkRvtSetStore.LoadAll, BatchLinkRvtSetStore.Exists, BatchLinkRvtSetStore.Save,
            BatchLinkRvtSetStore.Delete,
            s => s.Name, s => s.Clone(), (s, name) => s.Name = name,
            s => new SetItem(s), o => (o as SetItem)?.Set, Warn);
        var saveSet = Small("Save set", 80);
        saveSet.Click += (_, _) => SaveInputSet();
        var dupSet = Small("Duplicate", 80);
        dupSet.Click += (_, _) => DuplicateInputSet();
        var deleteSet = Small("Delete", 70);
        deleteSet.Click += (_, _) => DeleteInputSet();

        _presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        // Ordered, labelled and dimmed by the signed-in-account check, and rebuilt when that answer
        // lands — the whole treatment, in the one call that carries it (ModelSetAccess.BindSetCombo).
        // This was a bare foreach until 1.5.1.2, which is why a set this account cannot open looked
        // exactly like every other set here while Edit Model Set was flagging it.
        ModelSetAccess.BindSetCombo(_presetCombo, presets,
            p => new PresetItem(p), i => (i as PresetItem)?.Preset);

        // MinimumSize (not just Height) so LabeledRow's Dock.Fill can't collapse it in the auto-size row.
        _sourcesList = new ListBox
        {
            Height = 92, IntegralHeight = false, MinimumSize = new System.Drawing.Size(0, 92),
            SelectionMode = SelectionMode.MultiExtended
        };
        var addForma = Small("Add from Forma…", 120);
        addForma.Click += (_, _) => AddFormaSources();
        var addLocal = Small("Add local…", 100);
        addLocal.Click += (_, _) => AddLocalSources();
        var removeSource = Small("Remove", 80);
        removeSource.Click += (_, _) =>
        {
            foreach (object item in _sourcesList.SelectedItems.Cast<object>().ToList())
                _sourcesList.Items.Remove(item);
        };

        _sharedRadio = new RadioButton
        {
            Text = "All links on ONE workset:", AutoSize = true, Checked = true,
            Margin = new Padding(3, 4, 3, 4)
        };
        _perLinkRadio = new RadioButton
        {
            Text = "One workset per link, named <keyword>_<link name>:", AutoSize = true,
            Margin = new Padding(3, 4, 3, 4)
        };
        _sharedNameBox = new TextBox { Text = "Z-LINK" };
        _keywordBox = new TextBox { Text = "LINK", Enabled = false };
        _sharedRadio.CheckedChanged += (_, _) => SyncWorksetMode();
        _perLinkRadio.CheckedChanged += (_, _) => SyncWorksetMode();

        // Only what ImportPlacement actually has. The Revit UI's "Project Base Point to Project Base
        // Point" has no API member, so it is not offered rather than offered and broken.
        _placementCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _placementCombo.Items.Add(new PlacementItem(LinkPlacement.Shared, "Auto - By Shared Coordinates"));
        _placementCombo.Items.Add(new PlacementItem(LinkPlacement.Origin, "Auto - Origin to Origin"));
        _placementCombo.Items.Add(new PlacementItem(LinkPlacement.Centered, "Auto - Center to Center"));
        _placementCombo.SelectedIndex = 0;

        _attachmentCheck = new CheckBox
        {
            Text = "Attachment (default is Overlay — an attachment is carried into anything that links the host)",
            AutoSize = true, Margin = new Padding(3, 4, 3, 4)
        };
        _pinCheck = new CheckBox
        {
            Text = "Pin each new link instance", AutoSize = true, Checked = true,
            Margin = new Padding(3, 4, 3, 4)
        };
        _skipCheck = new CheckBox
        {
            Text = "Skip a source the host already links (never create a second link to the same model)",
            AutoSize = true, Checked = true, Margin = new Padding(3, 4, 3, 4)
        };

        _folderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        var browse = Small("Browse…", 80);
        browse.Click += (_, _) => BrowseForFolder();

        _projectLogBox = new TextBox { Text = "Batch Link RVT" };

        // Defaults to "Close files", NOT to "Close Revit when complete" like Internal Relink: a host
        // left open keeps the link this run just gave it LOADED, and any later host that is itself
        // that model then opens as a read-only linked document and fails. See the note below.
        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseDocuments, "Close files"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.LeaveOpen, "Leave files open"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseRevit, "Close Revit when complete"));
        _completionCombo.SelectedIndex = 0;

        _okButton = new Button
        {
            Text = "Run", DialogResult = DialogResult.OK, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0)
        };
        _okButton.Click += (_, _) => Commit();
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0)
        };

        // --- Run on active model (links into the open document; no sync, no logs) ---
        if (activeModelName != null)
        {
            var activeCheck = new CheckBox
            {
                Text = $"Run on active model only — “{activeModelName}” (links are added but NOT saved or synced)",
                AutoSize = true,
                Margin = new Padding(3, 2, 3, 2)
            };
            activeCheck.CheckedChanged += (_, _) =>
            {
                bool active = activeCheck.Checked;
                _presetCombo.Enabled = !active;
                _completionCombo.Enabled = !active;
                _folderBox.Enabled = !active;
                browse.Enabled = !active;
                _projectLogBox.Enabled = !active;
                _okButton.Text = active ? "Run on active" : "Run";
            };
            _activeModelCheck = activeCheck;
        }

        Label note = FormLayout.NoteLabel(this,
            "Links every source model into every model of the host set, then syncs each host back to " +
            "its cloud central. \"One workset per link\" uses the same name Correct Workset expects, " +
            "so a Correct Workset run straight afterwards reports the links as already correct. " +
            "A source the host already links is skipped rather than linked twice. Use \"Close files\": " +
            "a host left open keeps its new links loaded, and a later host that is itself one of those " +
            "links can then only be opened read-only, which fails it.");

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Input set:", _setCombo, saveSet, dupSet, deleteSet));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Host model set:", _presetCombo));
        if (_activeModelCheck != null)
            FormLayout.AddAutoRow(root, _activeModelCheck);
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Link these models:", _sourcesList));
        FormLayout.AddAutoRow(root, FlowLeft(addForma, addLocal, removeSource));
        FormLayout.AddAutoRow(root, _sharedRadio);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Workset name:", _sharedNameBox));
        FormLayout.AddAutoRow(root, _perLinkRadio);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Workset keyword:", _keywordBox));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Positioning:", _placementCombo));
        FormLayout.AddAutoRow(root, _attachmentCheck);
        FormLayout.AddAutoRow(root, _pinCheck);
        FormLayout.AddAutoRow(root, _skipCheck);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("When finished:", _completionCombo));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Log destination folder:", _folderBox, browse));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Project log name:", _projectLogBox));
        var buttons = FormLayout.FlowRow(_okButton, cancel);
        buttons.Anchor = AnchorStyles.Right;
        FormLayout.AddAutoRow(root, buttons);

        Controls.Add(root);
        AcceptButton = _okButton;
        CancelButton = cancel;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);
        ClientSize = new System.Drawing.Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width), root.PreferredSize.Height);
        MinimumSize = Size; // resizable, but never smaller than the fitted content (all controls shown)

        ReloadSetCombo(selectName: null);
        BatchLinkRvtInputSet? lastUsed = BatchLinkRvtSetStore.TryLoadLastUsed();
        if (lastUsed != null)
        {
            ApplyInputs(lastUsed, loaded: false);
            if (!string.IsNullOrEmpty(lastUsed.Name))
                SelectSetByName(lastUsed.Name);
        }

        // With no saved model sets, linking into the open model is the only thing left to do.
        if (_activeModelCheck != null && presets.Count == 0)
            _activeModelCheck.Checked = true;
    }

    private static Button Small(string text, int minWidth) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new System.Drawing.Size(minWidth, 0)
    };

    private static FlowLayoutPanel FlowLeft(params Control[] controls)
    {
        FlowLayoutPanel row = FormLayout.FlowRow(controls);
        row.Anchor = AnchorStyles.Left;
        return row;
    }

    /// <summary>Only the chosen mode's box is editable — a greyed box is the cheapest way to say
    /// which of the two names is actually in play.</summary>
    private void SyncWorksetMode()
    {
        _sharedNameBox.Enabled = _sharedRadio.Checked;
        _keywordBox.Enabled = _perLinkRadio.Checked;
    }

    // ---- input sets ------------------------------------------------------------

    private void ReloadSetCombo(string? selectName) => _sets.Reload(selectName);

    private void SelectSetByName(string name) => _sets.SelectByName(name);

    private void ApplyInputs(BatchLinkRvtInputSet s, bool loaded)
    {
        _loadedSetName = loaded && !string.IsNullOrEmpty(s.Name) ? s.Name : null;

        if (!string.IsNullOrEmpty(s.ModelSetName))
        {
            for (int i = 0; i < _presetCombo.Items.Count; i++)
                if (((PresetItem)_presetCombo.Items[i]).Preset.Name.Equals(s.ModelSetName, StringComparison.OrdinalIgnoreCase))
                {
                    _presetCombo.SelectedIndex = i;
                    break;
                }
        }

        _sourcesList.Items.Clear();
        foreach (CloudModel m in s.Sources)
            _sourcesList.Items.Add(new SourceItem(m));

        _sharedRadio.Checked = !s.PerLinkWorkset;
        _perLinkRadio.Checked = s.PerLinkWorkset;
        SyncWorksetMode();
        if (!string.IsNullOrWhiteSpace(s.SharedWorksetName)) _sharedNameBox.Text = s.SharedWorksetName;
        if (!string.IsNullOrWhiteSpace(s.LinkKeyword)) _keywordBox.Text = s.LinkKeyword;

        for (int i = 0; i < _placementCombo.Items.Count; i++)
            if (((PlacementItem)_placementCombo.Items[i]).Placement == s.Placement)
            {
                _placementCombo.SelectedIndex = i;
                break;
            }

        _attachmentCheck.Checked = s.Attachment;
        _pinCheck.Checked = s.PinInstance;
        _skipCheck.Checked = s.SkipIfAlreadyLinked;
        if (!string.IsNullOrWhiteSpace(s.DestinationFolder)) _folderBox.Text = s.DestinationFolder;
        if (!string.IsNullOrWhiteSpace(s.ProjectLogName)) _projectLogBox.Text = s.ProjectLogName;
    }

    private BatchLinkRvtInputSet BuildSnapshot(string name) => new()
    {
        Name = name,
        ModelSetName = (_presetCombo.SelectedItem as PresetItem)?.Preset.Name ?? "",
        Sources = _sourcesList.Items.Cast<SourceItem>().Select(i => i.Model).ToList(),
        PerLinkWorkset = _perLinkRadio.Checked,
        SharedWorksetName = _sharedNameBox.Text.Trim(),
        LinkKeyword = _keywordBox.Text.Trim(),
        Placement = (_placementCombo.SelectedItem as PlacementItem)?.Placement ?? LinkPlacement.Shared,
        Attachment = _attachmentCheck.Checked,
        PinInstance = _pinCheck.Checked,
        SkipIfAlreadyLinked = _skipCheck.Checked,
        DestinationFolder = _folderBox.Text.Trim(),
        ProjectLogName = _projectLogBox.Text.Trim()
    };

    private void SaveInputSet() => _sets.Save(ref _loadedSetName, BuildSnapshot,
        () => _sourcesList.Items.Count == 0 ? "Add at least one source model before saving the set." : null);

    /// <summary>Forks the selected set to a new "&lt;name&gt; (copy)" and loads the copy — see
    /// <see cref="InputSetActions{T}.Duplicate"/>.</summary>
    private void DuplicateInputSet() => _sets.Duplicate();

    private void DeleteInputSet() => _sets.Delete(ref _loadedSetName);

    // ---- sources ---------------------------------------------------------------

    /// <summary>
    /// The Forma picker, unchanged — including its grey-out, which is exactly right for a link source:
    /// a wrong-year model raises a modal upgrade prompt on load, an upload that is not cloud-workshared
    /// has no model GUID to build a cloud path from, and a copy/share carries the SOURCE's model GUID,
    /// so linking it would silently link the original instead.
    /// </summary>
    private void AddFormaSources()
    {
        using var picker = new FormaPickerWindow(_client,
            title: "Batch Link RVT — source models",
            prompt: "Models in selected Forma folder (.rvt) — check the ones to link into every host:",
            okText: "Add checked");
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedModels.Count == 0) return;
        AddSources(picker.SelectedModels);
    }

    private void AddLocalSources()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add local Revit models to link",
            Filter = "Revit models (*.rvt)|*.rvt|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AddSources(dlg.FileNames.Select(p => new CloudModel
        {
            DisplayName = Path.GetFileName(p),
            LocalPath = p
        }).ToList());
    }

    /// <summary>De-duplicated by <see cref="ModelKey"/> — the same identity rule the rest of the
    /// add-in uses, so a model added from Forma and again from a folder counts once.</summary>
    private void AddSources(IReadOnlyList<CloudModel> models)
    {
        var have = new HashSet<string>(
            _sourcesList.Items.Cast<SourceItem>().Select(i => ModelKey.For(i.Model)),
            StringComparer.OrdinalIgnoreCase);
        foreach (CloudModel m in models)
            if (have.Add(ModelKey.For(m)))
                _sourcesList.Items.Add(new SourceItem(m));
    }

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

    private void Commit()
    {
        bool active = _activeModelCheck?.Checked == true;
        var item = _presetCombo.SelectedItem as PresetItem;
        if (!active && item is null)
        {
            Warn("Select a host model set.");
            return;
        }
        if (_sourcesList.Items.Count == 0)
        {
            Warn("Add at least one source model to link.");
            return;
        }
        if (_sharedRadio.Checked && string.IsNullOrWhiteSpace(_sharedNameBox.Text))
        {
            Warn("Enter the name of the workset every link should go on (e.g. Z-LINK).");
            return;
        }
        if (_perLinkRadio.Checked && string.IsNullOrWhiteSpace(_keywordBox.Text))
        {
            Warn("Enter the workset keyword used to name each link's workset (e.g. LINK).");
            return;
        }
        // The log fields are moot on the active model — nothing is written there.
        if (!active && string.IsNullOrWhiteSpace(_folderBox.Text))
        {
            Warn("Choose a log destination folder.");
            return;
        }
        if (!active && string.IsNullOrWhiteSpace(_projectLogBox.Text))
        {
            Warn("Enter a project log name.");
            return;
        }

        BatchLinkRvtInputSet snapshot = BuildSnapshot(_loadedSetName ?? "");

        // A drive-letter source resolves against whatever that letter means on the PC running the
        // batch. Warned about, not blocked — running here, as this user, it is perfectly valid, and
        // the user is the one who knows which case this is.
        List<string> driveLetter = BatchLinkRules.NonUncLocalSources(snapshot.Sources);
        if (driveLetter.Count > 0)
        {
            string names = string.Join(", ", driveLetter.Take(3)) + (driveLetter.Count > 3 ? ", …" : "");
            if (MessageBox.Show(this,
                    $"{driveLetter.Count} local source model(s) are on a drive letter, not a network (UNC) path:\n\n" +
                    names + "\n\n" +
                    "Every host will store that path in its link. On another PC — or under another " +
                    "account — that drive may not exist, and the link will come back unresolved.\n\n" +
                    "Link them anyway?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                DialogResult = DialogResult.None;
                return;
            }
        }

        RunOnActiveModel = active;
        SelectedPreset = active ? null : item!.Preset;
        Inputs = snapshot;
        SelectedCompletion = (_completionCombo.SelectedItem as CompletionItem)?.Completion
                             ?? BatchCompletion.CloseDocuments;
        if (!active) Settings.UserSettings.LastDestinationFolder = _folderBox.Text.Trim();
        BatchLinkRvtSetStore.SaveLastUsed(Inputs);
    }

    private void Warn(string text)
    {
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        DialogResult = DialogResult.None;
    }

    private sealed class PresetItem
    {
        public ModelPreset Preset { get; }
        public PresetItem(ModelPreset preset) => Preset = preset;
        public override string ToString() => ModelSetAccess.Label(Preset, Preset.IsFolderBacked
            ? $"{Preset.Name}  [folder]"
            : $"{Preset.Name}  ({Preset.Models.Count} models)");
    }

    private sealed class SetItem
    {
        public BatchLinkRvtInputSet Set { get; }
        public SetItem(BatchLinkRvtInputSet set) => Set = set;
        public override string ToString() => Set.Name;
    }

    /// <summary>A source in the list, labelled with where it comes from — a local file and a cloud
    /// model behave differently enough (path vs GUID, and the UNC warning) to be worth telling apart
    /// at a glance.</summary>
    private sealed class SourceItem
    {
        public CloudModel Model { get; }
        public SourceItem(CloudModel model) => Model = model;
        public override string ToString() => Model.IsLocal
            ? $"{Model.DisplayName}  [local: {Model.LocalPath}]"
            : $"{Model.DisplayName}  [Forma]";
    }

    private sealed class PlacementItem
    {
        public LinkPlacement Placement { get; }
        private readonly string _label;
        public PlacementItem(LinkPlacement placement, string label) { Placement = placement; _label = label; }
        public override string ToString() => _label;
    }

    private sealed class CompletionItem
    {
        public BatchCompletion Completion { get; }
        private readonly string _label;
        public CompletionItem(BatchCompletion completion, string label) { Completion = completion; _label = label; }
        public override string ToString() => _label;
    }
}
