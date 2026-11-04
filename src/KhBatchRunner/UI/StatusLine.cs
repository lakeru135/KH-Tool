using System.Drawing;
using System.Windows.Forms;

namespace KhBatchRunner.UI;

/// <summary>
/// The one-line status bar at the bottom of every dialog — which now keeps the whole message and
/// can show it.
///
/// <para><b>The fault this replaces.</b> Sixteen windows each built the same
/// <c>Label { Dock = Bottom, Height = 24 }</c> and pushed text into it. A fixed-height,
/// non-AutoSize Label neither grows nor ellipsises: a two-line message renders its second line
/// below the visible strip and a long one is cut mid-word, with no way to select or copy what is
/// left. The messages that suffered most were the ones worth reading — a failed schedule save
/// carries one refusal line per launcher copy, and those lines are the diagnosis. Callers had
/// started defending themselves by squeezing (<c>.Replace('\n', ' ')</c>) or truncating
/// (<c>ex.Message.Split('\n')[0]</c>), which threw the rest away at the source.</para>
///
/// <para><b>What it does instead.</b> The strip still shows one line — that is what a status bar is
/// for — but it keeps the full text. When there is more to see it paints a small badge at its right
/// edge, turns the cursor into a hand and opens <see cref="MessageDetailsWindow"/> on click. A short
/// message looks and behaves exactly as before: no badge, no click target, nothing new to learn.</para>
///
/// <para>"More to see" means the text has line breaks, or is too wide to fit the strip at its
/// current width — measured, not guessed, so the badge does not appear on a message that happens
/// to fit and does appear on the same message in a narrower window.</para>
///
/// <para><b>Why it paints itself instead of being a Label.</b> The first version was a Label, and it
/// shrank to a 100 px box in the bottom-left corner. A non-AutoSize Label resets its Size to the
/// size it was last *asked* for on every text change (<c>Label.AdjustSize</c>) — 100 px wide, since
/// docking never asks. A plain status label survived that because the form re-docked it on the next
/// layout; this one changed its text (badge on/off) from inside <c>Resize</c>, i.e. during the
/// form's own dock layout, where the re-layout it asked for is dropped. A Control that owns its
/// painting has no self-sizing to fight the dock, and nothing here changes Size or Text while a
/// layout is running.</para>
/// </summary>
public sealed class StatusLine : Control
{
    /// <summary>Right-edge badge. A circled-i rather than an ellipsis: the clipped line already
    /// ends in one, and an ellipsis alone does not say "click me".</summary>
    private const string MoreBadge = "ⓘ";

    private const int WS_BORDER = 0x00800000;
    private const int WS_EX_CLIENTEDGE = 0x00000200;

    private const TextFormatFlags LineFlags =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.NoPrefix;

    private BorderStyle _borderStyle = BorderStyle.Fixed3D;
    private bool _hasMore;
    private readonly ToolTip _tip = new();

    public StatusLine()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Dock = DockStyle.Bottom;
        Height = 24;
        Padding = new Padding(8, 0, 8, 0);

        Click += (_, _) => ShowDetails();
        Disposed += (_, _) => _tip.Dispose();
    }

    /// <summary>
    /// Sunken (the default, as the old Label strip looked), single line, or none. Drawn by Windows
    /// through the window style, exactly as a Label draws its own.
    /// </summary>
    public BorderStyle BorderStyle
    {
        get => _borderStyle;
        set
        {
            if (_borderStyle == value) return;
            _borderStyle = value;
            if (IsHandleCreated) UpdateStyles();
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            if (_borderStyle == BorderStyle.Fixed3D) cp.ExStyle |= WS_EX_CLIENTEDGE;
            else if (_borderStyle == BorderStyle.FixedSingle) cp.Style |= WS_BORDER;
            return cp;
        }
    }

    /// <summary>
    /// The title the details window opens with. Set by the host form (its own <c>Text</c>) so the
    /// details of a failed save say which dialog they came from.
    /// </summary>
    public string DetailsTitle { get; set; } = "Details";

    /// <summary>
    /// Shows <paramref name="message"/>, keeping all of it. <see cref="Control.Text"/> holds the
    /// whole message, so setting Text directly (theming, the layout harness) means the same thing.
    /// </summary>
    public void SetMessage(string message)
    {
        Text = message ?? "";
        Refresh();
    }

    /// <summary>The full text of the last message, for a caller that wants to log it too.</summary>
    public string FullMessage => Text;

    /// <summary>Opens the details window when there is more than the strip is showing.</summary>
    public void ShowDetails()
    {
        if (!_hasMore) return;
        using var window = new MessageDetailsWindow(DetailsTitle, Text);
        window.ShowDialog(FindForm());
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Decide();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Decide();   // badge + cursor only — never Text or Size, see the class remarks
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        // Room for one line of this font inside the border, at any DPI.
        Height = Math.Max(24, Font.Height + 8);
        Decide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle area = TextArea();
        if (area.Width <= 0) return;

        if (_hasMore)
        {
            int badge = TextRenderer.MeasureText(e.Graphics, MoreBadge, Font, Size.Empty,
                LineFlags | TextFormatFlags.NoPadding).Width;
            var badgeRect = new Rectangle(area.Right - badge, area.Top, badge, area.Height);
            TextRenderer.DrawText(e.Graphics, MoreBadge, Font, badgeRect, ForeColor,
                LineFlags | TextFormatFlags.NoPadding);
            area.Width = Math.Max(0, area.Width - badge - 6);
        }
        TextRenderer.DrawText(e.Graphics, FirstLine(Text), Font, area, ForeColor,
            LineFlags | TextFormatFlags.EndEllipsis);
    }

    private Rectangle TextArea() => new(
        Padding.Left, 0, ClientSize.Width - Padding.Horizontal, ClientSize.Height);

    /// <summary>Is there more than one line, or a line wider than the strip? Sets the badge, the
    /// hand cursor and the tooltip to match.</summary>
    private void Decide()
    {
        string full = (Text ?? "").Trim();
        string line = FirstLine(full);
        bool hasMore = line.Length != full.Length || TooWide(line);
        if (hasMore == _hasMore) return;

        _hasMore = hasMore;
        Cursor = hasMore ? Cursors.Hand : Cursors.Default;
        _tip.SetToolTip(this, hasMore ? "Click to read the whole message" : null);
        Invalidate();
    }

    private static string FirstLine(string s)
    {
        string trimmed = (s ?? "").Trim();
        int br = trimmed.IndexOfAny(new[] { '\r', '\n' });
        return br < 0 ? trimmed : trimmed.Substring(0, br).TrimEnd();
    }

    /// <summary>Would this line be clipped at the strip's current width?</summary>
    private bool TooWide(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        int usable = TextArea().Width;
        if (usable <= 0) return false; // not laid out yet — decide on the next Resize
        try
        {
            return TextRenderer.MeasureText(line, Font, Size.Empty, LineFlags).Width > usable;
        }
        catch
        {
            // Measuring needs a device context; if one is unavailable (a window being built
            // off-screen by the render harness) a missing badge is the harmless answer.
            return false;
        }
    }
}
