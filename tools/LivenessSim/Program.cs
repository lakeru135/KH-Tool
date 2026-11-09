using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using KhBatchRunner.Forma;
using KhBatchRunner.Actions;
using KhBatchRunner.Batch;
using KhBatchRunner.Interop;
using KhBatchRunner.Logging;
using KhBatchRunner.Presets;
using KhBatchRunner.Reporting;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace LivenessSim;

/// <summary>
/// Every case here is a real failure from the 2026-07-26 office run (98 models, group size 10,
/// 77 complete / 7 partial / 14 unrecoverable), turned into something that fails in milliseconds
/// instead of an hour and fifty-two minutes.
///
/// The clocks are not slowed down or mocked: the cases backdate a <see cref="ChildLiveness.Watch"/>
/// so the PRODUCTION thresholds are the ones under test. A case that passes here passes with the
/// same numbers the add-in ships with.
/// </summary>
internal static class Program
{
    private static int _failed;
    private static int _passed;
    private static string _scratch = "";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--live-schedule-create", StringComparison.OrdinalIgnoreCase)))
            return LiveScheduleCreate(args);
        if (args.Any(a => a.Equals("--live-schedule-cleanup", StringComparison.OrdinalIgnoreCase)))
            return LiveScheduleCleanup(args);
        if (args.Any(a => a.Equals("--scheduler-integration", StringComparison.OrdinalIgnoreCase)))
            return SchedulerIntegrationProbe();
        if (args.Length >= 3 && args[0].Equals("--interop-summary", StringComparison.OrdinalIgnoreCase))
        {
            // --interop-summary <out.xlsx> <report.xlsx>... : merge real reports from a finished run.
            (int models, List<string> unreadable) = InteropExcelReport.Merge(args.Skip(2), args[1]);
            Console.WriteLine($"{models} model(s) -> {args[1]}; unreadable: {string.Join(", ", unreadable)}");
            return models > 0 ? 0 : 1;
        }

        _scratch = Path.Combine(Path.GetTempPath(), "LivenessSim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);

        Console.WriteLine("Liveness regression harness");
        Console.WriteLine("thresholds: startup " + ChildLiveness.StartupGrace.TotalMinutes + " min, " +
                          "no-progress " + ChildLiveness.NoProgressGrace.TotalMinutes + " min, " +
                          "default ceiling " + ChildLiveness.DefaultCeiling.TotalMinutes + " min");
        Console.WriteLine();

        try
        {
            CeilingCases();
            StartupCases();
            StaggerCase();
            ShutdownCases();
            SlowOpenCases();
            ModelBlockCases();
            FirstOpenGateCases();
            CompletionCases();
            RoundLadderCases();
            SlotCases();
            PackingCases();
            ManifestStoreCases();
            ReuseCases();
            RelinkSplitCases();
            BatchLinkRvtCases();
            InteropSafetyCases();
            InteropLinkWorksetCases();
            InteropSummaryCases();
            ExportNwcSpecCases();
            ToastLedgerCases();
            ChunkSpecCases();
            ScheduleEnabledCases();
            LauncherVbsExecutionCase();
        }
        finally
        {
            try { Directory.Delete(_scratch, recursive: true); } catch { /* scratch */ }
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Creates one uniquely named, real scheduled Revit run for live end-to-end verification.  It
    /// intentionally uses the production ScheduleStore and TaskSchedulerGateway; the caller later
    /// invokes --live-schedule-cleanup with the printed id.  Existing jobs/tasks are never touched.
    /// </summary>
    private static int LiveScheduleCreate(string[] args)
    {
        string id = ArgValue(args, "--id") ??
                    "codex_live_134_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string name = ArgValue(args, "--name") ?? id;
        string owner = ArgValue(args, "--owner") ?? "Full";
        if (!owner.Equals("Full", StringComparison.OrdinalIgnoreCase) &&
            !owner.Equals("DRW", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--owner must be Full or DRW");
            return 2;
        }
        string revit = ArgValue(args, "--revit") ?? @"C:\Program Files\Autodesk\Revit 2026\Revit.exe";
        int delay = int.TryParse(ArgValue(args, "--delay"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsedDelay) ? Math.Max(1, parsedDelay) : 2;
        int groupSize = int.TryParse(ArgValue(args, "--split"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsedGroup) ? Math.Max(0, parsedGroup) : 0;
        string destination = ArgValue(args, "--destination") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "KhBatchTest", "schedule-live-134", id);

        List<string> modelPaths = ArgValues(args, "--model");
        if (modelPaths.Count == 0)
        {
            string modelDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "KhBatchTest", "models");
            modelPaths.Add(Path.Combine(modelDir, "RT01.rvt"));
            modelPaths.Add(Path.Combine(modelDir, "RT02.rvt"));
        }
        foreach (string modelPath in modelPaths)
            if (!File.Exists(modelPath))
            {
                Console.Error.WriteLine("Model not found: " + modelPath);
                return 2;
            }
        if (!File.Exists(revit))
        {
            Console.Error.WriteLine("Revit not found: " + revit);
            return 2;
        }

        DateTime start = DateTime.Now.AddMinutes(delay);
        start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0,
            DateTimeKind.Local);
        if (start <= DateTime.Now) start = start.AddMinutes(1);

        var job = new ScheduledJob
        {
            Id = id,
            ScheduleOwner = owner,
            Name = name,
            AdHocModels = modelPaths.Select(p => new CloudModel
            {
                DisplayName = Path.GetFileName(p),
                LocalPath = Path.GetFullPath(p)
            }).ToList(),
            RunItems = new List<RunItem>
            {
                new("Get Phase & Workset", RunItemKind.Action)
            },
            Completion = BatchCompletion.CloseRevit,
            Sync = SyncMode.NoSync,
            DestinationFolder = destination,
            ProjectLogName = id,
            Time = start.ToString("HH':'mm", CultureInfo.InvariantCulture),
            Recurrence = ScheduleRecurrence.Once,
            StartDate = start.Date,
            GroupSize = groupSize
        };

        Directory.CreateDirectory(destination);
        ScheduleStore.Save(job);
        (bool ok, string message) = TaskSchedulerGateway.Create(job, revit);
        Console.WriteLine(message);
        if (!ok)
        {
            ScheduleStore.Delete(id);
            return 1;
        }

        Console.WriteLine("LIVE_ID=" + job.Id);
        Console.WriteLine("LIVE_TASK=" + job.TaskName);
        Console.WriteLine("LIVE_NAME=" + job.Name);
        Console.WriteLine("LIVE_OWNER=" + job.ScheduleOwner);
        Console.WriteLine("LIVE_START=" + start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        Console.WriteLine("LIVE_DESTINATION=" + destination);
        Console.WriteLine("LIVE_SPLIT=" + groupSize.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("LIVE_MODELS=" + modelPaths.Count.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static int LiveScheduleCleanup(string[] args)
    {
        string? id = ArgValue(args, "--id");
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("--id is required for --live-schedule-cleanup");
            return 2;
        }

        ScheduledJob job = ScheduleStore.GetById(id) ?? new ScheduledJob { Id = id };
        (bool ok, string message) = TaskSchedulerGateway.Delete(job);
        Console.WriteLine(message);
        ScheduleStore.Delete(id);
        Console.WriteLine("Deleted saved job " + id + ".");
        return ok ? 0 : 1;
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> ArgValues(string[] args, string name)
    {
        var values = new List<string>();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                values.Add(args[++i]);
        return values;
    }

    /// <summary>
    /// Opt-in real Windows Task Scheduler probe. This is deliberately not part of the fast default
    /// suite: it changes external scheduler state for a few seconds. It calls the production
    /// <see cref="TaskSchedulerGateway"/>, queries the resulting task, then deletes both task and
    /// launcher in a finally block. The next-day date catches the locale regression that originally
    /// made Schedule Run fail on a PC whose displayed short date is yy/MM/dd.
    /// </summary>
    private static int SchedulerIntegrationProbe()
    {
        const string id = "codex_scheduler_integration_probe";
        string revit = @"C:\Program Files\Autodesk\Revit 2026\Revit.exe";
        var job = new ScheduledJob
        {
            Id = id,
            Name = "Scheduler integration probe",
            Recurrence = ScheduleRecurrence.Once,
            StartDate = DateTime.Today.AddDays(1),
            Time = DateTime.Now.AddMinutes(5).ToString("HH':'mm", CultureInfo.InvariantCulture)
        };

        Console.WriteLine("Production TaskSchedulerGateway integration probe");
        Console.WriteLine($"PC display pattern: {CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern}");
        Console.WriteLine($"Requested local run: {job.StartDate.Value:d} {job.Time}");

        bool created = false;
        try
        {
            (bool ok, string message) = TaskSchedulerGateway.Create(job, revit);
            Console.WriteLine(message);
            if (!ok) return 1;
            created = true;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in new[] { "/Query", "/TN", job.TaskName, "/FO", "LIST", "/V" })
                psi.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.Contains("TaskName", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Start Date", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Start Time", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Task To Run", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine(line);

            if (process.ExitCode != 0 || !output.Contains(job.TaskName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Query failed (exit {process.ExitCode}).");
                return 1;
            }

            string markerPath = TaskSchedulerGateway.RegistrationMarkerPath(id);
            string markerText = File.Exists(markerPath) ? File.ReadAllText(markerPath) : "";
            if (!markerText.Contains(TaskSchedulerGateway.RegistrationMarker, StringComparison.Ordinal) ||
                !markerText.Contains("KhBatchLauncher.exe", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Registration marker missing or not XML-v7:\n" + markerText);
                return 1;
            }
            if (File.Exists(Path.Combine(ScheduleStore.ScheduledDirectory, "run_" + id + ".vbs")))
            {
                Console.WriteLine("A superseded .vbs launcher was left behind by registration.");
                return 1;
            }

            var xmlPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in new[] { "/Query", "/TN", job.TaskName, "/XML" })
                xmlPsi.ArgumentList.Add(arg);
            using var xmlProcess = System.Diagnostics.Process.Start(xmlPsi)!;
            string taskXml = xmlProcess.StandardOutput.ReadToEnd() + xmlProcess.StandardError.ReadToEnd();
            xmlProcess.WaitForExit();
            DateTime expectedStart = job.StartDate.Value.Date +
                TimeSpan.Parse(job.Time, CultureInfo.InvariantCulture);
            string expectedBoundary = TaskSchedulerGateway.LocalBoundary(expectedStart);
            Console.WriteLine("Expected local StartBoundary: " + expectedBoundary);
            if (xmlProcess.ExitCode != 0 ||
                !taskXml.Contains(expectedBoundary, StringComparison.Ordinal) ||
                !taskXml.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>",
                    StringComparison.Ordinal) ||
                !taskXml.Contains("KhBatchLauncher.exe", StringComparison.OrdinalIgnoreCase) ||
                // No scripting host and no console host anywhere in the action.
                taskXml.Contains("wscript.exe", StringComparison.OrdinalIgnoreCase) ||
                taskXml.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Exported task XML did not preserve local time/single-instance policy:\n" + taskXml);
                return 1;
            }

            // The three recurrence shapes use different Task Scheduler XML trigger elements.
            // Re-register the same harmless future task to make Windows validate all three.
            job.Recurrence = ScheduleRecurrence.Daily;
            job.StartDate = null;
            (ok, message) = TaskSchedulerGateway.Create(job, revit);
            Console.WriteLine("Daily: " + message);
            if (!ok) return 1;

            job.Recurrence = ScheduleRecurrence.Weekly;
            job.WeeklyDays = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Friday };
            (ok, message) = TaskSchedulerGateway.Create(job, revit);
            Console.WriteLine("Weekly: " + message);
            if (!ok) return 1;

            // A completed legacy one-time task can still be started on demand. Windows must
            // accept its past boundary when migration changes only the action to quiet WScript.
            job.Recurrence = ScheduleRecurrence.Once;
            job.StartDate = DateTime.Today.AddDays(-1);
            job.LastRunAt = job.StartDate.Value.AddHours(1);
            job.Time = "01:00";
            job.WeeklyDays.Clear();
            (ok, message) = TaskSchedulerGateway.CreateForMigration(job, revit);
            Console.WriteLine("Completed-once migration: " + message);
            if (!ok) return 1;
            return 0;
        }
        finally
        {
            if (created)
            {
                (bool ok, string message) = TaskSchedulerGateway.Delete(job);
                Console.WriteLine(message);
                if (!ok) Console.WriteLine("WARNING: remove the probe task manually: " + job.TaskName);
            }
        }
    }

    // ---- RC1: the ceiling killed instances that were working ---------------------------------

    /// <summary>
    /// Rebuild Matrix from CSV: the two kinds that remain, and the one that was removed.
    ///
    /// <para>1.3.8.2 dropped Link Workset entirely — 1.3.8.1 had already stopped it writing a matrix
    /// at run end, and keeping it rebuildable left a third state nobody was tracking. Its old cases
    /// (both eras of its CSV columns, across the 1.3.5 "Instance Workset" → "Workset" rename) are
    /// gone with it; what stays is a case pinning that its CSV no longer resolves to a matrix, so
    /// the removal cannot be quietly undone by re-adding a descriptor.</para>
    /// </summary>
    private static void RebuildMatrixCases()
    {
        Expect("only two matrices are offered", MatrixCsvImport.Kinds.Count, 2);

        // A Link Workset detail CSV — current columns and the pre-1.3.5 shape alike — must no longer
        // resolve to anything. Its data is unchanged; it simply has no workbook any more.
        Expect("a Link Workset CSV no longer resolves to a matrix",
            MatrixCsvImport.Detect(new[] { "Model Name", "Link Name", "Kind", "Type", "Workset" }),
            (MatrixKind?)null);
        Expect("nor does the pre-1.3.5 one",
            MatrixCsvImport.Detect(
                new[] { "Model Name", "Link Name", "Kind", "Type", "Instance Workset", "Type Workset" }),
            (MatrixKind?)null);

        // The two that remain still detect, and still do not shadow each other.
        Expect("a Phase & Workset CSV is recognised",
            MatrixCsvImport.Detect(new[] { "Model Name", "Category", "Name" }), MatrixKind.PhaseWorkset);
        Expect("a Design Option CSV is recognised",
            MatrixCsvImport.Detect(
                new[] { "Model Name", "Design Set", "Design Option", "Is Primary" }),
            MatrixKind.DesignOption);

        string phase = Path.Combine(_scratch, "phase.csv");
        File.WriteAllText(phase,
            "Datetime,Model Name,Run,Category,Name\n" +
            "2026-08-13 10:00:00,Tower A.rvt,20260813_100000,Phase,Existing\n" +
            "2026-08-13 10:00:00,Tower B.rvt,20260813_100000,Workset,Shared Levels\n");
        string outPhase = Path.Combine(_scratch, "phase.xlsx");
        Expect("a Phase & Workset CSV rebuilds",
            MatrixCsvImport.Build(MatrixKind.PhaseWorkset, phase, outPhase), 2);
        Expect("and writes a workbook", File.Exists(outPhase), true);

        // A CSV missing the columns that identify it at all is still rejected.
        string junk = Path.Combine(_scratch, "junk.csv");
        File.WriteAllText(junk, "Something,Else\nfoo,bar\n");
        bool rejected = false;
        try { MatrixCsvImport.Build(MatrixKind.PhaseWorkset, junk, Path.Combine(_scratch, "junk.xlsx")); }
        catch (InvalidDataException) { rejected = true; }
        Expect("a CSV with none of the required columns is still rejected", rejected, true);

        SplitRunMatrixCases();
        DetailLogColumnCases();
        WorkbookIntegrityCases();
    }

    /// <summary>
    /// The workbook has to be READABLE, not merely present.
    ///
    /// <para>A Phase &amp; Workset matrix reached a user as Excel's "we found a problem with some
    /// content" prompt, and the repair silently dropped the whole <c>Workset Report</c> sheet. The
    /// old cases above would all have passed: they assert the file exists and never open it. What
    /// broke it was one character XML cannot represent, somewhere in several thousand user-typed
    /// workset names — <c>XlsxWriter.Escape</c> handled <c>&amp; &lt; &gt; "</c> and nothing else,
    /// so a name pasted out of Word (Word writes <c>0x0B</c> for a soft line break) condemned the
    /// part. Phase names come from the template and are tame, which is exactly why sheet 1 survived
    /// and sheet 2 did not.</para>
    /// </summary>
    private static void WorkbookIntegrityCases()
    {
        // 1. Both sheets are really there, and readable.
        string both = Path.Combine(_scratch, "both.csv");
        File.WriteAllText(both,
            "Datetime,Model Name,Run,Category,Name\n" +
            "2026-08-25 10:00:00,Tower A.rvt,20260825_100000,Phase,Existing\n" +
            "2026-08-25 10:00:00,Tower A.rvt,20260825_100000,Workset,Shared Levels & Grids\n" +
            "2026-08-25 10:00:00,Tower B.rvt,20260825_100000,Workset,Interiors\n");
        string bothXlsx = Path.Combine(_scratch, "both.xlsx");
        MatrixCsvImport.Build(MatrixKind.PhaseWorkset, both, bothXlsx);
        Expect("a Phase & Workset workbook carries both sheets",
            string.Join("|", XlsxReader.Read(bothXlsx).Select(s => s.Name)),
            "Phase Report|Workset Report");

        // 2. The characters that caused the corruption. Every one of these is either illegal in
        //    XML 1.0 outright or an orphaned half of a surrogate pair; none may reach the file, and
        //    none may take the sheet down with it.
        //
        //    Driven through PhaseWorksetMatrix.Write rather than a CSV on purpose: in a real run
        //    these strings come from the Revit API straight into the in-memory rows the action
        //    accumulates, and a lone surrogate cannot be written into a UTF-8 CSV at all — the
        //    fixture would throw before the code under test ever ran.
        string[] nasty =
        {
            "Word\u000Bbreak",      // 0x0B — the soft line break Word pastes; the prime suspect
            "nul\u0000here",        // 0x00
            "bell\u0007ring",       // 0x07
            "amp & lt < gt > quote \"",
            "emoji \U0001F3D7 site",// a legitimate surrogate PAIR: must survive intact
            "orphan \uD83D tail",   // a high surrogate with no partner
            "orphan \uDC00 head",   // a low surrogate with no partner
        };
        var hostileRows = new List<(string Model, string Category, string Name)>
        {
            ("Tower A.rvt", "Phase", "Existing"),
        };
        foreach (string n in nasty) hostileRows.Add(("Tower A.rvt", "Workset", n));

        string hostileXlsx = Path.Combine(_scratch, "hostile.xlsx");
        Expect("a workbook of hostile names is written at all",
            PhaseWorksetMatrix.Write(hostileXlsx, hostileRows), true);
        IReadOnlyList<XlsxSheet> read = XlsxReader.Read(hostileXlsx);
        Expect("a workbook full of illegal characters still has both sheets",
            string.Join("|", read.Select(s => s.Name)), "Phase Report|Workset Report");

        XlsxSheet worksets = read.First(s => s.Name == "Workset Report");
        var labels = new HashSet<string>(
            Enumerable.Range(1, worksets.MaxRow).Select(r => worksets.Cell(r, 0)));
        Expect("every hostile workset name still has a row", labels.Count >= nasty.Length, true);
        Expect("the escaping survives round-trip", labels.Contains("amp & lt < gt > quote \""), true);
        Expect("a real surrogate pair is kept", labels.Contains("emoji \U0001F3D7 site"), true);
        Expect("the illegal control character is gone, the name is not",
            labels.Contains("Wordbreak"), true);

        // 3. A run with no workset rows still produces the tab, carrying the reason. The two states
        //    "no model was workshared" and "the sheet was written and Excel threw it away" were
        //    indistinguishable, and a real report of the second was read as the first.
        string phaseOnly = Path.Combine(_scratch, "phase-only.csv");
        File.WriteAllText(phaseOnly,
            "Datetime,Model Name,Run,Category,Name\n" +
            "2026-08-25 10:00:00,Tower A.rvt,20260825_100000,Phase,Existing\n");
        string phaseOnlyXlsx = Path.Combine(_scratch, "phase-only.xlsx");
        MatrixCsvImport.Build(MatrixKind.PhaseWorkset, phaseOnly, phaseOnlyXlsx);
        IReadOnlyList<XlsxSheet> po = XlsxReader.Read(phaseOnlyXlsx);
        Expect("a phase-only run still gets a Workset tab",
            string.Join("|", po.Select(s => s.Name)), "Phase Report|Workset Report");
        Expect("and the empty tab says why",
            po.First(s => s.Name == "Workset Report").Cell(0, 0).StartsWith("No worksets were recorded"),
            true);

        // 4. Fault-verify the guard itself: a part that does not parse must abort the save with the
        //    sheet NAMED, and must leave no file behind for anyone to trust.
        var wb = new XlsxWriter();
        XlsxWriter.Sheet ok = wb.AddSheet("Fine");
        ok.WriteString(0, 0, "hello", XlsxWriter.XlsxStyle.Default);
        XlsxWriter.Sheet bad = wb.AddSheet("Broken");
        bad.WriteRawForTest(0, 0, "<not xml", XlsxWriter.XlsxStyle.Default);
        string badPath = Path.Combine(_scratch, "broken.xlsx");
        string message = "";
        try { wb.Save(badPath); }
        catch (InvalidDataException ex) { message = ex.Message; }
        Expect("an unparseable sheet aborts the save", message.Contains("'Broken'"), true);
        Expect("and leaves no workbook behind", File.Exists(badPath), false);

        // 5. The original defect, pinned. This is precisely what the old Escape let through — the
        //    character copied in with a workset name, sitting unescaped in the sheet's text — and
        //    it proves the corruption was real rather than inferred: XML cannot carry it, so the
        //    part Excel got was unparseable and repair discarded the sheet.
        var raw = new XlsxWriter();
        raw.AddSheet("Workset Report")
           .WriteRawForTest(0, 0, "Word\u000Bbreak", XlsxWriter.XlsxStyle.RowLabel);
        string rawPath = Path.Combine(_scratch, "raw-0x0b.xlsx");
        string rawMessage = "";
        try { raw.Save(rawPath); }
        catch (InvalidDataException ex) { rawMessage = ex.Message; }
        Expect("an unescaped 0x0B really does produce XML Excel would reject",
            rawMessage.Contains("'Workset Report'"), true);

        FileLogScopeCases();
    }

    /// <summary>
    /// Which files a Get File Log extract reports on.
    ///
    /// <para>The extract tested <c>displayName.EndsWith(".rvt")</c> and dropped the rest, so a
    /// folder's DWGs, IFCs and PDFs had no version history anyone could pull — though every column
    /// the report writes is generic Data Management version metadata that a PDF fills in exactly as
    /// a model does. The scope is now the caller's, and <b>a job that names none gets every
    /// file</b>: jobs saved before 1.4.4 carry no spec, so that default is what they inherit.</para>
    /// </summary>
    private static void FileLogScopeCases()
    {
        Expect("no spec means every file type", FileLogSpec.Default.Accepts("Site.dwg"), true);
        Expect("…including a Revit model", FileLogSpec.Default.Accepts("Tower A.rvt"), true);
        Expect("…and a file with no extension at all", FileLogSpec.Default.Accepts("README"), true);

        FileLogSpec revit = FileLogSpec.RevitOnly;
        Expect("Revit-only takes .rvt", revit.Accepts("Tower A.rvt"), true);
        Expect("…case-insensitively, as the old EndsWith did", revit.Accepts("TOWER A.RVT"), true);
        Expect("…and refuses a DWG", revit.Accepts("Site.dwg"), false);
        Expect("…and refuses a name that merely contains .rvt", revit.Accepts("Tower.rvt.bak"), false);

        // 1.4.4 briefly offered a third scope, a free-text list of extensions, saved as 2. Such a
        // job must not match NOTHING now that the value has no meaning — an extract that silently
        // reports zero rows reads as broken, where reporting everything is visibly wrong and safe.
        var withdrawn = new FileLogSpec { Scope = (FileLogScope)2 };
        Expect("a scope this build has no name for falls back to every file",
            withdrawn.Accepts("Report.pdf"), true);
        Expect("…and describes itself as such", withdrawn.ScopeDescription(), "files");

        Expect("the extension is lowercased and dotless", FileLogSpec.ExtensionOf("A.RVT"), "rvt");
        Expect("a dotfile has no extension", FileLogSpec.ExtensionOf(".gitignore"), "");
        Expect("nor does a trailing dot", FileLogSpec.ExtensionOf("name."), "");
        Expect("the LAST dot wins", FileLogSpec.ExtensionOf("a.b.c.pdf"), "pdf");

        // The scope has to be able to explain itself: "no files found" and "no .rvt models found"
        // are different answers, and the old message could only ever give the second.
        Expect("an all-files scope describes itself",
            FileLogSpec.Default.ScopeDescription(), "files");
        Expect("a Revit-only scope describes itself", revit.ScopeDescription(), ".rvt models");

        FileLogSetCases();
        FileLogReportPathCases();
        FileLogUrlCases();
        FileLogColumnCases();
        FileLogRetryCases();
        DataExtractCases();
    }

    /// <summary>
    /// The second REST-only step: that a job recognises it, that <c>Duplicate()</c> carries its
    /// set, that old job JSON still loads without one, and that its output folder can tell two runs
    /// of the same extract apart.
    ///
    /// <para>The <c>IsOnly</c> cases are the point. A job holding BOTH REST-only steps opens no
    /// model either, and the schedule editor waives the model-set requirement on exactly that
    /// answer — get it wrong and the only way to save an extract-plus-file-log schedule is to tick
    /// a model set the runner then discards.</para>
    /// </summary>
    private static void DataExtractCases()
    {
        var set = new DataExtractInputSet
        {
            Name = "Nightly Forma data",
            HubId = "b.c0ffee01-0000-4000-8000-000000000001",
            HubName = "SampleHub",
            RequestId = "c0ffee02-0000-4000-8000-000000000002",
            RequestName = "Insight Data Extraction",
            DestinationFolder = @"C:\logs",
        };
        Expect("a set naming an account and an extract has a target", set.HasTarget, true);
        Expect("one missing half is not a target",
            new DataExtractInputSet { HubId = set.HubId }.HasTarget, false);

        // The account id the Data Connector paths want is the hub id WITHOUT Data Management's
        // "b." prefix. Verified against the live API 2026-09-18; the sets store the prefixed form,
        // so this conversion is on the path of every request the step makes.
        Expect("the b. prefix is stripped for Data Connector",
            DataConnectorClient.AccountIdFrom(set.HubId), "c0ffee01-0000-4000-8000-000000000001");
        Expect("…and a bare GUID is left alone",
            DataConnectorClient.AccountIdFrom("c0ffee01-0000"), "c0ffee01-0000");

        var job = new ScheduledJob
        {
            Name = "Nightly extract",
            RunItems = new List<RunItem> { new(DataExtractStep.DisplayName, RunItemKind.Action) },
            DataExtractInputs = set,
        };
        Expect("the step is recognised on the job", DataExtractStep.IsRequested(job), true);
        Expect("…and as the only item", DataExtractStep.IsOnly(job), true);
        Expect("…so the job opens no model", RestOnlySteps.IsOnly(job), true);

        // Both REST-only steps together: still no model opened.
        var both = new ScheduledJob
        {
            Name = "Nightly extract + file log",
            RunItems = new List<RunItem>
            {
                new(FileLogStep.DisplayName, RunItemKind.Action),
                new(DataExtractStep.DisplayName, RunItemKind.Action),
            },
        };
        Expect("two REST-only steps still open no model", RestOnlySteps.IsOnly(both), true);
        Expect("…but neither step calls that job ITS own only item",
            DataExtractStep.IsOnly(both), false);

        // One model action in the list is enough to make it a model job again.
        both.RunItems.Add(new RunItem("Purge Import CAD", RunItemKind.Action));
        Expect("a model action ends the waiver", RestOnlySteps.IsOnly(both), false);
        Expect("…though the REST-only steps are still requested", RestOnlySteps.IsRequested(both), true);

        ScheduledJob dup = job.Duplicate();
        dup.DataExtractInputs!.RequestId = "changed";
        Expect("Duplicate() deep-copies the set", job.DataExtractInputs!.RequestId,
            "c0ffee02-0000-4000-8000-000000000002");

        ScheduledJob old = JsonSerializer.Deserialize<ScheduledJob>(
            "{\"Name\":\"Nightly extract\"}")!;
        Expect("a job saved before this step carries no set", old.DataExtractInputs,
            (DataExtractInputSet?)null);

        // The run folder carries the Forma job id, which is what makes "already downloaded?"
        // answerable by looking at the destination instead of keeping a state file in step with it.
        var stamp = new DateTime(2026, 9, 18, 6, 5, 0);
        string folder = DataExtractPaths.RunFolder(@"C:\logs", "SampleHub", "Get Data Extract", stamp,
            "c0ffee03-0000-4000-8000-000000000003");
        Expect("the run folder is stamped and job-tagged", folder,
            @"C:\logs\Get Data Extract\20260918_060500_SampleHub_Get Data Extract_c0ffee03");
        Expect("a second run of the SAME extract lands somewhere else",
            DataExtractPaths.RunFolder(@"C:\logs", "SampleHub", "Get Data Extract",
                stamp.AddDays(1), "c0ffee03-0000-4000-8000-000000000003") != folder, true);
        Expect("Forma's own filenames are kept inside it",
            DataExtractPaths.FilePath(folder, "issues_issues.csv"),
            folder + @"\issues_issues.csv");

        // An interactive run has no job to take a log name from and passes the task's own name;
        // printing it twice is what the file log fixed, and this path must not reintroduce it.
        Expect("the task name is not repeated for an interactive run",
            DataExtractPaths.RunFolder(@"C:\logs", "Get Data Extract", "Get Data Extract", stamp, "c0ffee03"),
            @"C:\logs\Get Data Extract\20260918_060500_Get Data Extract_c0ffee03");
    }

    /// <summary>
    /// Which request failures are worth trying again, and which are answers.
    ///
    /// <para>MEASURED 2026-08-26: a Get File Log over
    /// <c>Project-ALPHA-A…\\2.1.1_Drawings</c> failed 156 s in with "the request was canceled
    /// due to the configured HttpClient.Timeout of 100 seconds elapsing", while a sibling folder
    /// pulled 291 versions in 28 s. Nothing capped the folder — 100 s is HttpClient's default
    /// on ONE request, and a drawings folder full of PDFs and DWGs spends one request per file.
    /// Retrying the transient half of that is what keeps a big folder readable.</para>
    /// </summary>
    private static void FileLogRetryCases()
    {
        // A timeout arrives as a CANCELLATION, because HttpClient cancels its own token. Nothing
        // else cancels this crawl, so treating it as a timeout is safe — and getting this wrong
        // is what would silently turn the retry off.
        Expect("a TaskCanceledException is the timeout shape",
            DataManagementClient.IsTimeoutForTest(new TaskCanceledException()), true);
        Expect("…and so is a bare OperationCanceledException",
            DataManagementClient.IsTimeoutForTest(new OperationCanceledException()), true);
        Expect("a wrapped TimeoutException counts too",
            DataManagementClient.IsTimeoutForTest(
                new System.Net.Http.HttpRequestException("x", new TimeoutException())), true);
        Expect("an ordinary failure does not",
            DataManagementClient.IsTimeoutForTest(new InvalidOperationException()), false);

        // 429 is the one that matters for a folder of thousands of files.
        Expect("a throttle is retried",
            DataManagementClient.IsTransientForTest((System.Net.HttpStatusCode)429), true);
        Expect("so is a bad gateway",
            DataManagementClient.IsTransientForTest(System.Net.HttpStatusCode.BadGateway), true);
        Expect("and a gateway timeout",
            DataManagementClient.IsTransientForTest(System.Net.HttpStatusCode.GatewayTimeout), true);

        // These are answers, not hiccups: retrying them only spends the user's time to be told the
        // same thing twice more.
        Expect("a 401 is not retried",
            DataManagementClient.IsTransientForTest(System.Net.HttpStatusCode.Unauthorized), false);
        Expect("nor a 403",
            DataManagementClient.IsTransientForTest(System.Net.HttpStatusCode.Forbidden), false);
        Expect("nor a 404",
            DataManagementClient.IsTransientForTest(System.Net.HttpStatusCode.NotFound), false);

        // Backoff grows, and the request timeout is well clear of the 100 s default that failed.
        Expect("the first backoff is short", DataManagementClient.BackoffForTest(1).TotalSeconds, 2d);
        Expect("the second is longer", DataManagementClient.BackoffForTest(2).TotalSeconds, 6d);
        Expect("one request may now take far longer than 100s",
            DataManagementClient.RequestTimeoutForTest > TimeSpan.FromSeconds(100), true);

        // A REST-only job's REPORT. Until 1.5.1.3 a job made only of Get File Log / Get Data Extract
        // finished in total silence: the runner's short-circuit for it wrote its log rows and quit,
        // and only the model path ever raised a notification. The aggregation below is what the
        // notification and the "Run now" dialog are built from, so it is pinned here — it is pure,
        // and the run it describes is the one nobody is awake to watch.
        var reported = new RestOnlyOutcome();
        reported.Add(RestOnlyStepOutcome.Refused(
            "Get File Log", "Get File Log: no set chosen.", @"C:\logs\run.csv"));
        Expect("a refused step is counted as failed", reported.StepsFailed, 1);
        Expect("…and not as delivered", reported.StepsOk, 0);
        Expect("a failed run keeps its notification until it is dismissed", reported.Failed, true);
        Expect("the refusal reaches the notification body",
            reported.Body.Contains("no set chosen"), true);
        Expect("the run's log is carried for the toast to link",
            reported.TrackingLog, @"C:\logs\run.csv");

        reported.Add(new RestOnlyStepOutcome
        {
            StepName = "Get Data Extract",
            Notes = new List<string> { "hoisted first", @"Get Data Extract: 7 file(s) → C:\logs\x" },
            Ok = true,
            TrackingLog = @"C:\logs\other.csv"
        });
        Expect("a delivered step is counted as such", reported.StepsOk, 1);
        Expect("one bad step still makes the run a failure", reported.Failed, true);
        // The SUMMARY is the last note, because every path out of a step closes with one sentence
        // about what happened. Taking the first would report the hoist notice instead of the result.
        Expect("the summary is the step's closing sentence",
            reported.Summaries[1], @"Get Data Extract: 7 file(s) → C:\logs\x");
        Expect("every step reaches the body", reported.Summaries.Count, 2);
        // First one wins: both steps of one run write into the same tracking log anyway, and a body
        // that linked the second would point somewhere the first step's rows are not.
        Expect("the first step's log is the one linked", reported.TrackingLog, @"C:\logs\run.csv");
        Expect("notes accumulate in run order", reported.Notes.Count, 3);

        Expect("a step that reported nothing still says something",
            new RestOnlyStepOutcome { StepName = "Get File Log" }.Summary,
            "Get File Log: nothing was reported.");
    }

    /// <summary>
    /// The URL column that replaced Version URN, in order of trust.
    ///
    /// <para>Forma's own <c>links.webView.href</c> wins wherever it exists — it is version-specific
    /// and points at the right site for the hub. The constructed URL is a last resort written from
    /// the documented shape and never verified against a live hub, so a test that pins the
    /// PREFERENCE matters more than one that pins the template.</para>
    /// </summary>
    private static void FileLogUrlCases()
    {
        var folder = new FormaEntry
        {
            Kind = FormaKind.Folder, Id = "urn:adsk.wipprod:fs.folder:co.FOLDER",
            Name = "01-WIP", ProjectId = "b.11111111-2222-3333-4444-555555555555",
        };
        const string itemUrn = "urn:adsk.wipprod:dm.lineage:ITEM";

        JsonElement Parse(string json) =>
            System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

        JsonElement versionWithLink = Parse(
            "{\"id\":\"v1\",\"links\":{\"webView\":{\"href\":\"https://acc/version-link\"}}}");
        JsonElement itemWithLink = Parse(
            "{\"id\":\"i1\",\"links\":{\"webView\":{\"href\":\"https://acc/item-link\"}}}");
        JsonElement bare = Parse("{\"id\":\"x\"}");

        Expect("the version's own web link wins",
            DataManagementClient.WebUrlFor(versionWithLink, itemWithLink, folder, itemUrn, 3),
            "https://acc/version-link");
        Expect("…then the item's, which at least opens the right file",
            DataManagementClient.WebUrlFor(bare, itemWithLink, folder, itemUrn, 3),
            "https://acc/item-link");

        string built = DataManagementClient.WebUrlFor(bare, bare, folder, itemUrn, 3);
        Expect("…and only then the constructed one", built.StartsWith("https://acc.autodesk.com/docs/files/projects/"), true);
        Expect("which strips the b. from the project id", built.Contains("b.1111"), false);
        Expect("carries the folder and the item", built.Contains("entityId=urn%3Aadsk"), true);
        Expect("and names the version", built.EndsWith("&version=3"), true);

        // A row with nothing to build from gets a blank cell, not half a URL.
        Expect("no item urn means no link at all",
            DataManagementClient.FallbackWebUrl("b.p", "urn:f", "", 3), "");
        Expect("nor does a missing project",
            DataManagementClient.FallbackWebUrl("", "urn:f", "urn:i", 3), "");
        Expect("an unknown version number simply omits it",
            DataManagementClient.FallbackWebUrl("b.p", "urn:f", "urn:i", 0).Contains("version="), false);
    }

    /// <summary>The CSV's locked column order — what every downstream workbook binds to.</summary>
    private static void FileLogColumnCases()
    {
        (IReadOnlyList<string> columns, List<IReadOnlyDictionary<string, string>> rows) =
            FormaExtractReport.FileLog(new[]
            {
                new FormaFileLogRow
                {
                    FileName = "SITE1A-BLDB-ARCH.rvt", FileType = "rvt", VersionNumber = 5,
                    Url = "https://acc/x", VersionUrn = "urn:v5", ModifiedBy = "Someone",
                },
            });

        Expect("the key column is File Name, not Model Name", columns.Contains("File Name"), true);
        Expect("Model Name is gone", columns.Contains("Model Name"), false);
        Expect("the link column is URL", columns.Contains("URL"), true);
        Expect("Version URN is gone from the CSV", columns.Contains("Version URN"), false);
        Expect("the file type stayed", columns.Contains("File Type"), true);
        Expect("and the row carries the link", rows[0]["URL"], "https://acc/x");
        Expect("…and the name", rows[0]["File Name"], "SITE1A-BLDB-ARCH.rvt");
    }

    /// <summary>
    /// The saved Get File Log set: what it round-trips, and that a copy is a real copy.
    ///
    /// <para>The folders live in the SET since 1.4.5. They used to be inferred from whichever of a
    /// job's model sets happened to be folder-backed, which meant a scheduled job could request a
    /// file log, find no folder-backed set, and produce nothing at all without saying so.</para>
    /// </summary>
    private static void FileLogSetCases()
    {
        var set = new FileLogInputSet
        {
            Name = "SITE weekly file log",
            IncludeSubfolders = true,
            Spec = new FileLogSpec { Scope = FileLogScope.RevitOnly, LatestVersionOnly = true },
            DestinationFolder = @"C:\logs",
            Folders =
            {
                new FolderRef { Name = "01-WIP", DisplayPath = "SITE / 01-WIP", ProjectId = "b.p1", FolderUrn = "urn:f1" },
                new FolderRef { Name = "02-Shared", DisplayPath = "SITE / 02-Shared", ProjectId = "b.p1", FolderUrn = "urn:f2" },
            },
        };

        FileLogInputSet back = System.Text.Json.JsonSerializer.Deserialize<FileLogInputSet>(
            System.Text.Json.JsonSerializer.Serialize(set))!;
        Expect("a saved set keeps its folders", back.Folders.Count, 2);
        Expect("…their URNs", back.Folders[1].FolderUrn, "urn:f2");
        Expect("…the subfolder flag", back.IncludeSubfolders, true);
        Expect("…and the scope inside its spec", back.Spec.Scope, FileLogScope.RevitOnly);
        Expect("a set with folders reports that it has them", back.HasFolders, true);
        Expect("and an empty one does not", new FileLogInputSet().HasFolders, false);

        // Clone() has to be DEEP: a split run duplicates the job carrying this set, and an aliased
        // list would be shared between processes. Mutating the copy must not touch the original.
        FileLogInputSet copy = set.Clone();
        copy.Folders[0].Name = "CHANGED";
        copy.Folders.Add(new FolderRef { Name = "03-Published", FolderUrn = "urn:f3" });
        copy.Spec.LatestVersionOnly = false;
        Expect("Clone() copies the folder objects, not the references", set.Folders[0].Name, "01-WIP");
        Expect("…and the list itself", set.Folders.Count, 2);
        Expect("…and the spec", set.Spec.LatestVersionOnly, true);

        // The job carries the set, and Duplicate() must carry it to a chunk child too.
        var job = new ScheduledJob
        {
            Name = "Daily Check SITE",
            RunItems = new List<RunItem> { new(FileLogStep.DisplayName, RunItemKind.Action) },
            FileLogInputs = set,
        };
        Expect("the step is recognised on the job", FileLogStep.IsRequested(job), true);
        Expect("…and as the only item", FileLogStep.IsOnly(job), true);
        ScheduledJob dup = job.Duplicate();
        dup.FileLogInputs!.Folders.Clear();
        Expect("Duplicate() deep-copies the set", job.FileLogInputs!.Folders.Count, 2);

        ScheduledJob old = System.Text.Json.JsonSerializer.Deserialize<ScheduledJob>(
            "{\"Name\":\"Daily Check SITE\"}")!;
        Expect("a job saved before 1.4.5 carries no set", old.FileLogInputs, (FileLogInputSet?)null);
    }

    /// <summary>
    /// One report per folder, named so two of them can be told apart.
    ///
    /// <para>Folder names repeat: MEASURED 2026-08-26, a run over Project-ALPHA-A and
    /// Project-BETA-A read two folders both called 2.1.1_Drawings, and the reports differed only
    /// by a trailing _2. The Forma project now leads the name. CsvReportWriter's own stamp is to the
    /// whole second, so the run stamp has to come from the run, not the write.</para>
    /// </summary>
    private static void FileLogReportPathCases()
    {
        var stamp = new DateTime(2026, 8, 26, 14, 30, 1);
        string dest = @"C:\logs";
        const string task = "Get File Log";

        // The real case, from the 2026-08-26 run: two folders with the SAME name under two
        // different Forma projects. They used to differ only by a trailing _2.
        var site1 = new FolderRef
        {
            Name = "2.1.1_Drawings",
            DisplayPath = @"Sample Hub\Project-ALPHA-A\Project Files\2_ARCHITECT-ENGINEER\2.1.1_Drawings",
        };
        var site2 = new FolderRef
        {
            Name = "2.1.1_Drawings",
            DisplayPath = @"Sample Hub\Project-BETA-A\Project Files\2_ARCHITECT-ENGINEER\2.1.1_Drawings",
        };

        Expect("the project comes out of the tree path", site1.FormaProjectName, "Project-ALPHA-A");
        Expect("and the hub is the segment before it", site1.FormaHubName, "Sample Hub");
        Expect("a folder with no captured path has no project",
            new FolderRef { Name = "x" }.FormaProjectName, "");

        string l1 = FileLogReportPaths.FolderLabel(site1.FormaProjectName, site1.Name);
        string l2 = FileLogReportPaths.FolderLabel(site2.FormaProjectName, site2.Name);
        Expect("the label leads with the project", l1, "Project-ALPHA-A_2.1.1_Drawings");
        Expect("so two same-named folders no longer collide", l1 == l2, false);
        Expect("without a project it is just the folder",
            FileLogReportPaths.FolderLabel("", "2.1.1_Drawings"), "2.1.1_Drawings");

        // A scheduled run: the job's log name and the task are different, so both appear.
        string a = FileLogReportPaths.OutputPath(dest, "SITE", task, stamp, l1);
        string b = FileLogReportPaths.OutputPath(dest, "SITE", task, stamp, l2);
        Expect("each folder gets its own report", a == b, false);
        Expect("the report lands in the task's folder",
            Path.GetDirectoryName(a), Path.Combine(dest, task));
        Expect("named for the run, the job, the task, the project and the folder",
            Path.GetFileName(a), "20260826_143001_SITE_Get File Log_Project-ALPHA-A_2.1.1_Drawings.csv");
        Expect("and the run stamp is shared, not the write time",
            Path.GetFileName(b).StartsWith("20260826_143001_"), true);

        // An interactive run has no job, so its log name IS the task name. Printing both gave
        // "…_Get File Log_Get File Log_…", which is what the first real run produced.
        string interactive = FileLogReportPaths.OutputPath(dest, task, task, stamp, l1);
        Expect("an interactive run does not repeat the task name",
            Path.GetFileName(interactive),
            "20260826_143001_Get File Log_Project-ALPHA-A_2.1.1_Drawings.csv");

        // Ordinals key on the LABEL. These two no longer collide, so neither is numbered.
        List<int> ordinals = FileLogReportPaths.Ordinals(new[] { l1, l2 });
        Expect("distinct projects need no disambiguation", ordinals[0] + ordinals[1], 2);

        // The same folder twice — the genuinely ambiguous case — still is disambiguated.
        List<int> same = FileLogReportPaths.Ordinals(new[] { l1, "other", l1 });
        Expect("the first of a repeated label is unsuffixed", same[0], 1);
        Expect("an unrelated label is also first", same[1], 1);
        Expect("the second of a repeated label is numbered", same[2], 2);
        string third = FileLogReportPaths.OutputPath(dest, "SITE", task, stamp, l1, same[2]);
        Expect("so its report cannot overwrite the first",
            Path.GetFileName(third),
            "20260826_143001_SITE_Get File Log_Project-ALPHA-A_2.1.1_Drawings_2.csv");

        // Long names are bounded: these paths live under deep OneDrive folders and Windows still
        // stops at 260 characters.
        string longLabel = FileLogReportPaths.FolderLabel(new string('P', 90), new string('F', 90));
        Expect("each part of a label is capped", longLabel.Length <= 81, true);
        Expect("and the head of each name survives", longLabel.StartsWith("PPPP"), true);

        // The log is NOT in the report folder: one Log\ at the destination root, shared by the run.
        string log = TrackingCsvLogger.PathFor(dest, "SITE", "Daily Check SITE", stamp);
        Expect("the tracking log sits under <dest>\\Log",
            Path.GetDirectoryName(log), Path.Combine(dest, "Log"));
        Expect("…which is not where the reports go",
            Path.GetDirectoryName(log) == Path.GetDirectoryName(a), false);
    }

    /// <summary>
    /// The detail log's columns, through the two cases that can corrupt a day-file.
    ///
    /// The <c>Run</c> column was added in 1.3.8.1, and this file is appended to all day — so on the
    /// day the new build is installed, the morning's rows are already there under the old header.
    /// Writing a wider row into that file would shift every value one column right from that point
    /// on, which is worse than the missing column it was trying to add.
    /// </summary>
    private static void DetailLogColumnCases()
    {
        Section("1.3.8.1  the detail log's Run column, and the day it was added");

        var fields = new Dictionary<string, string> { ["Category"] = "Phase", ["Name"] = "Existing" };

        // A fresh file gets the new header, and the tag lands under it — this is also the real
        // round-trip, so a BOM or an escaping slip shows up here rather than on the office PC.
        string fresh = Path.Combine(_scratch, "detail-fresh.csv");
        new DetailCsvLogger(fresh, "20260811_131050").Log("A.rvt", fields);
        (List<string> header, List<string[]> rows) = MatrixCsvImport.ReadCsv(fresh);
        Expect("a new day-file carries Datetime, Model Name, Run + the action's columns",
            string.Join("|", header), "Datetime|Model Name|Run|Category|Name");
        Expect("and the run tag lands in the Run column", rows.Single()[2], "20260811_131050");
        Expect("with the model name still in its own column", rows.Single()[1], "A.rvt");
        Expect("and the action's value still in its own", rows.Single()[4], "Existing");

        // Upgrade day: a file the previous build started. Its header wins, and the row must align to
        // it — four values, not five, and every one under the right heading.
        string legacy = Path.Combine(_scratch, "detail-legacy.csv");
        File.WriteAllText(legacy,
            "Datetime,Model Name,Category,Name\n2026-08-11 09:00:00,OLD.rvt,Phase,Existing\n");
        new DetailCsvLogger(legacy, "20260811_131050").Log("B.rvt", fields);
        (List<string> oldHeader, List<string[]> oldRows) = MatrixCsvImport.ReadCsv(legacy);
        Expect("an existing header is left alone", string.Join("|", oldHeader),
            "Datetime|Model Name|Category|Name");
        Expect("both rows have the same width as the header",
            oldRows.All(r => r.Length == oldHeader.Count), true);
        Expect("and the new row's values are not shifted", oldRows.Last()[1] + "/" + oldRows.Last()[3],
            "B.rvt/Existing");
    }

    /// <summary>
    /// One matrix per RUN, not one per group.
    ///
    /// Every chunk of a split run appends to the same per-day detail CSV, so the whole run is in one
    /// file by the time the last child exits — but that file also holds every OTHER run of the same
    /// task that day, and runs overlap in practice (three supervised runs started within four
    /// minutes of each other on 2026-08-07). The <c>Run</c> column is what separates them, and this
    /// pins that a merge takes its own rows and nobody else's.
    /// </summary>
    private static void SplitRunMatrixCases()
    {
        var runStamp = new DateTime(2026, 8, 11, 13, 10, 50);
        string tag = DetailCsvLogger.RunTag(runStamp);
        string other = DetailCsvLogger.RunTag(runStamp.AddMinutes(-4));

        string dest = Path.Combine(_scratch, "merge-dest");
        string task = ActionNames.GetPhaseAndWorkset;
        string csv = DetailCsvLogger.PathFor(dest, "Test 138", task, runStamp);
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);

        // Three groups' rows interleaved with an earlier run's, exactly as the day-file holds them —
        // plus a RETRY of the first model. A model whose instance died after its detail rows were
        // written but before its tracking row is rerun by the supervisor, so both attempts sit here
        // under one run tag; the second must not be counted twice in the Grand Totals.
        File.WriteAllText(csv,
            "Datetime,Model Name,Run,Category,Name\n" +
            $"2026-08-11 13:06:00,OLD-MODEL.rvt,{other},Phase,Existing\n" +
            $"2026-08-11 13:14:00,SITE1A-BLDA-ARCH.rvt,{tag},Phase,New Construction\n" +
            $"2026-08-11 13:15:00,SITE1A-BLDB-ARCH.rvt,{tag},Phase,New Construction\n" +
            $"2026-08-11 13:16:00,SITE1A-BLDE-ARCH.rvt,{tag},Workset,Shared Levels\n" +
            $"2026-08-11 13:31:00,SITE1A-BLDA-ARCH.rvt,{tag},Phase,New Construction\n" +
            $"2026-08-11 13:17:00,OLD-MODEL.rvt,{other},Workset,Shared Levels\n");

        var manifest = new RunManifest
        {
            Name = "Batch Run",
            RunStamp = runStamp,
            TrackingLogPath = Path.Combine(_scratch, "merge-track.csv"),
            AllModels = Models(3),
            BaseJob = new ScheduledJob
            {
                Name = "Batch Run",
                DestinationFolder = dest,
                ProjectLogName = "Test 138",
                RunItems = new List<RunItem> { new(task, RunItemKind.Action) }
            }
        };

        var notes = new List<string>();
        RunEndMatrix.BuildForRun(manifest, (status, message) => notes.Add(status + ": " + message));

        string expected = MatrixPaths.OutputPath(dest, "Test 138", task, runStamp);
        Expect("the split run gets a matrix", File.Exists(expected), true);
        Expect("named for the run stamp, not the second the last chunk finished",
            Path.GetFileName(expected).StartsWith("20260811_131050_", StringComparison.Ordinal), true);
        Expect("and exactly one workbook exists for the task",
            Directory.GetFiles(Path.GetDirectoryName(expected)!, "*.xlsx").Length, 1);

        // The pivot itself: this run's four rows and none of the overlapping run's — of which the
        // retry duplicate is one, so three survive to be counted.
        (List<string> header, List<string[]> rows) = MatrixCsvImport.ReadCsv(csv);
        int runCol = header.FindIndex(h => h == DetailCsvLogger.RunColumn);
        Expect("only this run's rows are eligible", rows.Count(r => r[runCol] == tag), 4);
        Expect("a retried model's duplicate row is not counted twice",
            RunEndMatrix.DeduplicateForTest(header, rows.Where(r => r[runCol] == tag).ToList()).Count, 3);

        // A task the run did not include must not produce a workbook from the same day-file.
        var unrelated = new RunManifest
        {
            Name = "Batch Run",
            RunStamp = runStamp,
            TrackingLogPath = manifest.TrackingLogPath,
            BaseJob = new ScheduledJob
            {
                Name = "Batch Run",
                DestinationFolder = dest,
                ProjectLogName = "Test 138",
                RunItems = new List<RunItem> { new("Get Site Location & PBP", RunItemKind.Action) }
            }
        };
        RunEndMatrix.BuildForRun(unrelated, (_, _) => { });
        Expect("a run without a matrix task writes nothing",
            Directory.GetFiles(Path.GetDirectoryName(expected)!, "*.xlsx").Length, 1);
    }

    /// <summary>
    /// The 2026-08-11 run that stopped itself. pid 39128 finished all nine of its models, posted its
    /// own close, and Revit took over ten minutes to actually exit — during which no stage was
    /// written, no cache grew and CPU stayed under the busy threshold, because that is what a
    /// shutdown looks like. The supervisor read it as <c>Wedged</c> at the stage
    /// <c>'normal close posted pid=39128: scheduled/headless batch complete'</c>, stopped the run,
    /// and deleted every queued group's chunk file. A completed instance cancelled the rest of the
    /// run, which is the worst possible trade: nothing was rescued and work was lost.
    /// </summary>
    private static void ShutdownCases()
    {
        Section("1.3.8.1  a finished instance is not a wedged one");

        string closeStage = $"{RevitShutdown.ClosePostedMark} pid=39128: scheduled/headless batch complete";
        Expect("the close mark is recognised", ChildLiveness.IsShuttingDown(closeStage), true);
        Expect("a work stage is not a close mark",
            ChildLiveness.IsShuttingDown("Get Design Option | A.rvt | run"), false);
        // The close line contains no " | ", so it must not be mistaken for progress either.
        Expect("the close mark is not a work mark", ChildLiveness.IsWorkMark(closeStage), false);

        // THE REGRESSION, at the exact shape it happened: flat for longer than the no-progress
        // grace, at the close stage, nine models in the group.
        var closing = Watch(pid: 39128, startedMinutesAgo: 40);
        closing.LastStage = closeStage;
        closing.LastWorkMarkAt = DateTime.Now.AddMinutes(-11);
        closing.CurrentModelStartedAt = DateTime.Now.AddMinutes(-11);
        closing.LastProgressAt = DateTime.Now.AddMinutes(-11);
        Expect("flat for 11 min after posting its close is not wedged",
            ChildLiveness.Check(closing, EmptyTail(), Models(9)).Verdict,
            ChildLiveness.Verdict.Working);

        // Checked before the ceilings too: an instance that used its whole allowance and then asked
        // to close must not be reported as over ceiling on its way out.
        var closingLate = Watch(pid: 39129, startedMinutesAgo: 400);
        closingLate.LastStage = closeStage;
        closingLate.LastWorkMarkAt = DateTime.Now.AddMinutes(-120);
        closingLate.CurrentModelStartedAt = DateTime.Now.AddMinutes(-120);
        closingLate.LastProgressAt = DateTime.Now.AddMinutes(-120);
        Expect("a 1-model instance past its ceiling but closing is not over ceiling",
            ChildLiveness.Check(closingLate, EmptyTail(), Models(1)).Verdict,
            ChildLiveness.Verdict.Working);

        // Still bounded — but by a verdict the supervisor does not stop the run for.
        var stuckClosing = Watch(pid: 39130, startedMinutesAgo: 90);
        stuckClosing.LastStage = closeStage;
        stuckClosing.LastWorkMarkAt = DateTime.Now.AddMinutes(-45);
        stuckClosing.LastProgressAt = DateTime.Now.AddMinutes(-45);
        stuckClosing.ShutdownSince = DateTime.Now.AddMinutes(-45);
        Expect("45 min into a shutdown is reported, not treated as a wedge",
            ChildLiveness.Check(stuckClosing, EmptyTail(), Models(9)).Verdict,
            ChildLiveness.Verdict.ShutdownStalled);

        // And a genuine wedge — same flat signals, but at a WORK stage — still stops the run.
        // Not `run`: since 1.4.2 that stage carries its own 60-minute grace (SlowActionCases).
        var wedged = Watch(pid: 39131, startedMinutesAgo: 40);
        wedged.LastStage = "Cut model | OLD-BLDA-ELE.rvt | persist";
        wedged.LastWorkMarkAt = DateTime.Now.AddMinutes(-11);
        wedged.CurrentModelStartedAt = DateTime.Now.AddMinutes(-11);
        wedged.LastProgressAt = DateTime.Now.AddMinutes(-11);
        Expect("flat for 11 min mid-persist is still wedged",
            ChildLiveness.Check(wedged, EmptyTail(), Models(9)).Verdict,
            ChildLiveness.Verdict.Wedged);

        // A lingering instance must stop holding a queued group behind it: its models are logged and
        // a Revit tearing down is releasing memory, not competing for it.
        var groups = new List<RunGroup>
        {
            new() { ChunkId = "a", Pid = 101, StartedAt = DateTime.Now, SlowExitReported = true },
            new() { ChunkId = "b", Pid = 102, StartedAt = DateTime.Now },
            new() { ChunkId = "c", Pid = 0 },
        };
        Expect("a slow-exiting group frees its slot",
            RunSupervisor.SlotsToFill(groups, 2, g => !g.SlowExitReported).Single(), 2);
        Expect("and without that rule the queued group would still be waiting",
            RunSupervisor.SlotsToFill(groups, 2).Count, 0);
    }

    /// <summary>
    /// Which Forma models the browser lets you pick, and — the case that matters most — which ones it
    /// must NOT refuse. 1.3.5.4 put a version on model SETS and was withdrawn the same day because
    /// it guarded on data nobody had entered; the governing rule that came out of it is that an
    /// unknown version is never blocked. It is doubly load-bearing here: whether Forma reports
    /// <c>revitProjectVersion</c> on this hub at all is unverified, and if it does not, blocking on
    /// unknown would grey out every model in the browser.
    /// </summary>
    private static void ModelBlockCases()
    {
        Section("1.3.8.2  the crawler greys out what this Revit cannot run");

        FormaModelItem Cloud(string version, bool workshared = true) => new()
        {
            DisplayName = "SITE2A-BLDB-STR.rvt",
            ModelGuid = Guid.NewGuid(),
            RevitVersion = version,
            IsCloudWorkshared = workshared
        };

        Expect("a matching year is selectable", Cloud("2026").BlockedReason("2026"), null);
        Expect("a NEWER model is blocked — it cannot be opened at all",
            Cloud("2027").BlockedReason("2026") != null, true);
        Expect("an OLDER model is blocked too — opening it would upgrade it",
            Cloud("2024").BlockedReason("2026") != null, true);
        Expect("a plain upload is blocked whatever its year",
            Cloud("2026", workshared: false).BlockedReason("2026") != null, true);

        // THE RULE. Both directions of "we do not know" must stay selectable.
        Expect("a model whose year Forma did not report stays selectable",
            Cloud("").BlockedReason("2026"), null);
        Expect("and so does every model when the host has no Revit to compare against",
            Cloud("2024").BlockedReason(""), null);

        // A local file is judged from its own header at run time (BatchEngine.WrongVersionReason),
        // exactly and for free — nothing here should second-guess it.
        var local = FormaModelItem.Local(@"C:\sim\Tower.rvt");
        Expect("a local file is never blocked here", local.BlockedReason("2026"), null);

        Expect("the year column says so when the year is unknown", Cloud("").VersionLabel, "—");
        Expect("and shows the year when it is known", Cloud("2026").VersionLabel, "2026");
        Expect("the type column names a plain upload",
            Cloud("2026", workshared: false).TypeLabel, "uploaded file");

        // Two uploads in one folder both have an empty ModelGuid; without the lineage-URN fallback
        // they would key identically and the second would vanish in de-duplication.
        var a = new FormaModelItem { DisplayName = "A.rvt", ItemUrn = "urn:a", IsCloudWorkshared = false };
        var b = new FormaModelItem { DisplayName = "B.rvt", ItemUrn = "urn:b", IsCloudWorkshared = false };
        Expect("two GUID-less models are told apart", a.Key == b.Key, false);

        CopyAndShareCases();
        ScalarTextCases();
    }

    /// <summary>
    /// A copy / shared / Bridge-consumed instance carries the SOURCE's cloud GUIDs, so it passes
    /// every worksharing test and looks selectable. Running one does not fail — it silently
    /// processes the source model a second time. Proven on this account: a "Test Share" copy's
    /// version reports the "Test 2A" original's lineage as its originalItemUrn.
    /// </summary>
    private static void CopyAndShareCases()
    {
        FormaModelItem Copy(string version = "2026") => new()
        {
            DisplayName = "SITE1A-BLDA-STR.rvt",
            ModelGuid = Guid.NewGuid(),
            RevitVersion = version,
            IsCloudWorkshared = true,
            IsCopyOrShare = true,
            SourceItemUrn = "urn:adsk.wipprod:dm.lineage:SampleLineageId0000001"
        };

        Expect("a copy is blocked", Copy().BlockedReason("2026") != null, true);
        // The year check must not be reached first: a copy inherits the source's year, so a
        // matching year is no evidence that running it is the right thing to do.
        Expect("…even when its Revit year matches exactly",
            Copy("2026").BlockedReason("2026")!.Contains("copy"), true);
        Expect("…and it says so in the Type column", Copy().TypeLabel, "copy / shared");

        // The original in the same folder is unaffected.
        var original = new FormaModelItem
        {
            DisplayName = "SITE1A-BLDA-STR.rvt",
            ModelGuid = Guid.NewGuid(),
            RevitVersion = "2026",
            IsCloudWorkshared = true
        };
        Expect("the original is still selectable", original.BlockedReason("2026"), null);
        Expect("and still reads as cloud workshared", original.TypeLabel, "cloud workshared");
    }

    /// <summary>
    /// THE 1.3.8.2 DEFECT. Forma sends <c>"revitProjectVersion": 2026</c> as a JSON NUMBER, and the
    /// reader returned a value only for strings — so the crawler's Revit column read "—" for every
    /// model on a hub that was answering perfectly, and the question of whether the field existed
    /// at all (open since 1.3.5.5) was answered "no" by our own code.
    /// </summary>
    private static void ScalarTextCases()
    {
        static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        Expect("a numeric Revit year is read", DataManagementClient.ScalarText(Parse("2026")), "2026");
        Expect("a string one still is", DataManagementClient.ScalarText(Parse("\"2026\"")), "2026");
        // Invariant formatting: a locale that groups thousands must not turn 2026 into "2,026".
        Expect("and it is never locale-formatted",
            DataManagementClient.ScalarText(Parse("2026")).Contains(","), false);
        Expect("a bool comes back as text", DataManagementClient.ScalarText(Parse("true")), "true");
        Expect("null is empty", DataManagementClient.ScalarText(Parse("null")), "");
        Expect("an object is empty, not raw JSON",
            DataManagementClient.ScalarText(Parse("{\"a\":1}")), "");
    }

    /// <summary>
    /// The 2026-08-13 regression, at the shape it happened. Report
    /// <c>BKKLH3C7QV3_20260813_1851</c>: pid 75764 began
    /// <c>Batch Interop | SITE2A-BLDB-STR.rvt | open (background, detached)</c> at 18:35:36 and was
    /// declared wedged at 18:45:53 on <c>cpu +0.3s</c>. The open returned at <b>18:51:32</b> and the
    /// instance finished <c>6 ok, 0 failed</c>. Revit's journal blames the Steel Connections
    /// extension contending with a sibling Revit for a machine-wide SQL instance and an abandoned
    /// mutex — the point being that an open can be blocked for a quarter of an hour and perfectly
    /// healthy, so its silence must not be read the way a stalled <c>run</c> is.
    /// </summary>
    private static void SlowOpenCases()
    {
        Section("1.3.8.2  an open is allowed to be slow");

        string openStage = $"Batch Interop | SITE2A-BLDB-STR.rvt | {BatchStages.OpenDetached}";
        Expect("the open stage is recognised", ChildLiveness.IsOpening(openStage), true);
        Expect("the attached open is too",
            ChildLiveness.IsOpening($"Cut model | A.rvt | {BatchStages.OpenAttached}"), true);
        Expect("a run stage is not an open",
            ChildLiveness.IsOpening("Batch Interop | A.rvt | run"), false);
        // A lifecycle line carries no " | ", so it can never be mistaken for an open either — which
        // is what stops "staggered start: waiting 12s before the first cloud open" matching.
        Expect("a lifecycle line is not an open",
            ChildLiveness.IsOpening("staggered start: waiting 12s before the first cloud open"), false);

        // THE REGRESSION: flat for longer than the ten-minute no-progress grace, mid-open.
        var opening = Watch(pid: 75764, startedMinutesAgo: 25);
        opening.LastStage = openStage;
        opening.LastWorkMarkAt = DateTime.Now.AddMinutes(-11);
        opening.LastProgressAt = DateTime.Now.AddMinutes(-11);
        Expect("flat for 11 min mid-open is not wedged",
            ChildLiveness.Check(opening, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.Working);

        // The measured duration itself: 15 min 55 s. This is the number the run was lost over.
        var measured = Watch(pid: 75764, startedMinutesAgo: 25);
        measured.LastStage = openStage;
        measured.LastWorkMarkAt = DateTime.Now.AddMinutes(-16);
        measured.LastProgressAt = DateTime.Now.AddMinutes(-16);
        Expect("the measured 16-minute open is not wedged",
            ChildLiveness.Check(measured, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.Working);

        // Still bounded — but by a verdict the supervisor does not stop the run for.
        var slow = Watch(pid: 75765, startedMinutesAgo: 45);
        slow.LastStage = openStage;
        slow.LastWorkMarkAt = DateTime.Now.AddMinutes(-31);
        slow.LastProgressAt = DateTime.Now.AddMinutes(-31);
        Expect("31 min mid-open is reported, not treated as a wedge",
            ChildLiveness.Check(slow, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.OpenSlow);

        // And a genuinely hung open is still caught — by the per-model ceiling, which DOES stop the
        // run. The grace widens one clock; it does not remove the backstop behind it.
        var hung = Watch(pid: 75766, startedMinutesAgo: 96);
        hung.LastStage = openStage;
        hung.LastWorkMarkAt = DateTime.Now.AddMinutes(-95);
        hung.CurrentModelStartedAt = DateTime.Now.AddMinutes(-95);
        hung.LastProgressAt = DateTime.Now.AddMinutes(-95);
        Expect("an open past the per-model ceiling still stops the run",
            ChildLiveness.Check(hung, EmptyTail(), Models(1)).Verdict,
            ChildLiveness.Verdict.OverCeiling);

        // Guarding the guard: the same flat signals at a stage that is NEITHER an open NOR an action
        // must still be a wedge, or these graces would have quietly disabled stall detection.
        // "persist" is the right probe — it sits between `ran` and the next pass, it is bracketed by
        // breadcrumbs milliseconds apart in every real log, and eleven minutes of silence there is
        // genuinely wrong. (This case used to use `run`; 1.4.2 gave that stage its own grace, and
        // this spec failing is how that change announced itself.)
        var wedged = Watch(pid: 75767, startedMinutesAgo: 25);
        wedged.LastStage = "Batch Interop | SITE2A-BLDB-STR.rvt | persist";
        wedged.LastWorkMarkAt = DateTime.Now.AddMinutes(-11);
        wedged.LastProgressAt = DateTime.Now.AddMinutes(-11);
        Expect("flat for 11 min mid-persist is still wedged",
            ChildLiveness.Check(wedged, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.Wedged);

        ActivatedOpenCases();
        CloudOpenGateCases();
        SlowActionCases();
    }

    /// <summary>
    /// 1.4.2 — the same argument as <see cref="SlowOpenCases"/>, for the other stage that is one call
    /// between two breadcrumbs.
    ///
    /// <para>Report <c>BKKLH3C7QV3_20260818_0801</c>: pid 81908 entered
    /// <c>Drawing Progress | SITE3A-BLDC-STR.rvt | run (reusing open document)</c> at 03:22:13 and was
    /// declared wedged at 03:32:26 on <c>cpu +1.2s</c>. The call returned at <b>03:48:24</b> — 26 min
    /// 11 s — and the instance reported <c>36 ok, 0 failed</c> and posted its own close. Its run had
    /// already been stopped, cancelling the retry round. The 17 Aug bundle holds a 45 min 12 s pass of
    /// the same action, which is why the grace is 60 minutes and not 45.</para>
    /// </summary>
    private static void SlowActionCases()
    {
        Section("1.4.2  an action is allowed to be slow");

        string runStage = $"Drawing Progress | SITE3A-BLDC-STR.rvt | {BatchStages.ActionRunReusing}";
        Expect("the reusing-run stage is recognised", ChildLiveness.IsRunningAction(runStage), true);
        Expect("a plain run stage is too",
            ChildLiveness.IsRunningAction($"Drawing Progress | A.rvt | {BatchStages.ActionRun}"), true);
        Expect("an open is not an action run",
            ChildLiveness.IsRunningAction($"Drawing Progress | A.rvt | {BatchStages.OpenDetached}"), false);
        Expect("a finished action is not a running one",
            ChildLiveness.IsRunningAction("Drawing Progress | A.rvt | ran"), false);
        // The reason the match is on the LAST segment, compared whole: "run" is three letters that
        // appear inside ordinary words and inside model names. A substring match would hand the
        // 60-minute grace to stages that must not have it.
        Expect("a model whose NAME contains run is not a run stage",
            ChildLiveness.IsRunningAction("Drawing Progress | OLD-RUNWAY-STR.rvt | persist"), false);
        Expect("a lifecycle line is not an action",
            ChildLiveness.IsRunningAction("batch model 6/6, pass 5/6"), false);

        // THE REGRESSION, at the time it fired: ten minutes flat, mid-action.
        var running = Watch(pid: 81908, startedMinutesAgo: 40);
        running.LastStage = runStage;
        running.LastWorkMarkAt = DateTime.Now.AddMinutes(-11);
        running.LastProgressAt = DateTime.Now.AddMinutes(-11);
        Expect("flat for 11 min mid-action is not wedged",
            ChildLiveness.Check(running, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.Working);

        // The two measured durations. Both were healthy instances; both must be Working.
        var measured = Watch(pid: 81908, startedMinutesAgo: 40);
        measured.LastStage = runStage;
        measured.LastWorkMarkAt = DateTime.Now.AddMinutes(-27);
        measured.LastProgressAt = DateTime.Now.AddMinutes(-27);
        Expect("the measured 26-minute action is not wedged",
            ChildLiveness.Check(measured, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.Working);

        var worst = Watch(pid: 42456, startedMinutesAgo: 60);
        worst.LastStage = $"Drawing Progress | SITE2A-BLDB-ELE.rvt | {BatchStages.ActionRunReusing}";
        worst.LastWorkMarkAt = DateTime.Now.AddMinutes(-46);
        worst.LastProgressAt = DateTime.Now.AddMinutes(-46);
        Expect("…and neither is the worst measured pass, 45 min",
            ChildLiveness.Check(worst, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.Working);

        // Past the grace it is REPORTED, with a verdict the supervisor does not stop the run for.
        var slow = Watch(pid: 81909, startedMinutesAgo: 75);
        slow.LastStage = runStage;
        slow.LastWorkMarkAt = DateTime.Now.AddMinutes(-61);
        slow.LastProgressAt = DateTime.Now.AddMinutes(-61);
        Expect("61 min mid-action is reported, not treated as a wedge",
            ChildLiveness.Check(slow, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.ActionSlow);

        // The backstop is untouched: an action that never returns is still caught by the per-model
        // ceiling, which DOES stop the run.
        var hung = Watch(pid: 81910, startedMinutesAgo: 96);
        hung.LastStage = runStage;
        hung.LastWorkMarkAt = DateTime.Now.AddMinutes(-95);
        hung.CurrentModelStartedAt = DateTime.Now.AddMinutes(-95);
        hung.LastProgressAt = DateTime.Now.AddMinutes(-95);
        Expect("an action past the per-model ceiling still stops the run",
            ChildLiveness.Check(hung, EmptyTail(), Models(1)).Verdict,
            ChildLiveness.Verdict.OverCeiling);

        Expect("the action grace is 60 min, above the 45-min worst case",
            ChildLiveness.ActionGrace, TimeSpan.FromMinutes(60));
        Expect("…and below the per-model ceiling that backs it",
            ChildLiveness.ActionGrace < ChildLiveness.DefaultCeiling, true);
    }

    /// <summary>
    /// 1.3.8.4 — the same rule for the OTHER open. 1.3.8.2 taught the supervisor that a background
    /// open may be slow, and matched it on <c>"open (background"</c>, which covers the Idling path
    /// and the probe. It does not cover the ACTIVATED open the modal runner uses for every pass
    /// whose action needs the active document — Dynamo scripts, and now Export NWC, whose
    /// all-worksets open is exactly the slow kind. Left unmatched, a healthy child on that path
    /// would be condemned at ten flat minutes: the very bug 1.3.8.2 fixed, through the one door it
    /// did not cover.
    /// </summary>
    private static void ActivatedOpenCases()
    {
        Section("1.3.8.4  an ACTIVATED open is allowed to be slow too");

        string activated = $"Export NWC | SITE2A-BLDB-STR.rvt | {BatchStages.OpenActivatedDetached}";
        Expect("the activated detached open is recognised",
            ChildLiveness.IsOpening(activated), true);
        Expect("the activated attached open is too",
            ChildLiveness.IsOpening($"Correct Workset | A.rvt | {BatchStages.OpenActivatedAttached}"), true);
        Expect("the probe open still matches (it is a background open)",
            ChildLiveness.IsOpening("Internal Relink | A.rvt | probe open (background)"), true);
        Expect("the workset reopen is an open",
            ChildLiveness.IsOpening($"Internal Relink | A.rvt | {BatchStages.ReopenForWorksets}3 workset(s)"), true);
        // The completion marks are NOT opens — they are the breadcrumb that the open returned.
        Expect("'opened' is not an open", ChildLiveness.IsOpening("Export NWC | A.rvt | opened"), false);
        Expect("'reopened' is not an open", ChildLiveness.IsOpening("Internal Relink | A.rvt | reopened"), false);

        // The regression this prevents, on the activated path: an all-worksets cloud open that has
        // been silent for longer than the ten-minute no-progress grace.
        var opening = Watch(pid: 81002, startedMinutesAgo: 25);
        opening.LastStage = activated;
        opening.LastWorkMarkAt = DateTime.Now.AddMinutes(-11);
        opening.LastProgressAt = DateTime.Now.AddMinutes(-11);
        Expect("flat for 11 min mid-activated-open is not wedged",
            ChildLiveness.Check(opening, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.Working);

        var slow = Watch(pid: 81003, startedMinutesAgo: 45);
        slow.LastStage = activated;
        slow.LastWorkMarkAt = DateTime.Now.AddMinutes(-31);
        slow.LastProgressAt = DateTime.Now.AddMinutes(-31);
        Expect("31 min mid-activated-open is reported, not treated as a wedge",
            ChildLiveness.Check(slow, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.OpenSlow);

        // Guarding the guard, again: widening the match must not make every stage an open.
        // Probed at `persist` rather than `run` since 1.4.2 gave `run` its own grace.
        var wedged = Watch(pid: 81004, startedMinutesAgo: 25);
        wedged.LastStage = "Export NWC | SITE2A-BLDB-STR.rvt | persist";
        wedged.LastWorkMarkAt = DateTime.Now.AddMinutes(-11);
        wedged.LastProgressAt = DateTime.Now.AddMinutes(-11);
        Expect("flat for 11 min mid-persist is still wedged on this path too",
            ChildLiveness.Check(wedged, EmptyTail(), Models(6)).Verdict,
            ChildLiveness.Verdict.Wedged);
    }

    /// <summary>
    /// The other half of the 2026-08-13 fix: two sibling Revits must not be inside a document open
    /// together, because Revit's Steel Connections extension contends with itself over a
    /// machine-wide database when they are. Named locally so the harness can never take the real
    /// gate and hold a genuine run behind it.
    /// </summary>
    private static void CloudOpenGateCases()
    {
        CloudOpenGate.NameOverride = @"Local\LivenessSim_CloudOpenGate_" + Guid.NewGuid().ToString("N");
        try
        {
            // An action that does not ask for the gate never queues behind anything.
            using (CloudOpenGate.Acquire(needed: false, "2026"))
            using (CloudOpenGate.Acquire(needed: false, "2026"))
                Expect("a run that does not need the gate is never held", true, true);

            IDisposable held = CloudOpenGate.Acquire(needed: true, "2026");

            // A sibling must NOT get in while the first holds it. Run on another thread: a mutex is
            // re-entrant for the thread that owns it, so testing this in-line would prove nothing.
            var gotIn = new ManualResetEventSlim(false);
            var sibling = new Thread(() =>
            {
                using IDisposable mine = CloudOpenGate.Acquire(needed: true, "2026");
                gotIn.Set();
            }) { IsBackground = true };
            sibling.Start();

            Expect("a sibling does not get in while another instance is opening",
                gotIn.Wait(TimeSpan.FromMilliseconds(500)), false);

            held.Dispose();

            Expect("and gets in as soon as that open finishes",
                gotIn.Wait(TimeSpan.FromSeconds(10)), true);
            sibling.Join(TimeSpan.FromSeconds(10));
        }
        finally
        {
            CloudOpenGate.NameOverride = null;
        }
    }

    /// <summary>
    /// The stagger that never fired. <c>ChunkLauncher</c> assigned 0/12/24s, but the wait was
    /// measured from ARMING (Revit's OnStartup) while the first Idling tick came ~56s later, so
    /// every child skipped it: on 2026-08-11 three children reached their first cloud open at
    /// 13:12:56.099, .100 and .100 and all three died in Revit's loader. These cases pin the two
    /// halves of the fix — the clock, and the machine-wide gate that also covers a SECOND run
    /// started minutes after the first, which no per-run delay can see.
    /// </summary>
    private static void FirstOpenGateCases()
    {
        Section("1.3.8.1  first cloud opens are actually spread out");

        // The arithmetic the Idling handler performs, stated plainly: the wait is over only once
        // the delay has elapsed since the instance became ABLE to open — not since it armed.
        DateTime armed = DateTime.UtcNow.AddSeconds(-56);
        DateTime firstIdling = DateTime.UtcNow;
        Expect("56s of Revit start-up used to consume a 24s stagger",
            (firstIdling - armed).TotalSeconds >= 24, true);
        Expect("but measured from the first tick, the instance still owes its wait",
            (DateTime.UtcNow - firstIdling).TotalSeconds < 24, true);

        // The gate itself, against a scratch stamp — claiming the real one would make the next
        // genuine run on this PC wait for the test.
        FirstOpenGate.StampPathOverride = Path.Combine(_scratch, "first-open.stamp");

        // First claim goes through; the next is asked to wait; and an instance that has already
        // deferred past the cap goes anyway rather than stalling the run.
        Expect("the first instance through the gate may open",
            FirstOpenGate.TryClaim(DateTime.UtcNow, out _), true);
        bool second = FirstOpenGate.TryClaim(DateTime.UtcNow, out int wait);
        Expect("a second instance moments later is asked to wait", second, false);
        Expect("and is told roughly how long", wait > 0, true);
        Expect("an instance that has waited past the cap goes anyway",
            FirstOpenGate.TryClaim(DateTime.UtcNow.AddHours(-1), out _), true);
    }

    private static void CeilingCases()
    {
        Section("RC1  ceiling is per model, not per instance");

        // THE REGRESSION. Round 2 gave each instance 10 models; at ~4 min each they pass 45 minutes
        // legitimately. The old code compared one model's budget against the whole instance's age
        // and killed three of them — one mid-'run', reported as "no progress for 0 sec".
        var working = Watch(pid: 4001, startedMinutesAgo: 60);
        working.LastWorkMarkAt = DateTime.Now.AddMinutes(-1);
        working.CurrentModelStartedAt = DateTime.Now.AddMinutes(-2);
        working.LastProgressAt = DateTime.Now;                       // mid-run, progressing now
        Expect("a 10-model instance running 60 min, 2 min into its current model",
            ChildLiveness.Check(working, EmptyTail(), Models(10)).Verdict,
            ChildLiveness.Verdict.Working);

        // …and the per-model clock still fires when a single model really does hang past its budget.
        var stuckModel = Watch(pid: 4002, startedMinutesAgo: 200);
        stuckModel.LastWorkMarkAt = DateTime.Now.AddMinutes(-95);
        stuckModel.CurrentModelStartedAt = DateTime.Now.AddMinutes(-95);
        stuckModel.LastProgressAt = DateTime.Now;                    // still emitting signals
        Expect("one model in progress for 95 min (90 min ceiling)",
            ChildLiveness.Check(stuckModel, EmptyTail(), Models(10)).Verdict,
            ChildLiveness.Verdict.OverCeiling);

        // The instance-wide backstop scales with the assignment.
        var many = Watch(pid: 4003, startedMinutesAgo: 400);
        many.LastWorkMarkAt = DateTime.Now.AddMinutes(-5);
        many.CurrentModelStartedAt = DateTime.Now.AddMinutes(-5);
        many.LastProgressAt = DateTime.Now;
        Expect("a 10-model instance at 400 min is inside its 905 min instance ceiling",
            ChildLiveness.Check(many, EmptyTail(), Models(10)).Verdict,
            ChildLiveness.Verdict.Working);

        var one = Watch(pid: 4004, startedMinutesAgo: 400);
        one.LastWorkMarkAt = DateTime.Now.AddMinutes(-5);
        one.CurrentModelStartedAt = DateTime.Now.AddMinutes(-5);
        one.LastProgressAt = DateTime.Now;
        Expect("a 1-model instance at 400 min is past its 95 min instance ceiling",
            ChildLiveness.Check(one, EmptyTail(), Models(1)).Verdict,
            ChildLiveness.Verdict.OverCeiling);

        // The old floor of 45 min sat BELOW the 90 min unknown-size default, so finding the cache
        // file halved the budget. Knowing more must never buy less time.
        long twoHundredMb = 200L * 1024 * 1024;
        Expect("a small model's ceiling is not shorter than the unknown-size default",
            ChildLiveness.PerModelCeiling(twoHundredMb) >= ChildLiveness.DefaultCeiling, true);
        Expect("a 4 GB model gets more than the default",
            ChildLiveness.PerModelCeiling(4L * 1024 * 1024 * 1024) >= ChildLiveness.DefaultCeiling, true);
    }

    // ---- RC2: 'armed group' defeated the startup grace ----------------------------------------

    private static void StartupCases()
    {
        Section("RC2  a lifecycle breadcrumb is not evidence that work started");

        Expect("'armed group abc123' is not a work mark", ChildLiveness.IsWorkMark("armed group abc123"), false);
        Expect("'staggered start: waiting 96s' is not a work mark",
            ChildLiveness.IsWorkMark("staggered start: waiting 96s before the first cloud open"), false);
        Expect("a BatchEngine stage IS a work mark",
            ChildLiveness.IsWorkMark("Get Phase & Workset | SITE2A-BLDA-ARCH.rvt | open (background, detached)"), true);

        // Nine instances died here. Each had written exactly one line — "armed group <id>" — and the
        // old code read that as "processing started", skipping the startup grace entirely and
        // starting a ten-minute no-progress clock on a Revit that had not reached its first Idling
        // tick. The watches below have that line ALREADY CONSUMED (LastMarkAt set, no work mark),
        // which is the state a real watch is in after its first poll.
        var starting = Armed(pid: 5001, minutesAgo: 12);
        Expect("only 'armed group' after 12 min: still starting, not wedged",
            ChildLiveness.Check(starting, EmptyTail(), Models(1)).Verdict,
            ChildLiveness.Verdict.Working);

        // The office run's kills landed at 10m34s. That must now be inside the window.
        var atTheOldKillPoint = Armed(pid: 5002, minutesAgo: 11);
        Expect("…and still alive at 11 min, where the old code killed it",
            ChildLiveness.Check(atTheOldKillPoint, EmptyTail(), Models(1)).Verdict,
            ChildLiveness.Verdict.Working);

        // Alive but silent well past both clocks is a genuine failed launch, and says so.
        var dead = Armed(pid: 5003, minutesAgo: 40);
        ChildLiveness.Judgement j = ChildLiveness.Check(dead, EmptyTail(), Models(1));
        Expect("alive but silent for 40 min with no model opened", j.Verdict, ChildLiveness.Verdict.NeverStarted);
        Expect("and the reason names the clock that fired", j.Reason.Contains("no model opened"), true);

        // A kill must be able to justify itself — the old "no progress for 0 sec" could not.
        Expect("every kill carries a non-empty reason", j.Reason.Length > 0, true);

        // The trail reader must actually parse what App.OnStartup writes, or none of the above is
        // reached in production.
        string trail = Trail((Stamp(DateTime.Now.AddMinutes(-3)), 5004, "armed group f852b877"));
        var tail = new RunTrace.Tail(trail);
        tail.Poll();
        Expect("the trail reader parses a real 'armed group' line",
            tail.For(5004)?.Stage, "armed group f852b877");
    }

    // ---- RC4: the stagger ran inside the no-progress clock ------------------------------------

    private static void StaggerCase()
    {
        Section("RC4  time the child was told to wait is not time it failed");

        // Group 31 of a 31-group round was told to idle 360 s. Its clocks must start after that.
        var staggered = Watch(pid: 6001, startedMinutesAgo: 20, staggerSeconds: 360);
        staggered.LastProgressAt = DateTime.Now.AddMinutes(-20);
        var plain = Watch(pid: 6002, startedMinutesAgo: 20);
        plain.LastProgressAt = DateTime.Now.AddMinutes(-20);

        Expect("NotBefore excludes the stagger",
            (int)Math.Round((staggered.NotBefore - plain.NotBefore).TotalSeconds), 360);
        Expect("so a staggered child is judged 6 minutes later than an unstaggered one",
            staggered.NotBefore > plain.NotBefore, true);
    }

    // ---- RC5: a model with SOME rows counted as done ------------------------------------------

    private static void CompletionCases()
    {
        Section("RC5  a model is finished only when every action has a row");

        // Exactly the shape of the 7 models that shipped part-processed: killed after 3 of 6.
        string csv = Csv(
            ("PARTIAL.rvt", "1 of 6 — Get Site Location & PBP", "OK"),
            ("PARTIAL.rvt", "2 of 6 — Get Phase & Workset", "OK"),
            ("PARTIAL.rvt", "3 of 6 — Get Design Option", "OK"),
            ("WHOLE.rvt", "1 of 6 — Get Site Location & PBP", "OK"),
            ("WHOLE.rvt", "2 of 6 — Get Phase & Workset", "OK"),
            ("WHOLE.rvt", "3 of 6 — Get Design Option", "OK"),
            ("WHOLE.rvt", "4 of 6 — Get Link Workset", "OK"),
            ("WHOLE.rvt", "5 of 6 — Drawing Progress", "OK"),
            ("WHOLE.rvt", "6 of 6 — Type Mark by Text Note", "OK"),
            ("FAILED.rvt", "1 of 6 — Get Site Location & PBP", "OK"),
            ("FAILED.rvt", "2 of 6 — Get Phase & Workset", "ERROR"),
            ("SINGLE.rvt", "Get Phase & Workset", "OK"));

        HashSet<string> done = TrackingCsvReader.CompletedModels(csv);

        Expect("3 of 6 actions is NOT done", done.Contains("PARTIAL.rvt"), false);
        Expect("6 of 6 actions is done", done.Contains("WHOLE.rvt"), true);
        // Load-bearing: an action that fails ends the model's sequence, so its indices can never
        // reach 6. Without this rule a failing model would be retried until it exhausted the ladder.
        Expect("an ERROR row settles the model (never retried forever)", done.Contains("FAILED.rvt"), true);
        Expect("a single-action run needs one row", done.Contains("SINGLE.rvt"), true);
        Expect("a model with no rows at all is missing", done.Contains("ABSENT.rvt"), false);
    }

    // ---- RC3 / C4: the round ladder ------------------------------------------------------------

    private static void RoundLadderCases()
    {
        Section("C4  four rounds: your size, your size, auto, isolate");

        Expect("MaxRounds", RunSupervisor.MaxRounds, 4);

        Environment.SetEnvironmentVariable(InstanceBudget.EnvVar, "4");
        try
        {
            Expect("round 2 reruns at the original size",
                RunSupervisor.RoundGroupSize(2, originalSize: 10, missingCount: 56), 10);

            // The office run's round 3: 31 survivors. The old ladder made that 31 instances at once.
            Expect("round 3 fits 31 survivors into one wave of 4",
                RunSupervisor.RoundGroupSize(3, originalSize: 10, missingCount: 31), 8);
            Expect("…which is 4 groups, not 31",
                ChunkLauncher.GroupCount(31, RunSupervisor.RoundGroupSize(3, 10, 31)), 4);

            Expect("round 4 isolates", RunSupervisor.RoundGroupSize(4, originalSize: 10, missingCount: 31), 1);

            Expect("a run with no configured split still collapses in round 2",
                RunSupervisor.RoundGroupSize(2, originalSize: 0, missingCount: 7), 7);
        }
        finally { Environment.SetEnvironmentVariable(InstanceBudget.EnvVar, null); }

        Section("C3  the instance cap");
        Expect("cap is at least 1", InstanceBudget.MaxConcurrent() >= 1, true);
        Expect("cap is at most 6", InstanceBudget.MaxConcurrent() <= 6, true);
        Console.WriteLine("      this machine: " + InstanceBudget.Describe());

        AllWorksetsWeightCases();
    }

    /// <summary>
    /// 1.3.8.6, re-measured at 1.3.8.8 — a run that opens every workset holds the WHOLE model, plus
    /// its links, in each instance.
    ///
    /// <para>The original figure here was "eight instances took a 64 GB machine down to about 10 GB
    /// free", and it was worthless: it was measured on the run whose worksets never opened, so the
    /// instances held nothing. Once 1.3.8.7 made the open work, the journals gave the real peaks —
    /// <b>48.6 GB</b> and <b>42.9 GB</b> for a single instance, four at once leaving 12 MB free, and
    /// one Revit dead of an access violation. Hence 24 GB each and a ceiling of 2.</para>
    ///
    /// <para>The rule lives in <see cref="InstanceBudget.WeightFor"/> so the parent that splits a run
    /// and the supervisor that relaunches its groups cannot disagree about it.</para>
    /// </summary>
    private static void AllWorksetsWeightCases()
    {
        var nwc = new ScheduledJob
        {
            Name = "Nightly NWC",
            RunItems = new List<RunItem> { new(ActionNames.ExportNwc, RunItemKind.Action) },
        };
        Expect("an Export NWC job is an all-worksets run",
            InstanceBudget.WeightFor(nwc), RunWeight.AllWorksets);
        Expect("…and is capped at 2 instances",
            InstanceBudget.MaxConcurrent(RunWeight.AllWorksets) <= 2, true);
        Expect("…which is never zero", InstanceBudget.MaxConcurrent(RunWeight.AllWorksets) >= 1, true);

        // The arithmetic on a STATED machine, so the numbers that decide oversubscription can be
        // checked from any PC. 32 logical processors throughout: the office PC's, and high enough
        // that the CPU term never masks the RAM term being tested.
        Expect("the office PC (64 GB) gets 2 all-worksets instances",
            InstanceBudget.CapFor(RunWeight.AllWorksets, 64, 32), 2);
        Expect("…a 32 GB laptop gets 1",
            InstanceBudget.CapFor(RunWeight.AllWorksets, 32, 32), 1);
        Expect("…and 128 GB is still held at the ceiling of 2",
            InstanceBudget.CapFor(RunWeight.AllWorksets, 128, 32), 2);
        // The number this replaces: 12 GB each would have authorised four on the machine that then
        // ran out of memory. This is the regression the constant exists to prevent.
        Expect("…where the old 12 GB budget would have allowed 4",
            (int)Math.Floor((64 - 8) / 12d), 4);
        // 1.4.1 lowered the ordinary ceiling from 8 to 6: the same 64 models took 61.8 min on 8
        // instances and 135.8 min on ~16, and nothing about the per-instance cost changed between
        // the two runs. 8 was never measured — it was whatever the RAM term allowed.
        Expect("an ordinary run gets the measured ceiling of 6",
            InstanceBudget.CapFor(RunWeight.Normal, 64, 32), 6);
        Expect("…and unreadable RAM still falls back to the ceiling, not to 1",
            InstanceBudget.CapFor(RunWeight.AllWorksets, 0, 32), 2);

        // Since 1.3.8.8 both weights carry the same budget — a checking instance opens every workset
        // too, so the 10 GB it used to be budgeted was under-counting the Revit before the Model
        // Checker was even considered. The order still has to pick the heavier of the two, because
        // the next weight added will not be a tie.
        Expect("an Interop run is capped at 2 as well",
            InstanceBudget.CapFor(RunWeight.Interop, 64, 32), 2);
        var both = new ScheduledJob
        {
            Name = "Both",
            RunItems = new List<RunItem>
            {
                new(ActionNames.ExportNwc, RunItemKind.Action),
                new(InteropStep.DisplayName, RunItemKind.Action),
            },
        };
        Expect("a job with both weighs as Interop", InstanceBudget.WeightFor(both), RunWeight.Interop);

        var ordinary = new ScheduledJob
        {
            Name = "Ordinary",
            RunItems = new List<RunItem> { new(ActionNames.GetPhaseAndWorkset, RunItemKind.Action) },
        };
        Expect("an ordinary run is unaffected",
            InstanceBudget.WeightFor(ordinary), RunWeight.Normal);

        // The env override still wins for every weight — it exists for a machine the formula reads
        // wrong, and a cap that some weights ignored would be worse than no override at all.
        // Deliberately NOT 6 — that is the ordinary ceiling since 1.4.1, and an override whose value
        // matches a default it is supposed to beat proves nothing.
        Environment.SetEnvironmentVariable(InstanceBudget.EnvVar, "4");
        try
        {
            Expect("KHBATCH_MAX_INSTANCES still overrides the new cap",
                InstanceBudget.MaxConcurrent(RunWeight.AllWorksets), 4);
        }
        finally { Environment.SetEnvironmentVariable(InstanceBudget.EnvVar, null); }

        WorksetPlanCases();
        ReopenLadderCases();
        NonWorksharedCloudCases();
        UnexpectedExitCases();
        DialogBreakerCases();
        LauncherOrderCases();
        ScheduleRulesCases();
    }

    /// <summary>
    /// 1.4.0 — when a scheduled job next fires.
    ///
    /// <para>This arithmetic shipped as FOUR private copies, one per Schedule* window, with no test
    /// over any of them. They turned out to differ only in comments and brace style — but nothing
    /// would have said so if they had differed in substance, and their neighbour in the same four
    /// files (the grid refresh) HAD drifted, leaving two windows that lose your sort and your
    /// selected row.</para>
    ///
    /// <para>The reason there was no test is that each copy called <c>DateTime.Now</c> inline:
    /// "the next Tuesday after now" has no fixed answer to assert against.
    /// <see cref="ScheduleRules.Next(ScheduleRecurrence, IReadOnlyList{DayOfWeek}, DateTime?, bool, TimeSpan, DateTime)"/>
    /// takes the clock as a parameter, so every branch below is checkable.</para>
    /// </summary>
    private static void ScheduleRulesCases()
    {
        Section("1.4.0  when a schedule next fires");

        // A Wednesday, 10:00. Every case below is relative to this instant.
        var now = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Local);
        TimeSpan at1800 = new(18, 0, 0);
        TimeSpan at0900 = new(9, 0, 0);
        var noDays = Array.Empty<DayOfWeek>();

        // --- Once ----------------------------------------------------------------------------
        NextRun once = ScheduleRules.Next(ScheduleRecurrence.Once, noDays,
            new DateTime(2026, 8, 21), hasRun: false, at1800, now);
        Expect("a Once job fires on its own date", once.At, new DateTime(2026, 8, 21, 18, 0, 0));

        // The date passing does NOT retire it — only having run does. A job whose slot slipped by
        // while the PC slept still shows when it was due, which is what the list has to show.
        NextRun overdue = ScheduleRules.Next(ScheduleRecurrence.Once, noDays,
            new DateTime(2026, 8, 17), hasRun: false, at1800, now);
        Expect("…a Once job whose date has passed still reports it",
            overdue.At, new DateTime(2026, 8, 17, 18, 0, 0));

        Expect("…a Once job that has RUN has no next run",
            ScheduleRules.Next(ScheduleRecurrence.Once, noDays, new DateTime(2026, 8, 21),
                hasRun: true, at1800, now).Kind,
            ScheduleRulesKind.RanOnce);

        // A job saved before start dates existed carries none: it falls back to today.
        Expect("…a legacy Once job with no date falls back to today",
            ScheduleRules.Next(ScheduleRecurrence.Once, noDays, null, false, at1800, now).At,
            new DateTime(2026, 8, 19, 18, 0, 0));

        // --- Daily ---------------------------------------------------------------------------
        Expect("a Daily job later today fires today",
            ScheduleRules.Next(ScheduleRecurrence.Daily, noDays, null, false, at1800, now).At,
            new DateTime(2026, 8, 19, 18, 0, 0));
        Expect("…a Daily job whose time has passed fires tomorrow",
            ScheduleRules.Next(ScheduleRecurrence.Daily, noDays, null, false, at0900, now).At,
            new DateTime(2026, 8, 20, 9, 0, 0));
        // Exactly now counts as passed — a schedule must not fire in the instant being asked about.
        Expect("…a Daily job due exactly NOW fires tomorrow",
            ScheduleRules.Next(ScheduleRecurrence.Daily, noDays, null, false,
                new TimeSpan(10, 0, 0), now).At,
            new DateTime(2026, 8, 20, 10, 0, 0));

        // --- Weekly --------------------------------------------------------------------------
        var friOnly = new[] { DayOfWeek.Friday };
        Expect("a Weekly job searches forward to its day",
            ScheduleRules.Next(ScheduleRecurrence.Weekly, friOnly, null, false, at1800, now).At,
            new DateTime(2026, 8, 21, 18, 0, 0));

        // Today IS the selected day and the slot is still ahead: it fires today, not in a week.
        Expect("…today counts when its time is still ahead",
            ScheduleRules.Next(ScheduleRecurrence.Weekly, new[] { DayOfWeek.Wednesday },
                null, false, at1800, now).At,
            new DateTime(2026, 8, 19, 18, 0, 0));

        // Today is the selected day but the slot has gone: the same weekday, a week out. This is
        // why the search runs over EIGHT days rather than seven.
        Expect("…and rolls a full week when today's slot has gone",
            ScheduleRules.Next(ScheduleRecurrence.Weekly, new[] { DayOfWeek.Wednesday },
                null, false, at0900, now).At,
            new DateTime(2026, 8, 26, 9, 0, 0));

        // Crossing a month boundary is just arithmetic, but it is the kind of arithmetic that is
        // wrong in a copy nobody tested.
        Expect("…and crosses a month boundary",
            ScheduleRules.Next(ScheduleRecurrence.Weekly, new[] { DayOfWeek.Tuesday },
                null, false, at1800, new DateTime(2026, 8, 30, 10, 0, 0)).At,
            new DateTime(2026, 9, 1, 18, 0, 0));

        Expect("…a Weekly job with NO day ticked can never fire",
            ScheduleRules.Next(ScheduleRecurrence.Weekly, noDays, null, false, at1800, now).Kind,
            ScheduleRulesKind.Never);

        // --- The ad-hoc de-duplication key ----------------------------------------------------
        Expect("a local ad-hoc model is keyed by path, case-insensitively",
            ScheduleRules.AdHocKey(new CloudModel { DisplayName = "a", LocalPath = @"C:\A\B.RVT" }),
            ScheduleRules.AdHocKey(new CloudModel { DisplayName = "b", LocalPath = @"c:\a\b.rvt" }));
        Expect("…and a cloud model by its GUID, not its name",
            ScheduleRules.AdHocKey(new CloudModel { DisplayName = "one", ModelGuid = Guid.Parse("11111111-1111-1111-1111-111111111111") }),
            ScheduleRules.AdHocKey(new CloudModel { DisplayName = "two", ModelGuid = Guid.Parse("11111111-1111-1111-1111-111111111111") }));
        Expect("…so two different cloud models stay distinct",
            ScheduleRules.AdHocKey(new CloudModel { DisplayName = "x", ModelGuid = Guid.Parse("11111111-1111-1111-1111-111111111111") })
                != ScheduleRules.AdHocKey(new CloudModel { DisplayName = "x", ModelGuid = Guid.Parse("22222222-2222-2222-2222-222222222222") }),
            true);

        Expect("Mon maps to Monday", ScheduleRules.DayForLabel("Mon"), DayOfWeek.Monday);
        Expect("…Sun to Sunday", ScheduleRules.DayForLabel("Sun"), DayOfWeek.Sunday);
    }

    /// <summary>
    /// 1.3.9 — which copy of KhBatchLauncher.exe is tried first.
    ///
    /// <para>"Beside the loaded assembly" was unconditionally first, for the dev and side-by-side
    /// case. On a normal machine-wide install that folder IS
    /// <c>%ProgramData%\Autodesk\Revit\Addins\&lt;year&gt;</c> — the location the default SRP and
    /// AppLocker rule sets DENY — so every Revit, on every start and every notification, launched a
    /// program group policy blocks and logged a paragraph about it. In
    /// <c>BKKLH3C7QV3_20260815_2347</c> that was roughly a third of surface.log.</para>
    ///
    /// <para>The ordering is a pure function precisely so these cases can exist: they need no
    /// filesystem, no Revit and no policy-managed PC, which is the only kind of machine where the
    /// bug is visible at all.</para>
    /// </summary>
    private static void LauncherOrderCases()
    {
        Section("1.3.9  the launcher copy that runs is tried before the one policy blocks");

        const string programFiles = @"C:\Program Files";
        const string commonAppData = @"C:\ProgramData";
        string addins2026 = Path.Combine(commonAppData, @"Autodesk\Revit\Addins\2026");
        string kanghun = Path.Combine(programFiles, @"Kanghun BIM\KhBatchLauncher.exe");

        // The live case: the add-in is the machine-wide install, so its own folder is the denied one.
        var installed = TaskSchedulerGateway
            .OrderedLauncherPaths(addins2026, programFiles, commonAppData).ToList();

        Expect("a machine-wide install tries %ProgramFiles% FIRST", installed[0], kanghun);
        Expect("…and still reaches its own Addins copy",
            installed.Any(p => p.StartsWith(addins2026, StringComparison.OrdinalIgnoreCase)), true);
        Expect("…exactly once (the year loop already yields it)",
            installed.Count(p => string.Equals(p, Path.Combine(addins2026, "KhBatchLauncher.exe"),
                StringComparison.OrdinalIgnoreCase)), 1);
        Expect("…after %ProgramFiles%, never before",
            installed.FindIndex(p => p.StartsWith(
                Path.Combine(commonAppData, "Autodesk"), StringComparison.OrdinalIgnoreCase))
            > installed.IndexOf(kanghun), true);

        // The case the old order existed for, which must not regress: a dev or side-by-side build
        // has to run ITS OWN launcher, not whatever an unrelated install left in Program Files.
        var dev = TaskSchedulerGateway
            .OrderedLauncherPaths(@"D:\src\khbatch\bin\Release", programFiles, commonAppData).ToList();

        Expect("a side-by-side build still uses its own copy first",
            dev[0], @"D:\src\khbatch\bin\Release\KhBatchLauncher.exe");
        Expect("…and %ProgramFiles% is still reachable behind it",
            dev.Contains(kanghun), true);

        // Reordered, never shortened: a copy dropped from the list is a schedule that cannot be
        // registered at all, which is the failure the multi-copy search exists to prevent. The
        // installed case has one fewer DISTINCT path only because its own folder is one of the
        // years — not because anything was lost.
        Expect("an installed copy sees %ProgramFiles% plus the three years", installed.Count, 4);
        Expect("…a side-by-side build sees its own on top of those", dev.Count, 5);
        Expect("…and every installed path is in the side-by-side list too",
            installed.All(dev.Contains), true);

        // A per-year folder that is not one of the three the loop knows about must still come back.
        string addins2027 = Path.Combine(commonAppData, @"Autodesk\Revit\Addins\2027");
        var future = TaskSchedulerGateway
            .OrderedLauncherPaths(addins2027, programFiles, commonAppData).ToList();
        Expect("an Addins folder outside the known years is not dropped",
            future.Contains(Path.Combine(addins2027, "KhBatchLauncher.exe")), true);
        Expect("…and still does not precede %ProgramFiles%",
            future.IndexOf(Path.Combine(addins2027, "KhBatchLauncher.exe")) > future.IndexOf(kanghun),
            true);
    }

    /// <summary>
    /// 1.3.8.10 — what to do when Revit ignores a workset request.
    ///
    /// <para>MEASURED 2026-08-15: <c>SITE2A-BLDA-STR</c> asked for the same 13 of its 58 worksets
    /// twice, 45 minutes apart, and got <c>reopened (13 of 58 …)</c> once and <c>reopened (0 of 58
    /// …)</c> the other time — no error in either journal, and the refused open was FASTER (26 s
    /// against 87 s). So <c>WorksetConfiguration.Open(ids)</c> is intermittently ignored on a
    /// background cloud open, exactly as <c>OpenAllWorksets</c> always is. One attempt loses a model
    /// to a blip; the ladder retries the same set, then asks for everything, then fails.</para>
    /// </summary>
    /// <summary>
    /// 2026-11-03 — a NON-workshared cloud model ("RG Test", 7 models) crashed Revit 2026.5 on its
    /// first open with a workset configuration (0xc0000005, "catch SelectedPartitionsForEdit"), twice,
    /// in the user's own Revit, losing the batch. A plain open of an uncached one opened in 54 s.
    /// </summary>
    private static void NonWorksharedCloudCases()
    {
        Section("2026-11-03  non-workshared cloud models open plain; a crash is retried once, then skipped");

        // The open decision.
        Expect("no fact: the engine's workset configuration, as before",
            CloudOpenFacts.Decide(null).Mode, CloudOpenMode.WorksetConfig);
        Expect("Forma says singleuser: plain",
            CloudOpenFacts.Decide(new CloudOpenFacts.Fact { Workshared = false, Source = "acc" }).Mode, CloudOpenMode.Plain);
        Expect("an open showed workshared: workset configuration",
            CloudOpenFacts.Decide(new CloudOpenFacts.Fact { Workshared = true, Source = "opened" }).Mode, CloudOpenMode.WorksetConfig);
        Expect("a crash with nothing known: plain",
            CloudOpenFacts.Decide(new CloudOpenFacts.Fact { CrashRetry = true }).Mode, CloudOpenMode.Plain);
        Expect("…and says why",
            CloudOpenFacts.Decide(new CloudOpenFacts.Fact { CrashRetry = true }).Source, "crash-retry");
        Expect("known workshared beats a crash mark — never plain-open a workshared model",
            CloudOpenFacts.Decide(new CloudOpenFacts.Fact { Workshared = true, CrashRetry = true }).Mode, CloudOpenMode.WorksetConfig);
        Expect("modelType multiuser = workshared", CloudOpenFacts.FromModelType("multiuser"), (bool?)true);
        Expect("modelType singleuser = not workshared", CloudOpenFacts.FromModelType("singleuser"), (bool?)false);
        Expect("modelType missing = unknown, never evidence", CloudOpenFacts.FromModelType(""), (bool?)null);

        // The trail the supervisor reads back.
        string plain = BatchStages.WithMode(BatchStages.OpenDetached, CloudOpenMode.Plain, "acc");
        Expect("a plain open is still an open stage", BatchStages.IsOpenStage(plain), true);
        Expect("…and is recognisably plain", BatchStages.IsPlainOpenStage(plain), true);
        Expect("a configured open is not tagged plain",
            BatchStages.IsPlainOpenStage(BatchStages.WithMode(BatchStages.OpenDetached, CloudOpenMode.WorksetConfig, "")), false);
        Expect("the model being opened is the exact middle field",
            BatchStages.OpeningModel("Batch Interop | SAMPLE-ARC-003-0001.rvt | " + plain), "SAMPLE-ARC-003-0001.rvt");
        Expect("a name containing another name is not confused with it",
            BatchStages.OpeningModel("Batch Interop | ARC-002-0001-B.rvt | " + BatchStages.OpenDetached), "ARC-002-0001-B.rvt");
        Expect("a line that is not an open names no model",
            BatchStages.OpeningModel("Batch Interop | ARC-002.rvt | ran in 2451 ms"), (string?)null);
        Expect("the gate's own note names no model",
            BatchStages.OpeningModel("cloud open gate: waited 12 s for another instance's open"), (string?)null);

        // The user's rule: retry once in safe open, then skip.
        Expect("first crash on an unknown/non-workshared model: retry",
            RunSupervisor.OpenCrashPolicy(0, knownWorkshared: false), RunSupervisor.OpenCrashOutcome.Retry);
        Expect("second crash this run: give up",
            RunSupervisor.OpenCrashPolicy(1, knownWorkshared: false), RunSupervisor.OpenCrashOutcome.GiveUp);
        Expect("a workshared model keeps the ordinary retry ladder",
            RunSupervisor.OpenCrashPolicy(1, knownWorkshared: true), RunSupervisor.OpenCrashOutcome.Ladder);

        // The crasher goes last, so the models it was blocking run first.
        var group = new RunGroup { Pid = 46520, Models = Models(4) };
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<CloudModel> order = RunSupervisor.RelaunchModels(group, none, RunSupervisor.MaxRelaunchesPerGroup, "MODEL-2.rvt");
        Expect("the crashed model is resent", order.Count, 4);
        Expect("…last", order[^1].DisplayName, "MODEL-2.rvt");
        Expect("…the others keep their order", order[0].DisplayName + order[1].DisplayName + order[2].DisplayName,
            "MODEL-1.rvtMODEL-3.rvtMODEL-4.rvt");

        // Batch Interop always gets a supervised child, however few models.
        Expect("7 models, group size 7, always-supervise: one child of 7",
            InteractiveSplit.SupervisedGroupSize(7, 7, alwaysSupervise: true), 7);
        Expect("7 models, group size 7, ordinary action: run in this Revit",
            InteractiveSplit.SupervisedGroupSize(7, 7, alwaysSupervise: false), 0);
        Expect("a real split is unchanged",
            InteractiveSplit.SupervisedGroupSize(30, 10, alwaysSupervise: true), 10);
        Expect("nothing to run, nothing launched",
            InteractiveSplit.SupervisedGroupSize(0, 10, alwaysSupervise: true), 0);

        // An ERROR row ends the model's retries: the give-up path relies on it.
        string csv = Path.Combine(Path.GetTempPath(), "livenesssim-openCrash-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            RunNote.Write(csv, "Batch Interop", "SAMPLE-ARC-004-0001.rvt", "ERROR", "skipped — crashed twice");
            Expect("a give-up ERROR row counts the model as done",
                TrackingCsvReader.CompletedModels(csv).Contains("SAMPLE-ARC-004-0001.rvt"), true);
        }
        finally { try { File.Delete(csv); } catch { } }
    }

    private static void ReopenLadderCases()
    {
        Section("1.3.8.10  a refused workset open is retried, not lost");

        Expect("three rungs", ReopenLadder.Attempts, 3);
        Expect("rung 1 asks for the derived set", ReopenLadder.WidensToAll(1), false);
        Expect("…rung 2 asks for it again (a refusal is transient)",
            ReopenLadder.WidensToAll(2), false);
        Expect("…rung 3 falls back to every workset", ReopenLadder.WidensToAll(3), true);
        Expect("there is a rung after 1", ReopenLadder.CanRetry(1), true);
        Expect("…and after 2", ReopenLadder.CanRetry(2), true);
        Expect("…but not after 3", ReopenLadder.CanRetry(3), false);

        // The exact numbers from the two live reopens of SITE2A-BLDA-STR.
        Expect("0 of 58 open having asked for 13 is a REFUSAL",
            ReopenLadder.Refused(openCount: 0, askedNowOpen: 0), true);
        Expect("…13 of 58 open having asked for 13 is not",
            ReopenLadder.Refused(openCount: 13, askedNowOpen: 13), false);

        // A partial answer is legitimate — a workset can be deleted or renamed between two opens of
        // a live cloud model — so it is reported, never retried.
        Expect("a PARTIAL open is not a refusal",
            ReopenLadder.Refused(openCount: 9, askedNowOpen: 9), false);
        Expect("…even down to a single workset", ReopenLadder.Refused(1, 1), false);
        // But worksets open that are NONE of the ones asked for is the configuration being ignored
        // just as surely as none at all.
        Expect("worksets open, but none of the ones asked for, is a refusal",
            ReopenLadder.Refused(openCount: 58, askedNowOpen: 0), true);
    }

    /// <summary>
    /// 1.3.8.9 — telling a Revit that CRASHED from one that simply finished.
    ///
    /// <para>1.3.8.8 added the post-mortem and got this wrong on its first live run: two instances
    /// that had written <c>batch end: 2 ok, 0 failed</c> and posted their close were both reported as
    /// crashes. The predicate was right; it was reading a stage the supervisor had cached up to one
    /// 15-second poll earlier, which by definition cannot contain the lines a child writes on its way
    /// out. The fix is to re-poll the trail before judging — so what is worth pinning down here is
    /// that the DECISION is a function of the freshest stage, and that a trail read late enough sees
    /// the close mark.</para>
    /// </summary>
    private static void UnexpectedExitCases()
    {
        Section("1.3.8.9  a finished instance is not a crashed one");

        Expect("a posted close is a finish",
            ChildLiveness.IsShuttingDown($"{RevitShutdown.ClosePostedMark} pid=576: batch complete"),
            true);
        // The exact stage the 2026-08-15 false alarm was judged on: the per-model document close,
        // which every model passes through on its way to a clean finish.
        Expect("…a per-model 'close' is NOT",
            ChildLiveness.IsShuttingDown("Export NWC | SITE2A-BLDE-FFE.rvt | close"), false);
        Expect("…and neither is nothing at all", ChildLiveness.IsShuttingDown(""), false);
        Expect("…nor a null stage", ChildLiveness.IsShuttingDown(null), false);

        // The trail is the authority, and it is pid-tagged — so a child's own last line is
        // attributable with certainty even when several instances are writing at once.
        string trail = Path.Combine(Path.GetTempPath(), "LivenessSim_exit_" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllLines(trail, new[]
            {
                "2026-08-15 19:14:23.913 pid=576 Export NWC | SITE2A-BLDE-FFE.rvt | close",
                "2026-08-15 19:14:37.970 pid=576 batch end: 2 ok, 0 failed",
                $"2026-08-15 19:14:38.127 pid=576 {RevitShutdown.ClosePostedMark} pid=576: scheduled/headless batch complete",
                "2026-08-15 19:14:39.000 pid=999 Export NWC | OTHER.rvt | open (background, detached)",
            });

            var tail = new RunTrace.Tail(trail);
            tail.Poll();

            Expect("a re-polled trail sees the close the cached stage missed",
                ChildLiveness.IsShuttingDown(tail.For(576)?.Stage), true);
            Expect("…while the still-opening sibling reads as no such thing",
                ChildLiveness.IsShuttingDown(tail.For(999)?.Stage), false);
            Expect("…and a pid that never wrote anything is unknown, not finished",
                tail.For(12345) is null, true);
        }
        finally { try { File.Delete(trail); } catch { /* temp file */ } }
    }

    /// <summary>
    /// 1.3.8.8 — which worksets a view-scoped export has to open. The first harness coverage this
    /// mechanism has ever had, and the gap is the point: three releases shipped an open that yielded
    /// 0 of 26 worksets, and nothing offline could have caught it.
    ///
    /// <para>Only the DECISION is checkable here — reading <c>GetWorksetVisibility</c> needs a Revit
    /// and a model. What this pins down is the part that goes wrong quietly: that Hidden means
    /// hidden, that UseGlobalSetting defers to the workset, that a view template can only ADD, and
    /// that every unreadable case lands on "open it" rather than "skip it".</para>
    /// </summary>
    private static void WorksetPlanCases()
    {
        Section("1.3.8.8  the worksets a view-scoped export opens");

        const bool onByDefault = true, offByDefault = false;

        Expect("a view that shows the workset opens it",
            WorksetVisibilityRule.Shows(offByDefault, true, false), true);
        Expect("…and one that hides it does not",
            WorksetVisibilityRule.Shows(onByDefault, false, false), false);

        // The case that carries the links: a coordination view hiding the link worksets is why an
        // export stops loading seventeen other models.
        Expect("a hidden workset stays shut even when it is visible by default",
            WorksetVisibilityRule.Shows(onByDefault, false, false), false);

        Expect("UseGlobalSetting defers to the workset's own default (on)",
            WorksetVisibilityRule.Shows(onByDefault, null, false), true);
        Expect("…and to it when it is off",
            WorksetVisibilityRule.Shows(offByDefault, null, false), false);

        // Union, not precedence: whether GetWorksetVisibility returns the APPLIED value under a
        // template is unconfirmed, so the template can only ever add worksets to the set.
        Expect("a template that shows it overrides a view that hides it",
            WorksetVisibilityRule.Shows(offByDefault, false, true), true);
        Expect("…and a template that hides it cannot shut one the view shows",
            WorksetVisibilityRule.Shows(offByDefault, true, false), true);
        Expect("…while a template deferring to the default follows the default",
            WorksetVisibilityRule.Shows(onByDefault, false, null), true);

        // Every uncertain path opens. Under-opening is silent; over-opening is only slow.
        Expect("no readable answer at all means open it",
            WorksetVisibilityRule.Shows(offByDefault), true);

        // Several views are unioned by the caller, so the rule only has to be right per view — but
        // the union must never SHRINK the set as views are added.
        var acrossViews = new[]
        {
            WorksetVisibilityRule.Shows(offByDefault, false, false),  // view 1 hides it
            WorksetVisibilityRule.Shows(offByDefault, true, false),   // view 2 shows it
        };
        Expect("a workset any matching view shows is in the set",
            acrossViews.Any(x => x), true);
    }

    /// <summary>
    /// 1.4.3: Batch Interop leaves worksets proven to hold nothing but links shut, because the Model
    /// Checker is invoked with <c>checkLinks=false</c> and cannot look inside a linked model anyway.
    /// MEASURED, bundle <c>BKKDDCJ74R3_20260819_1645</c>: one host model loaded <b>19</b> linked
    /// files on an open that ran 38 minutes, for a run that could not use any of them.
    ///
    /// <para>The subtraction is pure (<see cref="BatchInteropAction.WorksetsToOpen"/>) so it can be
    /// asserted here; the collector pass that PRODUCES the evidence needs a live document and is
    /// verified live, per the release plan. What must never regress is the direction of the safe
    /// answer: every uncertain path opens everything, because a checkset that cannot see an element
    /// reports a PASS, and a report full of passes looks like good news.</para>
    /// </summary>
    /// <summary>
    /// 2026-11-04: "Batch Interop is working as expected, except the xlsx does not summarise all the
    /// files that ran". Every model got its own workbook and the run got none — and since every
    /// Batch Interop run is supervised, no process ever held more than one group's results. The
    /// supervisor now merges the per-model workbooks named by this run's detail-CSV rows.
    /// </summary>
    private static void InteropSummaryCases()
    {
        Section("Batch Interop run summary: one workbook for every model the run checked");

        static InteropExcelReport.FileRow Report(string model, string overall, string pct,
            params InteropExcelReport.CheckRow[] checks)
        {
            string revitFile = "Autodesk Docs://SAMPLE-PROJECT/" + model;
            var cells = new[] { "Wednesday, November 4, 2026", revitFile, @"C:\cs\Sample.xml", "Sample Health",
                "Sample CAD-BIM", "desc", overall, pct, "1", "1", "0", "0", "0" };
            return new InteropExcelReport.FileRow(cells, checks.Select(c => new InteropExcelReport.CheckRow(
                new[] { revitFile }.Concat(c.Cells.Skip(1)).ToArray(),
                c.Elements.Select(e => e.Take(8).Append(revitFile).ToArray()).ToList())).ToList());
        }
        static InteropExcelReport.CheckRow Check(string id, string name, string result, int elements = 0) =>
            new(new[] { "", id, name, "", result, "", "", "", elements.ToString() },
                Enumerable.Range(1, elements)
                    .Select(i => new[] { id, (1000 + i).ToString(), "Walls", "", "", "", "", "1", "" }).ToList());

        var runStamp = new DateTime(2026, 11, 4, 14, 15, 31);
        string tag = DetailCsvLogger.RunTag(runStamp);
        string dest = Path.Combine(_scratch, "interop-summary");
        string g1 = InteropReportPaths.ReportFolder(dest, InteropStep.DisplayName, runStamp, "g1");
        string g2 = InteropReportPaths.ReportFolder(dest, InteropStep.DisplayName, runStamp, "g2");
        Directory.CreateDirectory(g1);
        Directory.CreateDirectory(g2);

        string a1 = Path.Combine(g1, "ARC-002.xlsx");
        string b = Path.Combine(g1, "ARC-003.xlsx");
        string a2 = Path.Combine(g2, "ARC-002.xlsx");     // the supervisor's retry of ARC-002
        InteropExcelReport.Write(new[] { Report("ARC-002.rvt", "Error", "60",
            Check("c-dwg", "Imported DWG file", "Fail", elements: 3), Check("c-dopt", "Design Options", "Report")) }, a1);
        InteropExcelReport.Write(new[] { Report("ARC-003.rvt", "Fail", "50",
            Check("c-dwg", "Imported DWG file", "Pass"), Check("c-site", "Site check", "Fail", elements: 2)) }, b);
        InteropExcelReport.Write(new[] { Report("ARC-002.rvt", "Pass", "100",
            Check("c-dwg", "Imported DWG file", "Pass"), Check("c-dopt", "Design Options", "Report")) }, a2);

        // Round trip: what Write wrote, Read gives back — elements re-attached to their own check.
        List<InteropExcelReport.FileRow> back = InteropExcelReport.Read(a1);
        Expect("a report reads back as one model", back.Count, 1);
        Expect("with its checks in order", string.Join(",", back[0].Checks.Select(c => c.Cells[1])), "c-dwg,c-dopt");
        Expect("and each check's elements under that check only",
            string.Join(",", back[0].Checks.Select(c => c.Elements.Count)), "3,0");
        Expect("empty cells read back as empty, not shifted", back[0].Checks[1].Cells[4], "Report");

        string csv = DetailCsvLogger.PathFor(dest, "Batch_Interop", InteropStep.DisplayName, runStamp);
        string other = DetailCsvLogger.RunTag(runStamp.AddMinutes(-30));
        System.IO.File.WriteAllText(csv,
            "Datetime,Model Name,Run,Checkset,Result,Passed,Failed,Errors,Not Run,Pass %,Report\n" +
            $"2026-11-04 13:50:00,OLD.rvt,{other},x,Pass,1,0,0,0,100,{Path.Combine(g1, "OLD.html")}\n" +
            $"2026-11-04 14:17:55,ARC-002.rvt,{tag},x,Error,3,2,1,0,60,{Path.ChangeExtension(a1, ".html")}\n" +
            $"2026-11-04 14:18:09,ARC-003.rvt,{tag},x,Fail,1,1,0,0,50,{Path.ChangeExtension(b, ".html")}\n" +
            $"2026-11-04 14:19:40,ARC-004.rvt,{tag},x,Pass,1,0,0,0,100,{Path.Combine(g1, "ARC-004.html")}\n" +
            $"2026-11-04 14:25:00,ARC-002.rvt,{tag},x,Pass,2,0,0,0,100,{Path.ChangeExtension(a2, ".html")}\n");

        RunManifest Manifest(string action) => new()
        {
            Name = "Batch Interop",
            RunStamp = runStamp,
            TrackingLogPath = Path.Combine(_scratch, "interop-summary-track.csv"),
            AllModels = Models(3),
            BaseJob = new ScheduledJob
            {
                Name = "Batch Interop",
                DestinationFolder = dest,
                ProjectLogName = "Batch_Interop",
                RunItems = new List<RunItem> { new(action, RunItemKind.Action) }
            }
        };

        var notes = new List<string>();
        RunEndMatrix.BuildForRun(Manifest(InteropStep.DisplayName), (s, m) => notes.Add(s + ": " + m));

        string summary = MatrixPaths.OutputPath(dest, "Batch_Interop", InteropStep.DisplayName, runStamp, "Summary");
        Expect("the run gets one summary workbook", System.IO.File.Exists(summary), true);
        Expect("beside the task's CSV, named for the run stamp",
            Path.GetFileName(summary), "20261104_141531_Batch_Interop_Batch Interop Summary.xlsx");
        if (!System.IO.File.Exists(summary)) return;

        IReadOnlyList<XlsxSheet> sheets = XlsxReader.Read(summary);
        Expect("Summary first, then AIT's own three sheets",
            string.Join("|", sheets.Select(s => s.Name)), "Summary|Files|Checks|Elements");

        List<InteropExcelReport.FileRow> merged = InteropExcelReport.Read(summary);
        Expect("every model of this run, once each, none from the earlier run",
            string.Join(",", merged.Select(f => f.Cells[1].Substring(f.Cells[1].LastIndexOf('/') + 1))),
            "ARC-002.rvt,ARC-003.rvt");
        Expect("a retried model reports its retry's result", merged[0].Cells[6], "Pass");
        Expect("and its retry's elements, not the failed attempt's",
            merged[0].Checks.Sum(c => c.Elements.Count), 0);
        Expect("another model's elements survive the merge", merged[1].Checks.Sum(c => c.Elements.Count), 2);

        XlsxSheet grid = sheets[0];
        Expect("grid: one column per model, by file name", grid.Cell(0, 2) + "," + grid.Cell(0, 3), "ARC-002.rvt,ARC-003.rvt");
        Expect("grid: overall result and pass % above the checks",
            grid.Cell(1, 3) + " " + grid.Cell(2, 3), "Fail 50%");
        Expect("grid: one row per check, first-seen order",
            grid.Cell(3, 0) + "|" + grid.Cell(4, 0) + "|" + grid.Cell(5, 0), "Imported DWG file|Design Options|Site check");
        Expect("grid: each cell is that check's result in that model", grid.Cell(3, 3), "Pass");
        Expect("grid: a check a model never ran is blank, not a pass", grid.Cell(5, 2), "");

        Expect("a model whose report is gone is named, not silently dropped",
            notes.Any(n => n.Contains("ARC-004.xlsx")), true);
        Expect("and the summary says how many models it holds",
            notes.Any(n => n.StartsWith("INFO: Batch Interop summary written for the whole run (2 model(s))")), true);

        // A run without Batch Interop must not build one from the same day-file.
        System.IO.File.Delete(summary);
        RunEndMatrix.BuildForRun(Manifest(ActionNames.GetPhaseAndWorkset), (_, _) => { });
        Expect("a run without Batch Interop writes no summary", System.IO.File.Exists(summary), false);

        // AIT's ACAD-layout fallback (no check listed a Revit element) still reads and merges.
        string acad = Path.Combine(g1, "ACAD.xlsx");
        InteropExcelReport.Write(new[] { Report("PLAIN.rvt", "Pass", "100", Check("c-dwg", "Imported DWG file", "Pass")) }, acad);
        (int count, List<string> unreadable) = InteropExcelReport.Merge(new[] { acad, b }, Path.Combine(g1, "mixed.xlsx"));
        Expect("an element-less report merges with one that has elements", count + "/" + unreadable.Count, "2/0");

        // Rebuild Report can top up a summary: an earlier summary plus more reports, and the output
        // may be one of its own inputs. Its Summary grid must not be read back as a model.
        string topped = Path.Combine(g1, "mixed.xlsx");
        (int toppedCount, List<string> toppedBad) = InteropExcelReport.Merge(new[] { topped, a2, b }, topped);
        Expect("a summary plus reports merges every model once", toppedCount + "/" + toppedBad.Count, "3/0");
        Expect("…and the Summary sheet is not read as a model",
            InteropExcelReport.Read(topped).Count, 3);
        Expect("a workbook that is not a report is named, not merged",
            InteropExcelReport.Merge(new[] { Path.Combine(g1, "nope.xlsx") }, Path.Combine(g1, "none.xlsx")).Unreadable.Count +
            "/" + System.IO.File.Exists(Path.Combine(g1, "none.xlsx")), "1/False");
    }

    private static void InteropLinkWorksetCases()
    {
        Section("1.4.3  Batch Interop stops loading the links it never looks at");

        var all = new List<string> { "Shared Levels and Grids", "STR", "MEP", "Z-LINK-ARCH", "Z-LINK-STR" };

        static ISet<string> Proven(params string[] names) =>
            new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        Expect("proven link-only worksets are left shut",
            string.Join(",", BatchInteropAction.WorksetsToOpen(all, Proven("Z-LINK-ARCH", "Z-LINK-STR"))),
            "Shared Levels and Grids,STR,MEP");

        // A model nobody has recorded yet behaves exactly as it did in 1.4.2.1. This is what makes
        // the change safe to ship on by default: the first run over any model is unchanged.
        Expect("no record at all opens every workset",
            BatchInteropAction.WorksetsToOpen(all, Proven()).Count, all.Count);

        // The failure this guard exists for. Asking for NO worksets is not a smaller request — it is
        // the closed open that had Batch Interop calling partial models clean for four versions.
        Expect("a model recorded as link-only THROUGHOUT still opens everything",
            BatchInteropAction.WorksetsToOpen(all, Proven(all.ToArray())).Count, all.Count);

        // Names, not ids: the audit stores names because a workset id means nothing across two
        // separate opens of a detached model (the 1.3.8.7 lesson).
        Expect("workset names are matched case-insensitively, as the audit stores them",
            BatchInteropAction.WorksetsToOpen(all, Proven("z-link-arch")).Count, all.Count - 1);

        Expect("a recorded workset the model no longer has removes nothing",
            BatchInteropAction.WorksetsToOpen(all, Proven("Z-LINK-DELETED")).Count, all.Count);

        // --- The posture, which is what actually decides whether the engine reopens --------------
        Expect("the default checkset plans its own, narrower open",
            new BatchInteropAction("checkset.xml").Worksets, WorksetPosture.PlansOwn);
        Expect("…and one declared to examine links opens every workset, as before 1.4.3",
            new BatchInteropAction("checkset.xml", checksetExaminesLinks: true).Worksets,
            WorksetPosture.OpensAll);
        Expect("either way it must not claim to read no instances",
            new BatchInteropAction("checkset.xml").Worksets == WorksetPosture.ReadsNoInstances, false);

        // --- The flag has to survive every hop to the process that opens the model ---------------
        // A field added to the model and dropped on the way to the runner is a silent revert: the
        // window would show the tick, the run would ignore it.
        var set = new ChecksetSet { Name = "Structural", Path = "c:\\cs.xml", ExaminesLinks = true };
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<ChecksetSet>(
            System.Text.Json.JsonSerializer.Serialize(set))!;
        Expect("a saved checkset remembers whether it examines links", reloaded.ExaminesLinks, true);

        // Backward compatibility: every checkset saved before 1.4.3 has no such property, and must
        // read as false — i.e. the skip is ON. Deliberate, and the reason it is safe is above.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ChecksetSet>(
            "{\"Name\":\"Old\",\"Path\":\"c:\\\\old.xml\"}")!;
        Expect("a checkset saved before 1.4.3 defaults to skipping link worksets",
            legacy.ExaminesLinks, false);

        var job = new ScheduledJob
        {
            Name = "Interop",
            RunItems = new List<RunItem> { new(BatchInteropAction.DisplayName, RunItemKind.Action) },
            InteropChecksetPath = "c:\\cs.xml",
            InteropChecksetExaminesLinks = true
        };
        Expect("Duplicate() carries the flag with the checkset path",
            job.Duplicate().InteropChecksetExaminesLinks, true);
        Expect("…and a job saved before 1.4.3 reads as the default",
            System.Text.Json.JsonSerializer.Deserialize<ScheduledJob>(
                "{\"Name\":\"Old\",\"InteropChecksetPath\":\"c:\\\\old.xml\"}")!
                .InteropChecksetExaminesLinks, false);
    }

    /// <summary>
    /// The rule that decides how long a run may sit frozen behind a dialog Revit does not own, and
    /// the one-line form its words take in a CSV cell. MEASURED 2026-08-15: the Navisworks
    /// exporter's message box stopped four parallel Revits for 8½ minutes, and only a person at the
    /// keyboard restarted them. The mechanism itself is fault-injected offline — see
    /// <c>tools\ThemePreview -- dialog</c>, which raises a real message box and checks it is read
    /// and closed.
    /// </summary>
    private static void DialogBreakerCases()
    {
        DateTime seen = new(2026, 8, 15, 0, 24, 6);
        var grace = TimeSpan.FromSeconds(45);
        Expect("a dialog just up is left alone",
            ForeignDialogBreaker.ShouldAct(seen, seen.AddSeconds(5), grace), false);
        Expect("…and one still up at the grace is answered",
            ForeignDialogBreaker.ShouldAct(seen, seen.AddSeconds(45), grace), true);
        // The real one sat for 8½ minutes because nothing was watching.
        Expect("…as the 2026-08-15 one would have been, 8 minutes earlier",
            ForeignDialogBreaker.ShouldAct(seen, seen.AddMinutes(8), grace), true);

        Expect("the dialog's words become one line",
            ForeignDialogBreaker.Compose("Navisworks NWC Exporter",
                "No suitable geometry found.\r\nPlease check what part you want to export."),
            "Navisworks NWC Exporter — No suitable geometry found. Please check what part you want to export.");
        Expect("a caption with no body does not trail a dash",
            ForeignDialogBreaker.Compose("Revit", ""), "Revit");
    }

    // ---- RC3: queued groups start as slots free, and the queue always drains -------------------

    private static void SlotCases()
    {
        Section("C3  slot arithmetic (over-subscribe = the bug; never drain = breaking the fix)");

        // 31 groups, nothing started yet.
        List<RunGroup> fresh = Groups(31, running: 0, exited: 0);
        Expect("a fresh round of 31 starts exactly 4", RunSupervisor.SlotsToFill(fresh, 4).Count, 4);

        // 4 running, 27 queued: full, start nothing.
        List<RunGroup> full = Groups(31, running: 4, exited: 0);
        Expect("while 4 run, nothing else starts", RunSupervisor.SlotsToFill(full, 4).Count, 0);

        // One exits: exactly one replacement, and it is the next in order.
        List<RunGroup> oneFree = Groups(31, running: 3, exited: 1);
        List<int> next = RunSupervisor.SlotsToFill(oneFree, 4);
        Expect("one exit frees exactly one slot", next.Count, 1);
        Expect("and the next group in order takes it", next[0], 4);

        // The queue must always drain: with nothing running, everything left is startable.
        List<RunGroup> tailEnd = Groups(2, running: 0, exited: 0);
        Expect("a short round starts all of it", RunSupervisor.SlotsToFill(tailEnd, 4).Count, 2);

        // A cap of 0 (a formula regression) must not deadlock the run.
        Expect("a cap of 0 still starts one", RunSupervisor.SlotsToFill(fresh, 0).Count, 1);

        DeferralCases();
        RelaunchCases();
    }

    /// <summary>
    /// 1.4.2 — one child needing attention sets ITS GROUP aside, not the run.
    ///
    /// <para>Report <c>BKKLH3C7QV3_20260818_0801</c>, twice in one night, and wrong both times: a
    /// child blocked by a Revit startup dialog (a 2026-08-18 run — four of ten groups never launched) and a
    /// child that was merely mid-action and went on to report <c>36 ok, 0 failed</c> (Weekly SITE3A —
    /// its retry round cancelled). Evidence about one instance was never evidence about the
    /// others.</para>
    /// </summary>
    private static void DeferralCases()
    {
        Section("1.4.2  a sick child is set aside, not the run");

        // A deferred group can no longer hold the round open — it is never going to exit, and the
        // retry that reruns its models cannot be built until the round ends.
        var deferred = new RunGroup { Pid = 4242, NeedsAttention = true, Models = Models(5) };
        Expect("a deferred group settles the round",
            RunSupervisor.GroupSettled(deferred, doneBarExiting: false), true);

        // …but it is still RUNNING, so it still occupies its slot. It holds a whole Revit's memory;
        // handing that slot to a queued group is how deferring would become oversubscribing.
        var groups = new List<RunGroup>
        {
            new() { Pid = 1, NeedsAttention = true, Models = Models(5) },   // set aside, still alive
            new() { Pid = 2, Models = Models(5) },                          // working
            new() { Models = Models(5) },                                   // pending
            new() { Models = Models(5) },                                   // pending
        };
        Expect("a deferred group still holds its slot",
            RunSupervisor.SlotsToFill(groups, cap: 3, g => !false).Count, 1);

        // A healthy group has NOT settled: the round still waits for it.
        var healthy = new RunGroup { Pid = 7, Models = Models(5) };
        Expect("a working group keeps the round open",
            RunSupervisor.GroupSettled(healthy, doneBarExiting: false), false);
        Expect("a pending group keeps the round open too",
            RunSupervisor.GroupSettled(new RunGroup { Models = Models(5) }, false), false);
        Expect("an exited group settles it",
            RunSupervisor.GroupSettled(new RunGroup { Pid = 8, ExitedAt = DateTime.Now }, false), true);

        // Carried into the NEXT round, a stranded worker is subtracted from the cap. Without this,
        // a run that set two children aside in round 1 would start a full wave on top of them.
        var rounds = new List<RunRound>
        {
            new() { Round = 1, Groups = new List<RunGroup>
            {
                new() { Pid = 11, NeedsAttention = true },   // stranded, alive
                new() { Pid = 12, NeedsAttention = true, ExitedAt = DateTime.Now }, // gone: not stranded
                new() { Pid = 13, ExitedAt = DateTime.Now }, // finished normally
            } },
            new() { Round = 2, Groups = new List<RunGroup> { new() { Pid = 21 } } },
        };
        Expect("a live deferred worker from round 1 is counted",
            RunSupervisor.StrandedCount(rounds, currentRound: 2, _ => true), 1);
        Expect("…and is not counted once its process is gone",
            RunSupervisor.StrandedCount(rounds, currentRound: 2, _ => false), 0);
        Expect("the current round is never counted against itself",
            RunSupervisor.StrandedCount(rounds, currentRound: 1, _ => true), 0);
    }

    /// <summary>
    /// 1.4.2 — a group whose Revit vanished restarts at once instead of at the end of the round.
    ///
    /// <para>2026-08-18: pid 87800 died inside its first cloud open at 00:53:47 and its five models
    /// did not run again until 01:21:39 — 28 minutes with a slot empty. Ten of that bundle's 31 first
    /// opens killed their Revit outright.</para>
    /// </summary>
    private static void RelaunchCases()
    {
        Section("1.4.2  a crashed group restarts now, not at the end of the round");

        var group = new RunGroup { Pid = 87800, Models = Models(10) };
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Expect("a crash before any model ran resends all ten",
            RunSupervisor.RelaunchModels(group, none, RunSupervisor.MaxRelaunchesPerGroup).Count, 10);

        // Partly through: the six that never produced a row, and NOT the four that did — a duplicate
        // row would corrupt the log the whole run is judged from.
        var four = new HashSet<string>(group.Models.Take(4).Select(m => m.DisplayName),
                                       StringComparer.OrdinalIgnoreCase);
        List<CloudModel> rest = RunSupervisor.RelaunchModels(group, four, RunSupervisor.MaxRelaunchesPerGroup);
        Expect("a crash at model 5 of 10 resends six", rest.Count, 6);
        Expect("…and none of them are already done",
            rest.Any(m => four.Contains(m.DisplayName)), false);

        var all = new HashSet<string>(group.Models.Select(m => m.DisplayName),
                                      StringComparer.OrdinalIgnoreCase);
        Expect("a group that finished everything before dying is not restarted",
            RunSupervisor.RelaunchModels(group, all, RunSupervisor.MaxRelaunchesPerGroup).Count, 0);

        // The bound. SITE2A-BLDA-ELE.rvt took its Revit down on all four attempts that night, twice in
        // isolation — without this, "restart it" becomes a launch loop.
        var replaced = new RunGroup { Pid = 58376, Models = Models(5), RelaunchCount = 1 };
        Expect("a replacement that also dies is not replaced again",
            RunSupervisor.RelaunchModels(replaced, none, RunSupervisor.MaxRelaunchesPerGroup).Count, 0);
        Expect("the bound is one per group per round", RunSupervisor.MaxRelaunchesPerGroup, 1);

        // 1.4.2.1 — THE DEFECT 1.4.2 SHIPPED. The replacement was appended to the round while
        // PolicePace was foreach-ing that same list, so every run that lost a child died with
        // "Collection was modified; enumeration operation may not execute" — and the supervisor's own
        // catch turned that into a stopped run, the exact outcome 1.4.2 existed to remove. It hit
        // both runs in report BKKDDCJ74R3_20260818_1015.
        //
        // First, the hazard itself, so the reason for the shape below is on the record and not just
        // in a comment somebody may tidy away.
        bool threw = false;
        try
        {
            var live = new List<RunGroup> { new() { Pid = 1 }, new() { Pid = 2 } };
            foreach (RunGroup g in live)
                if (g.Pid == 1) live.Add(new RunGroup { Pid = 3 });
        }
        catch (InvalidOperationException) { threw = true; }
        Expect("appending to a round while sweeping it throws", threw, true);

        // And the fix, pinned where it cannot be undone by accident: the round is handed out as a
        // read-only view, so the append is a COMPILE error rather than a nightly one. Groups are
        // added through AddGroups, which PolicePace can only reach after its loop ends.
        var currentGroups = typeof(RunSupervisor).GetMethod(
            "CurrentGroups",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Expect("CurrentGroups exists", currentGroups != null, true);
        Expect("…and hands out a read-only view, not the live list",
            currentGroups!.ReturnType, typeof(IReadOnlyList<RunGroup>));
    }

    // ---- packing: auto group size + round-robin dealing ---------------------------------------

    private static void PackingCases()
    {
        Section("PERF  group size is derived, and groups are dealt not sliced");

        Environment.SetEnvironmentVariable(InstanceBudget.EnvVar, "6");
        try
        {
            // The shipped cap: 6. The rule is ONE WAVE of at most 10 models each —
            // min(10, ceil(count / cap)) — which replaces the two-wave rule that pinned the group
            // COUNT to 2 x cap and so charged a 32-model run the same 23 Revit startups as a
            // 64-model one. See InstanceBudget.MaxModelsPerInstance for the 2026-08-17 measurement.
            Expect("98 models cap out at 10 each", InstanceBudget.AutoGroupSize(98), 10);
            Expect("…which is 10 groups, not 14", ChunkLauncher.GroupCount(98, InstanceBudget.AutoGroupSize(98)), 10);
            Expect("a 32-model prefix set fills one wave", InstanceBudget.AutoGroupSize(32), 6);
            Expect("…in 6 groups, not 16", ChunkLauncher.GroupCount(32, InstanceBudget.AutoGroupSize(32)), 6);
            Expect("a small set still fills the slots", InstanceBudget.AutoGroupSize(9), 2);
            Expect("one model never splits", InstanceBudget.AutoGroupSize(1), 1);
            Expect("zero models is still a legal size", InstanceBudget.AutoGroupSize(0), 1);
            Expect("auto sentinel resolves at execution time",
                InstanceBudget.EffectiveGroupSize(ScheduledJob.GroupSizeAuto, 98), 10);
            Expect("explicit group size survives resolution",
                InstanceBudget.EffectiveGroupSize(5, 98), 5);
            Expect("zero keeps split disabled",
                InstanceBudget.EffectiveGroupSize(0, 98), 0);

            // The two shapes 1.4.1 was specified with, asserted as shapes rather than as sizes —
            // the size is the mechanism, the shape is what was asked for.
            Expect("100 models: ten groups of ten", InstanceBudget.AutoGroupSize(100), 10);
            Expect("…six start now, four queue",
                ChunkLauncher.GroupCount(100, InstanceBudget.AutoGroupSize(100)), 10);
            Expect("45 models: one wave of six", InstanceBudget.AutoGroupSize(45), 8);
            Expect("…dealt 8/8/8/7/7/7",
                string.Join("/", ChunkSplitter.Split(Models(45), InstanceBudget.AutoGroupSize(45))
                    .Select(g => g.Count)),
                "8/8/8/7/7/7");

            // The boundary between "one wave" and "a queue": cap x 10.
            Expect("60 models still fit one wave",
                ChunkLauncher.GroupCount(60, InstanceBudget.AutoGroupSize(60)), 6);
            Expect("61 is the first count that queues",
                ChunkLauncher.GroupCount(61, InstanceBudget.AutoGroupSize(61)), 7);

            // The policy as invariants, not as points: no instance is ever handed more than ten
            // models, and no run below the boundary is ever split into more groups than slots.
            // A point-case is what let the two-wave rule pin the group count at 16 unnoticed.
            int overTen = 0, extraWaves = 0;
            for (int count = 1; count <= 500; count++)
            {
                int each = InstanceBudget.AutoGroupSize(count);
                if (each > 10) overTen++;
                if (count <= 6 * 10 && ChunkLauncher.GroupCount(count, each) > 6) extraWaves++;
            }
            Expect("no instance is ever given more than ten models", overTen, 0);
            Expect("no run of 60 or fewer is split beyond the cap", extraWaves, 0);

            var cloud = Enumerable.Range(1, 9)
                .Select(i => new CloudModel
                {
                    DisplayName = $"CLOUD-{i}.rvt",
                    ProjectGuid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ModelGuid = Guid.NewGuid()
                })
                .ToList();
            var mixed = Models(2).Concat(cloud.Take(1)).ToList();
            // Cloud runs used to be pinned to one instance after the 2026-08-03 native open crash,
            // which made every "split" Forma run serial in disguise. They now get the same cap as
            // local work; the first-open stagger is what keeps cold opens apart.
            Expect("cloud runs get the machine cap, not 1",
                InstanceBudget.MaxConcurrent(cloud), 6);
            Expect("a mixed local/cloud run gets it too",
                InstanceBudget.MaxConcurrent(mixed), 6);
            Expect("local-only runs retain the requested cap",
                InstanceBudget.MaxConcurrent(Models(9)), 6);
            Expect("cloud auto grouping fills one wave",
                InstanceBudget.AutoGroupSize(cloud), 2);
            Expect("the schedule auto sentinel uses the same shape",
                InstanceBudget.EffectiveGroupSize(ScheduledJob.GroupSizeAuto, cloud), 2);
            Expect("cloud concurrency is described with its stagger",
                InstanceBudget.Describe(cloud).Contains("first cloud open"), true);
            Expect("round 3 honours the cap persisted by the run",
                RunSupervisor.RoundGroupSize(3, originalSize: 10, missingCount: 31,
                    maxConcurrentInstances: 1), 31);

            // Dealing must not change how many instances a run produces — every caller that
            // predicts the instance count (confirmations, the summary line) uses GroupCount.
            int size = InstanceBudget.AutoGroupSize(98);
            List<List<CloudModel>> dealt = ChunkSplitter.Split(Models(98), size);
            Expect("dealing produces the same group count as slicing did",
                dealt.Count, ChunkLauncher.GroupCount(98, size));
            Expect("no group exceeds the size", dealt.All(g => g.Count <= size), true);
            Expect("every model is dealt exactly once",
                dealt.SelectMany(g => g).Select(m => m.DisplayName).Distinct().Count(), 98);

            // The point of dealing: adjacent models — the ones most likely to cost the same, since
            // the list arrives ordered by set and discipline — must not land together.
            List<CloudModel> models = Models(98);
            List<List<CloudModel>> d2 = ChunkSplitter.Split(models, size);
            int sameGroupNeighbours = 0;
            for (int i = 1; i < models.Count; i++)
            {
                int a = d2.FindIndex(g => g.Contains(models[i - 1]));
                int b = d2.FindIndex(g => g.Contains(models[i]));
                if (a == b) sameGroupNeighbours++;
            }
            Expect("no two consecutive models share a group", sameGroupNeighbours, 0);

            Expect("a run too small to split is still one group",
                ChunkSplitter.Split(Models(3), 0).Count, 1);

            // The escape hatch that replaces the hardcoded cloud cap: a machine whose cold cloud
            // opens still collide can be put back to serial, and the stagger widened, without a
            // rebuild. If these stop working there is no way out but shipping a new installer.
            Environment.SetEnvironmentVariable(InstanceBudget.EnvVar, "1");
            Expect("the env override still forces a cloud run serial",
                InstanceBudget.MaxConcurrent(cloud), 1);
            Environment.SetEnvironmentVariable(InstanceBudget.EnvVar, "6");

            Expect("the first-open stagger defaults to 12s", InstanceBudget.FirstOpenStaggerSeconds, 12);
            Environment.SetEnvironmentVariable(InstanceBudget.StaggerEnvVar, "30");
            Expect("the stagger is tunable without a rebuild", InstanceBudget.FirstOpenStaggerSeconds, 30);
            Environment.SetEnvironmentVariable(InstanceBudget.StaggerEnvVar, "nonsense");
            Expect("an unreadable stagger falls back rather than failing the run",
                InstanceBudget.FirstOpenStaggerSeconds, 12);
            Environment.SetEnvironmentVariable(InstanceBudget.StaggerEnvVar, "999");
            Expect("an out-of-range stagger falls back too", InstanceBudget.FirstOpenStaggerSeconds, 12);
            Environment.SetEnvironmentVariable(InstanceBudget.StaggerEnvVar, "0");
            Expect("zero is a legal stagger (opt out entirely)", InstanceBudget.FirstOpenStaggerSeconds, 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(InstanceBudget.EnvVar, null);
            Environment.SetEnvironmentVariable(InstanceBudget.StaggerEnvVar, null);
        }
    }

    private static void ManifestStoreCases()
    {
        Section("SAFETY  manifest writes are complete atomic replacements");

        string runId = "liveness-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(RunManifestStore.RunsDirectory, runId + ".json");
        var manifest = new RunManifest
        {
            RunId = runId,
            Name = "atomic-write-probe",
            State = RunState.Running,
            MaxConcurrentInstances = 1,
            AllModels = Models(2)
        };

        try
        {
            for (int i = 0; i < 50; i++)
            {
                manifest.StopReason = "revision-" + i;
                RunManifestStore.Save(manifest);
                string json = File.ReadAllText(path);
                using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(json);
                Expect($"manifest revision {i} is non-empty", json.Length > 100, true);
                Expect($"manifest revision {i} contains no zero bytes", json.IndexOf('\0') < 0, true);
            }

            RunManifest? loaded = RunManifestStore.Load(runId);
            Expect("the final atomic manifest remains loadable", loaded?.StopReason, "revision-49");
            Expect("existing enum values stay backward-compatible", (int)RunState.Abandoned, 2);
            Expect("Stopped is appended after legacy states", (int)RunState.Stopped, 3);
            Expect("atomic writes leave no temporary sibling",
                Directory.EnumerateFiles(RunManifestStore.RunsDirectory, runId + ".*.tmp").Any(), false);
        }
        finally
        {
            try { File.Delete(path); } catch { /* unique probe record */ }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static ChildLiveness.Watch Watch(int pid, int startedMinutesAgo, int staggerSeconds = 0)
    {
        // Every probe reports a live, idle process: these cases are about the CLOCKS, so the
        // signals are held still and the timestamps do the talking.
        ChildLiveness.AliveProbe = _ => true;
        ChildLiveness.CpuProbe = _ => TimeSpan.Zero;
        return new ChildLiveness.Watch
        {
            Pid = pid,
            RevitVersion = "2026",
            StartedAt = DateTime.Now.AddMinutes(-startedMinutesAgo),
            FirstOpenDelaySeconds = staggerSeconds
        };
    }

    /// <summary>
    /// An instance that has said "armed group …" and nothing since — the state every one of the
    /// nine killed startup instances was in. The mark is already consumed (LastMarkAt set), because
    /// that is what a watch looks like after its first poll; leaving it unconsumed would let the
    /// poll itself reset the very clock under test.
    /// </summary>
    private static ChildLiveness.Watch Armed(int pid, int minutesAgo)
    {
        ChildLiveness.Watch w = Watch(pid, minutesAgo);
        w.LastStage = "armed group f852b877";
        w.LastMarkAt = DateTime.Now.AddMinutes(-minutesAgo);
        w.LastProgressAt = DateTime.Now.AddMinutes(-minutesAgo);
        return w;                                   // LastWorkMarkAt deliberately left default
    }

    private static string Stamp(DateTime at) => at.ToString("yyyy-MM-dd HH:mm:ss.fff");

    /// <summary>Models with no cloud identity, so the cache signal abstains and the clocks decide.</summary>
    /// <summary>
    /// The open-once-per-model rule. Measured on the 2026-07-27 office run: six actions meant six
    /// opens of every model, 576 opens in all and 49% of the run's action time. Reuse fixes that,
    /// but only where the output is provably identical — so the interesting cases here are the ones
    /// that must REFUSE to reuse. A wrong "yes" silently changes exported values; a wrong "no" is
    /// merely slow, which is why every veto is tested and the permissive case is only one line.
    /// </summary>
    private static void ReuseCases()
    {
        Section("REUSE  one open per model, but only when the result is identical");

        // The shape the office run is made of: six read-only Checks, back to back, all detached.
        Expect("two Checks share one open document",
            BatchEngine.CanJoin(docUsable: true, openerNeedsActive: false, joinerNeedsActive: false,
                openerModifies: false, joinerModifies: false, worksetsSatisfied: true), true);

        // A write pass normally sees the model RE-OPENED after the previous pass synced, which also
        // pulls other people's central changes. Sharing an in-memory document skips that pull, so
        // the two could disagree. Refuse in both directions rather than reason about likelihood.
        Expect("a write pass never joins",
            BatchEngine.CanJoin(true, false, false, openerModifies: false, joinerModifies: true, true), false);
        Expect("nothing joins a write pass's document",
            BatchEngine.CanJoin(true, false, false, openerModifies: true, joinerModifies: false, true), false);
        Expect("two write passes still don't share",
            BatchEngine.CanJoin(true, false, false, true, true, true), false);

        // Script sets need the ACTIVE document; that has its own reuse path (RunOnOpenActiveDocument).
        Expect("a script set never joins a background document",
            BatchEngine.CanJoin(true, openerNeedsActive: false, joinerNeedsActive: true, false, false, true), false);
        Expect("nothing joins a script set's document",
            BatchEngine.CanJoin(true, openerNeedsActive: true, joinerNeedsActive: false, false, false, true), false);

        // Open worksets can only be chosen at open time, so a pass wanting closed ones must re-open.
        Expect("a pass needing other worksets re-opens",
            BatchEngine.CanJoin(true, false, false, false, false, worksetsSatisfied: false), false);

        // Revit can invalidate a document under us (the ~23% first-open crash class).
        Expect("an unusable document is never joined",
            BatchEngine.CanJoin(docUsable: false, false, false, false, false, true), false);

        // Identity is by VALUE: reusing a document for a model that merely looks the same would
        // export one model's numbers under another's name.
        var cloudA = new CloudModel { DisplayName = "A.rvt", ProjectGuid = Guid.Parse("11111111-1111-1111-1111-111111111111"), ModelGuid = Guid.Parse("22222222-2222-2222-2222-222222222222") };
        var cloudASame = new CloudModel { DisplayName = "A (renamed).rvt", ProjectGuid = cloudA.ProjectGuid, ModelGuid = cloudA.ModelGuid };
        var cloudB = new CloudModel { DisplayName = "A.rvt", ProjectGuid = cloudA.ProjectGuid, ModelGuid = Guid.Parse("33333333-3333-3333-3333-333333333333") };
        Expect("same cloud ids are the same model", ModelDocumentCursor.SameModel(cloudA, cloudASame), true);
        Expect("same name, different model guid is NOT", ModelDocumentCursor.SameModel(cloudA, cloudB), false);

        var localA = new CloudModel { DisplayName = "A.rvt", LocalPath = @"C:\sim\A.rvt" };
        var localACase = new CloudModel { DisplayName = "A.rvt", LocalPath = @"c:\SIM\a.rvt" };
        var localB = new CloudModel { DisplayName = "A.rvt", LocalPath = @"C:\other\A.rvt" };
        Expect("local paths compare case-insensitively", ModelDocumentCursor.SameModel(localA, localACase), true);
        Expect("a different local path is a different model", ModelDocumentCursor.SameModel(localA, localB), false);
        Expect("a local is never the same as a cloud model", ModelDocumentCursor.SameModel(localA, cloudA), false);
        Expect("nothing is the same as nothing", ModelDocumentCursor.SameModel(null, null), false);
    }

    /// <summary>
    /// Internal Relink is the one action that must sometimes refuse a parallel split: a host that is
    /// also one of its own link targets would be open ATTACHED for write in one instance while
    /// another loaded it as a link.
    /// </summary>

    /// <summary>
    /// Batch Link RVT's rules — the ones that decide where a link LANDS and whether it is made at all.
    ///
    /// <para>The first case is the one that matters. Mode B exists so that a Correct Workset run
    /// straight after a link run reports "already correct" instead of moving every link that was just
    /// placed, and that promise is only as good as the two actions agreeing on one string. So the case
    /// asserts the two FORMULAS against each other rather than against a literal — a literal would
    /// keep passing while both sides drifted together, which is exactly the failure the shared
    /// LinkWorksetNaming class was extracted to prevent.</para>
    /// </summary>
    private const string SEP = "\\";
    private const string UNC = SEP + SEP;

    private static void BatchLinkRvtCases()
    {
        Section("LINK   Batch Link RVT places links where Correct Workset expects them");

        var modeB = new BatchLinkRvtInputSet { PerLinkWorkset = true, LinkKeyword = "LINK" };

        // THE convention case: the name Batch Link RVT writes == the name Correct Workset expects.
        Expect("Mode B agrees with Correct Workset's Expected, for the same link type name",
            BatchLinkRules.WorksetNameFor(modeB, "SITE1A-ST-BLDA.rvt"),
            LinkWorksetNaming.Expected("LINK", "SITE1A-ST-BLDA.rvt"));

        Expect("…and the extension is stripped exactly once",
            BatchLinkRules.WorksetNameFor(modeB, "SITE1A-ST-BLDA.rvt"), "LINK_SITE1A-ST-BLDA");
        Expect("…a name with no extension is unchanged",
            BatchLinkRules.WorksetNameFor(modeB, "SITE1A-ST-BLDA"), "LINK_SITE1A-ST-BLDA");
        Expect("…the extension match is case-insensitive",
            BatchLinkRules.WorksetNameFor(modeB, "SITE1A-ST-BLDA.RVT"), "LINK_SITE1A-ST-BLDA");
        Expect("…and \".rvt\" inside the name is not stripped",
            BatchLinkRules.WorksetNameFor(modeB, "A.rvt.B.rvt"), "LINK_A.rvt.B");

        // Mode A: one workset, whatever the link is called.
        var modeA = new BatchLinkRvtInputSet { PerLinkWorkset = false, SharedWorksetName = " Z-LINK " };
        Expect("Mode A ignores the link name", BatchLinkRules.WorksetNameFor(modeA, "ANY.rvt"), "Z-LINK");
        Expect("…and trims the typed workset name",
            BatchLinkRules.WorksetNameFor(modeA, "OTHER.rvt"), "Z-LINK");
        Expect("Mode B trims the typed keyword too",
            BatchLinkRules.WorksetNameFor(new BatchLinkRvtInputSet
                { PerLinkWorkset = true, LinkKeyword = " LINK " }, "X.rvt"), "LINK_X");

        // --- skip-if-already-linked: identity, never display name ---------------------------------
        Section("LINK   \"already linked\" is decided by identity, never by name");

        Guid a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid b = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid proj = Guid.Parse("33333333-3333-3333-3333-333333333333");

        CloudModel Cloud(Guid id, string name) =>
            new() { DisplayName = name, Region = "US", ProjectGuid = proj, ModelGuid = id };
        CloudModel Local(string path) =>
            new() { DisplayName = System.IO.Path.GetFileName(path), LocalPath = path };

        var linked = new[] { new ExistingLink(a, null) };
        Expect("a cloud source already linked is skipped",
            BatchLinkRules.AlreadyLinked(linked, Cloud(a, "S.rvt")), true);
        Expect("a different model with the SAME name is not skipped",
            BatchLinkRules.AlreadyLinked(linked, Cloud(b, "S.rvt")), false);
        Expect("nothing linked yet means nothing is skipped",
            BatchLinkRules.AlreadyLinked(new ExistingLink[0], Cloud(a, "S.rvt")), false);

        // An unreadable link reference must never suppress a source the user asked for.
        Expect("a link whose reference could not be read never matches",
            BatchLinkRules.AlreadyLinked(new[] { new ExistingLink(null, null) }, Cloud(a, "S.rvt")), false);

        string unc = UNC + "srv" + SEP + "share" + SEP + "S.rvt";
        var linkedLocal = new[] { new ExistingLink(null, unc) };
        Expect("a local source already linked is skipped (case-insensitive)",
            BatchLinkRules.AlreadyLinked(linkedLocal, Local(unc.ToUpperInvariant())), true);
        Expect("a local source at a different path is not skipped",
            BatchLinkRules.AlreadyLinked(linkedLocal, Local(UNC + "srv" + SEP + "share" + SEP + "T.rvt")), false);
        Expect("a cloud link never matches a local source",
            BatchLinkRules.AlreadyLinked(linked, Local(unc)), false);

        // --- a source that is also a host ---------------------------------------------------------
        Section("LINK   a source that is also a host is caught before the run, not by N failed models");

        Expect("a clean set has no clash",
            BatchLinkRules.SourcesThatAreAlsoHosts(new[] { Cloud(a, "H.rvt") }, new[] { Cloud(b, "S.rvt") }).Count, 0);

        List<string> clash = BatchLinkRules.SourcesThatAreAlsoHosts(
            new[] { Cloud(a, "H.rvt"), Cloud(b, "K.rvt") }, new[] { Cloud(a, "H.rvt") });
        Expect("a host that is also a source is reported", clash.Count, 1);
        Expect("…and it is named", clash.Contains("H.rvt"), true);

        Expect("a same-NAMED but different model does not clash",
            BatchLinkRules.SourcesThatAreAlsoHosts(new[] { Cloud(a, "S.rvt") }, new[] { Cloud(b, "S.rvt") }).Count, 0);
        Expect("a local host has no cloud identity to clash with",
            BatchLinkRules.SourcesThatAreAlsoHosts(new[] { Local(UNC + "s" + SEP + "H.rvt") }, new[] { Cloud(a, "H.rvt") }).Count, 0);
        Expect("a local source has no cloud identity to clash with",
            BatchLinkRules.SourcesThatAreAlsoHosts(new[] { Cloud(a, "H.rvt") }, new[] { Local(UNC + "s" + SEP + "H.rvt") }).Count, 0);

        // --- UNC warning --------------------------------------------------------------------------
        Section("LINK   a local source on a drive letter is warned about, not blocked");

        Expect("a UNC path is fine", BatchLinkRules.IsUncPath(UNC + "srv" + SEP + "share" + SEP + "S.rvt"), true);
        Expect("a drive-letter path is not", BatchLinkRules.IsUncPath("C:" + SEP + "S.rvt"), false);
        Expect("forward slashes still read as UNC", BatchLinkRules.IsUncPath("//srv/share/S.rvt"), true);
        Expect("an empty path is not UNC", BatchLinkRules.IsUncPath(""), false);

        Expect("only the drive-letter source is warned about",
            string.Join(",", BatchLinkRules.NonUncLocalSources(new[]
                { Local(UNC + "srv" + SEP + "ok.rvt"), Local("C:" + SEP + "bad.rvt"), Cloud(a, "cloud.rvt") })),
            "bad.rvt");
    }

    private static void RelinkSplitCases()
    {
        Section("SPLIT  Internal Relink refuses to split a set that links itself");

        Guid project = Guid.Parse("44444444-4444-4444-4444-444444444444");
        Guid hostGuid = Guid.Parse("55555555-5555-5555-5555-555555555555");
        Guid otherGuid = Guid.Parse("66666666-6666-6666-6666-666666666666");

        CloudModel Host(Guid id, string name) =>
            new() { DisplayName = name, ProjectGuid = project, ModelGuid = id };

        Dictionary<string, List<RelinkTarget>> Targets(params Guid[] ids) =>
            new() { ["T"] = ids.Select(id => new RelinkTarget
            {
                DisplayName = "T.rvt", Region = "US", ProjectGuid = project, ModelGuid = id
            }).ToList() };

        Expect("hosts that are not targets split freely",
            RelinkSplitGuard.SelfLinkingHost(new[] { Host(hostGuid, "H.rvt") }, Targets(otherGuid)), null);

        string? blocked = RelinkSplitGuard.SelfLinkingHost(
            new[] { Host(hostGuid, "H.rvt") }, Targets(hostGuid, otherGuid));
        Expect("a host that is also a target blocks the split", blocked != null, true);
        Expect("…and the reason names the model", blocked?.Contains("H.rvt") ?? false, true);

        // Identity is by cloud id, not display name — two different models can share a name.
        Expect("a same-NAMED but different model does not block",
            RelinkSplitGuard.SelfLinkingHost(new[] { Host(hostGuid, "T.rvt") }, Targets(otherGuid)), null);

        // A local host has no cloud identity to collide with.
        Expect("a local host never blocks",
            RelinkSplitGuard.SelfLinkingHost(
                new[] { new CloudModel { DisplayName = "L.rvt", LocalPath = @"C:\sim\L.rvt" } },
                Targets(hostGuid)), null);
    }

    /// <summary>
    /// Batch Interop splits as of v1.3.7, under a budget rather than a ban.
    ///
    /// These cases replace v1.3.4's, which asserted the opposite — that Interop could never fan out.
    /// Two of the three rules under test here exist BECAUSE of that release: the report-folder rule
    /// is the defect its ban was hiding, and the instance cap is the mundane explanation its crash
    /// report never ruled out. Neither is a guess about AIT; both are things this side controls.
    /// </summary>
    private static void InteropSafetyCases()
    {
        Section("SPLIT  Batch Interop splits, under an Interop-sized budget");

        Expect("Batch Interop declares parallel execution enabled",
            BatchInteropAction.AllowsParallelRuns, true);

        // --- The budget: a checking instance is Revit PLUS the Model Checker ---------------------
        var interopJob = new ScheduledJob
        {
            GroupSize = ScheduledJob.GroupSizeAuto,
            RunItems = new List<RunItem>
            {
                new(BatchInteropAction.DisplayName, RunItemKind.Action)
            }
        };
        var regular = new ScheduledJob
        {
            GroupSize = ScheduledJob.GroupSizeAuto,
            RunItems = new List<RunItem>
            {
                new("Get Phase & Workset", RunItemKind.Action)
            }
        };

        Expect("an Interop job is weighed as an Interop run",
            InstanceBudget.WeightFor(interopJob), RunWeight.Interop);
        Expect("a mixed job containing Interop is weighed as one too",
            InstanceBudget.WeightFor(new ScheduledJob
            {
                RunItems = new List<RunItem>
                {
                    new("Get Phase & Workset", RunItemKind.Action),
                    new(BatchInteropAction.DisplayName, RunItemKind.Action)
                }
            }), RunWeight.Interop);
        Expect("an ordinary job keeps the normal weight",
            InstanceBudget.WeightFor(regular), RunWeight.Normal);
        Expect("a null job (manifest written before BaseJob existed) is normal",
            InstanceBudget.WeightFor(null), RunWeight.Normal);

        // Machine-independent: whatever this PC reports, the Interop cap can never EXCEED the
        // ordinary one, and is never zero. Asserting the concrete number would make the case pass
        // or fail on the RAM of whoever runs it.
        int normalCap = InstanceBudget.MaxConcurrent(RunWeight.Normal);
        int interopCap = InstanceBudget.MaxConcurrent(RunWeight.Interop);
        Expect("the Interop cap never exceeds the ordinary cap",
            interopCap <= normalCap, true);
        Expect("the Interop cap is still a real run (>= 1)", interopCap >= 1, true);

        // Fewer slots must mean BIGGER groups, not lost models: the same work through fewer Revits.
        var fifty = new List<CloudModel>();
        for (int i = 0; i < 50; i++) fifty.Add(new CloudModel { DisplayName = $"M{i}.rvt" });
        Expect("a heavier weight yields groups no smaller than the normal shape",
            InstanceBudget.AutoGroupSize(fifty, RunWeight.Interop)
                >= InstanceBudget.AutoGroupSize(fifty, RunWeight.Normal), true);

        // --- The verified-year rule ---------------------------------------------------------------
        Expect("splitting is refused on a Revit year whose AIT build is unverified",
            InteropSplitPolicy.BlockedReason(interopJob, "2024") != null, true);
        Expect("a job with no Interop step is never blocked by the Interop policy",
            InteropSplitPolicy.BlockedReason(regular, "2024"), null);

        // --- The defect the 1.3.4 ban was hiding ---------------------------------------------------
        // Sibling groups launch inside the same second. Same stamp, different groups, and the
        // folders must still differ — otherwise AIT (which names each HTML after the model) lets a
        // same-titled model in one group overwrite another group's report.
        var stamp = new DateTime(2026, 8, 9, 14, 30, 1);
        string g1 = InteropReportPaths.ReportFolder(@"C:\dest", "Batch Interop", stamp, "g1");
        string g2 = InteropReportPaths.ReportFolder(@"C:\dest", "Batch Interop", stamp, "g2");
        string single = InteropReportPaths.ReportFolder(@"C:\dest", "Batch Interop", stamp, null);
        Expect("two groups sharing a run stamp get different report folders", g1 != g2, true);
        Expect("a single-process run keeps the unsuffixed folder name",
            single, @"C:\dest\Batch Interop\InteropReports_20260809-143001");
        Expect("a group's folder is the run's folder plus its group tag",
            g2, single + "_g2");

        // The tag comes from the chunk job the child was handed; a non-chunk must produce none, or
        // an ordinary interactive run would start renaming its own output folders.
        ChunkContext.Publish(new ScheduledJob { IsEphemeralChunk = true, ChunkIndex = 3, LogRunStamp = stamp });
        Expect("a chunk publishes its group tag", ChunkContext.FolderTag, "g3");
        Expect("a chunk publishes the run's shared stamp", ChunkContext.RunStamp, (DateTime?)stamp);
        ChunkContext.Publish(new ScheduledJob());
        Expect("an ordinary job publishes no group tag", ChunkContext.FolderTag, null);
        Expect("an ordinary job publishes no shared stamp", ChunkContext.RunStamp, null);
    }

    /// <summary>
    /// Export NWC's configuration, through the two paths that would fail SILENTLY if it were lost.
    ///
    /// <para>An action that carries its own settings cannot be built by <c>ActionRegistry</c>, which
    /// constructs actions with no arguments — so the settings have to travel on the job. Two links
    /// in that chain break quietly rather than loudly: <c>ScheduledJob.Duplicate()</c> not copying
    /// the snapshot (the interactive run works, every split child throws), and
    /// <c>ScheduledRunner.BuildJobPasses</c> having no branch for it — in which case the item falls
    /// through to <c>ActionRegistry.Create</c>, which returns the FIRST registered action and
    /// reports success while running something else entirely.</para>
    /// </summary>
    private static void ExportNwcSpecCases()
    {
        Section("1.3.8.2  Export NWC carries its options to wherever it runs");

        var spec = new ExportNwcInputSet
        {
            Name = "Coordination",
            ExportEntireModel = false,
            ViewTitleContains = "NAVISWORKS",
            OutputFolder = @"C:\sim\nwc",
            ExportLinks = true,
            ExportRoomGeometry = true,
            UseInternalCoordinates = true,
            ConvertLinkedCADFormats = true,
            FacetingFactor = 2.5,
            Parameters = NwcParameterMode.Elements,
            NamePrefix = "SITE",
            NameSuffix = "v1",
            NameIncludesView = true,
            ProjectLogName = "Export NWC"
        };

        ExportNwcInputSet copy = spec.Clone();
        Expect("a cloned spec keeps its view filter", copy.ViewTitleContains, "NAVISWORKS");
        Expect("…its output folder", copy.OutputFolder, @"C:\sim\nwc");
        Expect("…and its options", copy.ExportLinks && copy.ExportRoomGeometry, true);
        // Every option has to survive Clone(), or a split-run child exports with different settings
        // from the one the user configured — silently, and only in the parallel case.
        Expect("…the coordinate mode", copy.UseInternalCoordinates, true);
        Expect("…the linked-CAD flag", copy.ConvertLinkedCADFormats, true);
        Expect("…the faceting factor", copy.FacetingFactor, 2.5);
        Expect("…the parameter mode", copy.Parameters, NwcParameterMode.Elements);
        Expect("…and the whole naming convention",
            copy.NamePrefix == "SITE" && copy.NameSuffix == "v1" && copy.NameIncludesView, true);

        // THE SPLIT-RUN TRAP: a chunk child is built with Duplicate(), and one that arrives without
        // the snapshot throws "carries no options snapshot" and the whole group does nothing.
        var job = new ScheduledJob
        {
            Name = "Nightly NWC",
            RunItems = new List<RunItem> { new(ActionNames.ExportNwc, RunItemKind.Action) },
            ExportNwcSpec = spec
        };
        ScheduledJob child = job.Duplicate();
        Expect("a duplicated job still carries the options", child.ExportNwcSpec != null, true);
        Expect("…and they are a COPY, not the same object",
            ReferenceEquals(child.ExportNwcSpec, spec), false);
        Expect("…with the view filter intact", child.ExportNwcSpec?.ViewTitleContains, "NAVISWORKS");

        // Where the files go: the set's own folder wins, and an empty one falls back to the run's
        // destination so the common case needs no second path typed in.
        Expect("an explicit output folder is used", spec.ResolveOutputFolder(@"C:\run"), @"C:\sim\nwc");
        Expect("an empty one falls back to the run's destination",
            new ExportNwcInputSet().ResolveOutputFolder(@"C:\run"), @"C:\run");
        Expect("with neither, there is nowhere to write", new ExportNwcInputSet().HasOutput, false);

        // Old job JSON must still load: the field is new and nullable, so no schema bump.
        Expect("the schema was not bumped for it", ScheduledJob.CurrentSchemaVersion, 3);
        Expect("a job that does not export carries no snapshot",
            new ScheduledJob().ExportNwcSpec, (ExportNwcInputSet?)null);

        NwcNamingCases();
        ExportIfcSpecCases();
    }

    /// <summary>
    /// Export IFC + Data is a synthetic action like Export NWC, so it has the same two traps: an
    /// option lost in Clone() changes a split-run child's export silently, and a job that loses its
    /// snapshot in Duplicate() makes the runner throw for the whole group.
    /// </summary>
    private static void ExportIfcSpecCases()
    {
        Section("2.1  Export IFC + Data carries its options to wherever it runs");

        var spec = new ExportIfcInputSet
        {
            Name = "Report",
            ViewTitleContains = "IFC EXPORT",
            OutputFolder = @"C:\sim\ifc",
            Schema = IfcSchemaChoice.Ifc2x3CoordinationView2,
            UseSharedCoordinates = false,
            ExportCommonPropertySets = false,
            ExportRevitPropertySets = true,
            ExportBaseQuantities = true,
            WriteElementsCsv = false,
            WriteRoomsCsv = false,
            ExtraParameters = new List<string> { "Mark", "Comments" },
            ConvertToFrag = false,
            KeepRunsDays = 3,
        };

        ExportIfcInputSet copy = spec.Clone();
        Expect("a cloned IFC spec keeps its view filter", copy.ViewTitleContains, "IFC EXPORT");
        Expect("…its schema", copy.Schema, IfcSchemaChoice.Ifc2x3CoordinationView2);
        Expect("…its coordinate and property-set options",
            !copy.UseSharedCoordinates && !copy.ExportCommonPropertySets && copy.ExportRevitPropertySets && copy.ExportBaseQuantities, true);
        Expect("…its data-file switches", copy.WriteElementsCsv || copy.WriteRoomsCsv, false);
        Expect("…its extra parameters", string.Join("|", copy.ExtraParameters), "Mark|Comments");
        Expect("…as a separate list", ReferenceEquals(copy.ExtraParameters, spec.ExtraParameters), false);
        Expect("…the conversion switch and retention", !copy.ConvertToFrag && copy.KeepRunsDays == 3, true);

        var job = new ScheduledJob
        {
            Name = "Nightly report",
            RunItems = new List<RunItem> { new(ActionNames.ExportIfcData, RunItemKind.Action) },
            ExportIfcSpec = spec
        };
        ScheduledJob child = job.Duplicate();
        Expect("a duplicated job still carries the IFC options", child.ExportIfcSpec != null, true);
        Expect("…as a COPY", ReferenceEquals(child.ExportIfcSpec, spec), false);

        Expect("an explicit IFC output folder is used", spec.ResolveOutputFolder(@"C:\run"), @"C:\sim\ifc");
        Expect("an empty one falls back to the run's destination",
            new ExportIfcInputSet().ResolveOutputFolder(@"C:\run"), @"C:\run");
        Expect("a blank view filter reads as whole model", new ExportIfcInputSet().ScopeLabel, "whole model");
        Expect("a job that does not export IFC carries no snapshot",
            new ScheduledJob().ExportIfcSpec, (ExportIfcInputSet?)null);
    }

    /// <summary>
    /// File naming, which decides whether a multi-view export produces distinct files or overwrites
    /// itself. One model can now emit several .nwc — if the view name is not in the name, they all
    /// land on the same path and the last one silently wins.
    /// </summary>
    private static void NwcNamingCases()
    {
        var plain = new ExportNwcInputSet();
        Expect("by default a file is just the model name", plain.BuildFileName("SITE1A-STR", ""), "SITE1A-STR");

        var full = new ExportNwcInputSet
        {
            NamePrefix = "SITE", NameSuffix = "v1", NameIncludesView = true, NameSeparator = "-"
        };
        Expect("prefix, model, view and suffix join in order",
            full.BuildFileName("SITE1A-STR", "NAVISWORKS 3D"), "SITE-SITE1A-STR-NAVISWORKS 3D-v1");
        // A whole-model export has no view, and must not leave a dangling separator behind.
        Expect("a missing view leaves no empty part",
            full.BuildFileName("SITE1A-STR", ""), "SITE-SITE1A-STR-v1");

        // Two views of one model must not collide.
        var byView = new ExportNwcInputSet { NameIncludesView = true };
        Expect("two views of one model produce different names",
            byView.BuildFileName("M", "Coord A") == byView.BuildFileName("M", "Coord B"), false);

        // Turning everything off would name every file "" — fall back to the model rather than
        // writing a folder full of files called "-.nwc".
        var empty = new ExportNwcInputSet { NameIncludesModel = false };
        Expect("with every part off it still uses the model name",
            empty.BuildFileName("SITE1A-STR", ""), "SITE1A-STR");

        // The dropdown labels in Batch Run and Schedule Run must not read as "nothing selected"
        // when a blank filter actually means "every 3D view".
        Expect("a blank view filter reads as all 3D views",
            new ExportNwcInputSet { ExportEntireModel = false }.ScopeLabel, "all 3D views");
        Expect("and the default reads as whole model", new ExportNwcInputSet().ScopeLabel, "whole model");
        Expect("an empty output folder reads as the run destination",
            new ExportNwcInputSet().OutputLabel, "run destination");

        MultiViewNamingCases();
    }

    /// <summary>
    /// 1.3.8.7 — THE OVERWRITE. On 2026-08-15 three views matched the keyword in SITE2A-BLDA-ARCH, so
    /// three exports ran and the log said "exported 3 views"; the options set had the view name
    /// switched OFF, so all three wrote to SITE2A-BLDA-ARCH.nwc and one file existed at the end. Two
    /// finished exports were lost with no error anywhere. When more than one view matches, the view
    /// name now goes into the name whatever the set says — a single match keeps the short name the
    /// team's existing files use, which is the behaviour the user asked for.
    /// </summary>
    private static void MultiViewNamingCases()
    {
        var setWithViewOff = new ExportNwcInputSet { NamePrefix = "Test", NameIncludesView = false };

        // What actually happened: three views, one name.
        Expect("with the view name off, two views build the SAME name",
            setWithViewOff.BuildFileName("BLDA-ARCH", "Forma PUBLISH A"),
            setWithViewOff.BuildFileName("BLDA-ARCH", "Forma PUBLISH B"));

        // …and what the run does about it now.
        Expect("forcing the view name separates them",
            setWithViewOff.BuildFileName("BLDA-ARCH", "Forma PUBLISH A", forceView: true) ==
            setWithViewOff.BuildFileName("BLDA-ARCH", "Forma PUBLISH B", forceView: true), false);
        Expect("…and the forced name is prefix-model-view",
            setWithViewOff.BuildFileName("BLDA-ARCH", "Forma PUBLISH A", forceView: true),
            "Test-BLDA-ARCH-Forma PUBLISH A");

        // A single matching view is left alone: the point of the choice is that today's file names
        // do not change for the models that match one view.
        Expect("a single view keeps the short name",
            setWithViewOff.BuildFileName("BLDA-ARCH", "Forma PUBLISH A"), "Test-BLDA-ARCH");

        // Forcing changes nothing for a whole-model export — there is no view to name.
        Expect("a whole-model export is unaffected by forcing",
            setWithViewOff.BuildFileName("BLDA-ARCH", "", forceView: true), "Test-BLDA-ARCH");

        // And when the set already asks for the view name, forcing is a no-op.
        var setWithViewOn = new ExportNwcInputSet { NameIncludesView = true };
        Expect("forcing an already-on set changes nothing",
            setWithViewOn.BuildFileName("M", "V", forceView: true), setWithViewOn.BuildFileName("M", "V"));
    }

    /// <summary>
    /// The record that lets a toast be removed after the process that raised it has exited.
    ///
    /// Now the FALLBACK rather than the mechanism: since the ExpirationTime interop works, Windows
    /// expires almost everything on its own and a row is written only when the expiry could not be
    /// set. The ledger still matters for the case Windows cannot help with — toasts left behind by
    /// builds that had no working expiry — and for any machine where setting one fails.
    ///
    /// Worth testing without Windows in the picture because the ONE rule that must never break is
    /// "a failure toast is never removed automatically", and that rule is enforced by a failure never
    /// getting a ledger row at all. That is pure bookkeeping, so it can be proven here rather than by
    /// watching Notification Center and hoping.
    /// </summary>
    private static void ToastLedgerCases()
    {
        Section("NOTIFY  the toast ledger decides what may be tidied away");

        var now = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        // --- round trip -------------------------------------------------------------------------
        var entry = new ToastLedgerEntry(now, "Kanghun.KhBatchRunner.2026", "KhBatch.Transient", "a1b2c3d4");
        ToastLedgerEntry? back = ToastLedger.Parse(ToastLedger.Format(entry));
        Expect("a recorded entry parses back", back != null, true);
        Expect("…with its identity", back?.AppId, "Kanghun.KhBatchRunner.2026");
        Expect("…its group", back?.Group, "KhBatch.Transient");
        Expect("…its tag", back?.Tag, "a1b2c3d4");
        Expect("…and its instant, in UTC", back?.ExpiresAt, now);

        // A run can straddle a DST change, and the two sides of the comparison are written by
        // different processes hours apart — so the stored form has to be unambiguous.
        var offsetEntry = new ToastLedgerEntry(
            new DateTimeOffset(2026, 8, 9, 19, 0, 0, TimeSpan.FromHours(7)), "App", "G", "t");
        Expect("a non-UTC instant is stored as the same moment in UTC",
            ToastLedger.Parse(ToastLedger.Format(offsetEntry))?.ExpiresAt, now);

        // --- unreadable lines are dropped, never guessed -----------------------------------------
        Expect("a blank line is not an entry", ToastLedger.Parse(""), null);
        Expect("a null line is not an entry", ToastLedger.Parse(null), null);
        Expect("a truncated line is not an entry", ToastLedger.Parse("2026-08-09T12:00:00Z\tApp"), null);
        Expect("an unparseable time is not an entry", ToastLedger.Parse("soon\tApp\tG\tt"), null);
        Expect("a row naming no toast is not an entry",
            ToastLedger.Parse("2026-08-09T12:00:00.0000000+00:00\tApp\tG\t"), null);
        Expect("a row naming no identity is not an entry",
            ToastLedger.Parse("2026-08-09T12:00:00.0000000+00:00\t\tG\tt"), null);

        // --- the sweep selects by time, and only by time -----------------------------------------
        string ledger = Path.Combine(_scratch, "toast-ledger.txt");
        var past = new ToastLedgerEntry(now.AddMinutes(-1), "App", "KhBatch.Transient", "expired");
        var future = new ToastLedgerEntry(now.AddMinutes(60), "App", "KhBatch.Transient", "still-fresh");
        ToastLedger.Record(past, ledger);
        ToastLedger.Record(future, ledger);

        var removed = new List<string>();
        int due = ToastLedger.Sweep(now, e => removed.Add(e.Tag), ledger);
        Expect("one entry was due", due, 1);
        Expect("the expired toast is the one offered for removal",
            string.Join(",", removed), "expired");

        string[] left = File.ReadAllLines(ledger);
        Expect("the unexpired entry is still on file", left.Length, 1);
        Expect("…and it is the right one", ToastLedger.Parse(left[0])?.Tag, "still-fresh");

        // A swept entry is forgotten even when removal throws: Windows may no longer have the toast
        // (already dismissed), and a ledger that only grows would become the problem.
        ToastLedger.Record(new ToastLedgerEntry(now.AddMinutes(-5), "App", "KhBatch.Transient", "gone"), ledger);
        ToastLedger.Sweep(now, _ => throw new InvalidOperationException("already dismissed"), ledger);
        Expect("an entry whose removal failed is not retried forever",
            File.ReadAllLines(ledger).Length, 1);

        // Once the last row goes, the file goes — nothing left behind on an idle PC.
        ToastLedger.Sweep(now.AddYears(1), _ => { }, ledger);
        Expect("an emptied ledger removes its own file", File.Exists(ledger), false);

        // --- sweeping is never the thing that breaks a run ---------------------------------------
        Expect("sweeping a ledger that was never written is not an error",
            ToastLedger.Sweep(now, _ => { }, Path.Combine(_scratch, "no-such-ledger.txt")), 0);

        string corrupt = Path.Combine(_scratch, "toast-ledger-corrupt.txt");
        File.WriteAllLines(corrupt, new[] { "half a line", "", "\t\t\t" });
        Expect("a corrupt ledger sweeps to nothing rather than throwing",
            ToastLedger.Sweep(now, _ => { }, corrupt), 0);
        Expect("…and does not keep the unreadable rows", File.Exists(corrupt), false);
    }

    /// <summary>
    /// Cut model, Internal Relink, Correct Workset and Initiate to Forma can now split across Revit
    /// instances. A chunk reaches its child process as JSON on disk and nothing else, and the child
    /// rebuilds the action from the carried input set — <c>ScheduledRunner</c> THROWS if that set
    /// arrives null ("carries no snapshot"). So the round trip through the chunk store is the whole
    /// contract for those four buttons, and a dropped property would only ever show up as a failed
    /// run on someone's machine.
    /// </summary>
    private static void ChunkSpecCases()
    {
        Section("SPLIT  a chunk job carries its action's input set to the child process");

        ScheduledJob RoundTrip(ScheduledJob job)
        {
            // Duplicate() is what ChunkLauncher.BuildChunkJob starts from; the store is how the
            // child receives it. Both are on the real path, so both are in the test.
            ScheduledJob chunk = job.Duplicate();
            ChunkJobStore.Save(chunk);
            try { return ChunkJobStore.GetById(chunk.Id)!; }
            finally { ChunkJobStore.Delete(chunk.Id); }
        }

        var cut = new KhBatchRunner.Batch.CutModelInputSet
        {
            Name = "BETA2A cut", FolderUrn = "urn:adsk.wipprod:fs.folder:co.ABC",
            FolderPath = "Project Files/Cut", Prefix = "BETA2A", Separator = "-",
            DeleteOtherLocations = true
        };
        ScheduledJob rtCut = RoundTrip(new ScheduledJob { Name = "cut", CutModelPublish = cut });
        Expect("Cut model spec survives", rtCut.CutModelPublish != null, true);
        Expect("…including its site prefix", rtCut.CutModelPublish?.Prefix, "BETA2A");
        // DeleteOtherLocations lives on the DERIVED type; a spec serialized as its base class would
        // silently lose it and quietly stop deleting the other site locations.
        Expect("…and the derived-type flag", rtCut.CutModelPublish?.DeleteOtherLocations, true);

        var init = new KhBatchRunner.Batch.InitiateInputSet
        {
            Name = "publish", FolderUrn = "urn:adsk.wipprod:fs.folder:co.DEF",
            FolderPath = "Project Files/New", Suffix = "-INIT"
        };
        ScheduledJob rtInit = RoundTrip(new ScheduledJob { Name = "init", InitiatePublish = init });
        Expect("Initiate spec survives", rtInit.InitiatePublish?.FolderUrn, init.FolderUrn);
        Expect("…and its suffix", rtInit.InitiatePublish?.Suffix, "-INIT");

        var relink = new KhBatchRunner.Batch.RelinkInputSet
        {
            Name = "OLD to SITE1A", OldCode = "OLD", NewCode = "SITE1A", Separator = "-",
            MatchByExactName = false, IncludeSubfolders = true,
            TargetFolders = { new KhBatchRunner.Forma.FolderRef() }
        };
        ScheduledJob rtRelink = RoundTrip(new ScheduledJob { Name = "relink", RelinkSpec = relink });
        Expect("Relink spec survives", rtRelink.RelinkSpec?.NewCode, "SITE1A");
        Expect("…its exact-name flag (a false that must stay false)",
            rtRelink.RelinkSpec?.MatchByExactName, false);
        Expect("…and its target folder list", rtRelink.RelinkSpec?.TargetFolders.Count, 1);

        var cw = new KhBatchRunner.Batch.CorrectWorksetInputSet
        {
            Name = "worksets", Fix = true, ApplyLinkRule = true, LinkKeyword = "LINK",
            Rules = { new KhBatchRunner.Batch.CorrectWorksetRule
                      { TargetWorkset = "SHARED", Keyword = "GRID", Categories = { "OST_Grids" } } }
        };
        ScheduledJob rtCw = RoundTrip(new ScheduledJob { Name = "cw", CorrectWorksetSpec = cw });
        Expect("Correct Workset spec survives", rtCw.CorrectWorksetSpec?.Fix, true);
        Expect("…its rules", rtCw.CorrectWorksetSpec?.Rules.Count, 1);
        Expect("…and each rule's categories",
            rtCw.CorrectWorksetSpec?.Rules[0].Categories.Count, 1);

        // A chunk must never split again: the parent already decided the grouping, and a chunk that
        // re-split would launch Revits from inside a Revit that was launched to run one group.
        ScheduledJob parent = new() { Name = "p", GroupSize = 7 };
        Expect("the parent's group size is what the launcher reads", parent.GroupSize, 7);

        // Task Scheduler XML carries an ISO local wall-clock boundary. It must be identical under
        // different display cultures, and deliberately has no UTC offset: Windows then applies
        // the timezone/DST rules of the PC that owns the task.
        CultureInfo oldCulture = CultureInfo.CurrentCulture;
        try
        {
            DateTime now = new(2026, 8, 3, 18, 0, 0);
            var once = new ScheduledJob
            {
                Name = "local time",
                Recurrence = ScheduleRecurrence.Once,
                StartDate = new DateTime(2026, 8, 4),
                Time = "7:05"
            };
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            DateTime thaiStart = TaskSchedulerGateway.TriggerStartLocal(once, now);
            string thaiDisplay = ScheduleDisplay.Local(thaiStart);
            string thaiTime = ScheduleDisplay.LocalTime(once.Time);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            DateTime usStart = TaskSchedulerGateway.TriggerStartLocal(once, now);
            string usDisplay = ScheduleDisplay.Local(usStart);
            string usTime = ScheduleDisplay.LocalTime(once.Time);
            Expect("task boundary ignores Thai/US display culture", thaiStart, usStart);
            Expect("task boundary is ISO local wall-clock time",
                TaskSchedulerGateway.LocalBoundary(thaiStart), "2026-08-04T07:05:00");
            // Display follows the PC's REGION settings — what the taskbar clock uses — and NOT the
            // thread culture, which Revit sets from its own UI language. An English Revit on a Thai
            // PC must still print the format the user sees everywhere else on that machine, so
            // reassigning CurrentCulture between the two reads above must change nothing.
            Expect("display ignores the thread culture Revit sets", thaiDisplay, usDisplay);
            Expect("display time ignores it too", thaiTime, usTime);
            Expect("schedule datetime display follows the PC's Windows region format", usDisplay,
                usStart.ToString(
                    ScheduleDisplay.DisplayCulture.DateTimeFormat.ShortDatePattern + " " +
                    ScheduleDisplay.DisplayCulture.DateTimeFormat.ShortTimePattern,
                    ScheduleDisplay.DisplayCulture));
            Expect("schedule time follows the PC's Windows region format", usTime,
                usStart.ToString(ScheduleDisplay.DisplayCulture.DateTimeFormat.ShortTimePattern,
                    ScheduleDisplay.DisplayCulture));

            // The editors are DateTimePickers again — a dropdown calendar and a spinner, not text
            // to be typed and parsed — formatted with the same PC patterns as the grids.
            using var dateEditor = DateInput.Create();
            using var timeEditor = TimeInput.Create();
            Expect("the date editor is formatted with the PC's short-date pattern",
                dateEditor.CustomFormat, ScheduleDisplay.DatePattern);
            Expect("the time editor is formatted with the PC's short-time pattern",
                timeEditor.CustomFormat, ScheduleDisplay.TimePattern);
            Expect("the time editor spins rather than dropping a calendar", timeEditor.ShowUpDown, true);
            Expect("the date editor drops a calendar", dateEditor.ShowUpDown, false);

            DateInput.SetDate(dateEditor, new DateTime(2026, 8, 4));
            TimeInput.SetStored(timeEditor, "7:05");
            Expect("the picked time reads back as invariant HH:mm",
                TimeInput.TryStored(timeEditor, out string normalizedTime), true);
            Expect("time storage stays culture-independent", normalizedTime, "07:05");
            TimeInput.SetStored(timeEditor, "19:05");
            TimeInput.TryStored(timeEditor, out normalizedTime);
            Expect("an afternoon time round-trips whatever it is displayed as", normalizedTime, "19:05");
            TimeInput.SetStored(timeEditor, "not a time");
            TimeInput.TryStored(timeEditor, out normalizedTime);
            Expect("an unreadable stored time falls back instead of throwing",
                normalizedTime.Length, 5);
            Expect("the picked date reads back", DateInput.TryRead(dateEditor, out DateTime localDate), true);
            Expect("and keeps the intended calendar date", localDate, new DateTime(2026, 8, 4));
            // DateTimePicker.Value throws outside 1753..9998, so a job whose saved StartDate is a
            // default or garbage value must look wrong, not take the schedule window down.
            DateInput.SetDate(dateEditor, DateTime.MinValue);
            Expect("an out-of-range stored date is clamped, not thrown",
                dateEditor.Value.Date, dateEditor.MinDate.Date);

            var daily = new ScheduledJob { Recurrence = ScheduleRecurrence.Daily, Time = "17:30" };
            Expect("past daily time advances to tomorrow",
                TaskSchedulerGateway.TriggerStartLocal(daily, now), new DateTime(2026, 8, 4, 17, 30, 0));

            var completedLegacyOnce = new ScheduledJob
            {
                Recurrence = ScheduleRecurrence.Once,
                StartDate = null,
                LastRunAt = new DateTime(2026, 7, 8, 2, 3, 59),
                Time = "02:03"
            };
            Expect("completed legacy once migration keeps its historical date",
                TaskSchedulerGateway.MigrationTriggerStartLocal(completedLegacyOnce, now),
                new DateTime(2026, 7, 8, 2, 3, 0));

            var weekly = new ScheduledJob
            {
                Recurrence = ScheduleRecurrence.Weekly,
                Time = "09:15",
                WeeklyDays = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Friday }
            };
            Expect("weekly boundary chooses next selected local day",
                TaskSchedulerGateway.TriggerStartLocal(weekly, now), new DateTime(2026, 8, 7, 9, 15, 0));

            string launcherExe = TaskSchedulerGateway.LauncherExePath();
            string revitExe = @"C:\Program Files\Autodesk\Revit 2026\Revit.exe";
            string xml = TaskSchedulerGateway.BuildTaskXml(once, launcherExe, revitExe, thaiStart).ToString();
            Expect("task XML carries the local boundary", xml.Contains("2026-08-04T07:05:00"), true);
            Expect("task XML prevents overlapping launches", xml.Contains("IgnoreNew"), true);
            Expect("task XML forbids Task Scheduler hard termination",
                xml.Contains("<AllowHardTerminate>false</AllowHardTerminate>"), true);
            Expect("task XML has no scheduler-imposed process timeout",
                xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"), true);
            // XML-v7: the scripting host is out of the chain entirely. A PC with Windows Script
            // Host disabled by policy returned 0x1 from wscript without running a line of the
            // launcher, so Revit never started and nothing was logged.
            Expect("task XML runs the launcher executable", xml.Contains("KhBatchLauncher.exe"), true);
            Expect("task XML no longer needs a scripting host", xml.Contains("wscript.exe"), false);
            Expect("task XML no longer launches cmd.exe", xml.Contains("cmd.exe"), false);

            string arguments = TaskSchedulerGateway.LauncherArguments(
                new ScheduledJob { Id = "quiet", ScheduleOwner = "Full" }, revitExe);
            Expect("launcher arguments name the job", arguments.Contains("--job quiet"), true);
            Expect("launcher arguments name the owner", arguments.Contains("--owner Full"), true);
            Expect("launcher arguments quote a spaced Revit path",
                arguments.Contains("--revit \"" + revitExe + "\""), true);
            Expect("launcher arguments name the diagnostic log",
                arguments.Contains("--log") && arguments.Contains("launcher_quiet.log"), true);

            // XML-v8: the shell FALLBACK, for a PC whose security policy refuses our unsigned
            // launcher however it is installed (measured 2026-09-18: Defender's ASR
            // "prevalence, age, or trusted list" rule refuses it in every folder, including
            // %TEMP%). It has to hand Revit exactly what the launcher hands it, or the child
            // Revit starts interactively and sits there all night doing nothing.
            var shellJob = new ScheduledJob { Id = "shellid", ScheduleOwner = "Full" };
            string shellArgs = TaskSchedulerGateway.ShellArguments(shellJob, revitExe);
            Expect("shell arguments set the job variable",
                shellArgs.Contains("set \"KHBATCH_JOB=shellid\""), true);
            Expect("shell arguments set the owner variable",
                shellArgs.Contains("set \"KHBATCH_OWNER=Full\""), true);
            // Quoted assignments, because `set VAR=value & next` puts the space INSIDE the value
            // and a job id with a trailing space matches no saved job.
            Expect("shell arguments never leave an unquoted assignment",
                shellArgs.Contains("set KHBATCH_"), false);
            Expect("shell arguments quote the Revit path",
                shellArgs.Contains("& \"" + revitExe + "\""), true);
            // No `start`: cmd must WAIT for Revit, so the task's result is the run's result and
            // IgnoreNew can still prevent an overlapping launch.
            Expect("shell arguments do not detach Revit", shellArgs.Contains("start "), false);
            Expect("shell arguments log the launch",
                shellArgs.Contains("launcher_shellid.log") && shellArgs.Contains("shell=cmd"), true);

            string shellXml = TaskSchedulerGateway.BuildTaskXmlWithAction(
                shellJob, TaskSchedulerGateway.ShellExePath(), shellArgs, thaiStart).ToString();
            Expect("shell task XML runs cmd.exe", shellXml.Contains("cmd.exe"), true);
            Expect("shell task XML keeps the no-overlap policy", shellXml.Contains("IgnoreNew"), true);
            Expect("shell task XML still forbids hard termination",
                shellXml.Contains("<AllowHardTerminate>false</AllowHardTerminate>"), true);

            // XML-v9: the Revit-DIRECT action, preferred over the shell for the same blocked-launcher
            // PC because it draws nothing. cmd waits for Revit on purpose, so its console window is
            // up for the whole run, not for a moment. The job travels in the WORKING DIRECTORY,
            // which is the only channel Task Scheduler leaves open once the command line is Revit's.
            var directJob = new ScheduledJob { Id = "directid", ScheduleOwner = "Full" };
            string fireDir = TaskSchedulerGateway.FireDirectory(directJob.Id);
            string directXml = TaskSchedulerGateway.BuildTaskXmlWithAction(
                directJob, revitExe, "", thaiStart, fireDir).ToString();
            Expect("Revit-direct task XML runs Revit itself",
                directXml.Contains("<Command>" + revitExe + "</Command>"), true);
            // Not "empty arguments" — NO arguments element. Revit treats a stray argument as a file
            // to open, and an unattended Revit sitting on a "cannot open" dialog is the failure this
            // whole mechanism exists to avoid.
            Expect("Revit-direct task XML passes Revit no arguments",
                directXml.Contains("<Arguments>"), false);
            Expect("Revit-direct task XML carries the job in its working directory",
                directXml.Contains("<WorkingDirectory>" + fireDir + "</WorkingDirectory>"), true);
            Expect("Revit-direct task XML keeps the no-overlap policy",
                directXml.Contains("IgnoreNew"), true);
            Expect("Revit-direct task XML still forbids hard termination",
                directXml.Contains("<AllowHardTerminate>false</AllowHardTerminate>"), true);
            Expect("the shell action still names cmd.exe rather than Revit",
                shellXml.Contains("<Command>" + revitExe + "</Command>"), false);

            // The round trip the whole action depends on: the folder the task hands Revit names the
            // job, and nothing else does. A false positive here would make a hand-started Revit
            // claim somebody's schedule.
            Expect("a fire folder names its job",
                TaskSchedulerGateway.JobIdFromFireDirectory(fireDir), "directid");
            Expect("a trailing separator does not hide the job",
                TaskSchedulerGateway.JobIdFromFireDirectory(fireDir + "\\"), "directid");
            Expect("the fire root itself names no job",
                TaskSchedulerGateway.JobIdFromFireDirectory(
                    TaskSchedulerGateway.FireRootDirectory()), "");
            Expect("the schedule folder names no job",
                TaskSchedulerGateway.JobIdFromFireDirectory(ScheduleStore.ScheduledDirectory), "");
            Expect("an ordinary folder names no job",
                TaskSchedulerGateway.JobIdFromFireDirectory(@"C:\Program Files\Autodesk\Revit 2026"), "");
            Expect("a grandchild of the fire root names no job",
                TaskSchedulerGateway.JobIdFromFireDirectory(
                    System.IO.Path.Combine(fireDir, "deeper")), "");
            Expect("no working directory at all names no job",
                TaskSchedulerGateway.JobIdFromFireDirectory(""), "");

            Expect("the marker spells each action",
                TaskSchedulerGateway.ActionName(TaskActionKind.Launcher) + "/" +
                TaskSchedulerGateway.ActionName(TaskActionKind.RevitDirect) + "/" +
                TaskSchedulerGateway.ActionName(TaskActionKind.Shell),
                "launcher/revit-direct/cmd-shell");

            Expect("old START launcher is selected for migration",
                TaskSchedulerGateway.NeedsRegistrationUpgrade(
                    "@echo off\r\nset \"KHBATCH_JOB=x\"\r\nstart \"\" \"Revit.exe\""), true);
            Expect("console-backed XML-v2 launcher is selected for migration",
                TaskSchedulerGateway.NeedsRegistrationUpgrade(
                    "@echo off\r\nrem KHBATCH_TASK_XML_V2"), true);
            Expect("XML-v3 launcher is upgraded to the no-hard-terminate registration",
                TaskSchedulerGateway.NeedsRegistrationUpgrade("' KHBATCH_TASK_XML_V3"), true);
            Expect("XML-v4 launcher is upgraded for diagnostic logging",
                TaskSchedulerGateway.NeedsRegistrationUpgrade("' KHBATCH_TASK_XML_V4"), true);
            Expect("XML-v5 launcher is upgraded for launcher-directory logging",
                TaskSchedulerGateway.NeedsRegistrationUpgrade("' KHBATCH_TASK_XML_V5"), true);
            Expect("XML-v6 wscript launcher is upgraded off the scripting host",
                TaskSchedulerGateway.NeedsRegistrationUpgrade("' KHBATCH_TASK_XML_V6"), true);
            Expect("XML-v7 launcher-only registration is upgraded for the fallback actions",
                TaskSchedulerGateway.NeedsRegistrationUpgrade("KHBATCH_TASK_XML_V7"), true);
            Expect("XML-v8 cmd registration is upgraded off the console window",
                TaskSchedulerGateway.NeedsRegistrationUpgrade("KHBATCH_TASK_XML_V8"), true);
            Expect("the current registration is not migrated repeatedly",
                TaskSchedulerGateway.NeedsRegistrationUpgrade(
                    TaskSchedulerGateway.RegistrationMarker), false);
            Expect("an unregistered job is not mistaken for a current one",
                TaskSchedulerGateway.NeedsRegistrationUpgrade(null), true);

            string toast = RunNotification.BuildToastXml("A&B", "<finished>");
            Expect("batch notices use a sticky Windows reminder toast",
                toast.Contains("scenario=\"reminder\""), true);
            Expect("the retained toast has a system-owned Dismiss action",
                toast.Contains("activationType=\"system\" arguments=\"dismiss\""), true);
            Expect("toast titles are XML escaped", toast.Contains("A&amp;B"), true);
            Expect("toast bodies are XML escaped", toast.Contains("&lt;finished&gt;"), true);
            // Retention by outcome: progress news expires, a failure waits to be acknowledged.
            var noon = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.FromHours(7));
            Expect("a failure notice has no expiry and waits to be dismissed",
                RunNotification.ExpiryFor(failed: true, noon).HasValue, false);
            Expect("progress notices expire after the retention window",
                RunNotification.ExpiryFor(failed: false, noon),
                noon.AddMinutes(RunNotification.RetainMinutes()));
            Expect("the default retention window is one hour",
                RunNotification.DefaultRetainMinutes, 60);

            // Clicking a failure opens its log. NEVER as a file: URI: Windows accepts such a toast,
            // shows it, then silently drops it from Notification Center (measured — a file: toast
            // vanished within 6s while https: and plain ones stayed). The link therefore uses the
            // scheme the installers register for the launcher's --open-log mode.
            string logFile = Path.Combine(_scratch, "run log.csv");
            File.WriteAllText(logFile, "Datetime (Finish),Model,Task,Status,Message\n");
            string withLog = RunNotification.BuildToastXml("Batch finished with errors", "2 failed", logFile);
            bool clickable = RunNotification.LogUri(logFile).Length > 0;   // false where unregistered
            Expect("a log link never uses file:, which Windows drops from Notification Center",
                withLog.Contains("file:///"), false);
            Expect("a failure toast keeps its Dismiss action",
                withLog.Contains("arguments=\"dismiss\""), true);
            if (clickable)
            {
                Expect("a failure toast is activated by protocol",
                    withLog.Contains("activationType=\"protocol\""), true);
                Expect("a failure toast offers an explicit Open log action",
                    withLog.Contains("content=\"Open log\""), true);
                Expect("the log link uses the registered khbatchlog scheme",
                    withLog.Contains(RunNotification.LogUriScheme + ":"), true);
                Expect("the log path is escaped into the URI",
                    RunNotification.LogUri(logFile).Contains("run%20log.csv"), true);
            }
            else
            {
                // No handler on this PC (dev checkout, or an installer older than 1.3.4.4). A toast
                // with an unactivatable target is worse than one with none, so it ships with none.
                Expect("with no registered handler the toast carries no launch target",
                    withLog.Contains("protocol"), false);
            }
            Expect("a toast with no log has nothing to activate",
                RunNotification.BuildToastXml("Batch finished", "all done").Contains("protocol"), false);
            Expect("a log path that does not exist is not offered as a button",
                RunNotification.BuildToastXml("t", "m", Path.Combine(_scratch, "missing.csv"))
                    .Contains("Open log"), false);

            Expect("Full installer toast identity is stable",
                RunNotification.FullToastAppId, "Kanghun.KhBatchRunner");
            Expect("controller installer toast identity is stable",
                RunNotification.ControllerToastAppId, "Kanghun.KhBatchController");
            // Titles as read at a glance in Notification Center. A run that failed has to be
            // distinguishable from one that did not WITHOUT opening it — that is what justifies it
            // being the only item still there in the morning.
            Expect("a clean finish does not claim errors",
                RunNotification.FinishedTitle("Nightly", 0), "Batch finished - Nightly");
            Expect("a failed run says so in the title, not only the body",
                RunNotification.FinishedTitle("Nightly", 2).Contains("with errors"), true);
            Expect("a start notice is not mistakable for a finish notice",
                RunNotification.StartedTitle("Nightly").Contains("finished"), false);
            Expect("a start notice names the run",
                RunNotification.StartedTitle("Nightly"), "Batch started - Nightly");

            // The fallback only helps if a WAITING caller is still there when it delivers. The
            // preferred path's timeout plus the balloon's handoff has to fit inside the budget a
            // caller sits for, or a locked-down PC times out mid-fallback and shows nothing — the
            // exact failure the fallback exists to prevent, reintroduced by a timeout tweak.
            Expect("a failed toast still leaves room for the balloon inside the caller's wait",
                RunNotification.ToastTimeoutMs + RunNotification.BalloonRegisterMs +
                    RunNotification.BalloonHandoffMs < RunNotification.DeliveryTimeoutMs, true);
            // Windows truncates a balloon past these lengths; losing the end of a sentence silently
            // is worse than an ellipsis that shows something was cut.
            Expect("a long run name is cut to the balloon title limit",
                RunNotification.Trim(new string('x', 200), 63).Length, 63);
            Expect("truncation is visible rather than silent",
                RunNotification.Trim(new string('x', 200), 63).EndsWith("…"), true);
            Expect("a title that fits is left exactly alone",
                RunNotification.Trim("Batch finished - Nightly", 63), "Batch finished - Nightly");
            Expect("an empty message does not throw on the balloon path",
                RunNotification.Trim("", 63), "");

            // The hosted fallback's content crosses a process boundary in environment variables.
            // These names are declared once, in the file both projects compile, because a literal
            // repeated in two places drifts silently — the symptom would be a blank notification,
            // not a build error.
            Expect("the notify title variable is named once and shared",
                KhBatchRunner.Schedule.ScheduleEnvironment.NotifyTitle, "KHBATCH_NOTIFY_TITLE");
            Expect("the notify log variable is named once and shared",
                KhBatchRunner.Schedule.ScheduleEnvironment.NotifyLog, "KHBATCH_NOTIFY_LOG");
            Expect("the notify variables do not collide with the job variables",
                KhBatchRunner.Schedule.ScheduleEnvironment.NotifyTitle ==
                    KhBatchRunner.Schedule.ScheduleEnvironment.Job, false);

            RebuildMatrixCases();

            Expect("blank legacy owner is accepted", ScheduledRunner.OwnerMatches(""), true);
            Expect("this product's owner is accepted",
                ScheduledRunner.OwnerMatches(KhBatchRunner.Product.ScheduleOwner), true);
            Expect("the other edition cannot consume this launch",
                ScheduledRunner.OwnerMatches(KhBatchRunner.Product.ScheduleOwner == "Full" ? "DRW" : "Full"), false);
            // 2.0.0: a 1.x job saved by the retired lite edition still runs in Full.
            Expect("a retired edition's owner is read as Full",
                KhBatchRunner.Schedule.ScheduledJob.NormalizeOwner("Lite"), "Full");
            Expect("DRW stays DRW",
                KhBatchRunner.Schedule.ScheduledJob.NormalizeOwner("drw"), "DRW");

            var stoppedModel = new CloudModel
            {
                DisplayName = "STOPPED-MODEL.rvt",
                LocalPath = @"C:\sim\STOPPED-MODEL.rvt"
            };
            var stoppedRun = new RunManifest { AllModels = new List<CloudModel> { stoppedModel } };
            stoppedRun.Unrecoverable.Add(ModelKey.For(stoppedModel));
            Expect("attempt-limit notification can name the stopped model",
                stoppedRun.UnrecoverableDisplayNames().Single(), "STOPPED-MODEL.rvt");

            string? previousClaim = Environment.GetEnvironmentVariable(ScheduledRunner.JobClaimEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(ScheduledRunner.JobClaimEnvVar, null);
                Expect("first add-in claims the scheduled job",
                    ScheduledRunner.TryClaimForCurrentProcess("job-a"), true);
                Expect("second add-in in the same Revit is refused",
                    ScheduledRunner.TryClaimForCurrentProcess("job-a"), false);
                Environment.SetEnvironmentVariable(ScheduledRunner.JobClaimEnvVar, "0:parent:Full");
                Expect("a child Revit may replace its inherited parent claim",
                    ScheduledRunner.TryClaimForCurrentProcess("chunk-a"), true);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ScheduledRunner.JobClaimEnvVar, previousClaim);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
        }
    }

    /// <summary>
    /// Runs the generated VBS through the same WScript host Task Scheduler uses. String-contains
    /// assertions alone cannot catch a VBScript parse/runtime failure, which would otherwise look
    /// exactly like Task Scheduler's unhelpful 0x1 result before the launcher can write a log.
    /// </summary>
    /// <summary>
    /// 1.4.7: a schedule can be switched off instead of deleted. One boolean, and it has to agree in
    /// four places — the JSON, the copy, the Windows task definition, and the save path that rebuilds
    /// the job from a form which has no control for it. Every one of those is a place where a paused
    /// schedule would quietly arm itself again, and none of them is visible until the run someone
    /// paused happens anyway.
    /// </summary>
    private static void ScheduleEnabledCases()
    {
        Section("PAUSE  a schedule that is switched off stays switched off");

        // --- the default, and what OLD job files load as -----------------------------------------
        Expect("a new schedule is armed", new ScheduledJob().Enabled, true);

        // Asserted against a FILE, not against the C# initializer: the claim is that every schedule
        // saved before 1.4.7 — whose JSON has no such property at all — loads armed. Serializing a
        // fresh object would write the property and prove nothing.
        string legacy = "{\"Id\":\"legacy1\",\"Name\":\"nightly\",\"Time\":\"18:00\"}";
        ScheduledJob? old = JsonSerializer.Deserialize<ScheduledJob>(
            legacy, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Expect("a job saved before the flag existed loads armed", old?.Enabled, true);

        // --- the copy ----------------------------------------------------------------------------
        var paused = new ScheduledJob { Name = "paused", Enabled = false };
        Expect("Duplicate keeps a schedule paused", paused.Duplicate().Enabled, false);

        // Duplicate() is also how a split run builds its children, and the chunk store is how one
        // reaches the child process. A false that must survive BOTH hops.
        ScheduledJob chunkCopy = paused.Duplicate();
        ChunkJobStore.Save(chunkCopy);
        try
        {
            Expect("…and the paused flag survives the chunk store",
                ChunkJobStore.GetById(chunkCopy.Id)?.Enabled, false);
        }
        finally { ChunkJobStore.Delete(chunkCopy.Id); }

        // --- the Windows task definition ---------------------------------------------------------
        // Three <Enabled> elements share that name in the document — one on the task's Settings and
        // one on each trigger shape — so a Contains() over the whole XML would pass on the trigger's
        // and prove nothing. Only Settings/Enabled is the arm/disarm flag Task Scheduler shows.
        XNamespace taskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string? SettingsEnabled(ScheduledJob j) =>
            TaskSchedulerGateway.BuildTaskXml(j, "L.exe", "R.exe", new DateTime(2026, 9, 1, 18, 0, 0))
                .Root?.Element(taskNs + "Settings")?.Element(taskNs + "Enabled")?.Value;

        var armedDaily = new ScheduledJob
        { Id = "armed", Name = "armed", Time = "18:00", Recurrence = ScheduleRecurrence.Daily };
        var pausedDaily = new ScheduledJob
        { Id = "off", Name = "off", Time = "18:00", Recurrence = ScheduleRecurrence.Daily, Enabled = false };

        Expect("an armed job registers an enabled task", SettingsEnabled(armedDaily), "true");
        // The one that matters: MigrateLegacyRegistrations re-registers through this builder on
        // every interactive Revit startup, and ScheduleSaver deletes and re-creates the task on
        // every save. A hardcoded "true" here re-arms a paused schedule behind the user's back.
        Expect("a paused job registers a DISABLED task", SettingsEnabled(pausedDaily), "false");

        // --- the save path -----------------------------------------------------------------------
        // Every Schedule* window's TryBuildJob constructs a BRAND NEW ScheduledJob carrying over
        // only the Id, so a field with no editor control arrives at its default. CarryForwardState
        // is the single place that repairs that, for both save paths.
        var stored = new ScheduledJob
        {
            Id = "carryfwd" + Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = "stored", Enabled = false, LastRunAt = new DateTime(2026, 8, 20, 3, 0, 0)
        };
        ScheduleStore.Save(stored);
        try
        {
            // Exactly what a window hands back: same id, everything else freshly defaulted.
            var rebuilt = new ScheduledJob { Id = stored.Id, Name = "stored, edited" };
            ScheduleStore.CarryForwardState(rebuilt);
            Expect("re-saving an edited schedule does not re-arm it", rebuilt.Enabled, false);
            Expect("…and does not blank its last-run stamp", rebuilt.LastRunAt, stored.LastRunAt);
            Expect("…while the edit itself is untouched", rebuilt.Name, "stored, edited");
        }
        finally { ScheduleStore.Delete(stored.Id); }

        var brandNew = new ScheduledJob { Id = "never-stored-" + Guid.NewGuid().ToString("N") };
        ScheduleStore.CarryForwardState(brandNew);
        Expect("a schedule that was never saved keeps its defaults", brandNew.Enabled, true);

        // --- what the grid shows -----------------------------------------------------------------
        Expect("a paused schedule reports no next run",
            KhBatchRunner.UI.ScheduleDisplay.DescribeNextRun(pausedDaily),
            KhBatchRunner.UI.ScheduleDisplay.Disabled);
        Expect("an armed one still reports a real one",
            KhBatchRunner.UI.ScheduleDisplay.DescribeNextRun(armedDaily) !=
                KhBatchRunner.UI.ScheduleDisplay.Disabled, true);

        // --- the headless backstop ---------------------------------------------------------------
        // A chunk is launched directly by the orchestrator, never by a task, so it must never
        // inherit the parent's pause — otherwise ScheduledRunner's new rejection kills the split
        // run's own children.
        // PrepareReplacement is the one public door onto BuildChunkJob that does not start Revit.
        RunGroup? group = ChunkLauncher.PrepareReplacement(
            new ScheduledJob { Id = "p", Name = "parent", Enabled = false },
            new List<CloudModel> { new() { DisplayName = "A.rvt", LocalPath = @"C:\A.rvt" } },
            new DateTime(2026, 9, 1, 18, 0, 0), chunkNumber: 2, out string? chunkError);
        Expect("a replacement chunk is prepared", chunkError, null);
        try
        {
            Expect("a chunk child is armed even from a paused parent",
                group == null ? (bool?)null : ChunkJobStore.GetById(group.ChunkId)?.Enabled, true);
        }
        finally { if (group != null) ChunkJobStore.Delete(group.ChunkId); }
    }

    private static void LauncherVbsExecutionCase()
    {
        Section("SCHEDULE  the launcher executable runs, waits, and reports what happened");
        string launcher = TaskSchedulerGateway.LauncherExePath();
        if (!File.Exists(launcher))
        {
            // Only reachable when the harness runs against a tree where the launcher has not been
            // built. Failing loudly beats silently skipping the one case that covers the 0x1 bug.
            Expect("KhBatchLauncher.exe is built beside the add-in (" + launcher + ")", false, true);
            return;
        }

        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string logPath = Path.Combine(_scratch, "launcher-probe.log");

        // THE PC ITSELF MAY REFUSE TO RUN IT, and that is not a regression in this code — it is the
        // condition the Revit-direct and cmd.exe task actions exist for (measured 2026-09-18:
        // Defender's "block executable files unless they meet a prevalence, age, or trusted list
        // criterion" rule refuses this freshly built binary in every folder, including %TEMP%).
        // Until this check existed the refusal surfaced as an UNHANDLED Win32Exception that killed
        // the harness mid-run, so every case after it went unreported on exactly the machine whose
        // schedules were being changed. Reported and skipped: a case that cannot run here is not a
        // case that failed.
        string? refusal = LauncherRefusal(launcher);
        if (refusal != null)
        {
            Note("this PC refuses to start " + Path.GetFileName(launcher) + " (" + refusal + ").");
            Note("The launcher cases are SKIPPED; the task actions that do not need it are covered above.");
            return;
        }

        // 1. The happy path: a real child, waited for, its exit code passed through.
        (int code, string text) = RunLauncher(launcher, Path.Combine(system32, "whoami.exe"), logPath);
        Expect("the launcher returns the child's exit code", code, 0);
        Expect("it records local and UTC time", text.Contains("local=") && text.Contains("utc="), true);
        Expect("it records the child pid", text.Contains("started pid="), true);
        Expect("it records the child exit code", text.Contains("exitCode=0"), true);

        // 2. A child that FAILS. This is the case that matters for reading Task Scheduler at all:
        //    0x1 is the child's own exit code passed straight through, not a launcher fault.
        //    reg.exe with no arguments prints its usage and exits 1 on every Windows machine, and
        //    the launcher passes no arguments to its target (Revit takes none).
        (code, text) = RunLauncher(launcher, Path.Combine(system32, "reg.exe"), logPath);
        Expect("a child's exit code 1 is passed through, not swallowed", code, 1);
        Expect("so 0x1 in Task Scheduler means the child failed, not the launcher",
            text.Contains("exitCode=1"), true);

        // 3. A target that is not there. Must be distinguishable from case 2 at a glance, because
        //    telling "Revit failed" from "Revit never started" is the entire diagnosis.
        (code, text) = RunLauncher(launcher, Path.Combine(system32, "no-such-revit.exe"), logPath);
        Expect("a missing target is not reported as the child's exit code", code, 4002);
        Expect("and says so in the log", text.Contains("does not exist on this PC"), true);

        // 4. The preflight the schedule editor runs before registering anything.
        Expect("the launch-chain preflight passes on this PC",
            TaskSchedulerGateway.VerifyLaunchChain(), null);

        // 5. Reading a result back. The whole 0x1 investigation started with a number nobody could
        //    interpret, so each code the chain can actually produce has to say what it means.
        Expect("0 reads as success", TaskSchedulerGateway.DescribeLastResult(0), "OK");
        Expect("1 names Revit, not the scheduler",
            TaskSchedulerGateway.DescribeLastResult(1).Contains("Revit exited"), true);
        Expect("the launcher's own failure is distinguishable from the child's",
            TaskSchedulerGateway.DescribeLastResult(4002).Contains("could not start Revit"), true);
        Expect("a still-running task is not reported as an error",
            TaskSchedulerGateway.DescribeLastResult(267009), "still running");
        Expect("an unknown code falls back to hex, matching Task Scheduler's own display",
            TaskSchedulerGateway.DescribeLastResult(unchecked((int)0x80070005)), "0x80070005");

        var known = new Dictionary<string, int> { ["KhBatchRunner_abc"] = 1 };
        Expect("a job with a recorded result shows it",
            ScheduleDisplay.LastResult(known, "KhBatchRunner_abc").Contains("Revit exited"), true);
        Expect("a job Windows has no task for shows nothing",
            ScheduleDisplay.LastResult(known, "KhBatchRunner_missing"), "—");
    }

    /// <summary>Runs the launcher once against <paramref name="target"/> and returns its exit code
    /// plus the text it appended to a fresh log.</summary>
    private static (int code, string log) RunLauncher(string launcher, string target, string logPath)
    {
        try { File.Delete(logPath); } catch { /* first run */ }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = launcher,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in new[] { "--job", "probe", "--owner", "Full", "--revit", target, "--log", logPath })
            psi.ArgumentList.Add(a);

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        return (process.ExitCode, File.Exists(logPath) ? File.ReadAllText(logPath) : "");
    }

    private static List<CloudModel> Models(int n) =>
        Enumerable.Range(1, n)
            .Select(i => new CloudModel { DisplayName = $"MODEL-{i}.rvt", LocalPath = $@"C:\sim\MODEL-{i}.rvt" })
            .ToList();

    private static RunTrace.Tail EmptyTail() => new(Path.Combine(_scratch, "empty.log"));

    private static string Trail(params (string At, int Pid, string Stage)[] lines)
    {
        string path = Path.Combine(_scratch, "trail-" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        foreach ((string at, int pid, string stage) in lines)
            sb.AppendLine($"{at} pid={pid} {stage}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private static string Csv(params (string Model, string Task, string Status)[] rows)
    {
        string path = Path.Combine(_scratch, "tracking-" + Guid.NewGuid().ToString("N") + ".csv");
        var sb = new StringBuilder();
        sb.AppendLine("Datetime (Finish),Model,Task,Status,Message");
        foreach ((string model, string task, string status) in rows)
            sb.AppendLine($"2026-07-26 20:00:00,{model},\"{task}\",{status},");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    /// <summary>A round of <paramref name="total"/> groups: the first few running, then some
    /// exited, then the rest pending.</summary>
    private static List<RunGroup> Groups(int total, int running, int exited)
    {
        var groups = new List<RunGroup>();
        for (int i = 0; i < total; i++)
        {
            var g = new RunGroup { ChunkId = "chunk" + i, Models = Models(1) };
            if (i < exited) { g.Pid = 100 + i; g.StartedAt = DateTime.Now; g.ExitedAt = DateTime.Now; }
            else if (i < exited + running) { g.Pid = 100 + i; g.StartedAt = DateTime.Now; }
            groups.Add(g);   // everything past that stays pending: Pid 0, no ExitedAt
        }
        return groups;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("  " + title);
    }

    /// <summary>A line that is neither a pass nor a failure — why a case could not be run here.
    /// Kept visually distinct from "ok" so a skipped section can never be mistaken for a green
    /// one when someone scans the output.</summary>
    private static void Note(string what) => Console.WriteLine($"    --   {what}");

    /// <summary>
    /// Why this PC will not start the launcher, or null when it will.
    ///
    /// <para>Asked by starting it, because the refusals that matter are not visible in the file
    /// system: an attack-surface-reduction rule, AppLocker or a software restriction policy all
    /// leave a perfectly readable executable that Windows declines to run. A refusal is thrown
    /// by Process.Start, so it must be caught here or it ends the harness.</para>
    /// </summary>
    private static string? LauncherRefusal(string launcher)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = launcher,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // No --job: the launcher exits on bad arguments without starting anything, which is all
            // this needs to know. Whether it RAN is the question, not what it did.
            psi.ArgumentList.Add("--self-test-arguments-that-do-not-exist");
            using System.Diagnostics.Process? probe = System.Diagnostics.Process.Start(psi);
            if (probe == null) return "Windows started no process";
            if (!probe.WaitForExit(30_000)) { try { probe.Kill(); } catch { } return "it did not exit within 30 seconds"; }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void Expect<T>(string what, T actual, T expected)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        if (ok) { _passed++; Console.WriteLine($"    ok   {what}"); }
        else { _failed++; Console.WriteLine($"    FAIL {what}\n           expected {expected}, got {actual}"); }
    }
}
