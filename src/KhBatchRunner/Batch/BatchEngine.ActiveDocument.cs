using Autodesk.Revit.DB;
using KhBatchRunner.Actions;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Batch;

public sealed partial class BatchEngine
{
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
}
