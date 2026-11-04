using System.Drawing;
using System.Windows.Forms;

namespace KhBatchRunner.UI;

/// <summary>
/// The whole of a message the status strip could only show the first line of — selectable,
/// scrollable, and copyable.
///
/// <para><b>Why it exists.</b> The status strip is one fixed-height line, and the messages that
/// matter most are the longest: a failed schedule save carries the launcher's per-copy refusal
/// reasons, and a failed REST call carries the status line plus the server's body. Both were
/// clipped to their first line, which is the half that says something went wrong and never the
/// half that says what. A <see cref="Label"/> cannot be selected either, so the text could not
/// even be sent to someone who could read it.</para>
///
/// <para>Deliberately not a <c>MessageBox</c>: its text is unselectable too (Ctrl+C copies a
/// mangled transcript of the whole dialog), it cannot scroll, and it is modal to a message that is
/// often worth keeping open while you act on it.</para>
///
/// <para>Revit-free, like every other window here, so the standalone controller shows the same
/// dialog.</para>
/// </summary>
public sealed class MessageDetailsWindow : Form
{
    private readonly string _body;

    public MessageDetailsWindow(string title, string body)
    {
        _body = body ?? "";

        Text = string.IsNullOrWhiteSpace(title) ? "Details" : title;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(700, 380);
        MinimumSize = new Size(420, 260);

        // The AboutWindow changelog pattern: read-only, wrapped, scrollable, and NOT a tab stop —
        // a focused multiline TextBox selects all of itself and opens looking like a highlighted
        // block of text the user is about to overwrite.
        var text = new TextBox
        {
            Text = Normalize(_body),
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            TabStop = false,
        };

        var copyButton = new Button
        {
            Text = "Copy", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 0)
        };
        copyButton.Click += (_, _) => CopyToClipboard();
        var closeButton = new Button
        {
            Text = "Close", DialogResult = DialogResult.OK, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0)
        };

        var root = FormLayout.SingleColumn();
        FormLayout.AddFillRow(root, text, 100);
        var buttons = FormLayout.FlowRow(copyButton, closeButton);
        buttons.Anchor = AnchorStyles.Right;
        FormLayout.AddAutoRow(root, buttons);

        Controls.Add(root);
        AcceptButton = closeButton;
        CancelButton = closeButton;

        Theme.Apply(this);
    }

    /// <summary>
    /// Windows' multiline TextBox needs CRLF: a message assembled in code carries bare <c>\n</c>,
    /// which renders as one unbroken line — the very fault this window exists to fix.
    /// </summary>
    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    /// <summary>The message as it was given, so a pasted copy matches what the log will show.</summary>
    private void CopyToClipboard()
    {
        try { Clipboard.SetText(Normalize(_body)); } catch { /* clipboard busy — ignore */ }
    }
}
