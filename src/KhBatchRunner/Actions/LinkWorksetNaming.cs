namespace KhBatchRunner.Actions;

/// <summary>
/// The one link-workset naming convention, in one place: <c>&lt;keyword&gt;_&lt;link type name without
/// ".rvt"&gt;</c>.
///
/// <para><b>Why it is shared rather than written twice.</b> Two actions have to agree on this string
/// or the pair is worse than either alone. <see cref="CorrectWorksetAction"/> uses it to decide
/// whether a link is already on the right workset; <see cref="BatchLinkRvtAction"/> uses it to decide
/// where to PUT a link it is creating. If the two formulas drift by so much as a separator, every
/// link Batch Link RVT places is a link Correct Workset immediately moves — and the run that was
/// supposed to establish the convention becomes the run that violates it. Before this class,
/// <c>StripRvt</c> existed privately in <see cref="CorrectWorksetAction"/> and again in
/// <see cref="RelinkLinksAction"/>, so a third copy was the default outcome.</para>
///
/// <para>Revit-free by construction — plain strings in, a plain string out — so the agreement between
/// the two actions is asserted offline in <c>tools/LivenessSim</c> with no Revit installed, the same
/// treatment <c>BatchInteropAction.WorksetsToOpen</c> and <c>ReopenLadder.Refused</c> get.</para>
/// </summary>
internal static class LinkWorksetNaming
{
    /// <summary>
    /// The workset a top-level Revit link belongs on under the one-link-per-workset convention.
    /// <paramref name="linkTypeName"/> is <c>RevitLinkType.Name</c> — Revit's own name for the link,
    /// not the name of the file or of the Forma item it came from. Those can differ (Revit appends a
    /// disambiguating suffix when a name is already taken), and the convention is only worth anything
    /// if both sides read it off the same element.
    /// </summary>
    public static string Expected(string keyword, string linkTypeName) =>
        keyword + "_" + StripRvt(linkTypeName);

    /// <summary>The link name without its ".rvt" extension; unchanged when it has none.</summary>
    public static string StripRvt(string name) =>
        name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
