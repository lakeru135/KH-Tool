using System.Drawing.Drawing2D;
using KhBatchRunner.Drw.Enlarge;
using KhBatchRunner.UI;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>
/// "Sample section placement": a plan of the room/stair looking N up the page, showing where the
/// chosen option puts the section line and what it sees. A nominal 5 × 4 m target; see
/// <see cref="DrwSampleDiagram"/> for the drawing language.
/// </summary>
internal sealed class SectionPlacementDiagram : DrwSampleDiagram
{
    // The nominal target, in metres: width across the view, depth along it, wall thickness.
    private const double W = 5.0, D = 4.0, Wall = 0.2;

    private SectionPlacement _mode = SectionPlacement.FromEdge;
    private double _inset = 0.5, _offset, _crop = 0.3;

    public void Set(SectionPlacement mode, double insetM, double centreOffsetM, double cropM)
    {
        _mode = mode;
        _inset = insetM;
        _offset = centreOffsetM;
        _crop = cropM;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fore = ForeColor, dim = Theme.Dim(this);
        bool centre = _mode == SectionPlacement.FromCentre;
        bool shifted = centre && Math.Abs(_offset) > 1e-6;

        Rectangle area = DrawingArea;
        if (area.Width < 60 || area.Height < 60) return;

        // Drawn values, clamped (min 0.45 m keeps neighbouring numbered lines a bubble apart).
        double crop = Math.Max(0.45, Math.Min(0.9, _crop));
        double cut = centre
            ? Math.Max(0.45, Math.Min(D - 0.45, D / 2 - Math.Max(-D / 2, Math.Min(D / 2, _offset))))
            : Math.Max(0.45, Math.Min(D / 2, _inset));

        double spanX = W + 2 * crop, spanY = D + crop + Wall + 0.15;
        float s = (float)Math.Min(area.Width / spanX, area.Height / spanY);
        float left0 = area.Left + (float)(area.Width - spanX * s) / 2;
        float top0 = area.Top + (float)(area.Height - spanY * s) / 2;
        float X(double x) => left0 + (float)((x + crop) * s);
        float Y(double y) => top0 + (float)((D + crop - y) * s);   // y = 0 near edge, D far edge

        // --- ranges: what the section sees (blue) and the crop past the walls (orange)
        using (var b = new SolidBrush(Orange))
        {
            g.FillRectangle(b, RectF(X(-crop), Y(D + crop), X(W + crop), Y(D)));
            g.FillRectangle(b, RectF(X(-crop), Y(D), X(0), Y(cut)));
            g.FillRectangle(b, RectF(X(W), Y(D), X(W + crop), Y(cut)));
        }
        using (var b = new SolidBrush(Blue))
            g.FillRectangle(b, RectF(X(0), Y(D), X(W), Y(cut)));

        // --- walls (inner and outer face) and the target's name
        using (var inner = new Pen(fore, 1.5f))
            g.DrawRectangle(inner, X(0), Y(D), (float)(W * s), (float)(D * s));
        using (var outer = new Pen(dim, 1f))
            g.DrawRectangle(outer, X(-Wall), Y(D + Wall), (float)((W + 2 * Wall) * s), (float)((D + 2 * Wall) * s));
        TextRenderer.DrawText(g, TargetWord, Small, new Point((int)X(0.15), (int)Y(D - 0.12)), dim);

        // --- the crop / far clip outline
        using (var dash = new Pen(dim, 1f) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(dash, X(-crop), Y(D + crop), (float)((W + 2 * crop) * s), (float)((D + crop - cut) * s));

        // --- numbered reference lines, red and dashed as in View Range
        float lineStart = X(-crop) - 4, lineEnd = area.Right + 2;
        var marks = new List<(int N, float Y, Color Ring)>();
        void Ref(int n, double y)
        {
            RefLine(g, lineStart, lineEnd, Y(y));
            marks.Add((n, Y(y), Red));
        }
        Ref(1, 0);
        if (centre) Ref(2, D / 2);
        Ref(4, D);
        Ref(5, D + crop);

        // --- the section line: thick, with its head pointing the way it looks (up = N)
        float yCut = Y(cut), xHead = X(-crop) + 2, xTail = X(W + crop) - 2;
        using (var pen = new Pen(LineColor, 3f))
            g.DrawLine(pen, xHead, yCut, xTail, yCut);
        using (var brush = new SolidBrush(LineColor))
        {
            g.FillPolygon(brush, new[] { new PointF(xHead, yCut - 11), new PointF(xHead - 6, yCut), new PointF(xHead + 6, yCut) });
            g.FillEllipse(brush, xTail - 3, yCut - 3, 6, 6);
        }
        TextRenderer.DrawText(g, "looking N", Small, new Point((int)xHead + 8, (int)yCut - 16), LineColor);
        marks.Add((3, yCut, LineColor));
        Bubbles(g, marks);

        // --- range bands on the left with arrows, as View Range's 5 / 6 / 7
        float bandA = area.Left - 12, bandB = area.Left - 31;
        Band(g, 6, bandA, Y(D), yCut, Blue);
        Band(g, 7, bandA, Y(D + crop), Y(D), Orange);
        if (!centre) Band(g, 8, bandB, yCut, Y(0), Green);
        else if (shifted) Band(g, 8, bandB, Math.Min(yCut, Y(D / 2)), Math.Max(yCut, Y(D / 2)), Green);

        KeyAndCaption(g, new (int, string, bool)[]
        {
            (1, "Near edge (behind)", true),
            (2, TargetTitle + " centre", centre),
            (3, "Section line", true),
            (4, "Far edge", true),
            (5, "Crop limit", true),
            (6, "Seen by the section", true),
            (7, $"Side/far crop {_crop:0.00} m", true),
            (8, centre ? $"Offset from centre {_offset:0.00} m" : $"Inset {_inset:0.00} m", !centre || shifted),
        }, "Each ticked side (N/E/S/W) is made the same way, turned to face it.");
    }
}
