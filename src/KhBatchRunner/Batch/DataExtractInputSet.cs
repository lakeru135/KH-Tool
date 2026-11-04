using System.Text.Json.Serialization;

namespace KhBatchRunner.Batch;

/// <summary>
/// The Get Data Extract dialog's inputs as a plain, JSON-round-trippable record — savable as a
/// named set (<see cref="DataExtractSetStore"/>), auto-restored as last-used, and snapshotted onto
/// a <c>ScheduledJob</c> so a scheduled download is self-contained.
///
/// <para>What it names is an extract that already exists in Forma: the account it lives on and the
/// request (Forma's word for one saved extract) whose newest successful run should be collected. The
/// add-in never creates or triggers the extract — Forma's own schedule does that overnight, and a
/// second schedule competing with it is exactly what this feature is meant to avoid.</para>
///
/// <para>Modelled on <see cref="FileLogInputSet"/>, the other Forma-only set that opens no model.</para>
/// </summary>
public sealed class DataExtractInputSet
{
    /// <summary>Set name (file key in <see cref="DataExtractSetStore"/>); empty for a last-used snapshot.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The Forma hub id as Data Management spells it (<c>b.&lt;guid&gt;</c>).
    ///
    /// <para>Stored in the Data Management spelling rather than the bare Data Connector account id
    /// because that is the form every other set in this repo carries, and
    /// <c>DataConnectorClient.AccountIdFrom</c> is the single place that converts. A set that
    /// stored the stripped form would be the one input set whose hub id did not match the others.</para>
    /// </summary>
    public string HubId { get; set; } = "";

    /// <summary>The hub's display name, for the UI and the log — ids tell a reader nothing.</summary>
    public string HubName { get; set; } = "";

    /// <summary>The extract request to collect.</summary>
    public string RequestId { get; set; } = "";

    /// <summary>The request's label as it read when the set was saved, for the same reason
    /// <see cref="HubName"/> is stored: a log row naming a GUID cannot be checked by a human.</summary>
    public string RequestName { get; set; } = "";

    /// <summary>The Forma project the extract covers (bare guid, Data Connector's spelling); empty
    /// for an account-wide extract. Stored so a saved set can say which project it means without
    /// re-reading Forma.</summary>
    public string ProjectId { get; set; } = "";

    /// <summary>
    /// The project's name as it read when the set was saved.
    ///
    /// <para>New in 1.5.1.3 and defaulted, so a set saved before it loads unchanged — it simply
    /// shows whatever <see cref="RequestName"/> it stored, which for a Forma-generated extract is
    /// the GUID sentence this field exists to replace.</para>
    /// </summary>
    public string ProjectName { get; set; } = "";

    /// <summary>
    /// Download the extract's ZIP instead of the individual CSVs.
    ///
    /// <para>Off by default: the point of the feature is files that open in Excel or Power BI
    /// without a manual unzip step.</para>
    /// </summary>
    public bool DownloadZipInstead { get; set; }

    /// <summary>
    /// Skip a run whose job has already been downloaded into the destination.
    ///
    /// <para>On by default. A daily schedule against a weekly extract would otherwise re-download
    /// the same job every morning, and a reader of the output folder could not tell which copy was
    /// this morning's.</para>
    /// </summary>
    public bool SkipIfAlreadyDownloaded { get; set; } = true;

    // Run-log fields, matching every other input set.
    public string DestinationFolder { get; set; } = "";
    public string ProjectLogName { get; set; } = "Get Data Extract";

    /// <summary>True once an account and a request have been chosen. A set without both can only
    /// produce an empty run, so the dialogs refuse to save or schedule it.</summary>
    [JsonIgnore]
    public bool HasTarget => !string.IsNullOrWhiteSpace(HubId) && !string.IsNullOrWhiteSpace(RequestId);

    /// <summary>
    /// What this set downloads, in a few words — shown in the Schedule Run dropdown and as the
    /// subject of every tracking-log row this step writes.
    ///
    /// <para>Account, then project, then the extract's label: the account is what a wrong choice
    /// gets wrong most damagingly (another company's data), and the project is what distinguishes
    /// two extracts on one account.</para>
    /// </summary>
    [JsonIgnore]
    public string TargetLabel
    {
        get
        {
            if (!HasTarget) return "no extract chosen";
            string account = string.IsNullOrWhiteSpace(HubName) ? HubId : HubName;
            string project = string.IsNullOrWhiteSpace(ProjectName)
                ? string.IsNullOrWhiteSpace(ProjectId) ? "whole account" : ProjectId
                : ProjectName;
            string request = string.IsNullOrWhiteSpace(RequestName) ? RequestId : RequestName;
            // The request label already leads with the project when the window resolved one, so
            // repeating it would read as "SampleHub → Sample Project → Sample Project — weekly".
            return request.StartsWith(project, StringComparison.OrdinalIgnoreCase)
                ? $"{account} → {request}"
                : $"{account} → {project} → {request}";
        }
    }

    /// <summary>The scope half of the same summary.</summary>
    [JsonIgnore]
    public string ScopeLabel => DownloadZipInstead ? "ZIP" : "CSV files";

    /// <summary>
    /// Deep copy. Every field above must be copied here — a split run builds its children with
    /// <c>ScheduledJob.Duplicate()</c>, and a field missed here is a field the children lose.
    /// </summary>
    public DataExtractInputSet Clone() => new()
    {
        Name = Name,
        HubId = HubId,
        HubName = HubName,
        RequestId = RequestId,
        RequestName = RequestName,
        ProjectId = ProjectId,
        ProjectName = ProjectName,
        DownloadZipInstead = DownloadZipInstead,
        SkipIfAlreadyDownloaded = SkipIfAlreadyDownloaded,
        DestinationFolder = DestinationFolder,
        ProjectLogName = ProjectLogName,
    };

    // Bound directly in the saved-sets combo (same convention as FileLogInputSet).
    public override string ToString() => Name;
}
