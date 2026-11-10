using System.Drawing.Drawing2D;
using KhBatchRunner.Drw.DoorWindow;
using KhBatchRunner.UI;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>
/// "Sample cell" for Enlarge Door &amp; Window: one cell drawn to the proportions of the Layout tab's
/// values — elevation box with a door standing on the floor line and its width dimension, plan box,
/// header strip, and three table rows (the second wrapping to two lines at the cell's width) — with every setting as a
/// numbered band and a key that carries its value (and, for the cell's own sizes, the distance it
/// is in the model at the legend's scale). The setting being edited is drawn in red and bold in the
/// key, so it is plain which part of the cell it moves; clicking a band's number or a key line picks
/// that setting (<see cref="PartClicked"/>: the user, 2026-10-22, "hard to find where to adjust").
/// Language as <see cref="DrwSampleDiagram"/>; a schematic, so tiny values are drawn a little larger.
/// </summary>
internal sealed class DoorWindowDiagram : DrwSampleDiagram
{
    /// <summary>The settings, in key order (the number drawn is the index + 1).</summary>
    public enum Part { CellWidth, ElevationBox, PlanBox, Header, WallStub, LineHeight, LabelWidth, Gap, FloorGap, DimOffset }

    private static readonly string[] Names =
    {
        "Cell width", "Elevation box height", "Plan box height", "Header strip height", "Wall either side in plan",
        "Line height", "Label column width", "Gap before the value", "Floor line above box bottom", "Dimensions stand off"
    };

    private DoorWindowPreset _p = new();
    private Part? _active;
    private string _size = "900 x 2100";

    private const int KeyLine = 15;

    /// <summary>Where each setting's number bubble and key line were last drawn, for clicks.</summary>
    private readonly List<(Part Part, RectangleF Hit)> _hits = new();

    /// <summary>A band's number or a key line was clicked: the window focuses that setting's field.</summary>
    public event Action<Part>? PartClicked;

    public DoorWindowDiagram()
    {
        Size = new Size(330, 520);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (part, hit) in _hits)
            if (hit.Contains(e.Location)) { PartClicked?.Invoke(part); return; }
    }

    /// <summary>A click at <paramref name="at"/>, as the mouse gives it (the self-test).</summary>
    internal void ClickAt(Point at) => OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0));

    /// <summary>The setting at <paramref name="at"/> (the self-test clicks with it), or null.</summary>
    internal Part? PartAt(Point at) => _hits.Where(h => h.Hit.Contains(at)).Select(h => (Part?)h.Part).FirstOrDefault();

    /// <summary>Where setting <paramref name="part"/>'s key line was drawn (the self-test).</summary>
    internal Point KeyPoint(Part part) =>
        _hits.Where(h => h.Part == part).Select(h => Point.Round(new PointF(h.Hit.Left + 10, h.Hit.Top + h.Hit.Height / 2))).LastOrDefault();

    /// <summary>"3.50 m": a cell size in the model at the legend's scale (paper mm x scale).</summary>
    public static string Real(double paperMm, int scale) => $"{paperMm * Math.Max(1, scale) / 1000:0.00} m";

    private static bool HasReal(Part part) => part is Part.CellWidth or Part.ElevationBox or Part.PlanBox;

    public void Set(DoorWindowPreset p, Part? active, string sampleSize)
    {
        _p = p;
        _active = active;
        _size = sampleSize;
        Invalidate();
    }

    private double Value(Part part) => part switch
    {
        Part.CellWidth => _p.CellWidthMm,
        Part.ElevationBox => _p.ElevationBoxMm,
        Part.PlanBox => _p.PlanBoxMm,
        Part.Header => _p.HeaderMm,
        Part.LineHeight => _p.RowLineMm,
        Part.LabelWidth => _p.LabelWidthMm,
        Part.Gap => _p.TextInsetMm,
        Part.WallStub => _p.WallStubMm,
        Part.FloorGap => _p.FloorGapMm,
        _ => _p.DimOffsetMm
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _hits.Clear();
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fore = ForeColor, dim = Theme.Dim(this);

        int keyHeight = Names.Length * KeyLine + 6;
        // Room under the cell for the label / gap / wrap bands (two rows of them) above the key.
        var area = Rectangle.FromLTRB(44, 26, ClientSize.Width - 36, ClientSize.Height - keyHeight - 46);
        if (area.Width < 80 || area.Height < 120) return;

        // The cell in paper mm (schematic floors so every band stays visible).
        double w = Math.Max(40, _p.CellWidthMm);
        double elev = Math.Max(20, _p.ElevationBoxMm), plan = Math.Max(10, _p.PlanBoxMm), head = Math.Max(4, _p.HeaderMm);
        double line = Math.Max(3, _p.RowLineMm);
        int[] rowLines = { 1, 2, 1 };
        double table = rowLines.Sum() * line;
        double total = elev + plan + head + table;
        bool elevFirst = _p.ElevationFirst;

        float s = (float)Math.Min(area.Width / w, area.Height / total);
        float x0 = area.Left + (float)(area.Width - w * s) / 2, y0 = area.Top;
        float X(double mm) => x0 + (float)(mm * s);
        float Y(double mm) => y0 + (float)(mm * s);

        double elevTop = elevFirst ? 0 : plan, planTop = elevFirst ? elev : 0;
        double headTop = elev + plan, tableTop = headTop + head;

        // --- frame
        using (var pen = new Pen(fore, 1.2f))
        {
            g.DrawRectangle(pen, X(0), Y(0), (float)(w * s), (float)(total * s));
            foreach (double yy in new[] { elevFirst ? elev : plan, headTop, tableTop })
                g.DrawLine(pen, X(0), Y(yy), X(w), Y(yy));
        }
        TextRenderer.DrawText(g, _p.ElevationTitle, Small, new Point((int)X(1.5) + 2, (int)Y(elevTop) + 2), dim);
        TextRenderer.DrawText(g, _p.PlanTitle, Small, new Point((int)X(1.5) + 2, (int)Y(planTop) + 2), dim);

        // --- elevation: a door on the floor line, its width dimension above
        double floor = elevTop + elev - Math.Max(2, _p.FloorGapMm);
        double doorW = Math.Min(w * 0.35, 18), doorH = Math.Min(elev * 0.55, Math.Max(10, floor - elevTop - Math.Max(2, _p.DimOffsetMm) - 8));
        double dx = (w - doorW) / 2, doorTop = floor - doorH;
        using (var pen = new Pen(fore, 1.2f))
        {
            g.DrawLine(pen, X(2), Y(floor), X(w - 2), Y(floor));
            g.DrawRectangle(pen, X(dx), Y(doorTop), (float)(doorW * s), (float)(doorH * s));
        }
        using (var thin = new Pen(dim, 1f))
            g.DrawRectangle(thin, X(dx + 2), Y(doorTop + 2), (float)((doorW - 4) * s), (float)((doorH - 2) * s));
        // The level symbol at the floor line's left end, its text above the line.
        if (_p.FloorLabel.Trim().Length > 0)
        {
            float tx = X(5), ty = Y(floor), tw = Math.Max(3f, (float)(1.25 * s)), th = Math.Max(4f, (float)(2 * s));
            using (var pen = new Pen(fore, 1f))
                g.DrawPolygon(pen, new[] { new PointF(tx, ty), new PointF(tx - tw, ty - th), new PointF(tx + tw, ty - th) });
            TextRenderer.DrawText(g, _p.FloorLabel.Trim(), Small, new Point((int)(tx + tw + 2), (int)(ty - th - 9)), fore, TextFormatFlags.NoPrefix);
        }
        double dimY = doorTop - Math.Max(2, _p.DimOffsetMm);
        using (var pen = new Pen(fore, 1f))
        {
            g.DrawLine(pen, X(dx), Y(dimY), X(dx + doorW), Y(dimY));
            g.DrawLine(pen, X(dx), Y(dimY - 1.5), X(dx), Y(doorTop));
            g.DrawLine(pen, X(dx + doorW), Y(dimY - 1.5), X(dx + doorW), Y(doorTop));
        }

        // --- plan: the opening, the wall stubs either side of it and a swing
        double py = planTop + plan / 2 + 3;
        double stub = Math.Min(Math.Max(2, _p.WallStubMm), dx - 4);
        using (var pen = new Pen(fore, 1.2f))
        {
            g.DrawRectangle(pen, X(dx - stub), Y(py - 1.5), (float)(stub * s), (float)(3 * s));
            g.DrawRectangle(pen, X(dx + doorW), Y(py - 1.5), (float)(stub * s), (float)(3 * s));
            float r = (float)(doorW * s);
            if (r > 4) g.DrawArc(pen, X(dx) - r, Y(py) - r, 2 * r, 2 * r, 270, 90);
            g.DrawLine(pen, X(dx), Y(py), X(dx), Y(py) - r);
        }

        // --- header
        using (var big = new Font("Segoe UI", Math.Max(7f, Math.Min(12f, (float)(head * s * 0.55))), FontStyle.Bold))
            TextRenderer.DrawText(g, "D1", big, new Rectangle((int)X(1), (int)Y(headTop), (int)(14 * s) + 30, (int)(head * s)), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Description", Small, new Rectangle((int)X(17), (int)Y(headTop), (int)((w - 17) * s), (int)(head * s)), dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        // --- table rows
        double label = Math.Min(Math.Max(5, _p.LabelWidthMm), w * 0.6);
        double valueLeft = Math.Min(label + Math.Max(0, _p.TextInsetMm), w - 5);
        // Values wrap at what is left of the cell (DoorWindowLayout.ValueWidthMm).
        double wrap = Math.Max(5, w - valueLeft - 3);
        double ry = tableTop;
        // English sample text, whatever the rows' own labels are (the preview explains the layout).
        string[] labels = { "Size", "Frame", "Qty." }, values = { _size, "Aluminium, bronze — a value that wraps", "4" };
        using (var pen = new Pen(fore, 1f))
        {
            g.DrawLine(pen, X(label), Y(tableTop), X(label), Y(total));
            for (int i = 0; i < rowLines.Length; i++)
            {
                double h = rowLines[i] * line;
                TextRenderer.DrawText(g, labels[i], Small, new Rectangle((int)X(0), (int)Y(ry), (int)(label * s), (int)(h * s)), fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, values[i], Small, new Rectangle((int)X(valueLeft), (int)Y(ry), (int)(wrap * s), (int)(h * s)), fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                ry += h;
                if (i < rowLines.Length - 1) g.DrawLine(pen, X(0), Y(ry), X(w), Y(ry));
            }
        }
        using (var wrapPen = new Pen(dim, 1f) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(wrapPen, X(valueLeft), Y(tableTop + line) + 1, (float)(wrap * s), (float)(2 * line * s) - 2);

        // --- bands: horizontal above / below, vertical left / right
        float left1 = X(0) - 12, right1 = X(w) + 12;
        HBand(g, Part.CellWidth, Y(0) - 12, X(0), X(w));
        VBand(g, Part.ElevationBox, left1, Y(elevTop), Y(elevTop + elev));
        VBand(g, Part.PlanBox, left1, Y(planTop), Y(planTop + plan));
        VBand(g, Part.Header, left1, Y(headTop), Y(tableTop));
        VBand(g, Part.LineHeight, right1, Y(tableTop), Y(tableTop + line));
        VBand(g, Part.FloorGap, right1, Y(floor), Y(elevTop + elev));
        VBand(g, Part.DimOffset, X(dx + doorW) + 12, Y(dimY), Y(doorTop));   // beside the door it measures
        float under = Y(total) + 12;
        HBand(g, Part.LabelWidth, under, X(0), X(label));
        HBand(g, Part.Gap, under + 18, X(label), X(valueLeft));
        HBand(g, Part.WallStub, Y(py) + (float)(3 * s) + 8, X(dx + doorW), X(dx + doorW + stub));

        // --- key
        int ky = ClientSize.Height - keyHeight + 2;
        foreach (Part part in Enum.GetValues(typeof(Part)))
        {
            bool on = _active == part;
            using var f = new Font(Small, on ? FontStyle.Bold : FontStyle.Regular);
            string real = HasReal(part) ? $" = {Real(Value(part), _p.Scale)} at 1:{_p.Scale}" : "";
            var keyRow = new Rectangle(4, ky, ClientSize.Width - 8, KeyLine);
            TextRenderer.DrawText(g, $"{(int)part + 1}  {Names[(int)part]}: {Value(part):0.#} mm{real}", f,
                keyRow, on ? Red : fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            _hits.Add((part, keyRow));
            ky += KeyLine;
        }
    }

    private void VBand(Graphics g, Part part, float x, float yTop, float yBottom)
    {
        if (yBottom - yTop < 3) yBottom = yTop + 3;
        using (var pen = new Pen(_active == part ? Red : Color.FromArgb(230, Blue), _active == part ? 3f : 2f))
        {
            pen.StartCap = LineCap.ArrowAnchor;
            pen.EndCap = LineCap.ArrowAnchor;
            g.DrawLine(pen, x, yTop + 1, x, yBottom - 1);
        }
        BubbleAt(g, (int)part + 1, x, (yTop + yBottom) / 2, _active == part ? Red : Color.FromArgb(255, Blue));
        _hits.Add((part, new RectangleF(x - Bubble, (yTop + yBottom) / 2 - Bubble, 2 * Bubble, 2 * Bubble)));
    }

    private void HBand(Graphics g, Part part, float y, float xLeft, float xRight)
    {
        if (xRight - xLeft < 3) xRight = xLeft + 3;
        using (var pen = new Pen(_active == part ? Red : Color.FromArgb(230, Blue), _active == part ? 3f : 2f))
        {
            pen.StartCap = LineCap.ArrowAnchor;
            pen.EndCap = LineCap.ArrowAnchor;
            g.DrawLine(pen, xLeft + 1, y, xRight - 1, y);
        }
        BubbleAt(g, (int)part + 1, (xLeft + xRight) / 2, y, _active == part ? Red : Color.FromArgb(255, Blue));
        _hits.Add((part, new RectangleF((xLeft + xRight) / 2 - Bubble, y - Bubble, 2 * Bubble, 2 * Bubble)));
    }
}
