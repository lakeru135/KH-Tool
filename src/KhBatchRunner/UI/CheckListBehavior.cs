namespace KhBatchRunner.UI;

/// <summary>
/// Small UI behaviours shared by the check-list windows (Batch Run / Schedule Run / Schedule
/// Interop). A <see cref="CheckedListBox"/> with <c>CheckOnClick</c> toggles one item per click;
/// <see cref="EnableRangeCheck"/> adds Shift+click range toggling (check/uncheck every item
/// between the last plain click and the Shift-click at once), the multi-select gesture users
/// expect from a list.
/// </summary>
internal static class CheckListBehavior
{
    /// <summary>Enables/disables a dropdown; when disabled it shows BLANK (no greyed label), and when
    /// re-enabled with nothing selected it defaults to the first item.</summary>
    public static void SetEnabledOrBlank(ComboBox combo, bool enabled)
    {
        combo.Enabled = enabled;
        if (!enabled) combo.SelectedIndex = -1;
        else if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    public static void EnableRangeCheck(CheckedListBox list)
    {
        int anchor = -1;
        list.MouseDown += (_, e) =>
        {
            int idx = list.IndexFromPoint(e.Location);
            if (idx < 0) return;

            bool shift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            if (shift && anchor >= 0 && anchor != idx)
            {
                // CheckOnClick is about to toggle the clicked item; apply that resulting state to
                // the rest of the range here (skip idx so the click's own toggle isn't undone).
                bool target = !list.GetItemChecked(idx);
                int lo = Math.Min(anchor, idx), hi = Math.Max(anchor, idx);
                list.BeginUpdate();
                for (int k = lo; k <= hi; k++)
                    if (k != idx) list.SetItemChecked(k, target);
                list.EndUpdate();
            }
            else
            {
                anchor = idx;
            }
        };
    }
}
