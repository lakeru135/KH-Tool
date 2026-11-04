using System.Drawing;
using System.Windows.Forms;
using KhBatchRunner.ParamFormula;

namespace KhBatchRunner.UI;

/// <summary>
/// What the Mirror Check window needs from the model. Kept apart from Revit's Document so the
/// window can be built offline by tools\ThemePreview (the construction and layout gates).
/// </summary>
public interface IMirrorCheckHost
{
    string ModelTitle { get; }
    /// <summary>Shared text instance parameters that could be written instead of KH_Mirror State.</summary>
    List<MirrorTarget> Targets();
    /// <summary>
    /// The parameter, the live setting and the counts, read from the model now, for
    /// <paramref name="targetGuid"/> ("" = KH_Mirror State); null = the parameter the model's setting names.
    /// </summary>
    MirrorStatus Status(string? targetGuid);
    /// <summary>Adds KH_Mirror State (or the categories it lacks); returns why it could not, or null.</summary>
    string? SetUp();
    /// <summary>A dry run: what Check All Now would change in <paramref name="targetGuid"/>, touching nothing.</summary>
    ApplyResult Preview(string targetGuid);
    void ShowResults(string title, ApplyResult result, bool dryRun);
    /// <summary>What the live updaters did last in this model, or "".</summary>
    string LiveActivity { get; }
    /// <summary>
    /// Saves the setting into the model: live on/off and the parameter to write. When
    /// <paramref name="extend"/>, a parameter of the user's own is first given every model category
    /// and allowed to vary between groups.
    /// </summary>
    (bool Saved, string? Why) Save(bool live, string targetGuid, string targetName, bool extend);
}

/// <summary>
/// Parameter Helper → "Mirror Check": keeps one text parameter on every family instance — its own
/// KH_Mirror State, or a shared text parameter of the user's own — holding blank, or e.g.
/// "Mirrored, Hand Flipped", so mirrored and flipped elements can be scheduled, tagged and
/// coloured by a view filter. Live mode keeps it up to date as people edit; Check All Now catches
/// up the rest.
/// </summary>
public sealed class MirrorCheckWindow : Form
{
    private const string OwnLabel = "KH_Mirror State  (Mirror Check's own)";

    private readonly IMirrorCheckHost _host;
    private readonly ComboBox _target;
    private readonly CheckBox _extend;
    private readonly TableLayoutPanel _root;
    private readonly Label _params;
    private readonly Button _setUp;
    private readonly FlowLayoutPanel _setUpRow;
    private readonly Label _counts;
    private readonly CheckBox _live;
    private readonly Label _status;
    private readonly Button _preview;
    private readonly Button _saveCheck;
    private bool _openedLive;
    private string _openedTarget = "";
    private string _shownTarget = "";
    private bool _filling;
    // Parameters of the user's own the user has already agreed may be overwritten.
    private readonly HashSet<string> _confirmed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user pressed Save and Check All: run Check All Now once this window has closed.</summary>
    public bool CheckAfterSave { get; private set; }

    private sealed class Choice
    {
        public string Guid = "";
        public string Name = "";
        public string Text = "";
        public override string ToString() => Text;
    }

    public MirrorCheckWindow(IMirrorCheckHost host)
    {
        _host = host;

        Text = "Mirror Check — Parameter Helper";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(640, 400);

        Label note = FormLayout.NoteLabel(this,
            "Mirror Check writes one text parameter on every family instance: blank, or what the element " +
            "is — e.g. \"Mirrored, Hand Flipped\". A view filter on \"has a value\" colours them all; it " +
            "works inside mirrored groups too. Use its own KH_Mirror State, or pick a shared text parameter " +
            "of your own. Only values that differ are written, so in a central model only those elements " +
            "are borrowed. The setting is saved in this model, so everyone on a central model with KH-Tool uses it.");

        Button B(string text, Action click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
            b.Click += (_, _) => click();
            return b;
        }

        _target = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _target.SelectedIndexChanged += (_, _) => TargetChanged();
        var targetRow = FormLayout.LabeledRow("Write to:", _target);

        _extend = new CheckBox
        {
            Text = "Add it to every model category and let it vary between groups",
            AutoSize = true,
            Checked = true
        };
        _extend.CheckedChanged += (_, _) => ShowParams();

        _params = FormLayout.NoteLabel(this, "");
        _setUp = B("Set up parameter", SetUp);
        _setUpRow = FormLayout.FlowRow(_setUp);

        _counts = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 4, 3, 4) };

        _live = new CheckBox { Text = "Live — update it as elements change", AutoSize = true };
        Label liveNote = FormLayout.NoteLabel(this,
            "Live, for everyone with KH-Tool who opens this model: an element you mirror, flip or place " +
            "gets its state in the same edit (one Undo takes back both). Changes that arrive by Sync or " +
            "Reload Latest, or that were made without KH-Tool, are not checked — Check All Now catches them up.");

        _status = FormLayout.NoteLabel(this, "");

        _preview = B("Preview", PreviewAll);
        var save = B("Save", () => Finish(check: false));
        _saveCheck = B("Save and Check All", () => Finish(check: true));
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        var leftButtons = FormLayout.FlowRow(_preview);
        var rightButtons = FormLayout.FlowRow(save, _saveCheck, cancel);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rightButtons.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        bottom.Controls.Add(leftButtons, 0, 0);
        bottom.Controls.Add(rightButtons, 1, 0);

        var root = _root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, targetRow);
        FormLayout.AddAutoRow(root, _extend);
        FormLayout.AddAutoRow(root, _params);
        FormLayout.AddAutoRow(root, _setUpRow);
        FormLayout.AddAutoRow(root, _counts);
        FormLayout.AddAutoRow(root, _live);
        FormLayout.AddAutoRow(root, liveNote);
        FormLayout.AddAutoRow(root, _status);
        // Takes any height the user adds by resizing, so the buttons stay on the bottom edge.
        FormLayout.AddFillRow(root, new Panel { Height = 0, Margin = Padding.Empty }, 100);
        FormLayout.AddAutoRow(root, bottom);
        Controls.Add(root);
        CancelButton = cancel;

        Theme.Apply(this);
        note.ForeColor = Theme.Dim(note);
        liveNote.ForeColor = Theme.Dim(liveNote);
        _status.ForeColor = Theme.Dim(_status);
        ReadModel(first: true, null);
        ClientSize = new Size(Math.Max(ClientSize.Width, root.PreferredSize.Width), ClientSize.Height);
        Fit();
    }

    /// <summary>
    /// Fits the window to its content, which changes with the chosen parameter. Nothing here gives
    /// way when the window shrinks (and narrower notes would wrap taller), so the size it opens at
    /// is also its minimum.
    /// </summary>
    private void Fit()
    {
        // Exactly the content: no slack under the buttons. It may shrink too (the window opens at a
        // guessed height, and the setup row comes and goes).
        MinimumSize = Size.Empty;
        ClientSize = new Size(ClientSize.Width, _root.PreferredSize.Height);
        MinimumSize = Size;
    }

    private Choice? Selected => _target.SelectedItem as Choice;

    private void FillTargets(string select)
    {
        _filling = true;
        try
        {
            _target.Items.Clear();
            _target.Items.Add(new Choice { Guid = "", Name = "KH_Mirror State", Text = OwnLabel });
            foreach (MirrorTarget t in _host.Targets())
                _target.Items.Add(new Choice { Guid = t.Guid, Name = t.Name, Text = t.ToString() });
            int i = _target.Items.Cast<Choice>().ToList().FindIndex(c => string.Equals(c.Guid, select, StringComparison.OrdinalIgnoreCase));
            _target.SelectedIndex = Math.Max(0, i);
        }
        finally { _filling = false; }
    }

    /// <summary>Reads the model for <paramref name="targetGuid"/> (null = what the model's setting names).</summary>
    private void ReadModel(bool first, string? targetGuid)
    {
        MirrorStatus s;
        Cursor = Cursors.WaitCursor;
        try { s = _host.Status(targetGuid); }
        finally { Cursor = Cursors.Default; }

        if (first)
        {
            _openedLive = s.Live;
            _live.Checked = s.Live;
            _openedTarget = s.TargetGuid;
            _confirmed.Add(s.TargetGuid);   // already the model's choice: no need to warn again
        }
        if (first || targetGuid == null) FillTargets(s.TargetGuid);
        _shownTarget = s.TargetGuid;

        bool own = s.TargetGuid.Length == 0;
        _paramsText = s.Problem ?? s.ParamsText;
        _params.ForeColor = s.Problem != null || s.MissingTarget != null ? Color.Firebrick : ForeColor;
        // Never read Visible back: it is false while the row (or the not-yet-shown window) is hidden,
        // which kept the button hidden for good.
        bool offerSetUp = own && !s.ParamsReady;
        _setUp.Visible = offerSetUp;
        _setUpRow.Visible = offerSetUp;   // a hidden button would still leave its row's margins
        _setUp.Enabled = s.Problem == null;

        // A parameter of the user's own: offer to make it cover what KH_Mirror State covers. Ticked by
        // default; greyed (and ticked) when there is nothing left to add.
        bool complete = s.TargetMissingCategories == 0 && s.TargetVaries;
        _extend.Visible = !own;
        _extend.Enabled = !own && !complete;
        if (!own && complete) _extend.Checked = true;
        ShowParams();

        MirrorCounts c = s.Counts;
        bool any = s.ParamsReady || c.Instances > c.NoParameters;
        _counts.Text =
            $"Family instances: {c.Instances}, mirrored or flipped: {c.Flagged}\n" +
            $"Mirrored: {c.Mirrored}     Hand flipped: {c.HandFlipped}     Facing flipped: {c.FacingFlipped}\n" +
            (any
                ? $"Out of date: {c.OutOfDate} element(s)" + (c.OutOfDate > 0 ? " — Check All Now writes them" : "") +
                  (c.NoParameters > 0 ? $"\nWithout the parameter: {c.NoParameters}" : "")
                : "Not set up yet: nothing is recorded on the elements.");

        _live.Enabled = any || (!own && _extend.Checked);
        _preview.Enabled = any;
        _saveCheck.Enabled = any || (!own && _extend.Checked);

        string activity = _host.LiveActivity;
        _status.Text = activity.Length > 0 ? "Last live update: " + activity : "";
        if (!first) Fit();
    }

    private string _paramsText = "";

    /// <summary>The line about the parameter, plus what Save will do to it while the box is ticked.</summary>
    private void ShowParams() =>
        _params.Text = _paramsText + (_extend.Enabled && _extend.Checked   // Enabled implies shown; Visible reads false before the window is
            ? " Save adds the categories it lacks and lets it vary between groups." : "");

    private void TargetChanged()
    {
        if (_filling || Selected is not Choice pick) return;
        if (pick.Guid.Length > 0 && !_confirmed.Contains(pick.Guid))
        {
            if (MessageBox.Show(this,
                    $"Mirror Check will write '{pick.Name}' on every family instance where it differs — " +
                    "including CLEARING it on elements that are not mirrored or flipped. Anything people have " +
                    "typed into it will be replaced.\n\nUse Preview before Check All Now to see what changes.",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                _filling = true;
                try
                {
                    int back = _target.Items.Cast<Choice>().ToList().FindIndex(c => string.Equals(c.Guid, _shownTarget, StringComparison.OrdinalIgnoreCase));
                    _target.SelectedIndex = Math.Max(0, back);
                }
                finally { _filling = false; }
                return;
            }
            _confirmed.Add(pick.Guid);
        }
        _extend.Checked = true;
        ReadModel(first: false, pick.Guid);
    }

    private void SetUp()
    {
        Cursor = Cursors.WaitCursor;
        string? why;
        try { why = _host.SetUp(); }
        finally { Cursor = Cursors.Default; }
        if (why != null)
        {
            MessageBox.Show(this, "The parameter was not set up.\n\n" + why, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ReadModel(first: false, _shownTarget);
    }

    private void PreviewAll()
    {
        Cursor = Cursors.WaitCursor;
        ApplyResult r;
        try { r = _host.Preview(_shownTarget); }
        finally { Cursor = Cursors.Default; }
        _host.ShowResults("Mirror Check preview — nothing changed", r, dryRun: true);
    }

    private void Finish(bool check)
    {
        Choice pick = Selected ?? new Choice();
        bool extend = pick.Guid.Length > 0 && _extend.Enabled && _extend.Checked;
        if (_live.Checked != _openedLive || !string.Equals(pick.Guid, _openedTarget, StringComparison.OrdinalIgnoreCase) || extend)
        {
            (bool saved, string? why) r;
            Cursor = Cursors.WaitCursor;
            try { r = _host.Save(_live.Checked, pick.Guid, pick.Name, extend); }
            finally { Cursor = Cursors.Default; }
            if (!r.saved)
            {
                MessageBox.Show(this, "The setting was not saved.\n\n" + (r.why ?? "Revit did not accept the change."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        CheckAfterSave = check;
        DialogResult = DialogResult.OK;
        Close();
    }
}
