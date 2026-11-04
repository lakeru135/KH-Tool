using System.IO;
using System.Text;
using KhBatchRunner.Actions;
using KhBatchRunner.Interop;
using KhBatchRunner.Logging;
using KhBatchRunner.Schedule;
using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Reporting;

/// <summary>
/// Builds the ONE matrix workbook for a finished auto-split run.
///
/// <para><b>The problem.</b> A matrix is a pivot across every model in the run: models are columns.
/// An auto-split run has no process that sees every model — each chunk opens its own group and
/// nothing else — so when every chunk wrote its own workbook at its own finish second, a split run
/// produced one partial workbook per group where an unsplit run produced one whole one. Tagging the
/// filenames per group would have made that legible but no less wrong; the data has to be brought
/// back together, not the names.</para>
///
/// <para><b>Where it comes from.</b> The rows already are together. Every chunk appends to the same
/// per-day detail CSV through <see cref="LogFileLock"/> (<see cref="DetailCsvLogger"/>), so by the
/// time the last chunk exits, the complete run is sitting in one file — only the pivot is missing.
/// This reads that file, keeps the rows belonging to THIS run, and hands them to
/// <see cref="MatrixCsvImport"/>, which is the same code the Rebuild Matrix button and the
/// in-process writer both end in. Nothing here re-implements a workbook.</para>
///
/// <para>Runs in the supervising process (<see cref="RunSupervisor"/>), which is a Revit for a
/// scheduled run and may be the controller for a driven one — hence no Revit types in this file.</para>
/// </summary>
internal static class RunEndMatrix
{
    /// <summary>
    /// The checks that pivot themselves into a workbook at run end, and which matrix each produces.
    ///
    /// Deliberately only two, and now the only two that exist. Four checks used to do this
    /// automatically; Link Workset and Drawing Progress were dropped in 1.3.8.1 because a run that
    /// quietly emits four workbooks nobody asked for is harder to read, not easier. 1.3.8.2 removed
    /// the Link Workset matrix entirely, rebuild included, so <see cref="MatrixKind"/> and this
    /// table now list the same two things.
    /// </summary>
    private static readonly (string Task, MatrixKind Kind)[] Producers =
    {
        (ActionNames.GetPhaseAndWorkset, MatrixKind.PhaseWorkset),
        (ActionNames.GetDesignOption, MatrixKind.DesignOption),
    };

    /// <summary>
    /// Writes one matrix per matrix-producing task in <paramref name="manifest"/>'s job, covering
    /// every model of the run. Call once, after the last chunk has exited.
    ///
    /// <para>Fail-soft in every direction: a run whose models all ran is a successful run whatever
    /// happens to its workbook, and a stopped run still gets a matrix of what it did manage. Each
    /// failure is reported through <paramref name="note"/> (status, message) naming the CSV, because
    /// the recovery — Rebuild Matrix from CSV — needs that path and nothing else.</para>
    /// </summary>
    public static void BuildForRun(RunManifest manifest, Action<string, string> note)
    {
        ScheduledJob? job = manifest.BaseJob;
        if (job == null) return;

        string dest = job.DestinationFolder;
        string projectLog = string.IsNullOrWhiteSpace(job.ProjectLogName) ? job.Name : job.ProjectLogName;
        if (string.IsNullOrWhiteSpace(dest)) return;

        var wanted = new HashSet<string>(
            job.RunItems.Where(i => i.Kind == RunItemKind.Action).Select(i => i.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach ((string task, MatrixKind kind) in Producers)
        {
            if (!wanted.Contains(task)) continue;
            try { BuildOne(manifest, dest, projectLog, task, kind, note); }
            catch (Exception ex)
            {
                note("ERROR", $"the {task} matrix could not be built: {ex.Message} — the data is " +
                              "safe in that task's CSV; use Rebuild Report to produce it.");
            }
        }

        if (InteropStep.IsRequested(job))
        {
            try { BuildInteropSummary(manifest, dest, projectLog, note); }
            catch (Exception ex)
            {
                note("ERROR", $"the {InteropStep.DisplayName} summary workbook could not be built: " +
                              $"{ex.Message} — each model's own report is unaffected; Rebuild Report → " +
                              "Batch Interop Summary can merge them.");
            }
        }
    }

    /// <summary>
    /// The Batch Interop run workbook: every model's report merged into one
    /// (<see cref="InteropExcelReport.Merge"/>), beside the task's CSV as
    /// <c>&lt;stamp&gt;_&lt;project&gt;_Batch Interop Summary.xlsx</c>.
    ///
    /// <para>Same problem as the matrices, same source. A supervised run — and since the RG crash
    /// every Batch Interop run is one — checks its models in child Revits, each writing per-model
    /// reports into its own group folder, so no process ever held the whole run. The detail CSV
    /// does: one row per checked model under this run's tag, its <c>Report</c> cell naming the
    /// HTML, whose workbook sits beside it. A model that crashed and was skipped has no row and is
    /// simply absent — its ERROR is in the tracking log, and a blank column would read as a result.</para>
    /// </summary>
    private static void BuildInteropSummary(RunManifest manifest, string dest, string projectLog,
        Action<string, string> note)
    {
        const string task = InteropStep.DisplayName;
        string? csv = FindDetailCsv(dest, projectLog, task, manifest.RunStamp);
        if (csv == null) return;    // no model was checked — nothing to summarise, not an error

        (List<string> header, List<string[]> rows) = MatrixCsvImport.ReadCsv(csv);
        int reportCol = IndexOf(header, "Report");
        if (reportCol < 0) return;

        List<string> reports = RowsForRun(header, rows, manifest)
            .Select(r => Cell(r, reportCol))
            .Where(p => p.Length > 0)
            .Select(p => Path.ChangeExtension(p, ".xlsx"))
            .ToList();
        if (reports.Count == 0) return;

        string outPath = MatrixPaths.OutputPath(dest, projectLog, task, manifest.RunStamp, "Summary");
        (int models, List<string> unreadable) = InteropExcelReport.Merge(reports, outPath);
        if (models > 0)
        {
            RunTrace.Mark($"run-end interop summary written: {Path.GetFileName(outPath)} ({models} model(s))");
            note("INFO", $"{task} summary written for the whole run ({models} model(s)): {outPath}");
        }
        if (unreadable.Count > 0)
            note("INFO", $"{unreadable.Count} model report(s) could not be read into the {task} summary " +
                         $"and are missing from it — each still has its own report: {string.Join(", ", unreadable)}");
    }

    private static void BuildOne(RunManifest manifest, string dest, string projectLog, string task,
        MatrixKind kind, Action<string, string> note)
    {
        string? csv = FindDetailCsv(dest, projectLog, task, manifest.RunStamp);
        if (csv == null) return;    // the task wrote no rows at all — nothing to pivot, not an error

        (List<string> header, List<string[]> rows) = MatrixCsvImport.ReadCsv(csv);
        List<string[]> mine = Deduplicate(header, RowsForRun(header, rows, manifest));
        if (mine.Count == 0)
        {
            note("INFO", $"no {task} rows were recorded for this run, so no matrix was written " +
                         $"({Path.GetFileName(csv)}).");
            return;
        }

        string outPath = MatrixPaths.OutputPath(dest, projectLog, task, manifest.RunStamp);
        MatrixCsvImport.Build(kind, header, mine, outPath);
        RunTrace.Mark($"run-end matrix written: {Path.GetFileName(outPath)} ({mine.Count} row(s))");
        note("INFO", $"{task} matrix written for the whole run: {outPath}");
    }

    /// <summary>
    /// Drops rows a RETRY recorded twice, comparing every column except <c>Datetime</c>.
    ///
    /// <para>The supervisor reruns a model that has no complete tracking row — and
    /// <see cref="Batch.BatchEngine"/> writes a model's detail rows BEFORE its tracking row, so an
    /// instance that dies in that gap leaves a full set of detail rows behind and the model is run
    /// again. Both attempts land in the same day-file under the same run tag. The pivot itself is a
    /// presence grid and survives that, but its Grand Totals count rows, so a retried model would be
    /// reported as having twice the phases it has.</para>
    ///
    /// <para>Excluding <c>Datetime</c> is the whole trick: it is the only column two attempts at the
    /// same model are guaranteed to differ in. Everything else identical means the same fact
    /// recorded twice — a model cannot have two phases of one name — so keeping one is not a guess.</para>
    /// </summary>
    /// <summary>Exposes <see cref="Deduplicate"/> to the offline harness, which pins the retry case
    /// against the row shape a real day-file has.</summary>
    internal static List<string[]> DeduplicateForTest(List<string> header, List<string[]> rows) =>
        Deduplicate(header, rows);

    private static List<string[]> Deduplicate(List<string> header, List<string[]> rows)
    {
        int timeCol = IndexOf(header, "Datetime");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string[]>(rows.Count);

        foreach (string[] row in rows)
        {
            var key = new StringBuilder();
            for (int i = 0; i < row.Length; i++)
            {
                if (i == timeCol) continue;
                key.Append(row[i]).Append('');    // unit separator: cannot occur in a CSV cell
            }
            if (seen.Add(key.ToString())) kept.Add(row);
        }
        return kept;
    }

    /// <summary>
    /// This run's rows, and only this run's.
    ///
    /// The detail CSV is a per-DAY file appended to by every run of that task — and runs overlap in
    /// practice (three supervised runs started within four minutes of each other in the 2026-08-11
    /// report), so a timestamp cut alone would silently fold one run's models into another's matrix.
    /// The <see cref="DetailCsvLogger.RunColumn"/> column is written for exactly this and is an
    /// exact match when present.
    ///
    /// <para>When it is absent — a file started earlier the same day by a build older than 1.3.8.1,
    /// whose header therefore wins for the rest of that day — fall back to what can still be known:
    /// rows stamped at or after the run began, for models this run was given. That is wrong only if
    /// an overlapping run shares both a model and a task, and it is strictly better than either
    /// refusing to build or building from everything.</para>
    /// </summary>
    private static List<string[]> RowsForRun(List<string> header, List<string[]> rows, RunManifest manifest)
    {
        int runCol = IndexOf(header, DetailCsvLogger.RunColumn);
        if (runCol >= 0)
        {
            string tag = DetailCsvLogger.RunTag(manifest.RunStamp);
            return rows.Where(r => Cell(r, runCol) == tag).ToList();
        }

        int timeCol = IndexOf(header, "Datetime");
        int modelCol = IndexOf(header, "Model Name");
        var models = new HashSet<string>(
            manifest.AllModels.Select(m => StripDetached(m.DisplayName)), StringComparer.OrdinalIgnoreCase);

        return rows.Where(r =>
        {
            if (modelCol >= 0 && !models.Contains(StripDetached(Cell(r, modelCol)))) return false;
            if (timeCol < 0) return true;
            return !DateTime.TryParse(Cell(r, timeCol), out DateTime at) || at >= manifest.RunStamp;
        }).ToList();
    }

    /// <summary>
    /// The task's detail CSV for this run's DATE, falling back to today's.
    ///
    /// Two dates because the file is named for the day it was created while the run carries its own
    /// stamp: a run that starts at 23:58 writes into yesterday's file by the time the supervisor
    /// looks. Neither existing means the task recorded nothing.
    /// </summary>
    private static string? FindDetailCsv(string dest, string projectLog, string task, DateTime runStamp)
    {
        foreach (DateTime day in new[] { runStamp, DateTime.Now })
        {
            string path = DetailCsvLogger.PathFor(dest, projectLog, task, day);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Model names reach the CSV through <c>MatrixUtil.ModelName</c>, which strips the
    /// "_detached" Revit appends; the manifest holds the raw Forma name. Strip both sides so the
    /// legacy fallback compares like with like.</summary>
    private static string StripDetached(string name)
    {
        const string suffix = "_detached";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - suffix.Length)
            : name;
    }

    private static int IndexOf(List<string> header, string column) =>
        header.FindIndex(h => string.Equals(h?.Trim(), column, StringComparison.OrdinalIgnoreCase));

    private static string Cell(string[] row, int index) =>
        index >= 0 && index < row.Length ? (row[index] ?? "").Trim() : "";
}
