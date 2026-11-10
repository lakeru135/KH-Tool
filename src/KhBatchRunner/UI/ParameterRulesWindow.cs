using System.IO;
using KhBatchRunner.ParamFormula;

namespace KhBatchRunner.UI;

/// <summary>
/// What the Parameter Helper windows need from the model. Kept apart from Revit's Document so the
/// windows can be built offline by tools\ThemePreview (the construction and layout gates).
/// </summary>
public interface IParameterRulesHost
{
    string ModelTitle { get; }
    bool IsWorkshared { get; }
    /// <summary>Who has the rules borrowed right now, from the local cache (display only), or null.</summary>
    string? RulesBorrowedBy { get; }
    string CategoryLabel(string categoryName);
    List<ParamChoice> Targets(IList<string> categories);
    List<ParamChoice> Sources(IList<string> categories);
    (bool Ok, string Message) Check(FormulaRule rule);
    /// <summary>A dry run of the whole set: what Apply would change, touching nothing.</summary>
    ApplyResult Preview(FormulaRuleSet set);
    void ShowResults(string title, ApplyResult result, bool dryRun);
    /// <summary>What the live updater did last in this model, or "".</summary>
    string LiveActivity { get; }
    /// <summary>The chosen categories with no element in the model yet.</summary>
    List<string> EmptyCategories(IList<string> categories);
    /// <summary>
    /// Saves the rules into the model before the window closes, so a refusal never loses the edits.
    /// <paramref name="openedJson"/> is the rules as they were when the window opened: if the model's
    /// rules have changed since, nothing is saved and <c>ChangedSinceOpened</c> is set — unless
    /// <paramref name="replace"/> says the user has already agreed to replace them.
    /// </summary>
    (bool Saved, bool ChangedSinceOpened, string? Why) TrySave(FormulaRuleSet set, string openedJson, bool replace);
}

/// <summary>
/// Parameter Helper → "Formula Rules": the model's rules, in run order. Each rule writes a formula's
/// result into a parameter of every element in its categories. Saved INTO the model (so a central
/// model's users share one set); Export/Import copies a set between projects as a .json file.
/// </summary>
public sealed class ParameterRulesWindow : Form
{
    private readonly IParameterRulesHost _host;
    private readonly FormulaRuleSet _set;
    /// <summary>The rules as the model had them when this window opened (to notice someone else's change).</summary>
    private readonly string _openedJson;
    private readonly DataGridView _grid;
    private readonly CheckBox _live;
    private readonly Label _status;

    /// <summary>The edited set, when the user pressed Save or Save and Apply.</summary>
    public FormulaRuleSet Result => _set;
    public bool ApplyAfterSave { get; private set; }

    public ParameterRulesWindow(IParameterRulesHost host, FormulaRuleSet set)
    {
        _host = host;
        _set = set.Clone();
        _openedJson = set.ToJson();

        Text = "Formula Rules — Parameter Helper";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(860, 520);

        Label note = FormLayout.NoteLabel(this,
            "Each rule writes a formula's result into a parameter of every element in its categories — " +
            "like a schedule's calculated value, but stored on the element, so it can be tagged, " +
            "filtered and exported. Rules run top to bottom; a rule that reads another rule's result " +
            "runs after it. Only values that differ are written. The rules are saved in this model, so " +
            "everyone on a central model uses the same ones.");

        string? borrowed = host.RulesBorrowedBy;
        Label? lockNote = borrowed == null ? null : FormLayout.NoteLabel(this,
            $"These rules are currently borrowed by {borrowed}. You can look and preview, but Save will " +
            "work only once they sync and relinquish.");

        _grid = new DataGridView
        {
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            MinimumSize = new Size(0, 130),
            Height = 240
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = "On", FillWeight = 8 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = "Writes to", ReadOnly = true, FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Formula", HeaderText = "Formula", ReadOnly = true, FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Categories", HeaderText = "Categories", ReadOnly = true, FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Note", HeaderText = "Note", ReadOnly = true, FillWeight = 18 });
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            _set.Rules[e.RowIndex].Enabled = _grid.Rows[e.RowIndex].Cells[0].Value is true;
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditRule(e.RowIndex); };

        Button B(string text, Action click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
            b.Click += (_, _) => click();
            return b;
        }
        var editRow = FormLayout.FlowRow(
            B("Add…", AddRule), B("Edit…", () => EditRule(Selected)), B("Duplicate", DuplicateRule),
            B("Delete", DeleteRule), B("Move up", () => MoveRule(-1)), B("Move down", () => MoveRule(1)));
        editRow.Dock = DockStyle.Fill;
        editRow.WrapContents = true;

        _live = new CheckBox
        {
            Text = "Live — recalculate as elements change (for everyone with KH-Tool who opens this model)",
            AutoSize = true,
            Checked = _set.Live
        };
        _live.CheckedChanged += (_, _) => _set.Live = _live.Checked;
        Label liveNote = FormLayout.NoteLabel(this,
            "Live: an element you change gets its values in the same edit (one Undo takes back both). " +
            "When a TYPE changes, its elements are written a moment later, borrowing only those that " +
            "change. Changes that arrive by Sync or Reload Latest are not recalculated — use Apply Now. " +
            "A value typed by hand into a rule's target is replaced on the next recalculation.");

        _status = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 4, 3, 4) };
        string activity = host.LiveActivity;
        _status.Text = activity.Length > 0 ? "Last live update: " + activity : "";

        var import = B("Import…", ImportSet);
        var export = B("Export…", ExportSet);
        var preview = B("Preview", PreviewSet);
        var save = B("Save", () => Finish(apply: false));
        var saveApply = B("Save and Apply", () => Finish(apply: true));
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        var leftButtons = FormLayout.FlowRow(import, export, preview);
        var rightButtons = FormLayout.FlowRow(save, saveApply, cancel);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rightButtons.Anchor = AnchorStyles.Right;
        bottom.Controls.Add(leftButtons, 0, 0);
        bottom.Controls.Add(rightButtons, 1, 0);

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, note);
        if (lockNote != null) FormLayout.AddAutoRow(root, lockNote);
        FormLayout.AddFillRow(root, _grid, 100);
        FormLayout.AddAutoRow(root, editRow);
        FormLayout.AddAutoRow(root, _live);
        FormLayout.AddAutoRow(root, liveNote);
        FormLayout.AddAutoRow(root, _status);
        FormLayout.AddAutoRow(root, bottom);
        Controls.Add(root);
        CancelButton = cancel;

        Theme.Apply(this);
        note.ForeColor = Theme.Dim(note);
        liveNote.ForeColor = Theme.Dim(liveNote);
        _status.ForeColor = Theme.Dim(_status);
        Fill();
        ClientSize = new Size(Math.Max(ClientSize.Width, root.PreferredSize.Width),
            Math.Max(ClientSize.Height, root.PreferredSize.Height + 120));
        MinimumSize = new Size(720, Size.Height - 60);
    }

    private int Selected => _grid.CurrentRow?.Index ?? -1;

    private void Fill(int select = -1)
    {
        _grid.Rows.Clear();
        foreach (FormulaRule r in _set.Rules)
            _grid.Rows.Add(r.Enabled, r.TargetOnType ? r.TargetName + " (type)" : r.TargetName, r.Formula,
                string.Join(", ", r.Categories.Select(_host.CategoryLabel)), r.Note);
        if (select >= 0 && select < _grid.Rows.Count)
        {
            _grid.ClearSelection();
            _grid.Rows[select].Selected = true;
            _grid.CurrentCell = _grid.Rows[select].Cells[1];
        }
    }

    private void AddRule()
    {
        var rule = new FormulaRule();
        if (Selected >= 0) rule.Categories = new List<string>(_set.Rules[Selected].Categories);
        using var dlg = new FormulaRuleDialog(_host, rule);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _set.Rules.Add(dlg.Rule);
        Fill(_set.Rules.Count - 1);
    }

    private void EditRule(int index)
    {
        if (index < 0 || index >= _set.Rules.Count) return;
        using var dlg = new FormulaRuleDialog(_host, _set.Rules[index].Clone());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _set.Rules[index] = dlg.Rule;
        Fill(index);
    }

    private void DuplicateRule()
    {
        int i = Selected;
        if (i < 0) return;
        FormulaRule copy = _set.Rules[i].Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Enabled = false;   // two enabled rules on one target would be refused; let the user change it first
        _set.Rules.Insert(i + 1, copy);
        Fill(i + 1);
    }

    private void DeleteRule()
    {
        int i = Selected;
        if (i < 0) return;
        if (MessageBox.Show(this, $"Delete the rule\n\n{_set.Rules[i]}\n\nValues it already wrote stay as they are.",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _set.Rules.RemoveAt(i);
        Fill(Math.Min(i, _set.Rules.Count - 1));
    }

    private void MoveRule(int delta)
    {
        int i = Selected, j = i + delta;
        if (i < 0 || j < 0 || j >= _set.Rules.Count) return;
        (_set.Rules[i], _set.Rules[j]) = (_set.Rules[j], _set.Rules[i]);
        Fill(j);
    }

    private void ImportSet()
    {
        using var dlg = new OpenFileDialog { Filter = "Parameter Helper rules (*.json)|*.json", Title = "Import rules" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        FormulaRuleSet incoming;
        try { incoming = FormulaRuleSet.FromJson(File.ReadAllText(dlg.FileName)); }
        catch (Exception ex) { MessageBox.Show(this, "Could not read that file:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        DialogResult add = _set.Rules.Count == 0 ? DialogResult.No : MessageBox.Show(this,
            $"{incoming.Rules.Count} rule(s) in the file.\n\nYes = add them after the current rules\nNo = replace the current rules",
            Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (add == DialogResult.Cancel) return;
        if (add == DialogResult.No) _set.Rules.Clear();
        foreach (FormulaRule r in incoming.Rules) { r.Id = Guid.NewGuid().ToString("N"); _set.Rules.Add(r); }
        Fill(_set.Rules.Count - 1);
    }

    private void ExportSet()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Parameter Helper rules (*.json)|*.json",
            FileName = SafeName(_host.ModelTitle) + " - formula rules.json",
            Title = "Export rules"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var copy = _set.Clone();
            copy.Live = false;   // a file carries rules, not a decision about another model's live mode
            copy.MirrorCheck = false;
            copy.MirrorTargetGuid = copy.MirrorTargetName = "";
            File.WriteAllText(dlg.FileName, copy.ToJson());
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not write the file:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static string SafeName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private void PreviewSet()
    {
        Cursor = Cursors.WaitCursor;
        ApplyResult r;
        try { r = _host.Preview(_set); }
        finally { Cursor = Cursors.Default; }
        _host.ShowResults("Preview — nothing changed", r, dryRun: true);
    }

    /// <summary>
    /// Saves BEFORE closing. If the rules cannot be saved — someone else is editing them, or they
    /// changed in central — the window stays open with the edits intact and offers to export them.
    /// </summary>
    private void Finish(bool apply)
    {
        Cursor = Cursors.WaitCursor;
        (bool saved, bool changed, string? why) r;
        try
        {
            r = _host.TrySave(_set, _openedJson, replace: false);
            if (!r.saved && r.changed)
            {
                Cursor = Cursors.Default;
                if (MessageBox.Show(this,
                        "The rules in this model were changed by someone else after you opened this window " +
                        "(they arrived by Reload Latest or Sync).\n\nReplace them with yours?\n\n" +
                        "No = keep this window open; Export your rules, close, and open Formula Rules again to see theirs.",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                Cursor = Cursors.WaitCursor;
                r = _host.TrySave(_set, _openedJson, replace: true);
            }
        }
        finally { Cursor = Cursors.Default; }

        if (!r.saved)
        {
            if (MessageBox.Show(this,
                    "The rules were not saved.\n\n" + (r.why ?? "Revit did not accept the change.") +
                    "\n\nYour edits are still here. Export them to a file so they are kept whatever happens?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                ExportSet();
            return;
        }
        ApplyAfterSave = apply;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Add or edit one rule: categories, target, formula — checked on a real element as you type.</summary>
public sealed class FormulaRuleDialog : Form
{
    private readonly IParameterRulesHost _host;
    private readonly FormulaRule _rule;
    private readonly Label _categories;
    private readonly Label _catNote;
    private readonly Control _catRow;
    private readonly ComboBox _target;
    private readonly TextBox _formula;
    private readonly ComboBox _insert;
    private readonly Label _typeNote;
    private readonly Control _typeRow;
    private readonly Label _check;
    private readonly TextBox _note;
    private readonly Button _ok;
    private readonly TableLayoutPanel _root;
    private readonly FlowLayoutPanel _buttons;
    // Typing re-checks the formula only once the user pauses: checking every keystroke flashed a red
    // error for every half-typed formula ("Missing ']'" while still typing "[Len…").
    private readonly System.Windows.Forms.Timer _typingPause = new() { Interval = 500 };
    private List<ParamChoice> _targets = new();
    private List<ParamChoice> _sources = new();
    private bool _checkOk;
    private bool _checkPending;

    public FormulaRule Rule => _rule;

    public FormulaRuleDialog(IParameterRulesHost host, FormulaRule rule)
    {
        _host = host;
        _rule = rule;

        Text = string.IsNullOrEmpty(rule.TargetName) ? "New rule — Parameter Helper" : "Edit rule — Parameter Helper";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(700, 440);

        _categories = new Label { AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        _catNote = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 0, 3, 2) };
        var pickCats = new Button { Text = "Categories…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(90, 0) };
        pickCats.Click += (_, _) => PickCategories();

        _target = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _target.SelectedIndexChanged += (_, _) => { ReadTarget(); FillInsert(); Recheck(); };
        _typeNote = new Label
        {
            AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 0, 3, 2),
            Text = "One value per type: the formula reads type parameters only."
        };

        _formula = new TextBox { Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical, Text = rule.Formula, Font = new Font("Consolas", 10f) };
        _formula.TextChanged += (_, _) =>
        {
            _rule.Formula = _formula.Text;
            _checkPending = true;
            _typingPause.Stop();
            _typingPause.Start();
        };
        _typingPause.Tick += (_, _) => { _typingPause.Stop(); Recheck(); };
        _formula.Leave += (_, _) => { if (_checkPending) Recheck(); };
        FormClosed += (_, _) => _typingPause.Dispose();

        _insert = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        var insert = new Button { Text = "Insert", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(70, 0) };
        insert.Click += (_, _) => InsertParam();

        // Two lines are always reserved for the check, so a message that comes and goes while the
        // user types does not make the rows below it jump.
        _check = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 4, 3, 4) };
        _note = new TextBox { Text = rule.Note };

        Label help = FormLayout.NoteLabel(this,
            "Parameters: [Unconnected Height], [type:Width] (the type's), or a bare name without spaces. " +
            "Numbers with units: 100 mm, 2.5 m, 12 m². Operators: + - * / ^ ( ), compare = <> < > <= >=, " +
            "join text with &. Functions: if(test, a, b), and(), or(), not(), round(x) / round(x, 5 mm), " +
            "roundup, rounddown, abs, sqrt, min, max. Text in \"quotes\". Units must agree, as in Revit.");

        _ok = new Button { Text = "OK", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        _ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };

        var root = FormLayout.SingleColumn();
        // Scrolls rather than overlaps if the notes ever outgrow the screen; the OK/Cancel strip is
        // OUTSIDE it, docked to the bottom, so it can never be pushed out of the window. (It used to be
        // the last row of this table: a long error or category list pushed it off the bottom.)
        root.AutoScroll = true;
        _root = root;
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Categories:", _categories, pickCats));
        _catRow = FormLayout.LabeledRow(" ", _catNote);
        FormLayout.AddAutoRow(root, _catRow);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Write result to:", _target));
        _typeRow = FormLayout.LabeledRow(" ", _typeNote);
        FormLayout.AddAutoRow(root, _typeRow);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Formula:", _formula));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Parameter:", _insert, insert));
        FormLayout.AddAutoRow(root, _check);
        FormLayout.AddAutoRow(root, help);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Note:", _note));
        _buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(8, 4, 8, 8)
        };
        _buttons.Controls.Add(cancel);
        _buttons.Controls.Add(_ok);
        Controls.Add(root);       // Fill: docked last, takes what the bottom strip leaves
        Controls.Add(_buttons);
        AcceptButton = _ok;
        CancelButton = cancel;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        help.ForeColor = Theme.Dim(help);
        _typeNote.ForeColor = Theme.Dim(_typeNote);
        _catNote.ForeColor = Theme.Dim(_catNote);
        LoadChoices();
        Recheck();
        Wrap();
        ClientSize = new Size(ClientSize.Width, Math.Max(ClientSize.Height, NeededHeight()));
        MinimumSize = new Size(560, 420);
        Resize += (_, _) => Wrap();
    }

    /// <summary>
    /// Every label whose text grows — the category list, the notes, the check — wraps at the window's
    /// width instead of widening the window (twelve categories made one line 914px wide).
    /// </summary>
    private void Wrap()
    {
        int field = Math.Max(240, ClientSize.Width - 260);   // beside the label column and the Categories… button
        _categories.MaximumSize = new Size(field, 0);
        _catNote.MaximumSize = new Size(Math.Max(240, ClientSize.Width - 190), 0);
        _typeNote.MaximumSize = new Size(Math.Max(240, ClientSize.Width - 190), 0);
        _check.MaximumSize = new Size(Math.Max(240, ClientSize.Width - 40), 0);
        _check.MinimumSize = new Size(0, 2 * _check.Font.Height + 2);
    }

    private int NeededHeight() => _root.PreferredSize.Height + _buttons.PreferredSize.Height + 8;

    /// <summary>After content changes, grow the window to show it all — up to the screen's height; past
    /// that the form scrolls. Never shrinks under the user.</summary>
    private void FitHeight()
    {
        if (!IsHandleCreated) return;
        Wrap();
        _root.PerformLayout();
        int max = Screen.FromControl(this).WorkingArea.Height - 40;
        int want = Math.Min(max, NeededHeight());
        if (want > ClientSize.Height) ClientSize = new Size(ClientSize.Width, want);
    }

    /// <summary>For the layout gate: what typing and picking do, without a keyboard.</summary>
    internal void SimulateEdit(List<string>? categories, string? formula)
    {
        if (categories != null) { _rule.Categories = categories; LoadChoices(); }
        if (formula != null) { _formula.Text = formula; }
        Recheck();
    }

    private void PickCategories()
    {
        using var dlg = new CategoryPickerDialog(_rule.Categories);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _rule.Categories = dlg.SelectedCategoryNames;
        LoadChoices();
        Recheck();
    }

    private void LoadChoices()
    {
        _categories.Text = _rule.Categories.Count == 0 ? "(none — choose at least one)"
            : string.Join(", ", _rule.Categories.Select(_host.CategoryLabel));
        // With several categories the lists hold only what they ALL carry; say so, and name any
        // category with nothing in the model to compare (it is left out of that test).
        List<string> empty = _rule.Categories.Count == 0 ? new List<string>() : _host.EmptyCategories(_rule.Categories);
        var notes = new List<string>();
        if (_rule.Categories.Count > 1) notes.Add($"Showing the parameters all {_rule.Categories.Count - empty.Count} categories share.");
        if (empty.Count > 0) notes.Add("No elements yet: " + string.Join(", ", empty.Select(_host.CategoryLabel)) + ".");
        _catNote.Text = string.Join(" ", notes);
        _catNote.MaximumSize = new Size(Math.Max(300, ClientSize.Width - 190), 0);
        if (_catRow != null) _catRow.Visible = notes.Count > 0;
        FitHeight();

        _targets = _rule.Categories.Count == 0 ? new List<ParamChoice>() : _host.Targets(_rule.Categories);
        _target.BeginUpdate();
        _target.Items.Clear();
        foreach (ParamChoice c in _targets) _target.Items.Add(c);
        int pick = _targets.FindIndex(c => string.Equals(c.Name, _rule.TargetName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Guid, _rule.TargetGuid, StringComparison.OrdinalIgnoreCase) && c.OnType == _rule.TargetOnType);
        if (pick < 0 && _rule.TargetName.Length > 0)
        {
            // Saved on another model, or no element carries it here yet: keep it visible, not silently dropped.
            _targets.Add(new ParamChoice { Name = _rule.TargetName, Guid = _rule.TargetGuid, OnType = _rule.TargetOnType,
                Label = _rule.TargetName + (_rule.TargetOnType ? "  (type · " : "  (") + "not found on these categories)" });
            _target.Items.Add(_targets[^1]);
            pick = _targets.Count - 1;
        }
        _target.SelectedIndex = pick;
        _target.EndUpdate();

        _sources = _rule.Categories.Count == 0 ? new List<ParamChoice>() : _host.Sources(_rule.Categories);
        FillInsert();
    }

    /// <summary>
    /// The parameters the formula can read, alphabetically. A type target can read only type
    /// parameters, so then only those are offered — and inserted as [Name], which means the type's.
    /// </summary>
    private void FillInsert()
    {
        if (_typeRow != null) _typeRow.Visible = _rule.TargetOnType;
        FitHeight();
        _typeNote.Text = ParamAccess.IsTypeName(_rule)
            ? "Renames each type. View filters, Dynamo scripts or saved presets that find a type by its " +
              "name will need the new name. A name another type already has is refused and reported."
            : "One value per type: the formula reads type parameters only.";
        _typeNote.MaximumSize = new Size(Math.Max(300, ClientSize.Width - 190), 0);
        _insert.BeginUpdate();
        _insert.Items.Clear();
        foreach (ParamChoice c in _sources)
            if (!_rule.TargetOnType || c.OnType) _insert.Items.Add(c);
        if (_insert.Items.Count > 0) _insert.SelectedIndex = 0;
        _insert.EndUpdate();
    }

    private void ReadTarget()
    {
        if (_target.SelectedItem is ParamChoice c)
        {
            _rule.TargetName = c.Name;
            _rule.TargetGuid = c.Guid;
            _rule.TargetOnType = c.OnType;
        }
    }

    private void InsertParam()
    {
        if (_insert.SelectedItem is not ParamChoice c) return;
        string token = _rule.TargetOnType ? $"[{c.Name}]" : c.Token;   // in a type rule every name is the type's
        int at = _formula.SelectionStart;
        _formula.Text = _formula.Text.Remove(at, _formula.SelectionLength).Insert(at, token);
        _formula.SelectionStart = at + token.Length;
        _formula.Focus();
    }

    private void Recheck()
    {
        _checkPending = false;
        _typingPause.Stop();
        bool ok;
        string msg;
        // Whatever goes wrong while checking is shown here, in the window — never as an exception
        // dialog, which repeated on every keystroke.
        try { (ok, msg) = _host.Check(_rule); }
        catch (Exception ex) { (ok, msg) = (false, "Could not check this rule: " + ex.Message); }
        _checkOk = ok;
        _check.Text = msg;
        _check.ForeColor = ok ? Theme.Dim(_check) : Color.Firebrick;
        FitHeight();
    }

    private void Accept()
    {
        if (_checkPending) Recheck();   // OK pressed straight after typing: judge what is in the box now
        _rule.Note = _note.Text.Trim();
        _rule.Formula = _formula.Text.Trim();
        if (!_checkOk &&
            MessageBox.Show(this, "This rule does not check out:\n\n" + _check.Text + "\n\nKeep it anyway? It will be reported, not run.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        DialogResult = DialogResult.OK;
        Close();
    }
}
