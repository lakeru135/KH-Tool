namespace KhBatchRunner.Forma;

/// <summary>
/// One row of the <b>File Log</b> extract — a single version of one file in a Forma folder.
/// Produced by <see cref="DataManagementClient.GetFileLog"/> from the Data Management
/// <c>/items/{id}/versions</c> endpoint (a native port of the team's
/// <c>Check_01_Get File Log from URL</c> Dynamo graph, but sourced from a picked folder rather
/// than a pasted URL). Read-only; opens no Revit document.
///
/// <para>Any file type since 1.4.4, not only <c>.rvt</c> — every field below is a generic Data
/// Management version attribute that a DWG or a PDF fills in exactly as a model does. See
/// <see cref="FileLogSpec"/>.</para>
/// </summary>
public sealed class FormaFileLogRow
{
    /// <summary>The item's display name, extension included.</summary>
    public string FileName { get; init; } = "";

    /// <summary>Lowercase extension without its dot ("rvt", "dwg", "pdf"); "" when the name has
    /// none. The column that makes a mixed-type log sortable.</summary>
    public string FileType { get; init; } = "";
    public int VersionNumber { get; init; }

    /// <summary>
    /// A browser link that opens THIS version in Forma; "" when one could not be determined.
    ///
    /// <para>Replaces the <c>Version URN</c> column, which was an opaque
    /// <c>urn:adsk.wipprod:fs.file:vf.…?version=3</c> nobody could act on. Plain text, not an Excel
    /// <c>=HYPERLINK()</c> formula — these CSVs are read by Power BI, where a bare URL can be typed
    /// as a Web URL column and a formula is only noise.</para>
    /// </summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// The version's Data Management URN. No longer a CSV column, and still needed: it is the
    /// dedupe key when one run reads folders that overlap (see <c>FileLogStep</c>), and it is the
    /// only value that distinguishes two versions when nothing else does.
    /// </summary>
    public string VersionUrn { get; init; } = "";
    public string LastModified { get; init; } = "";
    public string ModifiedBy { get; init; } = "";
    /// <summary>Storage size in bytes, or null when the API didn't report it.</summary>
    public long? StorageSize { get; init; }
    public string ProcessingState { get; init; } = "";
    public string Comment { get; init; } = "";
}

/// <summary>
/// One row of the <b>Folder Inventory</b> extract — a single <c>.rvt</c> in a Forma folder with
/// the cloud identity a preset would capture, plus its tip-version metadata and whether it's a
/// cloud-worksharing (published) model. Produced by
/// <see cref="DataManagementClient.GetFolderInventory"/>; the crawler's data widened to a CSV.
/// </summary>
public sealed class FormaInventoryRow
{
    public string ModelName { get; init; } = "";
    /// <summary>Human-readable folder path (hub/project names aren't included — just the folder chain from the picked root).</summary>
    public string FolderPath { get; init; } = "";
    public Guid ProjectGuid { get; init; }
    public Guid ModelGuid { get; init; }
    public string Region { get; init; } = "US";
    public int? LatestVersion { get; init; }
    public string LastModified { get; init; } = "";
    public string ModifiedBy { get; init; } = "";
    /// <summary>True when the item resolved to cloud GUIDs — i.e. a C4R cloud-worksharing model.</summary>
    public bool Published { get; init; }

    /// <summary>The item's lineage URN, the DM project id and the hub — the identity a folder-backed
    /// set needs to keep when it is re-scanned. Before 1.5.0 this method dropped all three, so a
    /// folder re-scan silently returned models that could no longer be published (which is keyed on
    /// the first two) and could not be attributed to an account (the third).</summary>
    public string ItemUrn { get; init; } = "";

    /// <inheritdoc cref="ItemUrn"/>
    public string DmProjectId { get; init; } = "";

    /// <inheritdoc cref="ItemUrn"/>
    public string HubId { get; init; } = "";

    /// <summary>The Revit release the model was saved in ("2026"), or empty when Forma did not say.
    /// Read live from the tip version and never stored — see
    /// <see cref="FormaModelItem.BlockedReason"/>.</summary>
    public string RevitVersion { get; init; } = "";

    /// <summary>A copy / shared / Bridge-consumed instance rather than the original — see
    /// <see cref="FormaModelItem.IsCopyOrShare"/>. Carried here too because a folder-backed model set
    /// is re-scanned through this path at run time and never passes the browser.</summary>
    public bool IsCopyOrShare { get; init; }

    /// <summary>This row as the browser would judge it, so the folder-scan path and the picking path
    /// answer "can this model run here?" with the same code rather than two rules that drift.</summary>
    public FormaModelItem AsModelItem() => new()
    {
        DisplayName = ModelName,
        Region = Region,
        ProjectGuid = ProjectGuid,
        ModelGuid = ModelGuid,
        RevitVersion = RevitVersion,
        IsCloudWorkshared = Published,
        IsCopyOrShare = IsCopyOrShare
    };
}

/// <summary>
/// A <c>.rvt</c> in a Forma folder resolved to its <b>version URN</b> — the id Model Derivative
/// needs (Base64url-encoded) to read the model's published sheet views. Produced by
/// <see cref="DataManagementClient.GetRevitItemRefs"/>; consumed by Check Sheet Publish.
/// </summary>
public sealed class FormaModelRef
{
    public string DisplayName { get; init; } = "";
    public string VersionUrn { get; init; } = "";
    public string Region { get; init; } = "US";
    public string FolderPath { get; init; } = "";
}

/// <summary>
/// One row of the <b>Check Sheet Publish</b> report (Model / Sheet No. / Is Match). Three row
/// shapes share this type: a match (model + sheet number, IsMatch "Yes"); a sheet number found
/// in NO model (ModelName = <see cref="NotFoundModel"/>, IsMatch "No" — exactly one row per such
/// sheet, no per-model "No" rows); and a model whose Forma translation isn't finished (SheetNumber
/// empty, IsMatch = <see cref="NotProcessed"/>) so "not found" results are visibly qualified
/// when a model couldn't be searched.
/// </summary>
public sealed class SheetPublishRow
{
    public const string NotFoundModel = "Not found in any Models";
    public const string NotProcessed = "Model not processed";

    public string ModelName { get; init; } = "";
    public string SheetNumber { get; init; } = "";
    /// <summary>"Yes", "No", or <see cref="NotProcessed"/>.</summary>
    public string IsMatch { get; init; } = "";
}

/// <summary>
/// What Forma actually says about one item in a folder — the raw diagnostic behind the crawler's
/// Model / Revit / Type columns.
///
/// <para><b>Why this is a shipped feature and not a spike.</b> Every Forma investigation in this
/// project has started from the same question — "what does the API actually return here?" — and
/// until 1.3.8.3 the only way to answer it was an env-gated self-test, a PowerShell script kept
/// outside the repo, and a Revit restart. Two defects came directly from guessing instead:
/// <c>revitProjectVersion</c> read as a string when Forma sends a number, and copies treated as
/// originals because <c>extension.type</c> is identical for both. Answering the question has to be
/// cheaper than that.</para>
/// </summary>
public sealed class FormaScanDetailRow
{
    public string ModelName { get; init; } = "";
    public string FolderPath { get; init; } = "";

    /// <summary>The item's own lineage URN.</summary>
    public string ItemUrn { get; init; } = "";

    /// <summary>The lineage the tip version says it came from. Equal to <see cref="ItemUrn"/> for
    /// an original; a DIFFERENT lineage for a copy, a shared instance or a Bridge-consumed model.
    /// This one field is the whole copy-detection rule.</summary>
    public string OriginalItemUrn { get; init; } = "";

    public string ItemExtensionType { get; init; } = "";
    public string VersionExtensionType { get; init; } = "";

    /// <summary>Every key name present under the tip version's <c>attributes.extension.data</c> —
    /// so a field we are not reading yet is visible without another code change.</summary>
    public string ExtensionDataKeys { get; init; } = "";

    public string ModelGuid { get; init; } = "";
    public string ProjectGuid { get; init; } = "";
    public string RevitVersion { get; init; } = "";
    public string ModelType { get; init; } = "";
    public string ProcessState { get; init; } = "";

    /// <summary>What the crawler decided, and why — so the CSV and the greyed row can be compared
    /// directly rather than reasoned about.</summary>
    public string Verdict { get; init; } = "";
}
