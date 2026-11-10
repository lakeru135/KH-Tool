using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace KhBatchRunner.UI;

/// <summary>
/// The colours a ribbon glyph is drawn with, per theme. Revit's own icons are flat multi-tone
/// drawings — a dark 1px outline, a light body, one blue accent on the part that matters, and red,
/// green or amber only where the colour itself means something — so a single foreground colour
/// (all this add-in had until 1.5.1.4) can't reproduce them.
///
/// <para>A plain class rather than a record: <c>tools/DimInk</c> links this file on its own, and a
/// positional record would drag an <c>IsExternalInit</c> polyfill into every project that does.</para>
/// </summary>
internal sealed class IconPalette
{
    public Color Outline { get; }
    /// <summary>The body of an object — paper, a box, a face.</summary>
    public Color Fill { get; }
    /// <summary>The second, shaded tone: a side face, a header row, the object behind.</summary>
    public Color FillAlt { get; }
    /// <summary>Revit blue — the one thing in the glyph the eye should land on.</summary>
    public Color Accent { get; }
    /// <summary>The light blue Revit uses for glass, clouds and selection bodies.</summary>
    public Color AccentSoft { get; }
    public Color Add { get; }
    public Color Remove { get; }
    public Color Warn { get; }

    private IconPalette(Color outline, Color fill, Color fillAlt, Color accent, Color accentSoft,
        Color add, Color remove, Color warn)
    {
        Outline = outline; Fill = fill; FillAlt = fillAlt; Accent = accent; AccentSoft = accentSoft;
        Add = add; Remove = remove; Warn = warn;
    }

    public static readonly IconPalette Light = new(
        outline: Color.FromArgb(80, 80, 80),
        fill: Color.FromArgb(255, 255, 255),
        fillAlt: Color.FromArgb(212, 212, 212),
        accent: Color.FromArgb(30, 136, 210),
        accentSoft: Color.FromArgb(181, 218, 245),
        add: Color.FromArgb(46, 154, 64),
        remove: Color.FromArgb(214, 58, 58),
        warn: Color.FromArgb(240, 163, 10));

    public static readonly IconPalette Dark = new(
        outline: Color.FromArgb(224, 224, 224),
        fill: Color.FromArgb(90, 99, 114),
        fillAlt: Color.FromArgb(116, 126, 142),
        accent: Color.FromArgb(79, 168, 232),
        accentSoft: Color.FromArgb(47, 95, 134),
        add: Color.FromArgb(92, 191, 92),
        remove: Color.FromArgb(239, 106, 106),
        warn: Color.FromArgb(242, 184, 68));

    /// <summary>
    /// The palette for the theme active right now. Delegates to <see cref="Theme.IsDark"/> rather
    /// than reading Revit's theme a second time here — that keeps the KHBATCH_FORCE_THEME override
    /// honoured for icons too, and leaves exactly one place in the codebase that knows how to ask
    /// Revit what theme it is in.
    /// </summary>
    public static IconPalette ForCurrentTheme()
    {
        try { return Theme.IsDark() ? Dark : Light; }
        catch { return Light; }   // API missing/unavailable → light-theme default.
    }
}

/// <summary>
/// Revit-style ribbon icons drawn with GDI+ instead of shipped image assets, plus the embedded
/// Kanghun logo. Every glyph is a factory <c>(palette, size)</c>, so <see cref="App"/> can redraw it
/// when Revit's theme flips and draw the 16px and 32px images separately.
///
/// <para>Glyphs are laid out on a 32-unit grid and scaled down for 16px. Outlines are one DEVICE
/// pixel at either size (<see cref="Ink.Px"/>), and integer coordinates land a 1px line exactly on a
/// pixel row at 32px — that is what keeps the edges as crisp as Revit's own. Fine detail (grid
/// lines, text rows, tick marks) is skipped at 16px via <see cref="Ink.Detail"/>, where it would only
/// blur into a grey smear.</para>
/// </summary>
internal static class Icons
{
    private static BitmapSource Draw(IconPalette p, int size, Action<Ink> draw)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 32f, size / 32f);
            draw(new Ink(g, p, size));
        }
        return ToImageSource(bmp);
    }

    private static BitmapSource ToImageSource(Bitmap bmp)
    {
        // Copy the 32bpp ARGB pixels straight into a WPF BitmapSource so the transparent
        // background survives. The old GetHbitmap path discards the alpha channel, which
        // paints transparent pixels black — unnoticed on the dark ribbon but a glaring
        // black square on the light one.
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            BitmapSource source = BitmapSource.Create(
                bmp.Width, bmp.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>The embedded Kanghun logo, for the top-left corner of custom dialogs.</summary>
    public static Bitmap? LoadLogo()
    {
        try
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string name = asm.GetManifestResourceNames().First(n => n.EndsWith("KanghunPureLogo.png"));
            using Stream? stream = asm.GetManifestResourceStream(name);
            return stream == null ? null : new Bitmap(stream);
        }
        catch
        {
            return null; // Missing resource shouldn't block the dialog from opening.
        }
    }

    private static PointF Pt(float x, float y) => new(x, y);

    // ---- drawing kit --------------------------------------------------------------------------

    /// <summary>
    /// The shapes Revit's icons are built from — a filled body with an outline, a folded sheet, a
    /// shaded box, a cloud — so each glyph below reads as what it draws rather than as GDI+ calls.
    /// </summary>
    private sealed class Ink
    {
        public Graphics G { get; }
        public IconPalette P { get; }
        /// <summary>False at 16px: leave out the detail that would only blur.</summary>
        public bool Detail { get; }
        /// <summary>One device pixel, in design units (1 at 32px, 2 at 16px).</summary>
        public float Px { get; }

        public Ink(Graphics g, IconPalette p, int size)
        {
            G = g; P = p; Detail = size >= 24; Px = 32f / size;
        }

        /// <summary>A pen <paramref name="width"/> design units wide, never thinner than a pixel.</summary>
        public Pen Stroke(Color c, float width = 1f)
        {
            var pen = new Pen(c, Math.Max(width, Px));
            if (width > 1.5f) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round; }
            return pen;
        }

        public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        /// <summary>The darker edge Revit draws around a coloured body, in the body's own hue.</summary>
        public static Color Deep(Color c) => Mix(c, Color.Black, 0.3f);

        public void Rect(float x, float y, float w, float h, Color fill, Color? stroke = null)
        {
            using (var b = new SolidBrush(fill)) G.FillRectangle(b, x, y, w, h);
            using var pen = Stroke(stroke ?? P.Outline);
            G.DrawRectangle(pen, x, y, w, h);
        }

        public void FillRect(float x, float y, float w, float h, Color fill)
        {
            using var b = new SolidBrush(fill);
            G.FillRectangle(b, x, y, w, h);
        }

        public void Ellipse(float x, float y, float w, float h, Color fill, Color? stroke = null, float width = 1f)
        {
            using (var b = new SolidBrush(fill)) G.FillEllipse(b, x, y, w, h);
            using var pen = Stroke(stroke ?? P.Outline, width);
            G.DrawEllipse(pen, x, y, w, h);
        }

        public void Ring(float x, float y, float w, float h, Color c, float width = 1f)
        {
            using var pen = Stroke(c, width);
            G.DrawEllipse(pen, x, y, w, h);
        }

        public void Dot(float x, float y, float d, Color c)
        {
            using var b = new SolidBrush(c);
            G.FillEllipse(b, x, y, d, d);
        }

        public void Poly(Color fill, Color? stroke, params PointF[] pts)
        {
            using (var b = new SolidBrush(fill)) G.FillPolygon(b, pts);
            using var pen = Stroke(stroke ?? P.Outline);
            G.DrawPolygon(pen, pts);
        }

        public void FillPoly(Color fill, params PointF[] pts)
        {
            using var b = new SolidBrush(fill);
            G.FillPolygon(b, pts);
        }

        public void Line(Color c, float width, float x1, float y1, float x2, float y2)
        {
            using var pen = Stroke(c, width);
            G.DrawLine(pen, x1, y1, x2, y2);
        }

        public void Lines(Color c, float width, params PointF[] pts)
        {
            using var pen = Stroke(c, width);
            G.DrawLines(pen, pts);
        }

        public void Dashed(Color c, float width, float x1, float y1, float x2, float y2)
        {
            using var pen = Stroke(c, width);
            pen.DashStyle = DashStyle.Dash;
            G.DrawLine(pen, x1, y1, x2, y2);
        }

        public void Arc(Color c, float width, float x, float y, float w, float h, float start, float sweep)
        {
            using var pen = Stroke(c, width);
            G.DrawArc(pen, x, y, w, h, start, sweep);
        }

        /// <summary>A straight arrow: a shaft, and a filled head whose tip is at (x2, y2).</summary>
        public void Arrow(Color c, float x1, float y1, float x2, float y2, float width = 2f, float head = 4.5f)
        {
            float dx = x2 - x1, dy = y2 - y1, len = (float)Math.Sqrt(dx * dx + dy * dy);
            float ux = dx / len, uy = dy / len;
            float bx = x2 - ux * head * 1.1f, by = y2 - uy * head * 1.1f;
            Line(c, width, x1, y1, bx + ux * 0.5f, by + uy * 0.5f);
            FillPoly(c, Pt(x2, y2), Pt(bx - uy * head, by + ux * head), Pt(bx + uy * head, by - ux * head));
        }

        /// <summary>
        /// A curved arrow along a circle of radius <paramref name="r"/> round (cx, cy): GDI+ angles,
        /// clockwise from 3 o'clock, with the filled head at the end of the sweep.
        /// </summary>
        public void ArcArrow(Color c, float width, float cx, float cy, float r, float start, float sweep, float head = 3f)
        {
            Arc(c, width, cx - r, cy - r, 2 * r, 2 * r, start, sweep);
            double a = (start + sweep) * Math.PI / 180;
            float ex = cx + r * (float)Math.Cos(a), ey = cy + r * (float)Math.Sin(a);
            float dir = Math.Sign(sweep);
            float tx = -(float)Math.Sin(a) * dir, ty = (float)Math.Cos(a) * dir;   // along the travel
            FillPoly(c, Pt(ex + tx * head, ey + ty * head),
                Pt(ex - ty * head, ey + tx * head), Pt(ex + ty * head, ey - tx * head));
        }

        /// <summary>Runs <paramref name="draw"/> clipped to an ellipse — what a magnifier's lens shows.</summary>
        public void InLens(float x, float y, float d, Action draw)
        {
            using var lens = new GraphicsPath();
            lens.AddEllipse(x, y, d, d);
            GraphicsState saved = G.Save();
            G.SetClip(lens);
            draw();
            G.Restore(saved);
        }

        /// <summary>A tick drawn with a stroke wide enough to read at 16px.</summary>
        public void Check(Color c, float x1, float y1, float x2, float y2, float x3, float y3, float width = 3f) =>
            Lines(c, width, Pt(x1, y1), Pt(x2, y2), Pt(x3, y3));

        /// <summary>A page with a folded top-right corner — a file, a report, a sheet.</summary>
        public void Sheet(float x, float y, float w, float h, float fold = 5f)
        {
            Poly(P.Fill, null, Pt(x, y), Pt(x + w - fold, y), Pt(x + w, y + fold), Pt(x + w, y + h), Pt(x, y + h));
            Poly(P.FillAlt, null, Pt(x + w - fold, y), Pt(x + w - fold, y + fold), Pt(x + w, y + fold));
        }

        /// <summary>
        /// A box in Revit's oblique 3D: front face, lighter top, darker side. <paramref name="s"/> is
        /// the front face's size and <paramref name="d"/> the depth the top and side recede by.
        /// </summary>
        public void Cube(float x, float y, float s, float d, Color front, Color top, Color side, Color stroke)
        {
            Poly(top, stroke, Pt(x, y + d), Pt(x + d, y), Pt(x + s + d, y), Pt(x + s, y + d));
            Poly(side, stroke, Pt(x + s, y + d), Pt(x + s + d, y), Pt(x + s + d, y + s), Pt(x + s, y + s + d));
            Rect(x, y + d, s, s, front, stroke);
        }

        // Revit's Worksets pillars: pastel, three tones per pillar (top lightest, left face, right
        // face darkest) and NO outline. Revit draws them the same in both themes.
        public static readonly Color[] PillarBlue = { Color.FromArgb(184, 224, 252), Color.FromArgb(142, 204, 248), Color.FromArgb(106, 172, 228) };
        public static readonly Color[] PillarPeach = { Color.FromArgb(252, 206, 170), Color.FromArgb(246, 178, 130), Color.FromArgb(228, 146, 92) };
        public static readonly Color[] PillarGreen = { Color.FromArgb(172, 234, 192), Color.FromArgb(112, 208, 142), Color.FromArgb(78, 176, 112) };

        /// <summary>
        /// One isometric pillar from Revit's Worksets icon: a diamond top whose left/right corners are
        /// <paramref name="w"/> apart and whose top corner is at (<paramref name="cx"/>,
        /// <paramref name="y"/>), standing <paramref name="h"/> tall.
        /// </summary>
        public void Pillar(float cx, float y, float w, float h, Color[] tones)
        {
            float r = w / 2f, q = w / 4f;
            FillPoly(tones[1], Pt(cx - r, y + q), Pt(cx, y + r), Pt(cx, y + r + h), Pt(cx - r, y + q + h));
            FillPoly(tones[2], Pt(cx, y + r), Pt(cx + r, y + q), Pt(cx + r, y + q + h), Pt(cx, y + r + h));
            FillPoly(tones[0], Pt(cx, y), Pt(cx + r, y + q), Pt(cx, y + r), Pt(cx - r, y + q));
        }

        /// <summary>The Navisworks-blue box both NWD Helper glyphs share, so the panel reads as one family.</summary>
        public void NavisCube(float x, float y, float s, float d) =>
            Cube(x, y, s, d, P.Accent, Mix(P.Accent, Color.White, 0.45f), Deep(P.Accent), Deep(P.Accent));

        /// <summary>
        /// A 22×13 cloud whose top-left is (<paramref name="ox"/>, <paramref name="oy"/>). Drawn as
        /// its lobes, each first in the outline colour grown by a pixel and then in the fill colour,
        /// which leaves one clean outline round the UNION — outlining each lobe would draw its seams.
        /// </summary>
        public void Cloud(float ox, float oy, Color fill, Color stroke)
        {
            var lobes = new[]
            {
                new RectangleF(ox, oy + 5, 8, 8), new RectangleF(ox + 5, oy, 11, 11),
                new RectangleF(ox + 13, oy + 4, 9, 9),
            };
            var body = new RectangleF(ox + 4, oy + 7, 14, 6);
            float g = Px;
            using (var b = new SolidBrush(stroke))
            {
                foreach (RectangleF r in lobes) G.FillEllipse(b, r.X - g, r.Y - g, r.Width + 2 * g, r.Height + 2 * g);
                G.FillRectangle(b, body.X - g, body.Y - g, body.Width + 2 * g, body.Height + 2 * g);
            }
            using (var b = new SolidBrush(fill))
            {
                foreach (RectangleF r in lobes) G.FillEllipse(b, r);
                G.FillRectangle(b, body);
            }
        }

        /// <summary>A magnifier: glass-blue lens, dark rim, a handle thick enough to read at 16px.</summary>
        public void Magnifier(float x, float y, float d)
        {
            float r = d / 2f, cx = x + r, cy = y + r, k = 0.7071f;
            Line(P.Outline, 3.2f, cx + r * k, cy + r * k, cx + r * k + 5, cy + r * k + 5);
            Ellipse(x, y, d, d, P.AccentSoft, P.Outline, 1.6f);
        }

        /// <summary>A clock face with tick marks at the quarters; the hands are the caller's.</summary>
        public void ClockFace(float x, float y, float d)
        {
            Ellipse(x, y, d, d, P.Fill, P.Outline, 1.5f);
            if (!Detail) return;
            float cx = x + d / 2f, cy = y + d / 2f;
            Line(P.Outline, 1f, cx, y + 2, cx, y + 4);
            Line(P.Outline, 1f, cx, y + d - 4, cx, y + d - 2);
            Line(P.Outline, 1f, x + 2, cy, x + 4, cy);
            Line(P.Outline, 1f, x + d - 4, cy, x + d - 2, cy);
        }

        // Revit's own link badge colours (Insert › Link: Link Revit, Link IFC, Link CAD…): a peach
        // chain with a burnt-orange edge. Revit keeps it the same in both themes, so this does too.
        private static readonly Color LinkBody = Color.FromArgb(244, 180, 128);
        private static readonly Color LinkEdge = Color.FromArgb(176, 96, 36);

        /// <summary>
        /// Revit's link badge: two interlocked peach rings with an orange edge, <paramref name="w"/>
        /// wide. Each ring is a thick edge-coloured stroke with a thinner body stroke on top, which
        /// outlines both sides of the ring; the left ring's right-hand arc is drawn again last so
        /// it passes OVER the right ring — that crossing is what makes them read as interlocked.
        /// </summary>
        public void LinkBadge(float x, float y, float w)
        {
            // Flat ovals, not circles: Revit's rings are about 3:2 and overlap by a third.
            float rw = w * 0.66f, h = w * 0.44f, rx = x + w - rw;
            void RingArc(float rxp, float start, float sweep)
            {
                using (var edge = Stroke(LinkEdge, 3.6f)) G.DrawArc(edge, rxp, y, rw, h, start, sweep);
                using (var body = Stroke(LinkBody, 1.8f)) G.DrawArc(body, rxp, y, rw, h, start, sweep);
            }
            RingArc(x, 0, 360);
            RingArc(rx, 0, 360);
            RingArc(x, -50, 100);
        }

        /// <summary>Text rows on a page; drawn only at 32px.</summary>
        public void Rows(float x1, float x2, params float[] ys)
        {
            if (!Detail) return;
            foreach (float y in ys) Line(P.Outline, 1f, x1, y, x2, y);
        }
    }

    // ---- Setting --------------------------------------------------------------------------------

    public static BitmapSource About(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's information badge: a blue disc with a white "i".
        k.Ellipse(3, 3, 26, 26, k.P.Accent, Ink.Deep(k.P.Accent));
        k.Dot(13.5f, 7.5f, 5, Color.White);
        k.FillRect(14, 14, 4, 11, Color.White);
    });

    public static BitmapSource AiBridge(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Two nodes joined by a wire — the AI (blue) linked to the model (white) it works on.
        k.Line(k.P.Outline, 2f, 10, 16, 22, 16);
        k.Ellipse(2, 8, 16, 16, k.P.Accent, Ink.Deep(k.P.Accent));
        k.Dot(10, 16, 4, Color.White);
        k.Rect(19, 9, 11, 14, k.P.Fill);
        k.Line(k.P.Outline, 1.5f, 22, 13, 27, 13);
        k.Line(k.P.Outline, 1.5f, 22, 19, 27, 19);
    });

    public static BitmapSource AiCode(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A page of code: blue < > brackets on a white sheet, the AI's own code.
        k.Rect(4, 3, 24, 26, k.P.Fill);
        k.Line(k.P.Accent, 2.5f, 13, 10, 8, 16);
        k.Line(k.P.Accent, 2.5f, 8, 16, 13, 22);
        k.Line(k.P.Accent, 2.5f, 19, 10, 24, 16);
        k.Line(k.P.Accent, 2.5f, 24, 16, 19, 22);
    });

    public static BitmapSource EditModelSet(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Three stacked cards — a saved set of models; the front one, blue, is the set in hand.
        // Flat on purpose: as three 3D boxes (1.5.1.4's first cut) it was too busy to read.
        k.Rect(13, 3, 16, 11, k.P.FillAlt);
        k.Rect(8, 9, 16, 11, k.P.Fill);
        k.Rect(3, 15, 16, 12, k.P.AccentSoft, k.P.Accent);
    });

    public static BitmapSource EditScriptSet(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Nodes joined by wires — a Dynamo graph. Inputs blue, the node they feed white.
        k.Line(k.P.Outline, 1.5f, 8, 8, 16, 16);
        k.Line(k.P.Outline, 1.5f, 8, 24, 16, 16);
        k.Line(k.P.Outline, 1.5f, 16, 16, 26, 16);
        k.Rect(2, 5, 8, 6, k.P.Accent, Ink.Deep(k.P.Accent));
        k.Rect(2, 21, 8, 6, k.P.Accent, Ink.Deep(k.P.Accent));
        k.Rect(12, 11, 10, 10, k.P.Fill);
        k.Rect(24, 13, 6, 6, k.P.Add, Ink.Deep(k.P.Add));
    });

    public static BitmapSource SettingsTransfer(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A box with two arrows — out to a file, and back in. The pair is the point: this one button
        // is both halves of the transfer.
        k.Rect(3, 15, 26, 13, k.P.Fill);
        k.FillRect(4, 16, 25, 3, k.P.FillAlt);
        k.Line(k.P.Outline, 1f, 3, 19, 29, 19);
        k.Arrow(k.P.Accent, 11, 13, 11, 1, 2.4f, 4f);       // out (up)
        k.Arrow(k.P.Add, 21, 1, 21, 13, 2.4f, 4f);          // in (down)
    });

    public static BitmapSource DiagnosticReport(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A report sheet under a magnifier — collect the evidence, then look at it.
        k.Sheet(4, 2, 17, 24);
        k.Rows(7, 17, 10, 14, 18);
        k.Magnifier(15, 14, 11);
    });

    // ---- Edit sets / matrix -----------------------------------------------------------------------

    public static BitmapSource RebuildMatrix(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A matrix (header row and column shaded) with a refresh arc — rebuild a matrix from data.
        k.Rect(2, 2, 17, 17, k.P.Fill);
        k.FillRect(3, 3, 16, 4, k.P.FillAlt);
        k.FillRect(3, 3, 4, 16, k.P.FillAlt);
        if (k.Detail)
        {
            k.Line(k.P.Outline, 1f, 2, 7, 19, 7);
            k.Line(k.P.Outline, 1f, 2, 13, 19, 13);
            k.Line(k.P.Outline, 1f, 7, 2, 7, 19);
            k.Line(k.P.Outline, 1f, 13, 2, 13, 19);
        }
        // Three-quarter turn ending at the top, head pointing along it — kept well inside the
        // 32px box (the first cut put the head's tip on the edge, where Revit clipped it).
        k.Arc(k.P.Accent, 2.4f, 18, 18, 11, 11, 0, 270);
        k.FillPoly(k.P.Accent, Pt(28, 18.5f), Pt(23.5f, 14.5f), Pt(23.5f, 22.5f));
    });

    // ---- Batch Run / Schedule ----------------------------------------------------------------------

    public static BitmapSource DynMultiPlayer(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A green play button over a stack of model layers — run across multiple models.
        k.Rect(3, 22, 26, 6, k.P.FillAlt);
        k.Rect(3, 15, 26, 6, k.P.Fill);
        k.Poly(k.P.Add, Ink.Deep(k.P.Add), Pt(10, 1), Pt(10, 13), Pt(21, 7));
    });

    public static BitmapSource BatchInterop(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A tick over the same layers — run the model checker across multiple models.
        k.Rect(3, 22, 26, 6, k.P.FillAlt);
        k.Rect(3, 15, 26, 6, k.P.Fill);
        k.Check(k.P.Add, 9, 6, 14, 11, 23, 2);
    });

    public static BitmapSource ScheduleRun(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A clock: hour hand dark, minute hand blue.
        k.ClockFace(3, 3, 26);
        k.Line(k.P.Outline, 2.4f, 16, 16, 16, 9);
        k.Line(k.P.Accent, 2.4f, 16, 16, 22, 20);
        k.Dot(14.5f, 14.5f, 3, k.P.Outline);
    });

    public static BitmapSource ScheduleInterop(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // The clock with a tick instead of hands — the model checker, on a schedule.
        k.ClockFace(3, 3, 26);
        k.Check(k.P.Add, 10, 16, 14.5f, 20.5f, 22, 11);
    });

    // ---- Multi Model Check ------------------------------------------------------------------------

    public static BitmapSource SiteLocationPbp(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A map pin standing on the site.
        k.Ellipse(7, 24, 18, 6, k.P.FillAlt);
        using (var pin = new GraphicsPath())
        {
            pin.AddArc(7, 2, 18, 18, 140, 260);              // the head, ending at 40°
            pin.AddLine(22.9f, 16.8f, 16, 28);               // down to the point; CloseFigure
            pin.CloseFigure();                               // runs back up the other side
            using var b = new SolidBrush(k.P.Accent);
            k.G.FillPath(b, pin);
            using var pen = k.Stroke(Ink.Deep(k.P.Accent));
            k.G.DrawPath(pen, pin);
        }
        k.Ellipse(12, 7, 8, 8, Color.White, Ink.Deep(k.P.Accent));
    });

    public static BitmapSource PhaseWorkset(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's own two icons, overlapped: the Phases table (blue header row) behind, and the
        // Worksets pillars — blue and peach at the back, green in front — on the right.
        k.Rect(1, 2, 17, 15, k.P.Fill);
        k.Rect(1, 2, 17, 4, k.P.Accent, Ink.Deep(k.P.Accent));
        if (k.Detail)
        {
            k.Line(k.P.Outline, 1f, 1, 10, 18, 10);
            k.Line(k.P.Outline, 1f, 1, 13, 12, 13);
            k.Line(k.P.Outline, 1f, 7, 6, 7, 17);
        }
        k.Pillar(17, 8, 9, 14, Ink.PillarBlue);
        k.Pillar(26, 6, 9, 11, Ink.PillarPeach);
        k.Pillar(22, 13, 9, 14, Ink.PillarGreen);
    });

    public static BitmapSource GetDesignOption(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Two overlapping option panels; the front (primary) one marked blue.
        k.Rect(3, 3, 16, 16, k.P.FillAlt);
        k.Rect(12, 12, 17, 17, k.P.Fill);
        k.Ellipse(17, 17, 7, 7, k.P.Accent, Ink.Deep(k.P.Accent));
    });

    public static BitmapSource GetRvtLinksWorkset(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's Worksets pillars wearing its link badge — read which workset each link sits on.
        k.Pillar(8, 8, 10, 14, Ink.PillarBlue);
        k.Pillar(18, 10, 10, 10, Ink.PillarPeach);
        k.Pillar(14, 14, 10, 11, Ink.PillarGreen);
        k.LinkBadge(14, 1, 17);
    });

    public static BitmapSource DrawingProgress(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A sheet with its title-block strip, and a green progress tick.
        k.Rect(3, 2, 18, 27, k.P.Fill);
        k.Rect(3, 22, 18, 7, k.P.FillAlt);
        k.Rows(6, 18, 7, 11, 15);
        k.Check(k.P.Add, 17, 21, 21.5f, 26, 30, 14);
    });

    public static BitmapSource TypeMarkTextNote(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A text-note "T" with a leader to a type-mark tag.
        k.Line(k.P.Outline, 3f, 3, 4, 17, 4);
        k.Line(k.P.Outline, 3f, 10, 4, 10, 16);
        k.Line(k.P.Outline, 1.2f, 13, 17, 18, 21);
        k.Rect(17, 19, 12, 10, k.P.AccentSoft, k.P.Accent);
        k.Rows(20, 26, 23);
        k.Rows(20, 24, 26);
    });

    // ---- Multi Model Action ------------------------------------------------------------------------

    public static BitmapSource PurgeImportCad(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A CAD sheet struck out in red — delete the CAD import.
        k.Sheet(5, 3, 18, 25);
        k.Rect(5, 3, 13, 5, k.P.FillAlt);
        k.Line(k.P.Remove, 3.4f, 9, 12, 27, 28);
        k.Line(k.P.Remove, 3.4f, 27, 12, 9, 28);
    });

    public static BitmapSource IsolateWarningElements(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A view "eye" holding an amber warning triangle — isolate the flagged elements in a view.
        k.Arc(k.P.Outline, 1.5f, 1, 5, 30, 22, 200, 140);   // upper eyelid
        k.Arc(k.P.Outline, 1.5f, 1, 5, 30, 22, 20, 140);    // lower eyelid
        k.Poly(k.P.Warn, Ink.Deep(k.P.Warn), Pt(16, 7), Pt(25.5f, 24), Pt(6.5f, 24));
        var ink = Color.FromArgb(40, 40, 40);   // on amber in both themes
        k.FillRect(15, 12, 2.4f, 6.5f, ink);
        k.FillRect(15, 20, 2.4f, 2.4f, ink);
    });

    public static BitmapSource BatchLinkRvt(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's Link Revit glyph — a blue RVT page with the link badge — over the pages behind it:
        // one source linked into many hosts.
        k.Rect(9, 6, 17, 21, k.P.FillAlt);
        k.Rect(3, 10, 17, 20, k.P.Accent, Ink.Deep(k.P.Accent));
        if (k.Detail)
        {
            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);
            k.G.DrawString("RVT", font, white, 3.5f, 21);
        }
        k.LinkBadge(14, 2, 16);
    });

    // ---- NWD Helper -------------------------------------------------------------------------------

    public static BitmapSource ExportNwc(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // The Navisworks-blue model with an arrow leaving it — export the model out to a file.
        k.NavisCube(2, 3, 15, 6);
        k.Arrow(k.P.Outline, 16, 27, 31, 27, 2.4f, 4f);
    });

    public static BitmapSource ExportIfcData(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A page marked IFC over rows of data, with the same leaving arrow as Export NWC — the model
        // out as an open file plus the tables the BIM report reads.
        k.Sheet(2, 2, 18, 26);
        k.Rows(5, 12, 16, 20, 24);
        if (k.Detail)
        {
            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var accent = new SolidBrush(k.P.Accent);
            k.G.DrawString("IFC", font, accent, 4.5f, 4.5f);
        }
        k.Arrow(k.P.Outline, 16, 27, 31, 27, 2.4f, 4f);
    });

    public static BitmapSource NwdFamily(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // An arrow into a family file holding the same blue object — Navisworks in, Revit family out.
        k.Sheet(12, 2, 18, 27);
        k.NavisCube(15, 12, 9, 4);
        k.Arrow(k.P.Outline, 1, 16, 11, 16, 2.4f, 4f);
    });

    // ---- Forma Batch Action ---------------------------------------------------------------------------

    public static BitmapSource GetFileLog(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A page of rows with a history clock — a file's version history.
        k.Sheet(3, 2, 18, 26);
        k.Rows(6, 16, 9, 13, 17);
        k.Ellipse(16, 15, 14, 14, k.P.Fill, k.P.Outline, 1.5f);
        k.Line(k.P.Accent, 2f, 23, 22, 23, 18);
        k.Line(k.P.Accent, 2f, 23, 22, 26, 24);
    });

    public static BitmapSource GetDataExtract(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Forma's cloud with a download arrow dropping into a table. Sits beside Get File Log, so the
        // two must read as siblings but never as the same button.
        k.Cloud(5, 1, k.P.AccentSoft, k.P.Accent);
        k.Arrow(k.P.Accent, 16, 10, 16, 22, 2.4f, 4f);
        k.Rect(4, 23, 24, 7, k.P.Fill);
        k.FillRect(5, 24, 23, 2, k.P.FillAlt);
        if (k.Detail) { k.Line(k.P.Outline, 1f, 12, 23, 12, 30); k.Line(k.P.Outline, 1f, 20, 23, 20, 30); }
    });

    public static BitmapSource InitiateToForma(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // An arrow rising into Forma's cloud — publish a model up as a new cloud model.
        k.Cloud(5, 4, k.P.AccentSoft, k.P.Accent);
        k.Arrow(k.P.Add, 16, 30, 16, 9, 3f, 4.5f);
    });

    public static BitmapSource CheckSheetPublish(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A drawing sheet with a green tick — which sheets are published.
        k.Sheet(3, 3, 17, 25);
        k.Rows(6, 15, 10, 14, 18);
        k.Check(k.P.Add, 16, 20, 21, 26, 30, 12, 3.2f);
    });

    public static BitmapSource PublishCloudModel(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // An upload arrow rising into a tray — publish the latest version to the cloud.
        k.Lines(k.P.Outline, 2f, Pt(6, 20), Pt(6, 27), Pt(26, 27), Pt(26, 20));
        k.Arrow(k.P.Accent, 16, 23, 16, 4, 2.6f, 5f);
    });

    // ---- Cut / Relink / Correct Workset ------------------------------------------------------------------------------

    public static BitmapSource CutModel(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A model with a red cut line and scissors — cut one site out of the model.
        k.Rect(3, 6, 26, 20, k.P.Fill);
        k.FillRect(17, 7, 12, 19, k.P.FillAlt);
        k.Dashed(k.P.Remove, 1.6f, 16, 3, 16, 29);
        k.Line(k.P.Outline, 2.2f, 10, 10, 24, 22);
        k.Line(k.P.Outline, 2.2f, 24, 10, 10, 22);
        k.Ring(5, 5, 6, 6, k.P.Accent, 2f);
        k.Ring(5, 21, 6, 6, k.P.Accent, 2f);
    });

    public static BitmapSource Relink(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's link badge under a green re-point arrow — point a link at a new target.
        k.LinkBadge(4, 15, 24);
        k.Arc(k.P.Add, 2.2f, 8, 3, 17, 14, 200, 200);
        k.FillPoly(k.P.Add, Pt(7, 6), Pt(6, 13.5f), Pt(13, 10.5f));
    });

    public static BitmapSource CorrectWorkset(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Workset layers with an element moving onto the top one — tidy the worksets.
        k.Rect(3, 7, 19, 6, k.P.AccentSoft, k.P.Accent);
        k.Rect(3, 15, 19, 6, k.P.Fill);
        k.Rect(3, 23, 19, 6, k.P.FillAlt);
        k.Rect(25, 23, 5, 5, k.P.Accent, Ink.Deep(k.P.Accent));
        k.Arrow(k.P.Add, 27.5f, 21, 27.5f, 4, 2f, 3.5f);
    });

    // ---- Excel Link ---------------------------------------------------------------------------------

    private static void Worksheet(Ink k)
    {
        // The workbook both Excel buttons share: a sheet with Excel's green header row.
        k.Rect(3, 5, 17, 22, k.P.Fill);
        k.Rect(3, 5, 17, 5, k.P.Add, Ink.Deep(k.P.Add));
        if (!k.Detail) return;
        k.Line(k.P.Outline, 1f, 3, 16, 20, 16);
        k.Line(k.P.Outline, 1f, 3, 21, 20, 21);
        k.Line(k.P.Outline, 1f, 10, 10, 10, 27);
    }

    public static BitmapSource ExcelExport(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // The worksheet with an arrow leaving it — export schedules to a workbook.
        Worksheet(k);
        k.Arrow(k.P.Accent, 20, 16, 31, 16, 2.4f, 4.5f);
    });

    public static BitmapSource ExcelImport(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // The worksheet with an arrow entering it — import the edited workbook back.
        Worksheet(k);
        k.Arrow(k.P.Accent, 31, 16, 21, 16, 2.4f, 4.5f);
    });

    // ---- DRW Helper -----------------------------------------------------------------------------------

    public static BitmapSource SyncView(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Two of Revit's view windows (blue title bar) framing the SAME patch of plan, and a pair of
        // sync arrows — the view you switch to opens at the zoom of the one you left.
        k.Rect(2, 2, 18, 15, k.P.FillAlt);
        k.Rect(2, 2, 18, 4, k.P.Accent, Ink.Deep(k.P.Accent));
        k.Rect(6, 9, 8, 5, k.P.AccentSoft, k.P.Accent);
        k.Rect(13, 13, 17, 16, k.P.Fill);
        k.Rect(13, 13, 17, 4, k.P.Accent, Ink.Deep(k.P.Accent));
        k.Rect(17, 20, 8, 5, k.P.AccentSoft, k.P.Accent);
        k.ArcArrow(k.P.Add, 2f, 6, 25, 4.2f, 200, 120);
        k.ArcArrow(k.P.Add, 2f, 6, 25, 4.2f, 20, 120);
    });

    public static BitmapSource EnlargeRoom(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's Room glyph — a light-blue room with its reference cross inside grey walls — under a
        // magnifier showing one corner of it up close: an enlarged plan of the room.
        k.Rect(2, 3, 21, 19, k.P.FillAlt);
        k.Rect(5, 6, 15, 13, k.P.AccentSoft, k.P.Outline);
        if (k.Detail)
        {
            k.Line(k.P.Accent, 1f, 5, 6, 20, 19);
            k.Line(k.P.Accent, 1f, 20, 6, 5, 19);
        }
        k.Magnifier(15, 14, 13);
        k.InLens(15, 14, 13, () =>
        {
            k.FillRect(15, 14, 13, 13, k.P.FillAlt);
            k.FillRect(19.5f, 18.5f, 9, 9, k.P.AccentSoft);
            k.Lines(k.P.Outline, 1.2f, Pt(19.5f, 28), Pt(19.5f, 18.5f), Pt(29, 18.5f));
        });
        k.Ring(15, 14, 13, 13, k.P.Outline, 1.6f);
    });

    public static BitmapSource EnlargeDoorWindow(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A door elevation (frame, leaf, handle) over its schedule table, under the same magnifier
        // as Enlarge Room: the door/window sheet, one type drawn up close with its rows.
        k.Rect(2, 2, 21, 27, k.P.FillAlt);
        k.Rect(6, 4, 11, 15, k.P.AccentSoft, k.P.Outline);
        k.Dot(15, 12, 1.6f, k.P.Outline);
        k.Line(k.P.Outline, 1f, 3, 21, 22, 21);
        if (k.Detail)
        {
            k.Line(k.P.Outline, 1f, 3, 24.5f, 22, 24.5f);
            k.Line(k.P.Outline, 1f, 9, 21, 9, 28);
        }
        k.Magnifier(19, 18, 11);
        k.InLens(19, 18, 11, () =>
        {
            k.FillRect(19, 18, 11, 11, k.P.FillAlt);
            k.FillRect(21, 19, 6, 9, k.P.AccentSoft);
            k.Lines(k.P.Outline, 1.2f, Pt(21, 29), Pt(21, 19), Pt(27, 19), Pt(27, 29));
        });
        k.Ring(19, 18, 11, 11, k.P.Outline, 1.6f);
    });

    public static BitmapSource EnlargeStair(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's Stair glyph — three steps in oblique 3D, treads lit blue — under the same
        // magnifier as Enlarge Room, showing one tread and riser up close.
        // Oblique like Ink.Cube: the side profile faces you, depth recedes up and right, so what
        // shows is the profile, each tread's top and the stair's far end.
        const float d = 4f;
        float[] xs = { 2, 7, 12 }, ys = { 21, 16, 11 };
        k.Poly(k.P.FillAlt, null, Pt(17, 11), Pt(17 + d, 11 - d), Pt(17 + d, 26 - d), Pt(17, 26));
        for (int i = 0; i < 3; i++)
            k.Poly(k.P.AccentSoft, k.P.Outline, Pt(xs[i], ys[i]), Pt(xs[i] + d, ys[i] - d),
                Pt(xs[i] + 5 + d, ys[i] - d), Pt(xs[i] + 5, ys[i]));
        k.Poly(k.P.Fill, null, Pt(2, 26), Pt(2, 21), Pt(7, 21), Pt(7, 16), Pt(12, 16), Pt(12, 11),
            Pt(17, 11), Pt(17, 26));
        k.Magnifier(15, 14, 13);
        k.InLens(15, 14, 13, () =>
        {
            k.FillRect(15, 14, 13, 13, k.P.Fill);
            k.FillPoly(k.P.AccentSoft, Pt(15, 22), Pt(23, 22), Pt(23, 25), Pt(15, 25));
            k.Lines(k.P.Outline, 1.2f, Pt(15, 22), Pt(23, 22), Pt(23, 16), Pt(29, 16));
        });
        k.Ring(15, 14, 13, 13, k.P.Outline, 1.6f);
    });

    public static BitmapSource GridDimension(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's Grid heads — numbered bubbles on centre lines — measured by its Aligned Dimension:
        // a blue string with oblique ticks and its values above. Datum Dimension, at a glance.
        int[] xs = { 5, 16, 27 };
        for (int i = 0; i < xs.Length; i++)
        {
            int x = xs[i];
            if (k.Detail) k.Dashed(k.P.Outline, 1f, x, 10, x, 30);
            else k.Line(k.P.Outline, 1f, x, 10, x, 30);
            k.Ellipse(x - 4.5f, 1, 9, 9, k.P.Fill, k.P.Outline, 1.2f);
            if (!k.Detail) continue;
            using var font = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(k.P.Outline);
            using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            k.G.DrawString((i + 1).ToString(), font, brush, new RectangleF(x - 4.5f, 1.5f, 9, 9), centre);
        }
        k.Line(k.P.Accent, 1.6f, 2, 23, 30, 23);
        foreach (int x in xs) k.Line(k.P.Accent, 2f, x - 2.5f, 25.5f, x + 2.5f, 20.5f);
        if (k.Detail)
        {
            k.Line(k.P.Accent, 1.4f, 8, 18, 13, 18);
            k.Line(k.P.Accent, 1.4f, 19, 18, 24, 18);
        }
    });

    public static BitmapSource MatchDatums(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's Propagate Extents, made legible: the source grid (blue head) on the left, and the
        // stack of other views on the right whose grid heads now sit on the same line.
        k.Rect(17, 5, 14, 25, k.P.FillAlt);
        k.Rect(13, 2, 15, 25, k.P.Fill);
        foreach (int x in new[] { 17, 24 })
        {
            k.Line(k.P.Outline, 1f, x, 11, x, 24);
            k.Ellipse(x - 3, 5, 6, 6, k.P.Fill, k.P.Outline, 1.2f);
        }
        k.Line(k.P.Outline, 1f, 6, 12, 6, 23);
        k.Ellipse(2, 4, 8, 8, k.P.AccentSoft, k.P.Accent, 1.6f);
        k.Arrow(k.P.Accent, 1, 28, 12, 28, 2.2f, 3.5f);
    });

    public static BitmapSource SingleFont(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's Text glyph — a capital A, here "Aa" on a text-type swatch — over one blue bar:
        // one typeface for every text type.
        k.Rect(2, 2, 28, 21, k.P.Fill);
        using var big = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(k.P.Outline);
        using var blue = new SolidBrush(k.P.Accent);
        k.G.DrawString("A", big, ink, 3, 0.5f);
        k.G.DrawString("a", small, blue, 16, 6);
        k.Rect(2, 26, 28, 3.5f, k.P.Accent, Ink.Deep(k.P.Accent));
    });

    public static BitmapSource StairAnnotation(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A stair run in plan wearing Revit's Stair Path — a start dot, a line up the run, an arrow
        // at the top — and a tread-number tag beside its first tread.
        k.Rect(4, 2, 16, 28, k.P.Fill);
        k.FillRect(5, 3, 2, 27, k.P.FillAlt);
        k.FillRect(18, 3, 2, 27, k.P.FillAlt);
        if (k.Detail)
            for (int y = 6; y < 30; y += 4) k.Line(k.P.Outline, 1f, 4, y, 20, y);
        k.Arrow(k.P.Accent, 12, 26, 12, 3, 2.2f, 4f);
        k.Dot(9.5f, 23.5f, 5, k.P.Accent);
        k.Rect(22, 20, 8, 9, k.P.AccentSoft, k.P.Accent);
        if (!k.Detail) return;
        using var font = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(k.P.Outline);
        using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        k.G.DrawString("1", font, brush, new RectangleF(22, 20.5f, 8, 9), centre);
    });

    public static BitmapSource PresetTagger(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // Revit's Tag by Category glyph — a door in plan, its leaf and swing, wearing a blue tag —
        // and a second tag waiting beside it: every element of a category tagged from one preset.
        k.FillRect(2, 24, 6, 4, k.P.Outline);
        k.FillRect(22, 24, 8, 4, k.P.Outline);
        k.Line(k.P.Outline, 1.4f, 8, 24, 8, 10);
        k.Arc(k.P.Outline, 1f, -6, 10, 28, 28, -90, 90);
        k.Rect(13, 3, 17, 10, k.P.Accent, Ink.Deep(k.P.Accent));
        k.Rect(2, 2, 9, 6, k.P.AccentSoft, k.P.Accent);
        if (!k.Detail) return;
        using var font = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        k.G.DrawString("D1", font, white, new RectangleF(13, 3.5f, 17, 10), centre);
    });

    public static BitmapSource WallMatTag(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A wall in plan, its layers drawn, and the Thai finish tag on each face: a triangle above the
        // wall pointing down at it and one below pointing up, each with its finish number.
        k.Rect(2, 13, 28, 7, k.P.Fill);
        k.FillRect(2, 13, 28, 1.6f, k.P.Outline);
        k.FillRect(2, 18.4f, 28, 1.6f, k.P.Outline);
        if (k.Detail) k.Line(k.P.Outline, 0.8f, 2, 16.5f, 30, 16.5f);
        using var tri = new SolidBrush(k.P.Accent);
        using var edge = new Pen(Ink.Deep(k.P.Accent), 1f);
        PointF[] down = { new(9, 12), new(3, 2), new(15, 2) };
        PointF[] upward = { new(23, 21), new(17, 31), new(29, 31) };
        k.G.FillPolygon(tri, down);
        k.G.DrawPolygon(edge, down);
        k.G.FillPolygon(tri, upward);
        k.G.DrawPolygon(edge, upward);
        if (!k.Detail) return;
        using var font = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        k.G.DrawString("1", font, white, new RectangleF(3, 2, 12, 7), centre);
        k.G.DrawString("2", font, white, new RectangleF(17, 24, 12, 7), centre);
    });

    // ---- Parameter Helper ------------------------------------------------------------------------

    /// <summary>The parameter table both Parameter Helper glyphs share: a name column and a value column.</summary>
    private static void ParamTable(Ink k)
    {
        k.Rect(2, 4, 22, 24, k.P.Fill);
        k.Rect(2, 4, 22, 5, k.P.FillAlt);
        k.Rows(2, 24, 15, 21);
        if (k.Detail) k.Line(k.P.Outline, 1f, 11, 9, 11, 28);
    }

    public static BitmapSource FormulaRules(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A parameter table and Revit's blue formula badge "fx": a value worked out from the others.
        ParamTable(k);
        k.Rect(15, 14, 15, 13, k.P.Accent, Ink.Deep(k.P.Accent));
        using var font = new Font("Times New Roman", k.Detail ? 12f : 13f, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        k.G.DrawString("fx", font, white, new RectangleF(15, 14, 15, 13), centre);
    });

    public static BitmapSource MirrorCheck(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // A door in plan, a dashed mirror line, and the mirrored door in blue: the one to look at.
        k.Rect(1, 25, 30, 4, k.P.FillAlt, k.P.Outline);
        k.Line(k.P.Outline, 2f, 4, 25, 4, 13);
        k.Arc(k.P.Outline, 1.2f, -8, 13, 24, 24, 270, 90);
        k.Dashed(k.P.Outline, 1.2f, 16, 2, 16, 31);
        k.Line(k.P.Accent, 2.4f, 28, 25, 28, 13);
        k.Arc(k.P.Accent, 1.6f, 16, 13, 24, 24, 180, 90);
        if (k.Detail) k.Arrow(k.P.Warn, 9, 6, 23, 6, 1.8f, 3.5f);
    });

    public static BitmapSource ApplyFormulas(IconPalette p, int size = 32) => Draw(p, size, k =>
    {
        // The same table with one value cell filled in blue, and an arrow writing into it.
        ParamTable(k);
        k.Rect(11, 15, 13, 6, k.P.AccentSoft, k.P.Accent);
        k.Arrow(k.P.Add, 31, 26, 21, 19, 2.4f, 4.5f);
    });
}
