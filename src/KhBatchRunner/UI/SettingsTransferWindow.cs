using System.Drawing;
using System.Text;
using System.Windows.Forms;
using KhBatchRunner.Settings;

namespace KhBatchRunner.UI;

/// <summary>
/// Moves saved settings between PCs: tick what to carry, Export to a single zip, Import it on the
/// other machine.
///
/// <para><b>What it does not carry, and why that is on the dialog rather than in a comment.</b>
/// Schedules are excluded — half of one lives in Windows Task Scheduler, so a bundle could carry
/// the JSON and still not carry the schedule, and a restored backup that silently re-armed an
/// unattended 2am run would be worse than retyping it. The licensing/expiry cache is excluded for
/// the reason <see cref="UserSettings.ClearUpdateCacheIfBuildChanged"/> exists. Both facts are
/// printed above the list, because a backup tool that quietly omits things is one nobody can
/// trust.</para>
///
/// <para>The category list comes from <see cref="SettingsCatalog"/> — this window holds no folder
/// names of its own, so a store added later appears here as soon as it is catalogued (and the
/// catalog gate fails the build if it is not).</para>
/// </summary>
public sealed class SettingsTransferWindow : Form
{
    private readonly CheckedListBox _categories;
    private readonly CheckBox _includePreferences;
    private readonly ComboBox _collisionCombo;
    private readonly TextBox _report;
    private readonly StatusLine _status;

    public SettingsTransferWindow()
    {
        Text = "Import / Export Settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(660, 620);

        Label note = FormLayout.NoteLabel(this,
            "Copy your saved sets to another PC. Export writes ONE .zip; Import reads it back. " +
            "Schedules are NOT included — a schedule is half-stored in Windows Task Scheduler, so " +
            "it has to be re-created on the other PC. Licence/update state is never carried.");

        _categories = new CheckedListBox
        {
            Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false,
            // The list is the only percent-sized row here. Without a floor it collapses to a
            // sliver at the form's minimum height while the dialog still looks like it works —
            // the finding the layout gate raised against CheckSheetPublishWindow in 1.4.7.
            MinimumSize = new Size(0, 120)
        };
        CheckListBehavior.EnableRangeCheck(_categories);
        RefreshCategories();

        var selectAll = Mk("Select all", () => SetAllChecked(true));
        var selectNone = Mk("Select none", () => SetAllChecked(false));

        _includePreferences = new CheckBox
        {
            Text = "Include preferences (default destination folder, list filter)",
            AutoSize = true, Checked = true
        };

        _collisionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _collisionCombo.Items.Add(new CollisionItem(ImportCollision.Skip, "Skip — keep what is on this PC"));
        _collisionCombo.Items.Add(new CollisionItem(ImportCollision.KeepBoth, "Keep both — import under a free name"));
        _collisionCombo.Items.Add(new CollisionItem(ImportCollision.Overwrite, "Overwrite — replace with the imported one"));
        _collisionCombo.SelectedIndex = 0;

        var exportButton = Mk("Export…", Export, wide: true);
        var importButton = Mk("Import…", Import, wide: true);
        var close = new Button
        {
            Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0)
        };
        var buttons = FormLayout.FlowRow(exportButton, importButton, close);
        buttons.Anchor = AnchorStyles.Right;

        _report = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            MinimumSize = new Size(0, 80)
        };

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddFillRow(root, FormLayout.TitledColumn(
            "What to carry", _categories, FormLayout.FlowRow(selectAll, selectNone)), 60);
        FormLayout.AddAutoRow(root, _includePreferences);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("If it already exists:", _collisionCombo));
        FormLayout.AddAutoRow(root, buttons);
        FormLayout.AddFillRow(root, FormLayout.TitledColumn("Result", _report), 40);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(root);
        Controls.Add(_status);
        CancelButton = close;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);

        // 640, not the 520 this started at. Two percent-sized rows each carry a floor (the list
        // and the report box), and the form minimum has to leave the 60/40 split enough room to
        // satisfy BOTH after the auto rows take theirs — below that the list overflowed its column
        // and sat on top of the Select all/none buttons. Measured floor is ~520; the rest is
        // headroom for a larger system font.
        MinimumSize = new Size(560, 640);
        SetStatus("Tick what to carry, then Export or Import.");
    }

    // ---- list ------------------------------------------------------------------------------

    private void RefreshCategories()
    {
        _categories.Items.Clear();
        foreach (SettingsCategory category in SettingsCatalog.ForThisEdition())
        {
            int index = _categories.Items.Add(new CategoryItem(category));
            // Everything that HAS something is ticked; an empty category is listed but unticked,
            // so the dialog shows the whole shape of what exists rather than hiding the gaps.
            _categories.SetItemChecked(index, category.Count() > 0);
        }
    }

    private void SetAllChecked(bool value)
    {
        for (int i = 0; i < _categories.Items.Count; i++) _categories.SetItemChecked(i, value);
    }

    private List<SettingsCategory> Chosen() =>
        _categories.CheckedItems.Cast<CategoryItem>().Select(i => i.Category).ToList();

    // ---- export ----------------------------------------------------------------------------

    private void Export()
    {
        List<SettingsCategory> chosen = Chosen();
        if (chosen.Count == 0) { SetStatus("Tick at least one thing to export."); return; }

        using var dlg = new SaveFileDialog
        {
            Filter = SettingsBundle.FileFilter,
            FileName = $"KhBatchSettings-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}.zip",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            List<TransferResult> results =
                SettingsBundle.Export(dlg.FileName, chosen, _includePreferences.Checked);
            ShowReport("Exported to " + dlg.FileName, results, "carried");
            SetStatus($"Exported {results.Sum(r => r.Written)} file(s) to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            _report.Text = "EXPORT FAILED\r\n\r\n" + ex.Message;
            SetStatus("Export failed — see the result box.");
        }
    }

    // ---- import ----------------------------------------------------------------------------

    private void Import()
    {
        using var dlg = new OpenFileDialog { Filter = SettingsBundle.FileFilter, CheckFileExists = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // Read and show the manifest BEFORE writing anything, and make the user confirm against it.
        // A bundle is a file someone was handed; "which PC and which Autodesk account did these
        // sets come from" is exactly what they need to see before it lands on top of their own.
        SettingsManifest? manifest = SettingsBundle.ReadManifest(dlg.FileName, out string? error);
        if (manifest is null)
        {
            _report.Text = "This file could not be read as a settings bundle.\r\n\r\n" + error;
            SetStatus("Not a settings bundle.");
            return;
        }

        List<SettingsCategory> chosen = Chosen()
            .Where(c => manifest.Counts.TryGetValue(c.Key, out int n) && n > 0)
            .ToList();
        if (chosen.Count == 0)
        {
            _report.Text = Describe(manifest) +
                           "\r\nNothing to import: the bundle carries none of the ticked items.";
            SetStatus("Nothing ticked that this bundle carries.");
            return;
        }

        var collision = ((CollisionItem)_collisionCombo.SelectedItem!).Value;
        if (!HostServices.Confirm("Import settings",
                Describe(manifest) +
                $"\r\nImport {chosen.Count} item group(s) into this PC?\r\n" +
                $"Anything that already exists: {DescribeCollision(collision)}.\r\n\r\n" +
                "Schedules are not part of a bundle and will not change."))
        {
            SetStatus("Import cancelled — nothing was changed.");
            return;
        }

        try
        {
            List<TransferResult> results =
                SettingsBundle.Import(dlg.FileName, chosen, collision, _includePreferences.Checked);
            ShowReport(Describe(manifest) + "Imported from " + dlg.FileName, results, "imported");
            RefreshCategories();
            SetStatus($"Imported {results.Sum(r => r.Written)} file(s). Reopen a dialog to see them.");
        }
        catch (Exception ex)
        {
            _report.Text = "IMPORT FAILED\r\n\r\n" + ex.Message;
            SetStatus("Import failed — see the result box.");
        }
    }

    // ---- reporting -------------------------------------------------------------------------

    private static string Describe(SettingsManifest m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bundle from {m.ProductName} {m.ProductVersion} ({m.Edition} edition)");
        sb.AppendLine($"  PC        : {m.SourceMachine} ({m.SourceWindowsUser})");
        sb.AppendLine($"  Autodesk  : {(m.SourceAutodeskUser.Length > 0 ? m.SourceAutodeskUser : "not recorded")}");
        sb.AppendLine($"  Exported  : {m.ExportedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string DescribeCollision(ImportCollision collision) => collision switch
    {
        ImportCollision.Overwrite => "is REPLACED by the imported one",
        ImportCollision.KeepBoth => "is kept, and the imported one is added under a free name",
        _ => "is kept, and the imported one is skipped"
    };

    private void ShowReport(string header, List<TransferResult> results, string verb)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        foreach (TransferResult r in results)
        {
            if (r.Error != null) sb.AppendLine($"  {r.DisplayName}: FAILED — {r.Error}");
            else if (r.Skipped > 0) sb.AppendLine($"  {r.DisplayName}: {r.Written} {verb}, {r.Skipped} skipped");
            else sb.AppendLine($"  {r.DisplayName}: {r.Written} {verb}");
        }
        _report.Text = sb.ToString();
    }

    private void SetStatus(string text) { _status.SetMessage(text); }

    private static Button Mk(string text, Action onClick, bool wide = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(wide ? 90 : 80, 0)
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private sealed class CategoryItem
    {
        public SettingsCategory Category { get; }
        public CategoryItem(SettingsCategory category) => Category = category;
        public override string ToString()
        {
            int n = Category.Count();
            return n == 0 ? $"{Category.DisplayName}  (none saved)" : $"{Category.DisplayName}  ({n})";
        }
    }

    private sealed class CollisionItem
    {
        public ImportCollision Value { get; }
        private readonly string _label;
        public CollisionItem(ImportCollision value, string label) { Value = value; _label = label; }
        public override string ToString() => _label;
    }
}
