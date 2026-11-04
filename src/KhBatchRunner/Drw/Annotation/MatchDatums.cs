using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

/// <summary>
/// Match Datums: copies each grid's and level's look in the ACTIVE view — its 2D ends, which ends
/// are 2D or 3D, bubbles on/off and elbows — onto the views the user ticks.
///
/// <para>Replaces Revit's Propagate Extents, which chooses the views itself and fails silently.
/// Here the user picks the views, only views parallel to the source are offered, and every datum in
/// every view gets a row saying Matched, Partly matched, Not applied, Unchanged, Skipped or Failed,
/// and why.</para>
///
/// <para>Each datum's source line is moved along the view direction onto the target view's plane,
/// so a plan's grid ends land at the same X/Y in every plan and a section's level ends at the same
/// position along the section in every parallel section.</para>
///
/// <para><b>Every end 2D, copied exactly (0.2.7, the user's choice).</b> In each ticked view both ends
/// of every datum are made 2D (view-specific) and placed exactly where the source view DRAWS them —
/// a 3D end at its visible position — whatever the grid's 3D extents or a scope box would say. That
/// is what makes views cropped by a scope box match a view without one. An end that lands beyond the
/// target's crop stays there too (Revit keeps it; the crop hides it — self-test "PROBE"), and its row
/// says so, so an invisible end is never a silent "Matched". Only views at the source's scale are
/// offered.</para>
///
/// <para><b>Nothing is claimed that Revit did not keep.</b> After each datum's edits the document is
/// regenerated and read back; only what is really there counts as matched.</para>
///
/// <para><b>What the API cannot do</b>: section markers have no per-view ends, gaps or head/tail
/// access (<c>ViewSection</c> offers only the Create* methods), and a datum elbow cannot be removed
/// (<c>SetLeader</c> refuses null). Both are reported, never silently left.</para>
/// </summary>
internal static class DatumMatcher
{
    public static readonly IReadOnlyList<string> Columns = new[] { "View", "Datum", "Status", "Note" };

    private const double Tolerance = 1e-6;
    /// <summary>How close a read-back end must be to count as placed (a Revit round trip is not exact).</summary>
    private const double PlacedTolerance = 1e-4;
    private static readonly DatumEnds[] Ends = { DatumEnds.End0, DatumEnds.End1 };

    /// <summary>A view whose datums Match Datums reads or writes: Datum Dimension's views, plus
    /// reflected ceiling plans, which show grids exactly as floor plans do.</summary>
    public static bool IsMatchView(View v) =>
        DrwScope.IsDatumView(v) || (!v.IsTemplate && v.ViewType == ViewType.CeilingPlan);

    /// <summary>Plans for a plan; for a section or elevation, the sections and elevations facing the
    /// same way or the opposite way — and only at the source's scale (the user's rule: 2D ends placed
    /// for 1:100 are not what a 1:50 view wants). Anything else would put the ends somewhere unrelated.</summary>
    public static List<View> Candidates(Document doc, View source) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.Id != source.Id && IsMatchView(v) && Parallel(source, v) && v.Scale == source.Scale)
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool Parallel(View a, View b)
    {
        bool planA = a is ViewPlan, planB = b is ViewPlan;
        if (planA || planB) return planA && planB;
        return Math.Abs(a.ViewDirection.DotProduct(b.ViewDirection)) > 1 - 1e-9;
    }

    /// <summary>The scope box a view's crop follows, or invalid.</summary>
    public static ElementId ScopeBox(View v) =>
        v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId() ?? ElementId.InvalidElementId;

    /// <summary>A datum as the source view shows it.</summary>
    private sealed class Source
    {
        public Source(DatumPlane datum, Curve curve, string label) { Datum = datum; Curve = curve; Label = label; }

        public DatumPlane Datum { get; }
        public Curve Curve { get; }
        public string Label { get; }
        public Dictionary<DatumEnds, DatumExtentType> Extent { get; } = new();
        public Dictionary<DatumEnds, bool> Bubble { get; } = new();
        public Dictionary<DatumEnds, Leader?> Leader { get; } = new();
    }

    /// <summary>What one datum should look like in one target view.</summary>
    private sealed class Want
    {
        public Curve Line = null!;
        public Dictionary<DatumEnds, DatumExtentType> Extent { get; } = new();
        public List<string> Notes { get; } = new();
    }

    public static List<IReadOnlyDictionary<string, string>> Run(Document doc, View source, IEnumerable<View> targets)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        List<Source> sources = Read(doc, source);
        if (sources.Count == 0)
        {
            rows.Add(DrwRows.Row(Columns, source.Name, "", "Skipped", "The active view shows no straight grids or levels to copy"));
            return rows;
        }
        int linked = LinkedDatumCount(doc, source is ViewPlan);
        if (linked > 0)
            rows.Add(DrwRows.Row(Columns, source.Name, "Linked models", "Skipped",
                $"{linked} grid(s){(source is ViewPlan ? "" : "/level(s)")} belong to linked models; Match Datums changes only this " +
                "model's datums. Adjust them in the link, or show them 'By linked view'."));

        using var group = new TransactionGroup(doc, "DRW Match Datums");
        group.Start();
        foreach (View target in targets)
        {
            var viewRows = new List<IReadOnlyDictionary<string, string>>();
            var shown = new HashSet<ElementId>(new FilteredElementCollector(doc, target.Id).OfClass(typeof(Grid)).ToElementIds()
                .Concat(new FilteredElementCollector(doc, target.Id).OfClass(typeof(Level)).ToElementIds()));
            using Transaction t = DrwWarningSwallower.Start(doc, "Match Datums " + target.Name);
            foreach (Source s in sources)
            {
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    (string status, string note) = Match(s, source, target, shown);
                    if (status is "Failed" or "Not applied") sub.RollBack(); else sub.Commit();
                    viewRows.Add(DrwRows.Row(Columns, target.Name, s.Label, status, note));
                }
                catch (Exception ex)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    viewRows.Add(DrwRows.Row(Columns, target.Name, s.Label, "Failed", ex.Message));
                }
            }
            if (t.Commit() != TransactionStatus.Committed)
                viewRows = viewRows.Select(r => DrwRows.Row(Columns, r["View"], r["Datum"],
                    r["Status"] is "Matched" or "Partly matched" ? "Failed" : r["Status"],
                    r["Status"] is "Matched" or "Partly matched" ? "Revit rolled back this view's changes" : r["Note"])).ToList();
            rows.AddRange(viewRows);
        }
        group.Assimilate();
        return rows;
    }

    private static List<Source> Read(Document doc, View view)
    {
        var list = new List<Source>();
        IEnumerable<DatumPlane> datums = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<DatumPlane>();
        if (view is not ViewPlan)
            datums = datums.Concat(new FilteredElementCollector(doc, view.Id).OfClass(typeof(Level)).Cast<DatumPlane>());
        foreach (DatumPlane d in datums)
        {
            Curve? curve;
            try { curve = d.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault(); }
            catch { curve = null; }
            if (curve == null) continue;
            var s = new Source(d, curve, LabelOf(d));
            foreach (DatumEnds end in Ends)
            {
                s.Extent[end] = d.GetDatumExtentTypeInView(end, view);
                s.Bubble[end] = BubbleShown(d, end, view);
                s.Leader[end] = LeaderAt(d, end, view);
            }
            list.Add(s);
        }
        return list.OrderBy(s => s.Datum is Level).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"Grid B", or for one segment of a multi-segment grid "Grid B (segment 2 of 3)" — the
    /// segments are separate grids that share one name, so without this two rows read the same.</summary>
    private static string LabelOf(DatumPlane d)
    {
        if (d is Level) return "Level " + d.Name;
        try
        {
            ElementId msId = MultiSegmentGrid.GetMultiSegementGridId((Grid)d);
            if (msId != ElementId.InvalidElementId && d.Document.GetElement(msId) is MultiSegmentGrid ms)
            {
                List<ElementId> ids = ms.GetGridIds().ToList();
                return $"Grid {d.Name} (segment {ids.IndexOf(d.Id) + 1} of {ids.Count})";
            }
        }
        catch { /* not a multi-segment member */ }
        return "Grid " + d.Name;
    }

    /// <summary>Grids (and, outside plans, levels) inside loaded linked models.</summary>
    private static int LinkedDatumCount(Document doc, bool plan)
    {
        int n = 0;
        foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            Document? ld = link.GetLinkDocument();
            if (ld == null) continue;
            n += new FilteredElementCollector(ld).OfClass(typeof(Grid)).GetElementCount();
            if (!plan) n += new FilteredElementCollector(ld).OfClass(typeof(Level)).GetElementCount();
        }
        return n;
    }

    /// <summary>Makes one datum in one view look as it does in the source. Returns the row's status and note.</summary>
    private static (string Status, string Note) Match(Source s, View source, View target, HashSet<ElementId> shown)
    {
        DatumPlane datum = s.Datum;
        Document doc = datum.Document;

        // --- can this datum be changed in this view at all? ---
        if (doc.IsWorkshared)
        {
            CheckoutStatus cs = WorksharingUtils.GetCheckoutStatus(doc, datum.Id, out string owner);
            if (cs == CheckoutStatus.OwnedByOtherUser)
                return ("Skipped", $"Borrowed by {owner} — ask them to relinquish it, then run again");
        }
        if (!datum.CanBeVisibleInView(target))
            return ("Skipped", "Not visible in this view (outside its range or cut)");
        if (datum.IsHidden(target))
            return ("Skipped", "Hidden in this view");
        if (CategoryHidden(target, datum))
            return ("Skipped", $"{(datum is Level ? "Levels" : "Grids")} are turned off in this view's Visibility/Graphics");
        if (!shown.Contains(datum.Id))
            return ("Skipped", "Not shown in this view (a filter, the view range or the crop hides it)");
        Curve? current;
        try { current = datum.GetCurvesInView(DatumExtentType.ViewSpecific, target).FirstOrDefault(); }
        catch { current = null; }
        if (current == null)
            return ("Skipped", "Not drawn in this view");

        // --- what it should look like here ---
        XYZ look = source.ViewDirection;
        double shift = (current.GetEndPoint(0) - s.Curve.GetEndPoint(0)).DotProduct(look);
        Want want = Plan(s, target, s.Curve.CreateTransformed(Transform.CreateTranslation(look * shift)));

        var intents = new List<string>();   // what was changed, before checking it stuck
        var notes = new List<string>(want.Notes);
        ElementId box = datum.get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST)?.AsElementId() ?? ElementId.InvalidElementId;
        string? boxName = box != ElementId.InvalidElementId ? doc.GetElement(box)?.Name ?? "?" : null;

        // Ends in their own sub-transaction: when Revit refuses them (a datum tied to a Scope Box,
        // most likely) they are undone together and named, while bubbles and elbows still go ahead.
        using (var ends = new SubTransaction(doc))
        {
            ends.Start();
            try
            {
                string? refused = MatchEnds(datum, target, current, want, intents);
                if (refused == null) ends.Commit();
                else
                {
                    ends.RollBack();
                    intents.RemoveAll(i => i.Contains("end"));
                    if (boxName == null) return ("Failed", refused);
                    notes.Add($"2D ends not changed: {refused} (tied to Scope Box '{boxName}')");
                }
            }
            catch (Exception ex) when (boxName != null)
            {
                ends.RollBack();
                intents.RemoveAll(i => i.Contains("end"));
                notes.Add($"2D ends not changed (tied to Scope Box '{boxName}'): {ex.Message}");
            }
        }

        // Bubbles.
        foreach (DatumEnds end in Ends)
        {
            if (BubbleShown(datum, end, target) == s.Bubble[end]) continue;
            if (s.Bubble[end]) datum.ShowBubbleInView(end, target); else datum.HideBubbleInView(end, target);
            intents.Add($"{EndName(end)} bubble {(s.Bubble[end] ? "on" : "off")}");
        }

        // Elbows.
        foreach (DatumEnds end in Ends)
        {
            Leader? src = s.Leader[end];
            Leader? have = LeaderAt(datum, end, target);
            XYZ move = look * shift;
            if (src != null && s.Bubble[end])
            {
                if (have != null && Same(have.End, src.End + move) && Same(have.Elbow, src.Elbow + move)) continue;
                string? why = SetElbow(datum, end, target, src, move, have == null);
                if (why == null) intents.Add($"{EndName(end)} elbow");
                else notes.Add($"{EndName(end)} elbow not copied: {why}");
            }
            else if (src == null && have != null)
            {
                notes.Add($"{EndName(end)} elbow left in place: the Revit API cannot remove an elbow — remove it by hand");
            }
        }

        if (intents.Count == 0)
            return ("Unchanged", string.Join("; ", notes));

        // Only what Revit kept after a regeneration counts.
        List<string> missed = Verify(datum, target, s, want);
        if (missed.Count > 0 && boxName != null) missed.Add($"tied to Scope Box '{boxName}' — adjust the box, or untie the datum");
        string done = string.Join(", ", intents);
        if (missed.Count == 0)
            return ("Matched", string.Join("; ", new[] { done }.Concat(notes)));
        string status = missed.Count >= intents.Count ? "Not applied" : "Partly matched";
        return (status, string.Join("; ", new[] { done, "Revit did not keep: " + string.Join(", ", missed) }.Concat(notes)));
    }

    /// <summary>
    /// The target's wanted line and extents: both ends 2D, exactly where the source view draws them
    /// (<paramref name="shifted"/> is the source's drawn line on the target's plane). An end beyond
    /// the target's crop is kept there, and named, because the crop will hide it.
    /// </summary>
    private static Want Plan(Source s, View target, Curve shifted)
    {
        var want = new Want { Line = shifted };
        foreach (DatumEnds end in Ends) want.Extent[end] = DatumExtentType.ViewSpecific;
        Crop? crop = Crop.Of(target);
        if (crop != null)
            foreach (DatumEnds end in Ends)
                if (!crop.Contains(shifted.GetEndPoint(end == DatumEnds.End0 ? 0 : 1)))
                    want.Notes.Add($"{EndName(end)} end is beyond this view's crop, so the crop hides it " +
                                   "(placed as in the source; widen the crop or scope box to show it)");
        return want;
    }

    /// <summary>Which ends are 2D/3D, and the 2D line. Returns why Revit refused, or null.</summary>
    private static string? MatchEnds(DatumPlane datum, View target, Curve current, Want want, List<string> intents)
    {
        // 1. Ends that should be 2D become 2D, so the 2D line can be set.
        bool switched = false;
        foreach (DatumEnds end in Ends)
            if (want.Extent[end] == DatumExtentType.ViewSpecific && datum.GetDatumExtentTypeInView(end, target) != DatumExtentType.ViewSpecific)
            {
                datum.SetDatumExtentType(end, target, DatumExtentType.ViewSpecific);
                intents.Add($"{EndName(end)} end 2D");
                switched = true;
            }
        // A 3D end turned 2D does not stay where it was drawn: for a grid tied to a scope box Revit put
        // the new 2D end 8.33 ft away (self-test, 0.2.7). Compared with the line read BEFORE the switch
        // it looked already in place, so it was never moved. Read it again after regenerating.
        if (switched)
        {
            datum.Document.Regenerate();
            current = datum.GetCurvesInView(DatumExtentType.ViewSpecific, target).FirstOrDefault() ?? current;
        }

        // 2. The 2D line — only when a 2D end is not where it should be.
        bool move = Ends.Any(e => want.Extent[e] == DatumExtentType.ViewSpecific &&
                                  !Same(current.GetEndPoint(e == DatumEnds.End0 ? 0 : 1), want.Line.GetEndPoint(e == DatumEnds.End0 ? 0 : 1)));
        if (move)
        {
            if (!datum.IsCurveValidInView(DatumExtentType.ViewSpecific, target, want.Line))
                return "Revit does not accept those ends in this view";
            datum.SetCurveInView(DatumExtentType.ViewSpecific, target, want.Line);
            intents.Add("ends moved");
        }

        // 3. Ends that should be 3D go back to 3D.
        foreach (DatumEnds end in Ends)
            if (want.Extent[end] == DatumExtentType.Model && datum.GetDatumExtentTypeInView(end, target) != DatumExtentType.Model)
            {
                datum.SetDatumExtentType(end, target, DatumExtentType.Model);
                intents.Add($"{EndName(end)} end 3D");
            }
        return null;
    }

    /// <summary>After a regeneration: what the target does NOT show as wanted, in words.</summary>
    private static List<string> Verify(DatumPlane datum, View target, Source s, Want want)
    {
        datum.Document.Regenerate();
        var missed = new List<string>();
        Curve? now = datum.GetCurvesInView(DatumExtentType.ViewSpecific, target).FirstOrDefault();
        foreach (DatumEnds end in Ends)
        {
            int i = end == DatumEnds.End0 ? 0 : 1;
            DatumExtentType type = datum.GetDatumExtentTypeInView(end, target);
            if (type != want.Extent[end])
                missed.Add($"{EndName(end)} end {(want.Extent[end] == DatumExtentType.Model ? "3D" : "2D")}");
            else if (type == DatumExtentType.ViewSpecific && now != null &&
                     now.GetEndPoint(i).DistanceTo(want.Line.GetEndPoint(i)) > PlacedTolerance)
                missed.Add($"{EndName(end)} end position ({now.GetEndPoint(i).DistanceTo(want.Line.GetEndPoint(i)):0.###} ft off)");
            if (BubbleShown(datum, end, target) != s.Bubble[end])
                missed.Add($"{EndName(end)} bubble {(s.Bubble[end] ? "on" : "off")}");
        }
        return missed;
    }

    private static bool CategoryHidden(View view, DatumPlane datum)
    {
        try { return view.GetCategoryHidden(new ElementId(datum is Level ? BuiltInCategory.OST_Levels : BuiltInCategory.OST_Grids)); }
        catch { return false; }
    }

    /// <summary>
    /// Adds (when missing) and shapes an elbow like the source's, moved by <paramref name="move"/>.
    /// Null when it worked.
    ///
    /// <para>Read a leader only after <c>doc.Regenerate()</c>. Measured on Revit 2026.5 (self-test
    /// "MD probe"): a leader read straight after AddLeader is not where Revit will draw it, and
    /// shaping from that stale copy landed every copied elbow 2.08 ft further out (and moved it
    /// again on every rerun). After a regeneration SetLeader places the points exactly. The loop
    /// still reads back and corrects, so a leader Revit will not settle is reported, never claimed.</para>
    /// </summary>
    private static string? SetElbow(DatumPlane datum, DatumEnds end, View target, Leader want, XYZ move, bool add)
    {
        try
        {
            if (add) datum.AddLeader(end, target);
            XYZ wantEnd = want.End + move, wantElbow = want.Elbow + move;
            XYZ askEnd = wantEnd, askElbow = wantElbow;
            Document doc = datum.Document;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                doc.Regenerate();
                Leader? leader = datum.GetLeader(end, target);
                if (leader == null) return "Revit added no elbow";
                if (attempt > 0 && Same(leader.End, wantEnd) && Same(leader.Elbow, wantElbow)) return null;
                if (attempt > 0)
                {
                    askEnd -= leader.End - wantEnd;
                    askElbow -= leader.Elbow - wantElbow;
                }
                try { leader.End = askEnd; leader.Elbow = askElbow; }
                catch { leader.Elbow = askElbow; leader.End = askEnd; }   // the other order, when one point sat on the other
                if (!datum.IsLeaderValid(end, target, leader)) return "Revit says that elbow is not valid in this view";
                datum.SetLeader(end, target, leader);
            }
            doc.Regenerate();
            Leader? last = datum.GetLeader(end, target);
            if (last != null && Same(last.End, wantEnd) && Same(last.Elbow, wantElbow)) return null;
            return last == null ? "Revit removed the elbow" :
                $"Revit would not settle the elbow where the source has it ({last.Elbow.DistanceTo(wantElbow):0.###} ft off)";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static Leader? LeaderAt(DatumPlane d, DatumEnds end, View view)
    {
        try { return d.GetLeader(end, view); }
        catch { return null; }
    }

    private static bool BubbleShown(DatumPlane d, DatumEnds end, View view)
    {
        try { return d.IsBubbleVisibleInView(end, view); }
        catch { return false; }
    }

    private static bool Same(XYZ a, XYZ b) => a.DistanceTo(b) < Tolerance;

    private static string EndName(DatumEnds end) => end == DatumEnds.End0 ? "Start" : "End";

    /// <summary>
    /// A view's crop rectangle in its own crop coordinates (X right, Y up, as
    /// <see cref="View.CropBox"/>'s transform gives them) — the same for a plan, a section and an
    /// elevation, and for a crop that follows a scope box. A non-rectangular crop shape is taken as
    /// its bounding rectangle. Used only to SAY that an end is hidden; ends are never moved by it.
    /// </summary>
    private sealed class Crop
    {
        private readonly Transform _inv;
        private readonly double _minX, _minY, _maxX, _maxY;

        private Crop(BoundingBoxXYZ box)
        {
            _inv = box.Transform.Inverse;
            _minX = box.Min.X; _minY = box.Min.Y; _maxX = box.Max.X; _maxY = box.Max.Y;
        }

        public static Crop? Of(View v) => v.CropBoxActive ? new Crop(v.CropBox) : null;

        public bool Contains(XYZ p)
        {
            XYZ q = _inv.OfPoint(p);
            return q.X >= _minX - Tolerance && q.X <= _maxX + Tolerance && q.Y >= _minY - Tolerance && q.Y <= _maxY + Tolerance;
        }
    }
}
