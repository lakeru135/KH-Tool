using System.Text;
using System.Windows.Forms;

namespace KhBatchRunner.UI;

/// <summary>
/// Shows the result of running a single action on the active model (the "Run on active model"
/// mode) — nothing is written to disk in that mode, so this grid is the only place the
/// extracted rows are surfaced. One column per the action's declared
/// <see cref="Actions.IBatchAction.Columns"/>, one row per detail row. When the action wrote
/// to the model, a warning banner reminds the user the changes are unsaved. Built in code
/// (no designer file), themed via <see cref="Theme.Apply"/> like every other dialog.
/// </summary>
public sealed class ActionResultsWindow : Form
{
    private readonly IReadOnlyList<string> _columns;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _rows;

    public ActionResultsWindow(
        string actionName, string modelName,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        string perModelNote, bool modelModified,
        Func<IReadOnlyDictionary<string, string>, string?>? openRow = null)
    {
        _columns = columns;
        _rows = rows;

        Text = $"Result: {actionName}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(820, 500);
        MinimumSize = new System.Drawing.Size(480, 300);

        // --- Header: action + model, then an item count / note sub-line ---
        // UseMnemonic off: half the action names contain "&" ("Get Phase & Workset", "Get Site
        // Location & PBP"), and a Label treats it as an access-key marker — the heading rendered as
        // "Get Phase  Workset" with the ampersand swallowed. The title bar is unaffected, so the
        // window disagreed with its own heading.
        var header = new Label
        {
            Text = $"{actionName} — {modelName}",
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(3, 2, 3, 0)
        };
        // Explicit Segoe UI base (not derived from the ambient font): Theme.Apply pins the form
        // to Segoe UI 9, but on net48 the label's default is Microsoft Sans Serif — deriving bold
        // from that would leave the header in a different face than the rest of the dialog.
        header.Font = new System.Drawing.Font("Segoe UI", 9.75f, System.Drawing.FontStyle.Bold);

        string sub = $"{rows.Count} item{(rows.Count == 1 ? "" : "s")}";
        if (!string.IsNullOrEmpty(perModelNote)) sub += $"  ·  {perModelNote}";
        if (openRow != null && rows.Count > 0) sub += "  ·  Double-click a row to open its view.";
        var subLabel = new Label { Text = sub, AutoSize = true, Margin = new Padding(3, 0, 3, 2) };

        // --- Modified-model warning (write actions only) ---
        Label? warning = modelModified
            ? FormLayout.NoteLabel(this,
                "Changes were made to the open model but NOT saved or synced. Review them, then " +
                "Save/Sync — or press Ctrl+Z in Revit to undo.")
            : null;

        // --- Grid (or an empty-state label) ---
        Control middle;
        if (rows.Count == 0)
        {
            middle = new Label
            {
                Text = "No items found.",
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter
            };
        }
        else
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            foreach (string col in columns)
            {
                int idx = grid.Columns.Add(col, col);
                grid.Columns[idx].MinimumWidth = 80;
                grid.Columns[idx].SortMode = DataGridViewColumnSortMode.Automatic;
            }
            foreach (IReadOnlyDictionary<string, string> row in rows)
            {
                object[] cells = columns
                    .Select(c => (object)(row.TryGetValue(c, out string? v) ? v : ""))
                    .ToArray();
                grid.Rows.Add(cells);
            }
            if (openRow != null)
            {
                // Read the row back from the GRID, by column name, so a sorted grid opens the row
                // that was clicked rather than the one that used to be at that index.
                grid.CellDoubleClick += (_, e) =>
                {
                    if (e.RowIndex < 0) return;
                    DataGridViewRow r = grid.Rows[e.RowIndex];
                    var row = columns.Select((c, i) => (c, v: r.Cells[i].Value?.ToString() ?? ""))
                        .ToDictionary(x => x.c, x => x.v);
                    string? problem = openRow(row);
                    if (problem != null) MessageBox.Show(this, problem, Text);
                };
            }
            middle = grid;
        }

        // --- Buttons ---
        var copyButton = new Button { Text = "Copy", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        copyButton.Click += (_, _) => CopyToClipboard();
        copyButton.Enabled = rows.Count > 0;
        var closeButton = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, header);
        FormLayout.AddAutoRow(root, subLabel);
        if (warning != null) FormLayout.AddAutoRow(root, warning);
        FormLayout.AddFillRow(root, middle, 100);
        var buttons = FormLayout.FlowRow(copyButton, closeButton);
        buttons.Anchor = AnchorStyles.Right;
        FormLayout.AddAutoRow(root, buttons);

        Controls.Add(root);
        AcceptButton = closeButton;
        CancelButton = closeButton;

        Theme.Apply(this);
        subLabel.ForeColor = Theme.Dim(subLabel);
        if (warning != null) warning.ForeColor = Theme.Dim(warning);
    }

    /// <summary>Copies header + rows as tab-separated text (pastes straight into Excel).</summary>
    private void CopyToClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", _columns));
        foreach (IReadOnlyDictionary<string, string> row in _rows)
            sb.AppendLine(string.Join("\t", _columns.Select(c => row.TryGetValue(c, out string? v) ? v : "")));
        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard busy — ignore */ }
    }
}
