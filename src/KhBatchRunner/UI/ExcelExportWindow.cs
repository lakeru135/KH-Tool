using System.IO;
using KhBatchRunner.ExcelLink;
using KhBatchRunner.Reporting;
// Import types individually — `using Autodesk.Revit.DB;` would collide Revit's Form with WinForms'.
using Document = Autodesk.Revit.DB.Document;
using ViewSchedule = Autodesk.Revit.DB.ViewSchedule;

namespace KhBatchRunner.UI;

/// <summary>
/// Excel Link → "Export Excel": pick which of the ACTIVE model's schedules to export and where the
/// workbook goes, then write it via <see cref="ScheduleWorkbookExporter"/>. Unsupported schedules
/// (key schedules, titleblock revision schedules, keynote legends, templates) are listed greyed
/// with the reason and can't be ticked. Read-only — nothing in the model changes.
/// </summary>
public sealed class ExcelExportWindow : Form
{
    private readonly Document _doc;
    private readonly List<(ViewSchedule Schedule, string? Unsupported)> _schedules;
    private readonly CheckedListBox _list;
    private readonly TextBox _filterBox;
    private readonly TextBox _pathBox;
    private readonly Button _okButton;

    // The list can be filtered, so the checkbox states can't live in the CheckedListBox alone (a
    // filtered-out row still exports). _checkedState is the master truth per _schedules index;
    // _visible maps a visible list row back to its master index; _rebuilding guards the repopulate.
    private readonly bool[] _checkedState;
    private readonly List<int> _visible = new();
    private bool _rebuilding;

    public ExcelExportWindow(Document doc)
    {
        _doc = doc;

        Text = "Export Excel — Excel Link";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(640, 480);
        MinimumSize = new System.Drawing.Size(560, 0);

        _schedules = ScheduleWorkbookExporter.ListSchedules(doc);
        _checkedState = new bool[_schedules.Count];
        _list = new CheckedListBox
        {
            CheckOnClick = true,
            IntegralHeight = false,
            Height = 240,
            MinimumSize = new System.Drawing.Size(0, 240)
        };
        // Unsupported entries stay visible (so the user sees WHY) but can never be ticked. Indices are
        // translated through _visible because the list only holds the rows matching the filter.
        _list.ItemCheck += (_, e) =>
        {
            int master = _visible[e.Index];
            if (_schedules[master].Unsupported != null) { e.NewValue = CheckState.Unchecked; return; }
            if (!_rebuilding) _checkedState[master] = e.NewValue == CheckState.Checked;
        };

        _filterBox = new TextBox();
        _filterBox.TextChanged += (_, _) => RebuildVisible();
        RebuildVisible(); // initial fill (empty filter = every schedule)

        var selectAll = new Button { Text = "Select all", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(90, 0) };
        selectAll.Click += (_, _) => SetAllChecks(true);
        var selectNone = new Button { Text = "Select none", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(90, 0) };
        selectNone.Click += (_, _) => SetAllChecks(false);

        _pathBox = new TextBox
        {
            Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                MatrixUtil.ModelName(doc) + "_Schedules.xlsx")
        };
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        browse.Click += (_, _) => BrowseForPath();

        Label note = FormLayout.NoteLabel(this,
            "Writes one worksheet per schedule. Row 1 is the legend — Instance (may edit), " +
            "Type (cannot edit), Read-only (cannot edit) — row 2 the headers; both stay frozen while " +
            "you scroll, and non-editable columns are grey. Column A holds each row's ElementId " +
            "(hidden) — don't change it; Import Excel uses it to find the element. One row per " +
            "ELEMENT: a schedule that isn't itemized exports more rows than it displays, and row " +
            "order can differ from the schedule's sorting.");

        _okButton = new Button { Text = "Export", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        _okButton.Click += (_, _) => Commit();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Filter:", _filterBox));
        FormLayout.AddFillRow(root, FormLayout.LabeledRow("Schedules:", _list), 100);
        var selectRow = FormLayout.FlowRow(selectAll, selectNone);
        selectRow.Anchor = AnchorStyles.Left;
        FormLayout.AddAutoRow(root, selectRow);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Workbook file:", _pathBox, browse));
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
            Math.Max(ClientSize.Width, root.PreferredSize.Width),
            Math.Max(ClientSize.Height, root.PreferredSize.Height));
        MinimumSize = Size; // resizable, but never smaller than the fitted content
    }

    // Repopulate the visible list from the current filter, restoring each row's checkbox from the
    // master _checkedState. Guarded so the Items.Add churn doesn't feed back through ItemCheck.
    private void RebuildVisible()
    {
        _rebuilding = true;
        try
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            _visible.Clear();
            string filter = _filterBox.Text.Trim();
            for (int i = 0; i < _schedules.Count; i++)
            {
                (ViewSchedule vs, string? reason) = _schedules[i];
                if (filter.Length > 0 && vs.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _visible.Add(i);
                _list.Items.Add(reason is null ? vs.Name : $"{vs.Name}   — {reason}", _checkedState[i]);
            }
            _list.EndUpdate();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    // Select all / none acts on the VISIBLE rows only (handy under an active filter); both the list
    // UI and the master _checkedState are updated so hidden rows keep their state.
    private void SetAllChecks(bool value)
    {
        for (int v = 0; v < _visible.Count; v++)
        {
            int master = _visible[v];
            bool check = value && _schedules[master].Unsupported is null;
            _list.SetItemChecked(v, check);
            _checkedState[master] = check;
        }
    }

    private void BrowseForPath()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = Path.GetFileName(_pathBox.Text),
            InitialDirectory = SafeDirectory(_pathBox.Text),
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _pathBox.Text = dlg.FileName;
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }
        catch { /* malformed text in the box */ }
        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    private void Commit()
    {
        // Gather from the master state, NOT the visible list — a schedule ticked then filtered out of
        // view must still export.
        var chosen = new List<ViewSchedule>();
        for (int i = 0; i < _schedules.Count; i++)
            if (_checkedState[i]) chosen.Add(_schedules[i].Schedule);
        if (chosen.Count == 0)
        {
            Warn("Tick at least one schedule to export.");
            return;
        }
        string path = _pathBox.Text.Trim();
        if (path.Length == 0)
        {
            Warn("Choose where to save the workbook.");
            return;
        }
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";

        List<(string SheetName, int Rows)> summary;
        try
        {
            summary = ScheduleWorkbookExporter.Export(_doc, chosen, path);
        }
        catch (IOException ex)
        {
            Warn("Could not write the workbook — if it is open in Excel, close it first.\n\n" + ex.Message);
            return;
        }
        catch (Exception ex)
        {
            Warn("Export failed:\n\n" + ex.Message);
            return;
        }

        MessageBox.Show(this,
            $"Exported {summary.Count} schedule(s) to:\n{path}\n\n" +
            string.Join("\n", summary.Select(s => $"  {s.SheetName}: {s.Rows} row(s)")) +
            "\n\nEdit the green Instance columns in Excel, save, then run Import Excel.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        // DialogResult.OK already set on the button — the window closes.
    }

    private void Warn(string text)
    {
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        DialogResult = DialogResult.None;
    }
}
