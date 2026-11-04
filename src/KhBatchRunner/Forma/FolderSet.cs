using System.Text.Json.Serialization;

namespace KhBatchRunner.Forma;

/// <summary>
/// A serializable reference to one Forma folder — everything needed to re-issue Data Management
/// calls against it (project id, folder URN, region) without re-browsing the tree, plus a
/// human-readable path. Rebuilt into an <see cref="FormaEntry"/> via <see cref="ToEntry"/>.
/// </summary>
public sealed class FolderRef
{
    public string Name { get; set; } = "";
    /// <summary>Tree path shown to the user (hub \ project \ folder \ …).</summary>
    public string DisplayPath { get; set; } = "";

    /// <summary>
    /// The Forma PROJECT this folder sits in, read out of <see cref="DisplayPath"/>.
    ///
    /// <para><see cref="DisplayPath"/> is the Forma tree's own path, and that tree is
    /// hub → project → folders — so segment 0 names the hub and segment 1 the project. Derived
    /// rather than stored so folder sets saved before this existed still answer.</para>
    ///
    /// <para>Wanted because folder names repeat: a run over
    /// <c>Project-ALPHA-A\…\2.1.1_Drawings</c> and <c>Project-BETA-A\…\2.1.1_Drawings</c> produces
    /// two reports whose only distinguishing feature would otherwise be a <c>_2</c> nobody can
    /// interpret. Empty when the path was never captured, and callers fall back to the bare folder
    /// name.</para>
    /// </summary>
    [JsonIgnore]
    public string FormaProjectName => PathSegment(1);

    /// <summary>The Forma HUB, segment 0 of <see cref="DisplayPath"/>. Not in report names — the hub
    /// repeats across every folder of a normal run, where the project actually varies — but it is
    /// what tells two same-named projects apart when that happens.</summary>
    [JsonIgnore]
    public string FormaHubName => PathSegment(0);

    private string PathSegment(int index)
    {
        if (string.IsNullOrWhiteSpace(DisplayPath)) return "";
        string[] parts = DisplayPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? parts[index].Trim() : "";
    }
    public string HubId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    /// <summary>The folder's Data Management URN (the <see cref="FormaEntry.Id"/>).</summary>
    public string FolderUrn { get; set; } = "";
    public string Region { get; set; } = "US";

    public static FolderRef FromEntry(FormaEntry folder, string displayPath) => new()
    {
        Name = folder.Name,
        DisplayPath = displayPath,
        HubId = folder.HubId,
        ProjectId = folder.ProjectId,
        FolderUrn = folder.Id,
        Region = folder.Region,
    };

    /// <summary>Field-for-field copy. A saved set snapshotted onto a scheduled job is duplicated
    /// per group for a split run, and a shared list element would be aliased across those copies.</summary>
    public FolderRef Clone() => new()
    {
        Name = Name,
        DisplayPath = DisplayPath,
        HubId = HubId,
        ProjectId = ProjectId,
        FolderUrn = FolderUrn,
        Region = Region,
    };

    public FormaEntry ToEntry() => new()
    {
        Kind = FormaKind.Folder,
        Id = FolderUrn,
        Name = Name,
        HubId = HubId,
        ProjectId = ProjectId,
        Region = Region,
    };

    public override string ToString() => string.IsNullOrEmpty(DisplayPath) ? Name : DisplayPath;
}

/// <summary>A named, saved collection of Forma folders — the folder-set analog of a model preset.</summary>
public sealed class FolderSet
{
    public string Name { get; set; } = "";
    public List<FolderRef> Folders { get; set; } = new();

    /// <summary>Deep copy, for the "Duplicate" button — the folder list is copied element by element
    /// with <see cref="FolderRef.Clone"/>, so the copy and the original never share a FolderRef.</summary>
    public FolderSet Clone() => new()
    {
        Name = Name,
        Folders = Folders.Select(f => f.Clone()).ToList(),
    };

    // Shown directly in CheckSheetPublishWindow's saved-sets combo (raw objects are bound,
    // same convention as FolderRef above) — without this the combo renders the type name.
    public override string ToString() => Name;
}
