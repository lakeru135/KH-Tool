using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KhBatchRunner.Presets;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Batch;

/// <summary>
/// Drives one or more <see cref="BatchEngine"/> passes — each its own action over its own
/// preset — one model per Idling tick instead of inline inside the command that started it.
/// <c>OpenAndActivateDocument</c> needs a UI/idle context with no open transaction; calling
/// it directly from inside an executing <c>IExternalCommand.Execute</c> corrupts that
/// command's journal/undo scope — which is exactly what used to wedge Revit (force-close
/// required afterwards) once a batch finished. The fix is for the command to arm this
/// driver and return immediately, so by the time any model opens, the command that opened
/// it has already exited cleanly and <see cref="Autodesk.Revit.UI.UIApplication.Idling"/> is
/// a legal calling context. A "Batch Run"/"Schedule Run" job that checks several actions
/// runs them as a queue of passes here (one <see cref="BatchEngine"/> per action, since each
/// engine names its own log files) instead of each command running its own engine
/// synchronously — that second, per-command synchronous path was the same wedge reached
/// from "Run now".
/// </summary>
public static class BatchIdlingDriver
{
    private static bool _running;
    private static DialogSuppressor? _suppressor;
    private static IDisposable? _dialogBreaker;
    private static UIApplication? _uiApp;
    private static IReadOnlyList<(BatchEngine Engine, ModelPreset Preset)>? _passes;
    private static BatchRunState[]? _states;   // one per pass, begun up front (model-major run)
    private static int _modelIndex;            // outer cursor: which model
    private static int _passIndex;             // inner cursor: which pass within the current model
    private static int _modelCount;
    private static string _title = "";
    private static Action? _onComplete;
    private static BatchCompletion _completion = BatchCompletion.CloseDocuments;
    private static readonly List<PassSummary> _completed = new();
    private static string? _passError;
    private static EventHandler<IdlingEventArgs>? _handler;
    private static Action? _onSurfaced;
    // The one open document shared by the current model's passes — see ModelDocumentCursor.
    private static readonly ModelDocumentCursor _cursor = new();

    /// <summary>One finished pass's action/preset name plus its aggregate result and log paths — what the final summary dialog lists per pass.</summary>
    public readonly record struct PassSummary(
        string ActionName, string PresetName, BatchResult Result, string TrackingLogPath, string DetailLogPath);

    /// <summary>
    /// Backward-compatible single-pass entry point (Multi Model Check/Action buttons, which
    /// only ever run one action over one preset). Delegates to the multi-pass overload with
    /// a one-item queue.
    /// </summary>
    public static bool TryStart(UIApplication uiApp, BatchEngine engine, ModelPreset preset, string title,
        BatchCompletion completion = BatchCompletion.CloseDocuments) =>
        TryStart(uiApp, new[] { (engine, preset) }, title, completion: completion);

    /// <summary>
    /// Arms the Idling loop for a queue of <paramref name="passes"/>, run MODEL-MAJOR: each model
    /// is taken through every pass (one open per tick) before the next model starts. Returns
    /// <c>false</c> (and starts nothing) if a batch is already in flight, so the caller can tell the
    /// user to wait instead of starting a second run that would interleave with the first.
    /// </summary>
    /// <param name="onComplete">
    /// Optional callback invoked exactly once, when the whole queue ends — whether it drained
    /// normally or stopped early on a mid-queue failure. Schedule Run's "Run now" uses this to
    /// stamp <c>job.LastRunAt</c> only when the driven run actually finishes, not when it was
    /// merely armed.
    /// </param>
    /// <param name="onSurfaced">
    /// Optional callback threaded through to <see cref="LeftOpenDocsActivator.Surface"/> when
    /// <paramref name="completion"/> is <see cref="BatchCompletion.LeaveOpen"/> — invoked once the
    /// re-opened models have all been surfaced. <c>null</c> for every caller except an automated
    /// test that needs to know when it's safe to inspect the re-opened documents.
    /// </param>
    /// <exception cref="Exception">
    /// Rethrows whatever any pass's <see cref="BatchEngine.BeginRun"/> throws (e.g. the destination
    /// folder is an unreachable share or permission-denied — it's a free-text box, only checked for
    /// non-empty). All passes are begun up front (<see cref="BeginAllPasses"/>) before the first
    /// model opens and before the handler subscribes, so such a failure means nothing has run yet.
    /// Before rethrowing, all static state is reset via <see cref="Finish"/> so the failure can't
    /// leave the "already running" guard stuck — without that, every later click would silently
    /// report "already running" until Revit restarts. The caller should show the message to the user.
    /// </exception>
    public static bool TryStart(
        UIApplication uiApp, IReadOnlyList<(BatchEngine Engine, ModelPreset Preset)> passes,
        string title, Action? onComplete = null, BatchCompletion completion = BatchCompletion.CloseDocuments,
        Action? onSurfaced = null)
    {
        if (_running)
            return false;
        if (passes.Count == 0)
            return false; // nothing to run — caller decides what an empty queue means to the user.

        _running = true;
        try
        {
            _uiApp = uiApp;
            _passes = passes;
            _title = title;
            _onComplete = onComplete;
            _completion = completion;
            _onSurfaced = onSurfaced;
            _completed.Clear();
            _passError = null;

            // Auto-dismiss any Revit pop-up (upgrade/missing-link/warning dialogs) for the whole
            // run so it never blocks waiting for a click; disposed in Finish, before the summary.
            _suppressor = new DialogSuppressor(uiApp);
            // The other half: a modal box raised by a third-party add-in, which DialogBoxShowing
            // never sees and which stops a headless run dead. Disposed in Finish alongside the
            // suppressor — unless it is the process-wide one an unattended session already started
            // at App.OnStartup, which ForBatch lends rather than hands over. See
            // ForeignDialogBreaker.StartShared for why that one exists.
            _dialogBreaker = ForeignDialogBreaker.ForBatch();

            BeginAllPasses(); // may throw — same contract as the old single-engine TryStart.

            _handler = OnIdling;
            uiApp.Idling += _handler;
            return true;
        }
        catch
        {
            Finish();
            throw;
        }
    }

    /// <summary>
    /// Opens every pass's loggers up front and resets the model-major cursors. All passes are begun
    /// before the first model opens because a model-major run interleaves them (one model through
    /// every pass, then the next), so each pass's loggers must stay open for the whole run. A pass's
    /// <see cref="BatchEngine.BeginRun"/> may throw (e.g. an unreachable destination folder) — the
    /// <see cref="TryStart"/> contract, which resets state and rethrows before the handler subscribes.
    /// </summary>
    private static void BeginAllPasses()
    {
        _states = _passes!.Select(p => p.Engine.BeginRun()).ToArray();
        _modelCount = _passes!.Max(p => p.Preset.Models.Count);
        _modelIndex = 0;
        _passIndex = 0;
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        try
        {
            // Model-major: one (model, pass) unit per tick — all passes for the current model, then
            // advance to the next model. At most one open per tick, and usually none: the cursor
            // keeps the model's document open across its passes, so only the FIRST pass of a model
            // opens anything (see ModelDocumentCursor). We still deliberately do NOT call
            // e.SetRaiseWithoutDelay() — a full idle gap around each open is the point; it's what
            // keeps every open in a genuinely idle context. The cursor try/catches per model, so
            // anything reaching the outer catch is unexpected.
            if (_modelIndex < _modelCount)
            {
                (BatchEngine Engine, ModelPreset Preset) pass = _passes![_passIndex];
                if (_modelIndex < pass.Preset.Models.Count)
                    _cursor.Run(pass.Engine, pass.Preset.Models[_modelIndex], _states![_passIndex]);

                _passIndex++;
                if (_passIndex >= _passes.Count)   // this model ran every pass — next model
                {
                    _passIndex = 0;
                    _modelIndex++;
                    _cursor.Release();             // nothing else will run against this model
                }
                return;
            }

            // Every model ran every pass — record each pass's summary (pass order), then finish.
            for (int p = 0; p < _passes!.Count; p++)
                _completed.Add(new PassSummary(
                    _passes[p].Engine.ActionName, _passes[p].Preset.Name, _states![p].Result,
                    _passes[p].Engine.TrackingLogPath, _passes[p].Engine.DetailLogPath));
            Complete();
        }
        catch (Exception ex)
        {
            _passError = "Batch run failed: " + ex.Message;
            Complete();
        }
    }

    /// <summary>
    /// Single exit path once the queue is done (drained, stopped on an error, or an unexpected
    /// exception) — snapshots what the disposition needs, <see cref="Finish"/>es, calls the
    /// completion callback, then finishes per the chosen <see cref="BatchCompletion"/>.
    ///
    /// <b>No modal summary on the normal flow</b> — this mirrors the headless
    /// <c>ScheduledRunner</c>, which never pops a dialog: "Close files" ends silently (every model
    /// was already closed as it was processed), "Leave files open" surfaces the re-opened models
    /// (those windows ARE the result the user asked to see), and "Close Revit" quits. The summary
    /// <see cref="TaskDialog"/> now appears ONLY when a "Close files" run actually failed — there's
    /// no left-open window or quit to hint at a problem otherwise, so an error must not be swallowed.
    /// Two reasons the old always-modal behaviour was wrong: (1) it was shown BEFORE the disposition,
    /// so "Close Revit when complete" waited for a click before quitting (never truly unattended);
    /// and (2) for "Leave files open" a modal shown here can be dismissed while the surfacing
    /// ExternalEvent fires inside the modal's own message loop — opening a document while a dialog is
    /// up. Surfacing with no modal avoids both.
    /// </summary>
    private static void Complete()
    {
        var completed = new List<PassSummary>(_completed);
        string? error = _passError;
        Action? onComplete = _onComplete;
        BatchCompletion completion = _completion;
        Action? onSurfaced = _onSurfaced;
        // Capture the batch's models before Finish() clears the passes — "leave files open"
        // re-opens them (with a window) at the very end.
        var surfaceModels = completion == BatchCompletion.LeaveOpen && _passes != null
            ? _passes.SelectMany(p => p.Preset.Models).ToList()
            : null;
        string title = _title;

        Finish();
        onComplete?.Invoke();

        switch (completion)
        {
            case BatchCompletion.CloseRevit:
                // Every model is already closed. Request Revit's normal close path after this
                // Idling callback returns; never terminate the process if shutdown is blocked.
                Schedule.RevitShutdown.RequestCurrent("interactive batch complete");
                break;

            case BatchCompletion.LeaveOpen when surfaceModels is { Count: > 0 }:
                // Re-open the models with a window (via an ExternalEvent — activating documents is
                // illegal in this Idling context) so the user sees them directly. No summary pop-up.
                LeftOpenDocsActivator.Surface(surfaceModels, onSurfaced);
                break;

            case BatchCompletion.LeaveOpen:
                // Leave-open selected but nothing to surface (empty preset / all filtered out) —
                // still fire the callback so anything awaiting the surface can't hang.
                onSurfaced?.Invoke();
                break;

            default: // CloseDocuments — "Close files": nothing is left open.
                // Finish silently on success; only surface the summary if something failed, so an
                // error is never swallowed (there's no open window or quit to signal it otherwise).
                if (error != null || completed.Any(p => p.Result.FailedCount > 0))
                    ShowSummary(title, completed, error);
                break;
        }
    }

    private static void ShowSummary(string title, List<PassSummary> completed, string? error)
    {
        int succeeded = completed.Sum(p => p.Result.SucceededCount);
        int failed = completed.Sum(p => p.Result.FailedCount);

        var lines = new List<string> { $"Succeeded: {succeeded}", $"Failed: {failed}", "" };
        foreach (PassSummary p in completed)
        {
            lines.Add($"{p.ActionName} — {p.PresetName}: {p.Result.SucceededCount} ok / {p.Result.FailedCount} failed");
            lines.Add($"  Tracking log: {p.TrackingLogPath}");
            lines.Add($"  Detail log:   {p.DetailLogPath}");
        }
        if (error != null)
            lines.Add($"\n{error}");

        TaskDialog.Show("KH Batch Script Runner", $"{title}\n\n" + string.Join("\n", lines));
    }

    /// <summary>
    /// Single exit path for every branch that ends a run — drained, a mid-queue failure, an
    /// unexpected exception in <see cref="OnIdling"/>, or a failed <see cref="TryStart"/>
    /// before the handler was even subscribed: unsubscribes (using the <see cref="_uiApp"/>
    /// stored at start, not the event's <c>sender</c>) if there is anything to unsubscribe,
    /// then clears every static per-run field and drops the guard. As long as every exit path
    /// calls this — which is the invariant the try/catch blocks above exist to guarantee — the
    /// guard can't get stuck and no handler is left dangling.
    /// </summary>
    private static void Finish()
    {
        // Before anything else: the run may be ending mid-model (an error, or the last model just
        // finished its passes), and a document held by the cursor would otherwise stay open for the
        // life of the process — including under "leave files open", which re-opens the models
        // itself and must not find one already open.
        _cursor.Release();

        if (_uiApp != null && _handler != null)
            _uiApp.Idling -= _handler;

        _suppressor?.Dispose();
        _suppressor = null;
        _dialogBreaker?.Dispose();
        _dialogBreaker = null;

        _uiApp = null;
        _passes = null;
        _states = null;
        _modelIndex = 0;
        _passIndex = 0;
        _modelCount = 0;
        _title = "";
        _onComplete = null;
        _completion = BatchCompletion.CloseDocuments;
        _onSurfaced = null;
        _completed.Clear();
        _passError = null;
        _handler = null;
        _running = false;
    }
}
