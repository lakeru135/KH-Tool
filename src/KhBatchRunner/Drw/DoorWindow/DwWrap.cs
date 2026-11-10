using System.Globalization;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>
/// A value broken into the lines the row's Value label shows, and how many, so the row can be made
/// that tall. The KH_DW Value label never wraps by itself (its box is 1000 mm wide, families v2): the
/// breaks are written into the text, at the cell's value width (the user, 2026-10-22: "it should be
/// auto with cell width"). Measured with GDI in the label's font at its paper size, times
/// <c>fit</c> — how much wider Revit draws that font than GDI measures it, read off a text note
/// once per build (<see cref="LegendBuilder.Extents"/>) — and wrapped greedily: at spaces where there
/// are any, and between characters inside Thai text, which has none.
/// </summary>
internal static class DwWrap
{
    public const int MaxLines = 8;

    /// <summary>How many lines <paramref name="text"/> takes at <paramref name="widthMm"/>.</summary>
    public static int Lines(string text, string font, double textMm, double widthMm, double fit = 1) =>
        Wrap(text, font, textMm, widthMm, fit).Lines;

    /// <summary><paramref name="text"/> with a line break wherever it must wrap at
    /// <paramref name="widthMm"/>, and its line count (at most <see cref="MaxLines"/>; the rest stays
    /// on the last line).</summary>
    public static (string Text, int Lines) Wrap(string text, string font, double textMm, double widthMm, double fit = 1)
    {
        if (string.IsNullOrEmpty(text) || widthMm <= 0) return (text ?? "", 1);
        using var bmp = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bmp);
        g.PageUnit = GraphicsUnit.Millimeter;
        using var f = new Font(font, (float)textMm, GraphicsUnit.Millimeter);
        var fmt = StringFormat.GenericTypographic;
        double Width(string s) => s.Length == 0 ? 0 : g.MeasureString(s, f, int.MaxValue, fmt).Width * Math.Max(0.1, fit);

        var lines = new List<string>();
        foreach (string para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = "";
            foreach (string piece in Pieces(para))
            {
                string tryLine = line + piece;
                if (line.Length > 0 && Width(tryLine.TrimEnd()) > widthMm && lines.Count < MaxLines - 1)
                {
                    lines.Add(line.TrimEnd());
                    line = piece.TrimStart();
                }
                else line = tryLine;
            }
            lines.Add(line.TrimEnd());
        }
        if (lines.Count > MaxLines) lines = lines.Take(MaxLines - 1).Concat(new[] { string.Join(" ", lines.Skip(MaxLines - 1)) }).ToList();
        return (string.Join("\n", lines), Math.Max(1, lines.Count));
    }

    /// <summary>The width GDI gives <paramref name="text"/>, paper mm (what <c>fit</c> is worked out against).</summary>
    public static double MeasureMm(string text, string font, double textMm)
    {
        using var bmp = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bmp);
        g.PageUnit = GraphicsUnit.Millimeter;
        using var f = new Font(font, (float)textMm, GraphicsUnit.Millimeter);
        return g.MeasureString(text, f, int.MaxValue, StringFormat.GenericTypographic).Width;
    }

    /// <summary>Words with their trailing space; a run with no spaces (Thai) splits into its
    /// characters (whole clusters, so a vowel mark stays with its consonant).</summary>
    private static IEnumerable<string> Pieces(string s)
    {
        foreach (string word in SplitKeepSpace(s))
        {
            bool thai = word.Any(c => c >= '฀' && c <= '๿');
            if (!thai) { yield return word; continue; }
            TextElementEnumerator e = StringInfo.GetTextElementEnumerator(word);
            while (e.MoveNext()) yield return (string)e.Current;
        }
    }

    private static IEnumerable<string> SplitKeepSpace(string s)
    {
        int start = 0;
        for (int i = 0; i < s.Length; i++)
            if (s[i] == ' ')
            {
                yield return s.Substring(start, i - start + 1);
                start = i + 1;
            }
        if (start < s.Length) yield return s.Substring(start);
    }
}
