using System.IO;
using Autodesk.Revit.DB;
using KhBatchRunner.Actions;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Batch;

public sealed partial class BatchEngine
{
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
