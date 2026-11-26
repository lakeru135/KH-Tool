using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KhBatchRunner.UI;

/// <summary>A 1px divider panel. A marker so <see cref="Theme.Apply"/> gives it the palette's border
/// colour instead of flattening it to the form colour like every other Panel (E13a).</summary>
internal sealed class ThemeRule : Panel { }

/// <summary>
/// Applies a light/dark palette to a WinForms dialog that follows Revit's own UI
/// theme (<c>Autodesk.Revit.UI.UIThemeManager.CurrentTheme</c>, Revit 2024+). Colors track
/// Revit's dark grey so the add-in's dialogs don't glare against the ribbon.
///
/// Revit's theme is read by REFLECTION rather than a compile-time reference, so this file
/// carries no dependency on RevitAPIUI.dll and can be compiled into hosts that have no Revit
/// at all — the standalone controller links it directly. Same reasoning as
/// <see cref="Auth.SessionTokenProvider"/>: a type that may not be present is reached
/// reflectively so its absence degrades instead of failing to load.
/// </summary>
internal static class Theme
{
    /// <summary>
    /// Overrides theme detection for rendering dialogs outside Revit (e.g. the
    /// <c>tools/ThemePreview</c> harness, which has no <c>UIThemeManager</c> to ask).
    /// Set to "dark" or "light" (case-insensitive); unset/anything else falls through to the
    /// normal Revit-theme check below.
    /// </summary>
    private const string ForceThemeEnvVar = "KHBATCH_FORCE_THEME";

    public static bool IsDark()
    {
        string? forced = Environment.GetEnvironmentVariable(ForceThemeEnvVar);
        if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) return false;

        // Inside Revit, follow Revit. Outside it (the standalone controller), follow WINDOWS —
        // a program with no Revit in the process has no reason to force light, and a dialog that
        // ignores the desktop's dark mode looks broken rather than neutral.
        try { if (IsRunningInRevit()) return IsRevitThemeDark(); }
        catch { /* fall through to the Windows setting */ }

        try { return IsWindowsThemeDark(); }
        catch { return false; } // nothing to ask → light.
    }

    /// <summary>
    /// Reads <c>UIThemeManager.CurrentTheme</c> out of the RevitAPIUI assembly Revit has already
    /// loaded. Returns false (light) in any host where that assembly isn't present — the
    /// standalone controller, or <c>tools/ThemePreview</c> — rather than throwing, so a
    /// non-Revit host renders in light mode instead of failing.
    ///
    /// Still isolated behind a never-inlined method: it keeps the Revit touch out of
    /// <see cref="IsDark"/>'s own JIT-compilation, which is what made the original
    /// compile-time-referenced version safe in a hostless process.
    /// </summary>
    /// <summary>
    /// True when RevitAPIUI is loaded in this process — i.e. we are running inside Revit and its
    /// theme is the one to follow. Never <c>Assembly.Load</c>s it: pulling RevitAPIUI into a process
    /// that isn't Revit is pointless and its dependencies wouldn't resolve anyway.
    /// </summary>
    private static bool IsRunningInRevit() => FindRevitApiUi() != null;

    private static Assembly? FindRevitApiUi()
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(a.GetName().Name, "RevitAPIUI", StringComparison.OrdinalIgnoreCase))
                return a;
        }
        return null;
    }

    /// <summary>
    /// Windows' own app theme, for hosts with no Revit to ask. 0 = dark in the
    /// Personalize key; a missing value means the default, which is light.
    /// </summary>
    private static bool IsWindowsThemeDark()
    {
        object? value = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", null);
        return value is int i && i == 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsRevitThemeDark()
    {
        Assembly? revitApiUi = FindRevitApiUi();
        if (revitApiUi is null) return false;

        Type? manager = revitApiUi.GetType("Autodesk.Revit.UI.UIThemeManager");
        object? current = manager?.GetProperty("CurrentTheme", BindingFlags.Public | BindingFlags.Static)
                                 ?.GetValue(null);
        // UITheme is an enum; Dark is the name we're testing for.
        return current != null &&
               string.Equals(current.ToString(), "Dark", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Palette applied to a dialog and all of its child controls.</summary>
    private readonly record struct Palette(
        Color Back, Color Field, Color Fore, Color Button, Color Border,
        Color GridHeader, Color Accent, Color Link, Color Error);

    // Revit 2026's dark theme is a blue-slate, not neutral charcoal — sampled from the
    // Manage Links dialog so the add-in's dialogs sit seamlessly against the ribbon.
    private static Palette ForDark() => new(
        Back: Color.FromArgb(69, 79, 97),
        Field: Color.FromArgb(48, 57, 70),
        Fore: Color.FromArgb(233, 234, 235),
        Button: Color.FromArgb(85, 96, 117),
        Border: Color.FromArgb(99, 112, 133),
        GridHeader: Color.FromArgb(51, 59, 73),
        Accent: Color.FromArgb(48, 141, 200),
        Link: Color.FromArgb(105, 198, 242),
        // Firebrick is ~2:1 on this slate; a salmon red keeps "this is wrong" legible.
        Error: Color.FromArgb(255, 128, 118));

    private static Palette ForLight() => new(
        Back: SystemColors.Control,
        Field: SystemColors.Window,
        Fore: SystemColors.ControlText,
        Button: SystemColors.Control,
        Border: SystemColors.ControlDark,
        // Light mode is untouched visually — these two just reproduce the single Button-based
        // color the grid used everywhere before GridHeader/Accent existed.
        GridHeader: SystemColors.Control,
        Accent: SystemColors.Control,
        Link: SystemColors.HotTrack,
        Error: Color.Firebrick);

    // Combo colors captured for the owner-draw handler.
    private static Color _comboBack, _comboFore, _comboSelect, _comboDisabledBack, _comboBorder;
    private static bool _comboDark;

    /// <summary>
    /// Per-combo "is this item dimmed?" predicates, keyed by the control.
    ///
    /// <para><b>Why the theme owns this and not the caller.</b> In dark mode <see cref="Apply"/>
    /// already owner-draws every combo, because a DropDownList's popup stays system-coloured
    /// however its BackColor is set. A caller that wants ONE item greyed therefore cannot just add
    /// its own <c>DrawItem</c> handler — both run, and whichever was subscribed last paints over
    /// the other. That is exactly what happened to the account greying in 1.5.1: it drew the dim
    /// text and the theme's handler immediately repainted every row in one flat colour.</para>
    ///
    /// <para>So there is one painter, and this is how it is told which rows to dim.
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> so a closed dialog's combo is still
    /// collectable.</para>
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, Func<int, bool>>
        ComboDimming = new();

    /// <summary>
    /// Draw items of <paramref name="combo"/> matching <paramref name="dimmed"/> in dimmed text.
    ///
    /// <para>Turns owner-draw on in BOTH themes — dark mode already had it, light mode did not, and
    /// a greying that only worked in one theme would be the sort of half-feature nobody trusts. The
    /// colours come from the active palette either way, so a light combo still looks light.</para>
    /// </summary>
    public static void DimItemsWhere(ComboBox combo, Func<int, bool> dimmed)
    {
        ComboDimming.Remove(combo);
        ComboDimming.Add(combo, dimmed);
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem -= ComboDrawItem;
        combo.DrawItem += ComboDrawItem;
    }

    /// <summary>
    /// Test hook for tools\ThemePreview's layout gate: 1.25, 1.5 or 2 builds every dialog with its
    /// font that much larger, BEFORE the dialog measures itself — which is what Windows display
    /// scaling does to a dialog in Revit. Always 1 in Revit; nothing in the add-in sets it.
    /// </summary>
    internal static float PreviewScale { get; set; } = 1f;

    public static void Apply(Form form)
    {
        // Pin the font explicitly: .NET Framework's WinForms default is Microsoft Sans Serif
        // 8.25pt, .NET 8's is Segoe UI 9pt — without this the Revit 2024 (net48) build renders
        // every dialog smaller/cramped. No-op on net8, where Segoe UI 9pt is already the default.
        form.Font = new Font("Segoe UI", 9f * PreviewScale);

        bool dark = IsDark();
        Palette p = dark ? ForDark() : ForLight();
        _comboBack = p.Field;
        _comboFore = p.Fore;
        _comboSelect = p.Accent;
        _comboDisabledBack = p.Back;   // a disabled combo sinks to the form colour
        _comboBorder = p.Border;
        _comboDark = dark;

        form.BackColor = p.Back;
        form.ForeColor = p.Fore;
        foreach (Control c in form.Controls)
            Style(c, p, dark);

        ApplyTitleBar(form, dark, p.Back);
        SetLogo(form);
    }

    /// <summary>
    /// A slightly dimmed variant of a themed control's own foreground, blended toward its
    /// background — for secondary/help text (e.g. <see cref="FormLayout.NoteLabel"/>) that
    /// shouldn't compete visually with primary labels. Call after <see cref="Apply"/> has
    /// already colored the control.
    /// </summary>
    /// <summary>
    /// The colour for an error / "this is wrong" message in the active theme. Resolved at call time
    /// from the current theme (not from the control), so it is right even when the message is set
    /// long after the dialog was built and themed. <paramref name="c"/> is accepted so call sites
    /// read like <see cref="Dim(Control)"/>.
    /// </summary>
    public static Color ErrorColor(Control c) => (IsDark() ? ForDark() : ForLight()).Error;

    public static Color Dim(Control c) =>
        // A HelpNote is dimmed by Apply already; blend from the palette's full-strength text rather
        // than from its (already dimmed) ForeColor, so the old per-window "note.ForeColor =
        // Theme.Dim(note)" lines are harmless instead of dimming a second time.
        c is HelpNote ? Blend((IsDark() ? ForDark() : ForLight()).Fore, c.BackColor, 0.6f)
                      : Blend(c.ForeColor, c.BackColor, 0.6f);

    /// <summary>
    /// As <see cref="Dim(Control)"/>, but for somewhere that has no control of its own to read —
    /// a <see cref="DataGridView"/> cell style, whose colours live on the style rather than on the
    /// control. Used to grey a row the user may look at but not choose.
    /// </summary>
    public static Color Dim(Color fore, Color back) => Blend(fore, back, 0.6f);

    private static Color Blend(Color fore, Color back, float foreWeight) => Color.FromArgb(
        ClampByte(fore.R * foreWeight + back.R * (1 - foreWeight)),
        ClampByte(fore.G * foreWeight + back.G * (1 - foreWeight)),
        ClampByte(fore.B * foreWeight + back.B * (1 - foreWeight)));

    private static int ClampByte(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;

    // ---- Kanghun logo (window icon — the "top-left" of every dialog) ----------

    private static Icon? _logoIcon;
    private static bool _logoLoaded;

    private static void SetLogo(Form form)
    {
        if (!_logoLoaded)
        {
            _logoLoaded = true;
            using Bitmap? bmp = Icons.LoadLogo();
            if (bmp != null)
            {
                // One HICON for the add-in's whole lifetime — the handle isn't explicitly
                // destroyed, but it's a single allocation reused by every dialog, not a
                // per-dialog leak.
                try { _logoIcon = Icon.FromHandle(bmp.GetHicon()); }
                catch { _logoIcon = null; }
            }
        }
        if (_logoIcon != null)
            form.Icon = _logoIcon;
    }

    private static void Style(Control c, Palette p, bool dark)
    {
        switch (c)
        {
            case ComboBox combo:
                combo.BackColor = p.Field;
                combo.ForeColor = p.Fore;
                if (dark)
                {
                    // Flat darkens the drop-arrow; owner-draw darkens the popup list itself —
                    // a DropDownList's list stays system-colored otherwise, no matter the BackColor.
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.DrawMode = DrawMode.OwnerDrawFixed;
                    combo.DrawItem -= ComboDrawItem;
                    combo.DrawItem += ComboDrawItem;
                    combo.EnabledChanged -= InvalidateOnEnabledChanged;
                    combo.EnabledChanged += InvalidateOnEnabledChanged;
                    DisabledCombos.Hook(combo);
                }
                break;

            case TextBox tb:
                tb.BackColor = p.Field;
                tb.ForeColor = p.Fore;
                if (dark) DisabledTextBoxes.Hook(tb);
                break;

            case TabControl tabs:
                tabs.BackColor = p.Back;
                tabs.ForeColor = p.Fore;
                if (dark) DarkTabs.Hook(tabs, p);
                break;

            case DateTimePicker dtp:
                dtp.BackColor = p.Field;
                dtp.ForeColor = p.Fore;
                if (dark)
                {
                    // The popup calendar is a system window; these colours reach it where visual
                    // styles allow, and cost nothing where they do not.
                    dtp.CalendarMonthBackground = p.Field;
                    dtp.CalendarForeColor = p.Fore;
                    dtp.CalendarTitleBackColor = p.Button;
                    dtp.CalendarTitleForeColor = p.Fore;
                    DarkDatePickers.Hook(dtp, p);
                }
                break;

            case ListBox or CheckedListBox or TreeView:
                c.BackColor = p.Field;
                c.ForeColor = p.Fore;
                break;

            case DataGridView grid:
                grid.BackgroundColor = p.Field;
                grid.GridColor = p.Border;
                grid.DefaultCellStyle.BackColor = p.Field;
                grid.DefaultCellStyle.ForeColor = p.Fore;
                grid.DefaultCellStyle.SelectionBackColor = p.Accent;
                grid.DefaultCellStyle.SelectionForeColor = p.Fore;
                grid.ColumnHeadersDefaultCellStyle.BackColor = p.GridHeader;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = p.Fore;
                // Selection colours equal the normal ones, so the active column's header never turns accent-blue.
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.GridHeader;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Fore;
                grid.EnableHeadersVisualStyles = false;
                break;

            case LinkLabel link:
                // No LinkLabel is in use yet — themed here so one dropped in later just works.
                link.BackColor = link.Parent?.BackColor ?? p.Back;
                link.LinkColor = p.Link;
                link.ForeColor = p.Fore;
                break;

            case Button b:
                // Light mode: leave the button natively themed. Forcing BackColor on a
                // visual-styles button triggers a flat repaint that leaves a mismatched
                // rectangle behind the text; the native look is correct on a light form.
                if (dark)
                {
                    b.BackColor = p.Button;
                    b.ForeColor = p.Fore;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = p.Border;
                    HookDisabledPaint(b);
                }
                break;

            case CheckBox or RadioButton:
                c.BackColor = p.Back;
                c.ForeColor = p.Fore;
                if (dark) HookDisabledPaint((ButtonBase)c);
                break;

            case ThemeRule rule:
                // A hairline divider: its own colour, never the form's (see ThemeRule).
                rule.BackColor = p.Border;
                break;

            case HelpNote note:
                // Help prose is ALWAYS secondary text: dimmed here so no window has to remember to.
                note.BackColor = note.Parent?.BackColor ?? p.Back;
                note.ForeColor = Blend(p.Fore, note.BackColor, 0.6f);
                break;

            case Label:
                // WinForms' Label "transparency" is a hack that just skips painting its own
                // background — on a custom-colored (non-default) parent it can leave a
                // mismatched system-color rectangle around the text instead of blending in
                // ("text bg not extend to internal box"). Match the parent's real, already-
                // themed color instead of relying on Transparent.
                c.BackColor = c.Parent?.BackColor ?? p.Back;
                c.ForeColor = p.Fore;
                break;

            default:
                // Panels, SplitContainer panels, etc.
                c.BackColor = p.Back;
                c.ForeColor = p.Fore;
                break;
        }

        foreach (Control child in c.Controls)
            Style(child, p, dark);
    }

    /// <summary>Paints a dark combo item (closed box + dropdown list) in the dark palette.</summary>
    private static void ComboDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        // Dark only: light mode keeps its previous look (this handler also serves DimItemsWhere there).
        bool disabled = _comboDark && !combo.Enabled;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        // The closed face with nothing chosen must not fill with the selection colour: an empty
        // focused DropDownList otherwise paints as one solid slab of accent blue.
        bool selected = !disabled && (e.State & DrawItemState.Selected) != 0
                        && !(_comboDark && edit && (e.Index < 0 || combo.SelectedIndex < 0));
        Color back = disabled ? _comboDisabledBack : selected ? _comboSelect : _comboBack;
        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, e.Bounds);
        if (e.Index >= 0)
        {
            string text = combo.GetItemText(combo.Items[e.Index]) ?? "";
            Color fore = _comboFore;
            if (disabled || (ComboDimming.TryGetValue(combo, out Func<int, bool>? dimmed) && dimmed(e.Index)))
                fore = Dim(fore, back);
            // NoPrefix: item text is data (e.g. "A & B"), so '&' must draw literally, not as a mnemonic.
            TextRenderer.DrawText(e.Graphics, text, e.Font ?? combo.Font, e.Bounds, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);
        }
    }

    private static void InvalidateOnEnabledChanged(object? sender, EventArgs e) =>
        (sender as Control)?.Invalidate();

    // ---- disabled Button / CheckBox / RadioButton text (dark only) ------------------------------
    //
    // WinForms paints a disabled button's or checkbox's caption in the system disabled colour and
    // ignores ForeColor, which on the dark slate is near-black. A Paint handler runs after the
    // native paint, so it covers the caption area and redraws the text in Dim. It returns at once
    // when enabled, so re-enabling simply stops the overdraw (the native paint is untouched), and
    // the -=/+= pairs mean a second Apply never stacks a second handler.

    private static void HookDisabledPaint(ButtonBase b)
    {
        b.Paint -= PaintDisabledText;
        b.Paint += PaintDisabledText;
        b.EnabledChanged -= InvalidateOnEnabledChanged;
        b.EnabledChanged += InvalidateOnEnabledChanged;
    }

    private static TextFormatFlags AlignFlags(ContentAlignment a) => a switch
    {
        ContentAlignment.TopLeft => TextFormatFlags.Left | TextFormatFlags.Top,
        ContentAlignment.TopCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Top,
        ContentAlignment.TopRight => TextFormatFlags.Right | TextFormatFlags.Top,
        ContentAlignment.MiddleLeft => TextFormatFlags.Left | TextFormatFlags.VerticalCenter,
        ContentAlignment.MiddleRight => TextFormatFlags.Right | TextFormatFlags.VerticalCenter,
        ContentAlignment.BottomLeft => TextFormatFlags.Left | TextFormatFlags.Bottom,
        ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom,
        ContentAlignment.BottomRight => TextFormatFlags.Right | TextFormatFlags.Bottom,
        _ => TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter,
    };

    private static void PaintDisabledText(object? sender, PaintEventArgs e)
    {
        if (sender is not ButtonBase b || b.Enabled || b.Image != null || b.Text.Length == 0) return;
        // Layouts this overdraw can't reproduce: leave the native drawing alone.
        if (b.RightToLeft == RightToLeft.Yes) return;
        if (b is Button && b.BackgroundImage != null) return;
        if (b is CheckBox { Appearance: Appearance.Button } || b is RadioButton { Appearance: Appearance.Button }) return;

        Rectangle area;
        // '&' is a mnemonic only when UseMnemonic: hide its underline (a disabled control has no cue);
        // otherwise it is literal text.
        TextFormatFlags flags = TextFormatFlags.EndEllipsis
            | (b.UseMnemonic ? TextFormatFlags.HidePrefix : TextFormatFlags.NoPrefix);
        if (b is Button)
        {
            area = Rectangle.Inflate(b.ClientRectangle, -1, -1);
        }
        else
        {
            // Only the common layout (box on the left, caption to its right) is repainted.
            ContentAlignment ca = b is CheckBox cb ? cb.CheckAlign : ((RadioButton)b).CheckAlign;
            if (ca != ContentAlignment.MiddleLeft && ca != ContentAlignment.TopLeft
                && ca != ContentAlignment.BottomLeft) return;
            int glyph = b is CheckBox
                ? CheckBoxRenderer.GetGlyphSize(e.Graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled).Width
                : RadioButtonRenderer.GetGlyphSize(e.Graphics, System.Windows.Forms.VisualStyles.RadioButtonState.UncheckedDisabled).Width;
            area = new Rectangle(glyph + 2, 0, Math.Max(0, b.Width - glyph - 2), b.Height);
        }
        flags |= AlignFlags(b.TextAlign);
        // A fixed-size control wraps long text instead of ellipsising it.
        if (!b.AutoSize && TextRenderer.MeasureText(e.Graphics, b.Text, b.Font).Width > area.Width)
            flags |= TextFormatFlags.WordBreak;
        using (var brush = new SolidBrush(b.BackColor))
            e.Graphics.FillRectangle(brush, area);
        TextRenderer.DrawText(e.Graphics, b.Text, b.Font, area, Dim(b), flags);
    }

    // ---- disabled TextBox (dark only) -----------------------------------------------------------

    /// <summary>
    /// A disabled edit draws its text in the system disabled colour whatever ForeColor says. This
    /// subclasses the one control (NativeWindow) and, after the native paint, redraws the
    /// single-line text in Dim over the field colour while disabled. Enabled = native path only.
    /// </summary>
    private sealed class DisabledTextBoxes : NativeWindow
    {
        private static readonly ConditionalWeakTable<TextBox, DisabledTextBoxes> Hooked = new();
        private readonly TextBox _tb;
        private const int WM_PAINT = 0x000F;

        private DisabledTextBoxes(TextBox tb) { _tb = tb; }

        public static void Hook(TextBox tb)
        {
            if (Hooked.TryGetValue(tb, out _)) return;   // Apply twice must not stack hooks
            var h = new DisabledTextBoxes(tb);
            Hooked.Add(tb, h);
            tb.EnabledChanged += (_, _) =>
            {
                tb.Invalidate();
                // Invalidate covers the client only; the border lives in the non-client frame.
                if (tb.IsHandleCreated) RedrawWindow(tb.Handle, IntPtr.Zero, IntPtr.Zero, RDW_FRAME | RDW_INVALIDATE);
            };
            tb.HandleCreated += (_, _) => h.AssignHandle(tb.Handle);
            tb.HandleDestroyed += (_, _) => h.ReleaseHandle();
            if (tb.IsHandleCreated) h.AssignHandle(tb.Handle);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // The border: Windows draws a DISABLED edit's frame in the light theme's pale grey, a bright
            // ring round a dark field. Repainted in the dark border colour, as the combos are.
            if (!_tb.Enabled && _tb.BorderStyle != BorderStyle.None)
            {
                if (m.Msg == WM_NCPAINT)
                {
                    IntPtr dc = GetWindowDC(Handle);
                    if (dc != IntPtr.Zero)
                        try { using Graphics g = Graphics.FromHdc(dc); PaintFrame(g); }
                        finally { ReleaseDC(Handle, dc); }
                    return;
                }
                if (m.Msg == WM_PRINT_ && m.WParam != IntPtr.Zero && ((long)m.LParam & PRF_NONCLIENT) != 0)
                {
                    using Graphics g = Graphics.FromHdc(m.WParam);
                    PaintFrame(g);
                    // falls through: the client repaint below covers WM_PRINT too
                }
            }
            // Message filter FIRST: _tb.Text sends WM_GETTEXTLENGTH back through this WndProc (infinite recursion).
            if (!IsPaintMessage(m.Msg) || _tb.Enabled || _tb.Multiline || _tb.Text.Length == 0
                || _tb.PasswordChar != '\0' || _tb.UseSystemPasswordChar) return;
            PaintOver(Handle, ref m, g =>
            {
                Rectangle area = Rectangle.Inflate(_tb.ClientRectangle, -1, -1);
                using (var brush = new SolidBrush(_tb.BackColor))
                    g.FillRectangle(brush, area);
                Rectangle textArea = new(area.X + 1, area.Y, Math.Max(0, area.Width - 2), area.Height);
                TextRenderer.DrawText(g, _tb.Text, _tb.Font, textArea, Dim(_tb),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                    | TextFormatFlags.EndEllipsis);
            });
        }

        /// <summary>The frame only (window coordinates): an outer ring in the border colour and, for the
        /// two-pixel 3-D border, an inner ring in the field colour so no light edge is left between.</summary>
        private void PaintFrame(Graphics g)
        {
            var r = new Rectangle(0, 0, _tb.Width - 1, _tb.Height - 1);
            if (r.Width < 2 || r.Height < 2) return;
            using (var border = new Pen(_comboBorder)) g.DrawRectangle(border, r);
            if (_tb.BorderStyle == BorderStyle.Fixed3D)
                using (var inner = new Pen(_tb.BackColor)) g.DrawRectangle(inner, Rectangle.Inflate(r, -1, -1));
        }
    }

    // ---- disabled ComboBox border + arrow button (dark only) ------------------------------------

    /// <summary>
    /// A flat ComboBox's border and drop-arrow button are drawn natively even when the face is dimmed
    /// by <see cref="ComboDrawItem"/>, so a disabled combo read as two tones. Like
    /// <see cref="DisabledTextBoxes"/>, this subclasses the one control (NativeWindow) and, after the
    /// native paint, repaints the border and button area in the disabled palette. Enabled = native only.
    /// </summary>
    private sealed class DisabledCombos : NativeWindow
    {
        private static readonly ConditionalWeakTable<ComboBox, DisabledCombos> Hooked = new();
        private readonly ComboBox _cb;
        private const int WM_PAINT = 0x000F;

        private DisabledCombos(ComboBox cb) { _cb = cb; }

        public static void Hook(ComboBox cb)
        {
            if (Hooked.TryGetValue(cb, out _)) return;   // Apply twice must not stack hooks
            var h = new DisabledCombos(cb);
            Hooked.Add(cb, h);
            cb.EnabledChanged += (_, _) => cb.Invalidate();
            cb.HandleCreated += (_, _) => h.AssignHandle(cb.Handle);
            cb.HandleDestroyed += (_, _) => h.ReleaseHandle();
            if (cb.IsHandleCreated) h.AssignHandle(cb.Handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMBOBOXINFO
        {
            public int cbSize;
            public RECT rcItem, rcButton;
            public int stateButton;
            public IntPtr hwndCombo, hwndItem, hwndList;
        }

        [DllImport("user32.dll")]
        private static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO info);

        /// <summary>The exact arrow-button rect at any DPI (and on the left under RightToLeft); falls
        /// back to a scroll-bar-width strip on the right if the call fails.</summary>
        private Rectangle ButtonRect(Rectangle r)
        {
            var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO)) };
            if (_cb.IsHandleCreated && GetComboBoxInfo(_cb.Handle, ref info))
            {
                var b = Rectangle.FromLTRB(info.rcButton.Left, info.rcButton.Top, info.rcButton.Right, info.rcButton.Bottom);
                if (b.Width > 2 && b.Height > 2) return b;
            }
            int w = SystemInformation.VerticalScrollBarWidth;
            return new Rectangle(r.Right - w - 1, r.Y + 1, w, r.Height - 2);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (!IsPaintMessage(m.Msg) || !_comboDark || _cb.Enabled || _cb.IsDisposed) return;
            // PaintOver handles WM_PAINT and WM_PRINT/WM_PRINTCLIENT, so DrawToBitmap sees it too.
            PaintOver(Handle, ref m, g =>
            {
                Rectangle r = _cb.ClientRectangle;
                if (r.Width < 8 || r.Height < 8) return;
                Rectangle button = ButtonRect(r);
                int w = button.Width;
                using (var back = new SolidBrush(_comboDisabledBack))
                    g.FillRectangle(back, button);
                using (var pen = new Pen(_comboBorder))
                    g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                // The flat combo also draws a light inner frame (and a divider next to the button): sink them.
                int divider = button.X > r.Width / 2 ? button.X - 1 : button.Right;
                using (var inner = new Pen(_comboDisabledBack))
                {
                    g.DrawRectangle(inner, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
                    g.DrawLine(inner, divider, r.Y + 1, divider, r.Bottom - 2);
                }
                // Dimmed chevron, centred in the button.
                using (var pen = new Pen(Dim(_comboFore, _comboDisabledBack), Math.Max(1f, w / 8f)))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    float cx = button.X + button.Width / 2f, cy = button.Y + button.Height / 2f, s = w / 5f;
                    g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
                }
            });
        }
    }

    // ---- TabControl + DateTimePicker (dark only) -----------------------------------------------
    //
    // Both are native controls that paint their own white chrome: the tab strip + page frame, and
    // the closed date/time field. Neither exposes a colour for it, so (like DisabledTextBoxes) a
    // NativeWindow lets the native paint run and then repaints the chrome over it. Handles WM_PAINT
    // and WM_PRINT/PRINTCLIENT so Control.DrawToBitmap (ThemePreview) sees the same result.

    private const int WM_PAINT_ = 0x000F, WM_PRINT_ = 0x0317, WM_PRINTCLIENT_ = 0x0318;
    private const int WM_NCPAINT = 0x0085, PRF_NONCLIENT = 0x0002;
    private const uint RDW_INVALIDATE = 0x0001, RDW_FRAME = 0x0400;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Runs <paramref name="paint"/> on the right Graphics for a paint-type message.</summary>
    private static bool IsPaintMessage(int msg) => msg == WM_PAINT_ || msg == WM_PRINT_ || msg == WM_PRINTCLIENT_;

    private static void PaintOver(IntPtr hwnd, ref Message m, Action<Graphics> paint)
    {
        if (m.Msg == WM_PAINT_)
        {
            using Graphics g = Graphics.FromHwnd(hwnd);
            paint(g);
        }
        else if ((m.Msg == WM_PRINT_ || m.Msg == WM_PRINTCLIENT_) && m.WParam != IntPtr.Zero)
        {
            using Graphics g = Graphics.FromHdc(m.WParam);
            paint(g);
        }
    }

    private static void AttachTo(Control c, NativeWindow w)
    {
        c.HandleCreated += (_, _) => w.AssignHandle(c.Handle);
        c.HandleDestroyed += (_, _) => w.ReleaseHandle();
        if (c.IsHandleCreated) w.AssignHandle(c.Handle);
    }

    private sealed class DarkTabs : NativeWindow
    {
        private static readonly ConditionalWeakTable<TabControl, DarkTabs> Hooked = new();
        private readonly TabControl _tab;
        private readonly Palette _p;

        private DarkTabs(TabControl tab, Palette p) { _tab = tab; _p = p; }

        public static void Hook(TabControl tab, Palette p)
        {
            if (Hooked.TryGetValue(tab, out _)) return;
            var h = new DarkTabs(tab, p);
            Hooked.Add(tab, h);
            tab.DrawMode = TabDrawMode.OwnerDrawFixed;
            tab.DrawItem += h.DrawTab;
            // OwnerDrawFixed tabs are one fixed size: make it fit the longest caption.
            void FitTabs()
            {
                int w = 0;
                foreach (TabPage page in tab.TabPages)
                    w = Math.Max(w, TextRenderer.MeasureText(page.Text, tab.Font).Width);
                tab.ItemSize = new Size(w + 28, tab.Font.Height + 10);
            }
            FitTabs();
            tab.FontChanged += (_, _) => FitTabs();
            AttachTo(tab, h);
        }

        private void DrawTab(object? sender, DrawItemEventArgs e)
        {
            bool sel = e.Index == _tab.SelectedIndex;
            using (var b = new SolidBrush(sel ? _p.Back : _p.GridHeader))
                e.Graphics.FillRectangle(b, e.Bounds);
            string text = _tab.TabPages[e.Index].Text;
            Color fore = sel ? _p.Fore : Blend(_p.Fore, _p.GridHeader, 0.75f);
            TextRenderer.DrawText(e.Graphics, text, _tab.Font, e.Bounds, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (_tab.TabCount == 0 || _tab.Alignment != TabAlignment.Top) return;
            PaintOver(Handle, ref m, Chrome);
        }

        private void Chrome(Graphics g)
        {
            Rectangle client = _tab.ClientRectangle;
            int stripBottom = 0;
            for (int i = 0; i < _tab.TabCount; i++)
            {
                Rectangle r = _tab.GetTabRect(i);
                stripBottom = Math.Max(stripBottom, r.Bottom);
            }
            using var back = new SolidBrush(_p.Back);
            using var border = new Pen(_p.Border);
            // Repaint the whole strip, then every tab, ourselves: the native tab edges stick out a
            // pixel or two past the owner-draw rectangle as a white outline otherwise.
            g.FillRectangle(back, 0, 0, client.Width, stripBottom);
            for (int i = 0; i < _tab.TabCount; i++)
            {
                Rectangle r = _tab.GetTabRect(i);
                bool sel = i == _tab.SelectedIndex;
                if (sel) r.Inflate(0, 1);
                using (var fill = new SolidBrush(sel ? _p.Back : _p.GridHeader))
                    g.FillRectangle(fill, r);
                g.DrawLines(border, new[] { new Point(r.Left, r.Bottom - 1), new Point(r.Left, r.Top),
                                            new Point(r.Right - 1, r.Top), new Point(r.Right - 1, r.Bottom - 1) });
                Color fore = sel ? _p.Fore : Blend(_p.Fore, _p.GridHeader, 0.75f);
                TextRenderer.DrawText(g, _tab.TabPages[i].Text, _tab.Font, r, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            // The page frame: the native themed bevel is a few px of white around the page area.
            Rectangle page = _tab.DisplayRectangle;
            Rectangle outer = new(0, stripBottom, client.Width, Math.Max(0, client.Height - stripBottom));
            g.FillRectangle(back, outer.Left, outer.Top, outer.Width, Math.Max(0, page.Top - outer.Top));
            g.FillRectangle(back, outer.Left, page.Bottom, outer.Width, Math.Max(0, outer.Bottom - page.Bottom));
            g.FillRectangle(back, outer.Left, page.Top, Math.Max(0, page.Left - outer.Left), page.Height);
            g.FillRectangle(back, page.Right, page.Top, Math.Max(0, outer.Right - page.Right), page.Height);
            g.DrawRectangle(border, outer.Left, outer.Top - 1, outer.Width - 1, outer.Height);
            // Open the frame under the selected tab so it reads as joined to its page.
            if (_tab.SelectedIndex >= 0)
            {
                Rectangle s = _tab.GetTabRect(_tab.SelectedIndex);
                g.FillRectangle(back, s.Left + 1, outer.Top - 1, Math.Max(0, s.Width - 1), 1);
            }
        }
    }

    private sealed class DarkDatePickers : NativeWindow
    {
        private static readonly ConditionalWeakTable<DateTimePicker, DarkDatePickers> Hooked = new();
        private readonly DateTimePicker _dtp;
        private readonly Palette _p;
        private UpDownSkin? _upDown;

        private DarkDatePickers(DateTimePicker dtp, Palette p) { _dtp = dtp; _p = p; }

        public static void Hook(DateTimePicker dtp, Palette p)
        {
            if (Hooked.TryGetValue(dtp, out _)) return;
            var h = new DarkDatePickers(dtp, p);
            Hooked.Add(dtp, h);
            dtp.EnabledChanged += (_, _) => { dtp.Invalidate(); h._upDown?.Invalidate(); };
            // The spinner child dies with the picker's handle; forget it so a recreated handle re-finds it.
            dtp.HandleDestroyed += (_, _) => { h._upDown?.ReleaseHandle(); h._upDown = null; };
            AttachTo(dtp, h);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_PAINT_ && m.Msg != WM_PRINT_ && m.Msg != WM_PRINTCLIENT_) return;
            if (_dtp.ShowUpDown && (_upDown == null || _upDown.Handle == IntPtr.Zero))
            {
                IntPtr child = FindWindowEx(Handle, IntPtr.Zero, "msctls_updown32", null);
                if (child != IntPtr.Zero)
                {
                    _upDown = new UpDownSkin(_p, () => _dtp.Enabled);
                    _upDown.AssignHandle(child);
                }
            }
            PaintOver(Handle, ref m, PaintField);
        }

        private string FieldText()
        {
            DateTime v = _dtp.Value;
            string? custom = _dtp.CustomFormat;
            // Custom with no format string is the native "G" display; don't throw out of WndProc.
            if (_dtp.Format == DateTimePickerFormat.Custom && string.IsNullOrEmpty(custom)) return v.ToString();
            return _dtp.Format switch
            {
                DateTimePickerFormat.Long => v.ToLongDateString(),
                DateTimePickerFormat.Short => v.ToShortDateString(),
                DateTimePickerFormat.Time => v.ToLongTimeString(),
                _ => v.ToString(custom!.Length == 1 ? "%" + custom : custom),
            };
        }

        private void PaintField(Graphics g)
        {
            string text;
            try { text = FieldText(); } catch (Exception) { return; }
            Rectangle r = _dtp.ClientRectangle;
            int right = r.Width;
            if (_dtp.ShowUpDown)
            {
                if (_upDown != null && _upDown.Handle != IntPtr.Zero && GetWindowRect(_upDown.Handle, out RECT ur))
                    right = _dtp.PointToClient(new Point(ur.Left, 0)).X;
                else
                    right = r.Width - SystemInformation.VerticalScrollBarWidth;   // spinner not found yet: leave its room alone
            }
            else right = r.Width - SystemInformation.VerticalScrollBarWidth;

            Color back = _dtp.Enabled ? _p.Field : _p.Back;
            Color fore = _dtp.Enabled ? _p.Fore : Dim(_p.Fore, back);
            Rectangle textArea = new(3, 1, Math.Max(0, right - 4), r.Height - 2);
            if (_dtp.Focused && _dtp.Enabled)
            {
                // Focused: leave the text to the native paint so its active-segment (hour/minute)
                // highlight stays visible; only dark the empty field to the right of the text.
                Size sz = TextRenderer.MeasureText(g, text, _dtp.Font, Size.Empty, TextFormatFlags.NoPadding);
                int textEnd = Math.Min(right, textArea.Left + sz.Width + 4);
                using var b = new SolidBrush(back);
                g.FillRectangle(b, textEnd, 0, Math.Max(0, right - textEnd), r.Height);
            }
            else
            {
                using (var b = new SolidBrush(back)) g.FillRectangle(b, 0, 0, right, r.Height);
                TextRenderer.DrawText(g, text, _dtp.Font, textArea, fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                    | TextFormatFlags.NoPadding);
            }

            if (!_dtp.ShowUpDown)
            {
                // Drop-down button: flat Button-coloured square with a chevron.
                var btn = new Rectangle(right, 0, r.Width - right, r.Height);
                using (var b = new SolidBrush(_p.Button)) g.FillRectangle(b, btn);
                DrawArrow(g, btn, fore, down: true);
            }
            using var pen = new Pen(_p.Border);
            g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
        }
    }

    /// <summary>The spinner child of a ShowUpDown DateTimePicker (class msctls_updown32).</summary>
    private sealed class UpDownSkin : NativeWindow
    {
        private readonly Palette _p;
        private readonly Func<bool> _enabled;
        public UpDownSkin(Palette p, Func<bool> enabled) { _p = p; _enabled = enabled; }

        public void Invalidate() { if (Handle != IntPtr.Zero) InvalidateRect(Handle, IntPtr.Zero, true); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_PAINT_ && m.Msg != WM_PRINT_ && m.Msg != WM_PRINTCLIENT_) return;
            PaintOver(Handle, ref m, g =>
            {
                if (!GetWindowRect(Handle, out RECT r)) return;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                bool on = _enabled();
                Color back = on ? _p.Button : _p.Back;
                Color arrow = on ? _p.Fore : Dim(_p.Fore, back);
                using (var b = new SolidBrush(back)) g.FillRectangle(b, 0, 0, w, h);
                DrawArrow(g, new Rectangle(0, 0, w, h / 2), arrow, down: false);
                DrawArrow(g, new Rectangle(0, h / 2, w, h - h / 2), arrow, down: true);
                using var pen = new Pen(_p.Border);
                g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
            });
        }
    }

    private static void DrawArrow(Graphics g, Rectangle box, Color color, bool down)
    {
        int cx = box.Left + box.Width / 2, cy = box.Top + box.Height / 2;
        int s = Math.Max(2, Math.Min(box.Width, box.Height) / 5);
        Point[] tri = down
            ? new[] { new Point(cx - s, cy - s / 2), new Point(cx + s, cy - s / 2), new Point(cx, cy + s - s / 2) }
            : new[] { new Point(cx - s, cy + s / 2), new Point(cx + s, cy + s / 2), new Point(cx, cy - s + s / 2) };
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, tri);
    }

    // ---- dark title bar (DWM) ------------------------------------------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;       // Win10 2004+ / Win11
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // early Win10 builds
    private const int DWMWA_CAPTION_COLOR = 35;                 // Win11 22000+

    private static void ApplyTitleBar(Form form, bool dark, Color back)
    {
        void Set()
        {
            // Immersive-dark-mode first — the fallback Win11 needs if DWMWA_CAPTION_COLOR
            // (below) isn't supported (older Win11 builds, or Win10 entirely).
            int on = dark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

            if (dark)
            {
                // COLORREF is 0x00BBGGRR — R in the low byte, unlike System.Drawing's ARGB.
                int colorRef = back.R | (back.G << 8) | (back.B << 16);
                DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
            }
            // Light mode: leave the title bar native — no attribute to reset it to "system
            // default" reliably across OS versions, and the light palette is untouched anyway.
        }

        try
        {
            if (form.IsHandleCreated) Set();
            else form.HandleCreated += (_, _) => Set();
        }
        catch
        {
            // DWM attribute unsupported on this OS — harmless.
        }
    }
}
