using System.Windows.Forms;
using KhBatchRunner.Forma;

namespace KhBatchRunner.UI;

/// <summary>
/// Tree-only Forma folder picker: browse hubs → projects → folders. Two modes:
/// <list type="bullet">
/// <item><b>Single</b> (default): "Select folder" returns the selection (<see cref="SelectedFolder"/>)
/// and closes — used to pick ONE target folder (Initiate / Cut model / relink host folder).</item>
/// <item><b>Multi</b> (<c>multiPick: true</c>): "Add folder" collects the selection into
/// <see cref="PickedFolders"/> WITHOUT closing — the tree (and its slow lazy crawl) stays loaded so
/// several folders can be added in one session; "Done" closes and the caller takes the list. Used
/// by Internal Relink's target folders.</item>
/// </list>
/// Reuses <see cref="FormaTreeController"/> for the lazy load; the action button stays disabled until
/// a folder (not a hub/project) is selected. Opens offline-tolerant — an empty tree just can't OK.
/// </summary>
public sealed class FormaFolderPickerWindow : Form
{
    private readonly Button _okButton;
    private readonly Label _prompt;
    private readonly string _promptText;
    private readonly bool _multiPick;

    /// <summary>Single mode: the chosen folder, or null if cancelled.</summary>
    public FormaEntry? SelectedFolder { get; private set; }

    /// <summary>Single mode: human-readable path of the chosen folder (the tree node's full path).</summary>
    public string SelectedFolderPath { get; private set; } = "";

    /// <summary>Multi mode: every folder added with "Add folder", deduped by URN, in click order.</summary>
    public List<FolderRef> PickedFolders { get; } = new();

    public FormaFolderPickerWindow(DataManagementClient client, string title, string prompt, bool multiPick = false)
    {
        _promptText = prompt;
        _multiPick = multiPick;

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(520, 520);
        MinimumSize = new Size(420, 400);

        var tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };

        _prompt = new Label { Text = prompt, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };

        _okButton = new Button
        {
            Text = multiPick ? "Add folder" : "Select folder", Enabled = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(110, 0)
        };
        Control[] buttonRow;
        if (multiPick)
        {
            // "Add folder" keeps the window (and the loaded tree) open; "Done" hands the list back.
            _okButton.Click += (_, _) => AddCurrentFolder(tree);
            var done = new Button
            {
                Text = "Done", DialogResult = DialogResult.OK,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0)
            };
            var cancelBtn = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0)
            };
            buttonRow = new Control[] { _okButton, done, cancelBtn };
            AcceptButton = _okButton;
            CancelButton = cancelBtn;
        }
        else
        {
            _okButton.DialogResult = DialogResult.OK;
            _okButton.Click += (_, _) => CommitSelection(tree);
            var cancelBtn = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0)
            };
            buttonRow = new Control[] { _okButton, cancelBtn };
            AcceptButton = _okButton;
            CancelButton = cancelBtn;
        }
        var buttons = FormLayout.FlowRow(buttonRow);
        buttons.Anchor = AnchorStyles.Right;

        // Single-column table (proven layout): prompt (auto) · tree (fills) · buttons (auto, right).
        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, _prompt);
        FormLayout.AddFillRow(root, tree, 100);
        FormLayout.AddAutoRow(root, buttons);

        var status = new StatusLine { DetailsTitle = Text };

        // root fills; status added last so it docks to the bottom and root fills above it.
        Controls.Add(root);
        Controls.Add(status);

        var controller = new FormaTreeController(tree, client, this, t => status.SetMessage(t));
        controller.FolderSelected += folder =>
        {
            _okButton.Enabled = folder != null;
            _prompt.Text = folder != null
                ? $"{BasePrompt()}\nSelected: {tree.SelectedNode?.FullPath}"
                : BasePrompt();
        };

        Theme.Apply(this);
        Load += (_, _) => controller.LoadHubs();
    }

    /// <summary>Prompt line, with a running count of added folders in multi mode.</summary>
    private string BasePrompt() =>
        _multiPick && PickedFolders.Count > 0
            ? $"{_promptText}  ({PickedFolders.Count} folder(s) added)"
            : _promptText;

    private void CommitSelection(TreeView tree)
    {
        SelectedFolder = tree.SelectedNode?.Tag as FormaEntry;
        SelectedFolderPath = tree.SelectedNode?.FullPath ?? "";
        if (SelectedFolder is null || SelectedFolder.Kind != FormaKind.Folder)
            DialogResult = DialogResult.None; // guard: don't close on a non-folder selection
    }

    private void AddCurrentFolder(TreeView tree)
    {
        if (tree.SelectedNode?.Tag is not FormaEntry entry || entry.Kind != FormaKind.Folder) return;
        string path = tree.SelectedNode.FullPath;
        if (!PickedFolders.Any(f => f.FolderUrn == entry.Id))
            PickedFolders.Add(FolderRef.FromEntry(entry, path));
        _prompt.Text = $"{BasePrompt()}\nAdded: {path}";
    }
}
