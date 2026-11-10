using System.Windows.Forms;
using KhBatchRunner.Forma;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// Lightweight Forma model picker: browse the Forma tree (hubs → projects → folders) on the left,
/// check the .rvt models in the selected folder on the right, OK to return them. Used by
/// Batch Run to add Forma cloud models as ad-hoc entries alongside "Add local…". Opens
/// offline-tolerant — with no signed-in Revit session the tree simply stays empty. Shares the
/// same lazy hub→project→folder loading pattern as <see cref="FormaBrowserWindow"/>.
/// </summary>
public sealed class FormaPickerWindow : Form
{
    private const string Placeholder = "__loading__";

    private readonly DataManagementClient _client;
    private readonly TreeView _tree;
    private readonly ModelCheckGrid _modelsList;
    private readonly StatusLine _status;

    /// <summary>As <see cref="FormaBrowserWindow"/>: this Revit's version number, empty outside Revit.
    /// It matters here too — a model blocked in the set editor and selectable from Batch Run's own
    /// picker would be no guard at all.</summary>
    private static string RunningRevitVersion => HostServices.RunningRevitVersion;

    public IReadOnlyList<CloudModel> SelectedModels { get; private set; } = Array.Empty<CloudModel>();

    /// <param name="title">Window caption. Defaults to the "add models to a run" wording this
    /// window was written for; Batch Link RVT passes its own, because there the checked models are
    /// the links being placed INTO the run's models, not the models the run acts on — and a picker
    /// that says the wrong one of those is worse than no label at all.</param>
    /// <param name="prompt">Heading above the model list.</param>
    /// <param name="okText">Caption of the commit button.</param>
    public FormaPickerWindow(DataManagementClient client,
        string? title = null, string? prompt = null, string? okText = null)
    {
        _client = client;

        Text = title ?? "Add models from Forma";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(620, 440);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 340, FixedPanel = FixedPanel.None };

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        _tree.BeforeExpand += OnBeforeExpand;
        _tree.AfterSelect += OnAfterSelect;
        split.Panel1.Controls.Add(_tree);

        var right = FormLayout.SingleColumn();
        _modelsList = new ModelCheckGrid();   // Shift+click range toggling is built in
        var okBtn = new Button
        {
            Text = okText ?? "Add checked", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(110, 0), DialogResult = DialogResult.OK
        };
        okBtn.Click += (_, _) => Commit();
        var cancelBtn = new Button
        {
            Text = "Cancel", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 0), DialogResult = DialogResult.Cancel
        };
        var buttons = FormLayout.FlowRow(okBtn, cancelBtn);
        buttons.Anchor = AnchorStyles.Right;

        // A NoteLabel rather than a plain AutoSize one: this heading is a sentence, and it sits in
        // the right-hand pane of a splitter the user can drag narrower than the text. A plain label
        // loses its right-hand end silently; this one wraps.
        Label modelsHeading = FormLayout.NoteLabel(this,
            prompt ?? "Models in selected Forma folder (.rvt) — check to add:");
        modelsHeading.Margin = new Padding(3, 6, 3, 2);
        FormLayout.AddAutoRow(right, modelsHeading);
        FormLayout.AddFillRow(right, _modelsList, 100);
        FormLayout.AddAutoRow(right, buttons);
        split.Panel2.Controls.Add(right);

        _status = new StatusLine { DetailsTitle = Text };

        Controls.Add(split);
        Controls.Add(_status);
        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        Theme.Apply(this);

        Load += (_, _) =>
        {
            try { split.SplitterDistance = 340; } catch { /* clamped by min sizes */ }
            LoadHubs();
        };
    }

    private void Commit() =>
        SelectedModels = _modelsList.CheckedModels.Select(i => i.ToCloudModel()).ToList();

    // ---- tree loading (same lazy hub→project→folder pattern as FormaBrowserWindow) ----

    private void LoadHubs() => RunGuarded("Loading hubs…", () =>
    {
        _tree.Nodes.Clear();
        foreach (FormaEntry hub in _client.GetHubs())
            _tree.Nodes.Add(MakeNode(hub));
        SetStatus(_tree.Nodes.Count == 0
            ? "No hubs found. Are you signed in to Forma in Revit?"
            : $"{_tree.Nodes.Count} hub(s).");
    });

    private TreeNode MakeNode(FormaEntry entry)
    {
        var node = new TreeNode(entry.Name) { Tag = entry };
        node.Nodes.Add(new TreeNode(Placeholder));
        return node;
    }

    private void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node is null) return;
        if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Text != Placeholder) return;
        if (e.Node.Tag is not FormaEntry entry) return;

        RunGuarded($"Loading {entry.Name}…", () =>
        {
            e.Node.Nodes.Clear();
            List<FormaEntry> children = entry.Kind switch
            {
                FormaKind.Hub => _client.GetProjects(entry),
                FormaKind.Project => _client.GetTopFolders(entry),
                FormaKind.Folder => _client.GetSubFolders(entry),
                _ => new List<FormaEntry>()
            };
            foreach (FormaEntry child in children)
                e.Node.Nodes.Add(MakeNode(child));
            SetStatus($"{entry.Name}: {children.Count} item(s).");
        });
    }

    private void OnAfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node?.Tag is not FormaEntry entry || entry.Kind != FormaKind.Folder) return;

        RunGuarded($"Loading models in {entry.Name}…", () =>
        {
            _modelsList.SetModels(_client.GetRevitModels(entry), RunningRevitVersion);
            SetStatus($"{entry.Name}: {_client.LastScanNote ?? $"{_modelsList.Count} .rvt model(s)."}");
        });
    }

    private void RunGuarded(string busy, Action work)
    {
        SetStatus(busy);
        UseWaitCursor = true;
        try { work(); }
        catch (Exception ex) { SetStatus("Error: " + ex.Message.Split('\n')[0]); }
        finally { UseWaitCursor = false; }
    }

    private void SetStatus(string text) { _status.SetMessage(text); }
}
