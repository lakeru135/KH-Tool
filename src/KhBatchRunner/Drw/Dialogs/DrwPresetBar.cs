using System.Drawing;
using System.Windows.Forms;
using KhBatchRunner.Drw.Common;

namespace KhBatchRunner.Drw.Dialogs;

/// <summary>
/// The "Preset [combo] Save  Save As…  Duplicate  Delete" row every DRW dialog starts with. Choosing a preset
/// loads it into the dialog; Save writes what the dialog shows now under the chosen name.
/// </summary>
internal sealed class DrwPresetBar<T> where T : class, IDrwPreset, new()
{
    private readonly DrwPresetStore<T> _store;
    private readonly Func<T> _capture;
    private readonly Action<T> _apply;
    private readonly ComboBox _combo;
    private bool _loading;

    public Control Row { get; }

    public DrwPresetBar(Form owner, DrwPresetStore<T> store, Func<T> capture, Action<T> apply, string current)
    {
        _store = store;
        _capture = capture;
        _apply = apply;
        _combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        _combo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _combo.SelectedItem is not string name) return;
            T? p = _store.Load(name);
            if (p != null) _apply(p);
        };

        Button Btn(string text, Action onClick) => DrwControls.Button(text, 70, onClick);

        var save = Btn("Save", () =>
        {
            if (_combo.SelectedItem is string name) SaveAs(name);
            else SaveAsPrompt(owner);
        });
        var saveAs = Btn("Save As…", () => SaveAsPrompt(owner));
        // The product-wide Duplicate: "<name> (copy)" at once, no prompt (UI.SetDuplication). It
        // copies what the dialog shows, so unsaved edits go into the copy and the original stays.
        var duplicate = Btn("Duplicate", () =>
        {
            string baseName = SelectedName.Length > 0 ? SelectedName : "Preset";
            SaveAs(KhBatchRunner.UI.SetDuplication.UniqueName(baseName, _store.LoadAll().Select(p => p.Name)));
        });
        var delete = Btn("Delete", () =>
        {
            if (_combo.SelectedItem is not string name) return;
            if (MessageBox.Show(owner, $"Delete preset \"{name}\"?", "Delete preset",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _store.Delete(name);
            Reload(null);
        });

        Row = KhBatchRunner.UI.FormLayout.LabeledRow("Preset:", _combo, save, saveAs, duplicate, delete);
        Reload(current);
    }

    /// <summary>The selected preset's name, or blank when the dialog holds unsaved values.</summary>
    public string SelectedName => _combo.SelectedItem as string ?? "";

    private void SaveAsPrompt(Form owner)
    {
        string? name = DrwInputBox.Ask(owner, "Save preset", "Preset name:", SelectedName.Length > 0 ? SelectedName : "Default");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (_store.Exists(name) && !string.Equals(name, SelectedName, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(owner, $"A preset named \"{name}\" exists. Replace it?", "Save preset",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        SaveAs(name);
    }

    private void SaveAs(string name)
    {
        T p = _capture();
        p.Name = name;
        _store.Save(p);
        Reload(name);
    }

    private void Reload(string? select)
    {
        _loading = true;
        try
        {
            _combo.Items.Clear();
            foreach (T p in _store.LoadAll()) _combo.Items.Add(p.Name);
            int i = select == null ? -1 : _combo.Items.IndexOf(
                _combo.Items.Cast<string>().FirstOrDefault(n => string.Equals(n, select, StringComparison.OrdinalIgnoreCase)) ?? "\0");
            _combo.SelectedIndex = i;
        }
        finally { _loading = false; }
    }
}

/// <summary>A one-line text prompt (WinForms has none built in).</summary>
internal static class DrwInputBox
{
    public static string? Ask(IWin32Window owner, string title, string prompt, string initial)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(360, 110), ShowInTaskbar = false
        };
        var label = new Label { Text = prompt, AutoSize = true, Location = new Point(12, 12) };
        var box = new TextBox { Text = initial, Location = new Point(12, 34), Width = 336 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(192, 72), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(273, 72), Width = 75 };
        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        KhBatchRunner.UI.Theme.Apply(form);
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}

/// <summary>Small control factories shared by the DRW dialogs.</summary>
internal static class DrwControls
{
    public static NumericUpDown Number(double value, double min, double max, int decimals, double step)
    {
        var n = new NumericUpDown
        {
            DecimalPlaces = decimals, Minimum = (decimal)min, Maximum = (decimal)max,
            Increment = (decimal)step, Width = 90, TextAlign = HorizontalAlignment.Right
        };
        n.Value = Clamp(n, value);
        return n;
    }

    public static decimal Clamp(NumericUpDown n, double value) =>
        Math.Min(n.Maximum, Math.Max(n.Minimum, (decimal)value));

    public static ComboBox Combo(IEnumerable<string> items, bool withNone = false)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
        if (withNone) c.Items.Add(DrwLookup.None);
        foreach (string s in items) c.Items.Add(s);
        if (c.Items.Count > 0) c.SelectedIndex = 0;
        // A long "Family : Type" may be cut in the closed box; the open list always shows it whole.
        KhBatchRunner.UI.FormLayout.FitList(c);
        return c;
    }

    /// <summary>
    /// Keeps every wrapping note label (AutoSize with a MaximumSize width, as
    /// <see cref="KhBatchRunner.UI.FormLayout.NoteLabel"/> makes them) exactly as wide as the table
    /// cell it sits in, on every layout pass. NoteLabel wraps at the WINDOW width minus 32, which is
    /// right in a form's root table but ~50 px too wide inside a group: the note measured fewer lines
    /// than it drew, and the row under it was pushed out of its group — the half-hidden "Skip a view
    /// whose name already exists" in Enlarge (found by the self-test's layout check, 0.2.7).
    /// </summary>
    public static void FitNotes(Control root)
    {
        foreach (TableLayoutPanel table in All(root).OfType<TableLayoutPanel>().ToList())
        {
            TableLayoutPanel t = table;
            t.Layout += (_, _) => FitNotesIn(t);
        }
    }

    private static void FitNotesIn(TableLayoutPanel t)
    {
        int[] widths;
        try { widths = t.GetColumnWidths(); } catch { return; }
        foreach (Control c in t.Controls)
        {
            if (c is not Label l || !l.AutoSize || l.MaximumSize.Width <= 0) continue;
            int col = t.GetColumn(l), span = Math.Max(1, t.GetColumnSpan(l));
            if (col < 0 || col >= widths.Length) continue;
            int w = widths.Skip(col).Take(span).Sum() - l.Margin.Horizontal;
            if (w > 40 && l.MaximumSize.Width != w) l.MaximumSize = new Size(w, 0);
        }
    }

    private static IEnumerable<Control> All(Control c)
    {
        foreach (Control child in c.Controls)
        {
            yield return child;
            foreach (Control d in All(child)) yield return d;
        }
    }

    /// <summary>Select <paramref name="value"/> if present; otherwise keep the first item.</summary>
    public static void Select(ComboBox c, string value)
    {
        int i = c.Items.IndexOf(value);
        // A preset saved before types were listed as "Family : Type" holds the bare type name.
        if (i < 0 && !string.IsNullOrWhiteSpace(value))
            for (int k = 0; k < c.Items.Count && i < 0; k++)
                if ((c.Items[k]?.ToString() ?? "").EndsWith(" : " + value, StringComparison.Ordinal)) i = k;
        if (i < 0 && string.IsNullOrWhiteSpace(value) && c.Items.Count > 0) i = 0;
        if (i >= 0) c.SelectedIndex = i;
    }

    public static string Text(ComboBox c) => c.SelectedItem?.ToString() ?? "";

    /// <summary>
    /// Every DRW button: sized to its text, never shorter than <paramref name="minWidth"/>. One
    /// factory so buttons on a row share a height — a Button left at AutoSize=false keeps the 23 px
    /// default while its AutoSize neighbour grows to fit its text, which is how "Cancel" came out
    /// shorter than "Create views".
    /// </summary>
    public static Button Button(string text, int minWidth, Action? onClick = null)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(minWidth, 0)
        };
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    public static CheckBox Check(string text, bool on) =>
        new() { Text = text, AutoSize = true, Checked = on, Margin = new Padding(3, 4, 12, 4) };

    public static GroupBox Group(string title, params Control[] rows)
    {
        var inner = KhBatchRunner.UI.FormLayout.SingleColumn();
        inner.AutoSize = true;
        inner.Padding = new Padding(4);
        foreach (Control r in rows) KhBatchRunner.UI.FormLayout.AddAutoRow(inner, r);
        var g = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        g.Controls.Add(inner);
        return g;
    }

    /// <summary>A vertically scrolling page holding groups stacked top to bottom.</summary>
    public static Panel Page(params Control[] groups)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
        // Dock=Top stacks in reverse order of addition. Docked here rather than trusted to the
        // caller: a bare CheckBox handed in (Stair Annotation's "Leave stairs…") kept Dock=None and
        // sat at 0,0 on top of the first group.
        foreach (Control g in groups.Reverse())
        {
            g.Dock = DockStyle.Top;
            page.Controls.Add(g);
        }
        return page;
    }}
