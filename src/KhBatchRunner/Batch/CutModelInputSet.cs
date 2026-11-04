namespace KhBatchRunner.Batch;

/// <summary>
/// The Cut-model dialog's inputs — an <see cref="InitiateInputSet"/> whose <see cref="InitiateInputSet.Prefix"/>
/// carries the SITE CODE (one input drives both the site-location match and the publish name
/// prefix; <see cref="InitiateInputSet.PrefixMode"/> is always <see cref="PublishPrefixMode.Rename"/>
/// so "XXX-AR" + "BETA2A" → "BETA2A-AR", mirroring the location name), plus whether to delete the
/// non-matching site locations from the temp copy. Saved/loaded by <see cref="CutModelSetStore"/>.
/// </summary>
public sealed class CutModelInputSet : InitiateInputSet
{
    public CutModelInputSet()
    {
        PrefixMode = PublishPrefixMode.Rename;
        ProjectLogName = "Cut model";
    }

    /// <summary>Delete every site/project location that did NOT match the site code from the temp copy.</summary>
    public bool DeleteOtherLocations { get; set; }

    /// <summary>Deep copy — hides <see cref="InitiateInputSet.Clone"/> so a Cut model snapshot keeps
    /// its own type and the <see cref="DeleteOtherLocations"/> flag (used when a job is duplicated).</summary>
    public new CutModelInputSet Clone() => new()
    {
        Name = Name,
        ModelSetName = ModelSetName,
        HubId = HubId,
        ProjectId = ProjectId,
        FolderUrn = FolderUrn,
        FolderPath = FolderPath,
        Separator = Separator,
        PrefixMode = PrefixMode,
        Prefix = Prefix,
        Suffix = Suffix,
        DestinationFolder = DestinationFolder,
        ProjectLogName = ProjectLogName,
        DeleteOtherLocations = DeleteOtherLocations
    };
}
