using System.Drawing;
using System.Windows.Forms;

namespace KhBatchRunner.UI;

/// <summary>
/// Small helpers for building resolution/DPI-safe WinForms dialogs out of a docked, single
/// column <see cref="TableLayoutPanel"/> (rows auto-size to their content) plus
/// <see cref="FlowLayoutPanel"/> button rows. Extracted from FormaBrowserWindow so every dialog
/// lays out the same way and controls never overlap or run off the panel edge at high DPI or
/// large fonts (the failure mode the old absolute <c>Location</c>/<c>Size</c> layouts had).
/// </summary>
internal static class FormLayout
{
    /// <summary>
    /// Whether a parallel split is possible for the chosen "When finished" disposition.
    ///
    /// Each group runs in a separate Revit that must quit once its models are done, which
    /// "Leave files open" contradicts — it used to be accepted and then silently overridden.
    /// Shared by all six windows that can start a split so the rule cannot drift between them.
    ///
    /// The SIZE of a group is no longer asked for. It depends on the model count and on the
    /// machine — 98 models wants a different shape on a 32-thread desktop than 32 models does on a
    /// laptop — so it is derived at run time by <see cref="Schedule.InstanceBudget.AutoGroupSize"/>.
    /// A hand-picked 10 was what turned a 98-model run into 10 groups for 8 slots, leaving six
    /// idle through the tail.
    /// </summary>
    public static bool SplitAllowed(string? completionValue) =>
        completionValue != Batch.BatchCompletion.LeaveOpen.ToString();

    /// <inheritdoc cref="SplitAllowed(string?)"/>
    public static bool SplitAllowed(Batch.BatchCompletion? completion) =>
        completion != Batch.BatchCompletion.LeaveOpen;

    /// <summary>Shown beneath the "When finished" picker in place of the old group-size box, so the
    /// split is still visible as a thing that happens rather than becoming invisible magic.</summary>
    public const string SplitNote =
        "Large runs split automatically across several Revit instances, sized to this PC. " +
        "\"Leave files open\" turns that off — a group's Revit has to close itself when it finishes.";

    /// <summary>A docked, padded, single-column table that grows one row at a time.</summary>
    public static TableLayoutPanel SingleColumn()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(8),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    /// <summary>A left-to-right, auto-sizing button/control row that never wraps.</summary>
    public static FlowLayoutPanel FlowRow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 2)
        };
        foreach (Control c in controls) flow.Controls.Add(c);
        return flow;
    }

    /// <summary>Adds an auto-height row hosting <paramref name="c"/> in column 0.</summary>
    public static void AddAutoRow(TableLayoutPanel t, Control c)
    {
        int r = t.RowStyles.Count;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(c, 0, r);
    }

    /// <summary>Adds a proportional-height row whose control fills the cell.</summary>
    public static void AddFillRow(TableLayoutPanel t, Control c, float percent)
    {
        int r = t.RowStyles.Count;
        t.RowStyles.Add(new RowStyle(SizeType.Percent, percent));
        c.Dock = DockStyle.Fill;
        t.Controls.Add(c, 0, r);
    }

    /// <summary>
    /// A titled column for a side-by-side lists area: a heading label on top, a control that
    /// fills the remaining height, and an optional button row beneath.
    /// </summary>
    public static TableLayoutPanel TitledColumn(string title, Control fill, Control? buttons = null)
    {
        var col = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0, 0, 8, 0) };
        col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddAutoRow(col, new Label { Text = title, AutoSize = true, Margin = new Padding(3, 0, 3, 2) });
        AddFillRow(col, fill, 100);
        if (buttons != null) AddAutoRow(col, buttons);
        return col;
    }

    /// <summary>
    /// A word-wrapping helper/note label whose wrap width tracks <paramref name="host"/>'s
    /// client width (AutoSize + a MaximumSize.Width kept in sync via the form's Resize
    /// event) so a line of guidance text wraps cleanly instead of clipping or overlapping
    /// the next row at the dialog's minimum size. Caller can update <c>.Text</c> later
    /// (e.g. when the effective action category changes); wrap width keeps tracking the
    /// form regardless. Theming (color) is the caller's job — <see cref="Theme.Apply"/>
    /// colors it like any other label; dim it afterwards with <see cref="Theme.Dim"/> if
    /// it's meant to read as secondary/help text.
    /// </summary>
    public static Label NoteLabel(Form host, string text)
    {
        // UseMnemonic off: a note is prose, never an access-key host, and a Label eats "&" as a
        // mnemonic marker. The About description read "model  folder sets" because of it, and the
        // product text is full of ampersands ("Get Phase & Workset", "Forma/BIM 360").
        var label = new Label
        {
            Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 4, 3, 4)
        };

        void UpdateWidth()
        {
            int width = host.ClientSize.Width - 32; // rough allowance for the table's own padding
            if (width > 40) label.MaximumSize = new Size(width, 0);
        }
        UpdateWidth();
        host.Resize += (_, _) => UpdateWidth();
        return label;
    }

    /// <summary>
    /// A label + control pair laid out so the field always starts clear of the label
    /// (label column auto-sizes to its text). Optional trailing controls (e.g. a Browse
    /// button) sit after the field. The grid is tagged with its <see cref="Label"/> so
    /// <see cref="AlignLabels"/> can later widen every row's label column to a common width,
    /// left-aligning all the fields in a window.
    /// </summary>
    public static TableLayoutPanel LabeledRow(string label, Control field, params Control[] trailing)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2 + trailing.Length, RowCount = 1, Margin = new Padding(0, 2, 0, 2)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) };
        grid.Controls.Add(lbl, 0, 0);
        field.Dock = DockStyle.Fill;
        grid.Controls.Add(field, 1, 0);
        int col = 2;
        foreach (Control c in trailing)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.Controls.Add(c, col++, 0);
        }
        grid.Tag = lbl; // marks this as a LabeledRow grid + points at its label, for AlignLabels
        return grid;
    }

    /// <summary>
    /// Left-aligns the fields of every <see cref="LabeledRow"/> in <paramref name="form"/> by giving
    /// their label columns one shared width (the widest label + its margin). Runs on the form's first
    /// <c>Shown</c> — when fonts/DPI are final — so the measurement is exact; harmless if the form has
    /// no labeled rows. Fields sit in a Percent-100 column, so widening the label column just shifts
    /// them to a common left edge. Call once from a window's constructor.
    /// </summary>
    /// <summary>
    /// Opens <paramref name="c"/>'s list wide enough to show every item whole (a long "Family :
    /// Type" may still be cut in the closed box). Measured again whenever the font or width
    /// changes: sized once while the items were added, the list was measured in the default font
    /// and opened 18-30 px short in the window's (Revit 2024 layout gate). Call again after
    /// changing the items.
    /// </summary>
    public static void FitList(ComboBox c)
    {
        void Fit() => c.DropDownWidth = Math.Max(Math.Max(1, c.Width), c.Items.Cast<object>()
            .Select(i => TextRenderer.MeasureText(i?.ToString() ?? "", c.Font).Width + SystemInformation.VerticalScrollBarWidth + 8)
            .DefaultIfEmpty(0).Max());
        Fit();
        if (Fitted.TryGetValue(c, out _)) return;
        Fitted.Add(c, new object());
        c.FontChanged += (_, _) => Fit();
        c.SizeChanged += (_, _) => Fit();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, object> Fitted = new();

    public static void AlignLabels(Form form) => form.Shown += (_, _) => Align(form);

    /// <summary>
    /// As <see cref="AlignLabels(Form)"/>, but each of <paramref name="scopes"/> gets its OWN common
    /// width. For a dialog whose rows live in unrelated boxes: one long label on one tab (Enlarge's
    /// "Batch Run takes rooms whose name contains:") would otherwise push every field on every other
    /// tab — the preset combo included — far to the right, and starve a field that shares its row
    /// with a picture.
    /// </summary>
    public static void AlignLabels(Form form, params Control[] scopes) =>
        form.Shown += (_, _) => { foreach (Control scope in scopes) Align(scope); };

    private static void Align(Control scope)
    {
        var rows = new List<(TableLayoutPanel grid, Label lbl)>();
        if (scope is TableLayoutPanel self && self.Tag is Label own) rows.Add((self, own));
        Collect(scope, rows);
        if (rows.Count == 0) return;

        int width = 0;
        foreach ((_, Label lbl) in rows)
            width = Math.Max(width, lbl.GetPreferredSize(Size.Empty).Width + lbl.Margin.Horizontal);

        foreach ((TableLayoutPanel grid, _) in rows)
            grid.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, width);
    }

    private static void Collect(Control parent, List<(TableLayoutPanel, Label)> rows)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is TableLayoutPanel t && t.Tag is Label lbl)
                rows.Add((t, lbl));
            Collect(c, rows);
        }
    }
}
