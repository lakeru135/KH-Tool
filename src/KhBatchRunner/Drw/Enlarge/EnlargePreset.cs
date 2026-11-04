using KhBatchRunner.Drw.Common;

namespace KhBatchRunner.Drw.Enlarge;

public enum EnlargeViewKind { Section, Elevation }

public enum ElevationMode
{
    /// <summary>One elevation per boundary edge at least <see cref="EnlargePreset.MinBoundaryLengthM"/> long.</summary>
    ByBoundary,
    /// <summary>One marker at the centre, with an elevation for each ticked side.</summary>
    ByCenter4
}

public enum RoomLabel { Name, Number, NumberName }

/// <summary>What a section's top crop is measured from.</summary>
public enum SectionTopFrom
{
    /// <summary>The top of the room, or a stair's last step.</summary>
    TargetTop,
    /// <summary>The first level above the room's level / the stair's top level (e.g. Roof).</summary>
    NextLevel
}

/// <summary>Where a section's line stands, before it looks across the room/stair.</summary>
public enum SectionPlacement
{
    /// <summary><see cref="EnlargePreset.SectionOffsetM"/> inside the edge behind the viewer.</summary>
    FromEdge,
    /// <summary>Through the centre, moved back by <see cref="EnlargePreset.SectionCentreOffsetM"/>.</summary>
    FromCentre
}

public sealed class ParamValue
{
    public string Name { get; set; } = "";
    /// <summary>
    /// The shared parameter's GUID when the row was picked from the dropdown, so a name two
    /// parameters share still writes to the one chosen. Blank = a project parameter, or a preset
    /// saved before this field existed (then the name must be unambiguous on the view).
    /// </summary>
    public string Guid { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// Everything Enlarge Room / Enlarge Stair needs to know, saved as a named preset. Lengths are in
/// metres, as the dialog shows them. Every value that follows one office's drawing standard is
/// here rather than in code, so another standard is another preset.
/// </summary>
public sealed class EnlargePreset : IDrwPreset, IDrwMigrating
{
    public string Name { get; set; } = "Default";

    // --- enlarged plan ---
    public bool CreatePlan { get; set; } = true;
    public string PlanTypeName { get; set; } = "";
    public double PlanMarginM { get; set; } = 0.5;
    public int Scale { get; set; } = 50;

    // --- sections / elevations ---
    public bool CreateVerticalViews { get; set; } = true;
    public EnlargeViewKind ViewKind { get; set; } = EnlargeViewKind.Section;

    /// <summary>Which way the views look. North = up the plan (+Y), or along the longest edge when oriented.</summary>
    public bool North { get; set; } = true;
    public bool East { get; set; } = true;
    public bool South { get; set; }
    public bool West { get; set; }

    /// <summary>Turn the N/E/S/W frame to the target's longest boundary edge (rotated rooms).</summary>
    public bool OrientToLongestEdge { get; set; }

    public string SectionTypeName { get; set; } = "";
    public SectionPlacement SectionPlacement { get; set; } = SectionPlacement.FromEdge;
    /// <summary>
    /// From the edge: how far inside the outline the section line stands, measured from the side
    /// behind the viewer. Before 0.2.2 this pushed the box OUT past the outline instead; the JSON
    /// name is kept so saved presets load, and the value now means "inside".
    /// </summary>
    public double SectionOffsetM { get; set; } = 0.5;
    /// <summary>From the centre: how far the line moves back from the centre (negative = forward).</summary>
    public double SectionCentreOffsetM { get; set; }
    /// <summary>How far the section's crop reaches past the side walls, and its far clip past the far
    /// wall. (Before 0.2.4 it also padded the bottom and top; those have their own values now.)</summary>
    public double SectionCropOffsetM { get; set; } = 0.3;
    /// <summary>How far the section's crop reaches below the room's floor / the stair's first step.</summary>
    public double SectionBelowM { get; set; } = 0.3;
    /// <summary>How far the section's crop reaches above <see cref="SectionTopFrom"/>.</summary>
    public double SectionAboveM { get; set; } = 0.3;
    public SectionTopFrom SectionTopFrom { get; set; } = SectionTopFrom.TargetTop;

    // --- stairs only: the boundary past the first and last step (0 = the stair itself) ---
    public double StairBeforeFirstStepM { get; set; }
    public double StairAfterLastStepM { get; set; }

    public string ElevationTypeName { get; set; } = "";
    public ElevationMode ElevationMode { get; set; } = ElevationMode.ByBoundary;
    public double MinBoundaryLengthM { get; set; } = 1.0;
    public bool IncludeInnerLoops { get; set; }
    /// <summary>How far inside the boundary a By-boundary marker stands.</summary>
    public double ElevationInsetM { get; set; } = 0.5;
    /// <summary>
    /// Before 0.2.7: an elevation's single crop offset (sides, bottom, top and far clip). Elevations
    /// now take the section's crop and height (<see cref="SectionCropOffsetM"/>,
    /// <see cref="SectionBelowM"/>, <see cref="SectionAboveM"/>, <see cref="SectionTopFrom"/>). Read
    /// only from old files, by <see cref="Migrate"/>, and never written again.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? ElevationOffsetM { get; set; }

    // --- naming ---
    public string Prefix { get; set; } = "Enlarge";
    public string PlanPrefix { get; set; } = "Plan";
    public string SectionPrefix { get; set; } = "Section";
    public string ElevationPrefix { get; set; } = "Elevation";
    public RoomLabel RoomLabel { get; set; } = RoomLabel.Name;
    /// <summary>Between a multistory stair's mark and its members' numbers: ST-1-1, ST-1.1, ST-1_1.</summary>
    public string MarkDelimiter { get; set; } = "-";
    public bool OverrideMultistoryMarks { get; set; } = true;

    // --- templates ---
    public string PlanTemplate { get; set; } = "";
    public string SectionTemplate { get; set; } = "";
    public string ElevationTemplate { get; set; } = "";

    // --- external parameters written to every view created ---
    public List<ParamValue> Parameters { get; set; } = new();

    /// <summary>Leave a view alone when one with its exact name already exists (safe re-runs).</summary>
    public bool SkipExisting { get; set; } = true;

    /// <summary>Batch only: targets whose name contains this. Blank = all.</summary>
    public string BatchFilter { get; set; } = "";

    /// <summary>
    /// A preset saved before 0.2.7 as Elevation used <see cref="ElevationOffsetM"/> for every side of
    /// the crop. Moving it into the section values (with the top measured from the room, as it was)
    /// makes the same views come out; for a Section preset the old value was unused and is dropped.
    /// </summary>
    public void Migrate()
    {
        if (ElevationOffsetM is not double old) return;
        if (ViewKind == EnlargeViewKind.Elevation)
        {
            SectionCropOffsetM = old;
            SectionBelowM = old;
            SectionAboveM = old;
            SectionTopFrom = SectionTopFrom.TargetTop;
        }
        ElevationOffsetM = null;
    }
}

public static class EnlargeStores
{
    public static readonly DrwPresetStore<EnlargePreset> Room = new("EnlargeRoomPresets");
    public static readonly DrwPresetStore<EnlargePreset> Stair = new("EnlargeStairPresets");
}
