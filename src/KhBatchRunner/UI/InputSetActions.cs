namespace KhBatchRunner.UI;

/// <summary>
/// The non-visual half of the "Input set:" row that Cut model, Initiate, Internal Relink, Correct
/// Workset and Batch Link RVT share: reload / select / save / duplicate / delete over one named-set
/// store, with the same messages in all five. It creates no controls — each window still builds,
/// orders and lays out its own combo and buttons, keeps <c>_loadedSetName</c> (passed in by ref, so
/// a SelectedIndexChanged → ApplyInputs that resets it mid-call is seen exactly as before), and
/// keeps its own <c>Warn</c>.
///
/// <para>Sets are keyed by file name, so saving a loaded set under a new name deletes the old file
/// (a rename, not a silent duplicate) — the same rule as model and script sets.</para>
/// </summary>
internal sealed class InputSetActions<T> where T : class
{
    private readonly Form _owner;
    private readonly ComboBox _combo;
    private readonly Func<List<T>> _loadAll;
    private readonly Func<string, bool> _exists;
    private readonly Action<T> _save;
    private readonly Action<string> _delete;
    private readonly Func<T, string> _nameOf;
    private readonly Func<T, T> _clone;
    private readonly Action<T, string> _rename;
    private readonly Func<T, object> _toItem;
    private readonly Func<object?, T?> _fromItem;
    private readonly Action<string> _warn;

    /// <param name="owner">Owner and caption (its <see cref="Control.Text"/>) of the confirm boxes.</param>
    /// <param name="combo">The editable "Input set:" combo.</param>
    /// <param name="toItem">Wraps a set as the combo item the window displays.</param>
    /// <param name="fromItem">The set behind a combo item, or null when it is not one.</param>
    /// <param name="warn">The window's own Warn (message box + DialogResult.None).</param>
    public InputSetActions(Form owner, ComboBox combo,
        Func<List<T>> loadAll, Func<string, bool> exists, Action<T> save, Action<string> delete,
        Func<T, string> nameOf, Func<T, T> clone, Action<T, string> rename,
        Func<T, object> toItem, Func<object?, T?> fromItem, Action<string> warn)
    {
        _owner = owner;
        _combo = combo;
        _loadAll = loadAll;
        _exists = exists;
        _save = save;
        _delete = delete;
        _nameOf = nameOf;
        _clone = clone;
        _rename = rename;
        _toItem = toItem;
        _fromItem = fromItem;
        _warn = warn;
    }

    public void Reload(string? selectName)
    {
        _combo.Items.Clear();
        foreach (T set in _loadAll())
            _combo.Items.Add(_toItem(set));
        if (selectName != null) SelectByName(selectName);
    }

    public void SelectByName(string name)
    {
        for (int i = 0; i < _combo.Items.Count; i++)
        {
            if (_nameOf(_fromItem(_combo.Items[i])!).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _combo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Saves the window's snapshot under the name typed in the combo.</summary>
    /// <param name="loadedSetName">The window's currently-loaded set name (rename-on-save).</param>
    /// <param name="snapshot">The window's BuildSnapshot.</param>
    /// <param name="missing">The window's own "can't save yet" check: the warning to show, or null.</param>
    public void Save(ref string? loadedSetName, Func<string, T> snapshot, Func<string?>? missing)
    {
        string name = _combo.Text.Trim();
        if (name.Length == 0)
        {
            _warn("Type a name for the input set (in the Input set box), then Save set.");
            return;
        }
        string? problem = missing?.Invoke();
        if (problem != null)
        {
            _warn(problem);
            return;
        }
        bool renaming = loadedSetName != null &&
                        !name.Equals(loadedSetName, StringComparison.OrdinalIgnoreCase);
        if (_exists(name) && (loadedSetName == null || renaming))
        {
            if (MessageBox.Show(_owner, $"An input set named '{name}' already exists. Overwrite it?",
                    _owner.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }

        _save(snapshot(name));
        if (renaming) _delete(loadedSetName!);
        loadedSetName = name;
        Reload(selectName: name);
    }

    /// <summary>
    /// Forks the selected set to a new "&lt;name&gt; (copy)" and loads the copy.
    ///
    /// <para>Until 1.4.7 there was no way to do this. The combo is editable, so loading a set and
    /// typing a new name LOOKS like a fork — but <see cref="Save"/> reads that as a rename and
    /// deletes the original file, so the obvious gesture destroyed the set being copied from.</para>
    /// </summary>
    public void Duplicate()
    {
        if (_fromItem(_combo.SelectedItem) is not T selected)
        {
            _warn("Select a saved input set to duplicate.");
            return;
        }
        T copy = _clone(selected);
        _rename(copy, SetDuplication.UniqueName(_nameOf(selected), _loadAll().Select(_nameOf)));
        try
        {
            _save(copy);
        }
        catch (Exception ex)
        {
            _warn("Could not duplicate the input set: " + ex.Message);
            return;
        }
        // Selecting the new row raises SelectedIndexChanged -> ApplyInputs(loaded: true), which is
        // what re-points _loadedSetName at the COPY. Leave it on the original and the very next
        // "Save set" takes the rename branch and deletes the set just copied from.
        Reload(selectName: _nameOf(copy));
    }

    public void Delete(ref string? loadedSetName)
    {
        if (_fromItem(_combo.SelectedItem) is not T selected)
        {
            _warn("Select a saved input set to delete.");
            return;
        }
        if (MessageBox.Show(_owner, $"Delete input set '{_nameOf(selected)}'?",
                _owner.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _delete(_nameOf(selected));
        if (loadedSetName != null && loadedSetName.Equals(_nameOf(selected), StringComparison.OrdinalIgnoreCase))
            loadedSetName = null;
        Reload(selectName: null);
        _combo.Text = "";
    }
}
