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
public sealed class BatchEngine
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

    /// <summary>
    /// Interactive counterpart to <see cref="ProcessModel"/> that OPENS AND ACTIVATES the model
    /// (a real window) instead of a background open, then runs the action, persists, and logs the
    /// tracking row — but does NOT close the document. This is the modal message-loop pattern: the batch
    /// runs inside a modal dialog's message loop (see <c>UI.BatchRunnerWindow</c>), a top-level UI
    /// context where <c>OpenAndActivateDocument</c> is legal and Revit's cloud-worksharing UI can
    /// initialise. The <see cref="ProcessModel"/> background/Idling path made a cloud
    /// <c>DocumentOpening</c> throw "Can not subscribe to an event during execution of that event"
    /// and then hang or access-violate. Returns the opened document (or <c>null</c> on failure);
    /// the caller owns closing — deferred, because Revit forbids closing the <em>active</em>
    /// document, so the caller closes the PREVIOUS one via <see cref="CloseAfterInteractive"/> and
    /// leaves the last open (or, for "leave files open", never closes at all).
    /// </summary>
    public Document? OpenRunActivate(CloudModel model, BatchRunState state)
    {
        state.Tracking.CurrentModel = model.DisplayName;
        string status;
        string message;
        Document? doc = null;
        try
        {
            bool detached = !_action.ModifiesModel;

            // An ACTIVATED document can't be closed to change its open worksets (Revit forbids
            // closing the active document), so when the action needs specific worksets open the
            // discovery runs on a throwaway BACKGROUND open first, and the real activated open
            // then opens those worksets directly. Costs one extra fast open, only for such actions.
            IList<WorksetId>? worksetsToOpen = null;
            (CloudOpenMode mode, string source) = CloudOpenFacts.PlanFor(model);
            // A model opened plain has no worksets to discover, so the probe would only be a second
            // first-open of the very model that crashes on one.
            if ((_action.PlansWorksetOpen || _action.OpensAllWorksets) && mode != CloudOpenMode.Plain)
            {
                Trace(model, "probe open (background)");
                Document probe = OpenModel(model, detached, activate: false);
                try { worksetsToOpen = RequiredClosedWorksets(probe); }
                finally
                {
                    RelinquishBeforeClose(probe);
                    CloseWithoutSaving(probe);
                    CleanupPendingLocal(); // the probe's temp local, before the real open makes its own
                    Trace(model, "probe closed");
                }
            }

            // Named constants, not literals, for the same reason the background open uses them: the
            // supervisor reads this stage from another process to tell a slow open from a wedge.
            Trace(model, BatchStages.WithMode(
                detached ? BatchStages.OpenActivatedDetached : BatchStages.OpenActivatedAttached, mode, source));
            doc = OpenModel(model, detached, activate: true, worksetsToOpen, mode);
            Trace(model, "opened");

            // The close is deferred to the caller — re-key any temp local to the document so
            // CloseInteractive can delete it after that (much later) close.
            if (_pendingLocalCleanup != null)
            {
                _pendingLocalByDoc[doc] = _pendingLocalCleanup;
                _pendingLocalCleanup = null;
            }

            Trace(model, BatchStages.ActionRun);
            // One locked write for the whole model, not one per row — see DetailCsvLogger.LogMany.
            state.Detail.LogMany(model.DisplayName, _action.RunDetailRows(_uiApp, doc));
            Trace(model, "ran");

            Trace(model, "persist");
            string syncNote = Persist(doc);
            Trace(model, "persisted (" + syncNote + ")");
            // Unlike ProcessModel, this path has no close-time finally — the caller may leave
            // the document open indefinitely. So release worksharing ownership here for any
            // document Persist didn't sync (a Check, a dry run, or a failed sync): an open
            // attached model must never keep central elements borrowed. Idempotent after a sync.
            if (syncNote != "synced")
                RelinquishBeforeClose(doc);
            status = "OK";
            message = CombineMessage(_action.PerModelNote, syncNote);
            state.Result.SucceededCount++;
        }
        catch (Exception ex)
        {
            status = "ERROR";
            message = WithForeignDialog(ex);
            state.Result.FailedCount++;
            Trace(model, "FAILED: " + message);
            // The half-open document stays on screen for the user, but must not retain any
            // worksharing ownership it grabbed before failing.
            RelinquishBeforeClose(doc);
            doc = null; // don't hand back a half-open document for the caller to close.
        }

        state.Tracking.Log(new Dictionary<string, string> { ["Status"] = status, ["Message"] = message });
        return doc;
    }

    /// <summary>
    /// Runs this engine's action against a document ANOTHER (compatible) pass already opened and
    /// activated for the SAME model — the reuse path a model-major runner takes when consecutive
    /// activated passes (e.g. two script sets) target one model. Revit forbids opening an
    /// already-open model, so rather than re-open it, each such pass runs against the one open
    /// document. Runs the action, persists per <see cref="SyncMode"/>, relinquishes any ownership a
    /// non-synced pass borrowed, and logs the tracking row — but never opens or closes the document;
    /// the engine that opened it (via <see cref="OpenRunActivate"/>) still owns the deferred close.
    /// Only sound when the opening pass and this one share an open mode
    /// (<see cref="ModifiesModel"/>), which the caller checks.
    /// </summary>
    public void RunOnOpenActiveDocument(CloudModel model, Document doc, BatchRunState state)
    {
        state.Tracking.CurrentModel = model.DisplayName;
        string status;
        string message;
        try
        {
            Trace(model, BatchStages.ActionRunReusing);
            // One locked write for the whole model, not one per row — see DetailCsvLogger.LogMany.
            state.Detail.LogMany(model.DisplayName, _action.RunDetailRows(_uiApp, doc));
            Trace(model, "ran");

            Trace(model, "persist");
            string syncNote = Persist(doc);
            Trace(model, "persisted (" + syncNote + ")");
            // Same rule as OpenRunActivate: this document may stay open indefinitely, so release
            // any ownership a pass that didn't sync (a Check, dry run, or failed sync) still holds.
            if (syncNote != "synced")
                RelinquishBeforeClose(doc);
            status = "OK";
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
    /// Persistence rule the engine owns so no individual action has to know it: a read-only
    /// action (<see cref="IBatchAction.ModifiesModel"/> false, e.g. Multi Model Check) never
    /// saves or syncs — its transaction, if any, is read-only anyway, and it opens detached. A
    /// write-capable action (Multi Model Action) always opens attached, so it persists its
    /// changes: sync+relinquish when workshared, else a plain <see cref="Document.Save"/> (a
    /// non-workshared local model has no central to sync to) — unless the run's
    /// <see cref="SyncMode"/> is a dry run, in which case nothing is persisted and the close
    /// path discards the changes (ownership is still relinquished before close).
    /// </summary>
    private string Persist(Document doc)
    {
        if (!_action.ModifiesModel)
            return "";

        if (_syncMode == SyncMode.NoSync)
            return "dry run — changes discarded";

        if (doc.IsWorkshared)
        {
            try
            {
                var relinquish = new RelinquishOptions(true);
                var swc = new SynchronizeWithCentralOptions();
                swc.SetRelinquishOptions(relinquish);
                doc.SynchronizeWithCentral(new TransactWithCentralOptions(), swc);
                return "synced";
            }
            catch (Exception ex)
            {
                return "sync failed: " + ex.Message;
            }
        }

        try
        {
            doc.Save();
            return "saved";
        }
        catch (Exception ex)
        {
            return "save failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Appends the words of any modal dialog <see cref="ForeignDialogBreaker"/> had to answer while
    /// this model was being processed. The dialog is raised inside a blocking API call by another
    /// add-in, so the exception the action throws afterwards knows nothing about it — on
    /// 2026-08-15 that produced "Revit reported no error but no file appeared" when the exporter had
    /// in fact said "No suitable geometry found" on screen. Taken (and cleared) per model so one
    /// dialog is reported against one model.
    /// </summary>
    /// <summary>
    /// The overload the catch blocks use. Applies the account-refused wording before the
    /// foreign-dialog note, so the interactive paths and <see cref="LogOpenFailure"/> describe a
    /// denial identically — three call sites, one rule.
    /// </summary>
    private static string WithForeignDialog(Exception ex) =>
        WithForeignDialog(AccessDeniedMessage(ex) ?? ex.Message);

    private static string WithForeignDialog(string message)
    {
        string dialog = ForeignDialogBreaker.TakeLastMessage();
        return dialog.Length == 0 ? message : message + " — a dialog was answered for you: " + dialog;
    }

    /// <summary>Joins an action's per-model roll-up with the engine's persistence note into one Message cell, dropping either if empty so a plain Check still logs exactly what it did before.</summary>
    private static string CombineMessage(string actionNote, string syncNote)
    {
        if (string.IsNullOrEmpty(actionNote)) return syncNote;
        if (string.IsNullOrEmpty(syncNote)) return actionNote;
        return actionNote + " — " + syncNote;
    }

    private static string ParseRegion(string region) =>
        string.Equals(region, "EMEA", StringComparison.OrdinalIgnoreCase)
            ? ModelPathUtils.CloudRegionEMEA
            : ModelPathUtils.CloudRegionUS;

    /// <summary>
    /// The one place a model becomes a Document: local files route through
    /// <see cref="OpenLocalModel"/> (never opening a central directly), cloud models through
    /// <see cref="CloudOpenOptions"/> (never detached — Revit rejects detach for C4R/Forma).
    /// <paramref name="openWorksets"/>, when given, opens exactly those worksets (all others
    /// closed) — see <see cref="ReopenWithRequiredWorksets"/>.
    /// </summary>
    private Document OpenModel(CloudModel model, bool detached, bool activate,
        IList<WorksetId>? openWorksets = null, CloudOpenMode mode = CloudOpenMode.WorksetConfig)
    {
        Document doc;
        if (model.IsLocal)
        {
            if (!System.IO.File.Exists(model.LocalPath))
                throw new System.IO.FileNotFoundException($"Local model not found: {model.LocalPath}");
            doc = OpenLocalModel(model.LocalPath!, detached, activate, openWorksets);
        }
        else
        {
            ModelPath cloudPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                ParseRegion(model.Region), model.ProjectGuid, model.ModelGuid);
            // Plain = no SetOpenWorksetsConfiguration at all. On a non-workshared cloud model's first
            // open, a workset configuration access-violates inside Revit (CloudOpenFacts); a model that
            // turns out workshared after all is reopened with its worksets by ReopenWithRequiredWorksets.
            doc = Open(cloudPath, mode == CloudOpenMode.Plain ? new OpenOptions() : CloudOpenOptions(openWorksets), activate);
            CloudOpenFacts.RecordOpened(model, doc.IsWorkshared);
        }

        // When this model is already loaded as a LINK of an earlier model left open (the
        // leave-files-open disposition), Revit hands back that linked document instead of a real
        // primary open. A linked document is read-only and ignores open options (worksets can't be
        // opened), so a write action would fail with a misleading per-element error ("closed
        // workset") — fail the model up front with the actionable cause instead. Live-hit by
        // Internal Relink: host 1 relinks + stays open holding hosts 2/3 as loaded links.
        if (doc.IsLinked)
            throw new InvalidOperationException(
                $"'{doc.Title}' is currently loaded as a LINK of another open model, so it cannot " +
                "be opened for processing. Run with \"Close files\" (recommended for Internal " +
                "Relink), or close the model that links it, then run again.");
        return doc;
    }

    /// <summary>
    /// Honors <see cref="IBatchAction.SelectWorksetsToOpen"/> — and, since 1.3.8.7,
    /// <see cref="IBatchAction.OpensAllWorksets"/> — for a BACKGROUND-opened document: when worksets
    /// the action needs are currently closed, the document is closed and the same model reopened
    /// with those worksets NAMED. Open worksets can only be chosen at open time — the API cannot
    /// open or close one on an already-open document — so a reopen is the only mechanism.
    ///
    /// <para>For an all-worksets action this is a REPAIR, not a plan: the open already asked for
    /// <see cref="WorksetConfigurationOption.OpenAllWorksets"/> and Revit ignored it (measured
    /// 2026-08-15, five models, 0 of 26/64/45/20/25 open). Naming the ids is the form that works.</para>
    ///
    /// Returns the document to keep using (the original when nothing is needed), and throws only
    /// after every rung of the ladder below has been refused.
    ///
    /// <para><b>THE BY-NAME OPEN IS A BETTER BET, NOT A GUARANTEE</b> (1.3.8.10). MEASURED
    /// 2026-08-15: <c>SITE2A-BLDA-STR</c> asked for the same 13 of its 58 worksets twice, 45 minutes
    /// apart — <c>reopened (13 of 58 …)</c> at 21:37 and <c>reopened (0 of 58 …)</c> at 22:38. The
    /// refused open was also FASTER (26 s against 87 s), which is what an open that never applied the
    /// configuration looks like, and its journal records no error of any kind. So
    /// <see cref="WorksetConfiguration.Open"/> is intermittently ignored on a background cloud open,
    /// exactly as <see cref="WorksetConfigurationOption.OpenAllWorksets"/> is — just rarely instead of
    /// always. One model in six, on that run.</para>
    ///
    /// <para>Hence a ladder rather than a verdict: retry the same set (the refusal is transient), then
    /// fall back to every workset (the 1.3.8.7 path, proven on these models), and only then fail. The
    /// fallback is expensive — with links unwanted it re-loads the link tree this release exists to
    /// avoid — so it is the last rung and it is announced, never silent.</para>
    /// </summary>
    private Document ReopenWithRequiredWorksets(CloudModel model, Document doc, bool detached, bool activate)
    {
        IList<WorksetId>? required = RequiredClosedWorksets(doc);
        if (required is null)
        {
            // Nothing to reopen. Two different reasons, and only one of them is an opportunity: this
            // action never wanted worksets open (nothing to record), or it did and Revit honoured
            // the cheap first request (so they ARE open, and this is the same moment the reopen path
            // below records at). Missing this case would leave every model whose first open works
            // permanently without a record.
            if (_action.OpensAllWorksets || _action.PlansWorksetOpen)
                WorksetContentsProbe.Record(doc);
            return doc;
        }

        // Read the full workset list from the document that is still OPEN — it is the fallback rung's
        // request, and after the close below there is nothing left to ask.
        List<WorksetId> all = AllUserWorksets(doc);

        // A SECOND blocking open of the same model — traced separately so a stall here is never
        // mistaken for the first open.
        Trace(model, $"{BatchStages.ReopenForWorksets}{required.Count} workset(s)");
        RelinquishBeforeClose(doc);
        CloseWithoutSaving(doc);
        CleanupPendingLocal(); // a local write-Action's first temp copy; the reopen makes a fresh one

        Document? reopened;
        string? refused = null;

        for (int attempt = 1; attempt <= ReopenLadder.Attempts; attempt++)
        {
            // Rung 3 widens the request to every user workset. Rungs 1 and 2 ask for the same set:
            // the evidence is that a refusal is transient, and a second cheap open beats loading a
            // federated link tree to work around a blip.
            bool widen = ReopenLadder.WidensToAll(attempt) && all.Count > 0;
            IList<WorksetId> asking = widen ? all : required;

            reopened = OpenModel(model, detached, activate, asking);

            int open = OpenUserWorksets(reopened).Count;
            int total = AllUserWorksets(reopened).Count;
            Trace(model, $"reopened ({open} of {total} workset(s) open, {asking.Count} asked for)");

            refused = WorksetOpenFailure(reopened, asking);
            if (refused == null)
            {
                if (attempt > 1)
                {
                    // Loud on purpose. A recovery nobody can see is how a rare fault stays
                    // un-measured, and the rate is the thing we do not yet know.
                    _worksetReopenRecoveries++;
                    string how = widen
                        ? $"opened all {open} workset(s) instead"
                        : $"the same {asking.Count} workset(s) opened on attempt {attempt}";
                    Trace(model, $"reopen recovered after {attempt - 1} refusal(s) — {how}");
                    // A row of its own, not just a breadcrumb: this is the only place the RATE of
                    // Revit's intermittent refusal can be read without opening a journal.
                    Logging.RunNote.Write(TrackingLogPath, _taskLabel, model.DisplayName, "INFO",
                        $"Revit ignored the workset request {attempt - 1} time(s); the reopen was " +
                        $"retried and {how}. The export is complete — it cost {attempt - 1} extra " +
                        "open(s).");
                }

                // The one moment in a run when a model's worksets are KNOWN to be open, which is the
                // only moment the question "does this workset hold nothing but links?" can be
                // answered — a closed workset loads no elements and would answer "yes" for every
                // workset in the model. Recorded for the NEXT run to use; costs one collector pass.
                //
                // Here rather than inside an action because two actions consume this evidence
                // (Export NWC's view-derived plan and, since 1.4.3, Batch Interop's) and only one
                // used to produce it. See WorksetContentsProbe.
                WorksetContentsProbe.Record(reopened);

                return reopened;
            }

            // Refused. Close before trying again — two open copies of a gigabyte model is exactly
            // the memory pressure that killed an instance on 2026-08-15.
            Trace(model, $"reopen refused (attempt {attempt} of {ReopenLadder.Attempts}): " +
                         $"{open} of {total} open, {asking.Count} asked for" +
                         (ReopenLadder.CanRetry(attempt) ? " — retrying" : ""));
            RelinquishBeforeClose(reopened);
            CloseWithoutSaving(reopened);
            CleanupPendingLocal();
            reopened = null;
        }

        // Every rung refused. Failing here — with the document closed — costs one model and says why;
        // carrying on costs an export that silently contains nothing, which is the failure this whole
        // sequence started with.
        throw new InvalidOperationException(
            refused + $" Retried {ReopenLadder.Attempts} times, the last asking for every workset.");
    }

    /// <summary>Count of reopens that needed a retry this run — reported at the end so the RATE of
    /// Revit's intermittent refusal becomes measurable instead of anecdotal.</summary>
    private int _worksetReopenRecoveries;

    /// <summary>
    /// The worksets the action wants open in <paramref name="doc"/> when at least one of them is
    /// currently CLOSED; null when no reopen is needed (action doesn't plan workset opens, doc not
    /// workshared, nothing wanted, or everything wanted is already open). Fail-soft: a selection
    /// error just runs the action against the doc as-is — its own per-item errors will surface.
    /// </summary>
    private IList<WorksetId>? RequiredClosedWorksets(Document doc)
    {
        if (!doc.IsWorkshared) return null;

        ICollection<WorksetId>? wanted;
        if (_action.OpensAllWorksets)
        {
            // EVERY user workset, by id. MEASURED 2026-08-15: five models opened with
            // WorksetConfigurationOption.OpenAllWorksets reported 0 of 26, 0 of 64, 0 of 45, 0 of 20
            // and 0 of 25 worksets open — Revit simply did not honour the request on a background
            // cloud open, so no model element was loaded and every export found nothing to write.
            // Naming them explicitly goes through WorksetConfiguration.Open(ids), which Internal
            // Relink has used on live cloud models since 0.9.1. The all-worksets request above stays
            // as the cheap first attempt: when it does work, this finds nothing closed and no reopen
            // happens.
            wanted = AllUserWorksets(doc);
        }
        else
        {
            if (!_action.PlansWorksetOpen) return null;
            try { wanted = _action.SelectWorksetsToOpen(doc); }
            catch { return null; }
        }
        if (wanted is null || wanted.Count == 0) return null;

        var open = new HashSet<WorksetId>(OpenUserWorksets(doc));
        return wanted.Any(id => !open.Contains(id)) ? wanted.ToList() : null;
    }

    private static List<WorksetId> AllUserWorksets(Document doc)
    {
        try
        {
            return new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .Select(w => w.Id).ToList();
        }
        catch { return new List<WorksetId>(); }
    }

    private static List<WorksetId> OpenUserWorksets(Document doc)
    {
        try
        {
            return new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .Where(w => w.IsOpen).Select(w => w.Id).ToList();
        }
        catch { return new List<WorksetId>(); }
    }

    /// <summary>
    /// After the reopen, is the document actually usable by the action that asked for those worksets?
    /// Null when it is; otherwise the sentence to fail the model with.
    ///
    /// <para>An action that reads elements cannot do its job with the worksets shut, and the failure
    /// is silent by nature — the model looks empty rather than broken, which is how Export NWC came
    /// to write nothing for three releases while reporting success. Better to say "0 of 26 worksets
    /// opened" than to let a third-party dialog explain it.</para>
    ///
    /// <para><b>Since 1.3.8.8 this covers a PLANNED open too</b>, not just an all-worksets one.
    /// Export NWC now derives its set from the export views, so the guard 1.3.8.7 added would have
    /// been switched off with the flag it was attached to — and the thing being guarded against, an
    /// open that quietly yields nothing, is if anything more likely when a smaller set is asked for.
    /// The test is "did NONE of what I asked for open", which is a refusal; a partial result is a
    /// legitimate answer (a workset can be deleted or renamed between the two opens) and is reported
    /// in the row rather than failed.</para>
    /// </summary>
    private string? WorksetOpenFailure(Document doc, IList<WorksetId> asked)
    {
        if (!doc.IsWorkshared) return null;
        if (!_action.OpensAllWorksets && !_action.PlansWorksetOpen) return null;

        int total = AllUserWorksets(doc).Count;
        if (total == 0 || asked.Count == 0) return null;

        // The verdict itself lives in ReopenLadder, Revit-free, so the harness checks the same rule
        // the engine runs — including that a PARTIAL open is not a refusal.
        var open = new HashSet<WorksetId>(OpenUserWorksets(doc));
        if (!ReopenLadder.Refused(open.Count, asked.Count(open.Contains))) return null;

        return _action.OpensAllWorksets
            ? $"'{doc.Title}' opened with 0 of {total} worksets open, so none of its model " +
              "elements are loaded and there is nothing for this action to read. Revit refused " +
              "both the all-worksets open and a reopen naming every workset."
            : $"'{doc.Title}' was reopened naming {asked.Count} of its {total} worksets and Revit " +
              $"opened none of them, so the elements this action needs are not loaded. This is a " +
              "refusal, not an empty model — running on would read nothing and report success.";
    }

    /// <summary>
    /// Opens a local .rvt without ever opening a workshared CENTRAL directly (which would lock
    /// the central and hold it open — the reported bug). A non-workshared file has no central, so
    /// it opens with the plain overload. For a workshared file: a read-only Check <b>detaches</b>
    /// — a throwaway copy, never the central — while a write Action opens a fresh <b>local copy</b>
    /// made from the central (<see cref="WorksharingUtils.CreateNewLocal"/>) and works on that, so
    /// its edits sync back through the local (Revit worksharing best practice) instead of editing
    /// the central directly. If the file turns out to already be a local (not a central), it's
    /// opened directly.
    /// </summary>
    private Document OpenLocalModel(string path, bool detached, bool activate = false,
        IList<WorksetId>? openWorksets = null)
    {
        bool workshared;
        try { workshared = BasicFileInfo.Extract(path).IsWorkshared; }
        catch { workshared = false; } // unreadable header → treat as a plain, non-central file

        if (!workshared)
            return activate
                ? _uiApp.OpenAndActivateDocument(path).Document
                : _uiApp.Application.OpenDocumentFile(path);

        ModelPath central = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);

        // Read-only Check: detached throwaway copy, never the central.
        if (detached)
            return Open(central, OpenOptionsFor(detached: true, openWorksets), activate);

        // Write Action: work on a fresh local copy of the central, never the central itself.
        string localCopy = Path.Combine(
            Path.GetTempPath(), "KhBatchRunner", "locals",
            Path.GetFileNameWithoutExtension(path) + "_" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localCopy)!);
            WorksharingUtils.CreateNewLocal(central, ModelPathUtils.ConvertUserVisiblePathToModelPath(localCopy));
        }
        catch
        {
            // Not a central (already a local copy, or CreateNewLocal not applicable): open the
            // file directly attached — it's already a local, so this doesn't touch a central.
            return Open(central, OpenOptionsFor(detached: false, openWorksets), activate);
        }
        _pendingLocalCleanup = localCopy;
        return Open(ModelPathUtils.ConvertUserVisiblePathToModelPath(localCopy), OpenOptionsFor(detached: false, openWorksets), activate);
    }

    /// <summary>Opens a model path either in the background (<c>OpenDocumentFile</c>, headless/Idling path) or activated with a window (<c>OpenAndActivateDocument</c>, the interactive modal-loop path — see <see cref="OpenRunActivate"/>).</summary>
    private Document Open(ModelPath path, OpenOptions opts, bool activate) =>
        activate
            ? _uiApp.OpenAndActivateDocument(path, opts, false).Document
            : _uiApp.Application.OpenDocumentFile(path, opts);

    /// <summary>
    /// Closes a document previously returned by <see cref="OpenRunActivate"/> and deletes the
    /// temp local copy a write action was given, if any. The caller must have activated another
    /// document first — Revit cannot close the active document, which is why a close-as-you-go
    /// activated run always closes the PREVIOUS model and leaves the very last one open.
    /// </summary>
    public void CloseInteractive(Document? doc)
    {
        if (doc == null) return;
        RelinquishBeforeClose(doc); // idempotent — OpenRunActivate already released unsynced docs
        string? local = null;
        if (_pendingLocalByDoc.TryGetValue(doc, out string? pending))
        {
            local = pending;
            _pendingLocalByDoc.Remove(doc);
        }
        CloseWithoutSaving(doc);
        if (local != null)
        {
            try { File.Delete(local); } catch { /* temp file — best effort */ }
        }
    }

    /// <summary>Deletes the temp local copy (if any) once its document has been closed.</summary>
    private void CleanupPendingLocal()
    {
        string? local = _pendingLocalCleanup;
        _pendingLocalCleanup = null;
        if (local == null) return;
        try { File.Delete(local); } catch { /* temp file — best effort */ }
    }

    /// <summary>
    /// Open options for a CLOUD (C4R/Forma) model: never detach — Revit rejects a detach option on a
    /// cloud model ("Detach option is not valid for cloud model") — so leave the default
    /// <see cref="DetachFromCentralOption.DoNotDetach"/> and only close the worksets. A read-only
    /// Check doesn't persist; a write Action syncs to the cloud central in <see cref="Persist"/>.
    /// </summary>
    private OpenOptions CloudOpenOptions(IList<WorksetId>? openWorksets = null)
    {
        var opts = new OpenOptions(); // DetachFromCentralOption defaults to DoNotDetach
        opts.SetOpenWorksetsConfiguration(WorksetsFor(openWorksets));
        return opts;
    }

    private OpenOptions OpenOptionsFor(bool detached, IList<WorksetId>? openWorksets = null)
    {
        var opts = new OpenOptions
        {
            DetachFromCentralOption = detached
                ? DetachFromCentralOption.DetachAndPreserveWorksets
                : DetachFromCentralOption.DoNotDetach
        };
        opts.SetOpenWorksetsConfiguration(WorksetsFor(openWorksets));
        return opts;
    }

    /// <summary>
    /// The workset configuration every open uses. All worksets CLOSED (the engine's fast default)
    /// except <paramref name="openWorksets"/>, the ones an action declared it must edit in — unless
    /// the action reads model elements (<see cref="IBatchAction.OpensAllWorksets"/>), in which case
    /// every workset is opened up front, because an element in a closed workset is not loaded and
    /// the action would silently see an empty model.
    ///
    /// <para>The two are never combined: opening everything already satisfies any specific
    /// request, and <see cref="RequiredClosedWorksets"/> then finds nothing closed to reopen for.</para>
    /// </summary>
    private WorksetConfiguration WorksetsFor(IList<WorksetId>? openWorksets)
    {
        if (_action.OpensAllWorksets)
            return new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);

        var config = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
        if (openWorksets is { Count: > 0 })
            config.Open(openWorksets);
        return config;
    }

    /// <summary>
    /// Relinquish all worksharing ownership so a model never stays "checked out" and blocks its own
    /// close (the reported "cloud model won't close, must force-quit Revit"). Safe/idempotent: a
    /// write Action already relinquished via sync in <see cref="Persist"/>; a read-only Check on an
    /// attached cloud model releases whatever it borrowed; a detached/non-workshared model has no
    /// central and the call is skipped or harmlessly ignored.
    /// </summary>
    private static void RelinquishBeforeClose(Document? doc)
    {
        try
        {
            if (doc is null || !doc.IsValidObject || !doc.IsWorkshared) return;
            WorksharingUtils.RelinquishOwnership(
                doc, new RelinquishOptions(true), new TransactWithCentralOptions());
        }
        catch
        {
            // Detached copy (no central), nothing borrowed, or not connected — safe to ignore; the
            // close below still proceeds.
        }
    }

    /// <summary>
    /// One breadcrumb line for a stage of this action against one model — see
    /// <see cref="Diagnostics.RunTrace"/>. Every blocking stage is bracketed (a "start" mark and a
    /// completion mark) so that when a run wedges, the trail's last line names the model AND the
    /// phase; the tracking CSV can't, because its row is only written once the model finishes.
    /// </summary>
    private void Trace(CloudModel model, string stage) =>
        Diagnostics.RunTrace.Mark($"{_action.Name} | {model.DisplayName} | {stage}");

    private static void CloseWithoutSaving(Document? doc)
    {
        // Close the just-processed background document to free memory before the next one opens.
        // Any persistence (sync/save) already happened in Persist; this closes without saving.
        try
        {
            if (doc != null && doc.IsValidObject)
                doc.Close(false);
        }
        catch
        {
            // Best-effort: if Revit refuses to close it, don't let that abort the batch — the
            // process is short-lived (headless quits; interactive frees it on the next open).
        }
    }
}
