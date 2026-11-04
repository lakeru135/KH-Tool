using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using KhBatchController.Auth;
using KhBatchRunner.Forma;
using KhBatchRunner.UI;

namespace KhBatchController;

/// <summary>
/// The controller's home screen: the add-in's ribbon tab, stacked vertically.
///
/// Panels and their contents mirror <c>App.OnStartup</c> — Setting, Batch Run, Schedule, Multi Model
/// Check, Multi Model Action, Forma Batch Action — so a person who knows the ribbon knows this. The
/// two action panels are BUILT FROM THE LIVE REGISTRY (via the broker, grouped by
/// <c>ActionCategory</c>) rather than hardcoded, so they cannot drift from what the add-in actually
/// offers: a new action appears here as soon as it appears on the ribbon.
///
/// Each action button opens Batch Run with that action pre-ticked, which is what the ribbon buttons
/// do in effect — they are single-action shortcuts over the same engine.
/// </summary>
internal sealed class LauncherWindow : Form
{
    private readonly BrokerTokenProvider _broker;
    private readonly DataManagementClient _dm;
    private readonly ModelDerivativeClient _md;
    private readonly DataConnectorClient _dc;
    private readonly string _revitExe;
    private readonly Panel _canvas;

    private int _y;
    private FlowLayoutPanel? _row;
    private readonly List<FlowLayoutPanel> _rows = new();

    /// <summary>What a stacked element is, so <see cref="Relayout"/> can reproduce the vertical
    /// rhythm the constructor built rather than each control having to remember its own spacing.</summary>
    private enum BlockKind { PanelTitle, Rule, Row, Note }

    /// <summary>Everything on the canvas, in the order it stacks. Rebuilt into position on every
    /// resize — see <see cref="Relayout"/>.</summary>
    private readonly List<(BlockKind Kind, Control Control)> _blocks = new();

    /// <summary>Guards against the relayout that adds a scrollbar re-triggering itself: the
    /// scrollbar takes width from the canvas, which is another resize.</summary>
    private bool _relayouting;
    private readonly IReadOnlyList<BrokerAction>? _actionsOverride;

    /// <param name="actionsOverride">
    /// Supplied instead of asking the broker. Used by the offline render mode, which has no Revit to
    /// ask but still needs the action panels populated so the layout can be checked honestly —
    /// rendering an empty window would prove nothing about the crowded case.
    /// </param>
    public LauncherWindow(BrokerTokenProvider broker, string signedInAs, string revitExe,
                          IReadOnlyList<BrokerAction>? actionsOverride = null)
    {
        _actionsOverride = actionsOverride;
        _broker = broker;
        _revitExe = revitExe;
        _dm = new DataManagementClient(broker);
        _md = new ModelDerivativeClient(broker);
        _dc = new DataConnectorClient(broker);

        // Its OWN version in the title; the engine version lives in About, where there is room to
        // label it. Showing the add-in's version here read as if this were the add-in.
        Text = $"{ControllerProduct.Name} — {ControllerProduct.Version}";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        // Wide enough for six ribbon-style buttons in a row before the flow wraps.
        ClientSize = new Size(700, 740);
        MinimumSize = new Size(480, 420);

        var header = new Label
        {
            Text = "KH Batch Controller",
            Font = new Font(Font.FontFamily, Font.Size + 3F, FontStyle.Bold),
            Location = new Point(16, 12),
            AutoSize = true
        };
        Controls.Add(header);

        var sub = new Label
        {
            Text = $"Signed in as {signedInAs} · Revit {broker.RevitVersion} (pid {broker.BrokerPid}, hidden)",
            Location = new Point(16, 42),
            Size = new Size(520, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(sub);

        // Scrolls, because the panel stack is taller than most screens once every action is listed.
        //
        // Sized FROM ClientSize, never a literal. An anchor preserves the margin a control has when
        // it is added — it does not create one — so a 560-wide canvas dropped into a 700-wide form
        // keeps a 140px dead strip on the right for the life of the window, with the scrollbar
        // stranded in the middle of the form. The render caught exactly that.
        const int canvasTop = 68;
        _canvas = new Panel
        {
            Location = new Point(0, canvasTop),
            Size = new Size(ClientSize.Width, ClientSize.Height - canvasTop),
            AutoScroll = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };
        Controls.Add(_canvas);

        IconPalette glyph = IconPalette.ForCurrentTheme();

        BeginPanel("Setting");
        AddTool("About", Icons.About(glyph), () => new AboutWindow());
        AddTool("Edit Model Set", Icons.EditModelSet(glyph), () => new FormaBrowserWindow(_dm));
        AddTool("Edit DYN Script Set", Icons.EditScriptSet(glyph), () => new EditScriptSetWindow());
        EndPanel();

        BeginPanel("Batch Run");
        AddTool("Batch Run", Icons.DynMultiPlayer(glyph), () => new BatchRunWindow(_broker, _revitExe));
        EndPanel();

        BeginPanel("Schedule");
        // The add-in's own Schedule Run window, unmodified — same dialog, same behaviour.
        AddTool("Schedule Run", Icons.ScheduleRun(glyph), () => new ScheduleRunWindow(_dm));
        EndPanel();

        AddActionPanels(glyph);

        BeginPanel("Forma Batch Action");
        AddTool("Get File Log", Icons.GetFileLog(glyph), () => new FormaExtractWindow(_dm));
        AddTool("Get Data Extract", Icons.GetDataExtract(glyph), () => new DataExtractWindow(_dm, _dc));
        AddTool("Check Sheet Publish", Icons.CheckSheetPublish(glyph), () => new CheckSheetPublishWindow(_dm, _md));
        EndPanel();

        AddNote("Excel Import/Export and Correct Workset stay in Revit — they need the active "
              + "document. Everything else here runs standalone; batches open their own worker Revit.");

        Theme.Apply(this);
        sub.ForeColor = Theme.Dim(sub);

        // Everything above stacked against the width the canvas happened to have while it was being
        // built. Lay it out against the real one, and again whenever that changes.
        _canvas.Resize += (_, _) => Relayout();
        Relayout();
    }

    /// <summary>
    /// The two action panels, grouped by the registry's own <c>ActionCategory</c>. If the broker
    /// cannot be reached the panels are skipped with a visible note rather than rendered empty —
    /// an empty panel would read as "this build has no actions", which would be a lie.
    /// </summary>
    private void AddActionPanels(IconPalette glyph)
    {
        List<BrokerAction> actions;
        try
        {
            actions = _actionsOverride is not null
                ? _actionsOverride.ToList()
                : _broker.GetActions();
        }
        catch (Exception ex)
        {
            ControllerLog.Write("launcher: could not read the action registry: " + ex.Message);
            BeginPanel("Multi Model Check / Action");
            AddNote("Could not read the action list from Revit. Batch Run can still be opened.");
            return;
        }

        if (actions.Count == 0)
        {
            BeginPanel("Multi Model Check / Action");
            AddNote("The broker returned no actions. " + (_broker.LastError ?? ""));
            return;
        }

        AddCategoryPanel("Multi Model Check", actions.Where(a => a.Category == "Check").ToList(), glyph);
        AddCategoryPanel("Multi Model Action", actions.Where(a => a.Category != "Check").ToList(), glyph);
    }

    private void AddCategoryPanel(string title, List<BrokerAction> actions, IconPalette glyph)
    {
        if (actions.Count == 0) return;

        BeginPanel(title);
        foreach (BrokerAction action in actions)
        {
            string name = action.Name;
            AddTool(name, GlyphFor(name, glyph), () => new BatchRunWindow(_broker, _revitExe, name));
        }
        EndPanel();
    }

    /// <summary>
    /// Best-effort match of a registry action to its ribbon glyph by name. A miss falls back to the
    /// generic batch glyph rather than failing — the icon is a nicety, the action list is the truth.
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapSource GlyphFor(string actionName, IconPalette c)
    {
        string n = actionName.ToLowerInvariant();
        if (n.Contains("site location")) return Icons.SiteLocationPbp(c);
        if (n.Contains("phase")) return Icons.PhaseWorkset(c);
        if (n.Contains("design option")) return Icons.GetDesignOption(c);
        if (n.Contains("link workset") || n.Contains("rvt link")) return Icons.GetRvtLinksWorkset(c);
        if (n.Contains("drawing progress")) return Icons.DrawingProgress(c);
        if (n.Contains("type mark")) return Icons.TypeMarkTextNote(c);
        if (n.Contains("purge")) return Icons.PurgeImportCad(c);
        if (n.Contains("isolate")) return Icons.IsolateWarningElements(c);
        if (n.Contains("cut")) return Icons.CutModel(c);
        if (n.Contains("relink")) return Icons.Relink(c);
        if (n.Contains("workset")) return Icons.CorrectWorkset(c);
        if (n.Contains("initiate")) return Icons.InitiateToForma(c);
        return Icons.DynMultiPlayer(c);
    }

    // ---- layout ----------------------------------------------------------------------------

    /// <summary>
    /// Starts a panel: caption, hairline, and a left-to-right flow for its buttons — a ribbon panel,
    /// stacked. Buttons sit in a ROW and wrap only when the window is too narrow, which is what makes
    /// this read as the ribbon rather than as a list of menu items.
    /// </summary>
    private void BeginPanel(string title)
    {
        _y += _y == 0 ? 8 : 16;

        var label = new Label
        {
            Text = title.ToUpperInvariant(),
            Font = new Font(Font.FontFamily, Font.Size - 0.5F, FontStyle.Bold),
            Location = new Point(16, _y),
            AutoSize = true
        };
        _canvas.Controls.Add(label);
        _blocks.Add((BlockKind.PanelTitle, label));
        _y += 20;

        var rule = new Panel
        {
            Location = new Point(16, _y),
            Size = new Size(Math.Max(40, _canvas.ClientSize.Width - 40), 1),
            BackColor = SystemColors.ControlDark
        };
        _canvas.Controls.Add(rule);
        _blocks.Add((BlockKind.Rule, rule));
        _y += 8;

        // AutoSize is OFF on purpose. With AutoSize + WrapContents the panel shrinks to its content
        // and then wraps against that shrunken width, so a 700px window wrapped after two buttons
        // and the next caption landed on top of the previous row.
        //
        // No Anchor either: width and height are both set by Relayout, from the canvas's ACTUAL
        // width. An anchor stretched the row while the height was still being computed from the
        // fixed RowWidth, so a widened window fitted six buttons on one line but still reserved
        // room for two — the empty band between panels.
        _row = new FlowLayoutPanel
        {
            Location = new Point(20, _y),
            Size = new Size(RowWidth, ButtonHeight + 8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = false
        };
        _canvas.Controls.Add(_row);
        _rows.Add(_row);
        _blocks.Add((BlockKind.Row, _row));
    }

    /// <summary>
    /// Positions every stacked block for the canvas's current width: rows are widened, their wrap
    /// is measured from that real width, and everything below shifts by the result. Runs on every
    /// resize, which is the only way a wrapping ribbon can keep its spacing honest.
    /// </summary>
    private void Relayout()
    {
        if (_relayouting || _blocks.Count == 0) return;
        _relayouting = true;
        _canvas.SuspendLayout();
        try
        {
            int width = _canvas.ClientSize.Width;
            int y = 0;
            bool first = true;

            foreach ((BlockKind kind, Control control) in _blocks)
            {
                switch (kind)
                {
                    case BlockKind.PanelTitle:
                        y += first ? 8 : 16;
                        first = false;
                        control.Location = new Point(16, y);
                        y += 20;
                        break;

                    case BlockKind.Rule:
                        control.Location = new Point(16, y);
                        control.Width = Math.Max(40, width - 40);
                        y += 8;
                        break;

                    case BlockKind.Row:
                        var row = (FlowLayoutPanel)control;
                        row.Location = new Point(20, y);
                        row.Width = Math.Max(ButtonWidth + 8, width - 44);
                        int perRow = Math.Max(1, row.ClientSize.Width / (ButtonWidth + 8));
                        int lines = Math.Max(1, (int)Math.Ceiling(row.Controls.Count / (double)perRow));
                        row.Height = (lines * (ButtonHeight + 8)) + 4;
                        y += row.Height + 6;
                        break;

                    case BlockKind.Note:
                        y += 10;
                        control.Location = new Point(16, y);
                        control.Width = Math.Max(40, width - 40);
                        y += control.Height + 4;
                        break;
                }
            }
        }
        finally
        {
            _canvas.ResumeLayout();
            _relayouting = false;
        }
    }

    // 112 wide so the longest caption segment ("Location & PBP") fits on ONE line. At 104 it wrapped
    // to a third line and the first line was drawn over the glyph.
    private const int ButtonWidth = 112;
    // Glyph (32) + THREE caption lines + padding. Two was not enough: "Get Site Location & PBP" and
    // "Isolate Warning Elements" both wrap to three, and the overflow was drawn back over the glyph.
    private const int ButtonHeight = 112;
    // Starting width only — Relayout replaces it with the canvas's real width, which is what the
    // wrap is then measured against.
    private const int RowWidth = 600;

    /// <summary>
    /// A ribbon-shaped button: glyph on top, label beneath, wrapped onto two lines the way the
    /// ribbon captions are ("Get Phase\n&amp; Workset").
    /// </summary>
    private void AddTool(string title, System.Windows.Media.Imaging.BitmapSource glyph, Func<Form> factory)
    {
        // TextImageRelation alone decides the stacking. Setting ImageAlign/TextAlign as well made
        // WinForms place both against the same edge, so captions were drawn over their glyphs.
        var button = new Button
        {
            Text = WrapCaption(title),
            Size = new Size(ButtonWidth, ButtonHeight),
            Margin = new Padding(4),
            TextImageRelation = TextImageRelation.ImageAboveText,
            Image = GlyphImage.From(glyph),
            Padding = new Padding(2, 4, 2, 4),
            // Action names contain "&" ("Get Phase & Workset", "Get Site Location & PBP"). WinForms
            // treats it as a mnemonic prefix and swallows it, so the captions read "Get Phase
            // Workset" — wrong, and inconsistent with the ribbon they mirror.
            UseMnemonic = false
        };
        button.Click += (_, _) => Open(title, factory);
        _row!.Controls.Add(button);
    }

    /// <summary>
    /// Breaks a caption near the middle on a space so it fits the button on two lines instead of
    /// being clipped. Long single words are left alone — clipping one is better than hyphenating
    /// an action name the user has to recognise.
    /// </summary>
    private static string WrapCaption(string title)
    {
        if (title.Length <= 12 || !title.Contains(' ')) return title;

        int middle = title.Length / 2;
        int best = -1;
        for (int i = 0; i < title.Length; i++)
            if (title[i] == ' ' && (best < 0 || Math.Abs(i - middle) < Math.Abs(best - middle)))
                best = i;

        return best < 0 ? title : title.Substring(0, best) + Environment.NewLine + title.Substring(best + 1);
    }

    /// <summary>Closes the current panel, advancing past however tall its wrapped row turned out.</summary>
    private void EndPanel()
    {
        if (_row == null) return;

        // A provisional height so construction can keep stacking; Relayout replaces it with one
        // measured against the canvas's real width. Counted from the button count rather than
        // PreferredSize, because an unshown FlowLayoutPanel has not laid out yet and reports one row.
        int perRow = Math.Max(1, _row.ClientSize.Width / (ButtonWidth + 8));
        int lines = Math.Max(1, (int)Math.Ceiling(_row.Controls.Count / (double)perRow));
        int height = (lines * (ButtonHeight + 8)) + 4;

        _row.Height = height;
        _y += height + 6;
        _row = null;
    }

    private void AddNote(string text)
    {
        EndPanel();
        _y += 10;
        var note = new Label
        {
            Text = text,
            Location = new Point(16, _y),
            Size = new Size(Math.Max(40, _canvas.ClientSize.Width - 40), 52),
            ForeColor = SystemColors.GrayText
        };
        _canvas.Controls.Add(note);
        _blocks.Add((BlockKind.Note, note));
        _y += 56;
    }

    private void Open(string title, Func<Form> factory)
    {
        // A failure inside one tool must not take the launcher — or the broker — down with it.
        try
        {
            ControllerLog.Write($"opening '{title}'");
            using Form dialog = factory();
            dialog.ShowDialog(this);
            ControllerLog.Write($"closed '{title}'");
        }
        catch (Exception ex)
        {
            ControllerLog.Write($"'{title}' FAILED: {ex}");
            MessageBox.Show(this,
                $"{title} could not be opened.\n\n{ex.Message}\n\nSee {ControllerLog.FilePath}",
                "KH Batch Controller", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
