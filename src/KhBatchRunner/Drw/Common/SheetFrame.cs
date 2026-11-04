using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Common;

/// <summary>
/// A title block's drawing area, read from its own lines. Moved here from the AI Bridge's sheet
/// tools (KH-Tool only) so DRW Helper's Enlarge Door &amp; Window can lay legends out on the same
/// frame; the MCP tools call it from here.
/// </summary>
internal static class SheetFrame
{
    private static double PaperFt(double mm) => mm / 304.8;

    /// <summary>
    /// The drawing area a title block frames, read from its own lines. Pieces lying on one vertical
    /// (or horizontal) line are merged; a line covering at least 70% of the sheet's height (width) is
    /// a long line. On each side the outermost one is the border; from there the edge steps inward
    /// over a title strip — a band between long lines holding short lines — and stops at the first
    /// empty band, so grid or zone lines across the drawing area are not taken for its edge.
    /// A side with no long line falls back to the title block's outer edge.
    /// </summary>
    internal static (double L, double B, double R, double T) DrawingFrame(Element titleBlock, View sheet, BoundingBoxXYZ tb,
        out string found)
    {
        double w = tb.Max.X - tb.Min.X, h = tb.Max.Y - tb.Min.Y;
        double tol = PaperFt(0.5);
        var vertical = new List<(double At, double From, double To)>();
        var horizontal = new List<(double At, double From, double To)>();
        List<(XYZ A, XYZ B)> segments = TitleBlockSegments(titleBlock, sheet, out string source);
        foreach ((XYZ p0, XYZ p1) in segments)
        {
            if (Math.Abs(p0.X - p1.X) < tol) vertical.Add(((p0.X + p1.X) / 2, Math.Min(p0.Y, p1.Y), Math.Max(p0.Y, p1.Y)));
            else if (Math.Abs(p0.Y - p1.Y) < tol) horizontal.Add(((p0.Y + p1.Y) / 2, Math.Min(p0.X, p1.X), Math.Max(p0.X, p1.X)));
        }
        List<double> frameX = FrameLines(vertical, 0.7 * h, tol);
        List<double> frameY = FrameLines(horizontal, 0.7 * w, tol);
        double midX = tb.Min.X + w / 2, midY = tb.Min.Y + h / 2;

        // Lines AT the outer edge are the sheet's edge itself; the frame is inside it.
        double edge = PaperFt(1);
        var left = frameX.Where(x => x < midX && x > tb.Min.X + edge).OrderBy(x => x).ToList();
        var right = frameX.Where(x => x > midX && x < tb.Max.X - edge).OrderByDescending(x => x).ToList();
        var bottom = frameY.Where(y => y < midY && y > tb.Min.Y + edge).OrderBy(y => y).ToList();
        var top = frameY.Where(y => y > midY && y < tb.Max.Y - edge).OrderByDescending(y => y).ToList();

        // Pieces of the long lines are not content; everything else (title strip boxes, rules) is.
        var content = segments
            .Where(s => !(Math.Abs(s.A.X - s.B.X) < tol && frameX.Any(x => Math.Abs(x - s.A.X) < tol))
                     && !(Math.Abs(s.A.Y - s.B.Y) < tol && frameY.Any(y => Math.Abs(y - s.A.Y) < tol)))
            .Select(s => (s.A + s.B) / 2).ToList();
        // Sides in two rounds: a band only counts the lines between the other pair's edges, so a
        // title strip down the right is not read as content of the top band, and vice versa.
        double? l = null, r = null, b = null, t = null;
        for (int round = 0; round < 2; round++)
        {
            double y0 = b ?? (bottom.Count > 0 ? bottom[0] : tb.Min.Y), y1 = t ?? (top.Count > 0 ? top[0] : tb.Max.Y);
            l = Inward(left, w, m => m.X, m => m.Y, y0, y1, content);
            r = Inward(right, w, m => m.X, m => m.Y, y0, y1, content);
            double x0 = l ?? tb.Min.X, x1 = r ?? tb.Max.X;
            b = Inward(bottom, h, m => m.Y, m => m.X, x0, x1, content);
            t = Inward(top, h, m => m.Y, m => m.X, x0, x1, content);
        }
        var sides = new List<string>();
        if (l != null) sides.Add("left");
        if (r != null) sides.Add("right");
        if (b != null) sides.Add("bottom");
        if (t != null) sides.Add("top");
        found = sides.Count == 0
            ? $" (no frame lines found: the outer edge; {vertical.Count} vertical / {horizontal.Count} horizontal pieces from {source})"
            : " (frame found: " + string.Join(", ", sides) + $"; from {source})";
        return (l ?? tb.Min.X, b ?? tb.Min.Y, r ?? tb.Max.X, t ?? tb.Max.Y);
    }

    /// <summary>
    /// One side's edge: from the border (<paramref name="lines"/>[0], the rest ordered inward) step
    /// over each band that holds a title strip's short lines (at least two, lying between
    /// <paramref name="from"/> and <paramref name="to"/> along it) or is under 10 mm wide, and stop at
    /// the first that does not, never more than 35% of the sheet in.
    /// </summary>
    private static double? Inward(List<double> lines, double across, Func<XYZ, double> at, Func<XYZ, double> on,
        double from, double to, List<XYZ> content)
    {
        if (lines.Count == 0) return null;
        double edge = lines[0], margin = PaperFt(1);
        foreach (double next in lines.Skip(1))
        {
            if (Math.Abs(next - lines[0]) > 0.35 * across) break;
            double lo = Math.Min(edge, next) + margin, hi = Math.Max(edge, next) - margin;
            // A thin empty band is a double line or the gap at a row of boxes' edge: step on.
            if (hi - lo > PaperFt(10) && content.Count(c => at(c) > lo && at(c) < hi && on(c) > from + margin && on(c) < to - margin) < 2) break;
            edge = next;
        }
        return edge;
    }

    /// <summary>The positions of lines whose merged pieces cover at least <paramref name="minLength"/>.</summary>
    internal static List<double> FrameLines(List<(double At, double From, double To)> pieces, double minLength, double tol)
    {
        var result = new List<double>();
        foreach (var group in pieces.OrderBy(p => p.At).Aggregate(new List<List<(double At, double From, double To)>>(), (groups, p) =>
                 {
                     if (groups.Count > 0 && Math.Abs(groups[groups.Count - 1][0].At - p.At) < tol) groups[groups.Count - 1].Add(p);
                     else groups.Add(new List<(double At, double From, double To)> { p });
                     return groups;
                 }))
        {
            // Length covered by the union of the pieces' spans.
            double covered = 0, reach = double.MinValue;
            foreach (var p in group.OrderBy(p => p.From))
            {
                double from = Math.Max(p.From, reach);
                if (p.To > from) covered += p.To - from;
                reach = Math.Max(reach, p.To);
            }
            if (covered >= minLength) result.Add(group.Average(p => p.At));
        }
        return result;
    }

    /// <summary>Lines read from each title block family, by family id: opening a family is slow.</summary>
    private static readonly Dictionary<long, List<(XYZ A, XYZ B)>> FamilyLines = new();

    /// <summary>
    /// The title block's straight segments in sheet coordinates, from the first source that has any:
    /// its geometry in the sheet, its geometry without a view, its original geometry, and finally its
    /// family's own lines (opened read-only, never saved) placed by the instance's transform.
    /// </summary>
    internal static List<(XYZ A, XYZ B)> TitleBlockSegments(Element titleBlock, View sheet, out string source)
    {
        var tries = new List<(string Name, Func<GeometryElement?> Read)>
        {
            ("sheet geometry", () => titleBlock.get_Geometry(new Options { View = sheet, IncludeNonVisibleObjects = true })),
            ("model geometry", () => titleBlock.get_Geometry(new Options { IncludeNonVisibleObjects = true })),
            ("original geometry", () => (titleBlock as FamilyInstance)?.GetOriginalGeometry(new Options { IncludeNonVisibleObjects = true }))
        };
        foreach ((string name, Func<GeometryElement?> read) in tries)
        {
            List<(XYZ A, XYZ B)> segs;
            try { segs = Segments(read()).ToList(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            if (segs.Count > 0) { source = name; return segs; }
        }

        source = "nothing readable";
        if (titleBlock is not FamilyInstance fi) return new List<(XYZ A, XYZ B)>();
        Family family = fi.Symbol.Family;
        if (!FamilyLines.TryGetValue(family.Id.Value, out List<(XYZ A, XYZ B)>? local))
        {
            local = new List<(XYZ A, XYZ B)>();
            Document? famDoc = null;
            try
            {
                famDoc = titleBlock.Document.EditFamily(family);
                foreach (CurveElement ce in new FilteredElementCollector(famDoc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                    if (ce.GeometryCurve is Line line) local.Add((line.GetEndPoint(0), line.GetEndPoint(1)));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* not editable: no frame */ }
            finally { famDoc?.Close(false); }
            FamilyLines[family.Id.Value] = local;
        }
        Transform place = fi.GetTransform();
        source = $"the family's own lines ({local.Count})";
        return local.Select(s => (place.OfPoint(s.A), place.OfPoint(s.B))).ToList();
    }

    /// <summary>Every straight segment in a title block's geometry — lines and polyline legs, nested
    /// families included — in sheet coordinates.</summary>
    internal static IEnumerable<(XYZ A, XYZ B)> Segments(GeometryElement? geometry)
    {
        if (geometry == null) yield break;
        foreach (GeometryObject g in geometry)
        {
            if (g is Line line) yield return (line.GetEndPoint(0), line.GetEndPoint(1));
            else if (g is PolyLine poly)
            {
                IList<XYZ> pts = poly.GetCoordinates();
                for (int i = 1; i < pts.Count; i++) yield return (pts[i - 1], pts[i]);
            }
            else if (g is GeometryInstance gi)
                foreach ((XYZ, XYZ) inner in Segments(gi.GetInstanceGeometry())) yield return inner;
        }
    }
}
