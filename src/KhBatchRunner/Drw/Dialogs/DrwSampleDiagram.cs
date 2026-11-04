using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using KhBatchRunner.UI;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>
/// What every DRW "Sample …" picture shares, in the visual language of Revit's View Range sample:
/// red dashed reference lines, translucent blue / orange / green range bands with arrows, circled
/// numbers on both, and a two-column key underneath that carries the real values.
///
/// <para>A plain owner-drawn <see cref="Control"/> for the same reason as <see cref="StatusLine"/>:
/// nothing here sizes itself, so nothing can fight the layout it sits in. Pictures are schematic —
/// a subclass clamps each value so its mark stays visible; the key says the real number.</para>
/// </summary>
internal abstract class DrwSampleDiagram : Control
{
    protected static readonly Color LineColor = Color.FromArgb(48, 141, 200);
    protected static readonly Color Red = Color.FromArgb(220, 70, 70);
    protected static readonly Color Blue = Color.FromArgb(115, 89, 171, 230);
    protected static readonly Color Orange = Color.FromArgb(135, 245, 166, 90);
    protected static readonly Color Green = Color.FromArgb(135, 140, 200, 120);

    protected const int KeyRows = 4, KeyRowHeight = 16, CaptionHeight = 30, Bubble = 14;

    protected readonly Font Small = new("Segoe UI", 7.5f);

    protected DrwSampleDiagram()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Size = new Size(270, 300);
    }

    /// <summary>"room" or "stair", for the labels.</summary>
    public string TargetWord { get; set; } = "room";

    protected string TargetTitle => char.ToUpperInvariant(TargetWord[0]) + TargetWord.Substring(1);

    /// <summary>Where the picture itself goes: above the key and the caption, with gutters for the
    /// bands (left) and the numbers (right).</summary>
    protected Rectangle DrawingArea => Rectangle.FromLTRB(46, 8, ClientSize.Width - 42, KeyTop - 8);

    protected int KeyTop => ClientSize.Height - KeyRows * KeyRowHeight - CaptionHeight;

    protected override void Dispose(bool disposing)
    {
        if (disposing) Small.Dispose();
        base.Dispose(disposing);
    }

    protected static RectangleF RectF(float x1, float y1, float x2, float y2) =>
        RectangleF.FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    /// <summary>A circled number, View Range style.</summary>
    protected void BubbleAt(Graphics g, int n, float cx, float cy, Color ring)
    {
        var r = new RectangleF(cx - Bubble / 2f, cy - Bubble / 2f, Bubble, Bubble);
        using (var back = new SolidBrush(BackColor)) g.FillEllipse(back, r);
        using (var pen = new Pen(ring, 1.2f)) g.DrawEllipse(pen, r);
        TextRenderer.DrawText(g, n.ToString(), Small, Rectangle.Round(r), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    /// <summary>Circled numbers down the right-hand gutter, nudged into a second column where two
    /// lines are closer together than a bubble.</summary>
    protected void Bubbles(Graphics g, IEnumerable<(int N, float Y, Color Ring)> marks)
    {
        Rectangle area = DrawingArea;
        float col1 = area.Right + 10, col2 = area.Right + 27, lastY = float.NaN;
        foreach ((int n, float y, Color ring) in marks.OrderBy(m => m.Y))
        {
            bool crowded = !float.IsNaN(lastY) && y - lastY < Bubble + 1;
            BubbleAt(g, n, crowded ? col2 : col1, y, ring);
            lastY = crowded ? float.NaN : y;
        }
    }

    /// <summary>A translucent range bar with an arrow at each end and its number in the middle.</summary>
    protected void Band(Graphics g, int n, float x, float yTop, float yBottom, Color color)
    {
        if (yBottom - yTop < 2) return;
        using (var pen = new Pen(Color.FromArgb(230, color), 4f))
        {
            pen.StartCap = LineCap.ArrowAnchor;
            pen.EndCap = LineCap.ArrowAnchor;
            g.DrawLine(pen, x, yTop + 2, x, yBottom - 2);
        }
        BubbleAt(g, n, x, (yTop + yBottom) / 2, Color.FromArgb(255, color));
    }

    /// <summary>A red dashed reference line across the drawing.</summary>
    protected static void RefLine(Graphics g, float x1, float x2, float y)
    {
        using var red = new Pen(Red, 1f) { DashStyle = DashStyle.Dash };
        g.DrawLine(red, x1, y, x2, y);
    }

    /// <summary>The key (two columns, numbers matching the picture; what does not apply is dimmed)
    /// and the caption under it.</summary>
    protected void KeyAndCaption(Graphics g, IReadOnlyList<(int N, string Text, bool Applies)> key, string caption)
    {
        Color dim = Theme.Dim(this);
        int colWidth = ClientSize.Width / 2;
        for (int i = 0; i < key.Count; i++)
        {
            int x = (i / KeyRows) * colWidth + 4, y = KeyTop + (i % KeyRows) * KeyRowHeight;
            Color c = key[i].Applies ? ForeColor : Theme.Dim(dim, BackColor);
            TextRenderer.DrawText(g, $"{key[i].N}  {key[i].Text}", Small,
                new Rectangle(x, y, colWidth - 6, KeyRowHeight), c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        TextRenderer.DrawText(g, caption, Small,
            new Rectangle(4, ClientSize.Height - CaptionHeight + 2, ClientSize.Width - 8, CaptionHeight - 2), dim,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}
