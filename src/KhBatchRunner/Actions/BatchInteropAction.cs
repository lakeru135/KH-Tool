using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Interop;

namespace KhBatchRunner.Actions;

/// <summary>
/// Batch Interop: runs an Autodesk Interoperability Tools (BIM Interoperability Tools) Model
/// Checker checkset against each model via <see cref="ModelCheckerInvoker"/> and exports a
/// per-model HTML + Excel report. A <see cref="ActionCategory.Check"/> — the checker runs no
/// transactions and the run is never stored in the model (verified: IsModified stays false),
/// so every model closes without sync/save. DocCheckRunner binds the document it's given, so
/// no active document is needed (models process on the cheap background path).
///
/// Carries configuration (the checkset path/URL), so — like <see cref="InitiateToFormaAction"/> —
/// it is NOT in <see cref="ActionRegistry"/>; <see cref="Commands.BatchInteropCommand"/>
/// constructs it per run. Reports land in "&lt;destination folder&gt;\Batch Interop\InteropReports_&lt;stamp&gt;\"
/// as "&lt;model&gt;.html" + "&lt;model&gt;.xlsx" — AIT names the HTML after the model itself, so
/// same-titled models are disambiguated by <see cref="UniqueReportName"/> within a process and by a
/// per-group folder across them (<see cref="InteropReportPaths"/>).
/// </summary>
public sealed class BatchInteropAction : BatchActionBase
{
    /// <summary>Alias of <see cref="Schedule.InteropStep.DisplayName"/>, which is where the name is
    /// actually declared — scheduling code has to recognise this step without being able to see a
    /// Revit type. See that class for why the dependency points this way.</summary>
    public const string DisplayName = Schedule.InteropStep.DisplayName;

    /// <summary>
    /// Whether Interop runs may fan out to parallel Revit instances.
    ///
    /// <b>v1.3.4 said no.</b> A multi-process Interop test crashed the PC, and every entry point was
    /// pinned to one sequential process pending "an isolated-machine investigation".
    ///
    /// <b>v1.3.7 says yes, under a budget.</b> What the investigation actually established is worth
    /// stating precisely, because the interesting part is what it did NOT find:
    /// <list type="bullet">
    /// <item>The crash was never root-caused, and the evidence was always thin — one event, on a PC
    /// already throwing an intermittent <c>0xc0000005</c> at <c>Jrn.AddInManagerSettings</c> in
    /// journals 0569–0573 BEFORE any Interop work, loading nine third-party add-ins. "Ran Interop in
    /// parallel, PC crashed" is a sequence, not a mechanism.</item>
    /// <item>No shared writable state was found in the check path. AIT's temp usage goes through
    /// <c>Path.GetTempPath()</c>, which Revit makes per-process. Its settings INI is shared but is
    /// read, not written, by a run. <see cref="Interop.AitBootstrapLock"/> serialises the bootstrap
    /// anyway — cheap, and it also stops several cold processes reflection-loading the same
    /// assemblies at the same instant, which is what the groups of a split run all do at once.</item>
    /// <item>The one concrete defect found was ours, not AIT's: sibling groups launch within the
    /// same second and shared a timestamped report folder, silently overwriting each other's
    /// reports (<see cref="Interop.InteropReportPaths"/>).</item>
    /// <item>The most plausible surviving explanation is mundane oversubscription — the instance
    /// budget had no idea a checking Revit costs more than an ordinary one. It does now
    /// (<see cref="Schedule.RunWeight.Interop"/>), and Interop runs are capped well below the
    /// general ceiling.</item>
    /// </list>
    /// So this is not "the crash was explained and fixed". It is "the run is now instrumented,
    /// budgeted, and correct, so the next failure will leave evidence instead of a rumour."
    /// <c>KHBATCH_MAX_INSTANCES=1</c> restores fully serial behaviour in the field without a build.
    /// </summary>
    public const bool AllowsParallelRuns = true;

    /// <summary>
    /// Parallel, but not while another instance is opening. MEASURED 2026-08-13: one open in a
    /// two-group Interop run took 15 min 55 s where its siblings took 7–50 s, because Revit's own
    /// Steel Connections extension was contending with the other instance over a machine-wide SQL
    /// instance and an abandoned mutex inside <c>DocumentOpening</c>. The run was stopped over it.
    /// See <see cref="Schedule.CloudOpenGate"/>; the checks themselves still run in parallel.
    /// </summary>
    public override bool SerialisesCloudOpens => true;

    /// <summary>
    /// A checkset asks questions about model ELEMENTS, and the engine's default open closes every
    /// user workset — so until 1.3.8.4 the Model Checker walked whatever happened to be loaded
    /// (views, sheets, project standards) and reported that as the model. Nothing failed, which is
    /// the problem: a check that cannot see an element cannot fail it. Reports produced before that
    /// build should be re-run.
    ///
    /// <para><b>1.4.3 narrows it from "every workset" to "every workset that could hold something a
    /// checkset can see".</b> The Model Checker is invoked with <c>checkLinks=false</c> — see
    /// <see cref="Interop.ModelCheckerInvoker.RunCheckset"/>, whose parameter no caller has ever
    /// passed — so a linked model's elements are never examined. A workset that holds nothing but
    /// <see cref="RevitLinkInstance"/>s therefore contributes nothing this action can look at, and
    /// opening it makes Revit download and open the whole linked model to no purpose. MEASURED in
    /// bundle <c>BKKDDCJ74R3_20260819_1645</c>: one host loaded <b>19</b> linked files, two of them
    /// Revit 2025 files that each raised a modal upgrade dialog, on an open that ran 38 minutes.</para>
    ///
    /// <para><b>Which is why the opt-out exists.</b> A <see cref="RevitLinkInstance"/> is a HOST
    /// element, so a checkset may legitimately ask about the links themselves — pinned, named, on
    /// the right workset — and with that workset shut it sees none of them and reports a pass. Note
    /// the asymmetry that makes this easy to miss: <see cref="RevitLinkType"/> is a TYPE and stays
    /// loaded whatever the workset does, so a check written against link types keeps working while
    /// one written against link instances quietly stops. Only the person who wrote the checkset
    /// knows which it is, so they are the one who answers, per saved checkset.</para>
    /// </summary>
    public override WorksetPosture Worksets =>
        _checksetExaminesLinks ? WorksetPosture.OpensAll : WorksetPosture.PlansOwn;

    public override string WorksetPostureReason => _checksetExaminesLinks
        ? "This checkset is declared to examine the links themselves, and a link instance is a host " +
          "element — so every workset opens, as every Interop run did before 1.4.3."
        : "A checkset can ask about any element, so there is no subset to DERIVE — unlike Export " +
          "NWC, which has its export views to ask. It subtracts instead: every user workset except " +
          "those a previous run PROVED hold nothing but links, which the checker cannot see anyway " +
          "(checkLinks=false). No record, no subtraction — a model's first run opens everything.";

    private readonly string _checksetPath;
    private readonly bool _checksetExaminesLinks;
    private string _reportDir = "";
    private string _lastNote = "";
    // What SelectWorksetsToOpen decided for the CURRENT model, in the words the row will use — the
    // same reason ExportNwcAction keeps one: a plan nobody can see is how under-opening stayed
    // invisible for three releases.
    private string _worksetPlan = "";
    // Report base names used so far this run — so two models with the SAME title in one set get
    // distinct report files (<title>, <title>_2, <title>_3…) instead of overwriting each other.
    private readonly HashSet<string> _usedReportNames = new(StringComparer.OrdinalIgnoreCase);
    // Every workbook this process wrote, for the run summary when no supervisor will build it.
    private readonly List<string> _writtenReports = new();
    private string _destinationFolder = "";
    private string _projectLog = "";

    /// <param name="checksetPath">Model Checker checkset — a local .xml path or an
    /// interoperability.autodesk.com hosted-checkset http(s) URL.</param>
    /// <param name="checksetExaminesLinks">Whether this checkset asks about the link instances in
    /// the host model. FALSE — the default everywhere — lets the run leave proven link-only worksets
    /// shut. See <see cref="Worksets"/> for why this is the user's call and not ours.</param>
    public BatchInteropAction(string checksetPath, bool checksetExaminesLinks = false)
    {
        _checksetPath = checksetPath;
        _checksetExaminesLinks = checksetExaminesLinks;
    }

    /// <summary>
    /// Every user workset except those a previous run proved hold nothing but links.
    ///
    /// <para><b>Subtraction, not derivation.</b> Export NWC can ask its export views which worksets
    /// they show; a checkset has no equivalent question to ask, so the starting point here is
    /// everything and the only thing removed is what has been PROVEN irrelevant. That proof comes
    /// from <see cref="Batch.WorksetAudit"/>, written by any earlier run that had the worksets open
    /// (since 1.4.3 the engine records it for every action that opens them, so an Interop run's own
    /// first pass over a model produces the evidence its second pass uses).</para>
    ///
    /// <para><b>Every uncertain path returns every workset</b> — no model identity, no record,
    /// nothing left after subtracting. Under-opening fails SILENTLY, with a report full of passes
    /// over elements that were never loaded; over-opening only costs time. That asymmetry decides
    /// every ambiguous case here, exactly as it does in Export NWC.</para>
    /// </summary>
    public override ICollection<WorksetId>? SelectWorksetsToOpen(Document doc)
    {
        List<WorksetId> all = AllUserWorksets(doc);
        try
        {
            var names = new List<string>();
            var byName = new Dictionary<string, WorksetId>(StringComparer.OrdinalIgnoreCase);
            foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                names.Add(w.Name);
                byName[w.Name] = w.Id;
            }

            List<string> keep = WorksetsToOpen(names, SkippableLinkWorksets());
            return Planned(keep.Select(n => byName[n]).ToList(), names.Count, names.Count - keep.Count);
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("Batch Interop | workset plan fell back to all: " + ex.Message);
            return all;
        }
    }

    /// <summary>
    /// The subtraction itself, with no Revit in it — which workset NAMES to open, given every user
    /// workset in the model and those proven to hold nothing but links.
    ///
    /// <para>Separated so the rule that decides whether a checkset sees an element can be asserted
    /// from any machine, the way <c>ReopenLadder.Refused</c>, <c>InstanceBudget.CapFor</c> and
    /// <c>WorksetVisibilityRule.Shows</c> already are. Names, not
    /// <see cref="WorksetId"/>s: a workset id means nothing across two separate opens of a detached
    /// model, which is the 1.3.8.7 lesson, and it is names the audit stores.</para>
    ///
    /// <para><b>Returns everything when subtracting would leave nothing.</b> A model whose every
    /// workset is recorded link-only is possible — a pure federation host — and asking for none is
    /// not a smaller request. It is the closed open that made this action call partial models clean
    /// for four versions.</para>
    /// </summary>
    internal static List<string> WorksetsToOpen(
        IReadOnlyList<string> allUserWorksets, ISet<string> provenLinkOnly)
    {
        if (provenLinkOnly.Count == 0) return allUserWorksets.ToList();

        List<string> keep = allUserWorksets.Where(n => !provenLinkOnly.Contains(n)).ToList();
        return keep.Count == 0 ? allUserWorksets.ToList() : keep;
    }

    /// <summary>Worksets this model is KNOWN to hold nothing but links in. Empty — skip nothing —
    /// when the checkset examines links, or when the engine could not say which model this is (a
    /// detached document cannot), or when no run has recorded this model yet.</summary>
    private ISet<string> SkippableLinkWorksets()
    {
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_checksetExaminesLinks) return none;

        string key = Batch.ModelContext.CurrentKey;
        return string.IsNullOrEmpty(key) ? none : Batch.WorksetAudit.LinkOnlyWorksets(key);
    }

    /// <summary>Records what was chosen so the row and the breadcrumb can say it, and returns the
    /// set unchanged.</summary>
    private List<WorksetId> Planned(List<WorksetId> chosen, int total, int skipped)
    {
        _worksetPlan = skipped > 0
            ? $"{chosen.Count} of {total} workset(s), skipping {skipped} proven link-only " +
              "(the checker does not look inside links)"
            : $"all {total} workset(s)";
        Diagnostics.RunTrace.Mark($"Batch Interop | workset plan: {_worksetPlan}");
        return chosen;
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

    public override string Name => DisplayName;

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Checkset", "Result", "Passed", "Failed", "Errors", "Not Run", "Pass %", "Report",
    };

    /// <summary>
    /// One run = one timestamped report folder, placed under this task's own folder
    /// (<c>&lt;destination&gt;\Batch Interop\InteropReports_&lt;stamp&gt;\</c>) so the reports sit
    /// beside the task's CSV logs in the per-task log tree.
    ///
    /// A group of an auto-split run gets its OWN folder under the run's shared stamp
    /// (<c>…_g2</c>): sibling groups launch within the same second, and AIT names each HTML after
    /// the model, so one shared folder would let a same-titled model in another group overwrite
    /// this one's report. See <see cref="InteropReportPaths"/>.
    /// </summary>
    public override void ConfigureOutput(string destinationFolder, string projectLogName)
    {
        _destinationFolder = destinationFolder;
        _projectLog = projectLogName;
        _reportDir = InteropReportPaths.ReportFolder(
            destinationFolder, Name,
            Schedule.ChunkContext.RunStamp ?? DateTime.Now,
            Schedule.ChunkContext.FolderTag);
    }

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        if (string.IsNullOrEmpty(_reportDir))
            throw new InvalidOperationException("ConfigureOutput was not called before Run.");
        Directory.CreateDirectory(_reportDir);

        string safeTitle = UniqueReportName(
            string.Join("_", doc.Title.Split(Path.GetInvalidFileNameChars())));
        string xlsxPath = Path.Combine(_reportDir, safeTitle + ".xlsx");

        ModelCheckerRunResult result = ModelCheckerInvoker.RunCheckset(
            uiApp, doc, _checksetPath, htmlExportFolder: _reportDir, excelExportPath: xlsxPath);
        if (File.Exists(xlsxPath)) _writtenReports.Add(xlsxPath);

        _lastNote = $"{result.OverallResult}: {result.Passed} passed, {result.Failed} failed" +
                    (result.Errors > 0 ? $", {result.Errors} errors" : "") +
                    $" ({result.PassPercent:0.#}%)";

        // Say so in the ROW, not only the trail, whenever this model was checked with worksets shut.
        // A result read six months from now has to carry the fact that part of the model was not
        // loaded — otherwise "0 failed" means two different things and looks like one.
        if (_worksetPlan.Contains("skipping"))
            _lastNote += $" — opened {_worksetPlan}";

        return new Dictionary<string, string>
        {
            ["Checkset"] = _checksetPath,
            ["Result"] = result.OverallResult,
            ["Passed"] = result.Passed.ToString(),
            ["Failed"] = result.Failed.ToString(),
            ["Errors"] = result.Errors.ToString(),
            ["Not Run"] = result.NotRun.ToString(),
            ["Pass %"] = result.PassPercent.ToString("0.#"),
            ["Report"] = Path.Combine(_reportDir, safeTitle + ".html"),
        };
    }

    public override string PerModelNote => _lastNote;

    /// <summary>One workbook for the whole run, merged from every model's report
    /// (<see cref="InteropExcelReport.Merge"/>). Skipped inside a supervised child, which only holds
    /// part of the run — the supervisor builds it from the detail CSV once the last child exits
    /// (<see cref="Reporting.RunEndMatrix"/>), as it does the matrices.</summary>
    public override void OnRunComplete()
    {
        if (!Reporting.MatrixUtil.WritesHere || _writtenReports.Count == 0) return;
        string path = Reporting.MatrixUtil.OutputPath(_destinationFolder, _projectLog, Name, RunStamp, "Summary");
        InteropExcelReport.Merge(_writtenReports, path);
    }

    /// <summary>Returns <paramref name="baseName"/>, or <c>baseName_2</c>, <c>baseName_3</c>… if a
    /// report with that name was already written this run (two models with the same title).</summary>
    private string UniqueReportName(string baseName)
    {
        if (_usedReportNames.Add(baseName)) return baseName;
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseName}_{n}";
            if (_usedReportNames.Add(candidate)) return candidate;
        }
    }
}

