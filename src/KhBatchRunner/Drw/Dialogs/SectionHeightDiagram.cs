using System.Drawing.Drawing2D;
using KhBatchRunner.Drw.Enlarge;
using KhBatchRunner.UI;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>
/// "Sample section height": a side view of the room or stair, showing how far the section's crop
/// reaches below the floor / first step and above the chosen top — the top of the room / last step,
/// or the next level above. Schematic: a room 2.6 m high under a level at 3.2 m, or a stair rising
/// 3.0 m under a level at 4.5 m; see <see cref="DrwSampleDiagram"/> for the drawing language.
/// </summary>
internal sealed class SectionHeightDiagram : DrwSampleDiagram
{
    private const double W = 5.0, Side = 0.4, Slab = 0.2;

    private SectionTopFrom _from = SectionTopFrom.TargetTop;
    private double _below = 0.3, _above = 0.3;

    public void Set(SectionTopFrom from, double belowM, double aboveM)
    {
        _from = from;
        _below = belowM;
        _above = aboveM;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fore = ForeColor, dim = Theme.Dim(this);
        bool stair = string.Equals(TargetWord, "stair", StringComparison.OrdinalIgnoreCase);
        bool next = _from == SectionTopFrom.NextLevel;

        Rectangle area = DrawingArea;
        if (area.Width < 60 || area.Height < 60) return;

        double own = stair ? 3.0 : 2.6;      // the last step / the top of the room
        double level = stair ? 4.5 : 3.2;    // the next level above
        double below = Math.Max(0.45, Math.Min(1.2, _below));
        double above = Math.Max(0.45, Math.Min(1.2, _above));
        double refZ = next ? level : own;
        double cropTop = refZ + above, cropBottom = -below;
        double topShown = Math.Max(cropTop, level) + 0.3, bottomShown = cropBottom - 0.3;

        double spanX = W + 2 * Side + 0.4, spanY = topShown - bottomShown;
        float s = (float)Math.Min(area.Width / spanX, area.Height / spanY);
        float left0 = area.Left + (float)(area.Width - spanX * s) / 2 + 0.2f * s;
        float top0 = area.Top + (float)(area.Height - spanY * s) / 2;
        float X(double x) => left0 + (float)((x + Side) * s);
        float Y(double z) => top0 + (float)((topShown - z) * s);

        // --- ranges: below the floor (orange), the target (blue), above the top reference (green)
        using (var b = new SolidBrush(Orange)) g.FillRectangle(b, RectF(X(-Side), Y(0), X(W + Side), Y(cropBottom)));
        using (var b = new SolidBrush(Blue)) g.FillRectangle(b, RectF(X(-Side), Y(refZ), X(W + Side), Y(0)));
        using (var b = new SolidBrush(Green)) g.FillRectangle(b, RectF(X(-Side), Y(cropTop), X(W + Side), Y(refZ)));

        // --- slabs, and the room or the stair between them
        using (var slab = new SolidBrush(Color.FromArgb(170, dim)))
        {
            g.FillRectangle(slab, RectF(X(-Side - 0.2), Y(0), X(W + Side + 0.2), Y(-Slab)));
            g.FillRectangle(slab, RectF(X(-Side - 0.2), Y(level), X(W + Side + 0.2), Y(level - Slab)));
            if (stair) g.FillRectangle(slab, RectF(X(3.9), Y(own), X(W + Side + 0.2), Y(own - Slab)));
        }
        using (var pen = new Pen(fore, 1.5f))
        {
            if (stair)
            {
                const int risers = 10;
                double x0 = 0.6, run = (3.9 - x0) / risers, rise = own / risers;
                var pts = new List<PointF> { new(X(x0 - 0.4), Y(0)) };
                for (int i = 0; i < risers; i++)
                {
                    pts.Add(new PointF(X(x0 + i * run), Y(i * rise)));
                    pts.Add(new PointF(X(x0 + i * run), Y((i + 1) * rise)));
                }
                pts.Add(new PointF(X(3.9), Y(own)));
                g.DrawLines(pen, pts.ToArray());
            }
            else
            {
                g.DrawLine(pen, X(0), Y(0), X(0), Y(level - Slab));
                g.DrawLine(pen, X(W), Y(0), X(W), Y(level - Slab));
                using var ceiling = new Pen(dim, 1f) { DashStyle = DashStyle.Dot };
                g.DrawLine(ceiling, X(0), Y(own), X(W), Y(own));
            }
        }
        TextRenderer.DrawText(g, TargetWord, Small, new Point((int)X(stair ? 1.6 : 1.9), (int)Y(own * 0.45)), dim);

        // --- the section's crop, bottom to top
        using (var dash = new Pen(dim, 1f) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(dash, X(-Side), Y(cropTop), (float)((W + 2 * Side) * s), (float)((cropTop - cropBottom) * s));

        // --- numbered reference lines, with level heads on the two levels
        float x1 = X(-Side) - 4, x2 = area.Right + 2;
        var marks = new List<(int N, float Y, Color Ring)>();
        foreach ((int n, double z) in new[] { (1, 0.0), (2, own), (3, level), (4, cropBottom), (5, cropTop) })
        {
            RefLine(g, x1, x2, Y(z));
            marks.Add((n, Y(z), Red));
        }
        TextRenderer.DrawText(g, "next level", Small, new Point((int)X(-Side) + 2, (int)Y(level) - 14), dim);
        TextRenderer.DrawText(g, "base level", Small, new Point((int)X(-Side) + 2, (int)Y(0) - 14), dim);
        Bubbles(g, marks);

        // --- range bands on the left
        float bandA = area.Left - 12, bandB = area.Left - 31;
        Band(g, 6, bandA, Y(0), Y(cropBottom), Orange);
        Band(g, 7, bandA, Y(cropTop), Y(refZ), Green);
        Band(g, 8, bandB, Y(cropTop), Y(cropBottom), Blue);

        string ownName = stair ? "last step" : "top of the room";
        KeyAndCaption(g, new (int, string, bool)[]
        {
            (1, stair ? "Base level / first step" : "Floor of the room", true),
            (2, stair ? "Last step" : "Top of the room", !next),
            (3, "Next level above", next),
            (4, "Crop bottom", true),
            (5, "Crop top", true),
            (6, $"Below {_below:0.00} m", true),
            (7, $"Above {_above:0.00} m", true),
            (8, stair ? "Section height" : "View height", true),
        }, next
            ? "Side view. The top is measured from the next level above, e.g. Roof over a stair that ends at L2."
            : $"Side view. The top is measured from the {ownName}.");
    }
}
