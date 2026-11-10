using System.IO;
using System.Threading;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;

// Disambiguated from System.Diagnostics, which this file also uses indirectly.
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

internal sealed partial class RunSupervisor
{
    /// <summary>
    /// Reconciles the round that just ended and starts the next one if anything is still missing.
    /// Returns false when the run is done — either everything has a row, or every survivor has
    /// exhausted <see cref="MaxRounds"/>.
    /// </summary>
    private bool StartNextRoundIfNeeded()
    {
        List<CloudModel> missing = MissingModels();
        if (missing.Count == 0)
        {
            Note("INFO", $"all {_manifest.AllModels.Count} model(s) accounted for after " +
                         $"round {_manifest.RoundNumber}.");
            return false;
        }

        if (_manifest.RoundNumber >= MaxRounds)
        {
            foreach (CloudModel m in missing)
            {
                RunManifestStore.Update(_manifest, x => x.Unrecoverable.Add(ModelKey.For(m)));
                RunNote.Write(_manifest.TrackingLogPath, _manifest.Name, m.DisplayName, "ERROR",
                    $"unrecoverable — no result after {MaxRounds} attempts; " +
                    $"see {RunTrace.LogPath} for the stage each attempt stopped at.");
            }
            return false;
        }

        int next = _manifest.RoundNumber + 1;

        // The escalation ladder, one rung per round:
        //
        //   2 — RE-CHUNK at the ORIGINAL group size. Round 2 used to put every survivor in a single
        //       instance, which undid the very thing the group size was chosen for: a run split
        //       10/10/10/10 that loses 2+3+4+8 would retry all 17 in ONE Revit — a heavier session
        //       than the original that just failed. It now reruns them as 10/7, the shape asked for.
        //       A run with no configured split (GroupSize 0) keeps the single-instance behaviour,
        //       since there is no original size to honour.
        //
        //   3 — RE-SHAPE TO THE MACHINE. Fit the survivors into exactly one wave of instances, so
        //       a model that failed under contention gets a run with the machine to itself before
        //       the expensive isolation round.
        //
        //   4 — ISOLATE, one model per instance: the best chance for a heavy model and the only way
        //       to identify which one is toxic.
        //
        // The original size comes from the MANIFEST, which the launcher stamped with the number it
        // actually split at. Asking BaseJob was wrong: interactive Batch Run never writes its
        // dialog's group size back onto the job, so that read returned 0 and round 2 collapsed into
        // one instance — precisely the behaviour this regrouping exists to prevent. BaseJob stays as
        // the fallback for a manifest written before GroupSize existed.
        int original = _manifest.GroupSize > 0 ? _manifest.GroupSize : (_manifest.BaseJob?.GroupSize ?? 0);
        int groupSize = RoundGroupSize(next, original, missing.Count, EffectiveConcurrencyCap());

        int groupCount = ChunkLauncher.GroupCount(missing.Count, groupSize);
        Note("INFO", $"round {next} of {MaxRounds} — rerunning {missing.Count} model(s) with no " +
                     $"result (or only a partial one) in {groupCount} group(s) of up to {groupSize}, " +
                     $"{InstanceBudget.Describe(_manifest.AllModels)}: " +
                     string.Join("; ", missing.Select(m => m.DisplayName)));

        if (next >= MaxRounds) PurgeCacheForLastChance(missing);

        // The manifest's executable, NOT this process's — the supervisor is Revit itself for an
        // in-add-in run, but it is KhBatchController.exe for a standalone one, and defaulting
        // there re-launched the controller as the worker. See RunManifest.RevitExePath.
        List<RunGroup> groups = ChunkLauncher.LaunchChunks(
            _manifest.BaseJob!, missing, groupSize, out string? error, _manifest.RunStamp,
            _manifest.RevitExePath, EffectiveConcurrencyCap());

        if (error != null) Note(groups.Count == 0 ? "ERROR" : "INFO", error);
        if (groups.Count == 0)
        {
            string reason = $"round {next} could not start; {missing.Count} model(s) left unprocessed.";
            StopRun(reason);
            Note("ERROR", reason + " No further retry was launched.");
            return false;
        }

        RunManifestStore.Update(_manifest, m =>
        {
            m.Rounds.Add(new RunRound { Round = next, StartedAt = DateTime.Now, Groups = groups });
            m.CountAttempts(missing);
        });
        return true;
    }

    /// <summary>
    /// Models per instance for <paramref name="nextRound"/> — the escalation ladder, as one pure
    /// function so it can be checked without launching anything.
    ///
    /// <list type="bullet">
    /// <item>round 2 — <paramref name="originalSize"/>, the shape the user asked for;</item>
    /// <item>round 3 — enough per instance that every survivor fits ONE wave of
    /// <see cref="InstanceBudget.MaxConcurrent"/> Revits: 31 survivors on a 4-instance machine
    /// become 4 groups of 8, not 31 groups of 1;</item>
    /// <item>round 4 — one model each.</item>
    /// </list>
    /// </summary>
    internal static int RoundGroupSize(int nextRound, int originalSize, int missingCount,
        int maxConcurrentInstances = 0)
    {
        if (nextRound >= MaxRounds) return 1;
        if (nextRound == MaxRounds - 1)
        {
            int cap = maxConcurrentInstances > 0
                ? maxConcurrentInstances
                : Math.Max(1, InstanceBudget.MaxConcurrent());
            return Math.Max(1, (int)Math.Ceiling(missingCount / (double)cap));
        }
        return originalSize > 0 ? originalSize : Math.Max(1, missingCount);
    }

    /// <summary>
    /// Models the run was asked to process that are not finished, and have not already been written
    /// off. Matching is by display name because that is what the tracking log records; a name that
    /// appears twice in the model list cannot be told apart from its twin, so it is treated as done
    /// rather than risk running the wrong file repeatedly.
    ///
    /// "Finished" means every action produced a row, not merely that some did — see
    /// <see cref="TrackingCsvReader.CompletedModels"/>. Under the former policy, a model whose
    /// instance was force-closed halfway through its action list counted as done and was silently
    /// shipped part-processed.
    /// </summary>
    private List<CloudModel> MissingModels()
    {
        HashSet<string> done = TrackingCsvReader.CompletedModels(_manifest.TrackingLogPath);
        var ambiguous = _manifest.AllModels
            .GroupBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new List<CloudModel>();
        foreach (CloudModel m in _manifest.AllModels)
        {
            if (done.Contains(m.DisplayName)) continue;
            if (_manifest.Unrecoverable.Contains(ModelKey.For(m))) continue;
            if (ambiguous.Contains(m.DisplayName))
            {
                Note("INFO", $"'{m.DisplayName}' appears more than once in this run's model list, so " +
                             "its result cannot be matched by name — not retried.");
                continue;
            }
            missing.Add(m);
        }
        return missing;
    }

    /// <summary>
    /// Before the last attempt, delete the model's local cache file: a half-written cache left by a
    /// crashed download is a plausible reason a retry keeps failing, and clearing the cloud cache is
    /// exactly what changed the behaviour when this was hit in the field.
    ///
    /// Only for a run that cannot have pending changes — a read-only Check, or an explicit dry run.
    /// A crashed instance of a sync-capable action may hold unsynced work in that cache, and deleting
    /// it would silently discard it.
    /// </summary>
    private void PurgeCacheForLastChance(IReadOnlyList<CloudModel> models)
    {
        // NoSync is the one state that guarantees nothing in that cache is owed to the central.
        // A Check writes nothing either, but the job doesn't record which actions are read-only,
        // and guessing wrong here loses a user's work — so err toward keeping the cache.
        if (_manifest.BaseJob?.Sync != Batch.SyncMode.NoSync) return;

        foreach (CloudModel m in models)
        {
            string? path = ChildLiveness.ResolveCachePath(m, _manifest.RevitVersion);
            if (path == null) continue;
            try
            {
                File.Delete(path);
                Note("INFO", $"cleared the local cloud cache for '{m.DisplayName}' before its final attempt.");
            }
            catch (Exception ex)
            {
                Note("INFO", $"could not clear the cache for '{m.DisplayName}': {ex.Message}");
            }
        }
    }

    private void Note(string status, string message) =>
        RunNote.Write(_manifest.TrackingLogPath, _manifest.Name, "", status, message);

    /// <summary>
    /// Says so when an instance disappears WITHOUT having asked to close — a crash, not a finish.
    ///
    /// <para>Until 1.3.8.8 this path was silent. On 2026-08-15 a Revit running
    /// <c>SITE2A-BLDE-FFE.rvt</c> took an access violation inside the model loader
    /// (<c>captureTryCrash 0xc0000005</c> in <c>LoadFromStorage</c>) with <b>569 MB</b> of RAM left,
    /// while its three neighbours held 44, 29 and 23 GB. The run recovered it in round 2 and finished
    /// clean — so the ONLY trace of a crashed Revit in the whole log was the round-2 line saying a
    /// model had "no result". A recovery that hides what it recovered from cannot be tuned.</para>
    ///
    /// <para>What it reports is what can be attributed with certainty: the pid's own last trail line
    /// (<see cref="RunTrace"/> is pid-tagged), and free RAM now. It does NOT go looking in Revit's
    /// journals — those are named sequentially rather than by pid, and guessing which one belonged to
    /// a dead child is exactly the attribution <see cref="ChildLiveness"/> declined to do. The
    /// journal is named as the place to look instead.</para>
    ///
    /// <para><b>The stage comes from a FRESH poll of the trail, not from the watch</b> (fixed
    /// 1.3.8.9). As shipped in 1.3.8.8 this read <c>watch.LastStage</c>, which only
    /// <see cref="ChildLiveness.Check"/> updates — and <see cref="PolicePace"/> takes the exit branch
    /// BEFORE calling it. The stage was therefore up to one poll stale and never included the burst
    /// of lines a child writes on its way out, so two instances that had written
    /// <c>batch end: 2 ok, 0 failed</c> and posted their close were both reported as crashes on
    /// 2026-08-15. A finished child read as a crash is the same error as
    /// <see cref="ChildLiveness.Verdict.ShutdownStalled"/> exists to prevent; the predicate was right
    /// and its input was wrong.</para>
    ///
    /// <para>INFO, not ERROR: the model has not failed yet — the next round is about to retry it, and
    /// on 2026-08-15 that retry succeeded. This is the sentence that says why there was a next
    /// round.</para>
    /// </summary>
    private void ReportUnexpectedExit(RunGroup group)
    {
        try
        {
            // One more incremental read before judging: the child's last lines are written between
            // the tick that saw it alive and the tick that finds it gone, so the poll at the top of
            // the loop is by definition too early to have them.
            _tail.Poll();

            _watches.TryGetValue(group.Pid, out ChildLiveness.Watch? watch);
            string? lastStage = _tail.For(group.Pid)?.Stage ?? watch?.LastStage;
            if (ChildLiveness.IsShuttingDown(lastStage)) return;   // asked to close: a finish

            string stage = string.IsNullOrEmpty(lastStage)
                ? "before it wrote any stage at all"
                : $"at '{lastStage}'";
            string free = InstanceBudget.FreeMemoryNote();
            string memory = free.Length == 0 ? "" : $" The machine has {free} now.";

            Note("INFO",
                $"the Revit running {group.Models.Count} model(s) (pid {group.Pid}) exited without " +
                $"asking to close — it stopped {stage}. That is a crash or a force-close, not a " +
                $"finished instance; its models have no result and a later round will rerun them." +
                memory + " Revit's own journal for that process holds the cause — look for " +
                "'captureTryCrash' near the end of the journal whose start time matches " +
                $"{group.StartedAt:yyyy-MM-dd HH:mm:ss}.");
        }
        catch (Exception ex)
        {
            RunTrace.Mark($"post-mortem for pid {group.Pid} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pivots the finished run into its matrix workbook(s) — see <see cref="Reporting.RunEndMatrix"/>.
    ///
    /// Wrapped rather than called inline because this runs in a <c>finally</c> whose remaining job is
    /// to hand the run's disposition to the caller: a report writer must not be able to prevent a
    /// completed run from being reported as completed, or a Revit that was told to close from
    /// closing.
    /// </summary>
    private void BuildRunEndMatrices()
    {
        try { Reporting.RunEndMatrix.BuildForRun(_manifest, Note); }
        catch (Exception ex) { RunTrace.Mark("run-end matrix failed: " + ex.Message); }
    }
}
