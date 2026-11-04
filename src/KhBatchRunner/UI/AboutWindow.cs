using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace KhBatchRunner.UI;

/// <summary>
/// "About" dialog for the Setting panel: the Kanghun logo, the add-in name/version/publisher, a
/// short description, and a "what's new" list. Read-only. Themed to Revit; the logo is the embedded
/// <c>KanghunPureLogo.png</c> (also this window's title-bar icon via <see cref="Theme"/>).
/// </summary>
public sealed class AboutWindow : Form
{
    public AboutWindow()
    {
        // Via HostServices so the standalone can describe itself: it has no ribbon tab, and it has
        // two versions worth showing — its own and the shared batch engine's.
        AboutFacts facts = HostServices.About();

        Text = "About " + facts.Name;
        // Resizable (drag) but no maximise — a full-screen about box is just empty space.
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(500, 560);
        MinimumSize = new Size(440, 420);

        var root = FormLayout.SingleColumn();

        Bitmap? logo = Icons.LoadLogo();
        if (logo != null)
        {
            var pic = new PictureBox
            {
                Image = logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(88, 88),
                Anchor = AnchorStyles.None, // centers in the table cell
                Margin = new Padding(3, 8, 3, 6)
            };
            FormLayout.AddAutoRow(root, pic);
        }

        var nameLbl = new Label { Text = facts.Name, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(3, 2, 3, 2) };
        FormLayout.AddAutoRow(root, nameLbl);
        var versionLbl = new Label { Text = "Version " + facts.Version, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(3, 0, 3, 2) };
        FormLayout.AddAutoRow(root, versionLbl);
        var pubLbl = new Label { Text = "Publisher: " + facts.Publisher, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(3, 0, 3, 2) };
        FormLayout.AddAutoRow(root, pubLbl);
        var tabLbl = new Label { Text = facts.DetailLabel + ": " + facts.DetailValue, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(3, 0, 3, 8) };
        FormLayout.AddAutoRow(root, tabLbl);

        Label desc = FormLayout.NoteLabel(this, facts.Description);
        FormLayout.AddAutoRow(root, desc);

        // What's-new list: a read-only, scrollable box. The raw changelog hard-wraps each bullet
        // across several indented lines, which reads as choppy; reflow so every bullet is ONE
        // paragraph and let WordWrap flow it to the box width.
        var changelog = new TextBox
        {
            Text = ReflowToParagraphs(facts.Changelog),
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            TabStop = false // don't grab focus (which would select-all and highlight the text)
        };
        FormLayout.AddFillRow(root, changelog, 100);

        var close = new Button
        {
            Text = "Close", DialogResult = DialogResult.OK,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(84, 0)
        };
        var buttons = FormLayout.FlowRow(close);
        buttons.Anchor = AnchorStyles.Right;
        FormLayout.AddAutoRow(root, buttons);

        Controls.Add(root);
        AcceptButton = close;
        CancelButton = close;

        Theme.Apply(this);

        // A multiline TextBox PREFERS one line of height, so fitting the window to its content
        // shrank the what's-new box to a single line. Give it about 14 lines as its floor, from the
        // font Theme.Apply just pinned so it scales with DPI.
        changelog.MinimumSize = new Size(0, changelog.Font.Height * 14);

        // Emphasis font AFTER Theme.Apply (which pins a base font on every control) but BEFORE the
        // fit below: the bigger name line is taller, and set later it pushed the what's-new box
        // onto the Close button (layout gate, 2.0.0).
        nameLbl.Font = new Font(nameLbl.Font.FontFamily, 13F, FontStyle.Bold);

        // Fit to content and never below it: at the hand-picked 440x420 minimum the changelog box
        // collapsed to a single pixel of height and the development-build banner was drawn over the
        // button row. A changelog nobody can read is the one thing this dialog is for.
        ClientSize = new Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width), root.PreferredSize.Height);
        MinimumSize = Size;
        desc.ForeColor = Theme.Dim(desc);
    }

    /// <summary>
    /// Collapses the changelog's manual line-wrapping so each bullet / heading is a single flowing
    /// paragraph the TextBox word-wraps itself. Indented continuation lines (a line starting with
    /// whitespace) fold into the previous line with one space; blank lines and new bullets/headings
    /// stay as separators. Emits CRLF so the WinForms TextBox renders the breaks.
    /// </summary>
    private static string ReflowToParagraphs(string changelog)
    {
        var paragraphs = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0) { paragraphs.Add(current.ToString()); current.Clear(); }
        }

        foreach (string raw in changelog.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Trim().Length == 0) { Flush(); paragraphs.Add(""); continue; } // blank separator
            bool continuation = char.IsWhiteSpace(raw[0]);                          // indented wrap line
            if (continuation && current.Length > 0) current.Append(' ').Append(raw.Trim());
            else { Flush(); current.Append(raw.TrimEnd()); }
        }
        Flush();

        // Drop a leading/trailing blank and collapse runs of blanks to one, then CRLF-join.
        var cleaned = new List<string>();
        foreach (string p in paragraphs)
        {
            if (p.Length == 0 && (cleaned.Count == 0 || cleaned[cleaned.Count - 1].Length == 0)) continue;
            cleaned.Add(p);
        }
        while (cleaned.Count > 0 && cleaned[cleaned.Count - 1].Length == 0) cleaned.RemoveAt(cleaned.Count - 1);
        return string.Join("\r\n", cleaned);
    }
}
