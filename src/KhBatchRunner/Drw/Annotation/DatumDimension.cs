using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

/// <summary>Which end of the datums the dimension strings sit at, as the VIEW shows them.</summary>
public enum GridSide
{
    /// <summary>The top of vertical datums and the right of horizontal ones.</summary>
    TopRight,
    /// <summary>The bottom of vertical datums and the left of horizontal ones.</summary>
    BottomLeft,
    Both,
    /// <summary>For each set of parallel datums, the end showing more bubbles in the view. A tie,
    /// including none at all, dimensions both ends.</summary>
    MostBubbles
}

/// <summary>
/// A Datum Dimension preset. Saved in the <c>GridDimensionPresets</c> folder the tool used when it
/// was Grid Dimension, so presets saved before the rename still load; a preset saved then has no
/// Grids/Levels/Plans/Sections/Elevations and so keeps its old meaning (grids, plans only).
/// </summary>
public sealed class DatumDimensionPreset : IDrwPreset
{
    public string Name { get; set; } = "Default";
    public string DimensionTypeName { get; set; } = "";
    /// <summary>One string through every datum.</summary>
    public bool EveryGrid { get; set; } = true;
    /// <summary>One string from the first datum to the last.</summary>
    public bool Overall { get; set; } = true;
    /// <summary>Paper distance from the datum end (bubble) inward, in mm at the view's scale.</summary>
    public double OverallOffsetMm { get; set; } = 10;
    public double EveryGridOffsetMm { get; set; } = 18;
    /// <summary>Space the strings by the dimension type's "Dimension Line Snap Distance" instead of
    /// <see cref="EveryGridOffsetMm"/>: every datum sits one snap distance in from the overall.</summary>
    public bool SpacingFromType { get; set; }
    /// <summary>One more every-datum string, one gap further in — for the user to edit by hand.</summary>
    public bool ExtraString { get; set; }
    public GridSide Side { get; set; } = GridSide.MostBubbles;
    /// <summary>Dimension grids (plans, sections and elevations).</summary>
    public bool Grids { get; set; } = true;
    /// <summary>Dimension levels (sections and elevations; plans do not show levels).</summary>
    public bool Levels { get; set; } = true;
    /// <summary>Which kinds of view the name-filter scope (and so Batch Run) covers.</summary>
    public bool Plans { get; set; } = true;
    public bool Sections { get; set; }
    public bool Elevations { get; set; }
    public DrwViewScope Scope { get; set; } = DrwViewScope.ActiveView;
    public string ViewNameContains { get; set; } = "";
    /// <summary>Leave a view's grids alone when it already has a dimension made only of grids, and
    /// its levels alone when it already has one made only of levels.</summary>
    public bool SkipDimensioned { get; set; } = true;
}

public static class DatumDimensionStore
{
    // The folder keeps its Grid Dimension name: renaming it would orphan every saved preset.
    public static readonly DrwPresetStore<DatumDimensionPreset> Store = new("GridDimensionPresets");
}

/// <summary>
/// Dimensions datums: grids in plans, and grids and levels in sections and elevations. For each set
/// of EXACTLY parallel straight datums, one overall string (first to last), one through every datum
/// and optionally one more, placed just inside the datum ends.
///
/// <para>Everything is judged in the view's own frame (<see cref="View.RightDirection"/> /
/// <see cref="View.UpDirection"/>), so "top/right" means what the view shows — a rotated crop or a
/// section reads the same as a plain plan — and positions come from each datum's extents IN THAT
/// VIEW, so 2D-adjusted ends are followed.</para>
///
/// <para>Each string is its own transaction. Revit refuses a string whose datums are not exactly
/// parallel, and before 0.2.6 one refusal rolled back every string in the view; now it costs only
/// that string, which is reported with Revit's reason. A transaction that commits is still checked
/// for the dimension afterwards — a commit is not proof of the edit.</para>
/// </summary>
internal static class DatumDimensioner
{
    public static readonly IReadOnlyList<string> Columns = new[] { "View", "Datums", "Dimension", "Status", "Note" };

    /// <summary>Two directions count as parallel when the sine of the angle between them is below
    /// this (about 0.00006°). Anything looser is what made Revit refuse whole views.</summary>
    private const double ParallelSine = 1e-6;

    /// <summary>A datum this close to another family's direction is named as "nearly parallel"
    /// rather than just "only one in this direction", because that is almost always a drafting slip.</summary>
    private const double NearlyParallelDegrees = 2.0;

    public static List<IReadOnlyDictionary<string, string>> Run(Document doc, IEnumerable<View> views, DatumDimensionPreset p)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        var warnings = new List<string>();
        List<DimensionType> dimTypes = LinearTypes(doc);
        // "Family : Type" label first, bare name for presets saved before labels; null = Revit default.
        DimensionType? dimType = dimTypes.FirstOrDefault(t => DrwLookup.TypeLabel(t) == p.DimensionTypeName)
                                 ?? dimTypes.FirstOrDefault(t => t.Name == p.DimensionTypeName);
        if (dimType == null && !string.IsNullOrWhiteSpace(p.DimensionTypeName))
            warnings.Add($"Dimension type '{p.DimensionTypeName}' is not in this model — Revit's default dimension type was used");

        // The gap between strings, in paper mm: the type's snap distance when asked and it has one.
        double gapMm = p.EveryGridOffsetMm - p.OverallOffsetMm;
        if (p.SpacingFromType)
        {
            double? snap = SnapDistanceMm(dimType);
            if (snap is > 0) gapMm = snap.Value;
            else warnings.Add((dimType == null ? "Revit's default dimension type" : $"'{DrwLookup.TypeLabel(dimType)}'") +
                              " has no Dimension Line Snap Distance — the strings are spaced by the mm values");
        }
        if (warnings.Count > 0)
            rows.Add(DrwRows.Row(Columns, "", "", "Setup", "Warning", string.Join("; ", warnings)));

        using var group = new TransactionGroup(doc, "DRW Datum Dimension");
        group.Start();
        foreach (View view in views)
        {
            try
            {
                rows.AddRange(DimensionView(doc, view, p, dimType, gapMm));
            }
            catch (Exception ex)
            {
                rows.Add(DrwRows.Row(Columns, view.Name, "", "", "Failed", ex.Message));
            }
        }
        group.Assimilate();
        return rows;
    }

    public static List<DimensionType> LinearTypes(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(DimensionType))
            .Cast<DimensionType>().Where(t => t.StyleType == DimensionStyleType.Linear).ToList();

    /// <summary>The type's "Dimension Line Snap Distance" in paper millimetres, or null if it has none.</summary>
    public static double? SnapDistanceMm(DimensionType? type)
    {
        Parameter? snap = type?.get_Parameter(BuiltInParameter.DIM_STYLE_DIM_LINE_SNAP_DIST);
        if (snap == null || snap.StorageType != StorageType.Double) return null;
        return UnitUtils.ConvertFromInternalUnits(snap.AsDouble(), UnitTypeId.Millimeters);
    }

    private static List<IReadOnlyDictionary<string, string>> DimensionView(Document doc, View view,
        DatumDimensionPreset p, DimensionType? dimType, double gapMm)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        bool plan = view is ViewPlan;
        bool grids = p.Grids;
        bool levels = p.Levels && !plan;   // a plan shows no levels
        if (!grids && !levels)
        {
            rows.Add(DrwRows.Row(Columns, view.Name, "", "", "Skipped",
                "Nothing to dimension here: plans show grids only, and Grids is not ticked"));
            return rows;
        }
        if (grids) DimensionKind(doc, view, false, p, dimType, gapMm, rows);
        if (levels) DimensionKind(doc, view, true, p, dimType, gapMm, rows);
        return rows;
    }

    /// <summary>One datum as the view shows it.</summary>
    private sealed class Item
    {
        public Item(DatumPlane datum, Line line, V2 p0, V2 p1, V2 dir, XYZ modelDir)
        {
            Datum = datum; Line = line; P0 = p0; P1 = p1; Dir = dir; ModelDir = modelDir;
        }

        public DatumPlane Datum { get; }
        /// <summary>The datum's extents in this view.</summary>
        public Line Line { get; }
        /// <summary>Its ends in the view's 2D frame; P0 is <see cref="DatumEnds.End0"/>.</summary>
        public V2 P0 { get; }
        public V2 P1 { get; }
        /// <summary>Its canonical direction on paper.</summary>
        public V2 Dir { get; }
        /// <summary>Its direction in the model: a grid's plan direction, a level's normal. Two grids
        /// can look parallel in a section (every grid is a vertical line there) and still not be.</summary>
        public XYZ ModelDir { get; }
    }

    private static void DimensionKind(Document doc, View view, bool levels, DatumDimensionPreset p,
        DimensionType? dimType, double gapMm, List<IReadOnlyDictionary<string, string>> rows)
    {
        string kind = levels ? "levels" : "grids";
        if (p.SkipDimensioned && HasDatumDimension(doc, view, levels))
        {
            rows.Add(DrwRows.Row(Columns, view.Name, "", Capital(kind), "Skipped", $"Already has {(levels ? "level" : "grid")} dimensions"));
            return;
        }

        var notes = new List<string>();
        List<Item> items = Collect(doc, view, levels, notes);
        if (items.Count < 2)
        {
            rows.Add(DrwRows.Row(Columns, view.Name, items.Count.ToString(), Capital(kind), "Skipped",
                Join($"Fewer than two straight {kind} in this view", notes)));
            return;
        }
        string setupNote = string.Join("; ", notes);

        List<List<Item>> families = Group(items);
        foreach (List<Item> family in families)
        {
            string label = Label(view, levels, family[0].Dir);
            if (family.Count < 2)
            {
                rows.Add(DrwRows.Row(Columns, view.Name, "1", label, "Skipped", NearlyParallelNote(family[0], families, levels)));
                continue;
            }
            DimensionFamily(doc, view, family, levels, label, p, dimType, gapMm, setupNote, rows);
            setupNote = "";   // said once, on the first family's rows
        }
    }

    private static List<Item> Collect(Document doc, View view, bool levels, List<string> notes)
    {
        var items = new List<Item>();
        XYZ right = view.RightDirection, up = view.UpDirection;
        int curved = 0, endOn = 0;
        IEnumerable<DatumPlane> datums = levels
            ? new FilteredElementCollector(doc, view.Id).OfClass(typeof(Level)).Cast<DatumPlane>()
            : new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<DatumPlane>();
        foreach (DatumPlane d in datums)
        {
            if (d is Grid g && g.IsCurved) { curved++; continue; }
            Line? line;
            try { line = d.GetCurvesInView(DatumExtentType.ViewSpecific, view).OfType<Line>().FirstOrDefault(); }
            catch { line = null; }   // not drawable in this view
            if (line == null) continue;

            V2 p0 = V2.In(line.GetEndPoint(0), right, up), p1 = V2.In(line.GetEndPoint(1), right, up);
            V2 dir = (p1 - p0).Normalized();
            if (dir.IsZero) { endOn++; continue; }
            XYZ modelDir = d is Grid grid && grid.Curve is Line gl
                ? Canonical3(gl.Direction)
                : XYZ.BasisZ;
            items.Add(new Item(d, line, p0, p1, dir.Canonical(), modelDir));
        }
        if (curved > 0) notes.Add($"{curved} arc grid(s) left out — a straight string cannot measure an arc");
        if (endOn > 0) notes.Add($"{endOn} datum(s) seen end-on in this view left out");
        return items;
    }

    /// <summary>Families of EXACTLY parallel datums — on paper and in the model.</summary>
    private static List<List<Item>> Group(List<Item> items)
    {
        var families = new List<List<Item>>();
        foreach (Item it in items)
        {
            List<Item>? home = families.FirstOrDefault(f =>
                Math.Abs(f[0].Dir.Cross(it.Dir)) < ParallelSine &&
                f[0].ModelDir.CrossProduct(it.ModelDir).GetLength() < ParallelSine);
            if (home == null) families.Add(new List<Item> { it });
            else home.Add(it);
        }
        return families;
    }

    /// <summary>Why a lone datum got no string: nothing parallel to it, or something ALMOST parallel.</summary>
    private static string NearlyParallelNote(Item lone, List<List<Item>> families, bool levels)
    {
        string what = levels ? "level" : "grid";
        foreach (List<Item> other in families)
        {
            if (other[0] == lone) continue;
            double degrees = Math.Asin(Math.Min(1, Math.Abs(other[0].Dir.Cross(lone.Dir)))) * 180 / Math.PI;
            double modelDegrees = Math.Asin(Math.Min(1, other[0].ModelDir.CrossProduct(lone.ModelDir).GetLength())) * 180 / Math.PI;
            double apart = Math.Max(degrees, modelDegrees);
            if (apart < NearlyParallelDegrees)
                return $"{Capital(what)} {lone.Datum.Name} is nearly parallel to {string.Join(", ", other.Take(3).Select(i => i.Datum.Name))}" +
                       $"{(other.Count > 3 ? "…" : "")} ({apart:0.###}° apart), so it was left out: Revit refuses a string " +
                       $"across {what}s that are not exactly parallel. Straighten it to include it.";
        }
        return $"Only one {what} in this direction";
    }

    private static void DimensionFamily(Document doc, View view, List<Item> family, bool levels, string label,
        DatumDimensionPreset p, DimensionType? dimType, double gapMm, string setupNote,
        List<IReadOnlyDictionary<string, string>> rows)
    {
        V2 d = family[0].Dir;
        V2 n = d.Perpendicular();
        family.Sort((a, b) => a.P0.Dot(n).CompareTo(b.P0.Dot(n)));
        bool upright = Math.Abs(d.Y) > Math.Abs(d.X);
        string highName = upright ? "top" : "right", lowName = upright ? "bottom" : "left";

        var sides = new List<bool>();   // true = the datums' high end along d
        string bubbleNote = "";
        switch (p.Side)
        {
            case GridSide.TopRight: sides.Add(true); break;
            case GridSide.BottomLeft: sides.Add(false); break;
            case GridSide.Both: sides.Add(true); sides.Add(false); break;
            default:
                (int high, int low) = CountBubbles(view, family, d);
                if (high > low) sides.Add(true);
                else if (low > high) sides.Add(false);
                else { sides.Add(true); sides.Add(false); }
                bubbleNote = $"Bubbles shown: {highName} {high}, {lowName} {low}" +
                             (high == low ? " — a tie, so both ends" : "");
                break;
        }
        string note = string.Join("; ", new[] { bubbleNote, setupNote }.Where(s => s.Length > 0));

        double scale = Math.Max(1, view.Scale);
        DatumPlane[] all = family.Select(f => f.Datum).ToArray();
        foreach (bool high in sides)
        {
            string sideName = high ? highName : lowName;
            // From the datum end inward: overall, every datum one gap in, the extra one more gap in.
            double overallMm = p.OverallOffsetMm, everyMm = overallMm + gapMm;
            double extraMm = everyMm + (gapMm > 0.5 ? gapMm : 8);   // never on top of the every-datum string
            string every = levels ? "Every level" : "Every grid";
            if (p.Overall && (family.Count > 2 || !p.EveryGrid))
                rows.Add(Place(doc, view, family, d, n, high, DrwUnits.FromMillimeters(overallMm) * scale,
                    new[] { family[0].Datum, family[family.Count - 1].Datum }, dimType, label, "Overall (" + sideName + ")", note));
            if (p.EveryGrid)
                rows.Add(Place(doc, view, family, d, n, high, DrwUnits.FromMillimeters(everyMm) * scale,
                    all, dimType, label, every + " (" + sideName + ")", note));
            if (p.ExtraString)
                rows.Add(Place(doc, view, family, d, n, high, DrwUnits.FromMillimeters(extraMm) * scale,
                    all, dimType, label, "Extra " + every.ToLowerInvariant() + ", to edit (" + sideName + ")", note));
        }
    }

    /// <summary>How many of the family show a bubble at their high end and at their low end.</summary>
    private static (int High, int Low) CountBubbles(View view, List<Item> family, V2 d)
    {
        int high = 0, low = 0;
        foreach (Item it in family)
        {
            bool end1High = it.P1.Dot(d) >= it.P0.Dot(d);
            DatumEnds highEnd = end1High ? DatumEnds.End1 : DatumEnds.End0;
            DatumEnds lowEnd = end1High ? DatumEnds.End0 : DatumEnds.End1;
            if (BubbleShown(it.Datum, highEnd, view)) high++;
            if (BubbleShown(it.Datum, lowEnd, view)) low++;
        }
        return (high, low);
    }

    private static bool BubbleShown(DatumPlane datum, DatumEnds end, View view)
    {
        try { return datum.IsBubbleVisibleInView(end, view); }
        catch { return false; }
    }

    private static IReadOnlyDictionary<string, string> Place(Document doc, View view, List<Item> family,
        V2 d, V2 n, bool high, double offset, DatumPlane[] refs, DimensionType? dimType, string label,
        string what, string note)
    {
        // Along d: stay inside the SHORTEST reach of the family's ends, so the string crosses every datum.
        double along = high
            ? family.Min(f => Math.Max(f.P0.Dot(d), f.P1.Dot(d))) - offset
            : family.Max(f => Math.Min(f.P0.Dot(d), f.P1.Dot(d))) + offset;
        double from = family.First().P0.Dot(n);
        double to = family.Last().P0.Dot(n);

        // Back to the model on the first datum's plane in depth: a plan keeps its Z, a section its cut.
        XYZ basePoint = family[0].Line.GetEndPoint(0);
        XYZ p0 = To3D(view, basePoint, d * along + n * from);
        XYZ p1 = To3D(view, basePoint, d * along + n * to);
        string dimension = label + " — " + what;
        string count = refs.Length.ToString();

        var array = new ReferenceArray();
        foreach (DatumPlane datum in refs)
            array.Append(datum is Level level ? level.GetPlaneReference() : new Reference(datum));

        using Transaction t = DrwStrictFailures.Start(doc, "Datum Dimension " + view.Name, out DrwStrictFailures failures);
        Dimension? dim;
        try
        {
            Line line = Line.CreateBound(p0, p1);
            dim = dimType != null
                ? doc.Create.NewDimension(view, line, array, dimType)
                : doc.Create.NewDimension(view, line, array);
        }
        catch (Exception ex)
        {
            t.RollBack();
            return DrwRows.Row(Columns, view.Name, count, dimension, "Failed", Join(ex.Message, note));
        }
        if (dim == null)
        {
            t.RollBack();
            return DrwRows.Row(Columns, view.Name, count, dimension, "Failed", Join("Revit returned no dimension", note));
        }
        ElementId id = dim.Id;
        if (t.Commit() != TransactionStatus.Committed)
            return DrwRows.Row(Columns, view.Name, count, dimension, "Failed",
                Join("Revit refused it" + (failures.Errors.Count > 0 ? ": " + string.Join("; ", failures.Errors) : ""), note));
        if (doc.GetElement(id) == null)
            return DrwRows.Row(Columns, view.Name, count, dimension, "Failed",
                Join("Revit deleted the dimension when the change was committed", note));
        return DrwRows.Row(Columns, view.Name, count, dimension, "Created", note);
    }

    private static XYZ To3D(View view, XYZ basePoint, V2 at) =>
        basePoint + view.RightDirection * (at.X - basePoint.DotProduct(view.RightDirection))
                  + view.UpDirection * (at.Y - basePoint.DotProduct(view.UpDirection));

    private static string Label(View view, bool levels, V2 d)
    {
        if (levels) return "Levels";
        if (view is not ViewPlan) return "Grids";
        double angle = Math.Atan2(d.Y, d.X) * 180 / Math.PI;   // (-90, 90] for a canonical direction
        if (Math.Abs(angle - 90) < 0.01) return "Vertical grids";
        if (Math.Abs(angle) < 0.01) return "Horizontal grids";
        return $"Grids at {angle:0.##}°";
    }

    /// <summary>A plan direction flipped to point right (or up when vertical), so parallel grids agree.</summary>
    private static XYZ Canonical3(XYZ dir)
    {
        XYZ d = new XYZ(dir.X, dir.Y, 0);
        if (d.GetLength() < 1e-12) return XYZ.BasisZ;
        d = d.Normalize();
        return d.X < -1e-9 || (Math.Abs(d.X) <= 1e-9 && d.Y < 0) ? d.Negate() : d;
    }

    /// <summary>True when the view already has a dimension made only of grids (or only of levels).</summary>
    private static bool HasDatumDimension(Document doc, View view, bool levels)
    {
        foreach (Dimension dim in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            if (dim.OwnerViewId != view.Id || dim.References == null || dim.References.Size < 2) continue;
            bool all = true;
            foreach (Reference r in dim.References)
            {
                Element? e = doc.GetElement(r.ElementId);
                if (levels ? e is not Level : e is not Grid) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    private static string Join(string first, IEnumerable<string> more) =>
        string.Join("; ", new[] { first }.Concat(more).Where(s => !string.IsNullOrEmpty(s)));

    private static string Join(string first, string second) => Join(first, new[] { second });
}

/// <summary>A point or direction on a view's paper: X along its right, Y along its up.</summary>
internal readonly struct V2
{
    public V2(double x, double y) { X = x; Y = y; }

    public double X { get; }
    public double Y { get; }

    public static V2 In(XYZ p, XYZ right, XYZ up) => new(p.DotProduct(right), p.DotProduct(up));

    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator *(V2 a, double k) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public bool IsZero => Length < 1e-9;
    public V2 Normalized() => Length < 1e-9 ? new V2(0, 0) : new V2(X / Length, Y / Length);
    public double Dot(V2 o) => X * o.X + Y * o.Y;
    public double Cross(V2 o) => X * o.Y - Y * o.X;
    /// <summary>Turned a quarter anticlockwise: across the family, in the order the datums sort.</summary>
    public V2 Perpendicular() => new(-Y, X);

    /// <summary>Flipped to point right (or up when vertical), so parallel datums agree.</summary>
    public V2 Canonical() => X < -1e-9 || (Math.Abs(X) <= 1e-9 && Y < 0) ? new V2(-X, -Y) : this;
}
