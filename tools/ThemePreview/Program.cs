using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using KhBatchRunner.Settings;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KhBatchRunner.Forma;
using KhBatchRunner.Actions;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

namespace ThemePreview;

/// <summary>
/// Standalone WinForms harness: constructs KhBatchRunner's dialogs (RunActionWindow,
/// BatchRunWindow, ScheduleRunWindow) with minimal fake data, off-screen, and saves a PNG
/// screenshot of each — so a theme/layout change can be reviewed without an interactive
/// Revit session. Not shipped; see ThemePreview.csproj for the build/run commands. No user
/// interaction — reads <c>args[0]</c> ("dark"/"light") and <c>args[1]</c> (output folder),
/// writes the PNGs, and exits.
/// </summary>
internal static class Program
{
    private const string RevitDir = @"C:\Program Files\Autodesk\Revit 2026\";

    /// <summary>
    /// How many windows failed to build. The process exits non-zero when any did, which is what
    /// makes this usable as a build gate rather than something you have to read the output of.
    ///
    /// It was not a gate before, and that cost a release: 1.3.8.2 shipped a
    /// <see cref="ScheduleRunWindow"/> whose constructor dereferenced a field two lines before
    /// assigning it, so the window threw <c>NullReferenceException</c> every time — taking Schedule
    /// Run, Batch Run's "Save as schedule" and the controller's tile with it. This harness already
    /// constructed that exact window; it just printed FAILED and exited 0, and nothing ran it.
    /// </summary>
    private static int _failures;

    /// <summary>Construct every window and report, but render nothing — the gate mode. Passing
    /// <c>check</c> as the first argument selects it.</summary>
    private static bool _checkOnly;

    /// <summary>
    /// <c>icons &lt;dir&gt;</c>: every ribbon glyph in <see cref="Icons"/>, found by signature so a new
    /// one cannot be left out, on Revit's light and dark ribbon colours — each at 32px and 16px as
    /// the ribbon shows them, plus a 2× nearest-neighbour blow-up of the 32px to judge pixels by.
    /// Writes <c>icons-light.png</c> and <c>icons-dark.png</c>. Added in 1.5.1.4, whose first cut of
    /// the Revit-style icons went into Revit unseen and came back with a clipped arrowhead.
    /// </summary>
    private static int IconSheet(string outDir)
    {
        var glyphs = typeof(Icons).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(System.Windows.Media.Imaging.BitmapSource)
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(IconPalette))
            .OrderBy(m => m.MetadataToken)
            .ToList();

        const int cellW = 150, cellH = 104, cols = 8;
        int rows = (glyphs.Count + cols - 1) / cols;
        foreach ((string name, IconPalette palette, Color ribbon, Color text) in new[]
        {
            ("light", IconPalette.Light, Color.FromArgb(240, 240, 240), Color.FromArgb(40, 40, 40)),
            ("dark", IconPalette.Dark, Color.FromArgb(59, 68, 83), Color.FromArgb(230, 230, 230)),
        })
        {
            using var sheet = new Bitmap(cols * cellW, rows * cellH);
            using (Graphics g = Graphics.FromImage(sheet))
            using (var font = new Font("Segoe UI", 9f))
            using (var brush = new SolidBrush(text))
            {
                g.Clear(ribbon);
                for (int i = 0; i < glyphs.Count; i++)
                {
                    int x = i % cols * cellW + 6, y = i / cols * cellH + 6;
                    using Bitmap big = ToBitmap(Glyph(glyphs[i], palette, 32));
                    using Bitmap small = ToBitmap(Glyph(glyphs[i], palette, 16));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(big, x, y, 64, 64);
                    g.DrawImage(big, x + 72, y, 32, 32);
                    g.DrawImage(small, x + 112, y + 8, 16, 16);
                    g.DrawString(glyphs[i].Name, font, brush, x, y + 70);
                }
            }
            string path = Path.Combine(outDir, $"icons-{name}.png");
            sheet.Save(path, ImageFormat.Png);
            Console.WriteLine($"{glyphs.Count} glyphs -> {path}");
        }
        return 0;
    }

    private static System.Windows.Media.Imaging.BitmapSource Glyph(MethodInfo m, IconPalette p, int size) =>
        (System.Windows.Media.Imaging.BitmapSource)m.Invoke(null, new object[] { p, size })!;

    private static Bitmap ToBitmap(System.Windows.Media.Imaging.BitmapSource src)
    {
        var bmp = new Bitmap(src.PixelWidth, src.PixelHeight, PixelFormat.Format32bppArgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { src.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0, data.Stride * bmp.Height, data.Stride); }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        _checkOnly = args.Length > 0 &&
                     (args[0].Equals("check", StringComparison.OrdinalIgnoreCase) ||
                      args[0].Equals("-CheckOnly", StringComparison.OrdinalIgnoreCase));

        string theme = _checkOnly ? "dark" : args.Length > 0 ? args[0] : "dark";
        string outDir = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        // RevitAPI/RevitAPIUI are heavily native-interop assemblies, not pure managed code —
        // loading them by absolute path alone still fails ("module not found") because their
        // own native imports are normally found via the OS's DLL search order relative to
        // Revit.exe. Adding the install folder to the process's native search path first is
        // what actually makes them loadable outside Revit.
        SetDllDirectory(RevitDir);

        // Revit normally loads RevitAPI.dll/RevitAPIUI.dll itself; standalone, the CLR asks
        // us. Must be wired up before anything touches an KhBatchRunner type that references
        // Document/UIApplication/UIThemeManager in its signature (which type-loading can
        // require even for a branch that's never executed at runtime, e.g. Theme.IsDark()'s
        // env-var short-circuit still needs UIThemeManager/UITheme to be a resolvable type).
        AppDomain.CurrentDomain.AssemblyResolve += ResolveRevitAssemblies;

        // Theme.IsDark() honors this ahead of asking a (nonexistent, here) UIThemeManager.
        Environment.SetEnvironmentVariable("KHBATCH_FORCE_THEME", theme);

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length > 0 && args[0].Equals("dialog", StringComparison.OrdinalIgnoreCase))
            return DialogBreakerCheck();

        if (args.Length > 0 && args[0].Equals("layout", StringComparison.OrdinalIgnoreCase))
            return LayoutCheck(args.Any(a => a.Equals("--inject", StringComparison.OrdinalIgnoreCase)));

        if (args.Length > 0 && args[0].Equals("roundtrip", StringComparison.OrdinalIgnoreCase))
            return RoundTripCheck(args.Any(a => a.Equals("--inject", StringComparison.OrdinalIgnoreCase)));

        if (args.Length > 0 && args[0].Equals("catalog", StringComparison.OrdinalIgnoreCase))
            return CatalogCheck(args.Any(a => a.Equals("--inject", StringComparison.OrdinalIgnoreCase)));

        if (args.Length > 0 && args[0].Equals("dim", StringComparison.OrdinalIgnoreCase))
            return DimCheck(args.Any(a => a.Equals("--inject", StringComparison.OrdinalIgnoreCase)));

        if (args.Length > 0 && args[0].Equals("coverage", StringComparison.OrdinalIgnoreCase))
            return CoverageCheck(args.Any(a => a.Equals("--inject", StringComparison.OrdinalIgnoreCase)));

        if (args.Length > 0 && args[0].Equals("icons", StringComparison.OrdinalIgnoreCase))
            return IconSheet(outDir);

        // --seeded also applies to the RENDER path, so the greying an inaccessible set gets can
        // actually be looked at in a PNG. Without a reachable snapshot every set reads Unknown and
        // nothing is dimmed, which makes the screenshots useless for reviewing that specific change.
        if (args.Any(a => a.Equals("--seeded", StringComparison.OrdinalIgnoreCase)))
            AccountAccess.Seed(SeededSnapshot());

        foreach ((string name, Func<Form> make) in Windows())
            Shoot(make, Path.Combine(outDir, $"{name}_{theme}.png"));

        // SECOND construction pass, with a good account snapshot already cached.
        //
        // Everything above runs with no Forma token, so every AccountAccess snapshot comes back
        // unreachable and the "we already know the answer" branch of ModelSetAccess.BeginRefresh is
        // never taken. That branch is not a corner: it is what EVERY window after the first one
        // hits in a real session. 1.5.0 shipped a constructor-time NullReferenceException there —
        // the callback ran synchronously inside a panel's constructor and touched a status label
        // the window had not built yet — and this gate reported "every window built" throughout.
        //
        // The seeded snapshot is deliberately the awkward one: reachable (so the branch is taken),
        // carrying a PreviousUser (so StatusNote() is non-empty and the callback actually calls
        // back), and naming a project no saved set can match (so sets read NoAccess).
        if (_checkOnly)
        {
            Console.WriteLine("--- with a cached account snapshot (the path every window after the first takes) ---");
            AccountAccess.Seed(SeededSnapshot());
            try
            {
                foreach ((string name, Func<Form> make) in Windows())
                    Shoot(make, Path.Combine(outDir, $"{name}_seeded.png"));
            }
            finally
            {
                AccountAccess.Seed(null);
            }
        }

        if (_failures > 0)
        {
            Console.WriteLine($"FAILED: {_failures} window(s) could not be built.");
            return 1;
        }
        Console.WriteLine(_checkOnly
            ? "Window construction gate passed: every window built."
            : "All windows rendered.");
        return 0;
    }

    /// <summary>
    /// Proves a combo really DRAWS an inaccessible model set in dimmed text.
    ///
    /// <para><b>Why this needs its own check.</b> A screenshot cannot see it: a ComboBox popup is a
    /// separate window and never appears in <c>Form.DrawToBitmap</c>. 1.5.1 shipped the dimming
    /// "working" on that basis and it did nothing — the theme's own combo painter, which dark mode
    /// installs on every combo, repainted every row in one flat colour on top of it. Sorting worked,
    /// so the list looked half-right and the failure was easy to miss.</para>
    ///
    /// <para>So this raises the paint itself, for a dimmed row and an undimmed one, and compares
    /// the ink. Both themes, because dark and light take different paths through Theme.</para>
    /// </summary>
    private static int DimCheck(bool inject)
    {
        int problems = 0;
        foreach (string theme in new[] { "dark", "light" })
        {
            Environment.SetEnvironmentVariable("KHBATCH_FORCE_THEME", theme);

            using var form = new Form();
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 400 };
            combo.Items.Add("Reachable set  (2 models)");
            combo.Items.Add("Denied set  (2 models)");
            form.Controls.Add(combo);

            // Index 1 is dimmed; with --inject nothing is, which must make the two rows identical
            // and so must FAIL the comparison — that is how this gate proves it can fail at all.
            Theme.DimItemsWhere(combo, i => !inject && i == 1);
            Theme.Apply(form);

            int plain = InkOf(combo, 0);
            int dimmed = InkOf(combo, 1);
            Color back = theme == "dark" ? Color.FromArgb(45, 45, 48) : Color.White;
            int backInk = back.R + back.G + back.B;

            // "Dimmed" means the text moved TOWARDS the background, whichever direction that is:
            // light-on-dark gets darker, dark-on-light gets lighter.
            bool moved = Math.Abs(dimmed - backInk) < Math.Abs(plain - backInk) - 30;
            Console.WriteLine($"  {theme,-5} plain ink {plain,4}, dimmed ink {dimmed,4} → " +
                              (moved ? "dimmed" : "NOT dimmed"));
            if (!moved) problems++;
        }

        if (inject)
        {
            if (problems == 0)
            {
                Console.WriteLine("FAILED: injected a no-op dimming rule and the gate still passed.");
                return 1;
            }
            Console.WriteLine($"ok   [injected no-op dimming detected] ({problems} theme(s) undimmed)");
            return 0;
        }

        if (problems > 0)
        {
            Console.WriteLine($"FAILED: an inaccessible set is not dimmed in {problems} theme(s).");
            return 1;
        }
        Console.WriteLine("Dimming gate passed: an inaccessible set draws dimmer in both themes.");
        return 0;
    }

    /// <summary>
    /// The COVERAGE gate: no window may show model sets without the account treatment.
    ///
    /// <para><b>Why this exists.</b> The rule — an inaccessible set is labelled, dimmed and sorted
    /// last — was written once in <see cref="ModelSetAccess"/> and then applied window by window, by
    /// hand. Fifteen windows let you pick a model set and it reached eight. Batch Interop, Batch
    /// Link RVT, Correct Workset, Cut model, Initiate to Forma, Internal Relink and Publish Cloud
    /// Model each pick one from a combo, and each showed an unopenable set exactly like any other —
    /// which is how 1.5.1.2 was reported: the user saw the flag in Edit Model Set and nowhere else.
    /// The class comment listing which windows were covered was itself seven windows out of date. A
    /// list in prose is not a gate.</para>
    ///
    /// <para><b>Why it has to construct the windows.</b> Neither half is visible any other way. A
    /// screenshot cannot see it (a ComboBox popup is a separate window <c>DrawToBitmap</c> never
    /// captures — the same blind spot that let 1.5.1 ship dimming that did nothing), and grep cannot
    /// see it either, because a window builds its combo in a constructor and fills it eighty lines
    /// away. So this builds every window in <see cref="Windows"/> against a seeded account snapshot
    /// and two seeded sets — one reachable, one not — walks the real control tree, and for every list
    /// control actually holding model sets asserts both halves: registered for greying
    /// (<c>ModelSetAccess.IsGreyed</c>), and the denied row's own <c>ToString</c> carrying the
    /// verdict. A window absent from <see cref="Windows"/> is still checked by nothing; that list
    /// stays the one thing a new window has to be added to.</para>
    /// </summary>
    private static int CoverageCheck(bool inject)
    {
        // Its own settings root, so the gate reads two known sets instead of whatever this PC
        // happens to have saved — the same seam the round-trip gate uses.
        string root = Path.Combine(Path.GetTempPath(), "KhBatchCoverage-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable(SettingsPaths.RootOverrideVariable);
        Environment.SetEnvironmentVariable(SettingsPaths.RootOverrideVariable, root);

        int problems = 0, controls = 0, ignored = 0;
        string? previousTheme = Environment.GetEnvironmentVariable("KHBATCH_FORCE_THEME");
        try
        {
            PresetStore.Save(ReachableSet());
            PresetStore.Save(DeniedSet());
            AccountAccess.Seed(SeededSnapshot());

            // BOTH themes, every window, because the three control types take three different
            // painting routes and the theme changes two of them: a combo is owner-drawn by Theme
            // only in dark mode, and a CheckedListBox is drawn by the framework using colours the
            // theme sets. A pass in one theme is not evidence about the other.
            foreach (string theme in new[] { "dark", "light" })
            {
                Environment.SetEnvironmentVariable("KHBATCH_FORCE_THEME", theme);
                Console.WriteLine($"--- {theme} ---");
                bool countThisPass = theme == "dark";   // once, not once per theme

                foreach ((string name, Func<Form> make) in Windows())
                {
                    // The WHOLE thing is guarded, construction and paint alike. Showing a window is
                    // new here, and a window that builds but throws from a Shown/Load handler would
                    // otherwise escape this method entirely — through the finally, out of Main, as
                    // an unhandled exception. The layout gate wraps the same way.
                    try
                    {
                        using Form form = make();
                        // SHOWN, off-screen, before anything is measured. Not strictly required —
                        // CheckedListBox.OnDrawItem reads its glyph size through an overload that
                        // accepts a null handle — but it gives realistic DPI sizing and lets the
                        // form finish its layout, and it is the recipe the layout gate already
                        // proves out. A future reader who has to drop it can.
                        ShowOffScreen(form);
                        problems += countThisPass
                            ? Inspect(name, form, ref controls)
                            : Inspect(name, form, ref ignored);
                    }
                    catch (Exception ex)
                    {
                        // Not this gate's job to report a construction failure — the check gate
                        // already does, and reporting it twice would make one bug look like two.
                        Console.WriteLine($"skip {name}: did not construct or show ({ex.GetType().Name})");
                    }
                }
            }

            // Fault injection, the layout gate's way round: an EXTRA window that is deliberately
            // wrong, checked by the same code, rather than a flag that makes every window look
            // wrong. A mode which reports all sixteen as broken can only prove the detector runs,
            // and would mask a genuine sixteenth failure while it did it. This way one invocation
            // both proves the gate can fail and still checks the real windows.
            //
            // And the planted fault is THE BUG ITSELF: a raw CheckedListBox handed to
            // AttachGreying. It registers as greyed and it labels its rows correctly — through the
            // real ModelSetAccess.Label — so it sails past both of the checks this gate had when
            // the bug shipped, and it paints no dimming at all, because CheckedListBox.DrawMode has
            // an empty setter. Only ink can catch it. If this gate cannot, the gate is worth
            // nothing.
            //
            // Once, in dark: that is where the measurement is most hostile, because the native
            // check-box glyph is near-white on a dark field and swamps the text unless InkOf skips
            // it.
            if (inject)
            {
                int before = problems;
                Environment.SetEnvironmentVariable("KHBATCH_FORCE_THEME", "dark");
                using var broken = new Form();
                var undimmable = new CheckedListBox { Name = "injectedRawCheckedList", Width = 400 };
                undimmable.Items.Add(new InjectedSetItem(ReachableSet()));
                undimmable.Items.Add(new InjectedSetItem(DeniedSet()));
                ModelSetAccess.AttachGreying(undimmable, i => (i as InjectedSetItem)?.Preset);
                broken.Controls.Add(undimmable);
                Theme.Apply(broken);
                ShowOffScreen(broken);

                int found = Inspect("INJECTED", broken, ref ignored);
                if (found == 0)
                {
                    Console.WriteLine("FAILED: a raw CheckedListBox — the bug itself — was planted " +
                                      "and the gate did not notice.");
                    return 1;
                }
                Console.WriteLine($"ok   injected undimmable CheckedListBox detected ({found} problem(s))");
                // NOT counted as a defect — the injected window is the test of the checker, not a
                // fault in the product.
                problems = before;
            }
        }
        finally
        {
            AccountAccess.Seed(null);
            Environment.SetEnvironmentVariable("KHBATCH_FORCE_THEME", previousTheme);
            Environment.SetEnvironmentVariable(SettingsPaths.RootOverrideVariable, previousRoot);
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder */ }
        }

        // Zero controls is a broken gate reporting success — the failure mode of every check that
        // looks for problems in a list it forgot to fill.
        if (controls == 0)
        {
            Console.WriteLine("FAILED: no model-set list control found in any window. The gate is not looking at anything.");
            return 1;
        }
        if (problems > 0)
        {
            Console.WriteLine($"FAILED: {problems} model-set list control problem(s).");
            return 1;
        }
        Console.WriteLine($"Coverage gate passed: all {controls} model-set list control(s) are registered, " +
                          "labelled and drawn dimmer, in both themes.");
        return 0;
    }

    /// <summary>
    /// Checks one window's control tree and returns how many problems it has, counting the
    /// model-set lists it found into <paramref name="controls"/>.
    ///
    /// <para>Split out so the fault injection goes through the SAME code the windows do. A gate
    /// whose injected case takes a different path only proves that path works.</para>
    /// </summary>
    private static int Inspect(string window, Control root, ref int controls)
    {
        int problems = 0;
        foreach (Control c in Descendants(root))
        {
            if (c is not (ListBox or ComboBox)) continue;
            List<(object Item, ModelPreset Preset)> sets = SetItemsOf(c);
            if (sets.Count == 0) continue;
            controls++;

            if (!ModelSetAccess.IsGreyed(c))
            {
                Console.WriteLine($"  {window}.{NameOf(c)}: shows {sets.Count} model set(s) " +
                                  "and never went through ModelSetAccess.AttachGreying");
                problems++;
                continue;
            }

            // Greying without the label is half the rule, and the half a colour-blind reader
            // depends on — so it is checked separately rather than assumed to follow.
            foreach ((object item, ModelPreset preset) in sets)
            {
                if (AccountAccess.For(preset) != AccessVerdict.NoAccess) continue;
                string shown = item.ToString() ?? "";
                if (shown.IndexOf("no access", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Console.WriteLine($"  {window}.{NameOf(c)}: inaccessible set is unlabelled — \"{shown}\"");
                problems++;
            }

            problems += InkCheck(window, c, sets);
        }
        return problems;
    }

    /// <summary>
    /// Does this control actually PAINT an inaccessible set differently? Raises the control's own
    /// draw for a denied row and a reachable row and compares the ink.
    ///
    /// <para><b>The check the other two could not make.</b> Registration proves a rule was wired to
    /// a control; the label proves a string was built. Neither says a pixel changed. 1.5.1.2 passed
    /// both on three controls that painted nothing, because <c>CheckedListBox.DrawMode</c> has an
    /// empty setter and its <c>OnDrawItem</c> never raises the event — so the owner-draw handler
    /// registered against them never ran. Only ink can tell you that.</para>
    /// </summary>
    private static int InkCheck(string window, Control c, List<(object Item, ModelPreset Preset)> sets)
    {
        int denied = -1, plain = -1;
        for (int i = 0; i < sets.Count; i++)
        {
            bool no = AccountAccess.For(sets[i].Preset) == AccessVerdict.NoAccess;
            if (no && denied < 0) denied = i;
            if (!no && plain < 0) plain = i;
        }

        // A list holding only one kind cannot be compared — and reporting that as a pass is the
        // silent-success shape this whole gate exists to stop. The seeded settings root and
        // PreviewPresets are built to give every list one of each; if one has not got both, that is
        // a fault in the gate's fixtures and it must be fixed, not skipped.
        if (denied < 0 || plain < 0)
        {
            Console.WriteLine($"  {window}.{NameOf(c)}: cannot compare ink — " +
                              $"{(denied < 0 ? "no inaccessible" : "no accessible")} set in the list. " +
                              "Fix the gate's fixture (PreviewPresets / DeniedSet); do not skip it.");
            return 1;
        }

        int inkDenied = InkOf(c, denied);
        int inkPlain = InkOf(c, plain);
        int back = c.BackColor.R + c.BackColor.G + c.BackColor.B;

        // "Dimmed" means the text moved TOWARDS the background, whichever direction that is:
        // light-on-dark gets darker, dark-on-light gets lighter.
        if (Math.Abs(inkDenied - back) < Math.Abs(inkPlain - back) - 30) return 0;

        Console.WriteLine($"  {window}.{NameOf(c)}: an inaccessible set is NOT drawn dimmer " +
                          $"(ink {inkDenied} vs {inkPlain}, background {back}) — " +
                          $"{c.GetType().Name} paints no dimming");
        return 1;
    }

    /// <summary>
    /// Total ink of the most extreme text pixel one row of <paramref name="c"/> paints, by raising
    /// the control's own draw for that item into a bitmap.
    ///
    /// <para><b>Only the text.</b> The left edge is skipped on a check-box list: the themed glyph is
    /// near-white on a dark field, far more extreme than any text, and measuring it would report
    /// every row identical. The window is narrow and fixed so both rows are measured over the same
    /// span of their names, whatever their lengths.</para>
    ///
    /// <para>Reflection reaches the PROTECTED <c>OnDrawItem</c>. Taking the method off
    /// <see cref="ListBox"/>/<see cref="ComboBox"/> is enough — it is virtual, so
    /// <see cref="System.Reflection.MethodInfo.Invoke"/> dispatches to whatever override the real
    /// control has, which is the entire point: this exercises the painter that is actually
    /// installed, not the one we hoped was.</para>
    /// </summary>
    private static int InkOf(Control c, int index)
    {
        int height = c is ListBox list ? Math.Max(list.ItemHeight, 12) : 20;
        var bounds = new Rectangle(0, 0, Math.Max(c.Width, 200), height);

        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(c.BackColor);
            var e = new DrawItemEventArgs(g, c.Font, bounds, index, DrawItemState.None,
                                          c.ForeColor, c.BackColor);
            Type owner = c is ComboBox ? typeof(ComboBox) : typeof(ListBox);
            owner.GetMethod("OnDrawItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                 .Invoke(c, new object[] { e });
        }

        int left = c is CheckedListBox ? height + 6 : 1;   // past the check box
        int right = Math.Min(bounds.Width - 2, left + 120); // a span both names reach
        int backInk = c.BackColor.R + c.BackColor.G + c.BackColor.B;
        int furthest = backInk;
        for (int x = left; x < right; x++)
            for (int y = 0; y < bmp.Height; y++)
            {
                Color px = bmp.GetPixel(x, y);
                int ink = px.R + px.G + px.B;
                if (Math.Abs(ink - backInk) > Math.Abs(furthest - backInk)) furthest = ink;
            }
        return furthest;
    }

    /// <summary>Shows a form off-screen so its controls have handles and a completed layout.</summary>
    private static void ShowOffScreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-5000, -5000);
        form.ShowInTaskbar = false;
        form.Show();
        for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(15); }
    }

    /// <summary>
    /// The injected list's item: a model set behind a wrapper, exactly like the seven private
    /// <c>PresetItem</c> classes.
    ///
    /// <para>It labels itself properly, through the same <c>ModelSetAccess.Label</c> every window
    /// uses. That is deliberate — the injected control must fail on the INK check and nothing else,
    /// or the injection would prove the label check works and say nothing about the new one.</para>
    /// </summary>
    private sealed class InjectedSetItem
    {
        public ModelPreset Preset { get; }
        public InjectedSetItem(ModelPreset preset) => Preset = preset;
        public override string ToString() => ModelSetAccess.Label(Preset, Preset.Name);
    }

    /// <summary>
    /// The items of <paramref name="list"/> that stand for a model set, paired with it.
    ///
    /// <para>By reflection, because every window wraps a preset in its own private item class
    /// (<c>PresetItem</c>, <c>SetItem</c>, <c>ModelSetItem</c>) — which is a large part of why a
    /// shared rule kept being applied by hand. Anything exposing a <see cref="ModelPreset"/> counts,
    /// so a window that invents an eighth wrapper is caught without touching this.</para>
    /// </summary>
    private static List<(object Item, ModelPreset Preset)> SetItemsOf(Control list)
    {
        var found = new List<(object, ModelPreset)>();
        IEnumerable<object> items = list switch
        {
            ComboBox combo => combo.Items.Cast<object>(),
            ListBox box => box.Items.Cast<object>(),
            _ => Enumerable.Empty<object>()
        };

        foreach (object item in items)
        {
            if (item is ModelPreset direct) { found.Add((item, direct)); continue; }
            foreach (PropertyInfo prop in item.GetType()
                         .GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (prop.PropertyType != typeof(ModelPreset)) continue;
                if (prop.GetValue(item) is ModelPreset preset) found.Add((item, preset));
                break;
            }
        }
        return found;
    }

    /// <summary>A control's field name where it has one, else its type — so the message says WHICH
    /// list in the window, since several windows have more than one.</summary>
    private static string NameOf(Control c) =>
        string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name;

    /// <summary>A saved set in the project <see cref="SeededSnapshot"/> can enumerate.</summary>
    private static ModelPreset ReachableSet() => new()
    {
        Name = "Gate Reachable Set",
        Models = new List<CloudModel>
        {
            new() { DisplayName = "OK-A.rvt", DmProjectId = "b.gate-project", HubId = "b.gate-hub" }
        }
    };

    /// <summary>A saved set in a project it cannot — the row every window must flag.</summary>
    private static ModelPreset DeniedSet() => new()
    {
        Name = "Gate Denied Set",
        Models = new List<CloudModel>
        {
            new() { DisplayName = "DENIED-A.rvt", DmProjectId = "b.gate-other-project", HubId = "b.gate-hub" }
        }
    };

    /// <summary>
    /// Total ink (summed RGB) of the text pixels one combo row paints, by raising the control's own
    /// draw for that item into a bitmap. Lower means darker; the caller compares against the
    /// background rather than assuming a direction.
    /// </summary>
    private static int InkOf(ComboBox combo, int index)
    {
        var bounds = new Rectangle(0, 0, combo.Width, 20);
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            var e = new DrawItemEventArgs(g, combo.Font, bounds, index, DrawItemState.None,
                                          combo.ForeColor, combo.BackColor);
            // The control raises its own DrawItem, so this exercises whatever handlers are actually
            // subscribed — which is the entire point, since the bug was a second handler winning.
            typeof(ComboBox).GetMethod("OnDrawItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(combo, new object[] { e });
        }

        // The most extreme pixel away from the row's own background is the text.
        Color background = bmp.GetPixel(bounds.Width - 2, bounds.Height / 2);
        int backInk = background.R + background.G + background.B;
        int furthest = backInk;
        for (int x = 0; x < bmp.Width; x++)
            for (int y = 0; y < bmp.Height; y++)
            {
                Color px = bmp.GetPixel(x, y);
                int ink = px.R + px.G + px.B;
                if (Math.Abs(ink - backInk) > Math.Abs(furthest - backInk)) furthest = ink;
            }
        return furthest;
    }

    /// <summary>
    /// The account snapshot the seeded passes install: deliberately the awkward one.
    ///
    /// <para><b>Reachable</b>, so <c>ModelSetAccess.BeginRefresh</c> takes its already-know-the-answer
    /// branch. <b>Carrying a PreviousUser</b>, so <c>StatusNote()</c> is non-empty and the callback
    /// actually calls back — with an empty note it returns early and proves nothing. And naming
    /// hub/project ids <b>no real saved set can match</b>, so every Forma set reads
    /// <c>NoAccess</c> and the dimming/ordering is visible.</para>
    /// </summary>
    private static AccessSnapshot SeededSnapshot() => new()
    {
        Reachable = true,
        SignedInUser = "gate-user@example.invalid",
        PreviousUser = "someone-else@example.invalid",
        HubIds = { "b.gate-hub" },
        ProjectIds = { "b.gate-project" },
    };

    /// <summary>
    /// The settings-catalog gate: every settings folder a <c>*Store</c> class actually writes to
    /// must be described by <see cref="SettingsCatalog"/>, or be listed there as deliberately not
    /// settings.
    ///
    /// <para><b>Why a gate and not a code review.</b> Import/export is only as complete as that
    /// catalog. A twelfth saved-set store added later would keep working perfectly — and would be
    /// silently absent from every bundle, which nobody discovers until a restored PC is missing one
    /// tool's sets. The stores are the source of truth; this asserts the list has kept up with
    /// them, the same way the window list is the input to both window gates.</para>
    ///
    /// <para>Discovery is by reflection over the shipped assembly rather than a second hand-written
    /// list, because a hand-written list of stores would have exactly the drift problem this
    /// exists to catch.</para>
    /// </summary>
    private static int CatalogCheck(bool inject)
    {
        int problems = 0;
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // GetTypes() on this assembly throws outside Revit: most of it is Revit-typed and RevitAPIUI
        // cannot fully load on a PC without Revit. ReflectionTypeLoadException still carries every
        // type that DID load, and the stores are Revit-free by construction, so they are all here.
        Type?[] types;
        try { types = typeof(ModelPreset).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        foreach (Type? candidate in types)
        {
            if (candidate is not { IsClass: true } type) continue;
            if (!type.Name.EndsWith("Store", StringComparison.Ordinal)) continue;

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (prop.PropertyType != typeof(string)) continue;
                if (prop.Name.IndexOf("Directory", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string? path;
                try { path = prop.GetValue(null) as string; }
                catch { continue; /* a store that cannot resolve its folder here is not this gate's business */ }
                if (string.IsNullOrWhiteSpace(path)) continue;

                string leaf = new DirectoryInfo(path!).Name;
                found.Add(leaf);
                Console.WriteLine($"  store {type.Name,-24} -> {leaf}");
            }
        }

        if (found.Count == 0)
        {
            Console.WriteLine("FAILED: no *Store directory properties were discovered — the gate found nothing to check.");
            return 1;
        }

        // The injected name stands in for a store someone adds later and forgets to catalogue.
        if (inject) found.Add("InjectedSets");

        foreach (string leaf in found)
        {
            if (SettingsCatalog.Covers(leaf)) continue;
            if (SettingsCatalog.NonSettingsFolders.Contains(leaf, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  ok   {leaf} is listed as deliberately not settings");
                continue;
            }
            Console.WriteLine($"  PROBLEM: '{leaf}' is written by a *Store but is not in SettingsCatalog — " +
                              "it would be missing from every exported bundle.");
            problems++;
        }

        // The other direction: a catalogued folder whose store was renamed or removed.
        foreach (SettingsCategory category in SettingsCatalog.All())
        {
            if (category.Kind != SettingsCategoryKind.SetFolder) continue;
            if (found.Contains(category.PathName)) continue;
            Console.WriteLine($"  PROBLEM: SettingsCatalog lists '{category.PathName}' but no *Store writes there.");
            problems++;
        }

        if (inject)
        {
            // Fault injection proves the gate can fail. It is only useful if it DID.
            if (problems == 0)
            {
                Console.WriteLine("FAILED: injected an uncatalogued store and the gate did not notice.");
                return 1;
            }
            Console.WriteLine($"ok   [injected uncatalogued store detected] ({problems} problem(s))");
            return 0;
        }

        if (problems > 0)
        {
            Console.WriteLine($"FAILED: {problems} settings-catalog problem(s).");
            return 1;
        }
        Console.WriteLine($"Settings-catalog gate passed: {found.Count} store folder(s), all accounted for.");
        return 0;
    }

    /// <summary>
    /// Every shared dialog this harness covers, as ONE list.
    ///
    /// <para>It used to be fourteen straight-line <see cref="Shoot"/> calls. A list because three
    /// modes now walk it — screenshots, the construction gate and the layout gate — and a window
    /// added to one of three copies is a window two gates never see.</para>
    /// </summary>
    private static IEnumerable<(string Name, Func<Form> Make)> Windows()
    {
        var client = new DataManagementClient(new SessionTokenProvider());
        yield return ("RunActionWindow_Check", () => BuildRunActionWindow(ActionCategory.Check));
        yield return ("RunActionWindow_Action", () => BuildRunActionWindow(ActionCategory.Action));
        yield return ("BatchRunWindow", () => new BatchRunWindow(client));
        yield return ("ScheduleRunWindow", () => new ScheduleRunWindow(client));
        yield return ("AboutWindow", () => new AboutWindow());
        yield return ("InitiateWindow", () => new InitiateWindow(PreviewPresets(), client));
        yield return ("BatchInteropWindow", () => new BatchInteropWindow(PreviewPresets(), "2026"));
        yield return ("ScheduleInteropWindow", () => new ScheduleInteropWindow(client, "2026"));
        yield return ("ExportNwcWindow", () => new ExportNwcWindow());
        yield return ("BatchLinkRvtWindow", () => new BatchLinkRvtWindow(PreviewPresets(), client));
        // 1.4.7 closes the hole the previous comment here admitted to. Relink and Correct Workset
        // "are still missing from this list, which is how a window skips both gates entirely" — and
        // 1.4.7 puts a Duplicate button in all four of these, which is exactly the kind of edit that
        // must not land in a window nothing checks. None carries an edition #if, so all four
        // construct in the Full build the gates run against.
        yield return ("CutModelWindow", () => new CutModelWindow(PreviewPresets(), client));
        yield return ("CorrectWorksetWindow", () => new CorrectWorksetWindow(PreviewPresets(), client));
        yield return ("RelinkModelWindow", () => new RelinkModelWindow(PreviewPresets(), client));
        yield return ("CheckSheetPublishWindow",
            () => new CheckSheetPublishWindow(client, new ModelDerivativeClient(new SessionTokenProvider())));
        // ARCHIVED (see App.cs: C4R publish cannot work with the SSONET session token), so it is on
        // no ribbon — and listed here anyway. The source still ships and still picks a model set, so
        // it is exactly the kind of window that quietly falls out of a rule while nobody looks at it.
        yield return ("PublishCloudModelWindow", () => new PublishCloudModelWindow(PreviewPresets(), client));
        yield return ("RebuildMatrixWindow", () => new RebuildMatrixWindow());
        yield return ("RebuildMatrixWindow-InteropSummary", () => new RebuildMatrixWindow(interopSummary: true));
        yield return ("FormaBrowserWindow", () => new FormaBrowserWindow(client));
        yield return ("FormaPickerWindow", () => new FormaPickerWindow(client));
        // Its two Forma siblings were listed and it was not, so the extract dialog's layout was never
        // checked by anything — and it now carries a folder list, a saved-set row and the scope
        // chooser, which is what the layout gate caught starving at minimum size.
        yield return ("FormaExtractWindow", () => new FormaExtractWindow(client));
        // Listed with the window, not after it: its rows are all fixed-height and its lists are
        // combos, so it is LESS likely to starve than its siblings — which is exactly the argument
        // that kept FormaExtractWindow off this list until a layout bug proved it wrong.
        yield return ("DataExtractWindow",
            () => new DataExtractWindow(client, new DataConnectorClient(new SessionTokenProvider())));
        // Listed as it was written, not afterwards. 1.4.7's lesson was that a window missing from
        // this list is checked by NOTHING — and this one is a percent-sized list above a percent-
        // sized report box, the exact shape the layout gate caught starving in CheckSheetPublishWindow.
        yield return ("SettingsTransferWindow", () => new SettingsTransferWindow());
        // The details window every status strip can now open. Its fixture is deliberately the shape
        // that created it — a multi-line save failure — because a one-line body would render a
        // dialog that never has to scroll, and prove nothing about the one that does.
        yield return ("MessageDetailsWindow",
            () => new MessageDetailsWindow("Schedule Run",
                "SAVE FAILED: Could not create scheduled task:\n" +
                "This PC will not let the scheduled-run launcher start a program, so the schedule " +
                "has NOT been registered — it would have failed when it fired.\n\n" +
                "Every installed copy was tried:\n" +
                "  C:\\Users\\Someone\\AppData\\Roaming\\Autodesk\\Revit\\Addins\\2026\\KhBatchLauncher.exe\n" +
                "      Access is denied"));
        // DRW Helper's dialogs. They carry no #if EDITION_DRW, so they construct in the Full build
        // the gates run against — and "Cancel" shipped shorter than "Create views" because nothing
        // looked at them.
        yield return ("DrwEnlargeRoomWindow", () => new KhBatchRunner.Drw.Dialogs.EnlargeWindow(
            DrwEnlargeFixture(isStair: false), new KhBatchRunner.Drw.Enlarge.EnlargePreset(),
            KhBatchRunner.Drw.Enlarge.EnlargeStores.Room));
        yield return ("DrwEnlargeStairWindow", () => new KhBatchRunner.Drw.Dialogs.EnlargeWindow(
            DrwEnlargeFixture(isStair: true), new KhBatchRunner.Drw.Enlarge.EnlargePreset(),
            KhBatchRunner.Drw.Enlarge.EnlargeStores.Stair));
        yield return ("DrwDatumDimensionWindow", () => new KhBatchRunner.Drw.Dialogs.DatumDimensionWindow(
            new List<string> { "Linear Dimension Style : Diagonal - 2.5mm Arial" },
            new Dictionary<string, double?> { ["Linear Dimension Style : Diagonal - 2.5mm Arial"] = 8.0 },
            "Level 1", 3, new KhBatchRunner.Drw.Annotation.DatumDimensionPreset()));
        yield return ("DrwMatchDatumsWindow", () => new KhBatchRunner.Drw.Dialogs.MatchDatumsWindow(
            "Level 1", "Plan", 101, "Scope Box 1",
            Enumerable.Range(1, 14).Select(i => new KhBatchRunner.Drw.Dialogs.MatchDatumsWindow.Choice(
                200 + i, $"Level {i + 1}" + (i % 3 == 0 ? " - Furniture Plan with a longer name" : ""), "Plan",
                i % 2 == 0 ? 101 : -1)).ToList(),
            new long[] { 202, 205 }));
        yield return ("DrwSingleFontWindow", () => new KhBatchRunner.Drw.Dialogs.SingleFontWindow(
            new List<string> { "Arial", "Arial Narrow", "Tahoma", "Segoe UI" },
            new Dictionary<string, int> { ["TextTypes"] = 24, ["TagFamilies"] = 64, ["ModelFamilies"] = 312 },
            new KhBatchRunner.Drw.Annotation.SingleFontPreset()));
        yield return ("DrwStairAnnotationWindow", () => new KhBatchRunner.Drw.Dialogs.StairAnnotationWindow(
            new List<string> { "Automatic Up/Down Direction : Standard", "Fixed Up Direction : Standard" },
            new List<string> { "Linear - 2.5mm Arial", "Linear - 2mm Arial" },
            new Dictionary<string, double?> { ["Linear - 2.5mm Arial"] = 8.0, ["Linear - 2mm Arial"] = null },
            "Level 1", 3,
            new KhBatchRunner.Drw.Annotation.StairAnnotationPreset()));
        yield return ("DrwPresetTaggerWindow", () => new KhBatchRunner.Drw.Dialogs.PresetTaggerWindow(
            DrwPresetTaggerFixture(), new KhBatchRunner.Drw.Annotation.PresetTaggerPreset()));
        yield return ("DrwWallMatTagWindow", () => new KhBatchRunner.Drw.Dialogs.WallMatTagWindow(
            DrwWallMatTagFixture(), new KhBatchRunner.Drw.Annotation.WallMatTagPreset()));
        yield return ("DrwDoorWindowWindow", () => new KhBatchRunner.Drw.Dialogs.DoorWindowWindow(
            DrwDoorWindowFixture(), new KhBatchRunner.Drw.DoorWindow.DoorWindowPreset()));
        // Parameter Helper (1.5.2). Both take a host interface instead of a Document so they build here.
        yield return ("ParameterRulesWindow", () => new ParameterRulesWindow(new PreviewRulesHost(), PreviewRuleSet()));
        yield return ("FormulaRuleDialog", () => new FormulaRuleDialog(new PreviewRulesHost(), PreviewRuleSet().Rules[0]));
        yield return ("FormulaRuleDialog_TypeName", () => new FormulaRuleDialog(new PreviewRulesHost(), new KhBatchRunner.ParamFormula.FormulaRule
        {
            TargetName = "Type Name", TargetGuid = "type-name", TargetOnType = true,
            Formula = "\"Generic - \" & round([Width] / 1 mm) & \"mm\"", Categories = new List<string> { "OST_Walls" }
        }));
        // The editor at its worst: twelve categories, the type-name and shared-parameter notes both
        // showing, and a long red error under the formula. The OK/Cancel row must stay reachable.
        yield return ("FormulaRuleDialog_Stress", () => new FormulaRuleDialog(new PreviewRulesHost(
            "Inconsistent units: [Unconnected Height] is a length, the formula gives area. Write the unit on the number, " +
            "e.g. 100 mm or 2.5 m². [Fire Rating Classification Description] is ambiguous: 2 parameters have this name on " +
            "the type — pick it again in the rule. Unexpected text at character 118: …] & \"x\" ]]"),
            new KhBatchRunner.ParamFormula.FormulaRule
            {
                TargetName = "Type Name", TargetGuid = "type-name", TargetOnType = true, Formula = "[Width] * [Height] +",
                Categories = new List<string> { "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_Doors", "OST_Windows",
                    "OST_GenericModel", "OST_Furniture", "OST_StructuralColumns", "OST_StructuralFraming", "OST_Columns", "OST_CurtainWallPanels" }
            }));
        // The real failure: the editor opens small and tidy, THEN the user picks twelve categories and
        // types a formula that errors. Grown after Show(), as it happens in Revit.
        yield return ("FormulaRuleDialog_GrowsAfterOpen", () =>
        {
            var host = new PreviewRulesHost(grow: true);
            var dlg = new FormulaRuleDialog(host, new KhBatchRunner.ParamFormula.FormulaRule
            {
                TargetName = "Panel Area", Formula = "[Length]", Categories = new List<string> { "OST_Walls" }
            });
            dlg.Shown += (_, _) => dlg.SimulateEdit(new List<string> { "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings",
                "OST_Doors", "OST_Windows", "OST_GenericModel", "OST_Furniture", "OST_StructuralColumns", "OST_StructuralFraming",
                "OST_Columns", "OST_CurtainWallPanels" }, "[Width] * [Height] +");
            return dlg;
        });
        // A check that throws (anything but a formula error) must never escape the dialog.
        yield return ("FormulaRuleDialog_CheckThrows", () => new FormulaRuleDialog(new PreviewRulesHost(checkThrows: true),
            new KhBatchRunner.ParamFormula.FormulaRule { TargetName = "Panel Area", Formula = "[Length]", Categories = new List<string> { "OST_Walls" } }));
        // Mirror Check (1.5.3): the model before set-up, and a set-up central with flags out of date.
        yield return ("MirrorCheckWindow_NotSetUp", () => new MirrorCheckWindow(new PreviewMirrorHost(ready: false)));
        yield return ("MirrorCheckWindow", () => new MirrorCheckWindow(new PreviewMirrorHost(ready: true)));
        yield return ("MirrorCheckWindow_OwnParameter", () => new MirrorCheckWindow(new PreviewMirrorHost(ready: true, saved: "3f2a1c9e-1111-4000-8000-000000000001")));
        yield return ("MirrorCheckWindow_ChosenMissing", () => new MirrorCheckWindow(new PreviewMirrorHost(ready: true, missing: true)));
        yield return ("FormulaRuleDialog_TypeTarget", () => new FormulaRuleDialog(new PreviewRulesHost(), new KhBatchRunner.ParamFormula.FormulaRule
        {
            TargetName = "Type Area", TargetOnType = true, Formula = "[Width] * 3 m", Categories = new List<string> { "OST_Walls" }
        }));
    }

    private static KhBatchRunner.ParamFormula.FormulaRuleSet PreviewRuleSet()
    {
        var set = new KhBatchRunner.ParamFormula.FormulaRuleSet { Live = true };
        set.Rules.Add(new KhBatchRunner.ParamFormula.FormulaRule
        {
            TargetName = "Panel Area", Formula = "[Length] * [Unconnected Height]",
            Categories = new List<string> { "OST_Walls", "OST_CurtainWallPanels" }, Note = "Net panel area for the facade schedule"
        });
        set.Rules.Add(new KhBatchRunner.ParamFormula.FormulaRule
        {
            TargetName = "Fire Label", Formula = "if([Fire Rating] >= 120, \"FR-2H\", \"FR-1H\") & \" / \" & [type:Type Mark]",
            Categories = new List<string> { "OST_Doors" }, Enabled = false, TargetOnType = true
        });
        return set;
    }

    /// <summary>What a model would answer, for the Parameter Helper windows: a few parameters of each
    /// kind, one name shared by two parameters so the qualified labels are in the render.</summary>
    private sealed class PreviewRulesHost : IParameterRulesHost
    {
        private readonly string? _checkMessage;
        private readonly bool _checkThrows;
        private readonly bool _grow;

        public PreviewRulesHost(string? checkMessage = null, bool checkThrows = false, bool grow = false)
        {
            _checkMessage = checkMessage;
            _checkThrows = checkThrows;
            _grow = grow;
        }

        private const string LongError =
            "Inconsistent units: [Unconnected Height] is a length, the formula gives area. Write the unit on the number, " +
            "e.g. 100 mm or 2.5 m². [Fire Rating Classification Description] is ambiguous: 2 parameters have this name on " +
            "the type — pick it again in the rule. Unexpected text at character 118: …] & \"x\" ]]";

        public string ModelTitle => "Tower A - ARC";
        public bool IsWorkshared => true;
        public string? RulesBorrowedBy => "someone.else";
        public string CategoryLabel(string categoryName) => categoryName.StartsWith("OST_") ? categoryName.Substring(4) : categoryName;
        public List<KhBatchRunner.ParamFormula.ParamChoice> Targets(IList<string> categories) => new()
        {
            new() { Name = "Panel Area", Kind = "area", Label = "Panel Area  (area)" },
            new() { Name = "Comments", Kind = "text", Label = "Comments  (text)" },
            new() { Name = "Fire Label", Kind = "text", Guid = "3f2a1c9e-0000-0000-0000-000000000000", Label = "Fire Label  (shared 3f2a1c9e · text)" },
            new() { Name = "Fire Label", Kind = "text", Label = "Fire Label  (project/built-in · text)" },
            new() { Name = "Type Area", Kind = "area", OnType = true, Label = "Type Area  (type · area)" },
            new() { Name = "Type Name", Kind = "text, renames the type", OnType = true, Guid = "type-name", Label = "Type Name  (type · text, renames the type)" },
        };
        public List<KhBatchRunner.ParamFormula.ParamChoice> Sources(IList<string> categories) => new()
        {
            new() { Name = "Length", Kind = "length", Label = "Length  (length)" },
            new() { Name = "Unconnected Height", Kind = "length", Label = "Unconnected Height  (length)" },
            new() { Name = "Width", Kind = "length", OnType = true, Label = "Width  (type · length)" },
        };
        public (bool Ok, string Message) Check(KhBatchRunner.ParamFormula.FormulaRule rule) =>
            _checkThrows ? throw new InvalidOperationException("Revit refused to format this value (a simulated failure).")
            : _grow && rule.Formula.EndsWith("+") ? (false, LongError)
            : _checkMessage != null ? (false, _checkMessage)
            : (true, "OK — gives area. Example, Generic - 200mm (id 412233): 7.500 m²");
        public KhBatchRunner.ParamFormula.ApplyResult Preview(KhBatchRunner.ParamFormula.FormulaRuleSet set) => new();
        public void ShowResults(string title, KhBatchRunner.ParamFormula.ApplyResult result, bool dryRun) { }
        public string LiveActivity => "14:02:11  3 value(s) recalculated in the last edit (4 ms)";
        public List<string> EmptyCategories(IList<string> categories) => categories.Count > 1 ? new List<string> { categories[^1] } : new List<string>();
        public (bool Saved, bool ChangedSinceOpened, string? Why) TrySave(KhBatchRunner.ParamFormula.FormulaRuleSet set, string openedJson, bool replace) =>
            (true, false, null);
    }

    private sealed class PreviewMirrorHost : IMirrorCheckHost
    {
        private const string Office = "3f2a1c9e-1111-4000-8000-000000000001";
        private bool _ready;
        private readonly string _saved;
        private readonly bool _missing;
        public PreviewMirrorHost(bool ready, string saved = "", bool missing = false) { _ready = ready; _saved = saved; _missing = missing; }
        public string ModelTitle => "Tower A - ARC";
        public List<KhBatchRunner.ParamFormula.MirrorTarget> Targets() => new()
        {
            new() { Name = "Door Handing", Guid = Office, Categories = 2, Varies = false },
            new() { Name = "Fire Label with a much longer shared parameter name", Guid = "3f2a1c9e-1111-4000-8000-000000000002", Categories = 125, Varies = true },
            new() { Name = "QA Note", Guid = "3f2a1c9e-1111-4000-8000-000000000003", Categories = 40, Varies = true },
        };
        public KhBatchRunner.ParamFormula.MirrorStatus Status(string? targetGuid)
        {
            string t = _missing ? "" : targetGuid ?? _saved;
            bool own = t.Length == 0;
            return new()
            {
                TargetGuid = t,
                MissingTarget = _missing ? "Door Handing" : null,
                TargetMissingCategories = own ? 0 : 123,
                TargetVaries = own,
                ParamsReady = own ? _ready : true,
                ParamsText = (_missing ? "'Door Handing', chosen for this model, is no longer in it — KH_Mirror State is used instead. " : "") +
                    (own ? (_ready ? "Set up: KH_Mirror State on 125 categories." : "KH_Mirror State is not set up in this model yet.")
                         : "'Door Handing' is on 2 of 125 model categories and does not vary between group instances (grouped elements are skipped)."),
                Live = _ready,
                Counts = new KhBatchRunner.ParamFormula.MirrorCounts
                {
                    Instances = 18342, Mirrored = 214, HandFlipped = 1093, FacingFlipped = 877, Flagged = 1706,
                    OutOfDate = _ready ? 37 : 0, NoParameters = own ? (_ready ? 12 : 18342) : 15001
                }
            };
        }
        public string? SetUp() { _ready = true; return null; }
        public KhBatchRunner.ParamFormula.ApplyResult Preview(string targetGuid) => new();
        public void ShowResults(string title, KhBatchRunner.ParamFormula.ApplyResult result, bool dryRun) { }
        public string LiveActivity => _ready ? "14:02:11  Mirror Check: 2 element(s) updated in the last edit (3 ms)" : "";
        public (bool Saved, string? Why) Save(bool live, string targetGuid, string targetName, bool extend) => (true, null);
    }

    /// <summary>What the Enlarge dialog is given in Revit, with two parameters sharing a name so
    /// the qualified dropdown labels are in the render too.</summary>
    private static KhBatchRunner.Drw.Dialogs.PresetTaggerDialogData DrwPresetTaggerFixture() => new()
    {
        Categories = new List<(string, string)> { ("OST_Doors", "Doors"), ("OST_Windows", "Windows"), ("OST_Rooms", "Rooms"),
            ("OST_Walls", "Walls"), ("OST_Furniture", "Furniture") },
        TagTypes = new Dictionary<string, List<string>>
        {
            ["OST_Doors"] = new() { "M_Door Tag : Standard", "Door Fire Rating Tag with a much longer family name : 60 min" },
            ["OST_Windows"] = new() { "M_Window Tag : Standard" },
            ["OST_Rooms"] = new() { "M_Room Tag : Room Tag With Area", "M_Room Tag : Room Tag" },
            ["OST_Walls"] = new() { "M_Wall Tag : 6mm" },
            ["OST_Furniture"] = new()
        },
        ActiveView = "Level 1 - Floor Plan", SelectedViews = 3
    };

    private static KhBatchRunner.Drw.Dialogs.DoorWindowDialogData DrwDoorWindowFixture() => new()
    {
        Items = new()
        {
            new() { Mark = "D1", Category = "Door", Size = "900 x 2100 mm", Qty = 24, Types = "M_Single-Flush : 0915 x 2134mm",
                WidthMm = 900, HeightSillMm = 2100, PlanWidthMm = 2200 },
            new() { Mark = "D2", Category = "Door", Size = "1800 x 2100 mm", Qty = 6, Types = "M_Double-Flush : 1830 x 2134mm, M_Double-Glass : 1830 x 2134mm",
                Warnings = "Type Mark D2 merges 2 types of the same size.", WidthMm = 1800, HeightSillMm = 2100, PlanWidthMm = 3100 },
            new() { Mark = "D3", Category = "Door", Size = "3000 x 2400 mm", Qty = 2, Types = "Sliding Door : 3000 x 2400",
                WidthMm = 3000, HeightSillMm = 2400, PlanWidthMm = 5600 },
            new() { Mark = "W1", Category = "Window", Size = "2500 x 2800 mm", Qty = 12, Types = "Sliding : 2500 x 2800",
                WidthMm = 2500, HeightSillMm = 3400, PlanWidthMm = 3700 },
            new() { Mark = "W2", Category = "Window", Size = "800 x 500 mm", Qty = 8, Types = "Awning : 800 x 500",
                WidthMm = 800, HeightSillMm = 2300, PlanWidthMm = 2000 },
            new() { Mark = "W3", Category = "Window", Size = "2000 x 1500 mm", Qty = 10, Types = "Sliding : 2000 x 1500",
                WidthMm = 2000, HeightSillMm = 2400, PlanWidthMm = 3200 },
            new() { Mark = "W4", Category = "Window", Size = "800 x 2000 mm", Qty = 4, Types = "Fixed : 800 x 2000",
                WidthMm = 800, HeightSillMm = 2600, PlanWidthMm = 2000 }
        },
        Warnings = new() { "2 window type(s) have no Type Mark and are left out: Fixed : 600 x 600, Fixed : 900 x 600" },
        DimensionTypes = new() { "Linear - 2.5mm Arial", "Linear - 2mm Arial" },
        TitleBlocks = new()
        {
            // An A3 with a 10 mm border and a 40 mm title strip along the bottom (the A-12 example).
            new KhBatchRunner.Drw.DoorWindow.DwTitleBlockData
            {
                Name = "A3 Thai : A3", WidthMm = 420, HeightMm = 297,
                Frame = new KhBatchRunner.Drw.DoorWindow.DwFrame(10, 50, 410, 287),
                Lines = new() { (10, 10, 410, 10), (410, 10, 410, 287), (410, 287, 10, 287), (10, 287, 10, 10), (10, 50, 410, 50),
                                (130, 10, 130, 50), (250, 10, 250, 50), (330, 10, 330, 50) }
            }
        }
    };

    private static KhBatchRunner.Drw.Dialogs.WallMatTagDialogData DrwWallMatTagFixture() => new()
    {
        MaterialTagTypes = new() { "M-Material Wall Tag : Up", "M-Material Wall Tag : Down", "M-Material Wall Tag : Left",
            "M-Material Wall Tag : Right", "M_Material Tag : Standard" },
        ActiveView = "Level 1 - Floor Plan", SelectedViews = 3
    };

    private static KhBatchRunner.Drw.Dialogs.EnlargeDialogData DrwEnlargeFixture(bool isStair) => new()
    {
        IsStair = isStair,
        PlanTypes = new() { "Floor Plan : Floor Plan" },
        SectionTypes = new() { "Section : Building Section", "Section : Wall Section" },
        ElevationTypes = new() { "Elevation : Interior Elevation" },
        PlanTemplates = new() { "Enlarged Plan" },
        SectionTemplates = new() { "Enlarged Section" },
        ElevationTemplates = new() { "Interior Elevation" },
        Targets = isStair
            ? new() { "ST-1", "ST-2", "ST-3 (multistory)", "ST-3-1", "ST-3-2" }
            : new() { "101 Kitchen", "102 Toilet", "103 Store", "201 Office" },
        Preselected = new() { 1 },
        FromSelection = 1,
        ViewParameters = new()
        {
            new KhBatchRunner.Drw.Dialogs.ParamChoice("Drawing Type  (Project · Text)", "Drawing Type", ""),
            new KhBatchRunner.Drw.Dialogs.ParamChoice("Drawing Type  (Shared · Text · 3f2a1c9e)", "Drawing Type", "3f2a1c9e-0000-0000-0000-000000000000"),
            new KhBatchRunner.Drw.Dialogs.ParamChoice("Title on Sheet  (Text)", "Title on Sheet", ""),
        },
    };

    /// <summary>
    /// The dummy sets handed to the windows that take a preset list rather than reading the store.
    ///
    /// <para>TWO of them, and the second is deliberately unopenable: a set of purely local models
    /// reads <c>Accessible</c> under any snapshot, so a list holding only "Preview Set" can never
    /// show the account greying — not in a screenshot, and not to the coverage gate. The denied one
    /// names a project <see cref="SeededSnapshot"/> does not list, which is what makes both
    /// visible.</para>
    /// </summary>
    private static List<ModelPreset> PreviewPresets() => new()
    {
        new ModelPreset
        {
            Name = "Preview Set",
            Models = new List<CloudModel>
            {
                new() { DisplayName = "ARC-Tower-A.rvt", LocalPath = @"C:\fake\ARC-Tower-A.rvt" },
                new() { DisplayName = "ARC-Tower-B.rvt", LocalPath = @"C:\fake\ARC-Tower-B.rvt" }
            }
        },
        DeniedSet()
    };

    /// <summary>One dummy two-model preset, reused for both the Check and Action screenshots.</summary>
    private static RunActionWindow BuildRunActionWindow(ActionCategory category)
    {
        // PreviewPresets(), not a fixture built here: it carries the inaccessible set, and a list
        // holding only reachable ones gives the coverage gate's ink check nothing to compare
        // against. This window built its own and was the one control the check could not judge.
        string actionName = category == ActionCategory.Action ? "Purge Import CAD" : "Get Phase & Workset";
        return new RunActionWindow(PreviewPresets(), actionName, category);
    }

    /// <summary>
    /// FAULT INJECTION for <see cref="KhBatchRunner.Batch.ForeignDialogBreaker"/>: raises a real
    /// Win32 message box — the same class of window (<c>#32770</c>) the Navisworks exporter put up
    /// on 2026-08-15 — and checks that the breaker finds it, reads its words, and closes it.
    ///
    /// <para>A watchdog nobody has watched fire is not a watchdog. This is the same discipline the
    /// forced-close gate is held to, and it runs on a PC with no Revit because the breaker is plain
    /// Win32 with no Revit types in it. Run: <c>dotnet run --project tools\ThemePreview -- dialog</c>.</para>
    /// </summary>
    private static int DialogBreakerCheck()
    {
        int box = MessageBoxCheck();
        if (box != 0) return box;

        // Sequential, never overlapping: each injection needs its OWN breaker so that ClosedCount is
        // evidence about the breaker under test. Run together, the first one answers the second's
        // dialog and the second reports zero — which is exactly how this check first failed.
        int taskDialog = TaskDialogCheck();
        if (taskDialog != 0) return taskDialog;

        Console.WriteLine("Foreign dialog breaker check passed.");
        return 0;
    }

    /// <inheritdoc cref="DialogBreakerCheck"/>
    private static int MessageBoxCheck()
    {
        const string caption = "Navisworks NWC Exporter";
        const string body = "No suitable geometry found. Please check what part you want to export.";

        // Answer on the next poll: the point here is the mechanism, not the 45-second grace (which
        // ForeignDialogBreaker.ShouldAct is pinned on in the LivenessSim harness).
        Environment.SetEnvironmentVariable(KhBatchRunner.Batch.ForeignDialogBreaker.GraceEnvVar, "0");

        using var breaker = new KhBatchRunner.Batch.ForeignDialogBreaker(
            line => Console.WriteLine("  " + line));

        var blocked = new Thread(() => MessageBoxW(IntPtr.Zero, body, caption, 0))
        { IsBackground = true, Name = "FaultInjectedDialog" };
        blocked.Start();

        // The thread stays inside MessageBox until something clicks OK, so its exit IS the proof.
        bool dismissed = blocked.Join(TimeSpan.FromSeconds(30));
        string captured = KhBatchRunner.Batch.ForeignDialogBreaker.LastMessage;

        if (!dismissed)
        {
            Console.WriteLine("FAILED: the message box was still up after 30s — the breaker did not close it.");
            return 1;
        }
        if (breaker.ClosedCount < 1)
        {
            Console.WriteLine("FAILED: the box closed but the breaker did not count it.");
            return 1;
        }
        if (captured.IndexOf("No suitable geometry", StringComparison.Ordinal) < 0 ||
            captured.IndexOf(caption, StringComparison.Ordinal) < 0)
        {
            Console.WriteLine($"FAILED: the breaker closed a dialog without keeping its words: '{captured}'");
            return 1;
        }

        Console.WriteLine($"ok   dialog answered and quoted: {captured}");
        return 0;
    }

    /// <summary>
    /// The SECOND shape, added 1.4.2: a comctl32 <c>TaskDialog</c> whose only button is
    /// <b>Close</b> — which is what Revit raises, and what stopped a run.
    ///
    /// <para>Report <c>BKKLH3C7QV3_20260818_0801</c>, journal.1309: at 03:10:30 Revit's startup
    /// warning flush put up <c>TaskDialog_Default_Family_Template_File_Invalid</c>
    /// ("The path specified for default family templates is empty or invalid.",
    /// <c>CommonButtons : Ok</c>, rendered with a Close button). Idling never ticked again, so the
    /// batch never began — and the breaker was only ever constructed once a batch began. 1.4.2 starts
    /// it at <c>App.OnStartup</c> for unattended sessions instead.</para>
    ///
    /// <para>Worth injecting separately from the message box above because it is a DIFFERENT
    /// control set. The affirmative-button match has to cover "Close" and not just "OK", and the
    /// body text lives in a <c>DirectUIHWND</c> rather than in <c>Static</c> children — so this
    /// check requires the CAPTION to be captured and treats the body as best effort. A breaker that
    /// answers a dialog it cannot fully quote is still the difference between a run and a night
    /// lost.</para>
    /// </summary>
    private static int TaskDialogCheck()
    {
        const string caption = "Default Family Template File Invalid";
        const string instruction = "The path you have specified for the default family template files is invalid.";

        Environment.SetEnvironmentVariable(KhBatchRunner.Batch.ForeignDialogBreaker.GraceEnvVar, "0");
        KhBatchRunner.Batch.ForeignDialogBreaker.TakeLastMessage();   // start from clean

        using var breaker = new KhBatchRunner.Batch.ForeignDialogBreaker(
            line => Console.WriteLine("  " + line));

        // WinForms' TaskDialog rather than a raw P/Invoke to comctl32: TaskDialog is exported only by
        // the v6 side-by-side comctl32, and a bare DllImport resolves the v5 in System32 and fails
        // with EntryPointNotFound even after EnableVisualStyles. This wrapper carries the activation
        // context, and produces the same #32770 window with the same standard Close button.
        string? raiseError = null;
        var blocked = new Thread(() =>
        {
            try
            {
                var page = new TaskDialogPage
                {
                    Caption = caption,
                    Heading = instruction,
                    Text = "To change the default file path, from the Application menu select " +
                           "Options, and then the File Locations tab.",
                    Buttons = { TaskDialogButton.Close },
                };
                TaskDialog.ShowDialog(page);
            }
            catch (Exception ex) { raiseError = ex.Message; }
        })
        { IsBackground = true, Name = "FaultInjectedTaskDialog" };
        blocked.SetApartmentState(ApartmentState.STA);
        blocked.Start();

        bool dismissed = blocked.Join(TimeSpan.FromSeconds(30));
        string captured = KhBatchRunner.Batch.ForeignDialogBreaker.LastMessage;

        if (raiseError != null)
        {
            Console.WriteLine($"FAILED: the TaskDialog could not be raised ({raiseError}) — this " +
                              "check proves nothing until it can be.");
            return 1;
        }
        if (!dismissed)
        {
            Console.WriteLine("FAILED: the Close-only TaskDialog was still up after 30s — the " +
                              "breaker did not answer it. This is the dialog that cost the " +
                              "2026-08-18 run.");
            return 1;
        }
        if (breaker.ClosedCount < 1)
        {
            Console.WriteLine("FAILED: the TaskDialog closed but the breaker did not count it.");
            return 1;
        }
        if (captured.IndexOf(caption, StringComparison.Ordinal) < 0)
        {
            Console.WriteLine($"FAILED: the TaskDialog was answered without keeping its caption: '{captured}'");
            return 1;
        }

        Console.WriteLine($"ok   Close-only TaskDialog answered and quoted: {captured}");
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    /// <summary>
    /// Builds a form off-screen, pumps the message loop just long enough for it to paint,
    /// captures it to a PNG via <see cref="Control.DrawToBitmap"/>, then closes it. Any
    /// single dialog failing to construct/paint is reported and skipped rather than aborting
    /// the rest of the run.
    /// </summary>
    private static void Shoot(Func<Form> build, string path)
    {
        try
        {
            using Form form = build();

            // The gate stops here: building the window is the whole test, and rendering needs a
            // desktop this may not have. A window that throws in its own constructor never
            // reaches this line.
            if (_checkOnly)
            {
                Console.WriteLine("ok   " + Path.GetFileNameWithoutExtension(path));
                return;
            }

            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-5000, -5000); // off-screen — no visible flicker
            form.ShowInTaskbar = false;
            form.Show();

            for (int i = 0; i < 15; i++)
            {
                Application.DoEvents();
                Thread.Sleep(30);
            }

            using var bmp = new Bitmap(Math.Max(form.Width, 1), Math.Max(form.Height, 1));
            form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(path, ImageFormat.Png);
            Console.WriteLine("Wrote " + path);

            form.Close();
        }
        catch (Exception ex)
        {
            // COUNTED, not just printed — see _failures.
            _failures++;
            Console.WriteLine($"FAILED {Path.GetFileNameWithoutExtension(path)}: {ex}");
        }
    }

    // ---- the layout gate --------------------------------------------------------------------

    /// <summary>A list shorter than this, docked to fill, is not a list any more — it is the
    /// symptom. MEASURED from the 2026-08-15 screenshot: Schedule Run's ad-hoc and actions columns
    /// had been squeezed to about one visible row while the dialog still "worked".</summary>
    private const int StarvedFillHeight = 48;

    /// <summary>
    /// FAULT-CHECKED layout gate: every shared dialog is shown off-screen and inspected for the four
    /// ways a control becomes unreadable — text wider than the control, siblings on top of each
    /// other, a control outside its parent, and a fill control starved to nothing.
    ///
    /// <para><b>Why it exists.</b> On 2026-08-15 two dialogs shipped unreadable: Export NWC's
    /// "Model set to run over:" label was clipped to half its height with its list crushed under the
    /// row below, and Schedule Run's ad-hoc and actions columns were squeezed to one row with the
    /// selection summary cut off. Both had passed the construction gate, every harness case and three
    /// build gates — because constructing a window says nothing about whether you can read it.</para>
    ///
    /// <para><b>Why it must Show().</b> WinForms runs no layout pass until the handle exists, and
    /// <c>FormLayout.AlignLabels</c> hangs off <see cref="Form.Shown"/>. The construction gate's
    /// deliberate refusal to show is exactly why it could not have caught this.</para>
    ///
    /// <para><b>Why two sizes.</b> These faults appear when content stops fitting, so the dialog's
    /// MinimumSize is where they surface first — <c>ScheduleRunWindow</c>'s own comment records that
    /// "a render at MinimumSize is what showed it" the last time.</para>
    /// </summary>
    /// <param name="inject">Adds a deliberately broken form to the sweep and INVERTS the result for
    /// it: the gate fails if the checker does not flag it. The dialog breaker shipped broken once
    /// because nobody had planted a fault in front of it; this is that lesson applied here.</param>
    // ---------------------------------------------------------------------------------------
    // ROUND-TRIP GATE (1.4.0)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// One schedule window, and what it is responsible for carrying between a job and its form.
    /// </summary>
    /// <param name="Owned">The <see cref="ScheduledJob"/> properties this window is RESPONSIBLE for
    /// round-tripping. Everything else it either derives (Cut always writes a single Cut RunItem;
    /// GroupSize follows the disposition) or never touches (chunk bookkeeping, LastRunAt). Listing
    /// the owned set per window is the point of the exercise as much as the checking is: before
    /// this, nothing anywhere said which window was answerable for which field.</param>
    private sealed record RoundTripCase(
        string Window,
        Func<Form> Make,
        Func<ScheduledJob, bool> Fixture,
        Action<Form, ScheduledJob> Load,
        Func<Form, (bool Ok, ScheduledJob? Job)> Build,
        string[] Owned,
        Func<ScheduledJob?> Synthetic);

    /// <summary>
    /// A job every window can accept without depending on machine state: its models are AD-HOC, so
    /// it needs no saved preset at all.
    ///
    /// <para>The saved schedules on a development machine name model-set presets that were deleted
    /// long ago, so they load into a form that then refuses to build — which would leave this gate
    /// checking nothing. A synthetic job with ad-hoc models is self-contained, and the fields under
    /// test (name, disposition, destination, recurrence, weekly days, time) are the same ones
    /// either way. Real saved jobs are still preferred when they work; this is the floor.</para>
    /// </summary>
    private static ScheduledJob SyntheticBase(string name) => new()
    {
        Id = "roundtripfixture0000000000000001",
        Name = name,
        AdHocModels = new List<CloudModel>
        {
            new() { DisplayName = "RT-Model-A.rvt", LocalPath = @"C:\RoundTrip\RT-Model-A.rvt" },
            new() { DisplayName = "RT-Model-B.rvt", LocalPath = @"C:\RoundTrip\RT-Model-B.rvt" },
        },
        Completion = BatchCompletion.CloseDocuments,
        DestinationFolder = @"C:\RoundTrip\Logs",
        ProjectLogName = "Round trip",
        Time = "23:17",
        Recurrence = ScheduleRecurrence.Weekly,
        WeeklyDays = new List<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Friday },
    };

    private static readonly string[] CommonOwned =
    {
        "Id", "Name", "ModelSetNames", "AdHocModels", "Completion",
        "DestinationFolder", "ProjectLogName", "Time", "Recurrence", "StartDate", "WeeklyDays"
    };

    /// <summary>Name of the throwaway File Log set the ScheduleRunWindow fixture needs. Written
    /// to the store before the cases run and deleted afterwards — see EnsureFileLogFixture.</summary>
    private const string FileLogFixtureSet = "ZZ round-trip fixture (delete me)";

    /// <summary>The same, for the Get Data Extract step — see <see cref="EnsureDataExtractFixture"/>.</summary>
    private const string DataExtractFixtureSet = "ZZ round-trip extract fixture (delete me)";

    /// <summary>
    /// Creates the saved File Log set the ScheduleRunWindow fixture depends on, and returns an
    /// action that removes it again.
    ///
    /// <para>Get File Log was chosen as this window's fixture item precisely because it needed no
    /// saved input set. 1.4.5 gave it one — the folders live in the set now — so the window
    /// refuses to build a job without one, and the fixture started SKIPPING, which took the gate's
    /// own fault-injection down with it. A fixture set restores the check rather than weakening it:
    /// the round trip now also proves the new field survives.</para>
    /// </summary>
    private static Action EnsureFileLogFixture()
    {
        var set = new FileLogInputSet
        {
            Name = FileLogFixtureSet,
            IncludeSubfolders = true,
            Spec = new FileLogSpec { Scope = FileLogScope.RevitOnly, LatestVersionOnly = true },
            DestinationFolder = @"C:\RoundTrip\Logs",
            Folders =
            {
                new FolderRef
                {
                    Name = "RT-Folder", DisplayPath = "RT Hub / RT Project / RT-Folder",
                    HubId = "b.rt-hub", ProjectId = "b.rt-project", FolderUrn = "urn:rt:folder",
                },
            },
        };
        bool existed = FileLogSetStore.Exists(set.Name);
        try { FileLogSetStore.Save(set); } catch { /* read-only profile: the case simply skips */ }
        return () =>
        {
            if (existed) return;
            try { FileLogSetStore.Delete(set.Name); } catch { /* best effort */ }
        };
    }

    /// <summary>
    /// The Get Data Extract twin of <see cref="EnsureFileLogFixture"/>: Schedule Run refuses to
    /// build a job carrying that step without a saved set, so without a fixture the round-trip case
    /// below would report SKIPPED and check nothing — including the new
    /// <c>DataExtractInputs</c> field it exists to pin.
    /// </summary>
    private static Action EnsureDataExtractFixture()
    {
        var set = new DataExtractInputSet
        {
            Name = DataExtractFixtureSet,
            HubId = "b.rt-hub",
            HubName = "RT Hub",
            RequestId = "rt-request-0001",
            RequestName = "RT extract",
            // NON-default on purpose, both of them: a window that dropped these fields entirely
            // would still round-trip false→false and true→true and the gate would call it ok.
            DownloadZipInstead = true,
            SkipIfAlreadyDownloaded = false,
            DestinationFolder = @"C:\RoundTrip\Extract",
        };
        bool existed = DataExtractSetStore.Exists(set.Name);
        try { DataExtractSetStore.Save(set); } catch { /* read-only profile: the case simply skips */ }
        return () =>
        {
            if (existed) return;
            try { DataExtractSetStore.Delete(set.Name); } catch { /* best effort */ }
        };
    }

    private static IEnumerable<RoundTripCase> RoundTripCases()
    {
        var client = new DataManagementClient(new SessionTokenProvider());

        yield return new RoundTripCase("ScheduleRunWindow",
            () => new ScheduleRunWindow(client),
            _ => true,
            (f, j) => ((ScheduleRunWindow)f).LoadJobIntoForm(j, true),
            f => { bool ok = ((ScheduleRunWindow)f).TryBuildJob(out ScheduledJob? j); return (ok, j); },
            CommonOwned.Concat(new[]
                { "ModelSetIds", "RunItems", "Sync", "FileLogInputs", "DataExtractInputs" }).ToArray(),
            () =>
            {
                // Get File Log, because it is the one item this window adds UNCONDITIONALLY. The
                // registry-driven actions come from HostServices.ListActions(), which is empty
                // outside Revit — a fixture naming one of those would silently check nothing here.
                // Since 1.4.5 the step also needs a saved set, which EnsureFileLogFixture provides.
                ScheduledJob j = SyntheticBase("RT Run");
                j.RunItems = new List<RunItem>
                    { new(FileLogStep.DisplayName, RunItemKind.Action) };
                j.Sync = SyncMode.NoSync;
                j.FileLogInputs = FileLogSetStore.LoadAll()
                    .FirstOrDefault(s => s.Name == FileLogFixtureSet);
                return j;
            });

        // A SECOND case for the same window, because the first one's fixture carries no data
        // extract: a job's REST-only steps are independent, and a window that loaded the file log
        // correctly while dropping the extract would pass the case above untouched.
        yield return new RoundTripCase("ScheduleRunWindow (data extract)",
            () => new ScheduleRunWindow(client),
            // Saved jobs are matched by the step they carry, so a real overnight extract schedule
            // on this PC is used as a fixture in preference to the synthetic one.
            j => j.RunItems.Any(DataExtractStep.IsDataExtractItem),
            (f, j) => ((ScheduleRunWindow)f).LoadJobIntoForm(j, true),
            f => { bool ok = ((ScheduleRunWindow)f).TryBuildJob(out ScheduledJob? j); return (ok, j); },
            CommonOwned.Concat(new[]
                { "ModelSetIds", "RunItems", "Sync", "FileLogInputs", "DataExtractInputs" }).ToArray(),
            () =>
            {
                ScheduledJob j = SyntheticBase("RT Extract");
                j.RunItems = new List<RunItem>
                    { new(DataExtractStep.DisplayName, RunItemKind.Action) };
                j.Sync = SyncMode.NoSync;
                j.DataExtractInputs = DataExtractSetStore.LoadAll()
                    .FirstOrDefault(s => s.Name == DataExtractFixtureSet);
                return j;
            });

        yield return new RoundTripCase("ScheduleInteropWindow",
            () => new ScheduleInteropWindow(client, "2026"),
            j => j.ActionNames.Count == 1 && j.ActionNames[0] == BatchInteropAction.DisplayName,
            (f, j) => ((ScheduleInteropWindow)f).LoadJobIntoForm(j, true),
            f => { bool ok = ((ScheduleInteropWindow)f).TryBuildJob(out ScheduledJob? j); return (ok, j); },
            CommonOwned.Concat(new[] { "InteropChecksetPath", "InteropChecksetExaminesLinks" }).ToArray(),
            () =>
            {
                ScheduledJob j = SyntheticBase("RT Interop");
                j.RunItems = new List<RunItem>
                    { new(BatchInteropAction.DisplayName, RunItemKind.Action) };
                // A URL, deliberately: the window accepts http without touching the filesystem,
                // so this fixture needs no checkset file to exist anywhere.
                j.InteropChecksetPath = "https://example.invalid/roundtrip-checkset.xml";
                // TRUE, and that matters: false is this flag's default, so a window that dropped it
                // entirely would still round-trip false→false and the gate would report ok. The
                // fixture has to carry the NON-default value or it proves nothing. Same reason the
                // round-trip gate uses an injected wrong value as well as a fixed point.
                j.InteropChecksetExaminesLinks = true;
                j.Sync = SyncMode.NoSync;
                return j;
            });

        // 2.0.0: Internal Relink and Correct Workset moved INTO this window (they had a window of
        // their own). A third case so a set that stopped round-tripping fails here, not in a run.
        yield return new RoundTripCase("ScheduleRunWindow (relink / correct workset)",
            () => new ScheduleRunWindow(client),
            j => j.RelinkSpec != null || j.CorrectWorksetSpec != null,
            (f, j) => ((ScheduleRunWindow)f).LoadJobIntoForm(j, true),
            f => { bool ok = ((ScheduleRunWindow)f).TryBuildJob(out ScheduledJob? j); return (ok, j); },
            CommonOwned.Concat(new[]
                { "ModelSetIds", "RunItems", "Sync", "RelinkSpec", "CorrectWorksetSpec" }).ToArray(),
            () =>
            {
                // Both need a saved set, so the fixture borrows whichever the machine has.
                var cw = CorrectWorksetSetStore.LoadAll().FirstOrDefault();
                var rl = RelinkSetStore.LoadAll().FirstOrDefault();
                if (cw is null && rl is null) return null;

                ScheduledJob j = SyntheticBase("RT Relink");
                j.RunItems = new List<RunItem>();
                if (rl != null)
                {
                    j.RunItems.Add(new(RelinkLinksAction.DisplayName, RunItemKind.Action));
                    j.RelinkSpec = rl;
                }
                if (cw != null)
                {
                    j.RunItems.Add(new(CorrectWorksetAction.DisplayName, RunItemKind.Action));
                    j.CorrectWorksetSpec = cw;
                }
                j.Sync = SyncMode.Sync;
                return j;
            });
    }

    /// <summary>
    /// Proves each schedule window still carries a job faithfully between its fields and a
    /// <see cref="ScheduledJob"/>.
    ///
    /// <para><b>Why this gate exists.</b> The layout gate proves a window still DRAWS. It says
    /// nothing about whether it still SAVES THE RIGHT SCHEDULE — and 1.4.0 collapses four
    /// near-identical schedule windows onto shared panels, where the whole risk is a field that
    /// quietly stops being wired. A schedule that fires at the wrong time next month is not
    /// something a screenshot can catch.</para>
    ///
    /// <para><b>Two checks, because one is not enough.</b>
    /// A: load a real saved job, read the form back, and compare the properties that window OWNS —
    /// this catches a field the refactor forgot to wire.
    /// B: feed the result back in and read it out again; the two must be identical — this is a
    /// fixed point, and it catches anything that survives one trip but not two, including
    /// properties nobody thought to list in A. On its own A misses drift in unlisted fields, and on
    /// its own B passes happily when a field is dropped on BOTH trips. Together they do not.</para>
    ///
    /// <para><b>Fixtures are the user's own saved schedules</b> (<see cref="ScheduleStore.LoadAll"/>),
    /// which is what makes this runnable anywhere: a synthetic job would have to name model-set
    /// presets and input sets that exist on THIS machine, and a real saved job does so by
    /// construction. A window with no matching saved job is reported SKIPPED, loudly and counted —
    /// a gate that silently checks nothing is worse than no gate.</para>
    /// </summary>
    /// <param name="inject">Compares against a deliberately altered job and requires the gate to
    /// notice. Same lesson as the layout gate's broken form: a check nobody has watched fail is not
    /// known to work.</param>
    private static int RoundTripCheck(bool inject)
    {
        List<ScheduledJob> saved;
        try { saved = ScheduleStore.LoadAll(); }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: could not read saved schedules: " + ex.Message);
            return 1;
        }

        // A saved job can name a model-set preset that has since been deleted. The window cannot
        // re-check what is not in its list, so that is the fixture being stale, not the window
        // being wrong — compare only against presets this machine still has.
        HashSet<string> presentPresets;
        try { presentPresets = PresetStore.LoadAll().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { presentPresets = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

        int problems = 0, skipped = 0, checkedCases = 0;
        Action removeFileLogFixture = EnsureFileLogFixture();
        Action removeDataExtractFixture = EnsureDataExtractFixture();
        try
        {

        foreach (RoundTripCase c in RoundTripCases())
        {
            // Real saved jobs first — they are the truest fixtures — with the synthetic one always
            // appended, so a machine whose saved jobs have all gone stale still checks something.
            var fixtures = saved.Where(c.Fixture).Take(3).ToList();
            ScheduledJob? synthetic = null;
            try { synthetic = c.Synthetic(); }
            catch (Exception ex) { Console.WriteLine($"  note: {c.Window} synthetic fixture unavailable: {ex.Message}"); }
            if (synthetic != null) fixtures.Add(synthetic);

            if (fixtures.Count == 0)
            {
                Console.WriteLine($"  SKIPPED {c.Window} — no saved schedule of this kind, and no " +
                                  "saved input set to build a fixture from.");
                skipped++;
                continue;
            }

            ScheduledJob? provenBuilt = null;   // the first fixture that actually built, for --inject

            foreach (ScheduledJob j0 in fixtures)
            {
                string label = $"{c.Window} / '{j0.Name}'";
                ScheduledJob? j1 = RunOnce(c, j0, label, ref problems);
                if (j1 == null) { skipped++; continue; }
                provenBuilt ??= j1;

                // --- Check A: the window kept what it is responsible for -------------------------
                var expected = Clone(j0);
                expected.ModelSetNames = expected.ModelSetNames.Where(presentPresets.Contains).ToList();
                problems += Compare(label + " [owned]", expected, j1, c.Owned);

                // --- Check B: a second trip changes nothing (fixed point) -----------------------
                ScheduledJob? j2 = RunOnce(c, j1, label + " [again]", ref problems);
                if (j2 == null) { skipped++; continue; }
                problems += Compare(label + " [fixed point]", j1, j2, AllProperties());

                checkedCases++;
                if (problems == 0) Console.WriteLine($"  ok   {label}");
            }

            // --- The injected fault, once per window, on a fixture KNOWN to have built ----------
            if (inject)
            {
                if (provenBuilt == null)
                {
                    Console.WriteLine($"  FAIL {c.Window} [inject]: nothing built, so the gate could " +
                                      "not be shown to work here.");
                    problems++;
                }
                else
                {
                    var damaged = Clone(provenBuilt);
                    damaged.ProjectLogName += " (injected)";
                    int caught = Compare(c.Window + " [inject]", damaged, provenBuilt,
                        new[] { "ProjectLogName" }, quiet: true);
                    if (caught == 0)
                    {
                        Console.WriteLine($"  FAIL {c.Window}: the gate did NOT notice a wrong ProjectLogName.");
                        problems++;
                    }
                    else
                    {
                        Console.WriteLine($"  ok   {c.Window} [injected wrong ProjectLogName detected]");
                    }
                }
            }
        }

        }
        finally { removeFileLogFixture(); removeDataExtractFixture(); }

        if (problems > 0)
        {
            Console.WriteLine($"FAILED: {problems} round-trip difference(s). A schedule window is no " +
                              "longer carrying a field it is responsible for.");
            return 1;
        }
        if (checkedCases == 0)
        {
            Console.WriteLine("FAILED: the round-trip gate checked NOTHING — every window was skipped. " +
                              "Save one schedule of each kind on this machine and re-run.");
            return 1;
        }
        Console.WriteLine($"Round-trip gate passed: {checkedCases} job(s) survived both trips" +
                          (skipped > 0 ? $"; {skipped} skipped." : "."));
        return 0;
    }

    /// <summary>One load-then-read cycle on a fresh window. Null when the form refused to build,
    /// which is a skip rather than a failure — the fixture may name an input set this machine has
    /// since lost.</summary>
    private static ScheduledJob? RunOnce(RoundTripCase c, ScheduledJob input, string label, ref int problems)
    {
        Form? form = null;
        try
        {
            form = c.Make();
            c.Load(form, input);
            (bool ok, ScheduledJob? built) = c.Build(form);
            if (!ok || built == null)
            {
                // The window already said what was wrong, into its own status label. Repeat it
                // here: a skip whose reason you have to guess is how a gate quietly stops testing.
                Console.WriteLine($"  SKIPPED {label} — {StatusOf(form)}");
                return null;
            }
            return built;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {label}: {ex.GetType().Name}: {ex.Message}");
            problems++;
            return null;
        }
        finally { form?.Dispose(); }
    }

    /// <summary>The window's own status text, read off its status label — the sentence the user
    /// would have seen. Reflection because the label is a private field, which is where it belongs;
    /// a rename here costs a less specific message, never a wrong result.</summary>
    private static string StatusOf(Form form)
    {
        FieldInfo? f = form.GetType().GetField("_status", BindingFlags.NonPublic | BindingFlags.Instance);
        string? text = (f?.GetValue(form) as Label)?.Text;
        return string.IsNullOrWhiteSpace(text) ? "the form would not build (no reason given)" : text!;
    }

    private static readonly System.Text.Json.JsonSerializerOptions RtJson = new() { WriteIndented = false };

    private static ScheduledJob Clone(ScheduledJob j) =>
        System.Text.Json.JsonSerializer.Deserialize<ScheduledJob>(
            System.Text.Json.JsonSerializer.Serialize(j, RtJson), RtJson)!;

    private static string[] AllProperties() =>
        typeof(ScheduledJob).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            // Computed views over RunItems (ActionNames/ScriptSetNames/TaskName) restate what is
            // already compared, and would report one difference twice.
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToArray();

    /// <summary>Compares the named properties of two jobs as JSON, reporting each difference.</summary>
    private static int Compare(string label, ScheduledJob a, ScheduledJob b, string[] properties, bool quiet = false)
    {
        int diffs = 0;
        foreach (string name in properties)
        {
            PropertyInfo? p = typeof(ScheduledJob).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null)
            {
                if (!quiet)
                    Console.WriteLine($"  FAIL {label}: ScheduledJob has no property '{name}' — " +
                                      "the gate's owned-property list is out of date.");
                diffs++;
                continue;
            }

            object? av = p.GetValue(a), bv = p.GetValue(b);
            // A Once job's start date is stored date-only by the picker; a fixture carrying a time
            // component is not a wiring fault.
            if (name == "StartDate")
            {
                av = (av as DateTime?)?.Date;
                bv = (bv as DateTime?)?.Date;
            }

            string aj = System.Text.Json.JsonSerializer.Serialize(av, RtJson);
            string bj = System.Text.Json.JsonSerializer.Serialize(bv, RtJson);
            if (aj == bj) continue;

            diffs++;
            if (!quiet)
                Console.WriteLine($"  FAIL {label}: {name}{Environment.NewLine}" +
                                  $"        expected {Trim(aj)}{Environment.NewLine}" +
                                  $"        got      {Trim(bj)}");
        }
        return diffs;
    }

    private static string Trim(string s) => s.Length <= 160 ? s : s.Substring(0, 157) + "...";

    private static int LayoutCheck(bool inject)
    {
        int problems = 0;

        foreach ((string name, Func<Form> make) in Windows())
            problems += InspectWindow(name, make);

        if (inject)
        {
            // One form per planted fault, each of which must be caught on its own: a single form
            // holding all three would pass on the easiest one while a new rule saw nothing.
            foreach ((string what, Func<Form> make) in new (string, Func<Form>)[]
                     {
                         ("truncated caption", BuildBrokenForm),
                         ("shrunken status strip", BuildBrokenStrip),
                         ("mismatched button row", BuildBrokenButtons),
                     })
            {
                int found = InspectWindow("INJECTED_" + what, make, quiet: true);
                if (found == 0)
                {
                    Console.WriteLine($"FAILED: the injected {what} was NOT detected — " +
                                      "the layout gate does not work.");
                    return 1;
                }
                // NOT added to `problems`: the injected window is the test of the checker, not a
                // defect in a shipped dialog. Nor does it clear the real count — a self-test that
                // could mask a genuine failure would be worse than no self-test.
                Console.WriteLine($"ok   injected {what} detected ({found} problem(s))");
            }
        }

        if (problems > 0)
        {
            Console.WriteLine($"FAILED: {problems} layout problem(s) across the shared dialogs.");
            return 1;
        }
        Console.WriteLine("Layout gate passed: every window readable at its natural and minimum size.");
        return 0;
    }

    /// <summary>Builds one window, inspects it at both sizes, and returns the problem count.</summary>
    private static int InspectWindow(string name, Func<Form> make, bool quiet = false)
    {
        int problems = 0;
        try
        {
            using Form form = make();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-5000, -5000);   // off-screen — no visible flicker
            form.ShowInTaskbar = false;
            form.Show();
            Settle(form);

            problems += Inspect(form, name, "natural", quiet);

            // Shrink to the size the user is allowed to drag it to. A dialog that only works at the
            // size it happens to open at is a dialog waiting to break on someone else's screen.
            Size min = form.MinimumSize;
            if (min.Width > 0 && min.Height > 0 && min != form.Size)
            {
                form.Size = min;
                Settle(form);
                problems += Inspect(form, name, "minimum", quiet);
            }

            form.Close();
            if (problems == 0 && !quiet) Console.WriteLine("ok   " + name);
        }
        catch (Exception ex)
        {
            problems++;
            Console.WriteLine($"FAILED {name}: {ex.Message}");
        }
        return problems;
    }

    /// <summary>Pump the message loop until the layout has actually settled — same recipe as the
    /// screenshot path, which needs the window painted for the same reason.</summary>
    private static void Settle(Form form)
    {
        form.PerformLayout();
        for (int i = 0; i < 15; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
    }

    /// <summary>Walks the control tree once and reports every unreadable control it finds.</summary>
    private static int Inspect(Control root, string window, string at, bool quiet)
    {
        int problems = 0;

        foreach (Control c in Descendants(root))
        {
            foreach (string problem in ProblemsWith(c))
            {
                problems++;
                if (!quiet)
                    Console.WriteLine($"FAILED {window} @ {at}: {Describe(c)} — {problem}");
            }
        }

        // Overlap is a property of a PAIR, so it is checked per container — each pair once, rather
        // than once from each side. This is the rule that catches the "Model set to run over:" label
        // sitting on top of the "Export CSV log to:" row.
        foreach (Control container in new[] { root }.Concat(Descendants(root)))
        {
            List<Control> kids = container.Controls.Cast<Control>()
                .Where(k => k.Visible && k.Width > 0 && k.Height > 0).ToList();
            for (int i = 0; i < kids.Count; i++)
                for (int j = i + 1; j < kids.Count; j++)
                {
                    Rectangle a = kids[i].Bounds, b = kids[j].Bounds;
                    a.Inflate(-1, -1);   // a shared edge is adjacency, not overlap
                    if (!a.IntersectsWith(b)) continue;
                    problems++;
                    if (!quiet)
                        Console.WriteLine($"FAILED {window} @ {at}: {Describe(kids[i])} overlaps " +
                                          $"{Describe(kids[j])} ({kids[i].Bounds} ∩ {kids[j].Bounds})");
                }
        }

        foreach (string problem in ButtonRowProblems(root))
        {
            problems++;
            if (!quiet) Console.WriteLine($"FAILED {window} @ {at}: {problem}");
        }

        return problems;
    }

    private static IEnumerable<string> ProblemsWith(Control c)
    {
        if (!c.Visible || c.Width <= 0 || c.Height <= 0) yield break;

        // 1. Text wider (or, when it wraps, taller) than the control showing it.
        if (c.AutoSize && c is Label or CheckBox or RadioButton or Button)
        {
            if (c.MaximumSize.Width > 0)
            {
                // A wrapping note: its width is capped on purpose, so height is what tells.
                int need = c.GetPreferredSize(new Size(c.MaximumSize.Width, 0)).Height;
                if (need > c.Height + 1)
                    yield return $"wrapped text needs {need}px of height, has {c.Height}px";
            }
            else
            {
                int need = c.GetPreferredSize(Size.Empty).Width;
                if (need > c.Width + 1)
                    yield return $"text needs {need}px of width, has {c.Width}px (truncated)";
            }
        }

        // 2. Outside its parent. Nothing in src/KhBatchRunner uses AutoScroll, so there is no
        //    legitimate case of a control living beyond the panel that owns it.
        // AutoScroll lives on ScrollableControl, not Control — a scrolling parent legitimately holds
        // children beyond its client area, so it is the one case to let through.
        if (c.Parent is { } p && p is not ScrollableControl { AutoScroll: true })
        {
            Rectangle inside = p.ClientRectangle;
            if (c.Right > inside.Right + 1 || c.Bottom > inside.Bottom + 1 ||
                c.Left < inside.Left - 1 || c.Top < inside.Top - 1)
                yield return $"sticks out of its {p.GetType().Name} " +
                             $"({c.Bounds} vs {inside})";
        }

        // 3. A docked list/grid squeezed to uselessness — the Schedule Run symptom.
        if (c.Dock == DockStyle.Fill && c is ListBox or CheckedListBox or DataGridView &&
            c.Height < StarvedFillHeight)
            yield return $"filled control starved to {c.Height}px (needs {StarvedFillHeight}px+)";

        // 4. A strip docked to the top or bottom that does not reach across its parent. The status
        //    bar shipped as a 100 px box in the bottom-left corner: a Label resets itself to the
        //    width it was last asked for whenever its text changes, and it changed its text during
        //    the form's own dock layout, so the re-dock never happened.
        //    Only under the default dock layout: inside a table or flow panel "Top" means the cell,
        //    which is narrower than the panel by design.
        if (c.Dock is DockStyle.Top or DockStyle.Bottom &&
            c.Parent is { } dp && dp is not TableLayoutPanel and not FlowLayoutPanel)
        {
            int across = dp.DisplayRectangle.Width;
            if (Math.Abs(c.Width - across) > 1)
                yield return $"docked {c.Dock} but {c.Width}px wide in a {across}px-wide {dp.GetType().Name}";
        }
    }

    /// <summary>
    /// Rule 5: buttons on one line must be one height and stand level. Checked on SCREEN
    /// coordinates, not per container, because the two halves of an action row are usually in
    /// different panels — Batch Run's Close sat 2 px above Run now for exactly that reason, and the
    /// Enlarge dialog's AutoSize "Create views" stood 7 px taller than its fixed-size "Cancel".
    /// </summary>
    private static IEnumerable<string> ButtonRowProblems(Control root)
    {
        List<(Button B, Rectangle R)> buttons = Descendants(root).OfType<Button>()
            .Where(b => b.Visible && b.Width > 0 && b.Height > 0 && b.IsHandleCreated)
            .Select(b => (b, b.RectangleToScreen(b.ClientRectangle)))
            .ToList();
        for (int i = 0; i < buttons.Count; i++)
            for (int j = i + 1; j < buttons.Count; j++)
            {
                Rectangle a = buttons[i].R, b = buttons[j].R;
                int shared = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                if (shared * 2 <= Math.Min(a.Height, b.Height)) continue;   // different lines
                if (Math.Abs(a.Height - b.Height) > 1 || Math.Abs(a.Top - b.Top) > 1)
                    yield return $"{Describe(buttons[i].B)} ({a.Height}px at y {a.Top}) and " +
                                 $"{Describe(buttons[j].B)} ({b.Height}px at y {b.Top}) share a line " +
                                 "but not a height/top";
            }
    }

    /// <summary>Every control under <paramref name="root"/>, depth-first.</summary>
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control g in Descendants(child)) yield return g;
        }
    }

    private static string Describe(Control c)
    {
        string text = (c.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 40) text = text.Substring(0, 40) + "…";
        return text.Length > 0 ? $"{c.GetType().Name} \"{text}\"" : $"{c.GetType().Name} ({c.Name})";
    }

    /// <summary>
    /// A form that is broken on purpose: a long caption in a column too narrow for it, which is the
    /// exact shape of Export NWC's truncated "Convert Element Properties". If the checker cannot see
    /// this, it cannot see the real thing either.
    /// </summary>
    private static Form BuildBrokenForm()
    {
        var form = new Form { ClientSize = new Size(120, 90), FormBorderStyle = FormBorderStyle.Sizable };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        table.Controls.Add(
            new CheckBox { Text = "A caption far too long for forty pixels", AutoSize = true }, 0, 0);
        form.Controls.Add(table);
        return form;
    }

    /// <summary>
    /// The first StatusLine, planted the way Batch Run used it: docked under a filling panel, given
    /// its message in the constructor. It ended up a 100 px box in the bottom-left corner.
    /// </summary>
    private static Form BuildBrokenStrip()
    {
        var form = new Form { ClientSize = new Size(860, 160), FormBorderStyle = FormBorderStyle.Sizable };
        var strip = new FirstStatusLine();
        form.Controls.Add(new Panel { Dock = DockStyle.Fill });
        form.Controls.Add(strip);
        strip.SetMessage("Check the model sets and actions/script sets to include and a destination, " +
                         "then Run now or Save as schedule.");
        return form;
    }

    /// <summary>
    /// StatusLine as 01db13d shipped it, reduced to the part that broke: a Label that re-decides
    /// its "ⓘ" badge — and so changes its own Text — from inside Resize. A non-AutoSize Label
    /// resets itself to its requested (100 px default) width on every text change, and doing that
    /// during the form's dock layout means the re-dock that would undo it is dropped.
    /// </summary>
    private sealed class FirstStatusLine : Label
    {
        private string _full = "";

        public FirstStatusLine()
        {
            Dock = DockStyle.Bottom;
            Height = 24;
            TextAlign = ContentAlignment.MiddleLeft;
            Padding = new Padding(8, 0, 0, 0);
            BorderStyle = BorderStyle.Fixed3D;
            Resize += (_, _) => Render();
        }

        public void SetMessage(string message)
        {
            _full = message;
            Render();
        }

        private void Render()
        {
            int usable = ClientSize.Width - Padding.Horizontal;
            bool tooWide = usable > 0 && TextRenderer.MeasureText(_full + "  ⓘ", Font).Width > usable;
            Text = tooWide ? _full + "  ⓘ" : _full;
        }
    }

    /// <summary>Enlarge's first bottom row, planted: an AutoSize button beside a fixed-size one.</summary>
    private static Form BuildBrokenButtons()
    {
        var form = new Form { ClientSize = new Size(360, 90), FormBorderStyle = FormBorderStyle.Sizable };
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        row.Controls.Add(new Button { Text = "Create views", AutoSize = true, MinimumSize = new Size(110, 0), Padding = new Padding(0, 3, 0, 3) });
        row.Controls.Add(new Button { Text = "Cancel", MinimumSize = new Size(90, 0) });
        form.Controls.Add(row);
        return form;
    }

    /// <summary>Loads RevitAPI/RevitAPIUI from a real Revit install — see the AssemblyResolve wiring in Main.</summary>
    private static Assembly? ResolveRevitAssemblies(object? sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name ?? "";
        if (name is not ("RevitAPI" or "RevitAPIUI"))
            return null;

        string path = Path.Combine(RevitDir, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetDllDirectory(string? lpPathName);
}
