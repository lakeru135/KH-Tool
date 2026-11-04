using System.Windows.Forms;
using KhBatchRunner.Forma;

namespace KhBatchRunner.UI;

/// <summary>
/// Wires a <see cref="TreeView"/> to <see cref="DataManagementClient"/> for lazy
/// hub → project → folder loading — the same pattern <see cref="FormaPickerWindow"/> and
/// <see cref="FormaBrowserWindow"/> each grew their own copy of, extracted here so the newer
/// Forma-Batch-Action windows (folder picker, extract) share one implementation. The host
/// window supplies a status sink and owns the wait cursor; the controller raises
/// <see cref="FolderSelected"/> as the selection moves onto (or off) a folder node.
/// Offline-tolerant: with no signed-in Revit session the tree simply stays empty.
/// </summary>
internal sealed class FormaTreeController
{
    private const string Placeholder = "__loading__";

    private readonly TreeView _tree;
    private readonly DataManagementClient _client;
    private readonly Form _host;
    private readonly Action<string> _setStatus;

    /// <summary>Raised with the selected folder, or null when the selection isn't a folder.</summary>
    public event Action<FormaEntry?>? FolderSelected;

    public FormaTreeController(TreeView tree, DataManagementClient client, Form host, Action<string> setStatus)
    {
        _tree = tree;
        _client = client;
        _host = host;
        _setStatus = setStatus;
        _tree.BeforeExpand += OnBeforeExpand;
        _tree.AfterSelect += OnAfterSelect;
    }

    public void LoadHubs() => RunGuarded("Loading hubs…", () =>
    {
        _tree.Nodes.Clear();
        foreach (FormaEntry hub in _client.GetHubs())
            _tree.Nodes.Add(MakeNode(hub));
        _setStatus(_tree.Nodes.Count == 0
            ? "No hubs found. Are you signed in to Forma in Revit?"
            : $"{_tree.Nodes.Count} hub(s).");
    });

    private static TreeNode MakeNode(FormaEntry entry)
    {
        var node = new TreeNode(entry.Name) { Tag = entry };
        node.Nodes.Add(new TreeNode(Placeholder)); // expandable stub, replaced on first expand
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
            _setStatus($"{entry.Name}: {children.Count} item(s).");
        });
    }

    private void OnAfterSelect(object? sender, TreeViewEventArgs e)
    {
        FormaEntry? folder = e.Node?.Tag is FormaEntry entry && entry.Kind == FormaKind.Folder ? entry : null;
        FolderSelected?.Invoke(folder);
    }

    private void RunGuarded(string busy, Action work)
    {
        _setStatus(busy);
        _host.UseWaitCursor = true;
        try { work(); }
        catch (Exception ex) { _setStatus("Error: " + ex.Message.Split('\n')[0]); }
        finally { _host.UseWaitCursor = false; }
    }
}
