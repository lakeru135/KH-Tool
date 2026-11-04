using Autodesk.Revit.DB;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Batch;

/// <summary>
/// Keeps ONE background document open across the consecutive passes of a model-major run, so a
/// model is opened once rather than once per action.
///
/// <b>Why this exists.</b> Both model-major drivers — <see cref="BatchIdlingDriver"/> (interactive)
/// and <c>ScheduledRunner</c>'s headless tick — already run one model through every pass before
/// advancing, but each pass called <see cref="BatchEngine.ProcessModel"/>, which opens, runs and
/// closes. Six actions therefore meant six opens of the same model in the same mode. Measured on
/// the 98-model office run of 2026-07-27: 576 opens for 96 model-runs, ~25 s median each, 240 of
/// the run's 487 action-minutes — 49%, the single largest cost in the batch.
///
/// <b>What it does NOT do.</b> It never keeps two documents open at once, so peak memory is
/// unchanged, and it only ever reuses a document when
/// <see cref="BatchEngine.CanJoinOpenDocument"/> says the result is identical to re-opening
/// (read-only Checks whose workset needs are already met). Anything else — a different model, a
/// write pass, a script set, an unusable document — closes what is held and opens fresh, exactly
/// as before.
///
/// <b>Failure shape.</b> Losing a Revit mid-model now costs that model's whole action sequence
/// rather than one action. That is already covered: <c>TrackingCsvReader.CompletedModels</c> only
/// counts a model done when every "k of N" index is present, so the supervisor's round ladder
/// re-runs a partially-processed model — the behaviour the 1.3.1/1.3.2 office runs exercised.
///
/// Not thread-safe, and not meant to be: a driver owns one cursor and touches it from the Revit
/// API thread only. <see cref="Release"/> must be called when the model changes and when the run
/// ends (including on the error path), or a document leaks for the life of the process.
/// </summary>
internal sealed class ModelDocumentCursor
{
    private Document? _doc;
    private BatchEngine? _owner;   // the engine that OPENED _doc — owns its temp local, if any
    private CloudModel? _model;

    /// <summary>
    /// Runs one (model, pass) unit: joins the held document when that is provably equivalent to a
    /// fresh open, otherwise releases it and opens the model for this pass. Never throws for a
    /// failed open — the model gets its ERROR tracking row (<see cref="BatchEngine.LogOpenFailure"/>)
    /// and the run continues, matching <see cref="BatchEngine.ProcessModel"/>'s per-model contract.
    /// </summary>
    public void Run(BatchEngine engine, CloudModel model, BatchRunState state)
    {
        // Checked BEFORE the join test, not only before the open: a model this Revit must not run
        // is not made acceptable by a document already being held for it. Same decision as
        // BatchEngine.ProcessModel — see BatchEngine.WrongVersionReason.
        string? wrongVersion = engine.WrongVersionReason(model);
        if (wrongVersion != null)
        {
            engine.LogWrongVersion(model, state, wrongVersion);
            return;
        }

        if (_doc != null && SameModel(_model, model) && engine.CanJoinOpenDocument(_owner!, _doc))
        {
            engine.RunOnOpenDocument(model, _doc, state, reusing: true);
            return;
        }

        // A different model, an incompatible pass, or a document Revit has invalidated: whatever is
        // held is of no further use, and Revit forbids opening a model that is already open.
        Release();

        Document doc;
        try
        {
            doc = engine.OpenForModel(model);
        }
        catch (Exception ex)
        {
            engine.LogOpenFailure(model, state, ex);
            return;
        }

        _doc = doc;
        _owner = engine;
        _model = model;
        engine.RunOnOpenDocument(model, doc, state);
    }

    /// <summary>
    /// Closes the held document (if any) through the engine that opened it, so its temp local copy
    /// is cleaned up by the same engine that created it. Idempotent — safe to call when nothing is
    /// held, and safe to call twice.
    /// </summary>
    public void Release()
    {
        Document? doc = _doc;
        BatchEngine? owner = _owner;
        CloudModel? model = _model;
        _doc = null;
        _owner = null;
        _model = null;
        if (doc == null || owner == null || model == null) return;
        try { owner.CloseOpened(model, doc); }
        catch { /* best effort — a close that fails must never abort the batch */ }
    }

    /// <summary>
    /// Identity by VALUE, not reference. Passes in one job normally share the same combined preset
    /// (so the instances are identical), but a job assembled from several presets can hand out
    /// distinct <see cref="CloudModel"/> objects for the same file — and reusing a document for a
    /// model that only looks the same would be a correctness bug, while failing to reuse one that
    /// is the same is merely slow.
    /// </summary>
    internal static bool SameModel(CloudModel? a, CloudModel? b)
    {
        if (a is null || b is null) return false;
        if (ReferenceEquals(a, b)) return true;
        if (a.IsLocal != b.IsLocal) return false;
        return a.IsLocal
            ? string.Equals(a.LocalPath, b.LocalPath, StringComparison.OrdinalIgnoreCase)
            : a.ModelGuid == b.ModelGuid && a.ProjectGuid == b.ProjectGuid;
    }
}
