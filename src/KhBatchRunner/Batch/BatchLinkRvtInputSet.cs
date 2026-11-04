using System.Text.Json.Serialization;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Batch;

/// <summary>Where a new link instance is placed in the host. Mirrors the subset of
/// <c>Autodesk.Revit.DB.ImportPlacement</c> that the Revit UI's "Positioning" list offers, and is
/// declared here rather than reusing the Revit enum so this settings record stays Revit-free —
/// the same reason <see cref="ActionCategory"/> lives outside the Revit-typed action files.
///
/// <para><b>There is no Project Base Point option.</b> The Revit UI offers "Auto - Project Base Point
/// to Project Base Point"; <c>ImportPlacement</c> does not — its only members are <c>Origin</c>,
/// <c>Shared</c>, <c>Site</c> and <c>Centered</c> (checked against the installed <c>RevitAPI.xml</c>
/// for 2024 and 2026). Offering it would produce a dropdown entry with nothing behind it.</para>
/// </summary>
public enum LinkPlacement
{
    /// <summary>Auto - Origin to Origin.</summary>
    Origin,

    /// <summary>Auto - By Shared Coordinates. The usual coordinated-BIM default, and the one that can
    /// FAIL: it throws when the host has no shared site matching the link's, so it is attempted per
    /// instance and degrades to a reported row rather than losing the link.</summary>
    Shared,

    /// <summary>Auto - Center to Center.</summary>
    Centered,
}

/// <summary>
/// The Batch Link RVT dialog's inputs as a plain, JSON-round-trippable record — savable as a named
/// set (<see cref="BatchLinkRvtSetStore"/>), auto-restored as last-used. A sibling of
/// <see cref="RelinkInputSet"/>, and deliberately the same shape: Internal Relink RE-POINTS the links
/// a host already has, this one CREATES the links a host does not have yet.
///
/// <para>The sources are snapshotted as <see cref="CloudModel"/> — already the codebase's universal
/// persisted model identity, and already able to describe a cloud model
/// (<see cref="CloudModel.Region"/>/<see cref="CloudModel.ProjectGuid"/>/<see cref="CloudModel.ModelGuid"/>)
/// or a local file (<see cref="CloudModel.LocalPath"/>) without a second DTO.</para>
/// </summary>
public sealed class BatchLinkRvtInputSet
{
    /// <summary>Set name (file key in <see cref="BatchLinkRvtSetStore"/>); empty for a last-used snapshot.</summary>
    public string Name { get; set; } = "";

    /// <summary>The HOST model set selected in the dialog — the models the links are placed INTO.</summary>
    public string ModelSetName { get; set; } = "";

    /// <summary>The models to link into every host. Cloud or local; see the class remarks.</summary>
    public List<CloudModel> Sources { get; set; } = new();

    // --- where the links land -----------------------------------------------------------------

    /// <summary>
    /// False = one SHARED workset named <see cref="SharedWorksetName"/> carries every link.
    /// True = one workset PER link, named <c>&lt;<see cref="LinkKeyword"/>&gt;_&lt;link name&gt;</c> —
    /// the convention <c>CorrectWorksetAction</c> enforces, via the shared
    /// <c>Actions.LinkWorksetNaming.Expected</c> so the two can never drift.
    /// </summary>
    public bool PerLinkWorkset { get; set; }

    /// <summary>The single workset every link goes on when <see cref="PerLinkWorkset"/> is false.</summary>
    public string SharedWorksetName { get; set; } = "Z-LINK";

    /// <summary>Prefix for the per-link workset names. Default "LINK" — the same default as
    /// <see cref="CorrectWorksetInputSet.LinkKeyword"/>, so the out-of-the-box settings of the two
    /// tools agree without the user having to notice that they must.</summary>
    public string LinkKeyword { get; set; } = "LINK";

    // --- how the links are made ---------------------------------------------------------------

    public LinkPlacement Placement { get; set; } = LinkPlacement.Shared;

    /// <summary>True = Attachment, false = Overlay. Overlay is the default: an attachment is carried
    /// into any model that links the HOST, which turns one batch into a federation nobody asked for.</summary>
    public bool Attachment { get; set; }

    /// <summary>Pin each new link instance so it cannot be dragged out of position.</summary>
    public bool PinInstance { get; set; } = true;

    /// <summary>Skip a source the host already links, instead of creating a second link to it.
    /// Type-level only — see <c>BatchLinkRvtAction</c>'s workset-posture reason for why it cannot
    /// also mean "and has a placed instance".</summary>
    public bool SkipIfAlreadyLinked { get; set; } = true;

    // Run-log fields.
    public string DestinationFolder { get; set; } = "";
    public string ProjectLogName { get; set; } = "Batch Link RVT";

    /// <summary>True once at least one source model has been chosen.</summary>
    [JsonIgnore]
    public bool HasSources => Sources.Count > 0;

    /// <summary>Deep copy — used when a saved set is loaded/duplicated.</summary>
    public BatchLinkRvtInputSet Clone() => new()
    {
        Name = Name,
        ModelSetName = ModelSetName,
        Sources = Sources.Select(CloneModel).ToList(),
        PerLinkWorkset = PerLinkWorkset,
        SharedWorksetName = SharedWorksetName,
        LinkKeyword = LinkKeyword,
        Placement = Placement,
        Attachment = Attachment,
        PinInstance = PinInstance,
        SkipIfAlreadyLinked = SkipIfAlreadyLinked,
        DestinationFolder = DestinationFolder,
        ProjectLogName = ProjectLogName
    };

    private static CloudModel CloneModel(CloudModel m) => new()
    {
        DisplayName = m.DisplayName,
        Region = m.Region,
        ProjectGuid = m.ProjectGuid,
        ModelGuid = m.ModelGuid,
        ItemUrn = m.ItemUrn,
        DmProjectId = m.DmProjectId,
        LocalPath = m.LocalPath
    };

    // Bound directly in the saved-sets combo (same convention as RelinkInputSet/FolderSet).
    public override string ToString() => Name;
}
