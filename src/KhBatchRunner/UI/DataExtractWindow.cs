using System.IO;
using KhBatchRunner.Forma;
using KhBatchRunner.Batch;
using KhBatchRunner.Logging;
using KhBatchRunner.Schedule;

namespace KhBatchRunner.UI;

/// <summary>
/// The Forma <b>Data Extract</b> dialog (Get Data Extract): point at an extract Forma already runs,
/// and download the newest run's CSVs — a pure Data Connector REST pull that opens no Revit
/// document.
///
/// <para><b>It collects; it does not extract.</b> The extract itself is set up and scheduled in
/// Forma's own Data Connector page (typically overnight), and this dialog lists what that page has
/// produced. Creating a request from here would add a second schedule to the account competing
/// with the one already there — so the account and request combos are a chooser, never an editor.</para>
///
/// <para>It is also the EDITOR for a saved <see cref="DataExtractInputSet"/>. The same named set is
/// what Schedule Run points a job at, so the download proved here by hand is the download that
/// happens at 06:00.</para>
///
/// <para>Runs in TWO hosts — the Revit command and the standalone controller — so nothing here may
/// touch the Revit API.</para>
/// </summary>
public sealed class DataExtractWindow : Form
{
    private readonly DataManagementClient _dm;
    private readonly DataConnectorClient _dc;

    private readonly ComboBox _hubCombo;
    private readonly ComboBox _requestCombo;
    private readonly Button _refreshButton;
    private readonly Label _latestLabel;
    private readonly CheckBox _zipInstead;
    private readonly CheckBox _skipDownloaded;
    private readonly TextBox _setNameBox;
    private readonly ComboBox _setCombo;
    private readonly TextBox _folderBox;
    private readonly Button _runButton;
    private readonly StatusLine _status;

    /// <summary>Name of the set currently loaded, so Save can RENAME rather than leave a duplicate.</summary>
    private string? _loadedSetName;

    /// <summary>
    /// The newest successful run of the chosen extract, as last looked up — what Download will
    /// fetch. Null until an extract is chosen and its runs have been read.
    /// </summary>
    private DataConnectorJob? _latestSuccessful;

    /// <summary>Suppresses the combo change handlers while a set is being applied, so loading a set
    /// does not fire a lookup per control it fills.</summary>
    private bool _applying;

    public DataExtractWindow(DataManagementClient dm, DataConnectorClient dc)
    {
        _dm = dm;
        _dc = dc;

        Text = "Forma Extract — Get Data Extract";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(760, 480);
        // No Forma tree here (the chooser is two combos), so this dialog is all fixed rows: the
        // minimum is what those rows plus the button strip actually need, measured by the layout
        // gate rather than guessed.
        MinimumSize = new Size(640, 495);   // +25 in 2.0.0: "Forma" wraps the intro one line longer

        Label note = FormLayout.NoteLabel(this,
            "Downloads the newest finished run of an extract that Forma's Data Connector already " +
            "produces — it does not start one. Set the extract up in Forma (Insight → Data " +
            "Connector); this saves its CSVs to a folder, by hand or on a schedule.");

        _hubCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _hubCombo.SelectedIndexChanged += (_, _) => { if (!_applying) ReloadRequests(null); };

        _requestCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _requestCombo.SelectedIndexChanged += (_, _) => { if (!_applying) ReloadLatestRun(); };

        _refreshButton = new Button
        {
            Text = "Check now",
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(90, 0)
        };
        _refreshButton.Click += (_, _) => ReloadLatestRun();

        // The one line that answers "is last night's data there?" without opening Forma. Kept to a
        // single row so it cannot push the button strip off a small dialog.
        _latestLabel = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 2) };

        _zipInstead = new CheckBox
        {
            Text = "Download the ZIP instead of the individual CSVs", AutoSize = true,
            Margin = new Padding(3, 6, 3, 2)
        };
        _skipDownloaded = new CheckBox
        {
            Text = "Skip if this run was already downloaded here", AutoSize = true, Checked = true,
            Margin = new Padding(3, 2, 3, 4)
        };

        _setNameBox = new TextBox();
        var saveSet = new Button { Text = "Save", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(70, 0) };
        saveSet.Click += (_, _) => SaveSet();

        _setCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        var loadSet = new Button { Text = "Load", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(60, 0) };
        loadSet.Click += (_, _) => LoadSelectedSet();
        var dupSet = new Button { Text = "Duplicate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        dupSet.Click += (_, _) => DuplicateSet();
        var deleteSet = new Button { Text = "Delete", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(60, 0) };
        deleteSet.Click += (_, _) => DeleteSet();

        _folderBox = new TextBox { Text = Settings.UserSettings.DefaultDestinationFolder };
        var browse = new Button { Text = "Browse…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
        browse.Click += (_, _) => BrowseForFolder();

        _runButton = new Button
        {
            Text = "Download", Enabled = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 0)
        };
        _runButton.Click += (_, _) => RunDownload();
        var close = new Button
        {
            Text = "Close", DialogResult = DialogResult.Cancel,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0)
        };
        var buttons = FormLayout.FlowRow(_runButton, close);
        buttons.Anchor = AnchorStyles.Right;

        var body = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(body, note);
        FormLayout.AddAutoRow(body, FormLayout.LabeledRow("Forma account:", _hubCombo));
        FormLayout.AddAutoRow(body, FormLayout.LabeledRow("Extract:", _requestCombo, _refreshButton));
        FormLayout.AddAutoRow(body, _latestLabel);
        FormLayout.AddAutoRow(body, _zipInstead);
        FormLayout.AddAutoRow(body, _skipDownloaded);
        FormLayout.AddAutoRow(body, FormLayout.LabeledRow("Set name:", _setNameBox, saveSet));
        FormLayout.AddAutoRow(body, FormLayout.LabeledRow("Saved sets:", _setCombo, loadSet, dupSet, deleteSet));
        FormLayout.AddAutoRow(body, FormLayout.LabeledRow("Destination folder:", _folderBox, browse));
        // The spacer row, not the rows above it, absorbs a resize — every row here is fixed, and
        // without one the table hands the extra height to whichever row is last.
        FormLayout.AddFillRow(body, new Panel(), 100);
        FormLayout.AddAutoRow(body, buttons);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(body);
        Controls.Add(_status);
        CancelButton = close;

        Theme.Apply(this);
        FormLayout.AlignLabels(this);
        note.ForeColor = Theme.Dim(note);
        _latestLabel.ForeColor = Theme.Dim(_latestLabel);

        Load += (_, _) =>
        {
            ReloadSets(null);
            LoadHubs(DataExtractSetStore.TryLoadLastUsed());
        };
    }

    // ---- Forma lookups -----------------------------------------------------------

    /// <summary>
    /// Fills the account combo, then re-applies <paramref name="pending"/> (the last-used or loaded
    /// set) on top of it.
    ///
    /// <para>The set is applied AFTER the hubs arrive rather than before, because a set names its
    /// hub by id and the combo holds hub objects — selecting one before the list exists silently
    /// leaves the dialog pointing at nothing.</para>
    /// </summary>
    private void LoadHubs(DataExtractInputSet? pending)
    {
        SetStatus("Reading Forma accounts…");
        UseWaitCursor = true;
        try
        {
            _hubCombo.Items.Clear();
            foreach (FormaEntry hub in _dm.GetHubs()) _hubCombo.Items.Add(hub);
            SetStatus(_hubCombo.Items.Count == 0
                ? "No Forma accounts found for the signed-in user."
                : $"{_hubCombo.Items.Count} Forma account(s).");
        }
        catch (Exception ex)
        {
            SetStatus("Could not read Forma accounts: " + ex.Message);
            return;
        }
        finally { UseWaitCursor = false; }

        if (pending != null) ApplySet(pending);
        else if (_hubCombo.Items.Count == 1) { _hubCombo.SelectedIndex = 0; }
    }

    /// <summary>The extracts on the selected account, newest first.</summary>
    private void ReloadRequests(string? selectRequestId)
    {
        _requestCombo.Items.Clear();
        _latestSuccessful = null;
        UpdateRunEnabled();
        if (_hubCombo.SelectedItem is not FormaEntry hub) return;

        SetStatus($"Reading extracts on {hub.Name}…");
        UseWaitCursor = true;
        try
        {
            List<DataConnectorRequest> requests = _dc.GetRequests(hub.Id);
            // Turn the GUIDs in Forma's own extract names into project names before anything is
            // shown. One extra request for the hub's project list, best-effort — see NameProjects.
            DataConnectorClient.NameProjects(_dm, hub, requests);
            foreach (DataConnectorRequest r in requests
                         .OrderByDescending(r => r.CreatedAt ?? DateTime.MinValue))
                _requestCombo.Items.Add(r);

            if (requests.Count == 0)
            {
                // Named rather than a bare "none": the API returns the extracts the SIGNED-IN user
                // created, so a colleague's extract is invisible here however senior the caller —
                // which is the first thing to check when this list is unexpectedly empty.
                SetStatus($"No extracts on {hub.Name} for the signed-in Autodesk account. " +
                          "Data Connector only lists extracts created by that account.");
                _latestLabel.Text = "";
                return;
            }

            if (selectRequestId != null)
            {
                for (int i = 0; i < _requestCombo.Items.Count; i++)
                    if (_requestCombo.Items[i] is DataConnectorRequest r &&
                        r.Id.Equals(selectRequestId, StringComparison.OrdinalIgnoreCase))
                    { _requestCombo.SelectedIndex = i; return; }
                // The saved set points at an extract this account no longer lists — deleted in Forma,
                // or created by someone else. Said plainly, because the combo would otherwise just
                // look like it defaulted.
                SetStatus("The saved set's extract is not in this account's list any more — choose one.");
                return;
            }
            SetStatus($"{requests.Count} extract(s) on {hub.Name}.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not read the extracts: " + ex.Message);
            _latestLabel.Text = "";
        }
        finally { UseWaitCursor = false; }
    }

    /// <summary>Looks up the chosen extract's newest run, and says what Download would fetch.</summary>
    private void ReloadLatestRun()
    {
        _latestSuccessful = null;
        _latestLabel.Text = "";
        UpdateRunEnabled();

        if (_hubCombo.SelectedItem is not FormaEntry hub ||
            _requestCombo.SelectedItem is not DataConnectorRequest request) return;

        SetStatus("Looking for the latest finished run…");
        UseWaitCursor = true;
        try
        {
            (DataConnectorJob? latest, DataConnectorJob? successful) =
                _dc.GetLatestJobs(hub.Id, request.Id);
            _latestSuccessful = successful;

            if (successful != null)
            {
                string when = successful.CompletedAt is { } done
                    ? done.ToString("ddd d MMM, HH:mm") : "an unknown time";
                _latestLabel.Text = $"Latest finished run: {when}" +
                    (latest != null && latest.Id != successful.Id
                        ? $"   (a newer run is {latest.StateLabel})" : "");
                SetStatus("Ready to download.");
            }
            else if (latest != null)
            {
                _latestLabel.Text = $"No finished run yet — the latest is {latest.StateLabel}.";
                SetStatus("Nothing to download yet.");
            }
            else
            {
                _latestLabel.Text = "This extract has never run.";
                SetStatus("Nothing to download yet.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Could not read the extract's runs: " + ex.Message);
        }
        finally
        {
            UseWaitCursor = false;
            UpdateRunEnabled();
        }
    }

    // ---- set state -------------------------------------------------------------

    /// <summary>The dialog's current state as a set — the exact inverse of <see cref="ApplySet"/>.</summary>
    private DataExtractInputSet Current(string name) => new()
    {
        Name = name,
        HubId = (_hubCombo.SelectedItem as FormaEntry)?.Id ?? "",
        HubName = (_hubCombo.SelectedItem as FormaEntry)?.Name ?? "",
        RequestId = (_requestCombo.SelectedItem as DataConnectorRequest)?.Id ?? "",
        RequestName = (_requestCombo.SelectedItem as DataConnectorRequest)?.DisplayName ?? "",
        ProjectId = (_requestCombo.SelectedItem as DataConnectorRequest)?.ProjectId ?? "",
        ProjectName = (_requestCombo.SelectedItem as DataConnectorRequest)?.ProjectName ?? "",
        DownloadZipInstead = _zipInstead.Checked,
        SkipIfAlreadyDownloaded = _skipDownloaded.Checked,
        DestinationFolder = _folderBox.Text.Trim(),
        ProjectLogName = DataExtractStep.DisplayName,
    };

    private void ApplySet(DataExtractInputSet? set)
    {
        if (set is null) return;
        _applying = true;
        try
        {
            _setNameBox.Text = set.Name;
            _loadedSetName = string.IsNullOrWhiteSpace(set.Name) ? null : set.Name;
            _zipInstead.Checked = set.DownloadZipInstead;
            _skipDownloaded.Checked = set.SkipIfAlreadyDownloaded;
            if (!string.IsNullOrWhiteSpace(set.DestinationFolder))
                _folderBox.Text = set.DestinationFolder;

            for (int i = 0; i < _hubCombo.Items.Count; i++)
                if (_hubCombo.Items[i] is FormaEntry hub &&
                    hub.Id.Equals(set.HubId, StringComparison.OrdinalIgnoreCase))
                { _hubCombo.SelectedIndex = i; break; }
        }
        finally { _applying = false; }

        // Outside the guard: the request list and the run lookup are exactly what a restored set
        // should trigger, once, now that the controls it fills are in place.
        if (_hubCombo.SelectedItem is FormaEntry) ReloadRequests(set.RequestId);
    }

    private void ReloadSets(string? select)
    {
        _setCombo.Items.Clear();
        foreach (DataExtractInputSet set in DataExtractSetStore.LoadAll()) _setCombo.Items.Add(set);
        if (select == null) return;
        for (int i = 0; i < _setCombo.Items.Count; i++)
            if (_setCombo.Items[i] is DataExtractInputSet s &&
                s.Name.Equals(select, StringComparison.OrdinalIgnoreCase))
            { _setCombo.SelectedIndex = i; return; }
    }

    private void LoadSelectedSet()
    {
        if (_setCombo.SelectedItem is not DataExtractInputSet set) { Warn("Choose a saved set first."); return; }
        ApplySet(set.Clone());
        SetStatus($"Loaded set '{set.Name}' — {set.TargetLabel}, {set.ScopeLabel}.");
    }

    private void SaveSet()
    {
        string name = _setNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { Warn("Enter a set name to save."); return; }
        if (!Current(name).HasTarget) { Warn("Choose an account and an extract before saving."); return; }

        // Renaming a loaded set moves it rather than leaving the old name behind as a stale twin.
        bool renaming = _loadedSetName != null &&
                        !name.Equals(_loadedSetName, StringComparison.OrdinalIgnoreCase);
        if (DataExtractSetStore.Exists(name) && (_loadedSetName == null || renaming))
        {
            if (MessageBox.Show(this, $"A set named '{name}' already exists. Replace it?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }

        try
        {
            DataExtractSetStore.Save(Current(name));
            if (renaming) DataExtractSetStore.Delete(_loadedSetName!);
            _loadedSetName = name;
            ReloadSets(name);
            SetStatus($"Saved set '{name}'.");
        }
        catch (Exception ex) { Warn("Could not save the set: " + First(ex)); }
    }

    /// <summary>
    /// Forks the selected set to a new "&lt;name&gt; (copy)" and loads the copy — see
    /// <c>FormaExtractWindow.DuplicateSet</c> for why <see cref="ApplySet"/> must re-point
    /// <c>_loadedSetName</c> at the copy.
    /// </summary>
    private void DuplicateSet()
    {
        if (_setCombo.SelectedItem is not DataExtractInputSet set) { Warn("Choose a saved set first."); return; }
        DataExtractInputSet copy = set.Clone();
        copy.Name = SetDuplication.UniqueName(set.Name, DataExtractSetStore.LoadAll().Select(s => s.Name));
        try
        {
            DataExtractSetStore.Save(copy);
        }
        catch (Exception ex) { Warn("Could not duplicate the set: " + First(ex)); return; }
        ReloadSets(copy.Name);
        ApplySet(copy);
        SetStatus($"Duplicated '{set.Name}' as '{copy.Name}'.");
    }

    private void DeleteSet()
    {
        if (_setCombo.SelectedItem is not DataExtractInputSet set) { Warn("Choose a saved set first."); return; }
        if (MessageBox.Show(this, $"Delete the saved set '{set.Name}'?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            DataExtractSetStore.Delete(set.Name);
            if (string.Equals(_loadedSetName, set.Name, StringComparison.OrdinalIgnoreCase))
                _loadedSetName = null;
            ReloadSets(null);
            SetStatus($"Deleted set '{set.Name}'.");
        }
        catch (Exception ex) { Warn("Could not delete the set: " + First(ex)); }
    }

    // ---- run -------------------------------------------------------------------

    private void UpdateRunEnabled() => _runButton.Enabled = _latestSuccessful != null;

    private void BrowseForFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(_folderBox.Text)
                ? _folderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _folderBox.Text = dlg.SelectedPath;
    }

    private void RunDownload()
    {
        if (string.IsNullOrWhiteSpace(_folderBox.Text)) { Warn("Choose a destination folder."); return; }

        DataExtractInputSet set = Current(_setNameBox.Text.Trim());
        if (!set.HasTarget) { Warn("Choose an account and an extract."); return; }
        string dest = set.DestinationFolder;
        // Remembered even when the set was never named, so reopening the dialog resumes where it
        // left off — the same courtesy every other input-set dialog extends.
        Settings.UserSettings.LastDestinationFolder = _folderBox.Text.Trim();
        DataExtractSetStore.SaveLastUsed(set);

        DateTime stamp = DateTime.Now;
        string trackingLog = TrackingCsvLogger.PathFor(
            dest, DataExtractStep.DisplayName, DataExtractStep.DisplayName, stamp);

        SetStatus("Downloading…");
        UseWaitCursor = true;
        _runButton.Enabled = false;
        try
        {
            // The same body the schedule runs, so an interactive proof is a proof of the scheduled
            // run too — including the tracking log.
            DataExtractResult result = DataExtractStep.RunSet(
                set, dest, DataExtractStep.DisplayName, stamp, trackingLog, SetStatus);

            string summary = result.SkippedAlreadyDownloaded
                ? "That run was already downloaded here — nothing to do."
                : $"{result.Downloaded} file(s) downloaded" +
                  (result.FailedFiles > 0 ? $"\n{result.FailedFiles} file(s) FAILED — see the log." : "");
            SetStatus(summary.Replace('\n', ' '));
            MessageBox.Show(this,
                summary +
                (string.IsNullOrEmpty(result.RunFolder) ? "" : "\n\nFiles: " + result.RunFolder) +
                "\nLog: " + trackingLog,
                Text, MessageBoxButtons.OK,
                result.FailedFiles > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            UpdateRunEnabled();
        }
    }

    private static string First(Exception ex) => ex.Message.Split('\n')[0];

    private void Warn(string text) =>
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void SetStatus(string text) { _status.SetMessage(text); }
}
