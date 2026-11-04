using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

using Microsoft.Win32;

using Timer = System.Windows.Forms.Timer;

namespace KhBatchLauncher;

/// <summary>
/// The notification itself, on a PC where Windows will not deliver one.
///
/// WHY A WINDOW AT ALL. The preferred notification is a Notification Center toast, and on an
/// AppLocker/SRP-managed PC it cannot be raised: the WinRT API is reached through Windows PowerShell,
/// which policy puts in ConstrainedLanguage mode (see UI.RunNotification). This is what stands in for
/// it — so it is built to the same shape Microsoft's own app-notification guidance describes for the
/// "Buttons" case: app logo, app name, title, body, and two side-by-side actions. A stand-in that
/// announces itself as a stand-in is worse than the thing it replaces; this one should be hard to
/// tell from a real toast.
///
/// IT WAITS. That is the whole reason it exists. A tray balloon is on screen for a few seconds —
/// Windows decides how many, the timeout passed to ShowBalloonTip is advisory and ignored on Windows
/// 10/11 — after which only the tray icon carries the message. That was reported twice as "shows a
/// short time before lost and cannot click to log": the icon technically held the notification and
/// technically opened the log, but nobody found it. A notification nobody finds has not notified.
///
/// IT MUST NOT STEAL FOCUS. Shown with <see cref="ShowWithoutActivation"/> and WS_EX_NOACTIVATE, so
/// a card raised while somebody is typing in Revit cannot swallow their keystrokes or interrupt a
/// dialog. A notification that hijacks the keyboard is worse than one that is missed — which is the
/// whole reason this is a plain window and not a modal dialog.
/// </summary>
internal sealed class NotifyCard : Form
{
    /// <summary>The width of a real Windows toast at 100% scaling. Matching it is not vanity: a
    /// notification that is a different size from every other notification on the screen reads as
    /// something else, whatever it says.</summary>
    private const int BaseWidth = 364;

    private const int Pad = 16;
    private const int EdgeGap = 12;
    private const int CornerRadius = 10;
    private const int ButtonCornerRadius = 6;
    private const int LogoSize = 20;
    private const int ButtonHeight = 34;
    private const int ButtonGap = 8;

    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int CsDropShadow = 0x00020000;

    /// <summary>Cards from one run must not sit on top of each other — a run raises a start and a
    /// finish, and a split run raises a message per attempt.</summary>
    private static int _stacked;

    private readonly string _logPath;
    private readonly bool _canOpenLog;
    private readonly Palette _palette;
    private readonly float _scale;

    /// <summary>Where the card comes to rest. The entrance animation runs up to it from below, so
    /// the resting place has to be decided before the first frame is drawn.</summary>
    private int _restingTop;

    internal NotifyCard(string appName, string title, string message, bool failed, string logPath, bool canOpenLog)
    {
        _logPath = logPath;
        _canOpenLog = canOpenLog;
        _palette = Palette.ForCurrentTheme(failed);
        _scale = DetectScale();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = _palette.Surface;
        DoubleBuffered = true;
        // AutoScaleMode.None: every measurement below is already scaled by hand through S(), and
        // letting WinForms scale a second time would compound the two.
        AutoScaleMode = AutoScaleMode.None;
        Width = S(BaseWidth);

        int contentLeft = S(Pad);
        int contentWidth = Width - S(Pad) * 2;

        using (Font uiFont = Fonts.Body(_scale))
        {
            // Nothing here uses Dock or a layout panel. The height depends on how many lines the
            // title and body wrap to, so the card is measured and then laid out — a docked stack
            // would have to be measured afterwards anyway, and could not size the window.
            int y = S(12);

            Controls.Add(new LogoBox(_palette, failed)
            {
                Bounds = new Rectangle(contentLeft, y, S(LogoSize), S(LogoSize))
            });

            var app = new Label
            {
                Text = appName,
                Font = Fonts.Caption(_scale),
                ForeColor = _palette.Secondary,
                AutoSize = false,
                // Run names and job names can contain '&'; without this WinForms eats it and
                // underlines the next character.
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(contentLeft + S(LogoSize + 8), y,
                                       contentWidth - S(LogoSize + 8) - S(52), S(LogoSize))
            };
            Controls.Add(app);

            var close = new GlyphButton(_palette)
            {
                Bounds = new Rectangle(Width - S(Pad) - S(24), y - S(2), S(24), S(24))
            };
            close.Click += (_, _) => Close();
            Controls.Add(close);

            // The "more options" dots next to Close on a real toast — decorative only, since there
            // is no settings menu behind it here. Left off the click-suppression list on purpose:
            // it behaves like the rest of the header (opens the log), never a dead spot that looks
            // interactive and does nothing.
            var overflow = new Label
            {
                Text = "•••",
                Font = Fonts.Caption(_scale),
                ForeColor = _palette.Secondary,
                AutoSize = false,
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(close.Left - S(24), y - S(2), S(20), S(24))
            };
            Controls.Add(overflow);

            y += S(LogoSize) + S(10);

            y = AddWrappedLabel(title, Fonts.Title(_scale), _palette.Primary, contentLeft, y, contentWidth);
            if (message.Length > 0)
            {
                y += S(3);
                y = AddWrappedLabel(message, Fonts.Body(_scale), _palette.Secondary, contentLeft, y, contentWidth);
            }

            y += S(14);

            // Two equal halves, the way the guidance's Buttons example lays them out. With one
            // action the button takes the full width rather than sitting half-empty.
            int buttonCount = canOpenLog ? 2 : 1;
            int buttonWidth = buttonCount == 2
                ? (contentWidth - S(ButtonGap)) / 2
                : contentWidth;

            int bx = contentLeft;
            if (canOpenLog)
            {
                var open = new CardButton(_palette)
                {
                    Text = "Open log",
                    Bounds = new Rectangle(bx, y, buttonWidth, S(ButtonHeight))
                };
                open.Click += (_, _) => OpenAndClose();
                Controls.Add(open);
                bx += buttonWidth + S(ButtonGap);
            }

            var dismiss = new CardButton(_palette)
            {
                Text = "Dismiss",
                Bounds = new Rectangle(bx, y, buttonWidth, S(ButtonHeight))
            };
            dismiss.Click += (_, _) => Close();
            Controls.Add(dismiss);

            Height = y + S(ButtonHeight) + S(14);
        }

        // Clicking the card anywhere is the same as Open log — the whole card reads as the message,
        // so requiring the button would be a trap. Every non-button child needs it too, or a click
        // on the text does nothing and the card looks broken.
        if (canOpenLog)
        {
            Click += (_, _) => OpenAndClose();
            foreach (Control c in Controls)
            {
                if (c is CardButton || c is GlyphButton) continue;
                c.Click += (_, _) => OpenAndClose();
                c.Cursor = Cursors.Hand;
            }
            Cursor = Cursors.Hand;
        }

        PositionBottomRight();
        StartEntrance();
    }

    /// <summary>Keeps the card from taking focus when it appears. Without this, a card raised
    /// mid-run steals keystrokes from whatever the user is doing in Revit.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// The card's own pixels, without putting it on screen where a user could see it — what
    /// <c>--notify-demo --save</c> writes. It exists because the look has to be reviewable on a
    /// machine that cannot show it: a build server, a remote session, or an agent working on
    /// someone else's desktop. The same <see cref="RoundedRect"/> that clips the real window clips
    /// this, so the corners in the file are the corners on screen; only the drop shadow, which
    /// Windows draws, is missing.
    ///
    /// MUST BE SHOWN TO BE PAINTED. <see cref="DrawToBitmap(Bitmap, Rectangle)"/> only captures
    /// what a control has actually painted through a real WM_PAINT pass — a card built but never
    /// shown draws as an empty surface, no text, no logo, no buttons, because none of its children
    /// have painted even once. Measured, not assumed: without this, every demo PNG came out as a
    /// blank rounded rectangle. Showing it off-screen gets a real paint pass into every child
    /// without ever putting a pixel where a user could see it — <see cref="ShowWithoutActivation"/>
    /// and the NOACTIVATE/TOOLWINDOW styles already in <see cref="CreateParams"/> mean it still
    /// cannot steal focus or appear in Alt-Tab even for the instant it exists.
    /// </summary>
    internal Bitmap Render()
    {
        Point resting = Location;
        Location = new Point(-32000, -32000);
        Show();
        Refresh();

        var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));

        Hide();
        Location = resting;

        var clipped = new Bitmap(Width, Height);
        using (Graphics g = Graphics.FromImage(clipped))
        using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width, Height), S(CornerRadius)))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(path);
            g.DrawImage(bitmap, 0, 0);
        }
        bitmap.Dispose();
        return clipped;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // NOACTIVATE for the same reason as ShowWithoutActivation, but enforced by the window
            // manager rather than by the show call: a later click on the card must not pull focus
            // out of Revit either. Buttons still receive their clicks under it.
            // TOOLWINDOW keeps a notification out of Alt-Tab.
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    /// <summary>
    /// Rounded corners, redone whenever the size changes. A region is used rather than a layered
    /// window with per-pixel alpha: the smoother corners that would buy are not worth hand-painting
    /// every control, and the card carries real buttons that must keep their hover states.
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width, Height), S(CornerRadius));
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(_palette.Border);
        using GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), S(CornerRadius));
        e.Graphics.DrawPath(pen, path);
    }

    private int AddWrappedLabel(string text, Font font, Color colour, int x, int y, int width)
    {
        int height = TextRenderer.MeasureText(text, font, new Size(width, 0),
                                              TextFormatFlags.WordBreak).Height;
        Controls.Add(new Label
        {
            Text = text,
            Font = font,
            ForeColor = colour,
            AutoSize = false,
            UseMnemonic = false,
            Bounds = new Rectangle(x, y, width, height)
        });
        return y + height;
    }

    private void OpenAndClose()
    {
        if (_canOpenLog) Program.OpenLog(_logPath);
        Close();
    }

    /// <summary>
    /// Bottom-right of the WORKING area, not the screen: the taskbar is part of the screen and a
    /// card placed over it is half hidden. Stacks upward for each additional card, and gives up
    /// stacking once it would run off the top rather than marching off-screen.
    /// </summary>
    private void PositionBottomRight()
    {
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        int slot = _stacked++;
        int y = area.Bottom - S(EdgeGap) - Height - (slot * (Height + S(8)));
        if (y < area.Top) y = area.Top + S(EdgeGap);
        _restingTop = y;
        Location = new Point(area.Right - S(EdgeGap) - Width, y);
    }

    /// <summary>
    /// Rises into place, the way a toast arrives. Without it the card blinks into existence and
    /// reads as a window rather than as a notification.
    ///
    /// It SLIDES BUT DOES NOT FADE. Touching <see cref="Form.Opacity"/> makes the window layered,
    /// and a layered window does not honour the rounded <see cref="Form.Region"/> set in OnResize —
    /// measured, not assumed: with the fade in place the card was created, reported a window handle,
    /// and drew absolutely nothing. Rounded corners are the point; the fade was decoration.
    /// </summary>
    private void StartEntrance()
    {
        const int steps = 12;
        int travel = S(24);

        Top = _restingTop + travel;

        int step = 0;
        var animate = new Timer { Interval = 15 };
        animate.Tick += (_, _) =>
        {
            step++;
            // Ease-out: fast at first, settling at the end. A linear slide looks mechanical.
            double t = (double)step / steps;
            double eased = 1 - Math.Pow(1 - t, 3);
            Top = _restingTop + (int)Math.Round(travel * (1 - eased));
            if (step < steps) return;
            animate.Stop();
            animate.Dispose();
            Top = _restingTop;
        };
        animate.Start();
    }

    private int S(int value) => (int)Math.Round(value * _scale);

    /// <summary>
    /// The card's own DPI scaling. This executable is deliberately not DPI-aware — a launcher that
    /// must run before Revit does should declare as little about itself as possible — so Windows
    /// scales the finished window and this returns 1. The arithmetic stays because it costs nothing
    /// and is what a manifest change would need.
    /// </summary>
    private static float DetectScale()
    {
        try
        {
            using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
            return Math.Max(1f, g.DpiX / 96f);
        }
        catch
        {
            return 1f;
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// The two colour sets, chosen by the Windows app theme so the card matches the notifications it
    /// is standing in for. A dark card on a light desktop is the tell that gives a stand-in away.
    /// </summary>
    private sealed class Palette
    {
        internal Color Surface = Color.White;
        internal Color Border = Color.Gainsboro;
        internal Color Primary = Color.Black;
        internal Color Secondary = Color.Gray;
        internal Color ButtonFace = Color.White;
        internal Color ButtonHover = Color.WhiteSmoke;
        internal Color Logo = Color.FromArgb(0, 95, 184);
        internal bool Failed;

        internal static Palette ForCurrentTheme(bool failed)
        {
            bool light = IsLightTheme();
            var p = light
                ? new Palette
                {
                    Surface = Color.FromArgb(249, 249, 249),
                    Border = Color.FromArgb(229, 229, 229),
                    Primary = Color.FromArgb(26, 26, 26),
                    Secondary = Color.FromArgb(93, 93, 93),
                    ButtonFace = Color.FromArgb(253, 253, 253),
                    ButtonHover = Color.FromArgb(243, 243, 243),
                    Logo = Color.FromArgb(0, 95, 184)
                }
                // Colours measured off a real Windows 11 dark-mode toast (Notifications Visualizer),
                // not guessed: the surface Mica material reads as a cool charcoal — blue channel
                // noticeably above red — not a neutral grey, which is what made the first version
                // read as "a dark window" rather than "a Windows notification".
                : new Palette
                {
                    Surface = Color.FromArgb(32, 37, 46),
                    Border = Color.FromArgb(55, 60, 70),
                    Primary = Color.FromArgb(255, 255, 255),
                    Secondary = Color.FromArgb(175, 180, 190),
                    ButtonFace = Color.FromArgb(41, 46, 56),
                    ButtonHover = Color.FromArgb(52, 57, 68),
                    Logo = Color.FromArgb(76, 156, 232)
                };
            p.Failed = failed;
            return p;
        }

        private static bool IsLightTheme()
        {
            // An override so both looks can be checked without changing the machine's actual theme
            // — a test that flips a user's Windows setting to inspect itself is not one you can run
            // on their PC.
            try
            {
                string forced = Environment.GetEnvironmentVariable("KHBATCH_NOTIFY_THEME") ?? "";
                if (forced.Equals("light", StringComparison.OrdinalIgnoreCase)) return true;
                if (forced.Equals("dark", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { /* fall through to the real setting */ }

            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int value) return value != 0;
            }
            catch { /* an unreadable theme setting is not worth failing a notification over */ }
            return true;
        }
    }

    private static class Fonts
    {
        /// <summary>Windows 11's UI face where it exists, Windows 10's where it does not, and the
        /// shell's own message font as the last resort. Named rather than assumed: a missing family
        /// silently falls back to Microsoft Sans Serif, which looks nothing like a notification.
        /// </summary>
        private static readonly string Family = Resolve();

        internal static Font Title(float scale) => new(Family, 11f * ScaleFudge(scale), FontStyle.Regular);
        internal static Font Body(float scale) => new(Family, 9f * ScaleFudge(scale), FontStyle.Regular);
        internal static Font Caption(float scale) => new(Family, 8.25f * ScaleFudge(scale), FontStyle.Regular);

        // Point sizes are already DPI-independent; the scale is only ever applied to pixel
        // measurements. Kept as a function so the two cannot be confused at the call sites.
        private static float ScaleFudge(float scale) => 1f;

        private static string Resolve()
        {
            foreach (string candidate in new[] { "Segoe UI Variable Text", "Segoe UI" })
            {
                try
                {
                    using var probe = new FontFamily(candidate);
                    return candidate;
                }
                catch (ArgumentException) { /* not installed; try the next */ }
            }
            return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Microsoft Sans Serif";
        }
    }

    /// <summary>
    /// The app logo in the header, where a real toast shows the app it came from. Drawn rather than
    /// loaded: this executable ships as one file beside the add-in, and an icon resource would be
    /// one more thing to keep in step across three installers for a 20-pixel square.
    /// </summary>
    private sealed class LogoBox : Control
    {
        private readonly Palette _palette;
        private readonly bool _failed;

        internal LogoBox(Palette palette, bool failed)
        {
            _palette = palette;
            _failed = failed;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            // The card's own colour rather than Color.Transparent: a Button does not support a
            // transparent back colour at all (it throws), and every one of these paints its own
            // background anyway, so real transparency would buy nothing but an inconsistency.
            BackColor = palette.Surface;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // AntiAliasGridFit, not ClearTypeGridFit: this control is its own double-buffered
            // surface (ControlStyles.OptimizedDoubleBuffer), which GDI+ builds as an alpha-channel
            // bitmap — ClearType's colour subpixel hinting assumes an opaque RGB surface and, drawn
            // onto alpha, leaves red/blue fringes around every glyph. Grayscale hinting has no
            // subpixel colour component, so there is nothing left to fringe. Measured, not assumed:
            // the "K" came out edged in magenta before this changed.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = RoundedRect(box, Math.Max(2, Width / 5)))
            using (var fill = new SolidBrush(_palette.Logo))
                g.FillPath(fill, path);

            // "K" for Kanghun BIM, which is the name on the installer and the Start-menu folder.
            using var font = new Font(Fonts.Body(1f).FontFamily, Height * 0.52f, FontStyle.Bold,
                                      GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("K", font, text, new RectangleF(0, 0, Width, Height), format);

            // A failed run gets a badge rather than a red card. Windows toasts do not recolour
            // themselves by severity, and one that did would read as a different application.
            if (!_failed) return;
            int d = Math.Max(6, Width / 2);
            var badge = new Rectangle(Width - d, Height - d, d - 1, d - 1);
            using var dot = new SolidBrush(Color.FromArgb(196, 43, 28));
            using var ring = new Pen(_palette.Surface, Math.Max(1f, Width / 12f));
            g.FillEllipse(dot, badge);
            g.DrawEllipse(ring, badge);
        }
    }

    /// <summary>An action in the button row. Flat, rounded, borderless, and hover-lit — a stock
    /// WinForms button has square corners and a system chrome that would be the one wrong note on
    /// the card. Matches the reference (Notifications Visualizer / Fluent toast buttons): a plain
    /// lighter-than-card fill with no outline and no accent colour on either button — real toast
    /// action buttons don't distinguish "primary" from "secondary" by colour.</summary>
    private sealed class CardButton : Button
    {
        private readonly Palette _palette;
        private bool _hot;

        internal CardButton(Palette palette)
        {
            _palette = palette;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = palette.Surface;
            TabStop = false;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // AntiAliasGridFit rather than TextRenderer.DrawText's GDI ClearType: same alpha-buffer
            // fringing as LogoBox (see its OnPaint) — this control is UserPaint + double-buffered
            // too, so GDI's colour subpixel hinting leaves fringes here as well.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var background = new SolidBrush(_palette.Surface))
                g.FillRectangle(background, ClientRectangle);

            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            Color face = _hot ? _palette.ButtonHover : _palette.ButtonFace;
            using (GraphicsPath path = RoundedRect(box, ButtonCornerRadius))
            using (var fill = new SolidBrush(face))
                g.FillPath(fill, path);

            using var font = Fonts.Body(1f);
            using var brush = new SolidBrush(_palette.Primary);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(Text, font, brush, ClientRectangle, format);
        }
    }

    /// <summary>The close affordance in the header corner. The glyph is drawn as two lines rather
    /// than set in Segoe MDL2 Assets, so it cannot come out as a missing-glyph box on a machine
    /// where that font has been stripped.</summary>
    private sealed class GlyphButton : Control
    {
        private readonly Palette _palette;
        private bool _hot;

        internal GlyphButton(Palette palette)
        {
            _palette = palette;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = palette.Surface;
            TabStop = false;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(_hot ? _palette.ButtonHover : _palette.Surface))
                g.FillRectangle(background, ClientRectangle);

            float inset = Width * 0.34f;
            using var pen = new Pen(_palette.Secondary, Math.Max(1f, Width / 18f));
            g.DrawLine(pen, inset, inset, Width - inset, Height - inset);
            g.DrawLine(pen, Width - inset, inset, inset, Height - inset);
        }
    }
}
