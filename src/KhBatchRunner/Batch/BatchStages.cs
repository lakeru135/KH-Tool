namespace KhBatchRunner.Batch;

/// <summary>
/// The stage names <see cref="BatchEngine"/> writes to the shared trail, for the code that has to
/// READ them back.
///
/// They live here rather than as literals at the call site for the same reason
/// <see cref="Schedule.RevitShutdown.ClosePostedMark"/> does: the supervisor runs in a different
/// process from the child that wrote the line, has no access to <see cref="BatchEngine"/> (which is
/// Revit-typed and excluded from the controller build), and matches these by substring. A stage
/// renamed at one end and not the other fails silently — the reader simply stops recognising it, and
/// the only symptom is a healthy instance being misjudged.
/// </summary>
internal static class BatchStages
{
    /// <summary>
    /// A model's document open. Written as the full
    /// <c>"open (background, detached)"</c> / <c>"open (background, attached)"</c>.
    /// </summary>
    public const string OpenDetached = "open (background, detached)";

    /// <inheritdoc cref="OpenDetached"/>
    public const string OpenAttached = "open (background, attached)";

    /// <summary>
    /// What a reader matches on to recognise a BACKGROUND open — the two above and the cheap
    /// <c>"probe open (background)"</c> alike. All three are the same thing as far as liveness is
    /// concerned: one blocking API call that emits no further breadcrumb until it returns.
    /// </summary>
    public const string OpenMark = "open (background";

    /// <summary>
    /// A model's document open on the ACTIVATED path — the modal runner's
    /// <c>BatchEngine.OpenRunActivate</c>, taken by every pass whose action needs the active
    /// document (Dynamo scripts, Export NWC). Just as blocking, and just as slow, as a background
    /// open.
    /// </summary>
    public const string OpenActivatedDetached = "open (activated, detached)";

    /// <inheritdoc cref="OpenActivatedDetached"/>
    public const string OpenActivatedAttached = "open (activated, attached)";

    /// <summary>
    /// Prefix of the SECOND open an action that needs specific worksets forces
    /// (<c>"reopen for 3 workset(s)"</c> — the count varies, so readers match the prefix).
    /// </summary>
    public const string ReopenForWorksets = "reopen for ";

    /// <summary>
    /// Whether <paramref name="stage"/> names ANY document open — background, activated, probe or
    /// workset reopen. The distinction matters nowhere it is read: all of them are one blocking call
    /// that emits no breadcrumb until it returns, so none of them can be judged by silence
    /// (<see cref="Schedule.ChildLiveness.IsOpening"/>).
    ///
    /// <para>Lives beside the constants deliberately. The supervisor matches these by substring from
    /// another process, so a stage renamed at one end and not the other fails silently — keeping the
    /// names and the match in one file is the only thing that makes that hard to do.</para>
    /// </summary>
    public static bool IsOpenStage(string? stage) =>
        !string.IsNullOrEmpty(stage) &&
        (stage!.IndexOf(OpenMark, StringComparison.Ordinal) >= 0 ||
         stage.IndexOf(OpenActivatedDetached, StringComparison.Ordinal) >= 0 ||
         stage.IndexOf(OpenActivatedAttached, StringComparison.Ordinal) >= 0 ||
         stage.IndexOf(ReopenForWorksets, StringComparison.Ordinal) >= 0);

    /// <summary>
    /// Appended to an open stage when the model is opened with plain <c>OpenOptions</c> (no workset
    /// configuration) — <c>"open (background, detached) [plain open: acc]"</c>. A suffix, so
    /// <see cref="IsOpenStage"/> still matches; the supervisor reads it to tell whether a crash happened
    /// under a plain open (see <see cref="CloudOpenFacts"/>).
    /// </summary>
    public const string PlainOpenTag = " [plain open: ";

    /// <summary>The stage text for an open of <paramref name="stage"/> under <paramref name="mode"/>.</summary>
    public static string WithMode(string stage, CloudOpenMode mode, string source) =>
        mode == CloudOpenMode.Plain ? stage + PlainOpenTag + (source.Length > 0 ? source : "?") + "]" : stage;

    /// <summary>Whether <paramref name="stage"/> is an open made with plain options.</summary>
    public static bool IsPlainOpenStage(string? stage) =>
        IsOpenStage(stage) && stage!.IndexOf(PlainOpenTag, StringComparison.Ordinal) >= 0;

    /// <summary>
    /// The model a trail line says is being OPENED — the middle field of
    /// <c>"&lt;action&gt; | &lt;model&gt; | &lt;open stage&gt;"</c> — or null when the line is not an open.
    ///
    /// <para>Exact field, not a substring search over the run's model names: <c>…ARC-002-0001.rvt</c>
    /// is not the only name that contains <c>ARC-002</c>, and the supervisor acts on this answer by
    /// changing how that one model is opened next.</para>
    /// </summary>
    public static string? OpeningModel(string? stage)
    {
        if (!IsOpenStage(stage)) return null;
        int first = stage!.IndexOf(" | ", StringComparison.Ordinal);
        int last = stage.LastIndexOf(" | ", StringComparison.Ordinal);
        if (first < 0 || last <= first) return null;
        string model = stage.Substring(first + 3, last - first - 3).Trim();
        return model.Length > 0 ? model : null;
    }

    /// <summary>The action itself running, on a document this pass opened.</summary>
    public const string ActionRun = "run";

    /// <summary>The action running on a document a previous pass left open — the common case, since
    /// 1.3.3 opens each model once per run rather than once per action.</summary>
    public const string ActionRunReusing = "run (reusing open document)";

    /// <summary>
    /// Whether <paramref name="stage"/> is an action mid-execution — the window between
    /// <c>run</c> and <c>ran</c>.
    ///
    /// <para>Matched on the LAST segment and compared whole, not by substring like
    /// <see cref="IsOpenStage"/>. "run" is three letters that occur inside ordinary words and inside
    /// model names, and a loose match here would hand every stage the long grace this predicate
    /// exists to grant.</para>
    ///
    /// <para>Why it needs a grace at all: an action is one call between two breadcrumbs, exactly like
    /// a document open. MEASURED 2026-08-18 (report <c>BKKLH3C7QV3_20260818_0801</c>): pid 81908 sat
    /// at <c>Drawing Progress | SITE3A-BLDC-STR.rvt | run (reusing open document)</c> from 03:22:13 and
    /// was called wedged at 03:32:26 on <c>cpu +1.2s</c>. The call returned at 03:48:24 — <b>26 min
    /// 11 s</b> — and the instance went on to report <c>36 ok, 0 failed</c> and post its own close.
    /// The run had already been stopped over it. See
    /// <see cref="Schedule.ChildLiveness.ActionGrace"/>.</para>
    /// </summary>
    public static bool IsActionRunStage(string? stage)
    {
        if (string.IsNullOrEmpty(stage)) return false;
        int last = stage!.LastIndexOf(" | ", StringComparison.Ordinal);
        if (last < 0) return false;
        string tail = stage.Substring(last + 3).Trim();
        return tail.Equals(ActionRun, StringComparison.Ordinal) ||
               tail.Equals(ActionRunReusing, StringComparison.Ordinal);
    }
}
