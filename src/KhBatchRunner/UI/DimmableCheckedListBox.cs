namespace KhBatchRunner.UI;

/// <summary>
/// A <see cref="CheckedListBox"/> whose rows can be drawn in dimmed text.
///
/// <para><b>Why a subclass is the ONLY way.</b> <see cref="CheckedListBox"/> declares
/// <c>public override DrawMode DrawMode { get =&gt; DrawMode.Normal; set { } }</c> — an <b>empty
/// setter</b> — and its own <c>OnDrawItem</c> paints the check box and the text itself without ever
/// calling base, so the <see cref="ListBox.DrawItem"/> event is never raised. Assigning
/// <see cref="DrawMode.OwnerDrawFixed"/> to one compiles, runs, throws nothing, and does nothing.</para>
///
/// <para>That is exactly what shipped. <c>ModelSetAccess.AttachGreying</c> did that assignment and
/// subscribed a handler that hand-drew the check box, for Batch Run, the Multi Model action picker
/// and the shared schedule panel — three controls, six windows. The handler has never once run: the
/// account greying was invisible in every tick-box list while working perfectly in every dropdown,
/// which is how it was reported. <c>ModelCheckGrid</c>'s comment and <c>development-plan.md</c> both
/// already said a CheckedListBox cannot be owner-drawn; the code disagreed and nothing arbitrated.
/// The gate that now does is <c>ThemePreview --coverage</c>, which measures the ink a control
/// actually paints rather than whether a rule was wired to it.</para>
/// </summary>
///
/// <remarks>
/// <para><b>Why this overrides rather than draws.</b> <c>CheckedListBox.OnDrawItem</c> chooses its
/// text colour with <c>(SelectionMode != SelectionMode.None) ? e.ForeColor : ForeColor</c> and its
/// background the same way, so for any list with a selection mode — all of ours — the colours on the
/// event args are honoured. Substituting one colour and letting the base do the drawing keeps the
/// themed check-box glyph, its DPI-dependent size, the text offsets, right-to-left and selection,
/// none of which the hand-drawn version got right (it hardcoded a 13px glyph at a fixed offset).</para>
///
/// <para><b>Measured, on both runtimes, not assumed.</b> A two-target probe set
/// <c>DrawMode = OwnerDrawFixed</c>, read it back, then substituted a red foreground and counted the
/// red pixels the base painted. <c>net48</c> (Revit 2024): DrawMode reads back <c>Normal</c>,
/// 0 red pixels without the substitution and 588 with it. <c>net8</c> (Revit 2025/2026): the same
/// <c>Normal</c>, 0 and 657. So the empty setter and the honoured <c>e.ForeColor</c> are facts about
/// both frameworks the product ships against, not an inference from one of them.</para>
///
/// <para><b>The condition that would silently switch this off.</b> Everything above holds because
/// <see cref="ListBox.SelectionMode"/> is not <see cref="SelectionMode.None"/>; all three of our
/// lists leave it at the default <c>One</c>. Setting it to <c>None</c> would make the base ignore
/// the substituted colour and dim nothing, with no error and nothing to see in review. What catches
/// that is <c>ThemePreview --coverage</c>, which measures the ink rather than trusting the wiring.</para>
///
/// <para><b>Do not put a <c>using</c> on that <see cref="DrawItemEventArgs"/>.</b> It is
/// <see cref="IDisposable"/> on net8 and is NOT on net48, so scoping it would break the Revit 2024
/// build. It is deliberately a plain local.</para>
/// </remarks>
public sealed class DimmableCheckedListBox : CheckedListBox
{
    private Func<int, bool>? _dim;

    /// <summary>
    /// Which rows to draw dimmed, by index. Re-evaluated on every paint, so the answer may arrive
    /// after the list is built — which it does: the Forma account check runs on a background thread.
    /// </summary>
    public void DimWhere(Func<int, bool> dim) => _dim = dim;

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        // Selected rows are left alone: the base replaces the colours with the highlight pair
        // regardless, so dimming one would be overwritten anyway.
        if (_dim is not null && e.Index >= 0 && e.Index < Items.Count &&
            (e.State & DrawItemState.Selected) == 0 && Dimmed(e.Index))
        {
            e = new DrawItemEventArgs(e.Graphics, e.Font!, e.Bounds, e.Index, e.State,
                                      Theme.Dim(e.ForeColor, e.BackColor), e.BackColor);
        }

        QueueSync();
        base.OnDrawItem(e);
    }

    // ---- Horizontal scrolling for long labels --------------------------------------------------
    // ListBox/CheckedListBox never raise OnPaint (no UserPaint), so the extent is synced from the
    // places that DO fire: handle creation, font/DPI change, and DrawItem (which only QUEUES one
    // deferred sync, never resizing mid-draw). The sync skips the measuring unless the item list
    // changed (count + total text length), so large model lists are not re-measured on every paint.
    private bool _syncQueued;
    private int _lastSignature = -1;

    public DimmableCheckedListBox() => HorizontalScrollbar = true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _syncQueued = false; // a BeginInvoke lost across a handle recreate must not freeze updates
        _lastSignature = -1;
        SyncExtent();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _syncQueued = false;
        base.OnHandleDestroyed(e);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _lastSignature = -1;
        if (IsHandleCreated) SyncExtent();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _lastSignature = -1;
        if (IsHandleCreated) SyncExtent();
    }

    private void QueueSync()
    {
        if (_syncQueued || !IsHandleCreated) return;
        _syncQueued = true;
        BeginInvoke((Action)(() => { _syncQueued = false; SyncExtent(); }));
    }

    private int _widestText;

    // Re-sync when the client width changes: a vertical bar appearing or disappearing changes the
    // room available (ClientSize already excludes it).
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated) SyncExtent();
    }

    private void SyncExtent()
    {
        if (IsDisposed) return;
        // Hash of the actual texts (so a same-length relabel is caught); manual combine is net48-safe.
        int signature = Items.Count;
        foreach (object item in Items) signature = unchecked(signature * 31 + StringComparer.Ordinal.GetHashCode(GetItemText(item)));
        if (signature != _lastSignature)
        {
            _lastSignature = signature;
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int widest = 0;
            foreach (object item in Items)
                widest = Math.Max(widest, TextRenderer.MeasureText(GetItemText(item), Font, new Size(int.MaxValue, int.MaxValue), flags).Width);
            _widestText = widest;
        }

        // Drawn row width = check glyph + the gap the base control leaves + text (+ a little slack for
        // the focus rectangle). Only a row that really overflows gets an extent; otherwise 0, so no bar.
        using Graphics g = CreateGraphics();
        int glyph = CheckBoxRenderer.GetGlyphSize(g, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width;
        int rowWidth = glyph + 4 + _widestText + 4;
        int extent = rowWidth > ClientSize.Width ? rowWidth : 0;
        if (HorizontalExtent != extent) HorizontalExtent = extent;
    }


    /// <summary>The predicate is supplied by a caller and runs inside a paint; a throw there would
    /// tear down the window rather than lose one row's colour.</summary>
    private bool Dimmed(int index)
    {
        try { return _dim!(index); }
        catch { return false; }
    }
}
