using KhBatchRunner.Presets;

namespace KhBatchRunner.Forma;

/// <summary>Where a tree node sits in the Forma Data Management hierarchy.</summary>
public enum FormaKind { Hub, Project, Folder }

/// <summary>Outcome of one <see cref="DataManagementClient.PublishCloudModel"/> call — the command
/// was accepted (2xx) plus any status/command id Forma returned. A rejected publish throws instead.</summary>
public sealed class PublishResult
{
    public bool Accepted { get; init; }
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>
/// A navigable node in the Forma tree (hub → project → folder). Carries the ids the
/// next-level Data Management call needs, plus the region inherited from the hub.
/// </summary>
public sealed class FormaEntry
{
    public FormaKind Kind { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string HubId { get; init; } = "";
    public string ProjectId { get; init; } = "";
    /// <summary>"US" or "EMEA", inherited from the hub — needed for the cloud path later.</summary>
    public string Region { get; init; } = "US";

    // Hubs, projects and folders can all be expanded further.
    public bool IsExpandable => true;

    public override string ToString() => Name;
}

/// <summary>
/// A single <c>.rvt</c> model chosen for a preset — either a Forma cloud model
/// (resolved to its cloud GUIDs) or a local file on this PC (a plain path).
/// </summary>
public sealed class FormaModelItem
{
    public string DisplayName { get; init; } = "";
    public string Region { get; init; } = "US";
    public Guid ProjectGuid { get; init; }
    public Guid ModelGuid { get; init; }

    /// <summary>The item's lineage URN (<c>urn:adsk.wipprod:dm.lineage:…</c>) — the resource the
    /// Forma "Publish Latest Version" (C4R publish) command targets. Empty for a local model or a
    /// model rehydrated from an older preset saved before publish support.</summary>
    public string ItemUrn { get; init; } = "";

    /// <summary>The Data Management (Forma Docs) project id, <c>b.</c>-prefixed — needed for the
    /// publish commands endpoint, which is keyed on the DM project, NOT the C4R
    /// <see cref="ProjectGuid"/> (they differ). Empty for a local model or an older preset.</summary>
    public string DmProjectId { get; init; } = "";

    /// <summary>The Forma hub this item was crawled from — see <see cref="Presets.CloudModel.HubId"/>.</summary>
    public string HubId { get; init; } = "";

    /// <summary>Set for a local model on this PC; null/empty for a Forma cloud model.</summary>
    public string? LocalPath { get; init; }

    public bool IsLocal => !string.IsNullOrWhiteSpace(LocalPath);

    /// <summary>
    /// The Revit release this model was saved in — <c>"2026"</c> — or empty when Forma did not say.
    ///
    /// Read from the tip version's <c>attributes.extension.data.revitProjectVersion</c> at SCAN
    /// time and never persisted: see <see cref="BlockedReason"/> for why that distinction is the
    /// whole design.
    /// </summary>
    public string RevitVersion { get; init; } = "";

    /// <summary>
    /// Whether this is a Revit Cloud Worksharing (C4R) model rather than a plain <c>.rvt</c> that was
    /// uploaded to Forma Docs and never initiated to the cloud. Only the former can be opened by a
    /// batch; the latter has no cloud GUID to open by. Always true for a local file, which is opened
    /// by path.
    /// </summary>
    public bool IsCloudWorkshared { get; init; } = true;

    /// <summary>
    /// Whether this item is a COPY, a SHARED instance, or a Forma Bridge CONSUMED model rather than
    /// the original — i.e. its version's <c>originalItemUrn</c> names a different lineage from its
    /// own item id.
    ///
    /// <para>These are the models that used to look perfectly fine: they carry the SOURCE's
    /// <see cref="ModelGuid"/> and <see cref="ProjectGuid"/>, so they pass every cloud-worksharing
    /// test and appear selectable. Running one does not fail — it opens the SOURCE central and does
    /// the work a second time, which is the worst of both worlds: no error, no new result, and a
    /// log that reads as a clean run. A genuinely bridged model is worse still and simply cannot be
    /// opened by GUID at all (0.9.1 proved all three candidate project GUIDs fail:
    /// <c>RevitServerUnauthorizedException</c> / <c>CentralModelMissing</c> / <c>LinkNotFound</c>).</para>
    /// </summary>
    public bool IsCopyOrShare { get; init; }

    /// <summary>The lineage this one was copied or shared from, when <see cref="IsCopyOrShare"/>.
    /// Shown in the browser's reason column so "which one is the real one?" has an answer.</summary>
    public string SourceItemUrn { get; init; } = "";

    /// <summary><c>extension.data.modelType</c> — <c>"multiuser"</c> for a workshared cloud model.
    /// Empty when Forma did not say, which is never treated as evidence of anything.</summary>
    public string ModelType { get; init; } = "";

    /// <summary>
    /// Why a batch running in <paramref name="runningRevitVersion"/> could not process this model,
    /// or null when it can. Null is also the answer whenever we do not KNOW — see below.
    ///
    /// <para><b>Nothing here is stored.</b> Both inputs are read live from Forma each time a folder is
    /// scanned, and neither is written to <see cref="Presets.ModelPreset"/>. That is deliberate and
    /// it is the difference between this and 1.3.5.4, which put a <c>RevitVersion</c> on model SETS,
    /// shipped, and was withdrawn the same day: every existing set was unassigned, unassigned sets
    /// were shown everywhere by design, and so the guard guarded nothing until each set was re-saved
    /// by hand. A guard keyed to data users have not entered yet is not a guard.</para>
    ///
    /// <para><b>UNKNOWN IS NEVER BLOCKED</b>, the same rule
    /// <see cref="Batch.BatchEngine.WrongVersionReason"/> states for the run itself. If Forma does not
    /// report a version — a field this hub may simply not carry — the model stays selectable and the
    /// run sorts it out at open time exactly as it does today. The alternative fails catastrophically
    /// on the one input we cannot verify from here: a missing field would grey out every model in
    /// the browser and leave nothing to pick.</para>
    ///
    /// <para>A mismatch blocks in BOTH directions. A newer model cannot be opened at all, and an
    /// older one would be UPGRADED just by opening it — quietly, and with no way back.</para>
    /// </summary>
    public string? BlockedReason(string runningRevitVersion)
    {
        if (IsLocal) return null;   // a local file's header is read at run time, exactly and free

        if (!IsCloudWorkshared)
            return "not a cloud-worksharing model — it was uploaded to Forma Docs, not initiated to " +
                   "Revit Cloud Worksharing, so a batch cannot open it";

        // Checked before the year: a copy carries the SOURCE's identity, so its year would be the
        // source's too and could easily match — a matching year is no reason to run the wrong model.
        if (IsCopyOrShare)
            return "a copy or shared instance, not the original — it points at the same cloud model " +
                   "as the file it came from, so running it would process that source model again";

        if (string.IsNullOrWhiteSpace(RevitVersion) || string.IsNullOrWhiteSpace(runningRevitVersion))
            return null;

        return string.Equals(RevitVersion, runningRevitVersion, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"saved in Revit {RevitVersion}, and this is Revit {runningRevitVersion}";
    }

    /// <summary>What to show in the browser's Type column.</summary>
    public string TypeLabel => IsLocal
        ? "local file"
        : !IsCloudWorkshared ? "uploaded file"
        : IsCopyOrShare ? "copy / shared"
        : "cloud workshared";

    /// <summary>What to show in the browser's Revit column; an em dash when Forma did not say.</summary>
    public string VersionLabel => string.IsNullOrWhiteSpace(RevitVersion) ? "—" : RevitVersion;

    /// <summary>
    /// Stable identity for de-duplication (cloud GUID vs. local path).
    ///
    /// The lineage-URN fallback covers the models listed since 1.3.8.2 that have NO cloud GUID —
    /// plain uploads, shown greyed out rather than silently omitted. Every one of those would
    /// otherwise key on <see cref="Guid.Empty"/>, so a folder holding two of them would look like a
    /// folder holding one.
    /// </summary>
    public string Key => IsLocal
        ? "local:" + LocalPath!.ToLowerInvariant()
        : ModelGuid != Guid.Empty ? "cloud:" + ModelGuid
        : "item:" + ItemUrn;

    /// <summary>A local .rvt on this PC picked from disk.</summary>
    public static FormaModelItem Local(string path) => new()
    {
        DisplayName = System.IO.Path.GetFileName(path),
        LocalPath = path
    };

    /// <summary>Rehydrate an editor item from a saved preset model.</summary>
    public static FormaModelItem FromCloudModel(CloudModel m) => new()
    {
        DisplayName = m.DisplayName,
        Region = m.Region,
        ProjectGuid = m.ProjectGuid,
        ModelGuid = m.ModelGuid,
        ItemUrn = m.ItemUrn,
        DmProjectId = m.DmProjectId,
        HubId = m.HubId,
        LocalPath = m.LocalPath
    };

    public CloudModel ToCloudModel() => new()
    {
        DisplayName = DisplayName,
        Region = Region,
        ProjectGuid = ProjectGuid,
        ModelGuid = ModelGuid,
        ItemUrn = ItemUrn,
        DmProjectId = DmProjectId,
        HubId = HubId,
        LocalPath = LocalPath,
    };

    public override string ToString() => IsLocal ? $"{DisplayName}  (local)" : DisplayName;
}
