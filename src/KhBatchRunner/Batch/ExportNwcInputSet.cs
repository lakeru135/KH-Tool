using System.Text.Json.Serialization;

namespace KhBatchRunner.Batch;

/// <summary>Which parameters travel into the NWC — Revit models this as one three-valued setting
/// (<c>NavisworksParameters</c>), so it is one here too.</summary>
public enum NwcParameterMode
{
    /// <summary>Everything, instance and type. Revit's own default.</summary>
    All,
    /// <summary>Element parameters only.</summary>
    Elements,
    None,
}

/// <summary>
/// The Export-NWC dialog's inputs as a plain, JSON-round-trippable record — savable as a named set
/// (<see cref="ExportNwcSetStore"/>), auto-restored as last-used, and snapshotted onto a Batch Run /
/// Schedule job so the export can run unattended. Same shape as <see cref="CutModelInputSet"/> and
/// for the same reason: an action that needs a per-run target cannot be constructed by
/// <see cref="Actions.ActionRegistry"/>, which builds actions with no arguments, so the target has
/// to travel with the job.
///
/// <para><b>The option set matches Bird Tools' NWC panel</b>, which is what the team already uses
/// and reads. Every one maps to a real <c>NavisworksExportOptions</c> property, and the member list
/// is identical on Revit 2024 through 2026, so nothing here needs a version guard. Defaults are
/// Revit's own, so a set nobody has touched behaves like a plain manual export.</para>
/// </summary>
public sealed class ExportNwcInputSet
{
    /// <summary>Set name (file key in <see cref="ExportNwcSetStore"/>); empty for a last-used snapshot.</summary>
    public string Name { get; set; } = "";

    // ---- what to export ------------------------------------------------------

    /// <summary>
    /// Export the whole model rather than views. When false, every 3D view whose title contains
    /// <see cref="ViewTitleContains"/> is exported — see that property.
    /// </summary>
    public bool ExportEntireModel { get; set; } = true;

    /// <summary>
    /// Case-sensitive substring matched against 3D view TITLES. **Blank means every 3D view**, which
    /// is Bird's behaviour and is deliberate: it is the difference between "export the coordination
    /// views" and "export the one called exactly this".
    ///
    /// <para>Matching many views means a model can produce SEVERAL <c>.nwc</c> files — one per view —
    /// which is why the action reports one log row per file rather than one per model.</para>
    /// </summary>
    public string ViewTitleContains { get; set; } = "";

    /// <summary>
    /// Where the .nwc files go. Empty means the run's own Destination Folder, which is what most
    /// runs want and saves entering the same path twice.
    /// </summary>
    public string OutputFolder { get; set; } = "";

    // ---- naming convention ---------------------------------------------------

    public string NamePrefix { get; set; } = "";
    public string NameSuffix { get; set; } = "";

    /// <summary>Include the model name in the file name. On by default — with it off and views off
    /// too, every model would fight for one file name.</summary>
    public bool NameIncludesModel { get; set; } = true;

    /// <summary>Include the exported view's name. Ignored for a whole-model export, which has no
    /// view to name.</summary>
    public bool NameIncludesView { get; set; }

    /// <summary>What joins the name parts. Bird shows "-" between each.</summary>
    public string NameSeparator { get; set; } = "-";

    // ---- Navisworks export options (Bird's grid, left to right) ---------------

    public bool ConvertElementProperties { get; set; } = true;
    public bool ExportLinks { get; set; }
    public bool ExportRoomAsAttribute { get; set; } = true;
    public bool FindMissingMaterials { get; set; } = true;

    public bool DivideFileIntoLevels { get; set; } = true;
    public bool ExportParts { get; set; } = true;
    public bool ExportRoomGeometry { get; set; }

    public bool ExportElementIds { get; set; } = true;
    public bool ExportUrls { get; set; } = true;

    /// <summary>Bird's "Export Internal Coordinates (Uncheck For Shared Coordinates)" — false here
    /// means shared coordinates, which is what a federated model almost always wants.</summary>
    public bool UseInternalCoordinates { get; set; }

    public bool ConvertLinkedCADFormats { get; set; }
    public bool ConvertLights { get; set; }

    /// <summary>Curved-surface tessellation quality; Revit's default is 1.</summary>
    public double FacetingFactor { get; set; } = 1.0;

    /// <summary>
    /// Bird exposes this as two checkboxes ("Export All Parameters" / "Export Element Parameters"),
    /// which can be ticked to contradict each other because the API has only one setting behind
    /// them. Modelled here as the one three-valued choice the API actually has.
    /// </summary>
    public NwcParameterMode Parameters { get; set; } = NwcParameterMode.All;

    // ---- run-log fields (standalone Export NWC only) -------------------------

    public string DestinationFolder { get; set; } = "";
    public string ProjectLogName { get; set; } = "Export NWC";

    /// <summary>True once somewhere to write to is known — either here or from the run.</summary>
    [JsonIgnore]
    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputFolder) ||
                             !string.IsNullOrWhiteSpace(DestinationFolder);

    /// <summary>What this set exports, in a few words — the dropdowns in Batch Run and Schedule Run
    /// both show it, so "all 3D views" (a blank filter) can never be mistaken for "none".</summary>
    [JsonIgnore]
    public string ScopeLabel => ExportEntireModel
        ? "whole model"
        : string.IsNullOrWhiteSpace(ViewTitleContains)
            ? "all 3D views"
            : $"3D views containing '{ViewTitleContains}'";

    /// <summary>Where the files land, in a few words, for the same dropdowns.</summary>
    [JsonIgnore]
    public string OutputLabel =>
        string.IsNullOrWhiteSpace(OutputFolder) ? "run destination" : OutputFolder;

    /// <summary>
    /// Where this run's files actually go: the set's own folder when it has one, else the run's
    /// destination. Resolved here rather than at each call site so the batch, the schedule and the
    /// standalone command cannot disagree about it.
    /// </summary>
    public string ResolveOutputFolder(string runDestinationFolder) =>
        !string.IsNullOrWhiteSpace(OutputFolder) ? OutputFolder : runDestinationFolder;

    /// <summary>
    /// The file name (no extension) for one export, per the naming convention.
    /// <paramref name="viewName"/> is empty for a whole-model export.
    ///
    /// Empty parts are dropped rather than leaving a dangling separator, and if every part is empty
    /// the model name is used anyway — a set of files called "-.nwc" helps nobody.
    /// </summary>
    /// <param name="forceView">
    /// Include the view name whatever <see cref="NameIncludesView"/> says. Passed by the action when
    /// MORE THAN ONE view matched for a model: without it every one of them builds the same name and
    /// each export overwrites the last. MEASURED 2026-08-15 — three views matched in SITE2A-BLDA-ARCH,
    /// three exports ran, and one file existed at the end of it.
    /// </param>
    public string BuildFileName(string modelName, string viewName, bool forceView = false)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(NamePrefix)) parts.Add(NamePrefix.Trim());
        if (NameIncludesModel && !string.IsNullOrWhiteSpace(modelName)) parts.Add(modelName);
        if ((NameIncludesView || forceView) && !string.IsNullOrWhiteSpace(viewName)) parts.Add(viewName);
        if (!string.IsNullOrWhiteSpace(NameSuffix)) parts.Add(NameSuffix.Trim());

        string sep = string.IsNullOrEmpty(NameSeparator) ? "-" : NameSeparator;
        string name = string.Join(sep, parts);
        return string.IsNullOrWhiteSpace(name) ? modelName : name;
    }

    /// <summary>Deep copy — used when a job carrying this snapshot is duplicated for a split run.
    /// A chunk child that arrives without it cannot export anything, so every new field added above
    /// must be copied here too.</summary>
    public ExportNwcInputSet Clone() => new()
    {
        Name = Name,
        ExportEntireModel = ExportEntireModel,
        ViewTitleContains = ViewTitleContains,
        OutputFolder = OutputFolder,
        NamePrefix = NamePrefix,
        NameSuffix = NameSuffix,
        NameIncludesModel = NameIncludesModel,
        NameIncludesView = NameIncludesView,
        NameSeparator = NameSeparator,
        ConvertElementProperties = ConvertElementProperties,
        ExportLinks = ExportLinks,
        ExportRoomAsAttribute = ExportRoomAsAttribute,
        FindMissingMaterials = FindMissingMaterials,
        DivideFileIntoLevels = DivideFileIntoLevels,
        ExportParts = ExportParts,
        ExportRoomGeometry = ExportRoomGeometry,
        ExportElementIds = ExportElementIds,
        ExportUrls = ExportUrls,
        UseInternalCoordinates = UseInternalCoordinates,
        ConvertLinkedCADFormats = ConvertLinkedCADFormats,
        ConvertLights = ConvertLights,
        FacetingFactor = FacetingFactor,
        Parameters = Parameters,
        DestinationFolder = DestinationFolder,
        ProjectLogName = ProjectLogName
    };
}
