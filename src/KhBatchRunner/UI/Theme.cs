using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KhBatchRunner.UI;

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
        Color GridHeader, Color Accent, Color Link);

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
        Link: Color.FromArgb(105, 198, 242));

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
        Link: SystemColors.HotTrack);

    // Combo colors captured for the owner-draw handler.
    private static Color _comboBack, _comboFore, _comboSelect;

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

    public static void Apply(Form form)
    {
        // Pin the font explicitly: .NET Framework's WinForms default is Microsoft Sans Serif
        // 8.25pt, .NET 8's is Segoe UI 9pt — without this the Revit 2024 (net48) build renders
        // every dialog smaller/cramped. No-op on net8, where Segoe UI 9pt is already the default.
        form.Font = new Font("Segoe UI", 9f);

        bool dark = IsDark();
        Palette p = dark ? ForDark() : ForLight();
        _comboBack = p.Field;
        _comboFore = p.Fore;
        _comboSelect = p.Accent;

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
    public static Color Dim(Control c) => Blend(c.ForeColor, c.BackColor, 0.6f);

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
                }
                break;

            case TextBox or ListBox or CheckedListBox or TreeView:
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
                }
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
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? _comboSelect : _comboBack))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (e.Index >= 0)
        {
            string text = combo.GetItemText(combo.Items[e.Index]) ?? "";
            Color back = selected ? _comboSelect : _comboBack;
            Color fore = _comboFore;
            if (ComboDimming.TryGetValue(combo, out Func<int, bool>? dimmed) && dimmed(e.Index))
                fore = Dim(fore, back);
            TextRenderer.DrawText(e.Graphics, text, e.Font ?? combo.Font, e.Bounds, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
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
