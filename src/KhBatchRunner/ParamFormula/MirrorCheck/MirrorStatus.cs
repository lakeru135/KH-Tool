namespace KhBatchRunner.ParamFormula;

/// <summary>
/// What Mirror Check found in a model, for its window. No Revit types, so tools\ThemePreview can
/// build the window offline.
/// </summary>
public sealed class MirrorCounts
{
    /// <summary>Family instances looked at.</summary>
    public int Instances { get; set; }
    public int Mirrored { get; set; }
    public int HandFlipped { get; set; }
    public int FacingFlipped { get; set; }
    /// <summary>Instances with at least one of the three.</summary>
    public int Flagged { get; set; }
    /// <summary>Elements whose KH_Mirror State differs from what they are (what Check All Now would write).</summary>
    public int OutOfDate { get; set; }
    /// <summary>Instances whose category does not carry the parameter.</summary>
    public int NoParameters { get; set; }
}

/// <summary>A shared text parameter Mirror Check could write instead of its own.</summary>
public sealed class MirrorTarget
{
    public string Name { get; set; } = "";
    public string Guid { get; set; } = "";
    /// <summary>How many categories it is bound to.</summary>
    public int Categories { get; set; }
    public bool Varies { get; set; }

    public override string ToString() => $"{Name}  ({Categories} categories{(Varies ? "" : " · does not vary in groups")})";
}

public sealed class MirrorStatus
{
    /// <summary>The parameter the status is about (blank = Mirror Check's own).</summary>
    public string TargetGuid { get; set; } = "";
    /// <summary>
    /// The parameter named by the model's setting is no longer in the model, so Mirror Check's own
    /// is used: its name, else null.
    /// </summary>
    public string? MissingTarget { get; set; }
    /// <summary>Model categories the chosen parameter lacks, and whether it varies between groups.</summary>
    public int TargetMissingCategories { get; set; }
    public bool TargetVaries { get; set; } = true;
    /// <summary>KH_Mirror State exists, varies between groups and is bound to every model category.</summary>
    public bool ParamsReady { get; set; }
    /// <summary>One line on the parameter, e.g. "Set up: KH_Mirror State on 125 categories."</summary>
    public string ParamsText { get; set; } = "";
    /// <summary>Why the parameter cannot be set up, or null.</summary>
    public string? Problem { get; set; }
    public bool Live { get; set; }
    public MirrorCounts Counts { get; set; } = new();
}
