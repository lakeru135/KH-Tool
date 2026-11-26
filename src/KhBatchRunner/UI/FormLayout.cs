using System.Windows.Forms;

namespace KhBatchRunner.UI;

/// <summary>
/// Marker type for help/secondary prose built by <see cref="FormLayout.NoteLabel"/>.
/// <see cref="Theme.Apply"/> always dims it, so a window cannot forget to (E09).
/// </summary>
internal sealed class HelpNote : Label { }

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

    /// <summary>
    /// Sizes <paramref name="host"/> to fit <paramref name="root"/> now, and keeps it fitted: the
    /// dialog (and its minimum size) GROWS whenever the content needs more room than it has. Returns
    /// the refit, for a caller whose content changes in ways no event here can see.
    ///
    /// <para><b>Why one measurement at construction is not enough.</b> The windows used to set
    /// <c>ClientSize</c> from <c>root.PreferredSize</c> once, in the constructor. Anything that grew
    /// the content afterwards — a restored saved set, a note that changes with a dropdown, Windows'
    /// DPI scaling at Load, <see cref="AlignLabels(Form)"/> widening the label column at Shown — had
    /// no room, and FormLayout's table does not scroll: the fill row (the model-set list) was
    /// squeezed and drawn over the rows below it. That is how 2.1.0.8's Export NWC shipped its list
    /// on top of "Export CSV log to:" in Revit, while looking right in the preview.</para>
    ///
    /// <para>Grow-only, so a user's own resize is never undone and a refit cannot loop.</para>
    /// </summary>
    /// <param name="beneath">A control docked below <paramref name="root"/> (a status strip) whose
    /// height the client area must also hold.</param>
    public static Action KeepFitted(Form host, Control root, Control? beneath = null)
    {
        void Fit()
        {
            if (host.IsDisposed) return;
            // Inside-out first. Windows' DPI autoscale grows the controls (a combo goes from 28 to 33
            // px at 125 %) while layout is suspended, and the table rows around them keep the
            // heights they measured before — the combo then hangs out of its row. Re-laying out
            // each container after its children makes every row read its children again.
            RelayoutInsideOut(root);
            // A table caches its preferred size per proposed width, so a relayout at the same width
            // reads the stale row heights back. One pixel wider and back forces a fresh measure
            // (measured: at 125 % the rows only corrected once the window had been resized).
            Size was = host.ClientSize;
            host.ClientSize = new Size(was.Width + 1, was.Height);
            host.ClientSize = was;
            Size need = root.PreferredSize;
            // A table's preferred height does not make sure each PERCENT row gets the floor of what it
            // holds (a list's MinimumSize, its heading and a button row that wraps at this width), so a
            // window sized to it let the list sit on its own buttons at 125-200 % (E20).
            // Only once shown: before that every control reports Visible = false (Visible is the
            // EFFECTIVE visibility), so hidden rows and shown rows cannot be told apart.
            if (root is TableLayoutPanel { Visible: true } table) need.Height = Math.Max(need.Height, RequiredHeight(table));
            if (beneath is { Visible: true }) need.Height += beneath.Height;
            Size chrome = host.Size - host.ClientSize;
            // Never past the screen's work area: at 200 % on a 1080p screen a tall need would put the
            // buttons under the taskbar and a raised MinimumSize would stop the user shrinking it back.
            Rectangle work = Screen.FromControl(host).WorkingArea;
            need = new Size(Math.Min(need.Width, work.Width - chrome.Width), Math.Min(need.Height, work.Height - chrome.Height));
            var client = new Size(Math.Max(host.ClientSize.Width, need.Width),
                                  Math.Max(host.ClientSize.Height, need.Height));
            if (client != host.ClientSize) host.ClientSize = client;
            var min = new Size(Math.Max(host.MinimumSize.Width, Math.Min(host.Width, need.Width + chrome.Width)),
                               Math.Max(host.MinimumSize.Height, need.Height + chrome.Height));
            if (min != host.MinimumSize) host.MinimumSize = min;
            if (host.Visible)
            {
                if (host.Bottom > work.Bottom) host.Top = Math.Max(work.Top, work.Bottom - host.Height);
                if (host.Right > work.Right) host.Left = Math.Max(work.Left, work.Right - host.Width);
            }
        }

        // Deferred when the window exists: a refit from inside a layout pass would re-enter it.
        void FitSoon()
        {
            if (host.IsHandleCreated && !host.IsDisposed) host.BeginInvoke((Action)Fit);
            else Fit();
        }

        Fit();
        host.Load += (_, _) => Fit();          // after Windows' DPI autoscale
        host.Shown += (_, _) => FitSoon();     // after AlignLabels widens the label column
        host.FontChanged += (_, _) => FitSoon();
        host.DpiChanged += (_, _) => FitSoon(); // dragged to a monitor with another scale
        // A tab page's controls only count while it is the one showing (see TabsFloor), so a tab
        // switch can ask for more room.
        foreach (TabControl tabs in AllControls(root).OfType<TabControl>())
            tabs.SelectedIndexChanged += (_, _) => FitSoon();
        return FitSoon;
    }

    /// <summary>
    /// The height <paramref name="t"/> needs so that no percent row is squeezed below the floor of
    /// what it holds. Auto rows count at their visible controls' preferred height at the width those
    /// controls have now (so a button row that wraps counts wrapped), absolute rows at their style.
    /// The leftover is shared out by percentage, so the table needs enough leftover for the row whose
    /// floor is largest against its share.
    ///
    /// <para>Measured from the controls, not read from <c>GetRowHeights()</c>: in a constructor the
    /// table has not laid out since rows were hidden, and the stale heights (four hidden 38 px rows)
    /// grew Batch Run 70 px and Schedule Run 300 px taller than they needed, for good — the fit is grow-only.</para>
    /// </summary>
    internal static int RequiredHeight(TableLayoutPanel t)
    {
        int rows = Math.Max(t.RowStyles.Count, t.RowCount);
        foreach (Control child in t.Controls) rows = Math.Max(rows, t.GetRow(child) + 1);

        float total = 0;
        for (int r = 0; r < rows; r++)
            if (RowStyleOf(t, r) is { SizeType: SizeType.Percent } s) total += s.Height;

        int fixedRows = 0;
        float leftover = 0;
        for (int r = 0; r < rows; r++)
        {
            RowStyle s = RowStyleOf(t, r);
            if (s.SizeType == SizeType.Absolute)
            {
                fixedRows += (int)s.Height;
                continue;
            }
            bool percent = s.SizeType == SizeType.Percent && total > 0 && s.Height > 0;
            int height = 0;
            foreach (Control child in t.Controls)
            {
                if (!child.Visible || t.GetRow(child) != r) continue;
                int h = percent ? FloorOf(child)
                                : child.GetPreferredSize(new Size(child.Width > 0 ? child.Width : t.ClientSize.Width, 0)).Height;
                height = Math.Max(height, h + child.Margin.Vertical);
            }
            if (percent) leftover = Math.Max(leftover, height * total / s.Height);
            else fixedRows += height;
        }
        return t.Padding.Vertical + fixedRows + (int)Math.Ceiling(leftover);
    }

    private static RowStyle RowStyleOf(TableLayoutPanel t, int row) =>
        row < t.RowStyles.Count ? t.RowStyles[row] : new RowStyle(SizeType.AutoSize);

    /// <summary>The least height <paramref name="c"/> can be given without drawing over its
    /// neighbours: its MinimumSize, or for a nested table, what that table needs.</summary>
    private static int FloorOf(Control c) => c switch
    {
        TableLayoutPanel { Dock: DockStyle.Fill } t => Math.Max(c.MinimumSize.Height, RequiredHeight(t)),
        TabControl tabs => Math.Max(c.MinimumSize.Height, TabsFloor(tabs)),
        _ => c.MinimumSize.Height
    };

    /// <summary>A tab control needs its tab strip plus the showing page's floor (the others report
    /// every control invisible). A page that scrolls (an AutoScroll panel) has no floor; a page laid
    /// out as a table does.</summary>
    private static int TabsFloor(TabControl tabs)
    {
        int strip = tabs.Height - tabs.DisplayRectangle.Height;
        int page = 0;
        if (tabs.SelectedTab is TabPage p)
            foreach (Control child in p.Controls)
                if (child.Dock == DockStyle.Fill && child is not ScrollableControl { AutoScroll: true })
                    page = Math.Max(page, FloorOf(child) + p.Padding.Vertical);
        return strip + page;
    }

    private static IEnumerable<Control> AllControls(Control c)
    {
        foreach (Control child in c.Controls)
        {
            yield return child;
            foreach (Control d in AllControls(child)) yield return d;
        }
    }

    private static void RelayoutInsideOut(Control c)
    {
        foreach (Control child in c.Controls)
            if (child.Controls.Count > 0) RelayoutInsideOut(child);
        c.PerformLayout(c, "Bounds");
    }

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
        var label = new HelpNote
        {
            Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 4, 3, 4)
        };

        // Wrap against the room the label ACTUALLY has: its nearest fixed-size ancestor's client
        // width, less the padding/margins between. The form's width is wrong for a note in a
        // SplitContainer panel or tab page (it wraps for a width the panel does not have and clips
        // at the right edge when the splitter moves). An AutoSize ancestor is skipped — its width
        // follows the label, so measuring it would be circular.
        Control? watched = null;
        void OnParentResized(object? s, EventArgs e) => UpdateWidth();
        void UpdateWidth()
        {
            int width = host.ClientSize.Width - 32; // fallback until the label has a parent
            Control? p = label.Parent;
            int allowance = label.Margin.Horizontal;
            while (p != null && p is not Form && p.AutoSize)
            {
                allowance += p.Padding.Horizontal + p.Margin.Horizontal;
                p = p.Parent;
            }
            if (p != null && p.ClientSize.Width > 0)
            {
                width = p.ClientSize.Width - p.Padding.Horizontal - allowance;
                if (p != watched)
                {
                    if (watched != null) watched.ClientSizeChanged -= OnParentResized;
                    watched = p;
                    watched.ClientSizeChanged += OnParentResized;
                }
            }
            if (width > 40) label.MaximumSize = new Size(width, 0);
        }

        UpdateWidth();
        label.ParentChanged += (_, _) => UpdateWidth();
        host.Resize += (_, _) => UpdateWidth();
        host.Load += (_, _) => UpdateWidth();   // ancestors may have been wired up after construction
        return label;
    }

    /// <summary>
    /// A label + control pair laid out so the field always starts clear of the label
    /// (label column auto-sizes to its text). Optional trailing controls (e.g. a Browse
    /// button) sit after the field. The grid is tagged with its <see cref="Label"/> so
    /// <see cref="AlignLabels"/> can later widen every row's label column to a common width,
    /// left-aligning all the fields in a window.
    /// </summary>
    public static TableLayoutPanel LabeledRow(string label, Control field, params Control[] trailing) =>
        LabeledRow(label, field, aligned: true, trailing);

    /// <summary>
    /// As above; <paramref name="aligned"/> false leaves the row OUT of <see cref="AlignLabels(Form)"/>,
    /// so its field sits right beside its own label instead of being pushed out to the widest label
    /// in the scope (Enlarge's "Filter:" sat 300 px from its box, E15).
    /// </summary>
    public static TableLayoutPanel LabeledRow(string label, Control field, bool aligned, params Control[] trailing)
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
        if (aligned) grid.Tag = lbl; // marks this as a LabeledRow grid + points at its label, for AlignLabels
        return grid;
    }

    /// <summary>
    /// For a window split into a tree on the left and a column of rows on the right
    /// (<paramref name="right"/>, inside <c>split.Panel2</c>). The splitter used to be set to a flat
    /// 380 px and the window to a flat size: scaled up, the right column needed more than its pane
    /// had, so the saved-set combo was squeezed to nothing under its buttons and rows drew over each
    /// other (E20, 125 %-200 %). This sets the splitter so the right column gets what it needs, and
    /// GROWS the window (and its minimum size) when the screen allows. Grow-only, so a user's own
    /// resize is never undone. Call from Load; it re-runs once the window is shown.
    /// </summary>
    /// <param name="beneath">The status strip docked under the split, whose height the client also needs.</param>
    public static void FitSplit(Form host, SplitContainer split, Control right, Control? beneath = null,
        int leftWidth = 380, int leftMinWidth = 200)
    {
        void Fit()
        {
            if (host.IsDisposed || host.WindowState != FormWindowState.Normal) return;
            float k = host.CurrentAutoScaleDimensions.Width / 7F;   // 1.0 at 100 %, more at 125 %+
            Size need = right.GetPreferredSize(Size.Empty);
            // A wrapping note's preferred width is its MaximumSize, which tracks THIS pane's width,
            // so it would make the pane need exactly what it has now and the splitter never move.
            // The width the pane needs is that of its widest NON-wrapping row.
            int widest = 0;
            foreach (Control c in right.Controls)
                if (c.Visible && !(c is Label { MaximumSize.Width: > 0 }))
                    widest = Math.Max(widest, c.GetPreferredSize(Size.Empty).Width + c.Margin.Horizontal);
            need.Width = widest + right.Padding.Horizontal;
            Rectangle work = Screen.FromControl(host).WorkingArea;
            Size chrome = host.Size - host.ClientSize;

            int minClientW = (int)(leftMinWidth * k) + split.SplitterWidth + need.Width;
            int minClientH = need.Height + (beneath is { Visible: true } ? beneath.Height : 0);
            var min = new Size(Math.Max(host.MinimumSize.Width, Math.Min(minClientW + chrome.Width, work.Width)),
                               Math.Max(host.MinimumSize.Height, Math.Min(minClientH + chrome.Height, work.Height)));
            if (min != host.MinimumSize) host.MinimumSize = min;
            var client = new Size(Math.Max(host.ClientSize.Width, min.Width - chrome.Width),
                                  Math.Max(host.ClientSize.Height, min.Height - chrome.Height));
            if (client != host.ClientSize) host.ClientSize = client;

            try
            {
                int room = split.Width - split.SplitterWidth - need.Width;
                split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min((int)(leftWidth * k), room));
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException) { /* clamped by the pane minimums */ }
            if (host.Bottom > work.Bottom) host.Top = Math.Max(work.Top, work.Bottom - host.Height);
            if (host.Right > work.Right) host.Left = Math.Max(work.Left, work.Right - host.Width);
        }

        Fit();
        host.Shown += (_, _) => host.BeginInvoke((Action)Fit);   // after AlignLabels widens the label column
    }

    /// <summary>
    /// Keeps a window wide enough for a table whose columns are PERCENTAGES of its width. A
    /// percent column gets its share however little its content needs, so a window sized once
    /// (880 px) starves a column (a checkbox "Hide sets this account cannot open" cut off, a
    /// column heading clipped) when the fonts grow: the table's own preferred width adds the
    /// columns' needs up and never notices one column is short. This asks, per percent column,
    /// "how wide would the whole table have to be for this column to hold its widest child?" and
    /// grows the window (and its minimum width) by the largest answer. Grow-only; runs again once
    /// shown, after label alignment and DPI scaling.
    /// </summary>
    public static void FitPercentColumns(Form host, TableLayoutPanel table)
    {
        void Fit()
        {
            if (host.IsDisposed || host.WindowState != FormWindowState.Normal) return;
            int tableWidth = RequiredWidth(table);
            int grow = tableWidth - table.Width;
            if (grow <= 0) return;
            Rectangle work = Screen.FromControl(host).WorkingArea;
            Size chrome = host.Size - host.ClientSize;
            int clientW = Math.Min(host.ClientSize.Width + grow, work.Width - chrome.Width);
            host.ClientSize = new Size(clientW, host.ClientSize.Height);
            host.MinimumSize = new Size(Math.Max(host.MinimumSize.Width, Math.Min(host.Width, work.Width)), host.MinimumSize.Height);
            if (host.Right > work.Right) host.Left = Math.Max(work.Left, work.Right - host.Width);
        }

        host.Shown += (_, _) => host.BeginInvoke((Action)Fit);
        host.DpiChanged += (_, _) => host.BeginInvoke((Action)Fit);
    }

    /// <summary>The width <paramref name="c"/> needs so nothing in it is cut off. A table made only of
    /// percent columns needs its widest column's content divided by that column's share; a wrapping
    /// flow row needs only its widest child; a wrapping note needs nothing (it wraps).</summary>
    private static int RequiredWidth(Control c)
    {
        if (c is TableLayoutPanel t)
        {
            float total = t.ColumnStyles.Cast<ColumnStyle>().Where(s => s.SizeType == SizeType.Percent).Sum(s => s.Width);
            bool onlyPercent = total > 0 && t.ColumnStyles.Cast<ColumnStyle>().All(s => s.SizeType == SizeType.Percent);
            if (!onlyPercent) return t.GetPreferredSize(Size.Empty).Width;
            int need = 0;
            for (int col = 0; col < t.ColumnStyles.Count; col++)
            {
                float share = t.ColumnStyles[col].Width;
                if (share <= 0) continue;
                int widest = 0;
                foreach (Control child in t.Controls)
                    if (child.Visible && t.GetColumn(child) == col)
                        widest = Math.Max(widest, RequiredWidth(child) + child.Margin.Horizontal);
                need = Math.Max(need, (int)Math.Ceiling(widest * total / share));
            }
            return need + t.Padding.Horizontal;
        }
        if (c is FlowLayoutPanel { WrapContents: true } flow)
            return flow.Controls.Cast<Control>().Where(k => k.Visible)
                       .Select(k => k.GetPreferredSize(Size.Empty).Width + k.Margin.Horizontal)
                       .DefaultIfEmpty(0).Max() + flow.Padding.Horizontal;
        if (c is Label { AutoSize: true, MaximumSize.Width: > 0 }) return 0;
        // A list or tree scrolls; its "preferred" width is its longest item and says nothing about need.
        if (c is ListControl or TreeView or DataGridView or TextBoxBase) return 0;
        return c.GetPreferredSize(Size.Empty).Width;
    }

    /// <summary>
    /// As <see cref="KeepFitted"/>, for a window whose one percent-sized row holds a LIST of many
    /// items (<paramref name="fill"/>). A table's preferred height counts that list at the height of
    /// every item, so asking it what the window needs gives a window taller than the screen; this
    /// counts the list at its MinimumSize instead, and leaves the user the rest of the height to
    /// drag. Grow-only, never beyond the screen's work area.
    /// </summary>
    public static void FitAroundList(Form host, Control root, Control fill)
    {
        void Fit()
        {
            if (host.IsDisposed || host.WindowState != FormWindowState.Normal) return;
            Rectangle work = Screen.FromControl(host).WorkingArea;
            Size chrome = host.Size - host.ClientSize;
            int need = 0;   // summed row by row below, once the narrowest width is known
            // Width: the widest row that cannot wrap (a wrapping note would only report the width it has).
            int widest = 0;
            foreach (Control c in root.Controls)
                if (c.Visible && !(c is Label { MaximumSize.Width: > 0 }))
                    widest = Math.Max(widest, RequiredWidth(c) + c.Margin.Horizontal);
            int needW = widest + root.Padding.Horizontal;
            int minW = Math.Min(Math.Max(host.MinimumSize.Width, needW + chrome.Width), work.Width);
            // Height at that narrowest width, row by row: a table's own preferred height does not
            // count its percent rows the way they are laid out, and its notes wrap to more lines
            // when the window is narrow. The list row counts at its floor.
            int atMin = minW - chrome.Width - root.Padding.Horizontal;
            need = root.Padding.Vertical;
            foreach (Control c in root.Controls)
            {
                if (!c.Visible) continue;
                need += c == fill ? Math.Max(fill.MinimumSize.Height, 48) + fill.Margin.Vertical
                                  : c.GetPreferredSize(new Size(atMin - c.Margin.Horizontal, 0)).Height + c.Margin.Vertical;
            }
            int minH = Math.Min(Math.Max(host.MinimumSize.Height, need + chrome.Height), work.Height);
            if (minW != host.MinimumSize.Width || minH != host.MinimumSize.Height)
                host.MinimumSize = new Size(minW, minH);
            host.ClientSize = new Size(Math.Max(host.ClientSize.Width, minW - chrome.Width),
                                       Math.Max(host.ClientSize.Height, minH - chrome.Height));
            if (host.Right > work.Right) host.Left = Math.Max(work.Left, work.Right - host.Width);
            if (host.Bottom > work.Bottom) host.Top = Math.Max(work.Top, work.Bottom - host.Height);
        }

        host.Shown += (_, _) => host.BeginInvoke((Action)Fit);
        host.DpiChanged += (_, _) => host.BeginInvoke((Action)Fit);
    }

    /// <summary>The width, at 96 dpi, of a field holding one short value (E15).</summary>
    public const int ShortFieldWidth = 80;

    /// <summary>
    /// Wraps a short numeric input (a NumericUpDown, a one-digit box) for <see cref="LabeledRow"/>,
    /// which otherwise docks its field to the whole row — so "Faceting factor: 1" became a box
    /// 700 px wide. The field keeps <see cref="ShortFieldWidth"/> and sits at the left of the row.
    /// The width is set before the form's autoscale runs, so AutoScaleMode.Font grows it with the
    /// font/DPI like every other fixed-width control here. Pass the result to LabeledRow; keep using
    /// the original control for reading and writing the value.
    /// </summary>
    /// <param name="beside">Controls that belong right after the field (a "= 3.5 m real" label), so
    /// they stay next to it instead of drifting to the far edge of the row.</param>
    public static Control ShortField(Control field, params Control[] beside)
    {
        field.Width = ShortFieldWidth;
        field.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        var row = FlowRow(new[] { field }.Concat(beside).ToArray());
        row.Margin = new Padding(0);
        return row;
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
