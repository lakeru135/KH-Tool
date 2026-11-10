using KhBatchRunner.Actions;

namespace KhBatchRunner.UI;

/// <summary>
/// Per-model approval step of a Correct Workset FIX run: lists every workset rename the rules
/// propose for THIS model ("'old' → 'new'  (rule)"), all ticked by default — untick to keep a
/// workset's name (the rule then CREATES its target workset instead, so elements still land
/// correctly). Shown mid-batch by the action's approval callback; being a WinForms dialog it is
/// NOT auto-answered by <see cref="Batch.DialogSuppressor"/> (which only hooks Revit's own
/// dialogs). Closing the window without Apply counts as "skip all renames" — never rename
/// without an explicit yes.
/// </summary>
public sealed class WorksetRenameApprovalDialog : Form
{
    private readonly CheckedListBox _list;
    private readonly IReadOnlyList<WorksetRenameProposal> _proposals;
    private bool _applied;

    public WorksetRenameApprovalDialog(string modelName, IReadOnlyList<WorksetRenameProposal> proposals)
    {
        _proposals = proposals;

        Text = $"Correct Workset — {modelName}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        TopMost = true; // the batch runner's progress window is up — surface above it
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(560, 340);
        MinimumSize = new System.Drawing.Size(460, 260); // resizable, with a sensible floor

        Label note = FormLayout.NoteLabel(this,
            $"'{modelName}' has workset(s) matching a rule keyword. Ticked worksets are RENAMED " +
            "to the rule's target name; unticked ones keep their name and the target workset is " +
            "created instead. The run continues after Apply.");

        _list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, Dock = DockStyle.Fill };
        foreach (WorksetRenameProposal p in proposals)
            _list.Items.Add($"'{p.From}'  →  '{p.To}'    ({p.RuleLabel})", true);

        var apply = new Button { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(90, 0) };
        apply.Click += (_, _) =>
        {
            for (int i = 0; i < _proposals.Count; i++)
                _proposals[i].Approved = _list.GetItemChecked(i);
            _applied = true;
        };
        var skipAll = new Button { Text = "Skip all renames", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(120, 0), DialogResult = DialogResult.OK };
        skipAll.Click += (_, _) =>
        {
            foreach (WorksetRenameProposal p in _proposals) p.Approved = false;
            _applied = true;
        };

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddFillRow(root, _list, 100);
        var buttons = FormLayout.FlowRow(apply, skipAll);
        buttons.Anchor = AnchorStyles.Right;
        FormLayout.AddAutoRow(root, buttons);

        Controls.Add(root);
        AcceptButton = apply;
        Theme.Apply(this);
        note.ForeColor = Theme.Dim(note);

        // Closed via the X (no Apply): treat as skip-all — renaming needs an explicit yes.
        FormClosed += (_, _) =>
        {
            if (_applied) return;
            foreach (WorksetRenameProposal p in _proposals) p.Approved = false;
        };
    }
}
