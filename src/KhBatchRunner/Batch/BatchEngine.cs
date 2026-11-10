using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Batch;

// SyncMode and BatchCompletion moved to Batch\BatchOptions.cs — they are job configuration, not
// engine machinery, and a non-Revit host needs them to compose a job without referencing this file.

/// <summary>Aggregate outcome of a batch run.</summary>
public sealed class BatchResult
{
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }

    /// <summary>
    /// Models not run because this Revit is the wrong version for them. A SUBSET of
    /// <see cref="FailedCount"/>, not a third category — every one of these is counted as a failure
    /// too, on purpose.
    ///
    /// It was the other way round for one release: a skip is a correct outcome, so it seemed wrong
    /// to call it an error. In use that reasoning is backwards — a model that did not run is work
    /// still owed, and a run that reports "all clear" while quietly leaving models untouched is
    /// exactly what nobody can monitor. A skip is now loud, and this counter only exists so the
    /// loud thing can still say WHY.
    /// </summary>
    public int SkippedCount { get; set; }
}

/// <summary>
/// The state one run of <see cref="BatchEngine"/> threads through every model — the two
/// loggers and the running <see cref="BatchResult"/>. <see cref="BatchEngine.BeginRun"/>
/// creates it; <see cref="BatchEngine.ProcessModel"/> mutates it once per model, driven one per
/// Idling tick (<see cref="BatchIdlingDriver"/> interactively, <c>ScheduledRunner</c> headless).
/// Each model is opened in the background and closed within the same
/// <see cref="BatchEngine.ProcessModel"/> call, so there is no cross-model document to carry here.
/// </summary>
public sealed class BatchRunState
{
    public required TrackingCsvLogger Tracking { get; init; }
    public required DetailCsvLogger Detail { get; init; }
    public BatchResult Result { get; init; } = new();
}

/// <summary>
/// Orchestrates open → run → (sync) → close over every model in a preset, one at a time.
/// Per-model try/catch so one bad model logs an ERROR row and the batch continues. The
/// engine (not the action) writes each log row, guaranteeing exactly one Status/Message
/// row per model with stable columns.
///
/// NOTE: models are opened in the background (<c>Application.OpenDocumentFile</c>), never
/// activated in the UI. Both entry points that drive the engine — <see cref="BatchIdlingDriver"/>
/// (interactive ribbon buttons) and <c>ScheduledRunner</c> (headless) — call
/// <see cref="BeginRun"/> + <see cref="ProcessModel"/> one model per <c>Idling</c> tick. Two
/// separate Revit rules force that shape: opening/activating a document synchronously inside an
/// executing <c>IExternalCommand</c> corrupts that command's journal/undo scope, and switching
/// the active UI document during API event handling is outright forbidden — a background open
/// under the idle loop avoids both.
/// </summary>
public sealed partial class BatchEngine
{
    private readonly UIApplication _uiApp;
    private readonly IBatchAction _action;
    private readonly string _destinationFolder;
    private readonly string _projectLog;
    private readonly SyncMode _syncMode;
    // Value written to the tracking log's "Task" column — the action name, prefixed "k of N"
    // when the run has more than one task (Batch Run / a multi-item schedule).
    private readonly string _taskLabel;
    // Value written to the detail log's "Run" column — see DetailCsvLogger.RunTag.
    private readonly string _runTag;
    // The same instant unformatted, handed to the action so a run-level artifact can be named for
    // the RUN rather than for the moment it was written — see IBatchAction.RunStamp.
    private readonly DateTime _runStamp;

    // Temp local copy created for a write Action on a workshared model (see OpenLocalModel);
    // deleted after the model is closed. One model is processed per ProcessModel call, so a
    // single field is enough.
    private string? _pendingLocalCleanup;

    // OpenRunActivate hands documents back to the caller and their closes are deferred (the
    // next model must activate before the previous can close), so the single field above isn't
    // enough there — temp locals are keyed by the document until CloseInteractive deletes them.
    private readonly Dictionary<Document, string> _pendingLocalByDoc = new();

    /// <summary>The action this engine runs — exposed so a multi-pass driver (<see cref="BatchIdlingDriver"/>) can label each pass without threading a parallel list of names alongside the engines.</summary>
    public string ActionName => _action.Name;

    /// <summary>True when this pass must run against the ACTIVE document (see <see cref="IBatchAction.NeedsActiveDocument"/>) — the modal runner then uses <see cref="OpenRunActivate"/> even for close-as-you-go dispositions.</summary>
    public bool NeedsActiveDocument => _action.NeedsActiveDocument;

    /// <summary>
    /// True when this pass opens models ATTACHED (a write Action / script set) rather than
    /// detached (a read-only Check). Exposed so a model-major runner can tell whether two
    /// consecutive activated passes on the same model share an open mode and may therefore reuse
    /// one open document (see <see cref="RunOnOpenActiveDocument"/>) instead of re-opening it.
    /// </summary>
    public bool ModifiesModel => _action.ModifiesModel;

    /// <summary>Per-run tracking log path (Datetime/Model/Status) — see <see cref="TrackingCsvLogger"/>.</summary>
    public string TrackingLogPath { get; }

    /// <summary>Persistent, append-across-runs detail/output log — see <see cref="DetailCsvLogger"/>.</summary>
    public string DetailLogPath { get; }

    /// <param name="logNameRun">Names the per-run tracking file (<c>&lt;Datetime&gt;_&lt;project&gt;_
    /// &lt;logNameRun&gt;.csv</c>): "Batch Run" for a Batch Run, the schedule name for a scheduled
    /// run, else the task's own name for a single-task run. Falls back to the action name when null.</param>
    /// <param name="taskIndex">1-based position of this task in the run (for the "k of N" Task label).</param>
    /// <param name="taskCount">Total number of tasks in the run; a "k of N" prefix is added only when &gt; 1.</param>
    /// <param name="runStamp">One timestamp for the whole run, shared by every pass so they all write to
    /// the SAME <c>Log\&lt;runStamp&gt;_…</c> tracking file. Defaults to now (fine for a single-pass run).</param>
    public BatchEngine(UIApplication uiApp, IBatchAction action, string destinationFolder, string projectLog,
        SyncMode syncMode = SyncMode.Sync, string? logNameRun = null, int taskIndex = 1, int taskCount = 1,
        DateTime? runStamp = null)
    {
        _uiApp = uiApp;
        _action = action;
        _destinationFolder = destinationFolder;
        _projectLog = projectLog;
        _syncMode = syncMode;
        _taskLabel = taskCount > 1 ? $"{taskIndex} of {taskCount} — {action.Name}" : action.Name;
        string runName = string.IsNullOrWhiteSpace(logNameRun) ? action.Name : logNameRun!;
        DateTime stamp = runStamp ?? DateTime.Now;
        _runStamp = stamp;
        _runTag = DetailCsvLogger.RunTag(stamp);
        TrackingLogPath = TrackingCsvLogger.PathFor(destinationFolder, projectLog, runName, stamp);
        DetailLogPath = DetailCsvLogger.PathFor(destinationFolder, projectLog, action.Name);
    }

    /// <summary>
    /// Opens the loggers and a fresh <see cref="BatchResult"/> for one run. Call once per
    /// preset, then drive <see cref="ProcessModel"/> over each model — either directly
    /// (headless, see <see cref="Run"/>) or one per Idling tick (<see cref="BatchIdlingDriver"/>).
    /// Also gives the action a chance to locate any sidecar output file
    /// (<see cref="IBatchAction.ConfigureOutput"/>) before the first model opens.
    /// </summary>
    public BatchRunState BeginRun()
    {
        // Before ConfigureOutput: a matrix action names its workbook from this, and would
        // otherwise stamp it with the second it finished writing. See IBatchAction.RunStamp.
        _action.RunStamp = _runStamp;
        _action.ConfigureOutput(_destinationFolder, _projectLog);
        return new()
        {
            Tracking = new TrackingCsvLogger(TrackingLogPath, _taskLabel),
            Detail = new DetailCsvLogger(DetailLogPath, _runTag)
        };
    }

    /// <summary>
    /// Called by the runner once the LAST model of this pass has been processed — lets the action
    /// write any run-level artifact (e.g. the Phase/Workset/Design-Option matrix from all the run's
    /// models). Fail-soft: a matrix write must never fail the batch.
    /// </summary>
    public void FinalizeRun()
    {
        // Fail-soft, but never silent: this swallowed the only signal that a run-level artifact
        // failed to write, so a matrix that never appeared looked exactly like one nobody asked
        // for. The detail/tracking logs still hold the data either way.
        try { _action.OnRunComplete(); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark($"{_action.Name}: OnRunComplete failed: {ex.Message}"); }

        // How often Revit ignored a workset request this instance. One line, and only when it
        // happened: the per-model rows say WHICH, this says HOW MANY, and the rate is the thing
        // nobody can currently quote — see ReopenWithRequiredWorksets.
        if (_worksetReopenRecoveries > 0)
        {
            try
            {
                Logging.RunNote.Write(TrackingLogPath, _taskLabel, "", "INFO",
                    $"{_worksetReopenRecoveries} model(s) in this Revit needed the workset reopen " +
                    "retried — Revit accepted the request only after being asked again. Each one is " +
                    "logged above with what it cost; the exports themselves are complete.");
            }
            catch { /* a count must never fail a finished run */ }
        }
    }

    /// <summary>
    /// Opens, runs the action over, (maybe) syncs, and closes-the-previous for a single
    /// model — the unit of work each Idling tick performs. Per-model try/catch so one bad
    /// model logs an ERROR row and the batch continues; <paramref name="state"/> carries the
    /// loggers, the previously-opened document (kept open until the next one activates,
    /// since Revit forbids closing the active document), and the running <see cref="BatchResult"/>.
    /// </summary>
    public void ProcessModel(CloudModel model, BatchRunState state)
    {
        // Before anything is opened. The same check guards ModelDocumentCursor.Run, the other path
        // that reaches OpenForModel — one decision (WrongVersionReason), consulted at every entry,
        // rather than a rule that happens to hold on the path someone tested.
        string? wrongVersion = WrongVersionReason(model);
        if (wrongVersion != null)
        {
            LogWrongVersion(model, state, wrongVersion);
            return;
        }

        Document? doc;
        try
        {
            doc = OpenForModel(model);
        }
        catch (Exception ex)
        {
            LogOpenFailure(model, state, ex);
            return;
        }

        try { RunOnOpenDocument(model, doc, state); }
        finally { CloseOpened(model, doc); }
    }

    /// <summary>
    /// Stage 1 of <see cref="ProcessModel"/>, split out so a model-major driver can open a model
    /// ONCE and let several consecutive passes run against that one document
    /// (<see cref="RunOnOpenDocument"/>) instead of re-opening it per action. Measured on the
    /// 98-model office run of 2026-07-27: six actions meant six opens per model, 576 opens in all,
    /// and those opens were 49% of the run's entire action time — by far the largest single cost.
    ///
    /// The caller owns the returned document and MUST pass it to <see cref="CloseOpened"/>.
    /// </summary>
    public Document OpenForModel(CloudModel model)
    {
        // Open mode is fixed by category, not chosen: a read-only Check opens detached
        // (read-only), a write Action stays attached so it can sync/save afterward.
        bool detached = !_action.ModifiesModel;

        // Open each model in the BACKGROUND (Application.OpenDocumentFile), never via
        // UIApplication.OpenAndActivateDocument. The batch is driven from an Idling event,
        // and activating/switching the UI's active document during API event handling is
        // forbidden — Revit throws "Switching active documents is not allowed during API
        // event handling" for every model after the first (the first only slips through
        // because Revit sits at the Home screen with no active document to switch away
        // from). A background-opened document never becomes the active document, so it
        // sidesteps that rule and can be closed as soon as it's processed. None of the
        // actions need an interactive UIDocument — they all operate on the Document.
        // Breadcrumb BEFORE each blocking stage: a cloud open can hang indefinitely or crash
        // natively, and this model's tracking row is only written at the very end, so a trail
        // that stops at "open" is the only evidence of which model wedged and where.
        // Published before the open so SelectWorksetsToOpen — which runs on the first, cheap,
        // worksets-closed document — can look this model up in the workset audit. A detached
        // document cannot say which cloud model it came from; only the engine knows.
        ModelContext.Set(model);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        // A non-workshared cloud model opens with no workset configuration — see CloudOpenFacts.
        (CloudOpenMode mode, string source) = CloudOpenFacts.PlanFor(model);
        string openStage = BatchStages.WithMode(
            detached ? BatchStages.OpenDetached : BatchStages.OpenAttached, mode, source);
        Trace(model, openStage);
        // Traced BEFORE the gate, deliberately: an instance waiting for a sibling is silent on every
        // liveness signal, and only the open stage on the trail keeps that silence from being read
        // as a stall (Schedule.ChildLiveness.IsOpening). Released when this method returns, so the
        // reopen below is covered too — it is another open, and the same extension runs again.
        using IDisposable openGate = Schedule.CloudOpenGate.Acquire(
            _action.SerialisesCloudOpens && Schedule.ChunkContext.GroupNumber > 0,
            _uiApp.Application.VersionNumber);
        // Again after the gate: its wait note is a trail line of its own, and a crash inside the open
        // must leave the OPEN as this pid's last line, or the supervisor cannot say which model died.
        if (_action.SerialisesCloudOpens && Schedule.ChunkContext.GroupNumber > 0) Trace(model, openStage);
        Document doc = OpenModel(model, detached, activate: false, mode: mode);
        // An action that must EDIT workset-owned elements (Internal Relink's LoadFrom) may
        // need specific worksets open — only settable at open time, so this closes and
        // reopens with exactly those worksets open. No-op for every other action.
        doc = ReopenWithRequiredWorksets(model, doc, detached, activate: false);
        // Includes any CloudOpenGate wait — that wait is open cost too, from the run's point of view.
        Trace(model, $"opened in {timer.ElapsedMilliseconds} ms");
        return doc;
    }

    /// <summary>
    /// Stage 2 of <see cref="ProcessModel"/>: run the action against an ALREADY-OPEN background
    /// document, persist per <see cref="SyncMode"/>, and write this model's tracking row. Opens
    /// nothing and closes nothing — the caller owns the document's lifetime.
    ///
    /// <paramref name="reusing"/> only labels the trail: true when this pass joined a document a
    /// previous pass of the same model opened, so a trail shows one open followed by several runs.
    /// Whether joining is legal at all is <see cref="CanJoinOpenDocument"/>'s decision.
    /// </summary>
    public void RunOnOpenDocument(CloudModel model, Document doc, BatchRunState state, bool reusing = false)
    {
        state.Tracking.CurrentModel = model.DisplayName;
        // Re-published here too: a pass that JOINS a document an earlier pass opened never went
        // through the open above, and would otherwise run under the previous model's identity.
        ModelContext.Set(model);
        string status;
        string message;
        try
        {
            Trace(model, reusing ? BatchStages.ActionRunReusing : BatchStages.ActionRun);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // One locked write for the whole model, not one per row — see DetailCsvLogger.LogMany.
            state.Detail.LogMany(model.DisplayName, _action.RunDetailRows(_uiApp, doc));
            Trace(model, $"ran in {timer.ElapsedMilliseconds} ms");

            Trace(model, "persist");
            string syncNote = Persist(doc);
            Trace(model, "persisted (" + syncNote + ")");
            status = "OK";
            // The action may contribute a one-line roll-up for this model (e.g. Isolate's
            // view counts); combine it with the persistence note so the tracking log's
            // Message cell reads as the original graphs' "general log". Read here, right after
            // RunDetailRows and while processing is one model per tick, so it's this model's.
            message = CombineMessage(_action.PerModelNote, syncNote);
            state.Result.SucceededCount++;
        }
        catch (Exception ex)
        {
            status = "ERROR";
            message = WithForeignDialog(ex);
            state.Result.FailedCount++;
            Trace(model, "FAILED: " + message);
        }

        state.Tracking.Log(new Dictionary<string, string> { ["Status"] = status, ["Message"] = message });
    }

    /// <summary>
    /// Stage 3 of <see cref="ProcessModel"/>: close a document <see cref="OpenForModel"/> returned
    /// and delete any temp local it made. Safe with null (an open that threw part-way may still
    /// have left a temp local behind, which is why the cleanup runs regardless).
    /// </summary>
    public void CloseOpened(CloudModel model, Document? doc)
    {
        Trace(model, "close");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        // Release any worksharing ownership BEFORE closing. A cloud model now opens attached
        // (DoNotDetach), so even a read-only Check holds a live central session; closing it
        // while it still "owns"/borrows anything is what left the model stuck open and forced a
        // Revit force-quit. A write Action already relinquished in Persist (sync+relinquish);
        // this is an idempotent no-op there. Detached models have no central and are skipped.
        RelinquishBeforeClose(doc);
        // Background docs are never the active document, so a model closes as soon as EVERY pass
        // that can share its open document has run. The "leave files open" disposition re-opens the
        // models with a window at the very end (see the driver / LeftOpenDocsActivator), so the
        // batch itself always closes as it goes.
        CloseWithoutSaving(doc);
        CleanupPendingLocal();
        Trace(model, $"closed in {timer.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Writes the ERROR tracking row for a model whose OPEN threw, and runs the same close/cleanup
    /// path the old single-method <see cref="ProcessModel"/> reached through its <c>finally</c> —
    /// a failed open can still have created a temp local copy.
    /// </summary>
    public void LogOpenFailure(CloudModel model, BatchRunState state, Exception ex)
    {
        state.Tracking.CurrentModel = model.DisplayName;

        // A wrong-version model is SKIPPED, not failed — even when we only find out by trying. A
        // cloud model has no header to read beforehand (see WrongVersionReason), so for those this
        // is the only place the mismatch can be recognised, and reporting it as an error would bury
        // the one fact the reader needs among genuine failures.
        if (IsWrongVersionOpenFailure(ex))
        {
            LogWrongVersion(model, state,
                "this Revit (" + _uiApp.Application.VersionNumber + ") cannot open it: " +
                FirstLine(ex.Message));
            return;
        }

        state.Result.FailedCount++;
        string message = AccessDeniedMessage(ex) ?? ex.Message;
        Trace(model, "FAILED: " + message);
        CloseOpened(model, null);
        state.Tracking.Log(new Dictionary<string, string> { ["Status"] = "ERROR", ["Message"] = message });
    }

    /// <summary>
    /// The message to log when the open was REFUSED, or null when it failed for some other reason.
    ///
    /// <para>Still an ERROR and still counted as a failure — the model genuinely did not run. What
    /// changes is that the row names the account. Until 1.5.0 the only exception this method
    /// classified was a wrong Revit version, so a model outside the signed-in account produced a
    /// row carrying the raw text of a <c>RevitServerUnauthorizedException</c> and nothing to
    /// suggest that switching Autodesk accounts was what broke the nightly run.</para>
    /// </summary>
    private static string? AccessDeniedMessage(Exception ex) =>
        Presets.AccessDenied.Is(ex)
            ? $"No access to this model as {Presets.AccessDenied.Account} — the signed-in Autodesk " +
              $"account cannot open it. ({FirstLine(ex.Message)})"
            : null;

    /// <summary>
    /// Why this Revit must not open <paramref name="model"/>, or null to run it as normal.
    ///
    /// THE RULE IS THAT UNKNOWN IS NEVER SKIPPED. Only a version positively read and positively
    /// different returns a reason. An unreadable header, a missing file, a model whose version
    /// cannot be established at all — every one of those runs exactly as it did before. A batch that
    /// silently skipped everything because a lookup broke would be far worse than one that tries and
    /// fails out loud, and that failure mode would be invisible in testing.
    ///
    /// LOCAL FILES ONLY, and deliberately so. <see cref="BasicFileInfo"/> reads the header of a file
    /// on disk without opening the model — free, exact, and already used a few lines into
    /// <see cref="OpenLocalModel"/> for IsWorkshared. A CLOUD model has no such header to read: its
    /// version is only knowable by asking Forma over the network (the Data Management version object
    /// carries it) or by trying to open it. Nothing here does the former, so a wrong-version cloud
    /// model is caught on the attempt instead — see <see cref="IsWrongVersionOpenFailure"/>.
    ///
    /// A MISMATCH IN EITHER DIRECTION SKIPS. A newer model cannot be opened at all; an older one
    /// would be UPGRADED by opening it, quietly and irreversibly, which is not something a batch may
    /// do to somebody's model without being asked.
    /// </summary>
    public string? WrongVersionReason(CloudModel model)
    {
        if (!model.IsLocal || string.IsNullOrWhiteSpace(model.LocalPath)) return null;

        try
        {
            if (!File.Exists(model.LocalPath)) return null;   // let the open report the real problem

            BasicFileInfo info = BasicFileInfo.Extract(model.LocalPath);
            if (info.IsSavedInCurrentVersion) return null;

            // Format is the major release the file was saved in ("2024"); the API offers no other
            // way to name it. Blank on an older header, so the message degrades rather than lying.
            string saved = string.IsNullOrWhiteSpace(info.Format) ? "another version" : info.Format;
            return $"saved in Revit {saved}, running Revit {_uiApp.Application.VersionNumber}";
        }
        catch
        {
            // An unreadable header is not evidence of a wrong version.
            return null;
        }
    }

    /// <summary>
    /// Whether a failed open is Revit refusing the file's version rather than a real fault. Matched
    /// on the message because the API raises this as a plain exception with no distinguishing type
    /// or error code to test — so this is kept narrow and additive, and anything it does not
    /// recognise stays an ERROR.
    /// </summary>
    private static bool IsWrongVersionOpenFailure(Exception ex)
    {
        string m = (ex.Message ?? "").ToLowerInvariant();
        return m.Contains("newer version") ||
               m.Contains("later version") ||
               m.Contains("created in a previous version") ||
               m.Contains("not a valid revit file version") ||
               (m.Contains("version") && m.Contains("cannot be opened"));
    }

    /// <summary>
    /// Records a model this Revit was the wrong version to run, and tidies up — mirroring
    /// <see cref="LogOpenFailure"/>, because a mismatch found on the attempt may still have left a
    /// temp local behind.
    ///
    /// LOGGED AS AN ERROR, NOT AS A QUIET SKIP. The model is not run either way; what changed is
    /// that it now SAYS SO where a run is watched from. A skip is a correct decision but an
    /// incomplete result — the models were still asked for and still have not been processed — and
    /// a batch that finishes "clean" while silently leaving some out is the one thing nobody can
    /// monitor. The Message carries the real reason so the run log distinguishes this from a
    /// genuine fault, and <see cref="BatchResult.SkippedCount"/> counts it separately for reporting.
    /// </summary>
    public void LogWrongVersion(CloudModel model, BatchRunState state, string reason)
    {
        state.Tracking.CurrentModel = model.DisplayName;
        state.Result.SkippedCount++;
        state.Result.FailedCount++;
        Trace(model, "SKIPPED as ERROR (wrong Revit version): " + reason);
        CloseOpened(model, null);
        state.Tracking.Log(new Dictionary<string, string>
        {
            ["Status"] = "ERROR",
            ["Message"] = "SKIPPED — not this Revit's version. " + reason
        });
    }

    private static string FirstLine(string? s) =>
        (s ?? "").Split('\n')[0].Trim();

    /// <summary>
    /// Whether THIS pass may run against <paramref name="doc"/>, a background document
    /// <paramref name="opener"/> opened for the same model, instead of closing it and opening the
    /// model again. Every condition below is about producing byte-identical output, not speed:
    ///
    /// <list type="bullet">
    /// <item>the document must still be usable;</item>
    /// <item><b>both passes must be read-only Checks.</b> A write pass currently sees the model
    /// freshly re-opened AFTER the previous pass synced — which also pulls other people's central
    /// changes. Reusing the in-memory document skips that pull, so a shared document could differ
    /// from what a re-open would have produced. A Check never syncs, so for Checks reuse is
    /// identical by construction. This is the fidelity rule; do not relax it without a measured
    /// re-open-vs-reuse comparison on write actions;</item>
    /// <item>neither pass may need the ACTIVE document — script sets keep the separate
    /// activated path (<see cref="OpenRunActivate"/> / <see cref="RunOnOpenActiveDocument"/>);</item>
    /// <item>this pass's workset needs must already be met by how the document was opened
    /// (<see cref="RequiredClosedWorksets"/> returning null), since open worksets can only be
    /// chosen at open time.</item>
    /// </list>
    /// </summary>
    public bool CanJoinOpenDocument(BatchEngine opener, Document? doc) =>
        CanJoin(
            docUsable: doc is not null && doc.IsValidObject,
            openerNeedsActive: opener.NeedsActiveDocument,
            joinerNeedsActive: NeedsActiveDocument,
            openerModifies: opener.ModifiesModel,
            joinerModifies: ModifiesModel,
            // Only meaningful when there IS a document; short-circuited otherwise so this never
            // touches a disposed one.
            worksetsSatisfied: doc is null || !doc.IsValidObject || RequiredClosedWorksets(doc) is null);

    /// <summary>
    /// <see cref="CanJoinOpenDocument"/>'s decision with the Revit objects taken out, so the rule
    /// that guarantees identical output can be tested without Revit (see <c>tools/LivenessSim</c>).
    /// Every argument is a veto: reuse needs all six to hold.
    /// </summary>
    internal static bool CanJoin(bool docUsable, bool openerNeedsActive, bool joinerNeedsActive,
        bool openerModifies, bool joinerModifies, bool worksetsSatisfied)
    {
        if (!docUsable) return false;
        if (openerNeedsActive || joinerNeedsActive) return false;
        if (openerModifies || joinerModifies) return false;
        return worksetsSatisfied;
    }
}
