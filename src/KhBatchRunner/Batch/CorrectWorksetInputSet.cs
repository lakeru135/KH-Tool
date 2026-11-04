using System.Text.Json.Serialization;
using KhBatchRunner.Forma;

namespace KhBatchRunner.Batch;

/// <summary>
/// One category rule of a Correct Workset run: move every element of <see cref="Categories"/>
/// onto the workset named <see cref="TargetWorkset"/>. When that workset doesn't exist, an
/// existing workset whose name CONTAINS <see cref="Keyword"/> is proposed for RENAME to the
/// target (user-approved per model); with no keyword match the target is created.
/// </summary>
public sealed class CorrectWorksetRule
{
    /// <summary>BuiltInCategory enum names (e.g. "OST_Walls") — stored as strings so the JSON is
    /// stable across Revit versions.</summary>
    public List<string> Categories { get; set; } = new();

    public string TargetWorkset { get; set; } = "";

    /// <summary>Case-insensitive "contains" search that finds an existing workset to rename;
    /// empty = never rename, always create when missing.</summary>
    public string Keyword { get; set; } = "";

    public CorrectWorksetRule Clone() => new()
    {
        Categories = new List<string>(Categories),
        TargetWorkset = TargetWorkset,
        Keyword = Keyword
    };

    /// <summary>Short display label used in grids/logs ("3 categories → 10_STRUCT").</summary>
    [JsonIgnore]
    public string Label => $"{Categories.Count} categorie(s) → {TargetWorkset}";
}

/// <summary>
/// The Correct Workset dialog's inputs — rules grid, RVT-link rule, Check/Fix mode and host
/// source — savable as a named set (<see cref="CorrectWorksetSetStore"/>) with last-used recall.
/// A sibling of <see cref="RelinkInputSet"/>.
/// </summary>
public sealed class CorrectWorksetInputSet
{
    /// <summary>Set name (file key in <see cref="CorrectWorksetSetStore"/>); empty for last-used.</summary>
    public string Name { get; set; } = "";

    // --- hosts (same trio as Internal Relink + active model) ---
    public string ModelSetName { get; set; } = "";
    public bool HostsFromFolder { get; set; }
    public FolderRef? HostFolder { get; set; }
    public bool HostIncludeSubfolders { get; set; }
    /// <summary>Run on the model open in Revit right now (no sync — review/undo like ActiveModelRunner).</summary>
    public bool RunOnActiveModel { get; set; }

    // --- what to correct ---
    public List<CorrectWorksetRule> Rules { get; set; } = new();

    /// <summary>Apply the RVT-link rule: each top-level link alone in "&lt;LinkKeyword&gt;_&lt;model name&gt;".</summary>
    public bool ApplyLinkRule { get; set; } = true;
    public string LinkKeyword { get; set; } = "LINK";

    /// <summary>Apply the linked-CAD rule: move ALL linked CAD (DWG/DGN/…) onto ONE named workset.</summary>
    public bool ApplyCadRule { get; set; }
    public string CadWorksetName { get; set; } = "*LINKED CAD";

    /// <summary>False = Check only (report, change nothing); true = Fix (rename/create/move).</summary>
    public bool Fix { get; set; }

    // Run-log fields.
    public string DestinationFolder { get; set; } = "";
    public string ProjectLogName { get; set; } = "Correct Workset";

    public CorrectWorksetInputSet Clone() => new()
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
        RunOnActiveModel = RunOnActiveModel,
        Rules = Rules.Select(r => r.Clone()).ToList(),
        ApplyLinkRule = ApplyLinkRule,
        LinkKeyword = LinkKeyword,
        ApplyCadRule = ApplyCadRule,
        CadWorksetName = CadWorksetName,
        Fix = Fix,
        DestinationFolder = DestinationFolder,
        ProjectLogName = ProjectLogName
    };

    // Bound directly in the saved-sets combo (same convention as the other input sets).
    public override string ToString() => Name;
}
