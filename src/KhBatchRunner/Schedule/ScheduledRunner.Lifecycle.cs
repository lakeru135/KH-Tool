using System.IO;
using System.Globalization;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;

// Disambiguated from System.Diagnostics, which this file also uses (Process).
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

public static partial class ScheduledRunner
{
    /// <summary>
    /// Interactive counterpart to <see cref="StartHeadless"/>: builds one <see cref="BatchEngine"/>
    /// pass per action — using the job's own destination and post-run
    /// <see cref="ScheduledJob.Completion"/>, never a dialog's defaults — and hands
    /// the whole queue to <see cref="BatchIdlingDriver"/> instead of running synchronously
    /// inside the calling command. That synchronous call (this method replaces it at both
    /// "Run now" call sites — <see cref="Commands.ScheduleRunCommand"/> and
    /// <see cref="Commands.BatchRunCommand"/>'s save-as-schedule branch) was reachable from
    /// the UI thread and is the same journal wedge <see cref="BatchIdlingDriver"/> exists to
    /// avoid for the built-in Multi Model Check/Action buttons.
    /// </summary>
    /// <param name="error">
    /// Set when the job can't be run at all (no models resolved, no built-in action selected).
    /// <see cref="ScheduledJob.LastRunAt"/> is deliberately NOT stamped in that case — nothing
    /// ran. If this returns <c>false</c> with <paramref name="error"/> still <c>null</c>, a
    /// batch is already running (<see cref="BatchIdlingDriver.TryStart"/>'s own guard).
    /// </param>
    /// <exception cref="Exception">
    /// Propagates whatever the first pass's <c>BeginRun</c> throws (see
    /// <see cref="BatchIdlingDriver.TryStart"/>) — the caller should catch this the same way
    /// <see cref="Commands.BatchActionCommandBase"/> does.
    /// </exception>
    public static bool TryRunInteractive(UIApplication uiApp, ScheduledJob job, string title, out string? error)
    {
        error = null;
        string destFolder = string.IsNullOrWhiteSpace(job.DestinationFolder)
            ? DefaultDestinationFolder()
            : job.DestinationFolder;
        string projectLog = string.IsNullOrWhiteSpace(job.ProjectLogName) ? job.Name : job.ProjectLogName;

        // As the headless path: one account read for the whole run, so the combiner's preflight has
        // something to answer with. Usually already cached — any window that lists model sets warms
        // it in the background — in which case this returns immediately.
        WarmAccountAccess();

        // The REST-only steps (Get File Log, Get Data Extract) open no model — run them first, then
        // continue with any model passes. A job made only of those has no passes and finishes here.
        if (RestOnlySteps.IsRequested(job))
        {
            RestOnlyOutcome restOnly =
                RestOnlySteps.RunWithOutcome(job, destFolder, projectLog, StampOf(job));
            foreach (string n in restOnly.Notes)
                WriteNote(destFolder, projectLog, job.Name, n, "INFO");
            if (RestOnlySteps.IsOnly(job))
            {
                job.LastRunAt = DateTime.Now;
                ScheduleStore.Save(job);
                // A dialog, not a toast: this run was started by hand and somebody is standing in
                // front of it. Until 1.5.1.3 "Run now" on a REST-only job returned having shown
                // nothing whatsoever, which reads exactly like a button that does not work.
                ShowRestOnlySummary(title, restOnly);
                return true;
            }
        }

        ModelPreset combined = ModelPresetCombiner.Combine(
            job.Name, job.ModelSetIds, job.ModelSetNames, job.AdHocModels, out _);
        if (combined.Models.Count == 0)
        {
            error = $"No models resolved for job '{job.Name}'.";
            return false;
        }

        // Parallel auto-split: run-now honours the job's group size the same way a scheduled fire
        // does — launch one fresh Revit per group, all at once, instead of the single-process modal
        // runner below. Confirmed first (this opens several Revit instances). A small run
        // (count <= group size) falls through to the normal single-process run.
        // Count first: CanSplit writes a note, and a run too small to split shouldn't produce one.
        // A job that runs Batch Interop goes to a supervised child even when it is too small to split,
        // so a native crash there cannot close this Revit (InteractiveSplit.SupervisedGroupSize).
        int splitSize = InteractiveSplit.SupervisedGroupSize(
            combined.Models.Count,
            InstanceBudget.EffectiveGroupSize(job.GroupSize, combined.Models, InstanceBudget.WeightFor(job)),
            alwaysSupervise: RunsInterop(job));
        if (splitSize > 0 &&
            CanSplit(job, combined.Models, destFolder, projectLog, uiApp.Application.VersionNumber))
        {
            int groups = ChunkLauncher.GroupCount(combined.Models.Count, splitSize);
            if (groups > 1)
            {
                var confirm = new Autodesk.Revit.UI.TaskDialog("Run now — parallel groups")
                {
                    MainInstruction = $"Run {combined.Models.Count} models as {groups} group(s) of up to {splitSize}?",
                    MainContent =
                        "Each group opens in its own Revit instance and closes when it finishes. " +
                        "Concurrency: " + InstanceBudget.Describe(combined.Models) + ".\n\nContinue?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };
                if (confirm.Show() != TaskDialogResult.Yes)
                    return true; // user declined the split; nothing ran (LastRunAt not stamped)
            }

            RunManifest? manifest = ChunkLauncher.LaunchSupervised(
                job, combined.Models, splitSize, uiApp.Application.VersionNumber, out string? chunkErr);
            if (manifest == null)
            {
                error = chunkErr ?? "Could not launch any group instances.";
                return false;
            }
            RunSupervisor.StartInteractive(manifest, job.Completion);
            int launched = manifest.CurrentRound!.Groups.Count;
            job.LastRunAt = DateTime.Now;
            ScheduleStore.Save(job);

            // Overall progress is opened by RunSupervisor.StartInteractive above, so every split
            // launch gets it rather than only the ones that remembered to ask.
            if (chunkErr != null)
                Autodesk.Revit.UI.TaskDialog.Show(title, chunkErr);
            return true;
        }

        List<UI.BatchRunnerWindow.Pass> passes =
            BuildJobPasses(uiApp, job, destFolder, projectLog, combined, interactive: true, out List<string> missingSets);
        if (missingSets.Count > 0)
            WriteNote(destFolder, projectLog, job.Name,
                "Script set(s) or preset(s) not found, skipped: " + string.Join(", ", missingSets));
        if (passes.Count == 0)
        {
            // A REST-only step already ran above (its files are written); nothing else to open.
            if (RestOnlySteps.IsRequested(job)) { job.LastRunAt = DateTime.Now; ScheduleStore.Save(job); return true; }
            error = "Nothing to run — no built-in action or script set resolved for this job.";
            return false;
        }

        // Interactive "Run now" runs in a modal message-loop runner, NOT the
        // Idling driver — opening Forma cloud models from an Idling event hung/crashed. The runner
        // is synchronous (it blocks here until the run finishes), honours the job's disposition
        // (Close files / Leave files open / Close Revit), and shows its own completion summary.
        // Stamp LastRunAt only once it actually ran (Results non-empty). NOTE: this is the
        // INTERACTIVE path only — the headless scheduled path (OnHeadlessIdling) must not show a
        // modal, so it keeps its own Idling loop.
        IReadOnlyList<UI.BatchRunnerWindow.PassResult> results =
            UI.BatchRunnerWindow.RunModal(uiApp, passes, title, job.Completion);
        if (results.Count > 0)
        {
            job.LastRunAt = DateTime.Now;
            ScheduleStore.Save(job);
        }
        return true;
    }

    /// <summary>
    /// First-tick setup for the headless run: resolves the models, builds one
    /// <see cref="BatchEngine"/> pass per action (job's own destination; open mode fixed by
    /// category), and opens the first pass. Returns <c>false</c> — after writing an explanatory note — for a
    /// terminal condition (no models resolved, or no built-in action), so the caller ends the run.
    /// </summary>
    private static bool BeginHeadless(UIApplication uiApp, ScheduledJob job)
    {
        string destFolder = string.IsNullOrWhiteSpace(job.DestinationFolder)
            ? DefaultDestinationFolder()
            : job.DestinationFolder;
        string projectLog = string.IsNullOrWhiteSpace(job.ProjectLogName) ? job.Name : job.ProjectLogName;

        // Mixed job (REST-only steps + model actions): run the REST pulls up front. A job made only
        // of them never reaches here — OnHeadlessIdling handles it before the model loop.
        foreach (string n in RestOnlySteps.Run(job, destFolder, projectLog, StampOf(job)))
            WriteNote(destFolder, projectLog, job.Name, n, "INFO");

        ModelPreset combined = ModelPresetCombiner.Combine(
            job.Name, job.ModelSetIds, job.ModelSetNames, job.AdHocModels, out List<string> setNotes);
        foreach (string n in setNotes) WriteNote(destFolder, projectLog, job.Name, n);
        if (combined.Models.Count == 0)
        {
            WriteNote(destFolder, projectLog, job.Name, $"No models resolved for job '{job.Name}'.");
            return false;
        }
        // Script-set jobs never reach here (OnHeadlessIdling routes them to the ExternalEvent /
        // modal-runner path), so an empty action list means the job selects nothing at all.
        if (job.ActionNames.Count == 0)
        {
            WriteNote(destFolder, projectLog, job.Name,
                "Nothing to run — the job selects no built-in action or script set.");
            return false;
        }

        // Same pass-building as the interactive path (handles the Initiate special-case), then
        // mapped to the headless tuple list. Script-set jobs never reach here.
        List<UI.BatchRunnerWindow.Pass> built =
            BuildJobPasses(uiApp, job, destFolder, projectLog, combined, interactive: false, out List<string> missingSets);
        if (missingSets.Count > 0)
            WriteNote(destFolder, projectLog, job.Name, "Script set(s) or preset(s) not found, skipped: " + string.Join(", ", missingSets));
        if (built.Count == 0)
        {
            WriteNote(destFolder, projectLog, job.Name, "Nothing to run — no action or script set resolved.");
            return false;
        }
        _hlPasses = built.Select(p => (p.Engine, p.Preset)).ToList();

        // Begin every pass up front (model-major runs interleave passes, so each pass's loggers
        // must stay open for the whole run). Any BeginRun throwing here (e.g. an unreachable
        // destination folder) is caught by OnHeadlessIdling before a single model opens.
        _hlStates = _hlPasses.Select(p => p.Engine.BeginRun()).ToArray();
        _hlModelCount = _hlPasses.Max(p => p.Preset.Models.Count);
        _hlModelIndex = 0;
        _hlPassIndex = 0;

        // Liveness watch for an UNATTENDED run: nobody is watching this Revit, so a cloud open
        // that never returns would hold the process (and its RAM, and its license) forever —
        // and in an auto-split run, one wedged group silently shrinks the batch with no marker
        // in the shared log. Here the watchdog may quit: there is no user mid-anything, and
        // everything already processed is flushed to CSV row by row.
        string watchLog = _hlPasses[0].Engine.TrackingLogPath;
        string watchName = job.Name;
        RunTrace.StartWatchdog(RunTrace.DefaultStallAfter, (stage, stuck) =>
        {
            RunNote.Write(watchLog, watchName, "", "ERROR",
                $"Stalled {(int)stuck.TotalMinutes} min at: {stage} — quitting this Revit instance. " +
                $"Remaining models in this run did not run. Trail: {RunTrace.LogPath}");
            RunTrace.Mark("quit (watchdog)");
            QuitRevit();
        });
        return true;
    }

    /// <summary>
    /// Shared teardown for both <see cref="EndHeadless"/> and <see cref="EndHeadlessKeepAlive"/>:
    /// unsubscribes the persistent headless handler (allowed mid-event), disposes the dialog
    /// suppressor, and clears the run's static state. Deciding whether to quit Revit is left to
    /// the caller.
    /// </summary>
    private static void EndHeadlessTeardown()
    {
        // First: the run may be ending mid-model (an error, or the last model just finished its
        // passes), and a document the cursor still holds would otherwise stay open — which matters
        // for "leave files open", whose caller re-opens the models right after this returns and
        // must not find one already open.
        _hlCursor.Release();
        RunTrace.StopWatchdog();   // the run is over; a late stall report would be a false alarm
        if (_hlApp != null && _hlHandler != null)
            _hlApp.Idling -= _hlHandler;
        _hlSuppressor?.Dispose();
        _hlSuppressor = null;
        _hlApp = null;
        _hlHandler = null;
        _hlJob = null;
        _hlPasses = null;
        _hlStates = null;
    }

    /// <summary>Unsubscribes the persistent headless handler (allowed mid-event) and requests
    /// Revit's normal close path.</summary>
    private static void EndHeadless()
    {
        EndHeadlessTeardown();
        QuitRevit();
    }

    /// <summary>
    /// Same teardown as <see cref="EndHeadless"/> but leaves Revit running — used for the "leave
    /// files open" disposition, whose caller surfaces the models (<see cref="LeftOpenDocsActivator.Surface"/>)
    /// right after this returns instead of quitting the process.
    /// </summary>
    private static void EndHeadlessKeepAlive() => EndHeadlessTeardown();

    /// <summary>
    /// Records a finished run: an ephemeral auto-split chunk deletes its own chunk file (it isn't a
    /// saved schedule, so there's nothing to stamp and nothing should appear in the schedule store),
    /// while a real saved job stamps <see cref="ScheduledJob.LastRunAt"/> and re-saves.
    /// </summary>
    private static void RecordRunFinished(ScheduledJob job)
    {
        if (job.IsEphemeralChunk)
        {
            ChunkJobStore.Delete(job.Id);
            return;
        }
        job.LastRunAt = DateTime.Now;
        ScheduleStore.Save(job);
    }

    private static void QuitRevit()
    {
        // One shared implementation for scheduled, interactive, broker, and diagnostic shutdown.
        // It posts WM_CLOSE only. If a modal dialog or genuine hang blocks that request, Revit is
        // intentionally left open for inspection; there is no timeout that terminates it.
        RevitShutdown.RequestCurrent("scheduled/headless batch complete");
    }

    /// <summary>
    /// Completion toast for an UNATTENDED run, then quit. The counts come from the manifest's own
    /// tracking log, which is the same file the supervisor reconciled against.
    ///
    /// <see cref="UI.RunNotification.ShowAndWait"/> verifies that Windows retained the item before
    /// this process requests its normal close, so the notification survives after Revit exits.
    /// </summary>
    private static void NotifyFinishedThenQuit(string runName, RunManifest manifest)
    {
        try
        {
            (int ok, int failed) = TrackingCsvReader.CountByStatus(manifest.TrackingLogPath);
            if (manifest.State == RunState.Stopped)
            {
                UI.RunNotification.ShowRunNeedsAttention(runName, ok, failed,
                    manifest.StopReason, manifest.TrackingLogPath);
            }
            else if (manifest.Unrecoverable.Count > 0)
            {
                UI.RunNotification.ShowRunStopped(runName, ok, failed,
                    RunSupervisor.MaxRounds, manifest.UnrecoverableDisplayNames(),
                    manifest.TrackingLogPath);
            }
            else
            {
                UI.RunNotification.ShowRunFinishedAndWait(runName, ok, failed,
                    manifest.TrackingLogPath);
            }
        }
        catch (Exception ex)
        {
            RunTrace.Mark("completion notification failed: " + ex.Message);
        }
        QuitRevit();
    }
}
