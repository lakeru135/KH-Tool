using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using KhBatchRunner.Drw.DoorWindow;
using KhBatchRunner.UI;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>
/// The Sheets tab's picture: the chosen title block's own lines, its drawing frame (dashed), and
/// the cells of one sheet as the builder will pack them — merged cells, each row's own height —
/// each showing its boxes and Type Mark. Packed by <see cref="DoorWindowLayout.Pack"/>, the same
/// call the builder makes, so the preview and the sheets agree.
/// </summary>
internal sealed class DoorWindowSheetPreview : Control
{
    private DwTitleBlockData? _tb;
    private DoorWindowPreset _p = new();
    private DwPack? _pack;
    private List<string> _marks = new();
    private int _sheet;

    private static readonly Color CellFill = Color.FromArgb(70, 89, 171, 230);
    private static readonly Color MergedFill = Color.FromArgb(80, 245, 166, 90);
    private readonly Font _small = new("Segoe UI", 7.5f);

    public DoorWindowSheetPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Size = new Size(420, 300);
    }

    public void Set(DwTitleBlockData? tb, DoorWindowPreset p, DwPack? pack, List<string> marks, int sheet)
    {
        _tb = tb;
        _p = p;
        _pack = pack;
        _marks = marks;
        _sheet = sheet;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _small.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fore = ForeColor, dim = Theme.Dim(this);
        Rectangle area = Rectangle.Inflate(ClientRectangle, -8, -8);
        if (_tb == null)
        {
            TextRenderer.DrawText(g, "Choose a title block to see the sheet.", _small, area, dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        if (_tb.WidthMm <= 0 || _tb.HeightMm <= 0 || area.Width < 40 || area.Height < 40) return;

        float s = (float)Math.Min(area.Width / _tb.WidthMm, area.Height / _tb.HeightMm);
        float x0 = area.Left + (float)(area.Width - _tb.WidthMm * s) / 2, y0 = area.Top + (float)(area.Height - _tb.HeightMm * s) / 2;
        float X(double mm) => x0 + (float)(mm * s);
        float Y(double mm) => y0 + (float)((_tb.HeightMm - mm) * s);   // paper y grows upward

        // The title block's own lines (its outline, title strip, text frames…).
        using (var pen = new Pen(dim, 1f))
        {
            g.DrawRectangle(pen, X(0), Y(_tb.HeightMm), (float)(_tb.WidthMm * s), (float)(_tb.HeightMm * s));
            foreach (var (x1, y1, x2, y2) in _tb.Lines) g.DrawLine(pen, X(x1), Y(y1), X(x2), Y(y2));
        }
        DwFrame f = _tb.Frame;
        using (var frame = new Pen(Color.FromArgb(220, 70, 70), 1f) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(frame, X(f.L), Y(f.T), (float)(f.Width * s), (float)(f.Height * s));

        if (_pack == null) return;
        double left = f.L + _p.SheetMarginMm, top = f.T - _p.SheetMarginMm;
        using var line = new Pen(fore, 1f);
        foreach (DwSlot slot in _pack.Slots.Where(sl => sl.Sheet == _sheet))
        {
            DwPackRow row = _pack.Rows[slot.Sheet][slot.Row];
            double cx = left + slot.XMm, cy = top - row.TopMm, w = slot.WidthMm;
            var r = RectangleF.FromLTRB(X(cx), Y(cy), X(cx + w), Y(cy - row.HeightMm));
            using (var b = new SolidBrush(w > _p.CellWidthMm + 0.5 ? MergedFill : CellFill)) g.FillRectangle(b, r);
            g.DrawRectangle(line, r.X, r.Y, r.Width, r.Height);
            // The box dividers: elevation | plan | header | table.
            foreach (double d in new[] { _p.ElevationFirst ? row.ElevationMm : _p.PlanBoxMm, row.ElevationMm + _p.PlanBoxMm,
                         row.ElevationMm + _p.PlanBoxMm + _p.HeaderMm })
                g.DrawLine(line, r.Left, Y(cy - d), r.Right, Y(cy - d));
            string mark = slot.Item < _marks.Count ? _marks[slot.Item] : "";
            float hy = Y(cy - row.ElevationMm - _p.PlanBoxMm);
            TextRenderer.DrawText(g, mark, _small, new Rectangle((int)r.Left + 2, (int)hy, (int)r.Width - 2, (int)(_p.HeaderMm * s) + 2), fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        // A grid taller than the frame (one row of cells that cannot fit) shows where it overruns.
        var rows = _pack.Rows.Count > _sheet ? _pack.Rows[_sheet] : new List<DwPackRow>();
        double used = rows.Count == 0 ? 0 : rows[rows.Count - 1].TopMm + rows[rows.Count - 1].HeightMm;
        if (used > f.Height - 2 * _p.SheetMarginMm + 1e-6)
            TextRenderer.DrawText(g, "Taller than the frame", _small, new Point((int)X(f.L) + 4, (int)Y(f.B) - 16), Color.FromArgb(220, 70, 70));
    }
}
