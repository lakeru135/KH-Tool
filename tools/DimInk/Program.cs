using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using KhBatchRunner.UI;

namespace DimInk;

/// <summary>
/// Does <see cref="DimmableCheckedListBox"/> actually paint a dimmed row — on THIS target
/// framework?
///
/// <para><b>Why a second harness exists at all.</b> <c>ThemePreview --coverage</c> asks this of
/// every real window, but it is net8-only, so it can only ever answer for Revit 2025/2026. Revit
/// 2024 ships the same source compiled against net48, where <c>CheckedListBox</c> is a different
/// implementation. The mechanism the dimming relies on — that <c>CheckedListBox.OnDrawItem</c>
/// honours the <see cref="DrawItemEventArgs.ForeColor"/> it is handed, because its own
/// <c>DrawMode</c> setter is empty and it never raises <c>DrawItem</c> — is a fact about the
/// framework, and a fact about one framework is not a fact about the other.</para>
///
/// <para>So this compiles the real product class for both and measures the ink, and it asserts the
/// premise too: setting <c>DrawMode = OwnerDrawFixed</c> must read back <c>Normal</c>. If a future
/// runtime ever fixes that, this says so instead of silently continuing to work by accident.</para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool inject = args.Any(a => a.Equals("--inject", StringComparison.OrdinalIgnoreCase));

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Console.WriteLine($"DimInk on {Framework()} — {(inject ? "INJECTED: DimWhere never called" : "the real class")}");

        int problems = 0;
        foreach (string theme in new[] { "dark", "light" })
        {
            Environment.SetEnvironmentVariable("KHBATCH_FORCE_THEME", theme);

            using var form = new Form();
            var list = new DimmableCheckedListBox { Width = 420 };
            list.Items.Add("Reachable set  (2 models)");
            list.Items.Add("Denied set  ⚠ no access as this account");
            form.Controls.Add(list);

            // Row 1 is the one that must go dim. --inject wires nothing, which must FAIL — a gate
            // never seen to fail is not known to work.
            if (!inject) list.DimWhere(index => index == 1);
            Theme.Apply(form);

            // The premise, asserted rather than trusted. This is the line that would have caught
            // the original bug in five minutes.
            list.DrawMode = DrawMode.OwnerDrawFixed;
            if (list.DrawMode != DrawMode.Normal)
            {
                Console.WriteLine($"  {theme,-5} NOTE: DrawMode now reads {list.DrawMode} — this runtime " +
                                  "no longer ignores it, so the subclass may not be needed here.");
            }

            ShowOffScreen(form);

            int plain = InkOf(list, 0);
            int dimmed = InkOf(list, 1);
            int back = list.BackColor.R + list.BackColor.G + list.BackColor.B;
            bool moved = Math.Abs(dimmed - back) < Math.Abs(plain - back) - 30;

            Console.WriteLine($"  {theme,-5} plain ink {plain,4}, dimmed ink {dimmed,4}, background {back,4} → " +
                              (moved ? "dimmed" : "NOT dimmed"));
            if (!moved) problems++;
            form.Close();
        }

        if (inject)
        {
            if (problems == 0)
            {
                Console.WriteLine("FAILED: nothing was told to dim and the gate still passed.");
                return 1;
            }
            Console.WriteLine($"ok   [injected no dimming detected] ({problems} theme(s) undimmed)");
            return 0;
        }

        if (problems > 0)
        {
            Console.WriteLine($"FAILED: DimmableCheckedListBox paints no dimming in {problems} theme(s) " +
                              $"on {Framework()}.");
            return 1;
        }
        Console.WriteLine($"DimInk passed on {Framework()}: an inaccessible row draws dimmer in both themes.");
        return 0;
    }

    /// <summary>
    /// Ink of the most extreme text pixel of one row, from the control's own paint.
    ///
    /// <para>The left edge is skipped: the themed check-box glyph is near-white on the dark field,
    /// further from the background than any text, and measuring it reports every row identical.
    /// Same reasoning, same offset, as ThemePreview's InkOf — the two must agree or one of them is
    /// measuring the wrong thing.</para>
    /// </summary>
    private static int InkOf(DimmableCheckedListBox list, int index)
    {
        int height = Math.Max(list.ItemHeight, 12);
        var bounds = new Rectangle(0, 0, Math.Max(list.Width, 200), height);

        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(list.BackColor);
            // Not disposed/scoped: DrawItemEventArgs is IDisposable on net8 and not on net48, so a
            // using here would not compile for Revit 2024.
            var e = new DrawItemEventArgs(g, list.Font, bounds, index, DrawItemState.None,
                                          list.ForeColor, list.BackColor);
            // Off ListBox, the virtual root — Invoke dispatches to the real override, which is the
            // whole point: this exercises the painter that is installed, not the one we hoped was.
            typeof(ListBox).GetMethod("OnDrawItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(list, new object[] { e });
        }

        int left = height + 6;
        int right = Math.Min(bounds.Width - 2, left + 120);
        int backInk = list.BackColor.R + list.BackColor.G + list.BackColor.B;
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

    private static void ShowOffScreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-5000, -5000);
        form.ShowInTaskbar = false;
        form.Show();
        for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
    }

    private static string Framework() =>
#if NET48
        "net48 (Revit 2024)";
#else
        "net8 (Revit 2025/2026)";
#endif
}
