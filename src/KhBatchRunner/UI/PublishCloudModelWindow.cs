using System.IO;
using KhBatchRunner.Forma;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// "Publish Cloud Model": batch-runs Forma "Publish Latest Version" over a saved model set — each
/// cloud model's latest synced version is published via the Data Management C4R publish command
/// (<see cref="DataManagementClient.PublishCloudModel"/>). Pure REST: opens no Revit document,
/// writes nothing to the models. A "with links" toggle switches between publishing each model on
/// its own (default) and pulling in its linked models. Per-model results land in a timestamped CSV.
/// Built in code (no designer file), resizable, themed to Revit like the other dialogs.
/// </summary>
public sealed class PublishCloudModelWindow : Form
{
    private readonly DataManagementClient _dm;

    private readonly ComboBox _presetCombo;
    private readonly CheckBox _withLinks;
    private readonly TextBox _folderBox;
    private readonly TextBox _reportNameBox;
    private readonly Button _runButton;
    private readonly StatusLine _status;

    public PublishCloudModelWindow(IReadOnlyList<ModelPreset> presets, DataManagementClient dm)
    {
        _dm = dm;

        Text = "Publish Cloud Model — Publish Latest Version";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(600, 300);
        MinimumSize = new System.Drawing.Size(520, 0);

        Label note = FormLayout.NoteLabel(this,
            "Publishes each cloud model's latest synced version to Forma (the batch equivalent of " +
            "Revit's “Publish Latest Version”). No model is opened and nothing is written to " +
            "the models — only Forma's published version is refreshed. Local models in the set are " +
            "skipped; a set saved before publish support must be re-saved so it carries the publish reference.");

        _presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        // Ordered, labelled and dimmed by the signed-in-account check, and rebuilt when that answer
        // lands — the whole treatment, in the one call that carries it (ModelSetAccess.BindSetCombo).
        // This was a bare foreach until 1.5.1.2, which is why a set this account cannot open looked
        // exactly like every other set here while Edit Model Set was flagging it.
        ModelSetAccess.BindSetCombo(_presetCombo, presets,
            p => new PresetItem(p), i => (i as PresetItem)?.Preset);

        _withLinks = new CheckBox
        {
            Text = "Publish with links (also publishes each model's linked models)",
            AutoSize = true, Margin = new Padding(3, 4, 3, 4)
        };

        _folderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        browse.Click += (_, _) => BrowseForFolder();

        _reportNameBox = new TextBox { Text = "PublishCloudModel" };

        _runButton = new Button { Text = "Publish", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(90, 0) };
        _runButton.Click += (_, _) => Run();
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        var buttons = FormLayout.FlowRow(_runButton, close);
        buttons.Anchor = System.Windows.Forms.AnchorStyles.Right;

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, note);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Model set:", _presetCombo));
        FormLayout.AddAutoRow(root, _withLinks);
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Destination folder:", _folderBox, browse));
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Report name:", _reportNameBox));
        FormLayout.AddAutoRow(root, buttons);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(root);
        Controls.Add(_status);
        AcceptButton = _runButton;
        CancelButton = close;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);

        // Size the window to its content (plus the bottom status bar) so every field and button
        // is visible at the default size; it can still be grown, not shrunk below this.
        ClientSize = new System.Drawing.Size(
            Math.Max(ClientSize.Width, root.PreferredSize.Width),
            root.PreferredSize.Height + _status.Height);
        MinimumSize = Size;
    }

    private void BrowseForFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(_folderBox.Text)
                ? _folderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dlg.SelectedPath;
    }

    private void Run()
    {
        if (_presetCombo.SelectedItem is not PresetItem item) { Warn("Pick a model set."); return; }
        if (string.IsNullOrWhiteSpace(_folderBox.Text)) { Warn("Choose a destination folder for the report."); return; }

        // Folder-backed sets are re-scanned to their current members (and so pick up fresh publish
        // references); a plain set uses its saved models.
        ModelPreset resolved = PresetResolver.Resolve(item.Preset, out string? setNote);
        var cloud = resolved.Models.Where(m => !m.IsLocal).ToList();
        int localCount = resolved.Models.Count - cloud.Count;
        if (cloud.Count == 0)
        {
            Warn("This set has no cloud models to publish" + (localCount > 0 ? " (all its models are local)." : "."));
            return;
        }

        bool withLinks = _withLinks.Checked;
        if (MessageBox.Show(this,
                $"Publish {cloud.Count} cloud model(s) from '{resolved.Name}' to Forma" +
                (withLinks ? " (with links)" : "") + "?\n\n" +
                "This refreshes each model's PUBLISHED version in Forma. Continue?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        string reportName = string.IsNullOrWhiteSpace(_reportNameBox.Text) ? "PublishCloudModel" : _reportNameBox.Text.Trim();

        UseWaitCursor = true;
        _runButton.Enabled = false;
        int accepted = 0, failed = 0, skipped = 0;
        var rows = new List<IReadOnlyDictionary<string, string>>();
        try
        {
            foreach (CloudModel m in cloud)
            {
                string statusText, detail;
                if (string.IsNullOrEmpty(m.ItemUrn) || string.IsNullOrEmpty(m.DmProjectId))
                {
                    skipped++;
                    statusText = "Skipped";
                    detail = "No publish reference — re-save this set in Edit Model Set to capture it.";
                }
                else
                {
                    SetStatus($"Publishing {m.DisplayName}… ({accepted + failed + skipped + 1}/{cloud.Count})");
                    try
                    {
                        PublishResult r = _dm.PublishCloudModel(m.DmProjectId, m.ItemUrn, withLinks);
                        accepted++;
                        statusText = "Accepted";
                        detail = string.IsNullOrEmpty(r.Status) ? "accepted" : r.Status;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        statusText = "Failed";
                        detail = ex.Message.Split('\n')[0];
                    }
                }
                rows.Add(new Dictionary<string, string>
                {
                    ["Model"] = m.DisplayName,
                    ["Status"] = statusText,
                    ["Detail"] = detail,
                    ["Item URN"] = m.ItemUrn,
                });
            }

            string path = CsvReportWriter.Write(
                _folderBox.Text.Trim(), reportName,
                new[] { "Model", "Status", "Detail", "Item URN" }, rows);

            string summary = $"{accepted} accepted, {failed} failed, {skipped} skipped" +
                             (localCount > 0 ? $", {localCount} local (not published)" : "");
            SetStatus($"{summary} → {path}");
            MessageBox.Show(this,
                $"Publish complete: {summary}." + (setNote != null ? $"\n\n{setNote}" : "") + $"\n\n{path}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message.Split('\n')[0]);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _runButton.Enabled = true;
        }
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void SetStatus(string text) { _status.SetMessage(text); }

    /// <summary>Combo item that shows the set name + model count but keeps the preset.</summary>
    private sealed class PresetItem
    {
        public ModelPreset Preset { get; }
        public PresetItem(ModelPreset preset) => Preset = preset;
        public override string ToString() =>
            ModelSetAccess.Label(Preset, $"{Preset.Name}  ({Preset.Models.Count} models)");
    }
}
