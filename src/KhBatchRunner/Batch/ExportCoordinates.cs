namespace KhBatchRunner.Batch;

/// <summary>
/// Which coordinate frame an exported NWC or IFC is written in. Shared by Export NWC and Export IFC
/// + Data so the two dialogs offer the same three choices with the same words.
///
/// <para>Our own enum, not a Revit one: this file is compiled into the controller too, where the
/// Revit API is not available.</para>
///
/// <para><b>Why there is a third choice.</b> Revit's exporters know two frames: the active site's
/// shared coordinates (survey, often UTM with a rotation) and the internal origin. Some federated
/// models use neither — they use a local grid whose origin is the Project Base Point, with project
/// north up. Neither exporter can write that frame directly, so
/// <see cref="ProjectBasePoint"/> builds it; see <c>Export.ProjectBasePointFrame</c> for how, and
/// the handbook's "Coordinates in Export NWC and Export IFC + Data" for the user-facing version.</para>
/// </summary>
public enum ExportCoordinates
{
    /// <summary>The active site's shared coordinates — what a survey-based federation wants.</summary>
    Shared,

    /// <summary>Revit's internal origin and axes.</summary>
    Internal,

    /// <summary>
    /// The Project Base Point becomes 0,0,0, project north points up, and Z 0 is the PBP's
    /// elevation. NWC: written through a temporary site that is created, used and deleted. IFC:
    /// written with the PBP as the coordinate base, then the base point Revit still offsets to the
    /// survey is set to 0 in the file.
    /// </summary>
    ProjectBasePoint,
}

/// <summary>The dialog wording for each choice, in one place so both dialogs and the run-row text
/// agree.</summary>
public static class ExportCoordinatesText
{
    public static string Label(ExportCoordinates c) => c switch
    {
        ExportCoordinates.Internal => "Internal origin",
        ExportCoordinates.ProjectBasePoint => "Project Base Point at 0,0,0",
        _ => "Shared coordinates",
    };

    /// <summary>The one-paragraph explanation shown under the dropdown.</summary>
    public static string Explanation(ExportCoordinates c, bool isNwc) => c switch
    {
        ExportCoordinates.Internal =>
            "Revit's internal origin and axes. Use when every model was built around Revit's origin.",
        ExportCoordinates.ProjectBasePoint => isNwc
            ? "The Project Base Point becomes 0,0,0, project north points up, Z 0 = PBP elevation. " +
              "Use when the federation uses a local grid with the PBP as its origin. Revit's NWC " +
              "exporter cannot do this by itself, so a temporary site (KH_TEMP_PBP_ORIGIN) is made " +
              "active, the file is exported, then your own site is made active again and the " +
              "temporary one deleted — the model ends as it was. Every model must have its PBP at " +
              "the same real-world point, or the files will not line up."
            : "The Project Base Point becomes 0,0,0, project north points up, Z 0 = PBP elevation. " +
              "Use when the federation uses a local grid with the PBP as its origin. The IFC is " +
              "exported with the PBP as its coordinate base, then the survey offset Revit still " +
              "writes into the file is set to 0. The model is not changed. Every model must have " +
              "its PBP at the same real-world point, or the files will not line up.",
        _ =>
            "The active site's shared coordinates (survey). Use for most federated models.",
    };
}
