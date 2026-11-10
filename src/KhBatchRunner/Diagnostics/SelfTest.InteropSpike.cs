using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KhBatchRunner.Forma;
using KhBatchRunner.Actions;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Diagnostics;

public static partial class SelfTest
{
    /// <summary>
    /// AIT Model Checker spike (<c>KHBATCH_INTEROP_SPIKE=&lt;checkset .xml path or http URL&gt;</c>):
    /// answers the Batch Interop Phase-A questions against the real AIT install. Part A runs a
    /// checkset with a model open-and-ACTIVATED (the modal-runner shape); Part B opens a second
    /// model in the BACKGROUND (no window) and runs against that Document object directly WITHOUT
    /// activating — DocCheckRunner takes the Document explicitly, so if B passes the action can
    /// use the cheaper background path (NeedsActiveDocument=false). Both parts log IsModified
    /// before/after (read-only proof) and verify the HTML + Excel exports appear.
    /// Mirrors RunDynSpike: the body runs from an ExternalEvent, not inside Idling.
    /// </summary>
    private static ExternalEvent? _interopSpikeEvent;
    private static IExternalEventHandler? _interopSpikeHandler;

    private static void RunInteropSpike(UIApplication? uiApp, string checkset)
    {
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* fresh log best-effort */ }
        Append($"=== Interop (AIT Model Checker) spike {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Append("checkset: " + checkset);
        if (uiApp is null) { Append("No UIApplication. Aborting."); return; }

        _deferEnd = true;
        _interopSpikeHandler = new RelayHandler(app =>
        {
            try { InteropSpikeBody(app, checkset); }
            catch (Exception ex) { Append("spike body threw: " + ex); }
            finally
            {
                try { _interopSpikeEvent?.Dispose(); } catch { /* best effort */ }
                _interopSpikeEvent = null;
                _interopSpikeHandler = null;
                // A supervised probe ends from the supervisor's callback, once its children are done.
                if (!_interopSupervising) End();
            }
        });
        try { _interopSpikeEvent = ExternalEvent.Create(_interopSpikeHandler); _interopSpikeEvent.Raise(); }
        catch (Exception ex) { Append("ExternalEvent.Create/Raise threw: " + ex); End(); }
    }

    private static void InteropSpikeBody(UIApplication uiApp, string checkset)
    {
        string modelA = Path.Combine(TestRoot, "models", "TestA.rvt");
        string modelB = Path.Combine(TestRoot, "models", "TestB.rvt");

        // "engine:<checkset>" — skip the raw-invoker parts and drive the REAL production path
        // instead: BatchInteropAction + BatchEngine + BatchRunnerWindow.RunModal over both test
        // models (verifies ConfigureOutput naming, CSV columns, PerModelNote, close-as-we-go).
        if (checkset.StartsWith("engine:", StringComparison.OrdinalIgnoreCase))
        {
            InteropEngineProbe(uiApp, checkset.Substring("engine:".Length), modelA, modelB);
            return;
        }
        // "supervised:<model set name>|<checkset>" — exactly what the Batch Interop button does since
        // 2026-11-03: the whole set in ONE supervised child Revit, crash → plain retry → skip.
        if (checkset.StartsWith("supervised:", StringComparison.OrdinalIgnoreCase))
        {
            InteropSupervisedProbe(uiApp, checkset.Substring("supervised:".Length));
            return;
        }
        // "schedule:<checkset>" — drive the Schedule Interop data path: a ScheduledJob with
        // InteropChecksetPath through the real ScheduledRunner.BuildJobPasses branch +
        // TryRunInteractive, exactly what ScheduleInteropCommand's "Run now" does.
        if (checkset.StartsWith("schedule:", StringComparison.OrdinalIgnoreCase))
        {
            InteropScheduleProbe(uiApp, checkset.Substring("schedule:".Length), modelA, modelB);
            return;
        }
        // "profile:<checkset>" — where an Interop run's time goes, on one bench model: the whole
        // checkset three times (cold, then warm) with the invoker's phase timings, then each check on
        // its own. Model = KHBATCH_INTEROP_MODEL, default the interopbench Arch model.
        if (checkset.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
        {
            InteropProfile(uiApp, checkset.Substring("profile:".Length));
            return;
        }
        string reportDir = Path.Combine(
            TestRoot, "interopspike", "reports",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        using var suppressor = new DialogSuppressor(uiApp);
        Interop.ModelCheckerInvoker.Trace = Append; // incremental step log — locates a failure inside AIT

        // G0 — which AIT assemblies the process already has (AIT's add-in loads at startup).
        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = asm.GetName().Name ?? "";
            if (name.StartsWith("AIT.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Autodesk.ValidationTool", StringComparison.OrdinalIgnoreCase))
                Append("G0 loaded: " + name);
        }

        // Part A — active document (the modal-runner shape).
        try
        {
            Autodesk.Revit.DB.Document docA = uiApp.OpenAndActivateDocument(modelA).Document;
            Append($"A: opened+activated {docA.Title}");
            InteropRunOn(uiApp, docA, checkset, reportDir, "A");
        }
        catch (Exception ex) { Append("A: open/run threw: " + ex); }

        // Part B — background document, nothing activated since A. DocCheckRunner gets docB.
        try
        {
            Autodesk.Revit.DB.Document docB = uiApp.Application.OpenDocumentFile(modelB);
            Append($"B: opened background {docB.Title}; active=" +
                   (uiApp.ActiveUIDocument?.Document?.Title ?? "(none)"));
            InteropRunOn(uiApp, docB, checkset, reportDir, "B");
            try { docB.Close(false); } catch { /* background doc — best effort */ }
        }
        catch (Exception ex) { Append("B: open/run threw: " + ex); }

        Append("open docs AFTER: " + OpenDocTitles(uiApp.Application));
        Append("=== Interop spike complete ===");
    }

    /// <summary>Drives the real Batch Interop production path headless (everything the button
    /// does except the dialogs): action + engine + modal runner over the two local test models,
    /// then logs the tracking/detail CSVs so the columns and roll-up note can be inspected.</summary>
    private static void InteropEngineProbe(UIApplication uiApp, string checkset, string modelA, string modelB)
    {
        string dest = Path.Combine(TestRoot, "interopspike", "enginetest");
        Directory.CreateDirectory(dest);
        Append("engine probe: checkset=" + checkset);

        var preset = new ModelPreset { Name = "InteropSpike" };
        // KHBATCH_INTEROP_MODELS (';'-separated paths) swaps the two tiny test models for a real
        // bench set — what a runtime measurement needs.
        string[] models = (Environment.GetEnvironmentVariable("KHBATCH_INTEROP_MODELS") ?? "")
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (models.Length == 0) models = new[] { modelA, modelB };
        foreach (string m in models)
            preset.Models.Add(new CloudModel { DisplayName = Path.GetFileName(m), LocalPath = m });

        var action = new BatchInteropAction(checkset);
        var engine = new BatchEngine(uiApp, action, dest, "InteropEngineProbe", SyncMode.NoSync);
        var results = UI.BatchRunnerWindow.RunModal(
            uiApp, new[] { new UI.BatchRunnerWindow.Pass(engine, preset) },
            "Run: Batch Interop (engine probe)", BatchCompletion.CloseDocuments, showSummary: false);

        foreach (var r in results)
        {
            Append($"pass: action={r.ActionName} preset={r.PresetName} result={r.Result}");
            foreach (string? csv in new[] { r.TrackingLogPath, r.DetailLogPath })
            {
                if (string.IsNullOrEmpty(csv) || !File.Exists(csv)) { Append("csv MISSING: " + csv); continue; }
                Append("csv " + csv + ":");
                foreach (string line in File.ReadAllLines(csv))
                    Append("    " + line);
            }
        }
        Append("open docs AFTER: " + OpenDocTitles(uiApp.Application));
        Append("=== Interop spike complete ==="); // same end marker the launcher script watches for
    }

    /// <summary>True while a supervised probe waits for its children — the spike's own finally must
    /// not close this Revit, because this Revit IS the supervisor.</summary>
    private static volatile bool _interopSupervising;

    /// <summary>
    /// The Batch Interop button's path, headless: a saved model set resolved, launched as ONE
    /// supervised child Revit (<see cref="Schedule.InteractiveSplit.SupervisedGroupSize"/>), and this
    /// Revit kept alive as the supervisor until every model has a row — so a crash in the child is
    /// observed, the model retried once plain, and skipped if it crashes again. The loop test
    /// (<c>tools/interopbench/Loop-TestInterop.ps1</c>) runs this from a cold cache.
    /// </summary>
    private static void InteropSupervisedProbe(UIApplication uiApp, string spec)
    {
        int bar = spec.IndexOf('|');
        if (bar <= 0) { Append("supervised probe: expected '<model set>|<checkset>', got: " + spec); return; }
        string setName = spec.Substring(0, bar).Trim();
        string checkset = spec.Substring(bar + 1).Trim();
        string dest = Path.Combine(TestRoot, "interopspike", "supervised");
        Directory.CreateDirectory(dest);
        Append($"supervised probe: set='{setName}' checkset={checkset}");

        ModelPreset? preset = PresetStore.LoadAll()
            .FirstOrDefault(p => string.Equals(p.Name, setName, StringComparison.OrdinalIgnoreCase));
        if (preset == null) { Append("supervised probe: no model set named " + setName); return; }
        ModelPreset chosen = PresetResolver.Resolve(preset, out _);
        Append($"supervised probe: {chosen.Models.Count} model(s)");
        foreach (CloudModel m in chosen.Models)
            Append($"  fact before: {m.DisplayName} → {Describe(Batch.CloudOpenFacts.Get(m))}");

        var action = new BatchInteropAction(checkset);
        var baseJob = new Schedule.ScheduledJob
        {
            Name = "InteropSupervisedProbe",
            RunItems = new List<Schedule.RunItem> { new(action.Name, Schedule.RunItemKind.Action) },
            InteropChecksetPath = checkset,
            Completion = BatchCompletion.CloseDocuments,
            Sync = SyncMode.NoSync,
            DestinationFolder = dest,
            ProjectLogName = "InteropSupervisedProbe",
        };
        Schedule.RunWeight weight = Schedule.InstanceBudget.WeightFor(baseJob);
        int groupSize = Schedule.InteractiveSplit.SupervisedGroupSize(
            chosen.Models.Count, Schedule.InstanceBudget.AutoGroupSize(chosen.Models, weight), alwaysSupervise: true);
        baseJob.GroupSize = groupSize;

        // The children inherit this process's environment: without this they would arm the same probe
        // and each try to supervise a run of their own.
        foreach (string v in new[] { "KHBATCH_SELFTEST_V9", "KHBATCH_SELFTEST_V8", "KHBATCH_INTEROP_SPIKE" })
            Environment.SetEnvironmentVariable(v, null);

        Schedule.RunManifest? manifest = Schedule.ChunkLauncher.LaunchSupervised(
            baseJob, chosen.Models, groupSize, uiApp.Application.VersionNumber, out string? err);
        if (manifest == null) { Append("supervised probe: launch failed: " + err); return; }
        Append($"supervised probe: launched {manifest.CurrentRound!.Groups.Count} child Revit(s), group size {groupSize}" +
               (err != null ? " — " + err : ""));

        _interopSupervising = true;
        Schedule.RunSupervisor.Start(manifest, m =>
        {
            try
            {
                Append($"supervised probe: run {m.State}; rounds={m.RoundNumber}; unrecoverable={m.Unrecoverable.Count}");
                foreach (KeyValuePair<string, int> c in m.OpenCrashes) Append($"  open crashes: {c.Key} × {c.Value}");
                foreach (CloudModel cm in chosen.Models)
                    Append($"  fact after: {cm.DisplayName} → {Describe(Batch.CloudOpenFacts.Get(cm))}");
                if (File.Exists(m.TrackingLogPath))
                {
                    Append("tracking csv " + m.TrackingLogPath + ":");
                    foreach (string line in File.ReadAllLines(m.TrackingLogPath)) Append("    " + line);
                }
            }
            catch (Exception ex) { Append("supervised probe: report failed: " + ex.Message); }
            Append("=== Interop spike complete ===");
            // WM_CLOSE only — safe from the supervisor's thread, unlike unhooking Idling.
            Schedule.RevitShutdown.RequestCurrent("diagnostic probe complete");
        }, showRoundNotifications: false);

        static string Describe(Batch.CloudOpenFacts.Fact? f) => f == null
            ? "(none)"
            : $"workshared={(f.Workshared?.ToString() ?? "?")} source={f.Source} crashRetry={f.CrashRetry}";
    }

    /// <summary>Drives the Schedule Interop data path headless: builds a <see cref="Schedule.ScheduledJob"/>
    /// exactly as <see cref="UI.ScheduleInteropWindow"/>.Commit would (ad-hoc local models,
    /// InteropChecksetPath, ActionNames=[BatchInteropAction.DisplayName]) and runs it through the
    /// real <see cref="Schedule.ScheduledRunner"/>.TryRunInteractive — the same call
    /// ScheduleInteropCommand's "Run now" makes. Proves the new BuildJobPasses branch end to end
    /// without needing an actual Windows Task Scheduler task.</summary>
    private static void InteropScheduleProbe(UIApplication uiApp, string checkset, string modelA, string modelB)
    {
        string dest = Path.Combine(TestRoot, "interopspike", "scheduletest");
        Directory.CreateDirectory(dest);
        Append("schedule probe: checkset=" + checkset);

        var job = new Schedule.ScheduledJob
        {
            Name = "InteropScheduleProbe",
            AdHocModels = new List<CloudModel>
            {
                new() { DisplayName = "TestA.rvt", LocalPath = modelA },
                new() { DisplayName = "TestB.rvt", LocalPath = modelB },
            },
            RunItems = new List<Schedule.RunItem>
                { new(Actions.BatchInteropAction.DisplayName, Schedule.RunItemKind.Action) },
            InteropChecksetPath = checkset,
            // Explicitly the default (skip proven link-only worksets), so this probe exercises the
            // branch a real scheduled job takes rather than only the opt-out one.
            InteropChecksetExaminesLinks = false,
            Sync = SyncMode.NoSync,
            Completion = BatchCompletion.CloseDocuments,
            DestinationFolder = dest,
            ProjectLogName = "InteropScheduleProbe",
        };

        bool ok = Schedule.ScheduledRunner.TryRunInteractive(uiApp, job, "Schedule Interop probe", out string? error);
        Append($"TryRunInteractive returned {ok}; error={error ?? "(none)"}; job.LastRunAt={job.LastRunAt}");

        foreach (string f in Directory.GetFiles(Path.Combine(dest, "Log"), "*.csv"))
        {
            Append("tracking csv " + f + ":");
            foreach (string line in File.ReadAllLines(f)) Append("    " + line);
        }
        foreach (string f in Directory.GetFiles(dest, "*.csv"))
        {
            Append("detail csv " + f + ":");
            foreach (string line in File.ReadAllLines(f)) Append("    " + line);
        }
        Append("open docs AFTER: " + OpenDocTitles(uiApp.Application));
        Append("=== Interop spike complete ===");
    }

    private static void InteropRunOn(
        UIApplication uiApp, Autodesk.Revit.DB.Document doc, string checkset, string reportDir, string label)
    {
        Append($"{label}: IsModelInCloud={doc.IsModelInCloud}, IsWorkshared={doc.IsWorkshared}");
        Append($"{label}: IsModified BEFORE run: {doc.IsModified}");
        string safeTitle = string.Join("_", doc.Title.Split(Path.GetInvalidFileNameChars()));
        string htmlDir = Path.Combine(reportDir, label + "_" + safeTitle);
        string xlsxPath = Path.Combine(reportDir, label + "_" + safeTitle + ".xlsx");
        Directory.CreateDirectory(htmlDir);

        var sw = Stopwatch.StartNew();
        Interop.ModelCheckerRunResult? result = Step($"{label}: RunCheckset",
            () => Interop.ModelCheckerInvoker.RunCheckset(uiApp, doc, checkset, htmlDir, xlsxPath));
        sw.Stop();
        if (result == null) return; // Step already logged the exception

        Append($"{label}: IsModified AFTER run: {doc.IsModified}");
        Append($"{label}: result={result.OverallResult} passed={result.Passed}/{result.TotalChecks} " +
               $"failed={result.Failed} errors={result.Errors} notRun={result.NotRun} " +
               $"({result.PassPercent:0.#}%) in {sw.Elapsed.TotalSeconds:0.#}s");
        Append($"{label}: html files={Directory.GetFiles(htmlDir, "*.html").Length}, " +
               $"xlsx exists={File.Exists(xlsxPath)}");
    }

    /// <summary>Profiles one checkset on one model: three whole runs (the first pays the cold AIT
    /// load), then a run per check, ranked slowest first. The model opens detached with every
    /// workset open — what a first Batch Interop run of a model sees — and closes unsaved.</summary>
    private static void InteropProfile(UIApplication uiApp, string checkset)
    {
        string benchDir = Path.Combine(TestRoot, "interopbench");
        string model = Environment.GetEnvironmentVariable("KHBATCH_INTEROP_MODEL") is { Length: > 0 } m
            ? m : Path.Combine(benchDir, "Bench-Arch-01.rvt");
        string outDir = Path.Combine(benchDir, "profile", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir);
        Interop.ModelCheckerInvoker.Trace = Append; // phase ms lines land in this log
        Append("profile: checkset=" + checkset);
        Append("profile: model=" + model);

        using var suppressor = new DialogSuppressor(uiApp);
        Autodesk.Revit.DB.Document? doc = null;
        try
        {
            Autodesk.Revit.DB.ModelPath path = Autodesk.Revit.DB.ModelPathUtils.ConvertUserVisiblePathToModelPath(model);
            var options = new Autodesk.Revit.DB.OpenOptions();
            if (Autodesk.Revit.DB.BasicFileInfo.Extract(model).IsWorkshared)
            {
                options.DetachFromCentralOption = Autodesk.Revit.DB.DetachFromCentralOption.DetachAndPreserveWorksets;
                options.SetOpenWorksetsConfiguration(new Autodesk.Revit.DB.WorksetConfiguration(
                    Autodesk.Revit.DB.WorksetConfigurationOption.OpenAllWorksets));
            }
            var open = Stopwatch.StartNew();
            doc = uiApp.Application.OpenDocumentFile(path, options);
            Append($"profile: open took {open.ElapsedMilliseconds} ms ({doc.Title})");
            // A detached open may already count as modified; read it before any check runs so an
            // "IsModified after" of True can be pinned on the open or on AIT.
            Append("profile: IsModified right after open: " + doc.IsModified);

            for (int i = 1; i <= 3; i++)
            {
                // Run 1 = AIT's own Excel exporter (the baseline); runs 2-3 = ours. Same model, same
                // checkset, so the two report.xlsx files can be diffed cell for cell.
                Interop.ModelCheckerInvoker.ForceAitExcel = i == 1;
                string runDir = Path.Combine(outDir, "full" + i);
                Directory.CreateDirectory(runDir);
                var sw = Stopwatch.StartNew();
                Interop.ModelCheckerRunResult? r = Step($"full run {i}", () => Interop.ModelCheckerInvoker.RunCheckset(
                    uiApp, doc, checkset, runDir, Path.Combine(runDir, "report.xlsx")));
                if (r != null)
                    Append($"profile: FULL {i} total {sw.ElapsedMilliseconds} ms — {r.OverallResult} " +
                           $"total={r.TotalChecks} pass={r.Passed} fail={r.Failed} err={r.Errors} notRun={r.NotRun} report={r.ReportOnly}");
            }

            Interop.ModelCheckerInvoker.ForceAitExcel = false;
            Append("profile: IsModified after full runs: " + doc.IsModified);

            // Per check: no export (that cost is measured above), so the number is the check alone.
            Interop.ModelCheckerInvoker.Trace = null;
            var rows = new List<(long ms, Interop.SplitCheck c, string outcome)>();
            foreach (Interop.SplitCheck c in Interop.ChecksetSplitter.SplitPerCheck(checkset, Path.Combine(outDir, "checks")))
            {
                var sw = Stopwatch.StartNew();
                string outcome;
                try
                {
                    Interop.ModelCheckerRunResult r = Interop.ModelCheckerInvoker.RunCheckset(uiApp, doc, c.Path);
                    outcome = $"total={r.TotalChecks} pass={r.Passed} fail={r.Failed} err={r.Errors} notRun={r.NotRun} report={r.ReportOnly}";
                }
                catch (Exception ex) { outcome = "THREW " + ex.GetType().Name + ": " + ex.Message; }
                rows.Add((sw.ElapsedMilliseconds, c, outcome));
            }

            Append($"profile: per-check sum {rows.Sum(x => x.ms)} ms over {rows.Count} checks; slowest first:");
            foreach (var (ms, c, outcome) in rows.OrderByDescending(x => x.ms))
                Append($"  {ms,8} ms | {c.CheckType,-24} | {(c.IsChecked ? "on " : "off")} | {c.Heading} | {c.Name} | {outcome}");
            File.WriteAllLines(Path.Combine(outDir, "per-check.csv"),
                new[] { "ms,checkType,isChecked,heading,name,outcome" }.Concat(rows.Select(x =>
                    $"{x.ms},{Csv(x.c.CheckType)},{x.c.IsChecked},{Csv(x.c.Heading)},{Csv(x.c.Name)},{Csv(x.outcome)}")));
            Append("profile: IsModified after all runs: " + doc.IsModified);
        }
        catch (Exception ex) { Append("profile threw: " + ex); }
        finally
        {
            Interop.ModelCheckerInvoker.Trace = null;
            Interop.ModelCheckerInvoker.ForceAitExcel = false;
            try { doc?.Close(false); } catch { /* best effort */ }
            Append("=== Interop spike complete ===");
        }

        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
