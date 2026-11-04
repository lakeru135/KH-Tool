using System.Text.Json.Serialization;

namespace KhBatchRunner.Batch;

/// <summary>
/// The Initiate-to-Forma dialog's inputs as a plain, JSON-round-trippable record — savable as a
/// named set (<see cref="InitiateSetStore"/>), auto-restored as last-used, and snapshotted onto
/// a Batch Run / Schedule job so Initiate can run unattended. Stores the picked folder's RAW
/// identifiers (hub/project ids as crawled, folder URN) rather than a resolved
/// <see cref="PublishTarget"/> — that type has required/init members and derived GUIDs, so the
/// GUID parsing happens on use, in <see cref="ToPublishTarget"/>.
/// </summary>
public class InitiateInputSet
{
    /// <summary>Set name (file key in <see cref="InitiateSetStore"/>); empty for a last-used/ad-hoc snapshot.</summary>
    public string Name { get; set; } = "";

    /// <summary>The model set selected in the dialog — informational for standalone Initiate;
    /// ignored when the set seeds a Batch Run / Schedule job (the job's own models win).</summary>
    public string ModelSetName { get; set; } = "";

    // Target Forma folder identity, exactly as crawled (hub id keeps its "b." prefix).
    public string HubId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string FolderUrn { get; set; } = "";
    public string FolderPath { get; set; } = "";

    // Naming rule.
    public string Separator { get; set; } = "-";
    public PublishPrefixMode PrefixMode { get; set; } = PublishPrefixMode.Add;
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";

    // Run-log fields (standalone Initiate only — a batch/schedule uses its own).
    public string DestinationFolder { get; set; } = "";
    public string ProjectLogName { get; set; } = "Initiate to Forma";

    /// <summary>True once a target folder has been picked/stored.</summary>
    [JsonIgnore]
    public bool HasTarget => !string.IsNullOrEmpty(FolderUrn);

    /// <summary>Resolves the stored identifiers + naming into the engine-ready target.
    /// Throws <see cref="InvalidOperationException"/> with a clear message if they don't parse.</summary>
    public PublishTarget ToPublishTarget() => PublishTarget.FromIdentifiers(
        HubId, ProjectId, FolderUrn, FolderPath, Prefix, Suffix, Separator, PrefixMode);

    /// <summary>Deep copy — used when a job carrying this snapshot is duplicated.</summary>
    public InitiateInputSet Clone() => new()
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
        ProjectLogName = ProjectLogName
    };

    /// <summary>Show the set's name in combos that bind these objects directly (e.g. the Schedule Run
    /// "Input set" dropdown for a Cut model job — CutModelInputSet inherits this). Without it a bound
    /// combo would render the type name, so the user never saw their saved set's name.
    /// Mirrors <see cref="RelinkInputSet.ToString"/> / <see cref="CorrectWorksetInputSet.ToString"/>.</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? base.ToString()! : Name;
}
