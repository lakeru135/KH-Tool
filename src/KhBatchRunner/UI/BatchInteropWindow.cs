using System.IO;
using System.Windows.Forms;
using KhBatchRunner.Batch;
using KhBatchRunner.Interop;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Modal dialog for the Batch Interop button (a <see cref="RunActionWindow"/> sibling — that
/// window stays generic for the registry Check/Action buttons; this one adds the checkset
/// row a config-carrying action needs). Choices:
///   1. Model set        — which saved set of models to run against.
///   2. Checkset         — a Model Checker .xml (local path or hosted http(s) URL).
///   3. When finished    — leave the opened files open, close them, or close Revit.
///   4. Destination folder + project log name — where logs AND the per-model AIT reports land.
/// No sync choice: Batch Interop is a Check — read-only, nothing is ever written back.
/// Built in code (no designer file).
/// </summary>
public sealed class BatchInteropWindow : Form
{
    private readonly ComboBox _presetCombo;
    private readonly ComboBox _checksetSetCombo;
    private readonly TextBox _checksetBox;
    private readonly CheckBox _examinesLinksCheck;
    private readonly ComboBox _completionCombo;
    private readonly TextBox _folderBox;
    private readonly TextBox _projectLogBox;

    public ModelPreset? SelectedPreset { get; private set; }
    public string? ChecksetPath { get; private set; }

    /// <summary>Whether the chosen checkset asks about the link instances in the host model, which
    /// stops the run leaving proven link-only worksets shut. See
    /// <see cref="Actions.BatchInteropAction.Worksets"/>.</summary>
    public bool ChecksetExaminesLinks { get; private set; }
    public string? DestinationFolder { get; private set; }
    public string? ProjectLogName { get; private set; }
    public BatchCompletion SelectedCompletion { get; private set; } = BatchCompletion.CloseDocuments;

    /// <summary>
    /// Whether the run may split across parallel Revit instances — i.e. the chosen disposition
    /// closes. Same rule and same single source (<see cref="FormLayout.SplitAllowed"/>) as every
    /// other window that can start a split; the SIZE is derived by the caller once the model list
    /// is real. A verified-AIT check applies on top, in
    /// <see cref="Interop.InteropSplitPolicy"/>.
    /// </summary>
    public bool SplitEnabled { get; private set; }

    public BatchInteropWindow(IReadOnlyList<ModelPreset> presets, string revitVersionNumber)
    {
        Text = "Run: Batch Interop";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(560, 320);
        MinimumSize = new System.Drawing.Size(520, 0);

        // --- Model preset ---
        _presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        // Ordered, labelled and dimmed by the signed-in-account check, and rebuilt when that answer
        // lands — the whole treatment, in the one call that carries it (ModelSetAccess.BindSetCombo).
        // This was a bare foreach until 1.5.1.2, which is why a set this account cannot open looked
        // exactly like every other set here while Edit Model Set was flagging it.
        ModelSetAccess.BindSetCombo(_presetCombo, presets,
            p => new PresetItem(p), i => (i as PresetItem)?.Preset);

        // --- Checkset (saved sets + a .xml path or hosted URL) ---
        _checksetSetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        _checksetSetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_checksetSetCombo.SelectedItem is not Interop.ChecksetSet set) return;
            _checksetBox.Text = set.Path;
            // The flag belongs to the checkset, so picking a saved one must bring its answer with
            // it. Leaving the previous checkset's answer behind is how a run ends up skipping
            // worksets for a checkset whose author said not to.
            _examinesLinksCheck.Checked = set.ExaminesLinks;
        };
        var saveChecksetSet = new Button { Text = "Save set", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        saveChecksetSet.Click += (_, _) => SaveChecksetSet();
        var dupChecksetSet = new Button { Text = "Duplicate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        dupChecksetSet.Click += (_, _) => DuplicateChecksetSet();
        var deleteChecksetSet = new Button { Text = "Delete", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(70, 0) };
        deleteChecksetSet.Click += (_, _) => DeleteChecksetSet();

        _checksetBox = new TextBox();
        var checksetBrowse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        checksetBrowse.Click += (_, _) => BrowseForCheckset(revitVersionNumber);

        _examinesLinksCheck = new CheckBox
        {
            Text = "This checkset examines the links themselves",
            AutoSize = true
        };
        Label linksNote = FormLayout.NoteLabel(this,
            "Leave clear and the run skips worksets a previous run proved hold nothing but links — " +
            "the checker never looks inside a linked model, and loading them is most of the open " +
            "time. Tick it if any check asks about the links as elements (pinned, named, on the " +
            "right workset), which needs every workset open.");

        // --- When finished (post-run disposition) ---
        _completionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseDocuments, "Close files"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.LeaveOpen, "Leave files open"));
        _completionCombo.Items.Add(new CompletionItem(BatchCompletion.CloseRevit, "Close Revit when complete"));
        _completionCombo.SelectedIndex = 2; // Close Revit when complete (default) — see BatchRunWindow
        Label serialNote = FormLayout.NoteLabel(this, FormLayout.SplitNote);

        // --- Destination folder (tracking + detail logs AND the InteropReports_<stamp> folder) ---
        _folderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        browse.Click += (_, _) => BrowseForFolder();

        // --- Project log name ---
        _projectLogBox = new TextBox { Text = "Batch_Interop" };

        // --- OK / Cancel ---
        var okButton = new Button { Text = "Run", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        okButton.Click += (_, _) => Commit();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };

        Label note = FormLayout.NoteLabel(this,
            "Read-only — runs the Interoperability Tools Model Checker checkset against each " +
            "model (Forma or local); nothing is synced or saved. Each model's HTML + Excel report " +
            "lands in an InteropReports_<time> folder inside the destination folder.");

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Model preset:", _presetCombo));
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Saved checkset:", _checksetSetCombo, saveChecksetSet, dupChecksetSet, deleteChecksetSet));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Checkset (.xml/URL):", _checksetBox, checksetBrowse));
        FormLayout.AddAutoRow(root, _examinesLinksCheck);
        FormLayout.AddAutoRow(root, linksNote);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("When finished:", _completionCombo));
        FormLayout.AddAutoRow(root, serialNote);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Destination folder:", _folderBox, browse));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Project log name:", _projectLogBox));
        var buttons = FormLayout.FlowRow(okButton, cancel);
        buttons.Anchor = AnchorStyles.Right;
        FormLayout.AddAutoRow(root, buttons);

        Controls.Add(root);
        AcceptButton = okButton;
        CancelButton = cancel;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);
        serialNote.ForeColor = Theme.Dim(serialNote);
        linksNote.ForeColor = Theme.Dim(linksNote);

        ClientSize = new System.Drawing.Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width), root.PreferredSize.Height);
        MinimumSize = Size; // resizable, but never smaller than the fitted content (all controls shown)

        // Populate saved checksets and restore the last-used one.
        ReloadChecksetCombo(selectName: null);
        Interop.ChecksetSet? lastCheckset = Interop.ChecksetSetStore.TryLoadLastUsed();
        if (lastCheckset != null)
        {
            _checksetBox.Text = lastCheckset.Path;
            _examinesLinksCheck.Checked = lastCheckset.ExaminesLinks;
            if (!string.IsNullOrEmpty(lastCheckset.Name)) SelectChecksetByName(lastCheckset.Name);
        }
    }

    // ---- saved checksets -------------------------------------------------------

    private void ReloadChecksetCombo(string? selectName)
    {
        _checksetSetCombo.Items.Clear();
        foreach (Interop.ChecksetSet set in Interop.ChecksetSetStore.LoadAll())
            _checksetSetCombo.Items.Add(set);
        if (selectName != null) SelectChecksetByName(selectName);
    }

    private void SelectChecksetByName(string name)
    {
        for (int i = 0; i < _checksetSetCombo.Items.Count; i++)
            if (((Interop.ChecksetSet)_checksetSetCombo.Items[i]).Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { _checksetSetCombo.SelectedIndex = i; return; }
    }

    private void SaveChecksetSet()
    {
        string name = _checksetSetCombo.Text.Trim();
        string path = _checksetBox.Text.Trim();
        if (name.Length == 0) { Warn("Type a name for the checkset (in the Saved checkset box), then Save set."); return; }
        if (path.Length == 0) { Warn("Choose a checkset (.xml file or hosted URL) before saving the set."); return; }
        if (Interop.ChecksetSetStore.Exists(name) &&
            MessageBox.Show(this, $"A checkset named '{name}' already exists. Overwrite it?", "Batch Interop",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        Interop.ChecksetSetStore.Save(new Interop.ChecksetSet
        {
            Name = name, Path = path, ExaminesLinks = _examinesLinksCheck.Checked
        });
        ReloadChecksetCombo(selectName: name);
    }

    /// <summary>
    /// Forks the selected checkset entry to a new "&lt;name&gt; (copy)" and selects the copy — the
    /// starting point for the same checkset answered differently on
    /// <see cref="Interop.ChecksetSet.ExaminesLinks"/>, or for one hosted URL saved per project.
    /// </summary>
    private void DuplicateChecksetSet()
    {
        if (_checksetSetCombo.SelectedItem is not Interop.ChecksetSet set)
        { Warn("Select a saved checkset to duplicate."); return; }
        Interop.ChecksetSet copy = set.Clone();
        copy.Name = SetDuplication.UniqueName(
            set.Name, Interop.ChecksetSetStore.LoadAll().Select(c => c.Name));
        try
        {
            Interop.ChecksetSetStore.Save(copy);
        }
        catch (Exception ex)
        {
            Warn("Could not duplicate the checkset: " + ex.Message.Split('\n')[0]);
            return;
        }
        ReloadChecksetCombo(selectName: copy.Name);
    }

    private void DeleteChecksetSet()
    {
        if (_checksetSetCombo.SelectedItem is not Interop.ChecksetSet set)
        { Warn("Select a saved checkset to delete."); return; }
        if (MessageBox.Show(this, $"Delete checkset '{set.Name}'?", "Batch Interop",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        Interop.ChecksetSetStore.Delete(set.Name);
        ReloadChecksetCombo(selectName: null);
        _checksetSetCombo.Text = "";
    }

    private void BrowseForCheckset(string revitVersionNumber)
    {
        // AIT ships its stock templates in the per-year Resources folder — a convenient start.
        string aitResources = Path.Combine(ModelCheckerInvoker.AitDirectory(revitVersionNumber), "Resources");
        using var dlg = new OpenFileDialog
        {
            Filter = "Model Checker checkset (*.xml)|*.xml|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(aitResources)
                ? aitResources
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _checksetBox.Text = dlg.FileName;
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
        if (_presetCombo.SelectedItem is not PresetItem item)
        {
            Warn("Select a model preset.");
            return;
        }
        string checkset = _checksetBox.Text.Trim();
        bool isUrl = checkset.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (checkset.Length == 0 || (!isUrl && !File.Exists(checkset)))
        {
            Warn(checkset.Length == 0
                ? "Choose a checkset (.xml file or hosted URL)."
                : $"Checkset not found:\n{checkset}");
            return;
        }
        if (string.IsNullOrWhiteSpace(_folderBox.Text))
        {
            Warn("Choose a destination folder.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_projectLogBox.Text))
        {
            Warn("Enter a project log name.");
            return;
        }

        SelectedPreset = item.Preset;
        ChecksetPath = checkset;
        ChecksetExaminesLinks = _examinesLinksCheck.Checked;
        Interop.ChecksetSetStore.SaveLastUsed(new Interop.ChecksetSet
        {
            Name = (_checksetSetCombo.SelectedItem as Interop.ChecksetSet)?.Name ?? "",
            Path = checkset,
            ExaminesLinks = ChecksetExaminesLinks
        });
        DestinationFolder = _folderBox.Text.Trim();
        Settings.UserSettings.LastDestinationFolder = DestinationFolder;
        ProjectLogName = _projectLogBox.Text.Trim();
        SelectedCompletion = (_completionCombo.SelectedItem as CompletionItem)?.Completion
                             ?? BatchCompletion.CloseDocuments;
        SplitEnabled = FormLayout.SplitAllowed(SelectedCompletion);
    }

    private void Warn(string text)
    {
        MessageBox.Show(this, text, "Batch Interop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        DialogResult = DialogResult.None;
    }

    /// <summary>Combo item that shows the preset name + model count but keeps the object.</summary>
    private sealed class PresetItem
    {
        public ModelPreset Preset { get; }
        public PresetItem(ModelPreset preset) => Preset = preset;
        public override string ToString() =>
            ModelSetAccess.Label(Preset, $"{Preset.Name}  ({Preset.Models.Count} models)");
    }

    /// <summary>Combo item mapping a friendly label to a <see cref="BatchCompletion"/>.</summary>
    private sealed class CompletionItem
    {
        public BatchCompletion Completion { get; }
        private readonly string _label;
        public CompletionItem(BatchCompletion completion, string label) { Completion = completion; _label = label; }
        public override string ToString() => _label;
    }
}
