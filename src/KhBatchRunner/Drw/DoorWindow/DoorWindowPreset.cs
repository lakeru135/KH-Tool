using System.Text.Json.Serialization;
using KhBatchRunner.Drw.Common;

namespace KhBatchRunner.Drw.DoorWindow;

public enum DwSizeFormat
{
    /// <summary>As the project shows lengths: 900 x 2100 in a metric project, 3' - 0" x 7' - 0" in an
    /// imperial one (Manage > Project Units > Length).</summary>
    ProjectUnits,
    /// <summary>2500 x 2800 mm.</summary>
    Millimetres,
    /// <summary>2.50 x 2.80 m.</summary>
    Metres
}

/// <summary>What one set schedules: doors or windows, never both (the user, 2026-10-21: separate
/// sets, each with its own rows, legends and sheets).</summary>
public enum DwCategory { Door, Window }

/// <summary>
/// Everything Enlarge Door &amp; Window needs, saved as a named preset. Sizes on the sheet are in
/// paper millimetres, so the same preset suits any legend scale.
///
/// <para>The table rows come from a Revit schedule (<see cref="ScheduleName"/>, the user's choice
/// 2026-10-21): its shown fields are the rows, their headings the labels, in its field order; the
/// Size and Qty rows are its fields too: a combined Width x Height and Revit's Count (the user,
/// 2026-10-22), added by the tool when ticked.</para>
/// </summary>
public sealed class DoorWindowPreset : IDrwPreset, IDrwMigrating
{
    public string Name { get; set; } = "Default";

    // --- what is scheduled ---
    public DwCategory Category { get; set; } = DwCategory.Door;
    /// <summary>Types with no Type Mark: left out (with a warning) unless this is on.</summary>
    public bool IncludeUnmarked { get; set; }
    /// <summary>Keep the legends current as doors/windows change (the live updater). Off: only a
    /// build or Apply now updates them.</summary>
    public bool Live { get; set; } = true;

    /// <summary>The set's key in the model: the preset name per category, so a door set and a window
    /// set may share a name without rebuilding each other's legends.</summary>
    [JsonIgnore]
    public string SetKey => (Category == DwCategory.Door ? "Doors|" : "Windows|") + Name;

    // --- the table ---
    /// <summary>The set's own schedule's name, as last seen (for show only: the schedule is found
    /// by its tag, <see cref="DwSchedule.Find"/>, so renaming it in Revit changes nothing).</summary>
    public string ScheduleName { get; set; } = "";
    /// <summary>The schedule column shown beside the Type Mark in the header instead of as a row
    /// (blank = none).</summary>
    public string DescriptionHeading { get; set; } = "Description";
    /// <summary>A first row: the schedule's combined Width x Height field, headed <see cref="SizeLabel"/>
    /// (written in <see cref="SizeFormat"/> only while the set has no schedule yet).</summary>
    public bool SizeRow { get; set; } = true;
    public string SizeLabel { get; set; } = "ขนาด";
    /// <summary>A last row: the schedule's Count field, headed <see cref="QtyLabel"/>.</summary>
    public bool QtyRow { get; set; } = true;
    public string QtyLabel { get; set; } = "Qty.";
    public DwSizeFormat SizeFormat { get; set; } = DwSizeFormat.ProjectUnits;
    /// <summary>What an empty value shows.</summary>
    public string Blank { get; set; } = "-";

    // --- layout, paper mm (defaults: the user's own settings in Revit 2024, 2026-10-22) ---
    public int Scale { get; set; } = 50;
    public double CellWidthMm { get; set; } = 70;
    public double ElevationBoxMm { get; set; } = 70;
    public double PlanBoxMm { get; set; } = 30;
    public double HeaderMm { get; set; } = 10;
    /// <summary>Height of one line of a table row (a row with wrapped text is this times its lines).
    /// KH_DW_Row's labels are locked to planes these sizes drive, so they suit any scale.</summary>
    public double RowLineMm { get; set; } = 7.5;
    /// <summary>The row's label column; the label is centred in it.</summary>
    public double LabelWidthMm { get; set; } = 20;
    /// <summary>The gap between the label column and the value.</summary>
    public double TextInsetMm { get; set; } = 2.5;
    /// <summary>Cells across one legend, and rows of cells down it, before the next legend starts
    /// (with no title block; with one, the frame decides).</summary>
    public int Columns { get; set; } = 4;
    public int RowsPerLegend { get; set; } = 2;
    /// <summary>The floor line's height above the elevation box's bottom.</summary>
    public double FloorGapMm { get; set; } = 5;
    /// <summary>How far dimensions stand off the door/window.</summary>
    public double DimOffsetMm { get; set; } = 5;
    /// <summary>The level text beside the ▽ on each floor line (blank = no symbol).</summary>
    public string FloorLabel { get; set; } = "FFL.";
    /// <summary>Row text, for wrapping values (TH Sarabun New 2 mm in the KH_DW labels, families v2).
    /// Values wrap at their cell's value column (<see cref="DoorWindowLayout.ValueWidthMm"/>); the
    /// fixed "wrap at" width before 2026-10-22 is gone.</summary>
    public string RowFont { get; set; } = "TH Sarabun New";
    public double RowTextMm { get; set; } = 2;
    /// <summary>The host wall drawn either side of the opening in plan, paper mm (each side).</summary>
    public double WallStubMm { get; set; } = 15;
    /// <summary>The titles written at the top of the two boxes.</summary>
    public string ElevationTitle { get; set; } = "Elevation View";
    public string PlanTitle { get; set; } = "Plan View";
    /// <summary>Elevation box above the plan box (Thai A-12 style); off = plan first.</summary>
    public bool ElevationFirst { get; set; } = true;
    public string DimensionTypeName { get; set; } = "";
    /// <summary>The legend per sheet; {n} = its number.</summary>
    public string LegendName { get; set; } = "Door Schedule {n}";

    // --- sheets ---
    /// <summary>"Family : Type" of the title block. Blank = legends only, laid out
    /// <see cref="Columns"/> x <see cref="RowsPerLegend"/>; set = one sheet per legend, each legend
    /// holding what fits inside the title block's frame.</summary>
    public string TitleBlockName { get; set; } = "";
    /// <summary>Kept clear inside the title block's frame, paper mm.</summary>
    public double SheetMarginMm { get; set; } = 5;
    public string SheetNumber { get; set; } = "A-12";
    public string SheetName { get; set; } = "แบบขยายประตู-หน้าต่าง";
    /// <summary>Sheet parameters written on the set's sheets at every build: those the sheet browser
    /// organization groups by (so the sheets land in the right folder), and any others picked.</summary>
    public List<DwSheetParameter> SheetParameters { get; set; } = new();

    /// <summary>Before 2026-10-21 one preset scheduled doors and windows together; read only from
    /// old files, by <see cref="Migrate"/>, and never written again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Doors { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Windows { get; set; }

    /// <summary>An old doors-and-windows preset becomes a door set (a window one if it had only
    /// windows ticked); the window set is a second preset the user saves.</summary>
    public void Migrate()
    {
        if (Doors == false && Windows == true) Category = DwCategory.Window;
        Doors = Windows = null;
        // Families v2 (2026-10-22) draw table text at 2 mm; presets saved before count it at 3.
        if (Math.Abs(RowTextMm - 3) < 1e-9) RowTextMm = 2;
    }
}

/// <summary>One sheet parameter the set writes on its sheets, by name.</summary>
public sealed class DwSheetParameter
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A sheet parameter the Sheets tab offers: its name, whether the sheet browser
/// organization groups by it, and the values the model's sheets use.</summary>
public sealed class DwSheetParameterChoice
{
    public string Name { get; set; } = "";
    public bool Organization { get; set; }
    public List<string> Values { get; set; } = new();
}

public static class DoorWindowStores
{
    public static readonly DrwPresetStore<DoorWindowPreset> Store = new("DoorWindowPresets");
}
