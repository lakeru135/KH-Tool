using System.Text.Json.Serialization;

namespace KhBatchRunner.Batch;

/// <summary>IFC schema/view definition to write. Our own enum, not Revit's <c>IFCVersion</c>: this file
/// is compiled into the controller too, where the Revit API is not available.</summary>
public enum IfcSchemaChoice
{
    /// <summary>IFC4 Reference View — geometry as triangulated/simple solids, the view definition
    /// meant for viewing and coordination. Smallest files, and what the report viewer reads best.</summary>
    Ifc4ReferenceView,
    /// <summary>IFC2x3 Coordination View 2.0 — the long-standing default most other tools accept.</summary>
    Ifc2x3CoordinationView2,
    /// <summary>IFC4 Design Transfer View — parametric geometry; larger, for handing the model on.</summary>
    Ifc4DesignTransferView,
}

/// <summary>
/// The "Export IFC + Data" inputs as a plain, JSON-round-trippable record — savable as a named set
/// (<see cref="ExportIfcSetStore"/>), restored as last-used, and snapshotted onto a Batch Run /
/// Schedule job so the export can run unattended. Same shape and reasons as
/// <see cref="ExportNwcInputSet"/>: an action that carries its own configuration cannot be built by
/// <see cref="Actions.ActionRegistry"/> and has to hand that configuration to the job.
///
/// <para><b>What one model produces</b>, under <c>&lt;output&gt;\runs\&lt;run stamp&gt;\&lt;model&gt;\</c>:
/// the <c>.ifc</c>, <c>elements.csv</c> (one row per model element in the export view) and
/// <c>rooms.csv</c> (every room, with its area — the GFA source). When <see cref="ConvertToFrag"/> is
/// on, the bundled converter adds a <c>.frag</c> and <c>idmap.csv</c> after the run, and refreshes
/// <c>&lt;output&gt;\latest\</c>, which is what the BIM report reads.</para>
/// </summary>
public sealed class ExportIfcInputSet
{
    /// <summary>Set name (file key in <see cref="ExportIfcSetStore"/>); empty for a last-used snapshot.</summary>
    public string Name { get; set; } = "";

    // ---- what to export ------------------------------------------------------

    /// <summary>
    /// Case-sensitive substring of the ONE 3D view to export; the first match by name is used.
    /// Blank exports the whole model. One view, not several like Export NWC: the report shows one
    /// model per discipline, and a second file per model would be a second model to it.
    /// </summary>
    public string ViewTitleContains { get; set; } = "";

    /// <summary>Root folder for runs\, latest\ and history\. Empty means the run's Destination Folder.</summary>
    public string OutputFolder { get; set; } = "";

    // ---- IFC options ------------------------------------------------------------

    public IfcSchemaChoice Schema { get; set; } = IfcSchemaChoice.Ifc4ReferenceView;

    /// <summary>Place the model by shared coordinates (true) or Revit's internal origin (false).</summary>
    public bool UseSharedCoordinates { get; set; } = true;

    public bool ExportCommonPropertySets { get; set; } = true;
    public bool ExportRevitPropertySets { get; set; }
    public bool ExportBaseQuantities { get; set; }

    // ---- data files ---------------------------------------------------------------

    public bool WriteElementsCsv { get; set; } = true;
    public bool WriteRoomsCsv { get; set; } = true;

    /// <summary>Extra parameter names added as columns to elements.csv and rooms.csv, read from the
    /// instance first and then its type. Blank cells where an element has no such parameter.</summary>
    public List<string> ExtraParameters { get; set; } = new();

    // ---- report output ------------------------------------------------------------

    /// <summary>Run the bundled IFC-to-FRAG converter when the run ends (after Revit closes, on a
    /// scheduled run) and refresh the latest\ folder the report reads.</summary>
    public bool ConvertToFrag { get; set; } = true;

    /// <summary>runs\ folders older than this many days are deleted by the converter. 0 keeps all.</summary>
    public int KeepRunsDays { get; set; } = 14;

    // ---- run-log fields (standalone command only) --------------------------------

    public string DestinationFolder { get; set; } = "";
    public string ProjectLogName { get; set; } = "Export IFC + Data";

    [JsonIgnore]
    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputFolder) ||
                             !string.IsNullOrWhiteSpace(DestinationFolder);

    /// <summary>What this set exports, in a few words, for the Batch/Schedule dropdowns.</summary>
    [JsonIgnore]
    public string ScopeLabel => string.IsNullOrWhiteSpace(ViewTitleContains)
        ? "whole model"
        : $"3D view containing '{ViewTitleContains}'";

    [JsonIgnore]
    public string OutputLabel => string.IsNullOrWhiteSpace(OutputFolder) ? "run destination" : OutputFolder;

    /// <summary>The set's own folder when it has one, else the run's destination.</summary>
    public string ResolveOutputFolder(string runDestinationFolder) =>
        !string.IsNullOrWhiteSpace(OutputFolder) ? OutputFolder : runDestinationFolder;

    /// <summary>Deep copy for split runs. Every new field above must be copied here too, or a chunk
    /// child arrives without it.</summary>
    public ExportIfcInputSet Clone() => new()
    {
        Name = Name,
        ViewTitleContains = ViewTitleContains,
        OutputFolder = OutputFolder,
        Schema = Schema,
        UseSharedCoordinates = UseSharedCoordinates,
        ExportCommonPropertySets = ExportCommonPropertySets,
        ExportRevitPropertySets = ExportRevitPropertySets,
        ExportBaseQuantities = ExportBaseQuantities,
        WriteElementsCsv = WriteElementsCsv,
        WriteRoomsCsv = WriteRoomsCsv,
        ExtraParameters = new List<string>(ExtraParameters ?? new List<string>()),
        ConvertToFrag = ConvertToFrag,
        KeepRunsDays = KeepRunsDays,
        DestinationFolder = DestinationFolder,
        ProjectLogName = ProjectLogName
    };
}
