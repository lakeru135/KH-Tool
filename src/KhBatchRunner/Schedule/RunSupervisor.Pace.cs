using System.Threading;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;

// Disambiguated from System.Diagnostics, which this file also uses indirectly.
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

internal sealed partial class RunSupervisor
{
    /// <summary>
    /// Sets aside children that need attention, records natural process exits, and replaces the
    /// ones that crashed.
    ///
    /// <para>Replacements are COLLECTED here and appended after the loop, never inside it. Appending
    /// to the round mid-<c>foreach</c> is what broke every 1.4.2 run that lost a child — see
    /// <see cref="CurrentGroups"/>. The read-only return type stops it happening again; this shape is
    /// what makes the read-only type sufficient rather than merely annoying.</para>
    /// </summary>
    private void PolicePace()
    {
        var replacements = new List<RunGroup>();

        // Before the sweep: a crashed child ended here is then found gone below, in this same tick.
        SweepCrashReporters();

        foreach (RunGroup group in CurrentGroups())
        {
            if (!IsRunning(group)) continue;

            if (!ChildLiveness.IsAlive(group.Pid))
            {
                ReportUnexpectedExit(group);
                // Before the replacement is planned: a model given up on here gets its ERROR row now,
                // so the replacement does not carry it, and a model retried here goes LAST in it.
                string? crashedOpening = HandleOpenCrash(group);
                RunManifestStore.Update(_manifest, _ => group.ExitedAt = DateTime.Now);
                _watches.Remove(group.Pid);
                PlanReplacement(group, replacements, crashedOpening);
                continue;
            }

            if (!_watches.TryGetValue(group.Pid, out ChildLiveness.Watch? watch)) continue;

            ChildLiveness.Judgement judgement = ChildLiveness.Check(watch, _tail, group.Models);

            // Finished, but its close is blocked: end it rather than leave it for the user. The
            // ended process is found gone on the next tick and takes the ordinary exit path, which
            // AskedToClose recognises as a finish, not a crash.
            if (ForceCloseIfFinished(group, watch)) continue;

            switch (judgement.Verdict)
            {
                case ChildLiveness.Verdict.Wedged:
                case ChildLiveness.Verdict.OverCeiling:
                case ChildLiveness.Verdict.NeverStarted:
                    DeferForAttention(group, judgement);
                    continue;

                // Its models all ran; it is only slow to disappear. Say so once and carry on —
                // stopping the run here would cancel every queued group over an instance that has
                // nothing left to do, which is exactly what happened on 2026-08-11.
                case ChildLiveness.Verdict.ShutdownStalled:
                    if (!group.SlowExitReported)
                    {
                        RunManifestStore.Update(_manifest, _ => group.SlowExitReported = true);
                        RunTrace.Mark($"supervisor: pid {group.Pid} {judgement.Reason} — " +
                                      "continuing, its models are done");
                        Note("INFO", $"pid {group.Pid} {judgement.Reason}. Its models all ran and " +
                                     "are in this log; the run carries on.");
                    }
                    break;

                // Slow inside a document open. Also reported and carried on with, for the same
                // reason: on 2026-08-13 an instance stopped here at 10 minutes went on to open the
                // model at 15m55s and finish all six of its models. A genuinely hung open is caught
                // by the per-model ceiling, which does stop the run.
                case ChildLiveness.Verdict.OpenSlow:
                    if (!string.Equals(group.SlowOpenReportedFor, watch.LastStage,
                                       StringComparison.Ordinal))
                    {
                        string stage = watch.LastStage;
                        RunManifestStore.Update(_manifest, _ => group.SlowOpenReportedFor = stage);
                        RunTrace.Mark($"supervisor: pid {group.Pid} {judgement.Reason} — " +
                                      "continuing, an open is allowed to be slow");
                        Note("INFO", $"pid {group.Pid} {judgement.Reason}. Nothing has been stopped; " +
                                     "the run carries on and this model is still opening.");
                    }
                    break;

                // Slow inside an ACTION. Same treatment as a slow open, and the same evidence
                // behind it: on 2026-08-18 an instance stopped here at ten minutes finished the
                // action at 26 minutes and reported 36 ok, 0 failed — after its run had been
                // cancelled. The per-model ceiling still catches an action that never returns.
                case ChildLiveness.Verdict.ActionSlow:
                    if (!string.Equals(group.SlowActionReportedFor, watch.LastStage,
                                       StringComparison.Ordinal))
                    {
                        string actionStage = watch.LastStage;
                        RunManifestStore.Update(_manifest, _ => group.SlowActionReportedFor = actionStage);
                        RunTrace.Mark($"supervisor: pid {group.Pid} {judgement.Reason} — " +
                                      "continuing, an action is allowed to be slow");
                        Note("INFO", $"pid {group.Pid} {judgement.Reason}. Nothing has been stopped; " +
                                     "the run carries on and this action is still running.");
                    }
                    break;
            }
        }

        // Only now that nothing is enumerating the round.
        if (replacements.Count > 0)
        {
            AddGroups(replacements);
            FillFreeSlots();   // the crashed children freed the slots these need
        }
    }

    /// <summary>
    /// A group counts as finished for round bookkeeping once its child has asked to close, even
    /// while the process lingers.
    ///
    /// Without this a slow exit holds a concurrency slot — and therefore a queued group — for as
    /// long as the shutdown takes, which on the 2026-08-11 report was over ten minutes with nothing
    /// happening. The models are already logged, so nothing is lost by letting the next group start;
    /// the exiting Revit is releasing memory, not claiming it.
    ///
    /// <para>Deliberately NOT true of a group deferred by <see cref="DeferForAttention"/>, even
    /// though that group is also never going to finish. This predicate answers "may its slot be
    /// reused", and a deferred Revit is alive and still holding its memory, where an exiting one is
    /// releasing it. <see cref="RoundFinished"/> asks the other question and does count it.</para>
    /// </summary>
    private bool IsDoneBarExiting(RunGroup group) =>
        group.SlowExitReported || AskedToClose(group);

    /// <summary>
    /// Whether this group's child has asked to close at ANY point since it was launched — not merely
    /// whether its latest trail line says so. The three callers (slot reuse, the crash post-mortem,
    /// replacement planning) used to read only the last line, and on 2026-11-23 a dialog line written
    /// four seconds after the close hid it from all three. See
    /// <see cref="ChildLiveness.HasAskedToClose"/>.
    /// </summary>
    private bool AskedToClose(RunGroup group)
    {
        if (group.Pid <= 0) return false;
        if (_watches.TryGetValue(group.Pid, out ChildLiveness.Watch? w))
            return ChildLiveness.HasAskedToClose(w, _tail);
        if (ChildLiveness.IsShuttingDown(_tail.For(group.Pid)?.Stage)) return true;
        DateTime? posted = _tail.ClosePosted(group.Pid);
        return posted.HasValue && group.StartedAt is DateTime started && posted.Value >= started;
    }

    /// <summary>Children this supervisor has already tried to force-close, so a process that refuses
    /// to die is reported once rather than every 15 seconds.</summary>
    private readonly HashSet<int> _forceCloseTried = new();

    /// <summary>
    /// Ends a child that has FINISHED — every model in its group has a row — and has been trying to
    /// close for <see cref="ChildLiveness.ForceCloseGrace"/>. The user's rule (2026-11-24): kill only
    /// what is finished or dead. Returns true when it acted.
    /// </summary>
    private bool ForceCloseIfFinished(RunGroup group, ChildLiveness.Watch watch)
    {
        if (watch.ShutdownSince == default || _forceCloseTried.Contains(group.Pid)) return false;
        DateTime now = DateTime.Now;
        if (now - watch.ShutdownSince < ChildLiveness.ForceCloseGrace) return false;

        HashSet<string> done = TrackingCsvReader.CompletedModels(_manifest.TrackingLogPath);
        if (!ChildLiveness.ShouldForceClose(watch, now, group.Models, done)) return false;

        _forceCloseTried.Add(group.Pid);
        string why = $"every one of its {group.Models.Count} model(s) has a result and it asked to close " +
                     $"{(int)(now - watch.ShutdownSince).TotalMinutes} min ago, but has not exited " +
                     $"(last trail line: '{watch.LastStage}')";
        bool gone = RevitTerminator.Terminate(group.Pid, why);
        Note("INFO", gone
            ? $"pid {group.Pid} finished its models but its close was blocked (usually a Revit dialog). " +
              "It was closed by the supervisor; nothing was lost — its results are in this log."
            : $"pid {group.Pid} finished its models but could not be closed — it is still open.");
        return gone;
    }

    /// <summary>
    /// Ends any child that crashed into Autodesk's crash reporter, and closes reporter windows that
    /// crashed children left behind. Runs before the per-group sweep, so a child ended here is found
    /// gone straight away and takes the ordinary crash path (post-mortem, open/run-crash rule,
    /// replacement). See <see cref="CrashReporterWatch"/>.
    /// </summary>
    private void SweepCrashReporters()
    {
        try
        {
            IReadOnlyList<CrashReporterWatch.Reporter> reporters = CrashReporterWatch.Probe();
            if (reporters.Count == 0) return;

            var launched = new HashSet<int>(_manifest.Rounds.SelectMany(r => r.Groups)
                .Select(g => g.Pid).Where(p => p > 0));
            foreach (CrashReporterWatch.Reporter r in reporters)
            {
                switch (CrashReporterWatch.Decide(r, launched, ChildLiveness.IsAlive))
                {
                    case CrashReporterWatch.Action.EndChildAndReporter:
                        RunTrace.Mark($"supervisor: pid {r.ParentPid} crashed — Autodesk's crash reporter " +
                                      $"(senddmp pid {r.Pid}) is waiting for a click");
                        RevitTerminator.Terminate(r.ParentPid, "crashed; its crash reporter is showing");
                        RevitTerminator.Terminate(r.Pid, $"crash reporter of pid {r.ParentPid}");
                        break;

                    case CrashReporterWatch.Action.EndReporter:
                        RevitTerminator.Terminate(r.Pid,
                            $"crash reporter left on screen by pid {r.ParentPid}, which has already exited");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            RunTrace.Mark("crash-reporter sweep failed: " + ex.Message);
        }
    }

    /// <summary>How many times one group may have its Revit replaced within a round.</summary>
    /// <remarks>
    /// ONE. The thing being recovered from is a crash inside the first cloud open, which is
    /// frequent — 10 of 31 first opens on 2026-08-18 — but also, for some models, perfectly
    /// repeatable: <c>SITE2A-BLDA-ELE.rvt</c> took its Revit down on all four attempts that night,
    /// including twice in isolation. Unbounded replacement would turn a reproducible crash into a
    /// launch loop. Past the bound the escalation ladder takes over, which is what it is for.
    /// </remarks>
    internal const int MaxRelaunchesPerGroup = 1;

    /// <summary>
    /// Which of a dead group's models a replacement should carry — the ones with no complete row —
    /// or an empty list when there is nothing to do or the group has used up its replacements.
    ///
    /// <para>Pure, and separate from the launching, for the same reason as <see cref="SlotsToFill"/>:
    /// the two ways to get this wrong are re-running models that already finished (duplicate rows in
    /// the tracking log the whole run is judged from) and launching forever against a model that
    /// crashes Revit every time. Neither belongs in code that can only be exercised by starting a
    /// Revit.</para>
    /// </summary>
    /// <param name="completed">Display names with a complete row set —
    /// <see cref="TrackingCsvReader.CompletedModels"/>, the same source <see cref="MissingModels"/>
    /// reads, so a replacement and a retry round can never disagree about what is left.</param>
    /// <param name="last">The model Revit died opening, when known: it goes at the END, so the
    /// models it was blocking finish before it is tried again.</param>
    internal static List<CloudModel> RelaunchModels(RunGroup group, ISet<string> completed,
        int maxRelaunches, string? last = null)
    {
        if (group.RelaunchCount >= maxRelaunches) return new List<CloudModel>();
        List<CloudModel> left = group.Models.Where(m => !completed.Contains(m.DisplayName)).ToList();
        if (last == null) return left;
        return left.Where(m => m.DisplayName != last)
                   .Concat(left.Where(m => m.DisplayName == last))
                   .ToList();
    }

    /// <summary>What to do about a model Revit died while opening.</summary>
    internal enum OpenCrashOutcome
    {
        /// <summary>Known workshared: today's retries, unchanged (its first-open crashes have been
        /// transient — on 2026-08-15 one ran clean the next round).</summary>
        Ladder,
        /// <summary>Once more, opened plain, last in a fresh Revit.</summary>
        Retry,
        /// <summary>It has already had its plain retry this run: an ERROR row, and the run moves on.</summary>
        GiveUp,
    }

    /// <summary>
    /// The user's rule (2026-11-03): a model that crashes Revit on open is retried ONCE in safe open —
    /// no workset configuration — and if it crashes again it is skipped with an ERROR while every other
    /// model carries on. Revit-free so the harness pins it.
    /// </summary>
    /// <param name="duringOpen">False for a crash while the model was already open — an action
    /// running on it (2.1.1.1). The workshared ladder is about how a model is OPENED, so it applies
    /// to open crashes only; a crash inside an export gets the plain user rule: once more, then
    /// skipped.</param>
    internal static OpenCrashOutcome OpenCrashPolicy(int priorCrashesThisRun, bool knownWorkshared,
        bool duringOpen = true) =>
        duringOpen && knownWorkshared ? OpenCrashOutcome.Ladder
        : priorCrashesThisRun == 0 ? OpenCrashOutcome.Retry
        : OpenCrashOutcome.GiveUp;

    /// <summary>
    /// Which of a dead child's models it died on, and whether it died opening it. Pure, so the harness
    /// pins it. Reads the child's last MODEL-level line (<see cref="RunTrace.Tail.LastModelLine"/>), not
    /// its last line: a dialog or lifecycle line written after it would otherwise hide the model.
    ///
    /// <para>A model that already has a complete row is never blamed — a child that died while
    /// CLOSING a finished model did not fail that model, and its next model was never started.</para>
    /// </summary>
    internal static (CloudModel Model, bool DuringOpen)? CrashedOn(string? lastModelLine,
        IReadOnlyList<CloudModel> groupModels, ISet<string> completed)
    {
        string? field = Batch.BatchStages.ModelField(lastModelLine);
        if (field == null) return null;
        CloudModel? model = groupModels.FirstOrDefault(m => Batch.BatchStages.SameModel(field, m.DisplayName));
        if (model == null || completed.Contains(model.DisplayName)) return null;
        return (model, Batch.BatchStages.IsOpenStage(lastModelLine));
    }

    /// <summary>
    /// A dead child: record the crash against the model it was on — opening it or running an action
    /// on it — and either arrange its retry or give it up; see <see cref="OpenCrashPolicy"/>. Returns
    /// the model's display name when it is retried (so the replacement carries it last), else null.
    /// </summary>
    private string? HandleOpenCrash(RunGroup group)
    {
        try
        {
            if (AskedToClose(group)) return null;   // a finish, however abrupt — nothing crashed

            _watches.TryGetValue(group.Pid, out ChildLiveness.Watch? watch);
            string? lastStage = _tail.LastModelLine(group.Pid)?.Stage
                                ?? _tail.For(group.Pid)?.Stage ?? watch?.LastStage;
            HashSet<string> done = TrackingCsvReader.CompletedModels(_manifest.TrackingLogPath);
            (CloudModel Model, bool DuringOpen)? hit = CrashedOn(lastStage, group.Models, done);
            if (hit == null) return null;
            CloudModel model = hit.Value.Model;
            bool duringOpen = hit.Value.DuringOpen;
            string name = model.DisplayName;
            // A local model has no cloud open to change, but a crash INSIDE its export is still a
            // crash on that model and gets the same once-more rule.
            if (duringOpen && model.IsLocal) return null;

            string key = ModelKey.For(model);
            int prior = _manifest.OpenCrashes.TryGetValue(key, out int n) ? n : 0;
            RunManifestStore.Update(_manifest, x => x.OpenCrashes[key] = prior + 1);
            bool workshared = duringOpen && Batch.CloudOpenFacts.MarkCrash(model);
            bool wasPlain = Batch.BatchStages.IsPlainOpenStage(lastStage);
            RunTrace.Mark(duringOpen
                ? $"supervisor: pid {group.Pid} died opening {name} " +
                  $"({(wasPlain ? "plain open" : "workset configuration")}; crash {prior + 1} this run)"
                : $"supervisor: pid {group.Pid} died while working on {name} at '{lastStage}' " +
                  $"(crash {prior + 1} this run)");

            switch (OpenCrashPolicy(prior, workshared, duringOpen))
            {
                case OpenCrashOutcome.Retry:
                    Note("INFO", duringOpen
                        ? $"Revit died while opening {name}" +
                          (wasPlain ? " (already a plain open)" : "") +
                          ". It is retried once in a fresh Revit, after the other models, opened " +
                          "plain — no workset configuration. If that crashes too it is skipped."
                        : $"Revit died while working on {name}. It is retried once in a fresh Revit, " +
                          "after the other models. If that crashes too it is skipped.");
                    return name;

                case OpenCrashOutcome.GiveUp:
                    RunManifestStore.Update(_manifest, x => { if (!x.Unrecoverable.Contains(key)) x.Unrecoverable.Add(key); });
                    RunNote.Write(_manifest.TrackingLogPath, _manifest.Name, name, "ERROR",
                        duringOpen
                            ? $"skipped — Revit crashed while opening this model {prior + 1} times this run, " +
                              "the last time in safe open (no workset configuration). The other models carried on. " +
                              $"See {RunTrace.LogPath} and the Revit journal ('captureTryCrash') for the cause."
                            : $"skipped — Revit crashed {prior + 1} times this run on this model, the last " +
                              $"time at '{lastStage}'. The other models carried on. See {RunTrace.LogPath} " +
                              "and the Revit journal ('captureTryCrash') for the cause.");
                    return null;

                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            RunTrace.Mark($"open-crash handling for pid {group.Pid} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Live workers set aside in EARLIER rounds — see <see cref="StrandedInstances"/> for why they
    /// have to be subtracted from the cap. Pure form, so the arithmetic that keeps 1.4.2 from
    /// trading a cancelled run for an oversubscribed one can be checked without processes.
    /// </summary>
    internal static int StrandedCount(IEnumerable<RunRound> rounds, int currentRound,
        Func<RunGroup, bool> isLive)
    {
        int stranded = 0;
        foreach (RunRound round in rounds)
        {
            if (round.Round == currentRound) continue;
            foreach (RunGroup g in round.Groups)
                if (g.NeedsAttention && IsRunning(g) && isLive(g)) stranded++;
        }
        return stranded;
    }

    /// <summary>
    /// Starts a replacement for a group whose Revit vanished, instead of leaving its models to wait
    /// for the round to end.
    ///
    /// <para><b>The wait was the cost.</b> On 2026-08-18 pid 87800 died inside its first cloud open
    /// at 00:53:47; its five models did not run again until the round ended and was re-chunked at
    /// 01:21:39 — <b>28 minutes</b> in which a slot sat empty and nothing was retried. 1.4.1's larger
    /// groups raised the stake from two models to five or ten.</para>
    ///
    /// <para>Only the models with no complete row are re-sent, read from the same tracking log
    /// <see cref="MissingModels"/> uses — a child that crashed on model 7 of 10 must not redo the
    /// first six.</para>
    /// </summary>
    /// <param name="into">Where the replacement goes. NOT the round — <see cref="PolicePace"/> is
    /// enumerating that, and appending to it here is the defect this signature exists to prevent.</param>
    private void PlanReplacement(RunGroup group, List<RunGroup> into, string? crashedOpening = null)
    {
        try
        {
            if (_manifest.State != RunState.Running) return;
            if (group.NeedsAttention) return;             // set aside on purpose, not a crash

            // Asked to close and then went: a finish, however abrupt. ReportUnexpectedExit applies
            // the same test before it calls anything a crash.
            if (AskedToClose(group)) return;

            if (group.RelaunchCount >= MaxRelaunchesPerGroup)
            {
                Note("INFO", $"the replacement for pid {group.Pid}'s group has also gone. Not " +
                             "starting another; its remaining models go to the next round.");
                return;
            }


            HashSet<string> done = TrackingCsvReader.CompletedModels(_manifest.TrackingLogPath);
            List<CloudModel> missing = RelaunchModels(group, done, MaxRelaunchesPerGroup, crashedOpening);
            if (missing.Count == 0) return;               // nothing left, or past the bound

            RunGroup? replacement = ChunkLauncher.PrepareReplacement(
                _manifest.BaseJob!, missing, _manifest.RunStamp,
                CurrentGroups().Count + into.Count + 1, out string? error);
            if (replacement == null)
            {
                Note("INFO", $"could not prepare a replacement for pid {group.Pid}'s group " +
                             $"({error}); its models go to the next round instead.");
                return;
            }

            replacement.RelaunchCount = group.RelaunchCount + 1;
            into.Add(replacement);
            RunTrace.Mark($"supervisor replacing pid {group.Pid}'s group: {missing.Count} model(s) " +
                          "still to run");
            Note("INFO", $"pid {group.Pid} is gone with {missing.Count} model(s) unfinished — a " +
                         "fresh Revit takes them now rather than at the end of the round: " +
                         string.Join("; ", missing.Select(m => m.DisplayName)));
        }
        catch (Exception ex)
        {
            RunTrace.Mark($"replacement for pid {group.Pid} could not be started: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets one group aside and records WHY, leaving its worker open for inspection — and lets the
    /// REST OF THE RUN CARRY ON.
    ///
    /// <para><b>It used to stop everything (until 1.4.2).</b> One child needing attention set
    /// <see cref="RunState.Stopped"/>, which cancelled every queued group and every retry round. On
    /// 2026-08-18 that policy fired twice in one night and <b>both times it was wrong</b>: once on a
    /// child blocked by a Revit startup dialog (a 2026-08-18 run — four of ten groups never launched), and once
    /// on a child that was simply mid-action and went on to report <c>36 ok, 0 failed</c> (Weekly
    /// SITE3A — its retry round cancelled). The evidence for "this instance is unwell" was never
    /// evidence about the other nine.</para>
    ///
    /// <para>What is deliberately NOT relaxed: the worker is still left open rather than closed, and
    /// it still <b>holds its concurrency slot</b>. It is alive and holding gigabytes; handing its
    /// slot to a queued group would oversubscribe the machine, which is the failure
    /// <see cref="InstanceBudget"/> exists to prevent. The run continues with one slot fewer.</para>
    ///
    /// <para>Its models simply have no complete rows, so <see cref="MissingModels"/> collects them
    /// for the next round with no extra bookkeeping — and
    /// <see cref="IsDoneBarExiting"/> is what lets the round reach its end at all, since this child
    /// will never exit on its own.</para>
    ///
    /// The reason comes from <see cref="ChildLiveness.Judgement"/> rather than being reconstructed
    /// here. The old version reported <c>now - LastProgressAt</c> for every verdict, including
    /// ceiling force-closes, which produced the uninterpretable "no progress for 0 sec" against instances
    /// that were demonstrably mid-<c>run</c> — and hid a bug that cost a whole test cycle.
    /// </summary>
    private void DeferForAttention(RunGroup group, ChildLiveness.Judgement judgement)
    {
        if (group.NeedsAttention) return;   // already set aside; say it once

        string label = judgement.Verdict switch
        {
            ChildLiveness.Verdict.OverCeiling => "over ceiling",
            ChildLiveness.Verdict.NeverStarted => "never started",
            _ => "wedged"
        };

        string reason = $"pid {group.Pid} needs attention ({label}): {judgement.Reason}";
        RunTrace.Mark("supervisor setting group aside; worker left open — " + reason);
        RunManifestStore.Update(_manifest, _ =>
        {
            group.NeedsAttention = true;
            group.AttentionReason = reason;
        });
        Note("ERROR", reason + ". Revit was left open for inspection and this group's models will " +
                      "be retried in the next round; the rest of the run carries on.");

        // The one case that IS about the machine rather than about one instance: nothing healthy is
        // left to carry on with. Stop, and say which children are holding it.
        List<RunGroup> live = CurrentGroups().Where(IsRunning).ToList();
        if (live.Count > 0 && live.All(g => g.NeedsAttention))
            StopRun("every running instance needs attention — the last was " + reason);
    }

    private void StopRun(string reason)
    {
        if (_manifest.State == RunState.Stopped) return;
        RunManifestStore.Update(_manifest, manifest =>
        {
            manifest.State = RunState.Stopped;
            manifest.StopReason = reason;
        });
    }

    /// <summary>
    /// True when nothing more will come out of this round: every group has either run and exited,
    /// or failed to launch at all.
    ///
    /// A PENDING group counts as unfinished. That is the whole difference from the old check, which
    /// read <c>Pid == 0</c> as "never started, ignore it" — correct when every group launched
    /// immediately, and a silent way to abandon a queue now that they do not.
    ///
    /// A group whose child has asked to close counts as FINISHED even before the process goes: its
    /// models are logged, and Revit's own shutdown can take longer than the round it belongs to.
    ///
    /// So does a group DEFERRED by <see cref="DeferForAttention"/> (1.4.2). That child is wedged, or
    /// stuck behind a dialog, or past its ceiling — whichever it is, it is never going to exit, and
    /// waiting for it would leave the round open forever and the retry that reruns its models never
    /// built. Unlike the case above its models are NOT logged, which is exactly why the round has to
    /// end: <see cref="MissingModels"/> is what picks them up.
    /// </summary>
    private bool RoundFinished() => CurrentGroups().All(g => GroupSettled(g, IsDoneBarExiting(g)));

    /// <summary>
    /// Whether one group can no longer hold its round open. Pure and separate from the manifest for
    /// the same reason <see cref="SlotsToFill"/> is: getting this wrong either abandons a queue or
    /// hangs a run forever, and neither is something to discover on a live overnight batch.
    /// </summary>
    /// <param name="doneBarExiting">The caller's <see cref="IsDoneBarExiting"/> answer, which needs
    /// per-child watch state this cannot see.</param>
    internal static bool GroupSettled(RunGroup g, bool doneBarExiting) =>
        g.LaunchFailed ||
        (g.Pid != 0 && (g.ExitedAt != null || g.NeedsAttention || doneBarExiting));
}
