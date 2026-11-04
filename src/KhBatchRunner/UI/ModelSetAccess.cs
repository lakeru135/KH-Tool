using System.Windows.Forms;
using KhBatchRunner.Forma;
using KhBatchRunner.Presets;

namespace KhBatchRunner.UI;

/// <summary>
/// The one place a saved model set is labelled and filtered by whether the signed-in Autodesk
/// account can open it.
///
/// <para><b>Why one place.</b> A rule applied in some of the windows is not a rule; this is the
/// same reasoning that produced <see cref="SetDuplication"/> and <c>ThemePreview.Windows()</c>.</para>
///
/// <para><b>And why no list of those windows here.</b> This comment used to name six places. There
/// are FIFTEEN windows in the add-in that let you pick a model set, and seven of them — Batch
/// Interop, Batch Link RVT, Correct Workset, Cut model, Initiate to Forma, Internal Relink and
/// Publish Cloud Model — showed a saved set with no suffix and no dimming, which is how 1.5.1.2 was
/// reported: "I see it only in Edit Model Set". A prose list is not a gate, and this one drifted
/// exactly the way <c>ThemePreview.Windows()</c> is documented to drift. The list now lives in
/// <c>ThemePreview --coverage</c>, which CONSTRUCTS every window and fails on any list control
/// holding model sets that never went through <see cref="AttachGreying"/>. Adding a window to that
/// list is what makes this rule cover it; nothing here can. (The sixteenth surface, the standalone
/// controller's Batch Run, labels and orders but does not dim — it is in a project that has not
/// compiled since before 1.4.7, so it is out of this gate's reach and named here instead.)</para>
///
/// <para><b>The label carries the verdict, and the colour carries it too.</b> Every one of those
/// lists is a <see cref="CheckedListBox"/>, a <see cref="ListBox"/> or a <see cref="ComboBox"/>. A
/// suffix says it in words, in every control type, and reads the same to someone who cannot easily
/// tell grey text from black; the dimming says it at a glance. Both, never one.</para>
///
/// <para><b>Three control types, three different ways to dim.</b> A <see cref="ComboBox"/> routes
/// through <see cref="Theme.DimItemsWhere"/> (the theme already owns that painter). A plain
/// <see cref="ListBox"/> is owner-drawn here. A <see cref="CheckedListBox"/> can be neither — its
/// <c>DrawMode</c> setter is empty and its <c>OnDrawItem</c> never raises the event — so it must be
/// a <see cref="DimmableCheckedListBox"/>, which overrides that method. This comment used to claim
/// the CheckedListBox case "means owner-drawing its checkbox by hand", and the code did exactly
/// that, and it silently never ran. See <see cref="DimmableCheckedListBox"/> for the full account.</para>
/// </summary>
public static class ModelSetAccess
{
    /// <summary>
    /// <paramref name="baseLabel"/> with the account verdict appended, or unchanged when there is
    /// nothing to say. Silent for <see cref="AccessVerdict.Accessible"/> and
    /// <see cref="AccessVerdict.Unknown"/>: a suffix on every row would be noise, and "we could not
    /// tell" is not a finding to put in front of someone.
    /// </summary>
    public static string Label(ModelPreset preset, string baseLabel)
    {
        switch (AccountAccess.For(preset))
        {
            case AccessVerdict.NoAccess:
                return baseLabel + NoAccessSuffix;
            case AccessVerdict.Partial:
                return baseLabel + "  ⚠ partly outside " + WhoWeAre;
            default:
                return baseLabel;
        }
    }

    /// <summary>
    /// A set's identity for "was this row ticked before the list was rebuilt?".
    ///
    /// <para>The stable <see cref="ModelPreset.Id"/> where there is one, so a rename cannot lose a
    /// tick; the name only for a set that has no id yet. Shared because a list that rebuilds itself
    /// on a filter toggle has to restore exactly what the user checked, and two windows do it.</para>
    /// </summary>
    public static string SetKey(ModelPreset preset) =>
        string.IsNullOrWhiteSpace(preset.Id) ? "name:" + preset.Name : "id:" + preset.Id;

    /// <summary>True when this set should be left out of a list because the user asked for that.</summary>
    public static bool Hidden(ModelPreset preset) =>
        HideInaccessible && AccountAccess.For(preset) == AccessVerdict.NoAccess;

    /// <summary>
    /// The sets a list should show, in order. <see cref="AccessVerdict.Unknown"/> and
    /// <see cref="AccessVerdict.Partial"/> are NEVER filtered out — hiding a set because a check
    /// was inconclusive makes it look deleted, which is worse than showing it honestly labelled.
    /// </summary>
    public static IEnumerable<ModelPreset> Visible(IEnumerable<ModelPreset> presets) =>
        presets.Where(p => !Hidden(p));

    /// <summary>
    /// The sets a list should show, in the order it should show them: everything this account can
    /// (or might) open first, then the ones it provably cannot.
    ///
    /// <para><see cref="Enumerable.OrderBy{TSource,TKey}(IEnumerable{TSource},Func{TSource,TKey})"/>
    /// is a STABLE sort, so the alphabetical order <see cref="PresetStore.LoadAll"/> already
    /// established survives inside each group — this only moves the dead ones to the bottom, it does
    /// not reshuffle the live ones.</para>
    ///
    /// <para><see cref="AccessVerdict.Partial"/> stays with the accessible group: part of it runs,
    /// so burying it would hide a set that still does most of its job.</para>
    /// </summary>
    public static IEnumerable<ModelPreset> Ordered(IEnumerable<ModelPreset> presets) =>
        presets.OrderBy(p => AccountAccess.For(p) == AccessVerdict.NoAccess ? 1 : 0);

    /// <summary>Convenience over <see cref="PresetStore.LoadAll"/> — what a list should be built
    /// from: filtered if the user asked for that, and always ordered.</summary>
    public static List<ModelPreset> VisibleSets() => Ordered(Visible(PresetStore.LoadAll())).ToList();

    /// <summary>
    /// Every list control that has been through <see cref="AttachGreying"/>.
    ///
    /// <para>Recorded for one reason: so a gate can ask. Coverage of a rule like this cannot be
    /// read off the source — a window builds its combo in a constructor eighty lines from where it
    /// fills it — and it cannot be read off a screenshot either, because a ComboBox popup is a
    /// separate window that <c>Form.DrawToBitmap</c> never captures. Asking the control itself is
    /// the only answer that cannot be out of date. Weak keys, so a closed window is still
    /// collectable.</para>
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object>
        Greyed = new();

    /// <summary>True when <paramref name="list"/> has had the account treatment applied to it.
    /// For the coverage gate; nothing in the product branches on it.</summary>
    public static bool IsGreyed(Control list) => Greyed.TryGetValue(list, out _);

    /// <summary>
    /// The whole treatment for a single-select model-set combo, in one call: ordered so
    /// inaccessible sets sink, labelled with the verdict, dimmed, and rebuilt when the account
    /// answer lands.
    ///
    /// <para><b>Why a binder and not three calls.</b> Seven windows had a preset combo and none of
    /// them had all three parts, because all three are separate calls made in separate places —
    /// the ordering at populate time, the label inside a private item class's <c>ToString</c>, the
    /// dimming next to the control. One call is one thing to forget instead of three.</para>
    ///
    /// <para>Nothing is HIDDEN here, unlike the multi-select lists. A combo is how you choose the
    /// set this window will act on; dropping the current one out of it would silently retarget the
    /// window, and the set editor's combo has always made the same exception for the same reason.
    /// The dimmed row is still selectable — the check can be wrong, so it warns and never blocks.</para>
    ///
    /// <para><paramref name="itemFor"/> wraps a set in the window's own item class and
    /// <paramref name="presetOf"/> unwraps it, because every window has its own.</para>
    /// </summary>
    public static void BindSetCombo(ComboBox combo, IEnumerable<ModelPreset> presets,
        Func<ModelPreset, object> itemFor, Func<object, ModelPreset?> presetOf)
    {
        List<ModelPreset> source = presets.ToList();

        void Fill()
        {
            // By SetKey, not by index: the order depends on the verdict, so the rebuilt list is not
            // the list that was there when the user picked a row.
            string? keep = combo.SelectedItem is null ? null : KeyOf(presetOf(combo.SelectedItem));
            combo.Items.Clear();
            foreach (ModelPreset p in Ordered(source)) combo.Items.Add(itemFor(p));

            if (combo.Items.Count == 0) return;
            for (int i = 0; keep != null && i < combo.Items.Count; i++)
                if (KeyOf(presetOf(combo.Items[i])) == keep) { combo.SelectedIndex = i; return; }
            combo.SelectedIndex = 0;
        }

        Fill();
        // Rebuild rather than repaint: Ordered() is applied above, at populate time, so a verdict
        // arriving later has to re-run it. See FormaBrowserWindow, which learned this first.
        AttachGreying(combo, presetOf, onSnapshotReady: Fill);
    }

    private static string? KeyOf(ModelPreset? preset) => preset is null ? null : SetKey(preset);

    /// <summary>
    /// Draws a set this account cannot open in dimmed text.
    ///
    /// <para>Owner-draw, because neither <see cref="ListBox"/> nor <see cref="ComboBox"/> has a
    /// per-item colour — but ONLY for a plain <see cref="ListBox"/>. A
    /// <see cref="CheckedListBox"/> cannot be owner-drawn at all: its <c>DrawMode</c> setter is
    /// empty and its <c>OnDrawItem</c> never raises <see cref="ListBox.DrawItem"/>, so the two
    /// lines below are silently discarded on one. That shipped, invisible, for three controls
    /// across six windows. Such a list must be a <see cref="DimmableCheckedListBox"/>, which
    /// overrides the method instead; passing a raw <see cref="CheckedListBox"/> here now paints
    /// nothing, and <c>ThemePreview --coverage</c> measures the ink and fails on it.</para>
    ///
    /// <para><paramref name="presetOf"/> maps a list item back to its set, because every window
    /// wraps presets in its own item class.</para>
    /// </summary>
    public static void AttachGreying(ListBox list, Func<object, ModelPreset?> presetOf,
        Action? onSnapshotReady = null)
    {
        Greyed.Remove(list);
        Greyed.Add(list, list);
        EnsureSnapshot(list, onSnapshotReady);

        // The subclass draws itself; it only needs telling WHICH rows. No DrawMode, no DrawItem —
        // both are inert on a CheckedListBox, and setting them here is what hid the bug.
        if (list is DimmableCheckedListBox dimmable)
        {
            dimmable.DimWhere(index =>
            {
                if (index < 0 || index >= list.Items.Count) return false;
                ModelPreset? preset = presetOf(list.Items[index]);
                return preset != null && AccountAccess.For(preset) == AccessVerdict.NoAccess;
            });
            return;
        }

        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            e.DrawBackground();
            DrawItemText(e, list, list.Items[e.Index], e.Bounds, presetOf);
            e.DrawFocusRectangle();
        };
    }

    /// <summary>
    /// The combo equivalent — but routed through <see cref="Theme.DimItemsWhere"/> rather than
    /// drawing here.
    ///
    /// <para><b>It must not add its own DrawItem handler.</b> In dark mode <see cref="Theme"/>
    /// already owner-draws every combo (a DropDownList's popup ignores BackColor otherwise), so a
    /// second handler means two painters and the later subscriber wins. 1.5.1 shipped exactly that:
    /// the dimming was drawn and then immediately repainted in one flat colour by the theme, so the
    /// list sorted correctly and greyed nothing. One painter; this only tells it which rows.</para>
    /// </summary>
    public static void AttachGreying(ComboBox combo, Func<object, ModelPreset?> presetOf,
        Action? onSnapshotReady = null)
    {
        Greyed.Remove(combo);
        Greyed.Add(combo, combo);
        EnsureSnapshot(combo, onSnapshotReady);
        Theme.DimItemsWhere(combo, index =>
        {
            if (index < 0 || index >= combo.Items.Count) return false;
            ModelPreset? preset = presetOf(combo.Items[index]);
            return preset != null && AccountAccess.For(preset) == AccessVerdict.NoAccess;
        });
    }

    private static void DrawItemText(DrawItemEventArgs e, Control owner, object item,
        Rectangle bounds, Func<object, ModelPreset?> presetOf)
    {
        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color fore = selected ? SystemColors.HighlightText : owner.ForeColor;
        Color back = selected ? SystemColors.Highlight : owner.BackColor;

        ModelPreset? preset = presetOf(item);
        if (preset != null && AccountAccess.For(preset) == AccessVerdict.NoAccess)
            fore = Theme.Dim(fore, back);   // tracks light/dark, unlike a hardcoded grey

        TextRenderer.DrawText(e.Graphics, item.ToString(), owner.Font, bounds, fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    /// <summary>Whether the user has asked for inaccessible sets to be left out. Off by default.</summary>
    public static bool HideInaccessible
    {
        get => Settings.UserSettings.HideInaccessibleModelSets;
        set => Settings.UserSettings.HideInaccessibleModelSets = value;
    }

    /// <summary>
    /// A ready-made "Hide sets this account cannot open" checkbox, wired to the persisted
    /// preference. <paramref name="onChanged"/> rebuilds the caller's list.
    /// </summary>
    public static CheckBox FilterCheckBox(Action onChanged)
    {
        var box = new CheckBox
        {
            Text = "Hide sets this account cannot open",
            AutoSize = true,
            Checked = HideInaccessible,
        };
        box.CheckedChanged += (_, _) => { HideInaccessible = box.Checked; onChanged(); };
        return box;
    }

    /// <summary>
    /// Makes sure an account snapshot exists for a control that is about to show model sets, and
    /// repaints it (plus whatever <paramref name="onSnapshotReady"/> rebuilds) when one arrives.
    ///
    /// <para><b>Why this hangs off <see cref="AttachGreying"/> rather than being wired per window.</b>
    /// Only Batch Run and the shared schedule panel ever ASKED for a snapshot; Edit Model Set,
    /// Export NWC and the action picker merely read whatever one of those had already cached. Open
    /// Revit and go straight to Edit Model Set and there was none — so every set read Unknown and
    /// the list showed no suffix and no dimming at all, which is exactly how 1.5.1.1 was reported.
    /// Anything that displays the verdict now ensures the verdict exists; there is no per-window
    /// step left to forget. Compare the run-notification bug, which had the same shape.</para>
    ///
    /// <para>It builds its own client from the signed-in Revit session — the same default
    /// <see cref="Presets.PresetResolver"/> uses — so a window with no Forma client of its own is no
    /// longer a second-class citizen. Outside Revit that yields an unreachable snapshot, which
    /// reads as Unknown and changes nothing.</para>
    /// </summary>
    public static void EnsureSnapshot(Control owner, Action? onSnapshotReady = null)
    {
        // NEVER invoke onSnapshotReady on this stack, cached or not. This is called from control
        // constructors, and its callbacks rebuild the very list being constructed — the same inline
        // re-entrancy that made all four Schedule windows throw out of their constructors in 1.5.0.
        // Callers populate their list themselves; this is only for the REBUILD once an answer lands.
        try
        {
            BeginRefresh(owner, new DataManagementClient(new Auth.SessionTokenProvider()), () =>
            {
                onSnapshotReady?.Invoke();
                if (!owner.IsDisposed) owner.Invalidate();
            });
        }
        catch { /* no session, no client — every set stays Unknown, which is the honest answer */ }
    }

    /// <summary>
    /// Takes the account snapshot on a background thread and calls <paramref name="onReady"/> back
    /// on the UI thread when it lands.
    ///
    /// <para>Off the UI thread deliberately: this is one REST call for the hub list plus one per
    /// hub, and <see cref="FormaTreeController"/> is the standing demonstration of what synchronous
    /// Forma calls on the UI thread look like from the outside — a window that appears frozen rather
    /// than slow. The list is built and shown first; the labels sharpen when the answer arrives.</para>
    ///
    /// <para>Skipped when a GOOD snapshot has already been taken this session — the answer only
    /// changes when the account does, and <c>App.CaptureSignedInUser</c> invalidates it then. An
    /// UNREACHABLE snapshot is retried, because the usual reason for one is that Revit had not
    /// finished signing in yet; caching that would leave every set Unknown for the whole session
    /// on the strength of one early miss.</para>
    /// </summary>
    public static void BeginRefresh(Control owner, DataManagementClient client, Action onReady)
    {
        // Captured on the UI thread, and used instead of Control.BeginInvoke because this is
        // routinely called from a panel's CONSTRUCTOR — the control has no window handle yet, and
        // BeginInvoke on a handle-less control either throws or silently drops the callback.
        System.Threading.SynchronizationContext? ui = System.Threading.SynchronizationContext.Current;

        void Deliver()
        {
            if (ui is null) return;
            try { ui.Post(_ => { if (!owner.IsDisposed) onReady(); }, null); }
            catch { /* the window closed while we were asking — nothing to update */ }
        }

        // A CACHED answer is still delivered asynchronously, and that is the whole point.
        //
        // This used to call onReady() inline, on the caller's stack. The caller is a panel
        // CONSTRUCTOR, and the callback reaches back into the half-built window that owns it — so
        // as soon as one window had cached a good snapshot, the NEXT window to open ran its own
        // status callback before it had built a status label, and threw NullReferenceException out
        // of its constructor. Revit shows that as "could not complete the external command", which
        // is what 1.5.0 did to all four Schedule windows once an account had been checked.
        //
        // Posting instead means the callback always lands after the constructor has returned, for
        // every caller, whether or not the answer was already known. See the seeded second pass in
        // ThemePreview's construction gate, which reproduces the old behaviour.
        if (AccountAccess.Cached is { Reachable: true }) { Deliver(); return; }

        System.Threading.Tasks.Task.Run(() =>
        {
            AccountAccess.Refresh(client);
            Deliver();
        });
    }

    /// <summary>
    /// A one-line description of the account situation for a status label, or "" when there is
    /// nothing worth saying.
    /// </summary>
    public static string StatusNote()
    {
        AccessSnapshot? snapshot = AccountAccess.Cached;
        if (snapshot is null) return "";
        if (!snapshot.Reachable)
            return "Could not check Forma access (" + snapshot.Error + ") — no set is hidden.";

        int denied = PresetStore.LoadAll().Count(p => AccountAccess.For(p, snapshot) == AccessVerdict.NoAccess);

        // The account change leads, because it is the CAUSE and the count is the symptom. Said even
        // when nothing turned out to be inaccessible: the schedules kept firing across the change
        // either way, and knowing which account they now run as is the point.
        if (snapshot.AccountChanged)
            return $"Signed in as {WhoWeAre} — previously {snapshot.PreviousUser}. " +
                   (denied == 0
                       ? "Every saved model set is still reachable."
                       : $"{denied} saved model set(s) cannot be opened by this account.");

        return denied == 0
            ? ""
            : $"{denied} saved model set(s) cannot be opened by {WhoWeAre}.";
    }

    private static string NoAccessSuffix => "  ⚠ no access as " + WhoWeAre;

    private static string WhoWeAre =>
        HostServices.SignedInUserId.Length > 0 ? HostServices.SignedInUserId : "this account";
}
