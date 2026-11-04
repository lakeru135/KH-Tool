using System.Drawing;
using System.Windows.Forms;
using KhBatchRunner.Scripts;

namespace KhBatchRunner.UI;

/// <summary>
/// "Edit DYN Script Set" window. Manage saved script sets (New / Load / Delete) and the
/// ordered list of <c>.dyn</c> paths in a set (Add / Remove / Save). Saved sets execute
/// via Batch Run / Schedule Run (the Dynamo runner).
/// </summary>
public sealed class EditScriptSetWindow : Form
{
    private readonly ComboBox _setsCombo;
    private readonly ListBox _scriptsList;
    private readonly TextBox _nameBox;
    private readonly StatusLine _status;

    // Name of the currently-loaded set (null for a brand-new set). If Save changes the name, the
    // old file is deleted so it's a rename, not a duplicate — sets are keyed by file name.
    private string? _loadedSetName;
    // Stable id of the loaded set — preserved across a rename so schedules that reference this script
    // set by id keep pointing at it (see ScriptSet.Id / RunItem.RefId).
    private string? _loadedSetId;

    public EditScriptSetWindow()
    {
        Text = "Edit DYN Script Set";
        StartPosition = FormStartPosition.CenterScreen;
        // Scale the whole dialog with Revit's DPI (positions, sizes, fonts together).
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(560, 460);
        MinimumSize = new Size(480, 400);

        Button Mk(string text, int minW, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(minW, 0) };
            b.Click += (_, _) => onClick();
            return b;
        }

        _setsCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        var newSetBtn = Mk("New", 56, NewSet);
        var loadSetBtn = Mk("Load", 56, LoadSelectedSet);
        var dupSetBtn = Mk("Duplicate", 80, DuplicateSelectedSet);
        var deleteSetBtn = Mk("Delete", 64, DeleteSelectedSet);

        _scriptsList = new ListBox
        {
            IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true
        };
        var addBtn = Mk("Add .dyn…", 100, AddScripts);
        var removeBtn = Mk("Remove selected", 130, RemoveSelected);
        var moveUpBtn = Mk("Move ▲", 70, () => MoveSelected(-1));
        var moveDownBtn = Mk("Move ▼", 70, () => MoveSelected(1));

        _nameBox = new TextBox();
        var saveBtn = Mk("Save set", 84, SaveSet);
        var closeBtn = new Button
        {
            Text = "Close", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 0), DialogResult = DialogResult.OK
        };

        // Responsive single-column layout — the scripts list fills, everything else auto-sizes.
        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Saved script sets:", _setsCombo, newSetBtn, loadSetBtn, dupSetBtn, deleteSetBtn));
        FormLayout.AddAutoRow(root, new Label { Text = "Scripts (.dyn) in this set — run in order:", AutoSize = true, Margin = new Padding(3, 8, 3, 2) });
        FormLayout.AddFillRow(root, _scriptsList, 100);
        FormLayout.AddAutoRow(root, FormLayout.FlowRow(addBtn, removeBtn, moveUpBtn, moveDownBtn));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Set name:", _nameBox, saveBtn, closeBtn));

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(root);
        Controls.Add(_status);
        CancelButton = closeBtn;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        RefreshSetsCombo();
    }

    private void RefreshSetsCombo()
    {
        string? keep = (_setsCombo.SelectedItem as SetItem)?.Set.Name;
        _setsCombo.Items.Clear();
        foreach (ScriptSet s in ScriptSetStore.LoadAll())
            _setsCombo.Items.Add(new SetItem(s));
        if (keep != null)
            for (int i = 0; i < _setsCombo.Items.Count; i++)
                if (((SetItem)_setsCombo.Items[i]).Set.Name == keep) { _setsCombo.SelectedIndex = i; break; }
    }

    private void NewSet()
    {
        _scriptsList.Items.Clear();
        _nameBox.Text = "";
        _setsCombo.SelectedIndex = -1;
        _loadedSetName = null;
        _loadedSetId = null;
        SetStatus("New set — add .dyn scripts, name it, then Save set.");
    }

    private void LoadSelectedSet()
    {
        if (_setsCombo.SelectedItem is not SetItem item) { SetStatus("Pick a saved set first."); return; }
        _scriptsList.Items.Clear();
        foreach (string p in item.Set.ScriptPaths)
            _scriptsList.Items.Add(p);
        _nameBox.Text = item.Set.Name;
        _loadedSetName = item.Set.Name;
        _loadedSetId = item.Set.Id;
        SetStatus($"Loaded '{item.Set.Name}' ({item.Set.ScriptPaths.Count} scripts).");
    }

    /// <summary>Clones the selected saved script set to a new uniquely-named copy, then loads it for editing.</summary>
    private void DuplicateSelectedSet()
    {
        if (_setsCombo.SelectedItem is not SetItem item) { SetStatus("Pick a saved set to duplicate."); return; }
        string newName = UniqueSetName(item.Set.Name);
        var copy = new ScriptSet { Name = newName, ScriptPaths = new List<string>(item.Set.ScriptPaths) };
        try { ScriptSetStore.Save(copy); }
        catch (Exception ex) { SetStatus("Could not duplicate set: " + ex.Message.Split('\n')[0]); return; }
        RefreshSetsCombo();
        for (int i = 0; i < _setsCombo.Items.Count; i++)
            if (((SetItem)_setsCombo.Items[i]).Set.Name == newName) { _setsCombo.SelectedIndex = i; break; }
        LoadSelectedSet();
        SetStatus($"Duplicated '{item.Set.Name}' as '{newName}'. Rename and Save set if you like.");
    }

    /// <summary>The shared "&lt;name&gt; (copy)" rule — see <see cref="SetDuplication.UniqueName"/>.</summary>
    private static string UniqueSetName(string baseName) =>
        SetDuplication.UniqueName(baseName, ScriptSetStore.LoadAll().Select(s => s.Name));

    private void DeleteSelectedSet()
    {
        if (_setsCombo.SelectedItem is not SetItem item) { SetStatus("Pick a saved set to delete."); return; }
        if (MessageBox.Show(this, $"Delete script set '{item.Set.Name}'?", "Delete set",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        ScriptSetStore.Delete(item.Set.Name);
        if (string.Equals(_loadedSetName, item.Set.Name, StringComparison.OrdinalIgnoreCase))
        {
            _loadedSetName = null;
            _loadedSetId = null;
        }
        RefreshSetsCombo();
        SetStatus($"Deleted '{item.Set.Name}'.");
    }

    private void AddScripts()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add Dynamo scripts",
            Filter = "Dynamo scripts (*.dyn)|*.dyn|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var existing = _scriptsList.Items.Cast<string>().Select(s => s.ToLowerInvariant()).ToHashSet();
        int added = 0;
        foreach (string p in dlg.FileNames)
            if (existing.Add(p.ToLowerInvariant())) { _scriptsList.Items.Add(p); added++; }
        SetStatus(added == 0 ? "Nothing new to add." : $"Added {added} script(s).");
    }

    private void RemoveSelected()
    {
        foreach (object sel in _scriptsList.SelectedItems.Cast<object>().ToList())
            _scriptsList.Items.Remove(sel);
    }

    /// <summary>Moves the single selected script up (-1) or down (+1) — scripts run in order.</summary>
    private void MoveSelected(int delta)
    {
        int i = _scriptsList.SelectedIndex;
        if (i < 0 || _scriptsList.SelectedItems.Count != 1)
        {
            SetStatus("Select exactly one script to reorder.");
            return;
        }
        int j = i + delta;
        if (j < 0 || j >= _scriptsList.Items.Count) return;

        object item = _scriptsList.Items[i];
        _scriptsList.Items.RemoveAt(i);
        _scriptsList.Items.Insert(j, item);
        _scriptsList.SelectedIndex = j;
    }

    private void SaveSet()
    {
        string name = _nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a set name.", "Save set", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_scriptsList.Items.Count == 0)
        {
            MessageBox.Show(this, "Add at least one .dyn script.", "Save set",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var set = new ScriptSet
        {
            // Keep the loaded set's id across a rename; a brand-new set gets a fresh one.
            Id = _loadedSetId ?? Guid.NewGuid().ToString("N"),
            Name = name,
            ScriptPaths = _scriptsList.Items.Cast<string>().ToList()
        };
        bool renaming = _loadedSetName != null && !string.Equals(_loadedSetName, name, StringComparison.OrdinalIgnoreCase);
        if (renaming && ScriptSetStore.LoadAll().Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            if (MessageBox.Show(this,
                    $"A script set named '{name}' already exists. Overwrite it?", "Rename set",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
        }

        try
        {
            ScriptSetStore.Save(set);
            // Keyed by file name, so a name change must delete the old file or it lingers as a duplicate.
            if (renaming) ScriptSetStore.Delete(_loadedSetName!);
            _loadedSetName = name;
            _loadedSetId = set.Id;
            RefreshSetsCombo();
            string verb = renaming ? "Renamed to" : "Saved";
            SetStatus($"{verb} '{name}' ({set.ScriptPaths.Count} scripts) in {ScriptSetStore.ScriptSetsDirectory}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save set:\n" + ex.Message, "Save set",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatus(string text) { _status.SetMessage(text); }

    private sealed class SetItem
    {
        public ScriptSet Set { get; }
        public SetItem(ScriptSet set) => Set = set;
        public override string ToString() => $"{Set.Name}  ({Set.ScriptPaths.Count} scripts)";
    }
}
