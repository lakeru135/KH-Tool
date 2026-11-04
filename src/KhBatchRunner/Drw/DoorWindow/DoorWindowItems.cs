using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>
/// One block on the sheet (D1, W1…): every door or window type carrying one Type Mark, merged, with
/// the instances placed of them. The first type (the one with the most instances) is the one the
/// legend draws and whose width, height and sill are dimensioned.
/// </summary>
internal sealed class DwItem
{
    public string Mark { get; init; } = "";
    public bool IsDoor { get; init; }
    public List<FamilySymbol> Types { get; } = new();
    public List<FamilyInstance> Instances { get; } = new();
    public FamilySymbol Primary => Types[0];
    /// <summary>Of <see cref="Primary"/>, in feet. Sill is the type's default (0 for doors).</summary>
    public double Width { get; set; }
    public double Height { get; set; }
    public double Sill { get; set; }
    /// <summary>Width or height is an INSTANCE parameter: a legend component can only draw the
    /// family at its default size, so the cell draws an outline at the placed size instead (the
    /// user, 2026-10-22).</summary>
    public bool InstanceSized { get; set; }
    /// <summary>The first placed instance's host wall thickness, feet (0: no wall host): the plan
    /// outline's depth.</summary>
    public double WallThickness { get; set; }
    public List<string> Warnings { get; } = new();
    /// <summary>The key the legend's own elements are tagged with: category + Type Mark.</summary>
    public string Key => (IsDoor ? "D|" : "W|") + Mark;
}

/// <summary>Where a table row's value comes from.</summary>
internal enum DwRowKind
{
    /// <summary>Width x height: the schedule's combined field (<see cref="DwRowDef.Column"/>), or with
    /// no schedule yet the type's, written in <see cref="DoorWindowPreset.SizeFormat"/>.</summary>
    Size,
    /// <summary>How many: the schedule's Count field, or with no schedule yet the placed instances
    /// carrying the item's Type Mark.</summary>
    Qty,
    /// <summary>A column of the set's schedule.</summary>
    Cell
}

/// <summary>One row of every item's table: its label and where its value comes from.</summary>
internal readonly record struct DwRowDef(string Label, DwRowKind Kind, int Column);

internal static class DoorWindowItems
{
    /// <summary>
    /// The table rows: the schedule's shown fields in order (Type Mark and the description column
    /// are the header's, not rows); its combined field is the Size row, its Count field the Qty row.
    /// With no schedule yet, this tool's own Size first and Qty last (if on).
    /// </summary>
    public static List<DwRowDef> Rows(DoorWindowPreset p, DwScheduleData? s)
    {
        var rows = new List<DwRowDef>();
        if (s == null)
        {
            if (p.SizeRow) rows.Add(new DwRowDef(p.SizeLabel, DwRowKind.Size, -1));
            if (p.QtyRow) rows.Add(new DwRowDef(p.QtyLabel, DwRowKind.Qty, -1));
            return rows;
        }
        for (int i = 0; i < s.Headings.Count; i++)
            if (i != DescriptionColumn(p, s))
                rows.Add(new DwRowDef(s.Headings[i], i == s.SizeColumn ? DwRowKind.Size : i == s.CountColumn ? DwRowKind.Qty : DwRowKind.Cell, i));
        return rows;
    }

    /// <summary>
    /// The Size row's text from the schedule's combined field. An instance-sized type's cell reads
    /// "&lt;varies&gt;" (or the type's unused default), so its placed size is written instead
    /// (<paramref name="measured"/>); a type of no size found shows blank, not "0 x 0".
    /// </summary>
    public static string SizeCell(string cell, bool instanceSized, string measured) =>
        instanceSized || cell.IndexOf("<varies>", StringComparison.OrdinalIgnoreCase) >= 0 ? measured : cell;

    /// <summary>The schedule column the header shows beside the mark, or -1.</summary>
    public static int DescriptionColumn(DoorWindowPreset p, DwScheduleData? s) =>
        s == null || p.DescriptionHeading.Length == 0 ? -1
            : s.Headings.FindIndex(h => string.Equals(h.Trim(), p.DescriptionHeading.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>What a row shows for an item.</summary>
    public static string Value(DwItem item, DwRowDef r, DoorWindowPreset p, DwScheduleData? s)
    {
        string v = r.Kind switch
        {
            DwRowKind.Size when s != null && r.Column >= 0 =>
                item.Width > 0 && item.Height > 0 ? SizeCell(s.Cell(item.Mark, r.Column), item.InstanceSized, Size(item.Primary.Document, item.Width, item.Height, DwSizeFormat.ProjectUnits)) : "",
            DwRowKind.Qty when s != null && r.Column >= 0 => s.Cell(item.Mark, r.Column),
            DwRowKind.Size => item.Width > 0 && item.Height > 0 ? Size(item.Primary.Document, item.Width, item.Height, p.SizeFormat) : "",
            DwRowKind.Qty => item.Instances.Count.ToString(),
            _ => s == null ? "" : CellValue(s, item.Mark, r, p)
        };
        return string.IsNullOrWhiteSpace(v) ? p.Blank : v;
    }

    /// <summary>A schedule row's value for a mark, by the row's "Value from" (saved on the schedule):
    /// the cell, a property of the field's material, or a type or instance parameter. Plain data, so
    /// the dialog previews exactly what the build writes.</summary>
    public static string CellValue(DwScheduleData s, string mark, DwRowDef r, DoorWindowPreset p) => s.Value(mark, r.Column);

    /// <summary>The header's description text for an item ("" when there is none).</summary>
    public static string Description(DwItem item, DoorWindowPreset p, DwScheduleData? s) =>
        DescriptionColumn(p, s) is int c && c >= 0 ? s!.Cell(item.Mark, c) : "";

    /// <summary>The set's schedule, read now; null (and a warning) when the set has none yet
    /// (<see cref="DwSchedule.Ensure"/> makes it).</summary>
    public static DwScheduleData? Schedule(Document doc, DoorWindowPreset p, List<string> warnings)
    {
        if (DwSchedule.Find(doc, p) is not { } s) { warnings.Add("The set has no schedule yet: only the Size and Qty rows are drawn."); return null; }
        DwScheduleData data = DwSchedule.Read(s);
        foreach (string problem in data.Problems) warnings.Add($"Schedule '{s.Name}': {problem}");
        return data;
    }

    /// <summary>
    /// The items to schedule: placed door (or window) types grouped by Type Mark. Types never
    /// placed are left out, as a Revit schedule leaves them out; with a schedule, so are instances
    /// its filters leave out.
    /// </summary>
    public static List<DwItem> Collect(Document doc, DoorWindowPreset p, List<string> warnings, DwScheduleData? schedule = null) =>
        Collect(doc, p.Category == DwCategory.Door, p.IncludeUnmarked, warnings, schedule);

    public static List<DwItem> Collect(Document doc, bool isDoor, bool includeUnmarked, List<string> warnings, DwScheduleData? schedule = null)
    {
        var items = new List<DwItem>();
        BuiltInCategory cat = isDoor ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows;
        var byType = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType()
            .OfType<FamilyInstance>().Where(i => i.Symbol != null && i.SuperComponent == null
                                                 && (schedule?.Instances == null || schedule.Instances.Contains(i.Id.Value)))
            .GroupBy(i => i.Symbol.Id).ToList();

        var byMark = byType.GroupBy(g => TypeMark(g.First().Symbol), StringComparer.OrdinalIgnoreCase);
        foreach (var group in byMark)
        {
            string mark = group.Key;
            if (mark.Length == 0 && !includeUnmarked)
            {
                warnings.Add($"{group.Count()} {(isDoor ? "door" : "window")} type(s) have no Type Mark and are left out: " +
                    string.Join(", ", group.Select(g => TypeName(g.First().Symbol)).Take(5)) + (group.Count() > 5 ? "…" : ""));
                continue;
            }

            var item = new DwItem { Mark = mark, IsDoor = isDoor };
            foreach (var g in group.OrderByDescending(g => g.Count()))
            {
                item.Types.Add(g.First().Symbol);
                item.Instances.AddRange(g);
            }
            Measure(item);

            var sizes = item.Types.Select(t => (W: Round(Width(t, isDoor)), H: Round(Height(t, isDoor)))).Distinct().ToList();
            if (sizes.Count > 1)
                item.Warnings.Add($"Type Mark {Show(mark)} is on {item.Types.Count} types of {sizes.Count} sizes: " +
                    string.Join(", ", sizes.Select(s => $"{s.W}x{s.H}")) + " — the legend draws " + TypeName(item.Primary));
            else if (item.Types.Count > 1)
                item.Warnings.Add($"Type Mark {Show(mark)} merges {item.Types.Count} types of the same size: " +
                    string.Join(", ", item.Types.Select(TypeName)));
            items.Add(item);
        }
        // A Type Mark the schedule has no row for (its filters keep the types but not that mark's
        // row, or the schedule is out of step): its values would all be blank, so say so.
        if (schedule != null && schedule.ByMark.Count > 0)
            foreach (DwItem i in items.Where(i => !schedule.ByMark.ContainsKey(i.Mark)))
                i.Warnings.Add($"No row for Type Mark {Show(i.Mark)} in schedule '{schedule.Name}'; its schedule rows are blank.");

        return items.OrderBy(i => i.Mark, NaturalComparer.Instance).ToList();
    }

    public static string Size(Document? doc, double w, double h, DwSizeFormat format) => format switch
    {
        DwSizeFormat.ProjectUnits when doc != null =>
            UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, w, false) + " x " +
            UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, h, false),
        DwSizeFormat.Metres => $"{w * 0.3048:0.00} x {h * 0.3048:0.00} m",
        _ => $"{Round(w)} x {Round(h)} mm"
    };

    /// <summary>"metric (Millimeters)" / "imperial (Feet and fractional inches)": how the project shows lengths.</summary>
    public static string ProjectUnitsLabel(Document doc)
    {
        ForgeTypeId unit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
        bool imperial = unit == UnitTypeId.Feet || unit == UnitTypeId.Inches || unit == UnitTypeId.FeetFractionalInches
                        || unit == UnitTypeId.FractionalInches;
        return (imperial ? "imperial" : "metric") + " (" + LabelUtils.GetLabelForUnit(unit) + ")";
    }

    // ------------------------------------------------------------------ sizes

    private static void Measure(DwItem item)
    {
        item.Width = Width(item.Primary, item.IsDoor);
        item.Height = Height(item.Primary, item.IsDoor);
        if (item.Width <= 0 || item.Height <= 0)
        {
            // Some families keep width/height as instance parameters; read the first placed one.
            FamilyInstance first = item.Instances[0];
            if (item.Width <= 0) item.Width = Len(first, item.IsDoor ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM);
            if (item.Height <= 0) item.Height = Len(first, item.IsDoor ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM);
            if (item.Width <= 0 || item.Height <= 0) item.Warnings.Add($"{TypeName(item.Primary)}: width or height not found; size left blank");
            else
            {
                item.InstanceSized = true;
                item.Warnings.Add($"{TypeName(item.Primary)}: width/height are instance parameters; drawn as an outline at the placed size");
            }
        }
        if (item.Instances.Count > 0 && item.Instances[0].Host is Wall wall) item.WallThickness = wall.Width;
        // The user's rule: the TYPE's default sill height, whatever the placed ones say.
        item.Sill = item.IsDoor ? 0 : Len(item.Primary, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
        if (!item.IsDoor && item.Sill <= 0 && item.Primary.LookupParameter("Default Sill Height") is { StorageType: StorageType.Double } ds)
            item.Sill = ds.AsDouble();
    }

    private static double Width(FamilySymbol t, bool door) =>
        Len(t, door ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM);

    private static double Height(FamilySymbol t, bool door) =>
        Len(t, door ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM);

    private static double Len(Element e, params BuiltInParameter[] bips)
    {
        foreach (BuiltInParameter b in bips)
            if (e.get_Parameter(b) is { StorageType: StorageType.Double, HasValue: true } p && p.AsDouble() > 0) return p.AsDouble();
        return 0;
    }

    private static long Round(double ft) => (long)Math.Round(ft * 304.8);

    public static string TypeMark(ElementType t) => t.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString()?.Trim() ?? "";

    public static string TypeName(ElementType t) => $"{t.FamilyName} : {t.Name}";

    private static string Show(string mark) => mark.Length == 0 ? "(none)" : mark;
}

/// <summary>D2 before D10: digit runs compare as numbers.</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    private static readonly Regex Parts = new(@"\d+|\D+");

    public int Compare(string? a, string? b)
    {
        var pa = Parts.Matches(a ?? ""); var pb = Parts.Matches(b ?? "");
        for (int i = 0; i < Math.Min(pa.Count, pb.Count); i++)
        {
            string x = pa[i].Value, y = pb[i].Value;
            int c = char.IsDigit(x[0]) && char.IsDigit(y[0])
                ? x.TrimStart('0').Length != y.TrimStart('0').Length
                    ? x.TrimStart('0').Length.CompareTo(y.TrimStart('0').Length)
                    : string.CompareOrdinal(x.TrimStart('0'), y.TrimStart('0'))
                : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return pa.Count.CompareTo(pb.Count);
    }
}
