using System.Drawing;
using System.Text;
using Autodesk.Revit.UI;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

// WinForms, WPF, and the Revit API all define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
// Autodesk.Revit.UI also defines TextBox (a ribbon control); the log panel wants the WinForms one.
using TextBox = System.Windows.Forms.TextBox;
// Disambiguated from System.Diagnostics, which this file also uses (Process).
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.UI;

/// <summary>
/// Modal progress dialog that runs an interactive batch <b>inside its own <see cref="ShowDialog()"/>
/// message loop</b>. The command that opens this window is suspended in
/// <c>ShowDialog</c> while the batch runs, so each model is opened from a top-level UI message loop
/// rather than from a <c>UIApplication.Idling</c> event. That distinction is the whole fix: opening
/// a Forma cloud model from inside an Idling event made Revit's own <c>CloudWorksharing.UI</c> throw
/// "Can not subscribe to an event during execution of that event" during <c>DocumentOpening</c>, and
/// the open then hung on the "Open Model" dialog or access-violated (0xc0000005). From a modal
/// message loop the cloud open behaves exactly like a user double-clicking the model.
///
/// Runs a queue of <see cref="Pass"/>es (one <see cref="BatchEngine"/> per action, since each engine
/// names its own log files) MODEL-MAJOR: each model is taken through every pass (all its actions and
/// script sets) before the run advances to the next model, so a model is visited as one group rather
/// than re-visited once per action. Two dispositions, two open strategies (both legal here, unlike
/// from Idling):
/// <list type="bullet">
/// <item><b>Close files / Close Revit</b> → each model is opened in the <em>background</em> and
/// closed as it's processed (<see cref="BatchEngine.ProcessModel"/>). Background documents close
/// freely, so nothing is left open and there's no "can't close the active document" problem.</item>
/// <item><b>Leave files open</b> → each model is opened <em>and activated</em> with a real window
/// and left open (<see cref="BatchEngine.OpenRunActivate"/>).</item>
/// </list>
///
/// <b>Never call this from the headless Idling loop</b> (<c>ScheduledRunner</c>'s): a modal
/// <c>ShowDialog</c> raised from an Idling event blocks forever with no one to dismiss it. The
/// headless path reaches this window only by escaping to an ExternalEvent first, and does so with
/// <c>showSummary: false</c>. A scheduled parent may still request the progress UI; a child instance
/// of a split run keeps it invisible because the supervising Revit owns the one overall bar.
/// </summary>
public sealed class BatchRunnerWindow : Form
{
    /// <summary>One action over one preset. A multi-action Batch Run / Schedule job is a queue of these.</summary>
    public readonly record struct Pass(BatchEngine Engine, ModelPreset Preset);

    /// <summary>What one finished pass produced — for the completion summary.</summary>
    public readonly record struct PassResult(
        string ActionName, string PresetName, BatchResult Result, string TrackingLogPath, string DetailLogPath);

    private readonly UIApplication _uiApp;
    private readonly IReadOnlyList<Pass> _passes;
    private readonly BatchCompletion _completion;
    private readonly Label _statusLabel;
    private readonly ProgressBar _bar;
    private readonly TextBox _log;
    private readonly RunConfirmPanel _confirmPanel;
    private readonly RunFooter _footer;
    private readonly int _total;
    private bool _started;

    /// <summary>
    /// Set by <see cref="RunModal"/> when the window should ask before it starts (Confirm state).
    /// Null runs straight away, as every unattended path does. Never set for an unattended run: a
    /// confirm nobody can answer would hang the scheduled path.
    /// </summary>
    private RunConfirmation? _confirm;

    /// <summary>
    /// True when nobody is watching this run — the headless scheduled path reuses this window for
    /// Dynamo script jobs (it dismisses itself and suppresses the summary). It is the one case
    /// where the stall watchdog emits a retained warning. It never terminates Revit; a stalled
    /// worker remains available for inspection and manual close.
    /// </summary>
    private bool _unattended;

    /// <summary>Per-pass outcomes; empty until the run finishes (and stays empty for "Close Revit", which terminates the process first).</summary>
    public IReadOnlyList<PassResult> Results { get; private set; } = System.Array.Empty<PassResult>();

    /// <summary>True once a run actually completed (so callers can stamp LastRunAt / show a summary only when something ran).</summary>
    public bool Ran => Results.Count > 0;

    public int TotalSucceeded => Results.Sum(r => r.Result.SucceededCount);
    public int TotalFailed => Results.Sum(r => r.Result.FailedCount);

    /// <summary>Single-pass convenience (Multi Model Check/Action, Initiate) — one action over one preset.</summary>
    public BatchRunnerWindow(UIApplication uiApp, BatchEngine engine, ModelPreset preset,
        string title, BatchCompletion completion)
        : this(uiApp, new[] { new Pass(engine, preset) }, title, completion) { }

    public BatchRunnerWindow(UIApplication uiApp, IReadOnlyList<Pass> passes,
        string title, BatchCompletion completion)
    {
        _uiApp = uiApp;
        _passes = passes;
        _completion = completion;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        // Font-based scaling like the other dialogs, so the larger type below grows with Windows
        // display scaling instead of being clipped at 125%/150%.
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(800, 500);

        // The bar spans the WHOLE run — every model of every pass — so it answers "how far through
        // the set am I", not "how far through this one action". A 3-model, 3-action run is 9 steps.
        _total = Math.Max(1, passes.Sum(p => p.Preset.Models.Count));
        _bar = new ProgressBar { Minimum = 0, Maximum = _total, Dock = DockStyle.Bottom, Height = 22 };
        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = RunWindowFonts.Body(),
            Text = "Preparing…"
        };

        // Running log, mirroring the standalone controller's output pane: one line per model per
        // pass, as it finishes. Without it a long run shows only "still working" on the current
        // model, and everything already done scrolls out of existence.
        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = RunWindowFonts.Log(),
            BorderStyle = BorderStyle.FixedSingle
        };

        // Confirm state: this panel sits where the log is until Start is pressed, so the question
        // that used to be a Revit TaskDialog is asked in the window that then shows the progress.
        _confirmPanel = new RunConfirmPanel { Visible = false };
        _footer = new RunFooter(this);
        _footer.StartClicked += (_, _) => RunOnce();
        _footer.CancelClicked += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _footer.CloseClicked += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        _footer.OpenLogRequested += (_, _) => RunResultPaths.OpenLog(FirstLogPath());
        _footer.OpenResultRequested += (_, _) => RunResultPaths.OpenResult(FirstLogPath());
        _footer.ShowRunning("Preparing…");

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        host.Controls.Add(_log);
        host.Controls.Add(_confirmPanel);
        host.Controls.Add(_statusLabel);
        host.Controls.Add(_bar);
        host.Controls.Add(_footer);
        Controls.Add(host);

        Theme.Apply(this);

        // Runs once the modal message loop is up (Shown fires after ShowDialog starts pumping).
        // With a confirmation the first thing shown is the question, and Start runs the same
        // RunOnce from a Click handler — still inside this dialog's modal message loop, which is
        // what cloud opens need (see the class remarks).
        Shown += (_, _) =>
        {
            if (_confirm != null) EnterConfirm(_confirm);
            else RunOnce();
        };
    }

    /// <summary>The tracking log of the first pass — what Open log / Open result derive from.</summary>
    private string FirstLogPath() => Results.Count > 0
        ? Results[0].TrackingLogPath
        : (_passes.Count > 0 ? _passes[0].Engine.TrackingLogPath : "");

    private void EnterConfirm(RunConfirmation confirm)
    {
        _confirmPanel.Set(confirm);
        _confirmPanel.Visible = true;
        _log.Visible = false;
        _bar.Visible = false;
        _statusLabel.Text = "Review, then press " + confirm.StartText + ".";
        _footer.ShowConfirm(confirm.StartText);
    }

    /// <summary>
    /// Runs <paramref name="passes"/> in a modal <see cref="BatchRunnerWindow"/> and shows a
    /// completion summary — the single entry point every interactive batch button uses. Returns the
    /// per-pass results (empty if nothing ran, or if "Close Revit" terminated the process first, in
    /// which case this never returns). The summary is shown for Close files / Leave files open —
    /// inside this window, whose Done state replaces the old summary TaskDialog; "Close Revit when
    /// complete" is inherently silent.
    ///
    /// <paramref name="confirm"/>, when given, makes the window open in its Confirm state: Start
    /// runs the batch, Cancel returns empty results having run nothing. It is ignored for an
    /// unattended run (<paramref name="showSummary"/> false), which has nobody to answer it.
    /// </summary>
    public static IReadOnlyList<PassResult> RunModal(
        UIApplication uiApp, IReadOnlyList<Pass> passes, string title, BatchCompletion completion,
        bool showSummary = true, bool showProgress = true, RunConfirmation? confirm = null)
    {
        // (CloudOpenFacts.RefreshFromForma used to run here, before the window. It now runs at the
        // start of RunOnce, so the window is on screen immediately and the up-to-15 s Forma lookup
        // happens after Start, under "Preparing…".)
        using var runner = new BatchRunnerWindow(uiApp, passes, title, completion);
        // Suppressing the summary is the headless path's marker that no one is watching — the same
        // condition that lets the stall watchdog quit the process. Must be set before ShowDialog,
        // which is what raises Shown and starts the run.
        runner._unattended = !showSummary;
        runner._confirm = showSummary ? confirm : null;

        // A CHILD of an auto-split run is unattended, and its window is pure noise: launching a
        // 5-group run put five progress dialogs on screen, each reporting only its own slice, while
        // the run as a whole was reported nowhere. Overall progress belongs to the supervisor
        // (UI.RunWatchWindow); a child reports through the shared tracking CSV.
        //
        // Made invisible rather than skipped. ShowDialog is not decoration here — it is the modal
        // message loop that makes opening a Forma cloud model survive at all (see the class remarks
        // and ScheduledRunner's cloud-model routing). Opacity 0 keeps that loop pumping with
        // nothing painted; Revit's own dialogs are separate windows and still appear normally.
        if (runner._unattended && !showProgress)
        {
            runner.Opacity = 0;                                 // nothing painted
            runner.ShowInTaskbar = false;                       // no taskbar button
            runner.StartPosition = FormStartPosition.Manual;    // and nowhere near a monitor, so
            runner.Location = new Point(-32000, -32000);        // not even a first frame can flash
        }
        LastRunCancelled = false;
        runner.ShowDialog();
        // Cancel at the Confirm state: nothing ran. Commands map this to Result.Cancelled, as the
        // old TaskDialog's "No" did.
        LastRunCancelled = !runner.Ran && runner.DialogResult == DialogResult.Cancel;
        // Interactive runs show their summary in the window's Done state, so no dialog follows. The
        // one exception is "Close Revit when complete" whose shutdown was refused: that window
        // closed itself and Revit is still here, so say what happened (as before). A headless
        // scheduled run (showSummary false) must never end on a TaskDialog nobody can click.
        if (runner.Ran && showSummary && completion == BatchCompletion.CloseRevit)
            TaskDialog.Show(Product.Name, runner.BuildSummaryText(title));
        return runner.Results;
    }

    /// <summary>True when the most recent <see cref="RunModal(UIApplication, IReadOnlyList{Pass}, string, BatchCompletion, bool, bool, RunConfirmation?)"/>
    /// ended because the user pressed Cancel in the Confirm state (nothing ran). UI thread only.</summary>
    public static bool LastRunCancelled { get; private set; }

    /// <summary>Single-pass overload of <see cref="RunModal(UIApplication, IReadOnlyList{Pass}, string, BatchCompletion)"/>.</summary>
    public static IReadOnlyList<PassResult> RunModal(
        UIApplication uiApp, BatchEngine engine, ModelPreset preset, string title, BatchCompletion completion,
        RunConfirmation? confirm = null) =>
        RunModal(uiApp, new[] { new Pass(engine, preset) }, title, completion, confirm: confirm);

    private void RunOnce()
    {
        if (_started) return;
        _started = true;

        // Leave the Confirm state (no-op when there was none): the question is answered, the
        // progress furniture takes its place. Painted before the Forma lookup below, which can
        // block this thread for a while.
        _confirmPanel.Visible = false;
        _log.Visible = true;
        _bar.Visible = true;
        _footer.ShowRunning();
        _statusLabel.Text = "Preparing…";
        Refresh();

        // Before any open: which cloud models are non-workshared, so the engine opens those with no
        // workset configuration (the open that crashes Revit on them). Every batch that opens a cloud
        // model comes through here — interactive, scheduled, and each supervised child alike. Moved
        // here from RunModal so the window (and its Confirm state) is up before the wait.
        Batch.CloudOpenFacts.RefreshFromForma(
            _passes.SelectMany(p => p.Preset.Models), TimeSpan.FromSeconds(15));

        // Say that it started. Withheld from interactive runs until 1.3.5 on the reasoning that the
        // button had just been pressed — which misreads how a batch is used: you start a long run
        // and walk away, and from across the room "still going" and "never started" look the same.
        // Unattended runs are excluded because the scheduled runner announces its own start.
        // Fire-and-forget by design, so a slow shell cannot delay the first model.
        if (!_unattended)
            RunNotification.ShowRunStarted(Text, _total);

        // Liveness watch. An interactive run has no timeout and no cancel button (ControlBox is
        // off), so a cloud open that never returns leaves this window frozen mid-model with
        // nothing written anywhere — the model's tracking row is only logged once it finishes.
        // The watchdog cannot unstick Revit, but it does record which model and which stage
        // stopped responding. It deliberately does NOT kill an interactive session: the user may
        // be mid-sync, and this is their own Revit. Only unattended runs self-terminate.
        string watchLog = _passes.Count > 0 ? _passes[0].Engine.TrackingLogPath : "";
        string watchTitle = Text;
        bool unattended = _unattended;
        RunTrace.StartWatchdog(RunTrace.DefaultStallAfter, (stage, stuck) =>
        {
            Logging.RunNote.Write(watchLog, watchTitle, "", "ERROR",
                $"No progress for {(int)stuck.TotalMinutes} min at: {stage} — Revit appears wedged " +
                $"on that model.{(unattended ? " The Revit worker was left open for inspection." : "")} " +
                $"Trail: {RunTrace.LogPath}");
            if (!unattended) return;
            RunTrace.Mark("watchdog left stalled Revit open (forced close disabled)");
            RunNotification.Show("Batch needs attention - " + watchTitle,
                $"No progress for {(int)stuck.TotalMinutes} minutes at {stage}. " +
                "This Revit was left open; close it manually after inspection.", failed: true);
        });

        try
        {
            // Auto-answer Revit's API pop-ups (upgrade / missing links / warnings-at-commit) for the
            // whole run, same as the headless path. Native windows the API can't reach (the cloud
            // "Open Model" progress) resolve on their own now that we open from a real message loop.
            // And the ones the API CANNOT reach: a message box raised inside another add-in's DLL
            // never fires DialogBoxShowing, and froze four parallel Revits for 8½ minutes on
            // 2026-08-15. See ForeignDialogBreaker — it reads the dialog before answering it.
            using (new DialogSuppressor(_uiApp))
            using (Batch.ForeignDialogBreaker.ForBatch())
            {
                RunBatch();
            }
        }
        catch (Exception ex)
        {
            // Nothing here handles the failure — it still propagates, exactly as before. But an
            // escape from RunBatch abandons the run with no summary and no closed window, so leave
            // evidence of it first rather than letting it vanish.
            RunTrace.Mark("batch ABORTED: " + ex.Message);
            Logging.RunNote.Write(watchLog, watchTitle, "", "ERROR", "Run aborted: " + ex.Message);
            // An interactive window has no ControlBox, so without this it would sit on "Running…"
            // forever behind the error. Say so and hand over a Close button.
            if (!_unattended)
            {
                AppendLog("Run aborted: " + ex.Message);
                _statusLabel.Text = "Run aborted — see the log.";
                _footer.ShowDone(RunResultPaths.CanOpenLog(watchLog), RunResultPaths.CanOpenResult(watchLog));
            }
            else
            {
                // Hidden split-child / headless window: nobody can press Close, so end the dialog
                // here or it stays open (invisible) forever.
                DialogResult = DialogResult.Cancel;
                Close();
            }
            throw;
        }
        finally
        {
            RunTrace.StopWatchdog();
        }

        // An interactive run that leaves Revit open keeps this window as its result screen (Done
        // state: summary in the log, Open result / Open log / Close) instead of closing and raising
        // a summary TaskDialog. Unattended runs and "Close Revit" still close at once: nobody is
        // there to press Close, or Revit is about to exit.
        bool staysOpen = !_unattended && _completion != BatchCompletion.CloseRevit;
        if (staysOpen)
        {
            EnterDone();
        }
        else
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        // Say so in Notification Center. Every batch ends here — the ribbon buttons, "Run now", and
        // the scheduled path all finish in this window — so this is the one place that cannot be
        // forgotten by a new entry point, which is exactly how the interactive buttons ended up
        // reporting nothing at all. It sits AFTER Close() and BEFORE the shutdown request below so
        // that "Close Revit when complete" — the disposition where nobody is left to read a summary
        // dialog — is the one that gets told first rather than not at all.
        //
        // Unattended runs are excluded: the scheduled runner and the split-run supervisor raise
        // their own final message once retries are settled, and that one is authoritative. Two
        // toasts disagreeing about the same run is worse than one arriving late.
        //
        // Waited for only when this process is about to go away. A window that stays open must not
        // freeze on its Done screen while the toast helper starts, and Revit outlives a fire-and-
        // forget delivery thread anyway.
        if (!_unattended && Ran)
            RunNotification.ShowRunFinished(Text, TotalSucceeded, TotalFailed,
                Results[0].TrackingLogPath, wait: !staysOpen);

        if (_completion == BatchCompletion.CloseRevit)
        {
            // Persist the silent summary, then request Revit's normal close path. If normal
            // shutdown is blocked, Revit remains open; there is deliberately no forced fallback.
            WriteSummaryRows();
            Schedule.RevitShutdown.RequestCurrent("modal batch complete");
        }
    }

    /// <summary>Done state: the old summary dialog's text goes into the log pane, and the footer
    /// offers Open result / Open log / Close.</summary>
    private void EnterDone()
    {
        AppendLog("");
        AppendLog(BuildSummaryText(Text).TrimEnd());
        string log = Results.Count > 0 ? Results[0].TrackingLogPath : "";
        _statusLabel.Text = $"Done — {TotalSucceeded} succeeded, {TotalFailed} failed.";
        _footer.ShowDone(RunResultPaths.CanOpenLog(log), RunResultPaths.CanOpenResult(log));
    }

    /// <summary>
    /// Appends one SUMMARY row per pass to that pass's tracking log — the written stand-in for the
    /// completion dialog when the run ends by closing Revit. Also what makes an auto-split group
    /// self-report: a group that launched but never wrote its SUMMARY row died mid-run.
    /// </summary>
    private void WriteSummaryRows()
    {
        foreach (PassResult r in Results)
            Logging.RunNote.Write(r.TrackingLogPath, r.ActionName, "", "SUMMARY",
                $"{Text} — succeeded: {r.Result.SucceededCount}, failed: {r.Result.FailedCount}");
    }

    /// <summary>
    /// Appends one line and repaints. Called only from the batch loop, which runs on the UI thread
    /// inside this dialog's own message loop — so no marshalling is needed, and none is done.
    /// </summary>
    private void AppendLog(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void RunBatch()
    {
        bool leaveOpen = _completion == BatchCompletion.LeaveOpen;
        int done = 0;

        // One run state (loggers + result) per pass, opened up front and kept for the whole run so
        // each action's log accrues all its models. Model-major iteration still appends each pass's
        // rows in model order, because for any fixed pass the models below are visited in order.
        var states = new BatchRunState[_passes.Count];
        for (int p = 0; p < _passes.Count; p++)
            states[p] = _passes[p].Engine.BeginRun();

        // The activated-open chain — script passes (IBatchAction.NeedsActiveDocument), or EVERY pass
        // under "leave files open". Revit cannot close the ACTIVE document, so a close-as-you-go
        // activated run opens the next model, then closes the PREVIOUS one; the very last activated
        // model stays open. Revit also cannot open an already-open model, so when the next activated
        // pass targets the SAME model with a matching open mode (e.g. a second script set), it
        // REUSES the open document rather than re-opening it.
        Autodesk.Revit.DB.Document? activeDoc = null;
        BatchEngine? activeEngine = null;   // the engine that OPENED activeDoc (owns its close)
        CloudModel? activeModel = null;

        // The BACKGROUND counterpart of that chain, and the one that carries the ordinary
        // "Close files" run of several Checks — every cloud job routes here (ScheduledRunner sends
        // any job with a cloud model to the modal runner), so this is the hot path for a scheduled
        // batch. It keeps one model's document open across its consecutive background passes
        // instead of re-opening the model per action; see ModelDocumentCursor for the measurement
        // that motivated it and the rules that keep the output identical.
        var cursor = new ModelDocumentCursor();

        // Model-major: run one model through EVERY pass (all its actions and script sets) before
        // moving to the next model — the ordering the batch is expected to have. Every pass in a
        // Batch/Schedule job shares the same combined model set; the guard tolerates a shorter list.
        int modelCount = _passes.Count == 0 ? 0 : _passes.Max(p => p.Preset.Models.Count);
        RunTrace.Mark($"batch begin: {Text} — {modelCount} model(s) × {_passes.Count} pass(es), {_completion}");
        try
        {
            for (int i = 0; i < modelCount; i++)
            {
                for (int p = 0; p < _passes.Count; p++)
                {
                    Pass pass = _passes[p];
                    if (i >= pass.Preset.Models.Count) continue;
                    CloudModel model = pass.Preset.Models[i];
                    BatchRunState state = states[p];
                    bool activate = leaveOpen || pass.Engine.NeedsActiveDocument;

                    RunTrace.Mark($"batch model {i + 1}/{modelCount}, pass {p + 1}/{_passes.Count}");
                    _statusLabel.Text =
                        $"[{done + 1}/{_total}] {model.DisplayName}: {pass.Engine.ActionName}…";
                    _bar.Value = Math.Min(done, _bar.Maximum);

                    // Counts before the call, so this model's own outcome can be told from the pass total.
                    int okBefore = state.Result.SucceededCount;
                    int failBefore = state.Result.FailedCount;
                    // Paint the label/bar before the (blocking, seconds-long) cloud open.
                    Refresh();
                    Application.DoEvents();

                    if (activate)
                    {
                        // An activated open of this model must not collide with a background
                        // document the cursor still holds — Revit forbids opening an open model.
                        cursor.Release();

                        // Same model already open+active from a prior activated pass with the same
                        // open mode → run against it (Revit forbids re-opening an open model).
                        bool reuse =
                            activeDoc != null &&
                            ReferenceEquals(activeModel, model) &&
                            activeEngine!.ModifiesModel == pass.Engine.ModifiesModel;

                        if (reuse)
                        {
                            pass.Engine.RunOnOpenActiveDocument(model, activeDoc!, state);
                        }
                        else
                        {
                            // open + activate the new model; leave-open keeps every window,
                            // otherwise close the previous (now-inactive) one.
                            Autodesk.Revit.DB.Document? doc = pass.Engine.OpenRunActivate(model, state);
                            if (!leaveOpen && activeDoc != null)
                                activeEngine!.CloseInteractive(activeDoc);
                            activeDoc = doc;
                            activeEngine = pass.Engine;
                            activeModel = model;
                        }
                    }
                    else
                    {
                        // Background open, run, close — except the close is deferred to the cursor,
                        // which holds the document for this model's remaining background passes.
                        cursor.Run(pass.Engine, model, state);
                    }

                    done++;

                    string outcome = state.Result.FailedCount > failBefore ? "ERROR"
                                   : state.Result.SucceededCount > okBefore ? "OK"
                                   : "—";
                    AppendLog($"[{done}/{_total}] {outcome,-5} {model.DisplayName}  [{pass.Engine.ActionName}]");
                }

                // This model has run every pass — nothing else will use its background document.
                cursor.Release();
            }
        }
        finally
        {
            // A pass that throws out of the loop must not strand an open background document: the
            // summary, the "leave files open" surfacing and (for a chunk) the process quit all
            // follow, and any of them would then be working around a model Revit still holds open.
            cursor.Release();
        }

        // Every model processed — let each pass write any run-level artifact (e.g. the
        // Phase/Workset/Design-Option matrix built from all the run's models).
        RunTrace.Mark("batch finalize");
        for (int p = 0; p < _passes.Count; p++)
            _passes[p].Engine.FinalizeRun();

        // Report per pass (unchanged summary shape), in pass order.
        var results = new List<PassResult>(_passes.Count);
        for (int p = 0; p < _passes.Count; p++)
            results.Add(new PassResult(
                _passes[p].Engine.ActionName, _passes[p].Preset.Name, states[p].Result,
                _passes[p].Engine.TrackingLogPath, _passes[p].Engine.DetailLogPath));

        _bar.Value = _bar.Maximum;
        int ok = results.Sum(r => r.Result.SucceededCount);
        int failed = results.Sum(r => r.Result.FailedCount);
        _statusLabel.Text = $"Done — {ok} succeeded, {failed} failed.";
        AppendLog("");
        AppendLog($"Done — {ok} succeeded, {failed} failed.");
        foreach (PassResult r in results)
            if (!string.IsNullOrWhiteSpace(r.TrackingLogPath)) AppendLog("Log: " + r.TrackingLogPath);
        Refresh();
        Results = results;
        RunTrace.Mark($"batch end: {results.Sum(r => r.Result.SucceededCount)} ok, " +
                      $"{results.Sum(r => r.Result.FailedCount)} failed");
    }

    private string BuildSummaryText(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{title} — complete.");
        sb.AppendLine();
        sb.AppendLine($"Succeeded: {TotalSucceeded}");
        sb.AppendLine($"Failed: {TotalFailed}");
        if (_completion == BatchCompletion.LeaveOpen)
        {
            sb.AppendLine();
            sb.AppendLine("The processed models have been left open.");
        }
        foreach (PassResult r in Results)
        {
            sb.AppendLine();
            if (Results.Count > 1)
                sb.AppendLine($"{r.ActionName} — {r.PresetName}: {r.Result.SucceededCount} ok / {r.Result.FailedCount} failed");
            sb.AppendLine($"Tracking log: {r.TrackingLogPath}");
            sb.AppendLine($"Detail log:   {r.DetailLogPath}");
        }
        return sb.ToString();
    }
}
