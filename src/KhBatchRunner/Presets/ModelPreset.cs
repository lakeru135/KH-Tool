namespace KhBatchRunner.Presets;

/// <summary>
/// A named, saved collection of models to batch-run against — Forma/BIM 360 cloud
/// models (opened via <c>ModelPathUtils.ConvertCloudGUIDsToCloudPath</c>) and/or
/// local models on this PC (opened by file path).
///
/// A set can be <b>folder-backed</b>: when <see cref="SourceFolders"/> has one or more Forma folders,
/// the set IS those folders and its members are re-scanned (and unioned) at run time (via
/// <see cref="PresetResolver"/>), so models added to the folders are picked up automatically.
/// <see cref="Models"/> then holds the save-time snapshot, used as a preview in the editor and as
/// an offline fallback when the folders can't be scanned. A plain (model-list) set leaves
/// <see cref="SourceFolders"/> empty and its <see cref="Models"/> are the authoritative list.
/// </summary>
public class ModelPreset
{
    /// <summary>Stable identity that survives a rename. Schedules reference this (not <see cref="Name"/>)
    /// so renaming a set doesn't orphan them. Empty by default (and on presets saved before ids
    /// existed); <see cref="PresetStore.LoadAll"/> back-fills + persists an id on load so a saved set
    /// always has a stable, non-empty id. Do not initialize to a fresh Guid here — deserializing old
    /// JSON that lacks the field would then mint a NEW id every load and defeat the purpose.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// Legacy field, kept only so older preset JSON still deserializes. The project/log name is
    /// now chosen at run time (Batch Run / Schedule Run / a Multi Model Check), so the Edit Model
    /// Set window no longer surfaces or writes this — new sets leave it empty.
    /// </summary>
    public string ProjectCode { get; set; } = "";

    /// <summary>Legacy single-folder field (pre-0.7.1). Still read so older folder-set JSON loads;
    /// <see cref="AllSourceFolders"/> folds it into <see cref="SourceFolders"/>. New sets write
    /// <see cref="SourceFolders"/> and leave this null.</summary>
    public Forma.FolderRef? SourceFolder { get; set; }

    /// <summary>Set for a FOLDER-backed model set: the Forma folder(s) whose models this set resolves
    /// to (unioned) at run time. Empty for a plain model-list set.</summary>
    public List<Forma.FolderRef> SourceFolders { get; set; } = new();

    /// <summary>Recurse into subfolders when re-scanning the source folder(s).</summary>
    public bool IncludeSubfolders { get; set; }

    /// <summary>The source folders to re-scan, folding in the legacy single <see cref="SourceFolder"/>
    /// if an old JSON only carried that. Empty for a plain set.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<Forma.FolderRef> AllSourceFolders =>
        SourceFolders.Count > 0
            ? SourceFolders
            : (SourceFolder != null ? new List<Forma.FolderRef> { SourceFolder } : new List<Forma.FolderRef>());

    /// <summary>True when this set resolves its members from a Forma folder at run time.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFolderBacked => SourceFolders.Count > 0 || SourceFolder != null;

    public List<CloudModel> Models { get; set; } = new();
}

/// <summary>Identity for a single .rvt: Forma cloud (GUIDs) or a local file path.</summary>
public class CloudModel
{
    /// <summary>Friendly name for logs/UI (e.g. "ARC-Tower-A.rvt").</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>"US" or "EMEA" — required for ConvertCloudGUIDsToCloudPath (cloud only).</summary>
    public string Region { get; set; } = "US";

    public Guid ProjectGuid { get; set; }

    public Guid ModelGuid { get; set; }

    /// <summary>The item's lineage URN (<c>urn:adsk.wipprod:dm.lineage:…</c>), captured when the
    /// model is crawled. Needed by the Forma "Publish Latest Version" command; empty for a local
    /// model or a set saved before publish support (re-save the set to capture it).</summary>
    public string ItemUrn { get; set; } = "";

    /// <summary>The Data Management (Forma Docs) project id (<c>b.</c>-prefixed) the publish command
    /// is keyed on — distinct from the C4R <see cref="ProjectGuid"/>. Empty for local/older sets.</summary>
    public string DmProjectId { get; set; } = "";

    /// <summary>
    /// The Forma hub (<c>b.</c>-prefixed) this model was crawled from. Empty for a local model and
    /// for every set saved before 1.5.0.
    ///
    /// <para>Recorded so a saved set can say which Autodesk ACCOUNT it belongs to. Nothing else in
    /// a model-list set can: <see cref="ProjectGuid"/> is the C4R project GUID and has no relation
    /// to the Data Management project id, so before this the only account-attributable sets were
    /// the folder-backed ones (via <see cref="Forma.FolderRef.HubId"/>). An empty value means
    /// UNKNOWN and must never be read as "no access" — see <see cref="AccountAccess"/>.</para>
    /// </summary>
    public string HubId { get; set; } = "";

    /// <summary>Set for a local model on this PC; null for a Forma cloud model.</summary>
    public string? LocalPath { get; set; }

    /// <summary>True when this entry is a local file rather than a Forma cloud model.</summary>
    public bool IsLocal => !string.IsNullOrWhiteSpace(LocalPath);
}
