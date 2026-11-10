using System.IO;
using KhBatchController.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;
using KhBatchRunner.Schedule;

namespace KhBatchController;

/// <summary>
/// Stage C: run a batch from the standalone controller.
///
/// The controller composes the job and starts the worker; it never opens a model itself. The job is
/// written as an ephemeral <see cref="ChunkJobStore"/> entry and a Revit is launched carrying
/// <c>KHBATCH_JOB</c> — the same mechanism <see cref="ChunkLauncher"/> already uses for auto-split
/// runs, so execution flows through the proven headless <c>ScheduledRunner</c> path with nothing
/// new in it. What is new is only WHO starts it.
///
/// Progress is read from the tracking CSV the worker writes, which is also how a person would check
/// on a run: no side channel, no second source of truth.
/// </summary>
internal static class BatchRun
{
    /// <param name="groupSize">Models per worker Revit; 0 runs everything in one. Exposed on the
    /// console flow so the auto-split path — the one with the supervisor and its retry rounds —
    /// can be exercised without the window.</param>
    public static int Run(BrokerTokenProvider broker, string revitExe, string? setName,
                          string? actionName, int groupSize = 0)
    {
        // ---- pick the model set -------------------------------------------------------------
        List<ModelPreset> sets = PresetStore.LoadAll();
        if (sets.Count == 0)
        {
            Console.Error.WriteLine("No model sets found in %AppData%\\KhBatchRunner\\Presets.");
            Console.Error.WriteLine("Create one first (run without --run to open the Forma browser).");
            return 6;
        }

        ModelPreset? set = setName is null
            ? null
            : sets.FirstOrDefault(s => string.Equals(s.Name, setName, StringComparison.OrdinalIgnoreCase));

        if (set is null)
        {
            Console.WriteLine("Model sets available:");
            foreach (ModelPreset s in sets)
                Console.WriteLine($"  {s.Name}  ({s.Models.Count} model(s))");
            Console.WriteLine();
            Console.Error.WriteLine(setName is null
                ? "Pick one with --set \"<name>\"."
                : $"No model set named '{setName}'.");
            return 6;
        }

        // ---- pick the action ----------------------------------------------------------------
        // The registry lives in the add-in and returns Revit-typed objects, so the broker is asked
        // rather than a list being duplicated here that could drift from what Revit actually offers.
        List<string> actions = broker.GetActionNames();
        string? action = actionName is null
            ? null
            : actions.FirstOrDefault(a => string.Equals(a, actionName, StringComparison.OrdinalIgnoreCase));

        if (action is null)
        {
            Console.WriteLine("Actions available (from the running Revit's registry):");
            foreach (string a in actions) Console.WriteLine("  " + a);
            Console.WriteLine();
            Console.Error.WriteLine(actionName is null
                ? "Pick one with --action \"<name>\"."
                : $"No action named '{actionName}'.");
            return 6;
        }

        return Execute(broker, revitExe, new[] { set },
                       new[] { new RunItem(action, RunItemKind.Action) },
                       ScheduleStore.DefaultDestinationFolder(), Console.WriteLine,
                       groupSize: groupSize) ? 0 : 8;
    }

    /// <summary>
    /// Composes one job over the union of <paramref name="sets"/>, runs every action in
    /// <paramref name="actions"/> in order, launches a single worker Revit and follows it to
    /// completion. Shared by the console flow and the Batch Run window — the only difference
    /// between them is where <paramref name="report"/> writes.
    /// </summary>
    /// <param name="report">
    /// Progress sink, called from the CALLING thread. A GUI caller running this on a worker thread
    /// is responsible for marshalling back to the UI thread.
    /// </param>
    public static bool Execute(BrokerTokenProvider broker, string revitExe,
                               IReadOnlyList<ModelPreset> sets, IReadOnlyList<RunItem> runItems,
                               string destination, Action<string> report,
                               SyncMode sync = SyncMode.Sync,
                               BatchCompletion completion = BatchCompletion.CloseRevit,
                               int groupSize = 0,
                               string? projectLogName = null)
    {
        // Everything the window shows also goes to the log. A run's output used to live only in a
        // text box, so closing the window destroyed the only record of what happened — and the
        // destination is user-editable, so without logging it there is no way to even find the CSV
        // afterwards.
        Action<string> caller = report;
        report = line => { caller(line); ControllerLog.Write("  run| " + line); };
        ControllerLog.Write($"run starting — destination '{destination}'");
        // ---- resolve the models -------------------------------------------------------------
        // A folder-backed set is re-scanned from Forma at run time; that scan needs a token, which is
        // why PresetResolver now takes an ITokenProvider — the controller's is the broker.
        // De-duplicated by Key so a model in two selected sets is still opened once.
        var models = new List<CloudModel>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelPreset set in sets)
        {
            ModelPreset resolved = PresetResolver.Resolve(set, out string? note, broker);
            if (!string.IsNullOrWhiteSpace(note)) report("  note: " + note);

            foreach (CloudModel m in resolved.Models)
                if (seenKeys.Add(DedupKey(m))) models.Add(m);
        }

        if (models.Count == 0)
        {
            report("ERROR: the selected model set(s) resolved to no models.");
            return false;
        }

        string setLabel = string.Join(" + ", sets.Select(s => s.Name));
        string itemLabel = string.Join(", ", runItems.Select(r => r.Name));
        report($"Sets    : {setLabel}  ({models.Count} model(s))");
        report($"Run     : {itemLabel}");
        report($"Options : {sync}, {completion}" +
               (groupSize > 0 ? $", split every {groupSize}"
                              : groupSize == ScheduledJob.GroupSizeAuto ? ", split automatically" : ""));
        report("");

        // ---- compose the job ----------------------------------------------------------------
        var job = new ScheduledJob
        {
            Name = $"Controller — {itemLabel}",
            ModelSetNames = new List<string>(),          // models are carried directly, so the
            AdHocModels = new List<CloudModel>(models),  // worker never re-scans anything
            RunItems = runItems.ToList(),
            DestinationFolder = destination,
            ProjectLogName = string.IsNullOrWhiteSpace(projectLogName)
                ? (sets.Count == 1 ? sets[0].Name : "Controller")
                : projectLogName!,
            Completion = completion,
            Sync = sync,
            Recurrence = ScheduleRecurrence.Once
        };

        DateTime stamp = DateTime.Now;
        job.LogRunStamp = stamp;
        // Resolved through the launcher's own helper rather than recomputed, so the file watched
        // here is by construction the file the worker writes.
        string trackingLog = ChunkLauncher.TrackingLogPathFor(job, stamp);

        // ---- launch the worker(s) -------------------------------------------------------------
        // groupSize 0 → one Revit for everything. GroupSizeAuto → the add-in's auto-split, sized
        // from THIS model count and this PC (see InstanceBudget.AutoGroupSize) now that the sets
        // have been resolved. A positive size is honoured as given, for the --split CLI flag.
        // Either way it is several Revits in parallel, all writing into the one tracking log they
        // share via the run stamp.
        int effectiveGroup = groupSize > 0
            ? groupSize
            : groupSize == ScheduledJob.GroupSizeAuto
                ? InstanceBudget.AutoGroupSize(models.Count)
                : models.Count;

        // SUPERVISED, like the add-in. LaunchChunks alone starts the workers and walks away: when a
        // worker died — and Revit does die, taking an access violation inside its own loader on a
        // cold cloud open — its models were simply gone, and the run ended "39 of 70 unreported"
        // with nothing retried. LaunchSupervised records the intended assignment in a manifest, and
        // RunSupervisor reruns whatever produced no row, in up to 3 rounds, re-splitting at the
        // group size the user chose. Same machinery the add-in has had since 1.2.1.
        RunManifest? manifest = ChunkLauncher.LaunchSupervised(
            // The release, from the exe we are about to launch — the supervisor needs it to find
            // the per-version CollaborationCache its liveness check reads.
            job, models, effectiveGroup, RevitLocator.VersionOf(revitExe), out string? launchError, revitExe);

        if (manifest == null)
        {
            report("ERROR: could not launch the worker — " + (launchError ?? "unknown error"));
            return false;
        }
        if (!string.IsNullOrWhiteSpace(launchError)) report("WARNING: " + launchError);

        // Only some of the groups start immediately: the rest are queued behind the instance cap
        // and the supervisor starts them as slots free. Reporting a queued group as "pid 0" would
        // read as a failed launch, which is the opposite of what it means.
        List<RunGroup> launchGroups = manifest.CurrentRound!.Groups;
        foreach (RunGroup g in launchGroups)
        {
            if (g.Pid == 0)
            {
                report($"Queued  : {g.Models.Count} model(s) — starts when a worker slot frees");
                continue;
            }
            ControllerLog.Write($"launched worker pid {g.Pid} for job {g.ChunkId}");
            report($"Worker  : Revit pid {g.Pid} — {g.Models.Count} model(s)");
        }
        report($"Limit   : {InstanceBudget.Describe()}");
        report($"Log     : {manifest.TrackingLogPath}");
        report("");

        // The supervisor runs on its own thread; this signals when it has finished reconciling.
        using var finished = new ManualResetEventSlim(false);
        RunSupervisor.Start(manifest, _ => finished.Set());

        // Each worker needs the security prompt answered too. The broker got a handler from the
        // start; the workers did not — so after every rebuild each worker Revit sat on "The
        // publisher of this add-in could not be verified" with nobody to click it, and the run
        // showed 0 results forever. Nothing about the batch was wrong; it never began.
        // Kept per-pid and topped up as retry rounds start new instances.
        var promptHandlers = new Dictionary<int, AddInSecurityDialogHandler>();
        try
        {
            // One row per model PER RUN ITEM — the tracking log records a pass per task.
            return Watch(manifest, finished, promptHandlers,
                         models.Count * Math.Max(1, runItems.Count), report);
        }
        finally
        {
            foreach (AddInSecurityDialogHandler h in promptHandlers.Values) h.Dispose();
        }
    }

    /// <summary>Attaches a prompt handler to any instance of the current round that does not have
    /// one yet — a retry round starts fresh Revits, and they prompt just like the first did.</summary>
    private static void TrackPrompts(RunManifest manifest,
                                     Dictionary<int, AddInSecurityDialogHandler> handlers,
                                     Action<string> report)
    {
        foreach (RunGroup g in manifest.CurrentRound?.Groups ?? new List<RunGroup>())
        {
            if (g.Pid == 0 || handlers.ContainsKey(g.Pid)) continue;
            var h = new AddInSecurityDialogHandler(g.Pid, line => report("  " + line));
            handlers[g.Pid] = h;
            h.Start();
        }
    }

    /// <summary>Identity of a model for de-duplication across selected sets: its file path when
    /// local, its cloud GUID otherwise — the same distinction <see cref="CloudModel.IsLocal"/>
    /// draws everywhere else.</summary>
    private static string DedupKey(CloudModel m) =>
        m.IsLocal ? "local:" + m.LocalPath : "cloud:" + m.ModelGuid;

    /// <summary>
    /// Follows the run by tailing the tracking CSV until every model has a row or the worker exits.
    /// Watching the worker process as well as the file is what distinguishes "still working" from
    /// "died with models left to do" — the distinction <see cref="RunSupervisor"/> exists for.
    /// </summary>
    private static bool Watch(RunManifest manifest, ManualResetEventSlim finished,
                              Dictionary<int, AddInSecurityDialogHandler> promptHandlers,
                              int expected, Action<string> report)
    {
        string trackingLog = manifest.TrackingLogPath;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var started = DateTime.UtcNow;
        var deadline = started + TimeSpan.FromHours(2);
        DateTime lastProgress = started;
        int announcedQuietMinutes = 0;
        var roundsReported = new HashSet<int>();

        report($"Waiting for {expected} result(s). A cloud model can take minutes to download " +
               "on a cold cache — the run is not stuck just because nothing has appeared yet.");

        while (DateTime.UtcNow < deadline)
        {
            TrackPrompts(manifest, promptHandlers, report);

            // Announce each round with the shape of its split, so a recovery is visible as it
            // happens rather than only showing up as a gap in the results.
            foreach (RunRound r in manifest.Rounds.ToList())
            {
                if (!roundsReported.Add(r.Round)) continue;
                var sizes = r.Groups.Select(g => g.Models.Count).ToList();
                if (r.Round > 1)
                    report($"  — retry round {r.Round} of {RunSupervisor.MaxRounds}: " +
                           $"{sizes.Sum()} model(s) in {sizes.Count} instance(s)  [{string.Join(" / ", sizes)}]");
            }

            int before = seen.Count;
            foreach (string row in NewRows(trackingLog, seen))
                report($"  [{seen.Count}/{expected}] {row}");

            if (seen.Count > before)
            {
                lastProgress = DateTime.UtcNow;
                announcedQuietMinutes = 0;
            }
            else
            {
                // Say something on a long silence, so "still working" never looks like "hung".
                int quiet = (int)(DateTime.UtcNow - lastProgress).TotalMinutes;
                if (quiet >= 2 && quiet > announcedQuietMinutes)
                {
                    announcedQuietMinutes = quiet;
                    report($"  … still running — {seen.Count}/{expected} done, " +
                           $"{quiet} min since the last result.");
                }
            }

            if (seen.Count >= expected)
            {
                report("");
                report($"DONE — {seen.Count} of {expected} result(s) reported.");
                return true;
            }

            // Done when the SUPERVISOR says so, not when the first instances exit. A dead worker
            // used to end the run here; now it just means the supervisor is about to start another
            // round, so waiting on it is what makes the retry visible instead of pre-empted.
            if (finished.IsSet)
            {
                // Read once more: the final rows may have landed between the last poll and the exit.
                foreach (string row in NewRows(trackingLog, seen))
                    report("  " + row);

                report("");
                if (seen.Count >= expected)
                {
                    report($"DONE — {seen.Count} of {expected} result(s) reported.");
                    return true;
                }
                report($"ERROR: {expected - seen.Count} of {expected} result(s) still missing after " +
                       $"{RunSupervisor.MaxRounds} attempts. See {trackingLog}");
                return false;
            }

            Thread.Sleep(2000);
        }

        report("ERROR: gave up waiting after 2 hours.");
        return false;
    }

    /// <summary>Tracking-CSV rows not yet printed, keyed by the model column so a re-read of the
    /// file never double-reports. Opened share-all: the worker is writing to it right now.</summary>
    private static IEnumerable<string> NewRows(string path, HashSet<string> seen)
    {
        if (!File.Exists(path)) yield break;

        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch { yield break; } // mid-write; try again next poll

        foreach (string raw in lines.Skip(1)) // skip header
        {
            string line = raw.Trim('\r', ' ');
            if (line.Length == 0) continue;

            // Columns: Datetime (Finish) | Model | Task | Status | Message
            string[] cells = SplitCsvLine(line);
            if (cells.Length < 4) continue;

            string model = cells[1];
            string task = cells[2];
            if (model.Length == 0) continue;

            // Keyed by model AND task: a run with two actions writes a row per model per task, and
            // keying on the model alone would report the first and silently swallow the rest.
            if (!seen.Add(model + "\0" + task)) continue;

            string status = cells[3];
            string message = cells.Length > 4 ? cells[4] : "";
            yield return $"{status,-7} {model}" + (task.Length > 0 ? $"  [{task}]" : "")
                       + (message.Length > 0 ? $"  — {message}" : "");
        }
    }

    /// <summary>
    /// Splits one CSV record, honouring the doubled-quote escaping <see cref="CsvFormat.Escape"/>
    /// writes. Needed because the Message column routinely contains commas — a naive Split(',')
    /// would shift every field after it and mislabel the status.
    /// </summary>
    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c != '"') { current.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }  // "" → "
                else inQuotes = false;
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
