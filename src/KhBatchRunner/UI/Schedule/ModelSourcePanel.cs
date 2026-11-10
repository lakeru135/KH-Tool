using System.IO;
using KhBatchRunner.Forma;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;

// Folder UI\Schedule\ groups the shared schedule panels; the NAMESPACE stays KhBatchRunner.UI —
// see ScheduleJobListPanel for why nesting it shadows KhBatchRunner.Schedule.
namespace KhBatchRunner.UI;

/// <summary>
/// "Which models does this job run over" — the saved model-set checklist beside the ad-hoc list,
/// with its Add local / Add Forma / Remove buttons.
///
/// <para><b>Why this is shared (1.4.0).</b> Every Schedule* window carried its own copy of the two
/// lists and the six methods behind them (<c>RefreshAdHocList</c>, <c>RemoveSelectedAdHoc</c>,
/// <c>MergeAdHoc</c>, <c>AddAdHocModels</c>, <c>AddAdHocFromForma</c>). Four of the six were
/// byte-identical in all four windows; the other two differed only in line wrapping. The one real
/// difference was that Schedule Run also refreshed its selection summary — which is what
/// <paramref name="onChanged"/> exists for, rather than a fifth copy.</para>
///
/// <para><b>De-duplication is by identity, not by name</b> — see
/// <see cref="ScheduleRules.AdHocKey"/>. The same model added once from disk and once from Forma is
/// one entry.</para>
/// </summary>
internal sealed class ModelSourcePanel : TableLayoutPanel
{
    private readonly DimmableCheckedListBox _modelSets;
    private readonly CheckBox _accessFilter = null!;

    /// <summary>
    /// Sets that must stay listed however the filter is set, because the job being edited names
    /// them. A job silently losing a ticked set because the account changed would turn a visible
    /// problem into an invisible one — the opposite of the point.
    /// </summary>
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox _adHocList;
    private readonly List<CloudModel> _adHoc = new();
    private readonly DataManagementClient _client;
    private readonly Action<string> _setStatus;
    private readonly Action? _onChanged;

    /// <param name="onChanged">Called whenever the ad-hoc list changes. Schedule Run uses it to
    /// re-count its selection summary; the other three pass nothing.</param>
    /// <param name="enableRangeCheck">Shift-click range checking on the model-set list. Only
    /// Schedule Run and a since-retired schedule window had it before 1.4.0; passing it through keeps this a
    /// structural change rather than quietly giving it to the other two.</param>
    public ModelSourcePanel(
        DataManagementClient client,
        Action<string> setStatus,
        Action? onChanged = null,
        bool enableRangeCheck = false,
        string adHocTitle = "Ad-hoc models (local + Forma)",
        float modelSetsPercent = 50)
    {
        _client = client;
        _setStatus = setStatus;
        _onChanged = onChanged;

        // DimmableCheckedListBox, not CheckedListBox: a plain one cannot be owner-drawn, so the
        // account greying was invisible here until 1.5.1.2. See that class.
        _modelSets = new DimmableCheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        ModelSetAccess.AttachGreying(_modelSets, i => (i as ModelSetItem)?.Preset);
        RebuildModelSets();
        if (enableRangeCheck) CheckListBehavior.EnableRangeCheck(_modelSets);

        // The account check is REST-bound, so the list is built and shown first and relabelled when
        // the answer lands. Until then every set reads exactly as it did before 1.5.0.
        _accessFilter = ModelSetAccess.FilterCheckBox(RebuildModelSets);
        ModelSetAccess.BeginRefresh(this, client, () =>
        {
            RebuildModelSets();
            string note = ModelSetAccess.StatusNote();
            if (note.Length > 0) _setStatus(note);
        });

        _adHocList = new ListBox
        {
            Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended
        };

        var adHocButtons = FormLayout.FlowRow(
            Mk("Add local…", 80, AddAdHocModels),
            Mk("Add Forma…", 80, AddAdHocFromForma),
            Mk("Remove", 60, RemoveSelectedAdHoc));
        adHocButtons.Dock = DockStyle.Fill;
        adHocButtons.WrapContents = true;

        // Floors under each column and under the row holding them: the percent rows in these dialogs
        // absorb every shortfall, and without a floor the lists render one row high while the dialog
        // still looks like it works.
        foreach (Control list in new Control[] { _modelSets, _adHocList })
            list.MinimumSize = new Size(0, 130);

        Dock = DockStyle.Fill;
        ColumnCount = 2;
        RowCount = 1;
        Margin = new Padding(0);
        MinimumSize = new Size(0, 240);
        // Schedule Run sits this panel beside a third list of its own and passes the split its
        // three tuned columns had, so its proportions survive the move.
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, modelSetsPercent));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - modelSetsPercent));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(FormLayout.TitledColumn("Model sets", _modelSets, _accessFilter), 0, 0);
        Controls.Add(FormLayout.TitledColumn(adHocTitle, _adHocList, adHocButtons), 1, 0);
    }

    /// <summary>
    /// Repopulates the checklist from the store, preserving which sets are ticked. Called on first
    /// build, when the access filter is toggled, and when the account snapshot arrives.
    /// </summary>
    private void RebuildModelSets()
    {
        var ticked = new HashSet<string>(
            _modelSets.CheckedItems.Cast<ModelSetItem>().Select(i => ModelSetAccess.SetKey(i.Preset)),
            StringComparer.OrdinalIgnoreCase);

        _modelSets.BeginUpdate();
        _modelSets.Items.Clear();
        // Ordered, so the sets this account cannot open sink to the bottom of the list.
        foreach (ModelPreset p in ModelSetAccess.Ordered(PresetStore.LoadAll()))
        {
            if (ModelSetAccess.Hidden(p) && !_pinned.Contains(p.Id) && !_pinned.Contains(p.Name)) continue;
            int idx = _modelSets.Items.Add(new ModelSetItem(p));
            if (ticked.Contains(ModelSetAccess.SetKey(p))) _modelSets.SetItemChecked(idx, true);
        }
        _modelSets.EndUpdate();
        _onChanged?.Invoke();
    }

    /// <summary>The model-set checklist, for a window that also drives it (Schedule Run re-counts
    /// its summary on every tick).</summary>
    public CheckedListBox ModelSets => _modelSets;

    public List<string> CheckedNames =>
        _modelSets.CheckedItems.Cast<ModelSetItem>().Select(i => i.Preset.Name).ToList();

    /// <summary>Stable ids of the checked sets. Only the windows that already persisted them write
    /// them — see <see cref="Load"/> for why reading them back is safe everywhere.</summary>
    public List<string> CheckedIds =>
        _modelSets.CheckedItems.Cast<ModelSetItem>().Select(i => i.Preset.Id).ToList();

    public List<CloudModel> AdHocModels => new(_adHoc);

    /// <summary>True when the job has at least one source — the check every window makes before
    /// building a job.</summary>
    public bool HasAnySource => _modelSets.CheckedItems.Count > 0 || _adHoc.Count > 0;

    /// <summary>
    /// Ticks the sets this job names and fills its ad-hoc list.
    ///
    /// <para>Matched by stable id FIRST and name second. A job that carries no ids — which is every
    /// job Schedule Cut and Schedule Interop have ever saved — falls straight through to the name
    /// match, so this is identical to what those two did before. For the windows that do persist
    /// ids it keeps the rename-safety 1.1.0 added.</para>
    /// </summary>
    public void Load(ScheduledJob job)
    {
        // Pin first, then rebuild: a set this job runs over must be visible to be ticked, whatever
        // the filter says about the account.
        _pinned.Clear();
        foreach (string id in job.ModelSetIds) _pinned.Add(id);
        foreach (string name in job.ModelSetNames) _pinned.Add(name);
        RebuildModelSets();

        for (int i = 0; i < _modelSets.Items.Count; i++)
        {
            ModelPreset preset = ((ModelSetItem)_modelSets.Items[i]).Preset;
            bool chk = (job.ModelSetIds.Count > 0 && job.ModelSetIds.Contains(preset.Id))
                       || job.ModelSetNames.Contains(preset.Name);
            _modelSets.SetItemChecked(i, chk);
        }

        _adHoc.Clear();
        _adHoc.AddRange(job.AdHocModels);
        RefreshAdHocList();
    }

    /// <summary>Clears everything, for "New schedule".</summary>
    public void Clear()
    {
        _pinned.Clear();
        RebuildModelSets();
        for (int i = 0; i < _modelSets.Items.Count; i++) _modelSets.SetItemChecked(i, false);
        _adHoc.Clear();
        RefreshAdHocList();
    }

    private static Button Mk(string text, int minW, Action onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(minW, 0)
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void RefreshAdHocList()
    {
        _adHocList.Items.Clear();
        foreach (CloudModel m in _adHoc)
            _adHocList.Items.Add(new AdHocItem(m));
        _onChanged?.Invoke();
    }

    private void RemoveSelectedAdHoc()
    {
        var keys = _adHocList.SelectedItems.Cast<AdHocItem>()
            .Select(i => ScheduleRules.AdHocKey(i.Model)).ToHashSet();
        _adHoc.RemoveAll(m => keys.Contains(ScheduleRules.AdHocKey(m)));
        RefreshAdHocList();
    }

    private int MergeAdHoc(IEnumerable<CloudModel> models)
    {
        var existing = _adHoc.Select(ScheduleRules.AdHocKey).ToHashSet();
        int added = 0;
        foreach (CloudModel m in models)
            if (existing.Add(ScheduleRules.AdHocKey(m))) { _adHoc.Add(m); added++; }
        if (added > 0) RefreshAdHocList();
        return added;
    }

    private void AddAdHocModels()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add local Revit models",
            Filter = "Revit models (*.rvt)|*.rvt|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int added = MergeAdHoc(dlg.FileNames.Select(p =>
            new CloudModel { DisplayName = Path.GetFileName(p), LocalPath = p }));
        _setStatus(added == 0 ? "Nothing new to add." : $"Added {added} local model(s).");
    }

    private void AddAdHocFromForma()
    {
        using var picker = new FormaPickerWindow(_client);
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        int added = MergeAdHoc(picker.SelectedModels);
        _setStatus(added == 0 ? "Nothing new to add." : $"Added {added} Forma model(s).");
    }
}
