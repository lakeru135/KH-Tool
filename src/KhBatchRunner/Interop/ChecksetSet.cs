namespace KhBatchRunner.Interop;

/// <summary>
/// A named, saved Model Checker checkset: a friendly <see cref="Name"/> mapped to a
/// <see cref="Path"/> (a local <c>.xml</c> file path or an interoperability.autodesk.com hosted
/// checkset <c>http(s)</c> URL). Saved as JSON by <see cref="ChecksetSetStore"/> so the Batch /
/// Schedule Interop windows can offer a dropdown of remembered checksets instead of re-browsing
/// each time. <see cref="ToString"/> returns the name so it renders directly in a combo.
/// </summary>
public sealed class ChecksetSet
{
    public string Name { get; set; } = "";

    /// <summary>Local <c>.xml</c> path or a hosted <c>http(s)</c> checkset URL.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Whether this checkset asks about the Revit link instances in the host model — pinned, named,
    /// on the right workset — as opposed to only the host's own elements.
    ///
    /// <para>Answered per checkset because only its author knows. It decides whether a run may leave
    /// worksets proven to hold nothing but links shut, which is worth many minutes per model; see
    /// <see cref="Actions.BatchInteropAction.Worksets"/> for the measurement and the risk. False by
    /// default, including for every checkset saved before 1.4.3 (an absent JSON property
    /// deserialises to false), because the Model Checker is invoked with <c>checkLinks=false</c> and
    /// so cannot look inside a link either way.</para>
    /// </summary>
    public bool ExaminesLinks { get; set; }

    /// <summary>Field-for-field copy, for the "Duplicate" button on both Interop windows.</summary>
    public ChecksetSet Clone() => new()
    {
        Name = Name,
        Path = Path,
        ExaminesLinks = ExaminesLinks,
    };

    public override string ToString() => Name;
}
