using System.Text.Json.Serialization;
using KhBatchRunner.Forma;

namespace KhBatchRunner.Batch;

/// <summary>
/// The Get File Log dialog's inputs as a plain, JSON-round-trippable record — savable as a named
/// set (<see cref="FileLogSetStore"/>), auto-restored as last-used, and snapshotted onto a
/// <c>ScheduledJob</c> so a scheduled pull is self-contained.
///
/// <para><b>Why a set exists at all.</b> The extract used to take one folder picked from the tree
/// and nothing else, so re-running last week's pull meant re-browsing the tree and re-ticking the
/// options, and a schedule could not describe the job at all — it borrowed the folders from
/// whatever folder-backed model sets the job happened to carry. A named set makes the same inputs
/// run identically by hand and unattended.</para>
///
/// <para>Modelled on <see cref="RelinkInputSet"/>, the other set whose subject is a LIST of Forma
/// folders rather than a single publish target; the folders are snapshotted as
/// <see cref="FolderRef"/> so a saved set never depends on a model set still existing.</para>
/// </summary>
public sealed class FileLogInputSet
{
    /// <summary>Set name (file key in <see cref="FileLogSetStore"/>); empty for a last-used snapshot.</summary>
    public string Name { get; set; } = "";

    /// <summary>The Forma folders to read. <b>One report is written per folder</b>, so the order and
    /// membership here is exactly the set of output files a run produces.</summary>
    public List<FolderRef> Folders { get; set; } = new();

    /// <summary>Recurse into subfolders. Applies to every folder in the set — a subfolder's rows go
    /// into its parent's report, not a report of their own.</summary>
    public bool IncludeSubfolders { get; set; }

    /// <summary>Which files, and how much history — see <see cref="FileLogSpec"/>.</summary>
    public FileLogSpec Spec { get; set; } = new();

    // Run-log fields, matching every other input set.
    public string DestinationFolder { get; set; } = "";
    public string ProjectLogName { get; set; } = "Get File Log";

    /// <summary>True once at least one folder has been chosen. A set without one can only produce
    /// an empty run, so the dialogs refuse to save or schedule it.</summary>
    [JsonIgnore]
    public bool HasFolders => Folders.Count > 0;

    /// <summary>What this set reads, in a few words — shown in the Schedule Run dropdown so
    /// "3 folders" can never be mistaken for "the whole project".</summary>
    [JsonIgnore]
    public string FoldersLabel =>
        Folders.Count == 0
            ? "no folders"
            : $"{Folders.Count} folder(s)" + (IncludeSubfolders ? ", incl. subfolders" : "");

    /// <summary>The scope half of the same summary.</summary>
    [JsonIgnore]
    public string ScopeLabel => Spec.Label();

    /// <summary>
    /// Deep copy, INCLUDING every folder and the spec.
    ///
    /// <para>Not optional: a split run builds its children with <c>ScheduledJob.Duplicate()</c>, and
    /// a shallow copy would hand several processes the same list instance. Every field added above
    /// must be copied here too.</para>
    /// </summary>
    public FileLogInputSet Clone() => new()
    {
        Name = Name,
        Folders = Folders.Select(f => f.Clone()).ToList(),
        IncludeSubfolders = IncludeSubfolders,
        Spec = Spec.Clone(),
        DestinationFolder = DestinationFolder,
        ProjectLogName = ProjectLogName,
    };

    // Bound directly in the saved-sets combo (same convention as FolderSet/FolderRef).
    public override string ToString() => Name;
}
