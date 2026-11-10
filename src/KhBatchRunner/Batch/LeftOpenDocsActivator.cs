using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Batch;

/// <summary>
/// Surfaces a batch's models for the "leave files open" disposition. The batch drives one model
/// per <c>Idling</c> tick, opening each in the background and closing it as it goes — activating
/// documents is forbidden inside an API event, so nothing can be shown during the run. Instead
/// this handler runs from an <see cref="ExternalEvent"/> (a valid context) after the batch and
/// <see cref="UIApplication.OpenAndActivateDocument(string)"/>s each model so it appears as a real
/// window the user can inspect.
///
/// <b>One model per <see cref="Execute"/> call, not a loop.</b> <c>OpenAndActivateDocument</c>
/// switches the active document; doing that more than once inside a single API event is what
/// used to crash Revit on a multi-model preset. Each call handles exactly one queued model, then
/// (if more remain) calls <see cref="ExternalEvent.Raise"/> again for a fresh event on the next
/// tick — <c>Raise()</c> from inside <see cref="Execute"/> only re-queues the event, it does not
/// re-enter <see cref="Execute"/> synchronously.
///
/// Opens are still kept safe: a workshared local model opens <b>detached</b> (never locks the
/// central); a cloud (C4R/Forma) model can never be detached — Revit throws "Detach option is not
/// valid for cloud model" if you try — so it opens attached instead. Both open with every workset
/// ON (not closed) so the re-opened window actually shows content instead of a blank view.
/// </summary>
internal sealed class LeftOpenDocsActivator : IExternalEventHandler
{
    private static LeftOpenDocsActivator? _instance;
    private static ExternalEvent? _event;

    /// <summary>Models still waiting to be opened+activated, one per <see cref="Execute"/> call.</summary>
    private readonly Queue<CloudModel> _queue = new();

    /// <summary>
    /// Keys already enqueued or already shown during the CURRENT drain — de-dup guard so the same
    /// model queued twice (e.g. a second <see cref="Surface"/> call arriving mid-drain) doesn't
    /// open twice. Cleared when the drain finishes so a later, independent batch can show the same
    /// model again.
    /// </summary>
    private readonly HashSet<string> _queuedKeys = new();

    /// <summary>Guards the re-open prompts (cloud "Opening…"/relinquish/upgrade/missing-links) — the batch's own suppressor is already disposed by the driver by the time this runs.</summary>
    private DialogSuppressor? _suppressor;

    /// <summary>True while a drain (one or more queued <see cref="Execute"/> events) is in flight — prevents a second <see cref="Surface"/> call from raising a redundant event mid-drain.</summary>
    private bool _draining;

    /// <summary>Invoked once, when the current drain empties — e.g. a headless test that needs to know when it's safe to inspect the re-opened documents.</summary>
    private Action? _onAllSurfaced;

    /// <summary>
    /// Creates the ExternalEvent once, from a valid context (call from <c>App.OnStartup</c>).
    /// No-op if already initialized or if not running inside Revit.
    /// </summary>
    public static void Initialize()
    {
        if (_instance != null) return;
        try
        {
            _instance = new LeftOpenDocsActivator();
            _event = ExternalEvent.Create(_instance);
        }
        catch
        {
            _instance = null;
            _event = null; // no Revit host (e.g. ThemePreview) — surfacing is simply unavailable.
        }
    }

    /// <summary>
    /// Queues <paramref name="models"/> to be re-opened and shown, one per Idling-adjacent
    /// <see cref="ExternalEvent"/> tick. Safe to call while a previous drain is still in flight —
    /// models append to the existing queue (never <c>Clear()</c>ed here) and are de-duplicated by
    /// key against both what's already queued and what's already been shown this drain.
    /// </summary>
    /// <param name="onAllSurfaced">
    /// Optional callback invoked exactly once, when the queue this call contributes to fully
    /// drains. If a drain is already in flight with its own pending callback, a non-null value
    /// here replaces it (last call wins); a <c>null</c> here never clobbers one already set.
    /// </param>
    public static void Surface(IEnumerable<CloudModel> models, Action? onAllSurfaced = null)
    {
        if (_instance == null || _event == null)
        {
            // No ExternalEvent (Initialize failed / no Revit host): we can't surface anything, but
            // the caller may be waiting on the callback to proceed (e.g. the headless leave-open
            // self-test defers its End() until this fires). Invoke it so it can never hang.
            onAllSurfaced?.Invoke();
            return;
        }

        foreach (CloudModel model in models)
        {
            if (_instance._queuedKeys.Add(KeyFor(model)))
                _instance._queue.Enqueue(model);
        }

        if (onAllSurfaced != null)
            _instance._onAllSurfaced = onAllSurfaced;

        if (!_instance._draining && _instance._queue.Count > 0)
        {
            _instance._draining = true;
            _event.Raise();
        }
    }

    /// <summary>
    /// Handles exactly ONE queued model per call — see the class doc for why. Re-raises the event
    /// for the next model if the queue isn't empty yet; otherwise ends the drain.
    /// </summary>
    public void Execute(UIApplication app)
    {
        _suppressor ??= new DialogSuppressor(app);

        if (_queue.Count == 0)
        {
            FinishDrain();
            return;
        }

        CloudModel model = _queue.Dequeue();
        // Breadcrumb BEFORE the open. A cloud open can access-violate (0xc0000005) deep in Revit's
        // worksets load — a NATIVE crash a C# try/catch can't stop — so a log written only after
        // the open would never appear. If surface.log ends at "opening ..." with no "opened", that
        // model's open is the culprit (the interactive journal is the other half of the evidence).
        Breadcrumb($"opening {model.DisplayName} ({(model.IsLocal ? "local" : "cloud")})");
        try
        {
            OpenAndActivate(app, model);
            Breadcrumb($"opened {model.DisplayName}");
        }
        catch (Exception ex)
        {
            // Best effort per model — one that won't open (e.g. already open, or missing)
            // shouldn't stop the rest from surfacing.
            Breadcrumb($"FAILED {model.DisplayName}: {ex.Message}");
        }

        if (_queue.Count > 0)
        {
            _event!.Raise(); // next model on a fresh event — one active-doc switch per event.
            return;
        }

        FinishDrain();
    }

    /// <summary>Shared exit path for both "nothing was queued" and "just dequeued the last model" — disposes the re-open suppressor, clears the per-drain de-dup set, and fires the completion callback exactly once.</summary>
    private void FinishDrain()
    {
        _suppressor?.Dispose();
        _suppressor = null;
        _draining = false;
        _queuedKeys.Clear();

        Action? callback = _onAllSurfaced;
        _onAllSurfaced = null;
        callback?.Invoke();
    }

    private static void OpenAndActivate(UIApplication app, CloudModel model)
    {
        if (model.IsLocal)
        {
            if (string.IsNullOrEmpty(model.LocalPath) || !File.Exists(model.LocalPath)) return;

            bool workshared;
            try { workshared = BasicFileInfo.Extract(model.LocalPath).IsWorkshared; }
            catch { workshared = false; }

            if (!workshared)
            {
                app.OpenAndActivateDocument(model.LocalPath); // plain local file — no central to lock
                return;
            }
            ModelPath local = ModelPathUtils.ConvertUserVisiblePathToModelPath(model.LocalPath);
            app.OpenAndActivateDocument(local, DetachedInspect(), false);
        }
        else
        {
            ModelPath cloud = ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                Region(model.Region), model.ProjectGuid, model.ModelGuid);
            // A non-workshared cloud model gets no workset configuration at all — the batch just
            // recorded which kind it is (CloudOpenFacts), and the configuration is what crashes it.
            OpenOptions options = CloudOpenFacts.PlanFor(model).Mode == CloudOpenMode.Plain
                ? new OpenOptions()
                : CloudInspect();
            app.OpenAndActivateDocument(cloud, options, false);
        }
    }

    /// <summary>Local workshared model: detached (so it never locks the central) with every workset OPEN — a safe, fully-visible inspection copy.</summary>
    private static OpenOptions DetachedInspect()
    {
        var opts = new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets };
        opts.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
        return opts;
    }

    /// <summary>
    /// Cloud (C4R/Forma) model: MUST NOT be detached — Revit throws "Detach option is not valid for
    /// cloud model" — so this leaves the default <see cref="DetachFromCentralOption.DoNotDetach"/>.
    /// Worksets are opened <b>closed</b>, NOT all-open: re-opening an attached cloud model with
    /// <see cref="WorksetConfigurationOption.OpenAllWorksets"/> makes Revit download and check out
    /// every workset (the "SelectedPartitionsForEdit" path), a heavy load that — from the nested
    /// surfacing context, moments after the batch closed the same model — either hangs on the
    /// "Open Model" progress dialog or access-violates (0xc0000005) deep in the epoch load. Opening
    /// worksets-closed still gives a real window the user can inspect and open worksets in manually,
    /// without triggering that crash. (Local detached copies stay all-open — they don't check out.)
    /// </summary>
    private static OpenOptions CloudInspect()
    {
        var opts = new OpenOptions(); // DetachFromCentralOption defaults to DoNotDetach
        opts.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
        return opts;
    }

    /// <summary>Best-effort append-only breadcrumb for surfacing, so an interactive cloud-open crash (which no C# catch can trap) still leaves an add-in-side trail at %LOCALAPPDATA%\KhBatchRunner\surface.log. The batch run itself writes the same trail — see <see cref="Diagnostics.RunTrace"/>.</summary>
    private static void Breadcrumb(string message) => Diagnostics.RunTrace.Mark("surface: " + message);

    /// <summary>De-dup key: one entry per distinct local path or cloud model, independent of which pass/preset it came from.</summary>
    private static string KeyFor(CloudModel model) =>
        model.IsLocal
            ? "local:" + (model.LocalPath ?? "").ToLowerInvariant()
            : "cloud:" + model.ModelGuid;

    private static string Region(string region) =>
        string.Equals(region, "EMEA", System.StringComparison.OrdinalIgnoreCase)
            ? ModelPathUtils.CloudRegionEMEA
            : ModelPathUtils.CloudRegionUS;

    /// <summary>
    /// Tears down the ExternalEvent and any in-flight suppressor on host shutdown
    /// (<c>App.OnShutdown</c>). Best-effort — wrapped in try/catch since the host may already be
    /// tearing down by the time this runs.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            _instance?._suppressor?.Dispose();
            _event?.Dispose();
        }
        catch
        {
            // Host may be mid-teardown — nothing more we can do.
        }
        _instance = null;
        _event = null;
    }

    public string GetName() => "KhBatchRunner: surface left-open models";
}
