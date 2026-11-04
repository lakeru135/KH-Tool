using System.Text.Json.Serialization;
using KhBatchRunner.Forma;

namespace KhBatchRunner.Batch;

/// <summary>
/// The Internal Relink dialog's inputs as a plain, JSON-round-trippable record — savable as a named
/// set (<see cref="RelinkSetStore"/>), auto-restored as last-used. Unlike Initiate/Cut model (one
/// publish target), relink's targets are a SET of Forma folders to scan for the new-prefix models
/// that each host link is re-pointed to; the folders are snapshotted here (as
/// <see cref="FolderRef"/>) so a saved set is self-contained. One <see cref="OldCode"/> →
/// <see cref="NewCode"/> pair drives the leading-token remap of every host link name (e.g.
/// "OLD-ST-BLDA" → "SITE1A-ST-BLDA", matching the Cut model site-code convention).
/// </summary>
public sealed class RelinkInputSet
{
    /// <summary>Set name (file key in <see cref="RelinkSetStore"/>); empty for a last-used snapshot.</summary>
    public string Name { get; set; } = "";

    /// <summary>The HOST model set selected in the dialog (used when <see cref="HostsFromFolder"/>
    /// is false).</summary>
    public string ModelSetName { get; set; } = "";

    /// <summary>True to take the HOSTS from <see cref="HostFolder"/> (every model found in that Forma
    /// folder is relinked) instead of from the saved model set — no set maintenance needed when the
    /// hosts are simply "everything the Cut just published into this folder".</summary>
    public bool HostsFromFolder { get; set; }

    /// <summary>The Forma folder scanned for HOST models when <see cref="HostsFromFolder"/> is true.
    /// Distinct from <see cref="TargetFolders"/> (the folders scanned for link TARGETS).</summary>
    public FolderRef? HostFolder { get; set; }

    /// <summary>Recurse into subfolders when scanning <see cref="HostFolder"/> for hosts.</summary>
    public bool HostIncludeSubfolders { get; set; }

    /// <summary>Leading token of a host link's name that identifies an OLD-prefix model, e.g. "OLD".</summary>
    public string OldCode { get; set; } = "";

    /// <summary>Replacement leading token naming the NEW-prefix target model, e.g. "SITE1A".</summary>
    public string NewCode { get; set; } = "";

    /// <summary>Token boundary between the code and the rest of the name — one of "-", "_", ".".</summary>
    public string Separator { get; set; } = "-";

    /// <summary>Also relink a link whose leading token is NOT the old code when a target model with
    /// the EXACT same name exists in the scanned folders — the incoming-Bridge / shared-model case
    /// (their names carry no site code, so the old→new remap can never apply). Default on; sets
    /// saved before this property existed deserialize to on.</summary>
    public bool MatchByExactName { get; set; } = true;

    /// <summary>Forma folders scanned for the new-prefix target models (self-contained snapshot).</summary>
    public List<FolderRef> TargetFolders { get; set; } = new();

    /// <summary>Recurse into subfolders when scanning the target folders.</summary>
    public bool IncludeSubfolders { get; set; } = true;

    // Run-log fields.
    public string DestinationFolder { get; set; } = "";
    public string ProjectLogName { get; set; } = "Internal Relink";

    /// <summary>True once at least one target folder has been chosen.</summary>
    [JsonIgnore]
    public bool HasTargets => TargetFolders.Count > 0;

    /// <summary>Deep copy — used when a saved set is loaded/duplicated.</summary>
    public RelinkInputSet Clone() => new()
    {
        Name = Name,
        ModelSetName = ModelSetName,
        HostsFromFolder = HostsFromFolder,
        HostFolder = HostFolder is null ? null : new FolderRef
        {
            Name = HostFolder.Name,
            DisplayPath = HostFolder.DisplayPath,
            HubId = HostFolder.HubId,
            ProjectId = HostFolder.ProjectId,
            FolderUrn = HostFolder.FolderUrn,
            Region = HostFolder.Region
        },
        HostIncludeSubfolders = HostIncludeSubfolders,
        OldCode = OldCode,
        NewCode = NewCode,
        Separator = Separator,
        MatchByExactName = MatchByExactName,
        TargetFolders = TargetFolders.Select(f => new FolderRef
        {
            Name = f.Name,
            DisplayPath = f.DisplayPath,
            HubId = f.HubId,
            ProjectId = f.ProjectId,
            FolderUrn = f.FolderUrn,
            Region = f.Region
        }).ToList(),
        IncludeSubfolders = IncludeSubfolders,
        DestinationFolder = DestinationFolder,
        ProjectLogName = ProjectLogName
    };

    // Bound directly in the saved-sets combo (same convention as FolderSet/FolderRef).
    public override string ToString() => Name;
}
