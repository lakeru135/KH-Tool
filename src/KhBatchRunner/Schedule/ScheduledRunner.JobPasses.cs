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
    // ---- script-set jobs (Dynamo runner) ----------------------------------------------------
    // Kept alive in statics for the duration of the ExternalEvent round-trip, like the
    // self-test's driver probe.
    private static ExternalEvent? _hlScriptEvent;
    private static IExternalEventHandler? _hlScriptHandler;

    /// <summary>
    /// Builds the job's pass queue: one <see cref="BatchEngine"/> per <see cref="RunItem"/>, in the
    /// job's stored order — actions and script sets stay interleaved exactly as arranged (schema v3;
    /// the old shape stored two separate lists and always ran actions first). Script sets that no
    /// longer exist are returned in <paramref name="missingSets"/> for the caller to report rather
    /// than silently skipping.
    /// </summary>
    private static List<UI.BatchRunnerWindow.Pass> BuildJobPasses(
        UIApplication uiApp, ScheduledJob job, string destFolder, string projectLog,
        ModelPreset combined, bool interactive, out List<string> missingSets)
    {
        var actions = new List<IBatchAction>();
        missingSets = new List<string>();
        List<Scripts.ScriptSet>? sets = null; // loaded lazily, only if a script set is used

        foreach (RunItem item in job.RunItems)
        {
            if (item.Kind == RunItemKind.ScriptSet)
            {
                sets ??= Scripts.ScriptSetStore.LoadAll();
                // Resolve by stable id first (survives a rename), then by name for older jobs that
                // carry no RefId. Only report missing when neither matches.
                Scripts.ScriptSet? set =
                    (!string.IsNullOrWhiteSpace(item.RefId)
                        ? sets.FirstOrDefault(s => s.Id == item.RefId) : null)
                    ?? sets.FirstOrDefault(s => s.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (set == null)
                {
                    missingSets.Add(item.Name);
                    continue;
                }
                actions.Add(new RunScriptSetAction(set));
                continue;
            }

            // The REST-only steps are handled outside the per-model pipeline (see RestOnlySteps and
            // the callers above) — never turn one into a BatchEngine action.
            if (RestOnlySteps.IsRestOnlyItem(item)) continue;

            IBatchAction action;
            if (item.Name == InitiateToFormaAction.DisplayName)
            {
                // Built from the job's OWN snapshot (never the live input-set store) so a
                // headless run is self-contained. A malformed/absent snapshot is fatal here —
                // let it throw so the caller logs an ERROR rather than silently skipping publish.
                if (job.InitiatePublish is null)
                    throw new InvalidOperationException(
                        "Job selects 'Initiate to Forma' but carries no publish target snapshot.");
                action = new InitiateToFormaAction(job.InitiatePublish.ToPublishTarget());
            }
            else if (item.Name == CutModelAction.DisplayName)
            {
                // Same self-contained reasoning as Initiate — build from the job's own snapshot.
                if (job.CutModelPublish is null)
                    throw new InvalidOperationException(
                        "Job selects 'Cut model' but carries no publish target snapshot.");
                action = new CutModelAction(job.CutModelPublish.ToPublishTarget(), job.CutModelPublish.DeleteOtherLocations);
            }
            else if (item.Name == ExportNwcAction.DisplayName)
            {
                // Same self-contained reasoning as Initiate. Note what the ELSE at the bottom of
                // this chain would do without this branch: ActionRegistry.Create falls back to the
                // first registered action, so a scheduled Export NWC would quietly run Get Site
                // Location & PBP and report success.
                if (job.ExportNwcSpec is null)
                    throw new InvalidOperationException(
                        "Job selects 'Export NWC' but carries no options snapshot.");
                action = new ExportNwcAction(job.ExportNwcSpec, uiApp.Application.VersionNumber);
            }
            else if (item.Name == ExportIfcDataAction.DisplayName)
            {
                // Same as Export NWC above: without this branch the registry fallback would run the
                // first registered action instead and report success.
                if (job.ExportIfcSpec is null)
                    throw new InvalidOperationException(
                        "Job selects 'Export IFC + Data' but carries no options snapshot.");
                action = new ExportIfcDataAction(job.ExportIfcSpec);
            }
            else if (item.Name == BatchInteropAction.DisplayName)
            {
                // Built from the job's own snapshot, same reasoning as Initiate above — a
                // headless run must be self-contained. Missing AIT on the machine that runs
                // this is not fatal here: BatchEngine.ProcessModel's per-model catch turns it
                // into an ERROR row instead of aborting the whole job.
                if (string.IsNullOrWhiteSpace(job.InteropChecksetPath))
                    throw new InvalidOperationException(
                        "Job selects 'Batch Interop' but carries no checkset path.");
                action = new BatchInteropAction(
                    job.InteropChecksetPath, job.InteropChecksetExaminesLinks);
            }
            else if (item.Name == RelinkLinksAction.DisplayName)
            {
                if (job.RelinkSpec is null)
                    throw new InvalidOperationException(
                        "Job selects 'Internal Relink' but carries no relink input snapshot.");
                // Scan the spec's target folders now (headless-safe — token works headless) so the
                // pass is self-contained. Hosts are the job's models, not the spec's host fields.
                var client = new Forma.DataManagementClient(new Auth.SessionTokenProvider());
                Dictionary<string, List<RelinkTarget>> targets =
                    Commands.RelinkModelCommand.BuildTargetMap(client, job.RelinkSpec, out _);
                if (targets.Count == 0)
                    throw new InvalidOperationException(
                        "Internal Relink: no target models found in the spec's target folders.");
                action = new RelinkLinksAction(job.RelinkSpec.OldCode, job.RelinkSpec.NewCode,
                    job.RelinkSpec.Separator, job.RelinkSpec.MatchByExactName, targets);
            }
            else if (item.Name == CorrectWorksetAction.DisplayName)
            {
                if (job.CorrectWorksetSpec is null)
                    throw new InvalidOperationException(
                        "Job selects 'Correct Workset' but carries no input snapshot.");
                CorrectWorksetInputSet spec = job.CorrectWorksetSpec;
                var cw = new CorrectWorksetAction(spec.Rules, spec.ApplyLinkRule, spec.LinkKeyword,
                    spec.Fix, spec.ApplyCadRule, spec.CadWorksetName)
                {
                    // Interactive Run-now prompts per model; a headless/scheduled run has nobody to
                    // approve, so the callback stays null and every rename auto-approves (logged).
                    ApprovalCallback = interactive
                        ? (doc, proposals) =>
                        {
                            using var approval = new UI.WorksetRenameApprovalDialog(doc.Title, proposals);
                            approval.ShowDialog();
                        }
                        : null
                };
                action = cw;
            }
            else
            {
#if EDITION_DRW
                // A DRW action is a saved preset; one deleted since the schedule was saved is
                // reported and skipped like a missing script set, and the other items still run.
                try { action = ActionRegistry.Create(item.Name); }
                catch (InvalidOperationException)
                {
                    missingSets.Add(item.Name);
                    continue;
                }
#else
                action = ActionRegistry.Create(item.Name);
#endif
            }
            actions.Add(action);
        }

        // Build one engine per task now the count is known, so each stamps its "Task" column
        // "k of N". All passes share ONE run timestamp so their tracking rows go to a single
        // <dest>\Log\<stamp>_<project>_<schedule name>.csv file. For an auto-split run every sibling
        // chunk carries the SAME LogRunStamp, so all their instances resolve to that one unified file.
        DateTime runStamp = job.LogRunStamp ?? DateTime.Now;
        var passes = new List<UI.BatchRunnerWindow.Pass>();
        for (int i = 0; i < actions.Count; i++)
        {
            var engine = new BatchEngine(uiApp, actions[i], destFolder, projectLog, job.Sync,
                logNameRun: job.Name, taskIndex: i + 1, taskCount: actions.Count, runStamp: runStamp);
            passes.Add(new UI.BatchRunnerWindow.Pass(engine, combined));
        }
        return passes;
    }

    /// <summary>
    /// Headless run of a job that contains script sets — executes from an ExternalEvent's
    /// <c>Execute</c> (Dynamo hangs inside Idling) via the self-dismissing modal runner with the
    /// summary suppressed (no one to click it). The runner opens script-pass models ACTIVATED,
    /// which Dynamo needs. Completion mapping: LeaveOpen keeps Revit up with the models open;
    /// anything else runs as Close files (so LastRunAt can be stamped) and then quits Revit —
    /// the same "headless always exits" behaviour as the action-only path.
    /// </summary>
    private sealed class ModalJobHandler : IExternalEventHandler
    {
        private readonly ScheduledJob _job;
        public ModalJobHandler(ScheduledJob job) => _job = job;
        public string GetName() => "KhBatchRunner scheduled modal job";

        public void Execute(UIApplication app)
        {
            bool leaveOpen = _job.Completion == BatchCompletion.LeaveOpen;
            string destFolder = string.IsNullOrWhiteSpace(_job.DestinationFolder)
                ? DefaultDestinationFolder() : _job.DestinationFolder;
            string projectLog = string.IsNullOrWhiteSpace(_job.ProjectLogName) ? _job.Name : _job.ProjectLogName;
            try
            {
                // Mixed job (REST-only steps + script sets): run the REST pulls up front.
                foreach (string n in RestOnlySteps.Run(_job, destFolder, projectLog, StampOf(_job)))
                    WriteNote(destFolder, projectLog, _job.Name, n, "INFO");

                ModelPreset combined = ModelPresetCombiner.Combine(
                    _job.Name, _job.ModelSetIds, _job.ModelSetNames, _job.AdHocModels, out _);
                if (combined.Models.Count == 0)
                {
                    WriteNote(destFolder, projectLog, _job.Name, $"No models resolved for job '{_job.Name}'.");
                    return;
                }
                List<UI.BatchRunnerWindow.Pass> passes =
                    BuildJobPasses(app, _job, destFolder, projectLog, combined, interactive: false, out List<string> missingSets);
                if (missingSets.Count > 0)
                    WriteNote(destFolder, projectLog, _job.Name,
                        "Script set(s) or preset(s) not found, skipped: " + string.Join(", ", missingSets));
                if (passes.Count == 0)
                {
                    WriteNote(destFolder, projectLog, _job.Name, "Nothing to run — no action or script set resolved.");
                    return;
                }

                IReadOnlyList<UI.BatchRunnerWindow.PassResult> results = UI.BatchRunnerWindow.RunModal(
                    app, passes, $"Scheduled: {_job.Name}",
                    leaveOpen ? BatchCompletion.LeaveOpen : BatchCompletion.CloseDocuments,
                    showSummary: false,
                    // The scheduled parent gets the same visible bar/log as a normal batch and it
                    // closes itself. Auto-split child Revits stay invisible; their parent shows one
                    // unified RunWatchWindow instead of one dialog per group.
                    showProgress: !_job.IsEphemeralChunk);
                if (results.Count > 0)
                {
                    RecordRunFinished(_job);
                    if (!_job.IsEphemeralChunk && !leaveOpen)
                        UI.RunNotification.ShowRunFinished(_job.Name,
                            results.Sum(r => r.Result.SucceededCount),
                            results.Sum(r => r.Result.FailedCount),
                            // Every pass of one run shares a tracking file; the first names it.
                            results[0].TrackingLogPath);
                }
            }
            catch (Exception ex)
            {
                WriteNote(destFolder, projectLog, _job.Name, "Scheduled run failed: " + ex.Message);
            }
            finally
            {
                try { _hlScriptEvent?.Dispose(); } catch { /* best effort */ }
                _hlScriptEvent = null;
                _hlScriptHandler = null;
                if (!leaveOpen) QuitRevit();
            }
        }
    }

    /// <summary>
    /// Whether this job may be auto-split into parallel group instances. Beyond "a group size is
    /// set", the one hard incompatibility is <see cref="BatchCompletion.LeaveOpen"/>: every group
    /// is a separate Revit that must quit when its models are done, so a split run can never leave
    /// anything open. The dialogs now grey the box out for that disposition, but a schedule saved
    /// before that rule can still carry both — such a job used to split anyway and have its
    /// disposition silently overwritten per chunk. Run it single-process instead, and say so.
    /// </summary>
    /// <summary>Whether <paramref name="job"/> runs Batch Interop — the action that always gets a
    /// supervised child Revit, however few models it has.</summary>
    private static bool RunsInterop(ScheduledJob job) =>
        !string.IsNullOrWhiteSpace(job.InteropChecksetPath) &&
        job.ActionNames.Contains(BatchInteropAction.DisplayName);

    private static bool CanSplit(ScheduledJob job, IReadOnlyList<CloudModel> hosts,
        string destFolder, string projectLog, string revitVersionNumber)
    {
        if (job.GroupSize == 0) return false;

        // Batch Interop splits as of 1.3.7, but only where the Model Checker bridge is verified.
        string? interopReason = Interop.InteropSplitPolicy.BlockedReason(job, revitVersionNumber);
        if (interopReason != null)
        {
            WriteNote(destFolder, projectLog, job.Name,
                "Parallel groups skipped — " + interopReason +
                " Running all models in this one process instead.", "INFO");
            return false;
        }

        if (job.Completion == BatchCompletion.LeaveOpen)
        {
            WriteNote(destFolder, projectLog, job.Name,
                "Parallel groups skipped — 'Leave files open' needs a single Revit instance. " +
                "Running all models in this one process instead.", "INFO");
            return false;
        }

        // Internal Relink is safe to split unless one host is also one of its target link models.
        // The dedicated interactive command checks this before launch; scheduled/general Batch Run
        // must enforce the same rule here or the safety policy changes with the entry point.
        if (job.RelinkSpec != null && job.ActionNames.Contains(RelinkLinksAction.DisplayName))
        {
            try
            {
                var client = new Forma.DataManagementClient(new Auth.SessionTokenProvider());
                Dictionary<string, List<RelinkTarget>> targets =
                    Commands.RelinkModelCommand.BuildTargetMap(client, job.RelinkSpec, out _);
                string? reason = RelinkSplitGuard.SelfLinkingHost(hosts, targets);
                if (reason != null)
                {
                    WriteNote(destFolder, projectLog, job.Name,
                        "Parallel groups skipped — " + reason +
                        " Running all models in this one process instead.", "INFO");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Do not multiply a target-scan/auth failure across child processes. The ordinary
                // single-process path will report the same configuration error through its normal
                // action-building/logging flow.
                WriteNote(destFolder, projectLog, job.Name,
                    "Parallel groups skipped because Internal Relink targets could not be checked: " +
                    ex.Message + " Running in one Revit instead.", "INFO");
                return false;
            }
        }

        return true;
    }

    /// <summary>Fallback destination when a job names none. Internal so <see cref="ChunkLauncher"/>
    /// resolves the SAME folder the child instances will, and its group notes land in their log.</summary>
    /// <summary>Kept as a thin alias so this file's many call sites read unchanged; the definition
    /// now lives on <see cref="ScheduleStore"/>, which non-Revit hosts can reach.</summary>
    internal static string DefaultDestinationFolder() => ScheduleStore.DefaultDestinationFolder();

    /// <summary>
    /// Makes sure this run has an answer to "what can the signed-in Autodesk account open?", so
    /// <see cref="Presets.ModelPresetCombiner"/> can name inaccessible models before the run pays
    /// to discover them. Cheap when a window already warmed it; never throws; never blocks a run.
    /// </summary>
    private static void WarmAccountAccess()
    {
        try
        {
            if (Presets.AccountAccess.Cached is { Reachable: true }) return;
            Presets.AccountAccess.Refresh(new Forma.DataManagementClient(new Auth.SessionTokenProvider()));
        }
        catch { /* an unknown account is Unknown everywhere — the run proceeds exactly as before */ }
    }

    /// <summary>The run's shared stamp — the same value every pass of this run uses, so a step's
    /// rows land in the run's own tracking log rather than a file of their own. A chunk carries
    /// its parent's stamp; an ordinary run stamps itself. Mirrors the derivation in RunPasses.</summary>
    private static DateTime StampOf(ScheduledJob job) => job.LogRunStamp ?? DateTime.Now;

    /// <summary>
    /// The end of a job whose every step was REST-only (Get File Log, Get Data Extract).
    ///
    /// <para>Deliberately the SAME mechanism a model run uses — <c>ShowAndWait</c>, so Windows has
    /// accepted the item before Revit is allowed to exit, and the same retention split: a run that
    /// delivered expires on the hour, a run that did not waits to be dismissed. What differs is the
    /// body, which names what each step produced rather than a model-task count, because there were
    /// no model tasks.</para>
    /// </summary>
    private static void NotifyRestOnlyFinished(string runName, RestOnlyOutcome outcome)
    {
        try
        {
            UI.RunNotification.ShowAndWait(
                UI.RunNotification.FinishedTitle(runName, outcome.StepsFailed),
                outcome.Body,
                outcome.Failed,
                outcome.TrackingLog);
        }
        catch (Exception ex)
        {
            // A notification must never take down the run it reports on — and this one runs with
            // Revit's shutdown already requested, which is the worst possible moment to throw.
            RunTrace.Mark("REST-only finish notification failed: " + ex.Message);
        }
    }

    /// <summary>The same summary for an interactive "Run now", where a dialog is the right shape.</summary>
    private static void ShowRestOnlySummary(string title, RestOnlyOutcome outcome)
    {
        try
        {
            var dialog = new Autodesk.Revit.UI.TaskDialog(title)
            {
                MainInstruction = outcome.Failed ? "Finished — with something to look at." : "Finished.",
                MainContent = string.Join("\n\n", outcome.Summaries)
            };
            if (outcome.TrackingLog.Length > 0)
                dialog.ExpandedContent = "Log: " + outcome.TrackingLog;
            dialog.Show();
        }
        catch (Exception ex)
        {
            RunTrace.Mark("REST-only summary dialog failed: " + ex.Message);
        }
    }
    private static void WriteNote(string destFolder, string projectLog, string model, string message, string status = "ERROR")
    {
        string path = TrackingCsvLogger.PathFor(destFolder, projectLog, "ScheduleError", DateTime.Now);
        var logger = new TrackingCsvLogger(path, "ScheduleError") { CurrentModel = model };
        logger.Log(new Dictionary<string, string> { ["Status"] = status, ["Message"] = message });
    }
}
