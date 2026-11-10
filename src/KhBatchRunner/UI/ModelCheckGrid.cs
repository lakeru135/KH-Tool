using System.Windows.Forms;
using KhBatchRunner.Forma;

namespace KhBatchRunner.UI;

/// <summary>
/// The list of Forma models a user picks from — model name, the Revit release it was saved in, what
/// kind of model it is, and why it cannot be chosen when it cannot.
///
/// <para><b>Why this is a grid and not the <see cref="CheckedListBox"/> it replaced.</b> Two
/// requirements arrived together in 1.3.8.2: show the Revit year and the model type as their own
/// columns, and grey out a model this Revit must not open so it cannot be selected at all. A
/// <c>CheckedListBox</c> can do neither — it has one string per row, and it throws outright if you
/// set <c>DrawMode</c> to anything but <c>Normal</c>, so there is no owner-draw route to a greyed
/// item. Both windows that pick models share this control so the rule cannot be enforced in one and
/// forgotten in the other, which is how the picker would otherwise have stayed a way round it.</para>
///
/// <para><b>Every cell is read-only and toggling is driven from the mouse handler.</b> Letting the
/// checkbox column edit itself would mean fighting <c>DataGridView</c>'s edit-commit cycle to keep a
/// blocked row un-tickable, and it would only toggle when the checkbox itself was hit — where the
/// list this replaces toggled on a click anywhere in the row. Driving it here keeps both the old
/// gesture and the guarantee.</para>
/// </summary>
internal sealed class ModelCheckGrid : DataGridView
{
    private const string ColCheck = "check";
    private const string ColName = "name";
    private const string ColVersion = "version";
    private const string ColType = "type";
    private const string ColNote = "note";

    /// <summary>Row index of the last plain click, for Shift+click range toggling — the same
    /// anchor <see cref="CheckListBehavior.EnableRangeCheck"/> keeps for the lists it serves.</summary>
    private int _anchor = -1;

    private string _runningRevitVersion = "";

    public ModelCheckGrid()
    {
        Dock = DockStyle.Fill;
        MinimumSize = new Size(0, 60);
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        RowHeadersVisible = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        MultiSelect = false;
        ReadOnly = true;                       // see the class summary: toggling is done in code
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColCheck,
            HeaderText = "",
            Width = 28,
            Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        Columns.Add(Text(ColName, "Model", DataGridViewAutoSizeColumnMode.Fill, fillWeight: 100));
        Columns.Add(Text(ColVersion, "Revit", DataGridViewAutoSizeColumnMode.None, width: 60));
        Columns.Add(Text(ColType, "Type", DataGridViewAutoSizeColumnMode.None, width: 120));
        Columns.Add(Text(ColNote, "", DataGridViewAutoSizeColumnMode.Fill, fillWeight: 120));
    }

    private static DataGridViewTextBoxColumn Text(
        string name, string header, DataGridViewAutoSizeColumnMode mode, int width = 100, int fillWeight = 100) =>
        new()
        {
            Name = name,
            HeaderText = header,
            AutoSizeMode = mode,
            Width = width,
            FillWeight = fillWeight,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

    /// <summary>How many models are listed, blocked ones included.</summary>
    public int Count => Rows.Count;

    /// <summary>The models the user has ticked. A blocked model can never be among them.</summary>
    public IEnumerable<FormaModelItem> CheckedModels
    {
        get
        {
            foreach (DataGridViewRow row in Rows)
                if (row.Tag is FormaModelItem m && IsChecked(row))
                    yield return m;
        }
    }

    public void Clear() { Rows.Clear(); _anchor = -1; }

    /// <summary>
    /// Replaces the list. <paramref name="runningRevitVersion"/> is this Revit's own
    /// <c>Application.VersionNumber</c> — the thing each model's year is compared against, and empty
    /// when the caller has no Revit to ask (the controller), in which case nothing is blocked on
    /// version. See <see cref="FormaModelItem.BlockedReason"/> for the rule.
    /// </summary>
    public void SetModels(IEnumerable<FormaModelItem> models, string runningRevitVersion)
    {
        _runningRevitVersion = runningRevitVersion ?? "";
        SuspendLayout();
        Rows.Clear();
        _anchor = -1;

        Color dim = Theme.Dim(DefaultCellStyle.ForeColor, DefaultCellStyle.BackColor);
        foreach (FormaModelItem m in models)
        {
            string? blocked = m.BlockedReason(_runningRevitVersion);
            int i = Rows.Add(false, m.DisplayName, m.VersionLabel, m.TypeLabel, blocked ?? "");
            DataGridViewRow row = Rows[i];
            row.Tag = m;
            if (blocked != null)
            {
                row.DefaultCellStyle.ForeColor = dim;
                row.DefaultCellStyle.SelectionForeColor = dim;
            }
        }

        ClearSelection();
        ResumeLayout();
    }

    /// <summary>Whether a row may be ticked at all — the single place the block is enforced.</summary>
    private bool Selectable(int rowIndex) =>
        rowIndex >= 0 && rowIndex < Rows.Count &&
        Rows[rowIndex].Tag is FormaModelItem m && m.BlockedReason(_runningRevitVersion) == null;

    private static bool IsChecked(DataGridViewRow row) =>
        row.Cells[ColCheck].Value is bool b && b;

    private void SetChecked(int rowIndex, bool value)
    {
        if (!Selectable(rowIndex)) return;
        Rows[rowIndex].Cells[ColCheck].Value = value;
    }

    protected override void OnCellMouseDown(DataGridViewCellMouseEventArgs e)
    {
        base.OnCellMouseDown(e);
        if (e.RowIndex < 0 || e.Button != MouseButtons.Left) return;

        bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
        if (shift && _anchor >= 0 && _anchor != e.RowIndex)
        {
            // Apply the state the clicked row is about to take to the whole range, skipping blocked
            // rows — a range select is a convenience, not a way past the rule.
            bool target = !(Rows[e.RowIndex].Cells[ColCheck].Value is bool b && b);
            int lo = Math.Min(_anchor, e.RowIndex), hi = Math.Max(_anchor, e.RowIndex);
            for (int k = lo; k <= hi; k++) SetChecked(k, target);
        }
        else
        {
            SetChecked(e.RowIndex, !IsChecked(Rows[e.RowIndex]));
            _anchor = e.RowIndex;
        }
    }
}
