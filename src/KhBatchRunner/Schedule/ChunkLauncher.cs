using System.Diagnostics;
using KhBatchRunner.Batch;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;

// Disambiguated from System.Diagnostics, which this file also uses (Process).
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Auto-split runner: takes a base job's settings plus one already-resolved model list, splits
/// the models into groups of <c>groupSize</c>, and launches ONE fresh Revit instance per group,
/// queued under the run's concurrency cap, each running its group headless and then requesting a
/// normal close. This automates the pattern a
/// user otherwise builds by hand (several same-time scheduled jobs, one per handful of models) —
/// smaller groups in fresh processes avoid the mid-batch failures a single long 20-model session
/// hits, and separate processes are the only way to run models in parallel (the Revit API is
/// single-threaded).
///
/// Each group is written as an EPHEMERAL <see cref="ScheduledJob"/> to <see cref="ChunkJobStore"/>
/// (not the user's schedule store) and launched via the existing <c>KHBATCH_JOB</c> env-var
/// mechanism, so it flows through the proven headless
/// <see cref="ScheduledRunner.OnHeadlessIdling"/> path unchanged. All sibling groups share ONE
/// <see cref="ScheduledJob.LogRunStamp"/> so their tracking rows unify into a single log file
/// (the loggers serialize the concurrent appends — see <see cref="Logging.LogFileLock"/>).
/// </summary>
public static class ChunkLauncher
{
    /// <summary>The separation between each group's first cloud open — the measured mitigation for
    /// concurrent cold opens, and the reason cloud runs no longer have to be serial. Lives on
    /// <see cref="InstanceBudget"/> with the concurrency cap, and with the same kind of env
    /// override, because the two are tuned together.</summary>
    private static int FirstOpenStaggerSeconds => InstanceBudget.FirstOpenStaggerSeconds;


    /// <summary>
    /// Splits <paramref name="models"/> into groups of <paramref name="groupSize"/> and starts a
    /// Revit instance for each — but no more than <see cref="InstanceBudget.MaxConcurrent"/> at a
    /// time.
    ///
    /// Returns one <see cref="RunGroup"/> per group, carrying the models it was given and, once
    /// started, its process id — the assignment <see cref="RunSupervisor"/> needs to tell "this
    /// group finished" from "this group died with models still to do". Groups beyond the cap come
    /// back <b>pending</b> (see <see cref="RunGroup"/>) and the supervisor starts them as slots
    /// free. Empty (with <paramref name="error"/> set) when nothing could be started at all.
    ///
    /// The caller should have already confirmed any publish action and resolved the models to
    /// concrete cloud/local entries.
    /// </summary>
    /// <param name="runStamp">
    /// The stamp every group's tracking rows are filed under. A retry round MUST pass the original
    /// run's stamp so its rows land in the same unified CSV as the first attempt; omit it (null)
    /// only for a fresh run.
    /// </param>
    /// <param name="revitExePath">
    /// Which Revit to launch. Defaults to the CURRENT process's executable, which is correct when
    /// the launcher is itself Revit (every in-add-in caller). The standalone controller is not
    /// Revit, so it passes the release it resolved explicitly.
    /// </param>
    public static List<RunGroup> LaunchChunks(ScheduledJob baseJob, IReadOnlyList<CloudModel> models,
        int groupSize, out string? error, DateTime? runStamp = null, string? revitExePath = null,
        int? maxConcurrentInstances = null)
    {
        error = null;
        var built = new List<RunGroup>();

        string revitExe = ResolveRevitExe(revitExePath);
        if (string.IsNullOrWhiteSpace(revitExe) || !System.IO.File.Exists(revitExe))
        {
            error = "Could not locate the running Revit executable to launch the group instances.";
            return built;
        }

        List<List<CloudModel>> groups = ChunkSplitter.Split(models, groupSize);
        if (groups.Count == 0)
        {
            error = "No models to run.";
            return built;
        }

        // Best-effort cleanup of any chunk files an interrupted run left behind, so the
        // folder doesn't grow without bound. Safe alongside the pending groups saved below: it only
        // deletes files older than 12 hours.
        ChunkJobStore.SweepOrphans();

        // One shared stamp for every group → one unified tracking log across all instances (and
        // across every retry round, when the caller passes the original run's stamp).
        DateTime sharedStamp = runStamp ?? DateTime.Now;

        // Resolve the very same tracking log the children will write to (they derive it from the
        // copied destination/project name and the shared stamp — see ScheduledRunner.BuildJobPasses),
        // so the launch notes below sit in the same file as the model rows they account for.
        string logPath = TrackingLogPathFor(baseJob, sharedStamp);

        RunWeight weight = InstanceBudget.WeightFor(baseJob);
        int cap = Math.Max(1, maxConcurrentInstances ?? InstanceBudget.MaxConcurrent(models, weight));
        RunNote.Write(logPath, baseJob.Name, "", "INFO",
            $"{groups.Count} group(s) of up to {(groupSize > 0 ? groupSize : models.Count)} model(s); " +
            InstanceBudget.Describe(models, weight) +
            (groups.Count > cap ? $" — the first {cap} start now, the rest queue for a free slot." : "."));

        // Build and PERSIST every chunk job up front, so the manifest records the full intent even
        // though only the first wave is started. A queued group already has its job file on disk;
        // all that is missing is the process.
        var launchErrors = new List<string>();
        for (int i = 0; i < groups.Count; i++)
        {
            ScheduledJob chunk = BuildChunkJob(baseJob, groups[i], sharedStamp, i + 1);
            // Stagger only applies WITHIN a wave. A queued group is already separated from the
            // others by however long it waited for its slot, so making it wait again would be
            // pure delay.
            chunk.FirstOpenDelaySeconds = i < cap ? i * FirstOpenStaggerSeconds : 0;

            var group = new RunGroup
            {
                ChunkId = chunk.Id,
                Models = new List<CloudModel>(groups[i]),
                FirstOpenDelaySeconds = chunk.FirstOpenDelaySeconds
            };
            try
            {
                ChunkJobStore.Save(chunk);
            }
            catch (Exception ex)
            {
                group.LaunchFailed = true;
                launchErrors.Add($"group {i + 1}: {ex.Message}");
                RunNote.Write(logPath, baseJob.Name, "", "ERROR",
                    $"group {i + 1} of {groups.Count} could not be prepared: {ex.Message} — " +
                    $"{groups[i].Count} model(s) did not run: {ModelNames(groups[i])}");
            }
            built.Add(group);
        }

        // Start only as many as the machine can carry. RunSupervisor starts the rest as slots free.
        int started = 0;
        for (int i = 0; i < built.Count && started < cap; i++)
        {
            if (built[i].LaunchFailed) continue;
            if (Start(built[i], baseJob.Name, revitExe, logPath, i + 1, built.Count, out string? err))
                started++;
            else
                launchErrors.Add($"group {i + 1}: {err}");
        }

        if (started == 0)
        {
            error = "Failed to launch any group instances:\n" + string.Join("\n", launchErrors);
            return new List<RunGroup>();
        }
        if (launchErrors.Count > 0)
            error = $"Started {started} of {built.Count} groups; some failed:\n" +
                    string.Join("\n", launchErrors);
        return built;
    }

    /// <summary>
    /// Starts one prepared group's Revit and records it. Shared by the first wave and by every
    /// later launch the supervisor makes when a slot frees, so a queued group produces exactly the
    /// same trail line and log note as one that started immediately — only later.
    ///
    /// Reconciliation, half one: nothing waits on these instances, so without a record of what was
    /// launched a group whose Revit dies just shrinks the batch invisibly. Each child answers with
    /// a "group &lt;id&gt; finished" SUMMARY row; a launch line with no answering summary is a group
    /// that died.
    /// </summary>
    private static bool Start(RunGroup group, string jobName, string revitExe, string logPath,
        int number, int total, out string? error)
    {
        error = null;
        try
        {
            int pid = LaunchRevit(revitExe, group.ChunkId);
            group.Pid = pid;
            group.StartedAt = DateTime.Now;
            RunTrace.Mark($"launch group {number}/{total} chunk={group.ChunkId} pid={pid} " +
                          $"models={ModelNames(group.Models)}");
            RunNote.Write(logPath, jobName, "", "INFO",
                $"group {number} of {total} ({group.ChunkId}, pid {pid}) launched — " +
                $"{group.Models.Count} model(s): {ModelNames(group.Models)}");
            return true;
        }
        catch (Exception ex)
        {
            group.LaunchFailed = true;
            error = ex.Message;
            ChunkJobStore.Delete(group.ChunkId); // don't leave a job file no Revit will consume
            RunNote.Write(logPath, jobName, "", "ERROR",
                $"group {number} of {total} failed to launch: {ex.Message} — " +
                $"{group.Models.Count} model(s) did not run: {ModelNames(group.Models)}");
            return false;
        }
    }

    /// <summary>
    /// Starts a group that has been waiting for a free slot. Called by
    /// <see cref="RunSupervisor"/> from its poll loop as running instances exit; everything it
    /// needs — which Revit, which log, what the run is called — comes off the manifest, because a
    /// retry round's supervisor is not necessarily the process that built the round.
    /// </summary>
    public static bool StartPending(RunManifest manifest, RunGroup group, int number, int total,
        out string? error) =>
        Start(group, manifest.Name, ResolveRevitExe(manifest.RevitExePath),
              manifest.TrackingLogPath, number, total, out error);

    /// <summary>
    /// Launches the first round AND records the assignment, returning the manifest a
    /// <see cref="RunSupervisor"/> needs. This is the entry point every caller should use: a split
    /// run without a manifest is one whose losses are invisible.
    /// Null (with <paramref name="error"/> set) when nothing could be launched.
    /// </summary>
    /// <param name="revitVersion">e.g. "2026" — locates the per-release CollaborationCache the
    /// liveness check reads to tell a slow download from a wedged one.</param>
    /// <param name="revitExePath">See <see cref="LaunchChunks"/> — null means "this process's Revit".</param>
    public static RunManifest? LaunchSupervised(ScheduledJob baseJob, IReadOnlyList<CloudModel> models,
        int groupSize, string revitVersion, out string? error, string? revitExePath = null)
    {
        // The stamp is chosen HERE, not inside the launch, because every retry round has to reuse it
        // to keep all rows in one CSV — and the manifest is what remembers it.
        DateTime runStamp = DateTime.Now;

        // Weight comes from the job, so an Interop run is budgeted as Revit + Model Checker and the
        // manifest records the smaller cap every later retry round then reuses.
        int cap = InstanceBudget.MaxConcurrent(models, InstanceBudget.WeightFor(baseJob));
        List<RunGroup> groups = LaunchChunks(baseJob, models, groupSize, out error, runStamp,
            revitExePath, cap);
        if (groups.Count == 0) return null;

        var manifest = new RunManifest
        {
            RunStamp = runStamp,
            TrackingLogPath = TrackingLogPathFor(baseJob, runStamp),
            Name = baseJob.Name,
            RevitVersion = revitVersion,
            SupervisorPid = Process.GetCurrentProcess().Id,
            BaseJob = baseJob,
            // The size the caller actually split at — the only place it is reliably known, since
            // not every caller writes it back onto the job. See RunManifest.GroupSize.
            GroupSize = groupSize,
            MaxConcurrentInstances = cap,
            // Likewise the executable: a retry round must launch the same Revit round 1 did, and
            // cannot fall back to "this process" when the supervisor is not itself Revit.
            RevitExePath = ResolveRevitExe(revitExePath),
            AllModels = new List<CloudModel>(models),
            State = RunState.Running
        };
        manifest.Rounds.Add(new RunRound { Round = 1, StartedAt = DateTime.Now, Groups = groups });
        manifest.CountAttempts(models);

        RunManifestStore.SweepOld();
        RunManifestStore.Save(manifest);
        return manifest;
    }

    /// <summary>
    /// The unified tracking CSV a job's rows are filed under for a given run stamp — the same path
    /// every child derives independently in <see cref="ScheduledRunner.BuildJobPasses"/>, resolved
    /// here so the launcher's notes and the supervisor's reconciliation read the one file.
    /// </summary>
    public static string TrackingLogPathFor(ScheduledJob job, DateTime runStamp)
    {
        string destFolder = string.IsNullOrWhiteSpace(job.DestinationFolder)
            ? ScheduleStore.DefaultDestinationFolder() : job.DestinationFolder;
        string projectLog = string.IsNullOrWhiteSpace(job.ProjectLogName)
            ? job.Name : job.ProjectLogName;
        return TrackingCsvLogger.PathFor(destFolder, projectLog, job.Name, runStamp);
    }

    /// <summary>A group's models as one readable cell, trimmed so a 40-model group can't produce an
    /// unreadable log row. The full membership is always recoverable from the model rows themselves.</summary>
    private static string ModelNames(List<CloudModel> group)
    {
        string all = string.Join("; ", group.Select(m => m.DisplayName));
        return all.Length <= 300 ? all : all.Substring(0, 300) + "…";
    }

    /// <summary>
    /// The "what did I just start" paragraph every split-run confirmation shows.
    ///
    /// Shared so the four dialogs that can start a split run cannot drift apart on the numbers, and
    /// so the instance cap is stated in all of them: now that groups queue, "10 groups" no longer
    /// means "10 Revits at once", and a confirmation that implied it would be actively wrong about
    /// how much of the machine the run is about to take.
    /// </summary>
    public static string DescribeLaunch(RunManifest manifest, int groupSize)
    {
        List<RunGroup> groups = manifest.CurrentRound?.Groups ?? new List<RunGroup>();
        int running = groups.Count(g => g.Pid != 0 && g.ExitedAt == null);
        int queued = groups.Count - running;

        string wave = queued > 0
            ? $"{running} started now; the other {queued} start as those finish."
            : $"All {running} started now.";

        return $"Running {manifest.AllModels.Count} model(s) as {groups.Count} group(s) of up to " +
               $"{groupSize}.\n\n{wave}\nThis run allows {InstanceBudget.Describe(manifest.AllModels)}.\n\n" +
               "Each runs in the background and closes when done. This Revit watches them and " +
               $"reruns any model that did not finish, up to {RunSupervisor.MaxRounds} attempts.";
    }

    /// <summary>Number of groups <paramref name="modelCount"/> models split into at
    /// <paramref name="groupSize"/> — for a caller's confirmation message.</summary>
    public static int GroupCount(int modelCount, int groupSize)
    {
        if (groupSize <= 0 || modelCount <= 0) return modelCount > 0 ? 1 : 0;
        return (modelCount + groupSize - 1) / groupSize;
    }

    /// <summary>
    /// Builds one ephemeral chunk job from the base: same actions/settings, but its models are
    /// exactly this group (as ad-hoc, so the child is self-contained and never re-scans folder
    /// sets), no further splitting (<see cref="ScheduledJob.GroupSize"/> = 0), always closes when
    /// done, and shares the run stamp. Any REST-only "Get File Log" step is dropped so it isn't
    /// pulled once per group.
    /// </summary>
    private static ScheduledJob BuildChunkJob(ScheduledJob baseJob, List<CloudModel> group,
        DateTime sharedStamp, int groupNumber)
    {
        ScheduledJob chunk = baseJob.Duplicate(); // fresh Id, copies actions/specs/dest/sync
        chunk.Name = baseJob.Name;                // drop Duplicate's " (copy)" so logs unify
        chunk.ChunkIndex = groupNumber;           // "group 3", the name the child calls itself
        chunk.ModelSetNames = new List<string>();
        chunk.ModelSetIds = new List<string>();
        chunk.AdHocModels = new List<CloudModel>(group);
        chunk.RunItems = baseJob.RunItems
            .Where(r => !RestOnlySteps.IsRestOnlyItem(r))
            .Select(r => new RunItem(r.Name, r.Kind, r.RefId))
            .ToList();
        chunk.GroupSize = 0;                       // a chunk never re-splits
        chunk.Completion = BatchCompletion.CloseDocuments; // fresh process quits at the end
        chunk.LogRunStamp = sharedStamp;
        chunk.Recurrence = ScheduleRecurrence.Once;
        // A chunk is launched directly by this process, never by a Windows task, so the parent's
        // paused state has no meaning here — and a run that got this far was already allowed to
        // start. Forced for the same reason GroupSize and LastRunAt are.
        chunk.Enabled = true;
        chunk.LastRunAt = null;
        return chunk;
    }

    /// <summary>
    /// Prepares a REPLACEMENT group for models whose Revit died, as a pending group the supervisor
    /// appends to the current round. Returns null with <paramref name="error"/> set if the chunk
    /// file could not be written.
    ///
    /// <para>Prepared, not started: the caller adds it to the round and
    /// <see cref="RunSupervisor.SlotsToFill"/> starts it on the next poll, through the same
    /// <see cref="StartPending"/> path as any queued group. That keeps the slot arithmetic in one
    /// place — a replacement must not be a second way to start a Revit that the cap cannot see.</para>
    ///
    /// <para>Why it needs a fresh chunk at all: a child DELETES its own chunk file as it consumes it
    /// (<see cref="ChunkJobStore"/>), so the dead group's <see cref="RunGroup.ChunkId"/> no longer
    /// names anything on disk. Reusing it would produce a Revit that starts, finds no job, and sits
    /// there — the exact shape of the fault this release is fixing elsewhere.</para>
    ///
    /// <para>No first-open stagger: a replacement is one instance starting on its own, not a wave, so
    /// the delay would be pure waiting. The machine-wide <see cref="FirstOpenGate"/> still separates
    /// its first cloud open from any sibling's.</para>
    /// </summary>
    public static RunGroup? PrepareReplacement(ScheduledJob baseJob, IReadOnlyList<CloudModel> models,
        DateTime runStamp, int chunkNumber, out string? error)
    {
        error = null;
        try
        {
            ScheduledJob chunk = BuildChunkJob(baseJob, models.ToList(), runStamp, chunkNumber);
            chunk.FirstOpenDelaySeconds = 0;
            ChunkJobStore.Save(chunk);
            return new RunGroup
            {
                ChunkId = chunk.Id,
                Models = new List<CloudModel>(models),
                FirstOpenDelaySeconds = 0
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Starts a fresh Revit process carrying <c>KHBATCH_JOB</c> so the add-in's startup
    /// hook runs the chunk headless. Does not wait — all groups launch and run simultaneously.
    /// Returns the new process id, which is how the supervisor later watches (and if necessary
    /// terminates) this exact instance.</summary>
    private static int LaunchRevit(string revitExe, string chunkJobId)
    {
        var psi = new ProcessStartInfo
        {
            FileName = revitExe,
            UseShellExecute = false, // required so EnvironmentVariables is honoured
            WorkingDirectory = System.IO.Path.GetDirectoryName(revitExe) ?? Environment.CurrentDirectory
        };
        psi.EnvironmentVariables[ScheduleEnvironment.Job] = chunkJobId;
        psi.EnvironmentVariables[ScheduleEnvironment.Owner] = Product.ScheduleOwner;
        Process? started = Process.Start(psi);
        if (started == null)
            throw new InvalidOperationException("Process.Start returned no process for " + revitExe);
        return started.Id;
    }

    /// <summary>
    /// Which executable to launch a group with: the caller's choice, or this process's own when it
    /// has none. The fallback is only correct when the caller IS Revit — the standalone controller
    /// must always pass the release it resolved, and <see cref="RunManifest.RevitExePath"/> is how
    /// a retry round remembers it.
    /// </summary>
    public static string ResolveRevitExe(string? revitExePath) =>
        string.IsNullOrWhiteSpace(revitExePath) ? CurrentRevitExe() : revitExePath!;

    private static string CurrentRevitExe()
    {
        try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
        catch { return ""; }
    }
}
