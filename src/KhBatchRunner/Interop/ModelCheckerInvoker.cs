using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Interop;

/// <summary>Summary of one Model Checker checkset run over one document (plus its links when
/// link checking is on). Counts are summed across all files the run covered.</summary>
public sealed class ModelCheckerRunResult
{
    public ModelCheckerRunResult(
        string overallResult, int total, int passed, int failed, int errors, int notRun,
        int reportOnly, double passPercent, string? htmlReportFolder, string? excelReportPath)
    {
        OverallResult = overallResult;
        TotalChecks = total; Passed = passed; Failed = failed; Errors = errors;
        NotRun = notRun; ReportOnly = reportOnly; PassPercent = passPercent;
        HtmlReportFolder = htmlReportFolder; ExcelReportPath = excelReportPath;
    }

    /// <summary>Pass/Fail/Error/Report — the host file's overall result.</summary>
    public string OverallResult { get; }
    public int TotalChecks { get; }
    public int Passed { get; }
    public int Failed { get; }
    public int Errors { get; }
    public int NotRun { get; }
    /// <summary>Checks that only report a count (no pass/fail condition).</summary>
    public int ReportOnly { get; }
    public double PassPercent { get; }
    public string? HtmlReportFolder { get; }
    public string? ExcelReportPath { get; }
}

/// <summary>
/// Reflection bridge to the Autodesk Interoperability Tools (AIT / "BIM Interoperability
/// Tools") Model Checker. Mirrors <see cref="Scripts.DynamoInvoker"/>: no compile-time AIT
/// reference — the AIT install is per Revit year (2024 net48, 2026 net8; 2025 may be absent)
/// and its assemblies are loaded from <c>%ProgramFiles%\Autodesk\AIT\&lt;year&gt;</c> (AIT 10.5+) or
/// <c>%ProgramFiles(x86)%\…</c> (earlier) at runtime, preferring the copies AIT's own add-in already loaded (their static state — e.g.
/// ModelChecker's SharedData — is then already initialized).
///
/// The invoked surface (decompiled from AIT 10.0.9 / Revit 2026, all public types + ctors):
///   CheckSetProviderService(new PreBuiltOptionsService()).GetCheckSet(path or http URL)
///   new DocCheckRunner(doc, new RevitIniService(new IniService(), year)).Run(checkLinks, set)
///   new ReportExporterHtml()/.._Excel().Export(reportRun, ExportOptions)
/// DocCheckRunner takes the Document explicitly (not the active UI document) and runs no
/// transactions — the only transactions in AIT's ModelChecker are in its extensible-storage
/// repositories (ReportRunRepository etc.), which this bridge deliberately never calls, so a
/// run leaves the document unmodified.
/// </summary>
public static class ModelCheckerInvoker
{
    private const string ApiAssembly = "AIT.ModelChecker.API";
    private const string RevitApiAssembly = "AIT.ModelChecker.RevitAPI";
    private const string CommonApiAssembly = "AIT.Common.API";

    private static string _aitDir = "";
    private static bool _resolverHooked;

    // Resolved once per process (the add-in lives as long as Revit does).
    private static Type? _checkSetProviderType;   // ADSK.AIT.ModelChecker.API.Services.Implementation.CheckSetProviderService
    private static Type? _preBuiltOptionsType;    // ADSK.AIT.ModelChecker.API.Services.Implementation.PreBuiltOptionsService
    private static Type? _docCheckRunnerType;     // ADSK.AIT.ModelChecker.Revit.API.Services.Implementation.DocCheckRunner
    private static Type? _iniServiceType;         // ADSK.AIT.Common.API.Services.Implementation.IniService
    private static Type? _revitIniServiceType;    // ADSK.AIT.Common.API.Services.Implementation.RevitIniService
    private static Type? _htmlExporterType;       // ADSK.AIT.ModelChecker.API.Services.Implementation.ReportExporterHtml
    private static Type? _excelExporterType;      // ADSK.AIT.ModelChecker.API.Services.Implementation.ReportExporterExcel
    private static Type? _exportOptionsType;      // ADSK.AIT.ModelChecker.API.DataModel.ExportOptions
    private static MethodInfo? _totalDisplayMethod; // ADSK.AIT.ModelChecker.API.Utility.GetTotalDisplay

    /// <summary>Optional diagnostic sink — each invocation step is reported here (the spike
    /// and self-test logs wire in) so a failure inside AIT can be located. No-op by default.
    /// Independent of the always-on <see cref="Diagnostics.RunTrace"/> breadcrumb; see
    /// <see cref="Note"/>.</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>
    /// One breadcrumb per AIT step, written to TWO places for different reasons.
    ///
    /// <see cref="Trace"/> is the caller's own log (spike window, self-test report) and is null
    /// unless somebody asked. <see cref="Diagnostics.RunTrace"/> is unconditional, because this is
    /// the only trail that survives what we are actually hunting: AIT runs inside Revit's process,
    /// so a native failure in there takes the process with it and no managed handler ever sees it —
    /// whatever the last line in <c>surface.log</c> says IS the diagnosis. A parallel Interop run
    /// that killed the machine in 1.3.4 left nothing at all, which is why it was never root-caused.
    ///
    /// It also keeps the stall watchdog honest: a long checkset over a big model can spend many
    /// minutes inside <c>Run</c> without the engine marking anything, and a stage that never
    /// changes is what "stalled" is defined as. Marking AIT's own steps means a slow check reads as
    /// slow rather than wedged.
    /// </summary>
    private static void Note(string msg)
    {
        string line = "[ModelCheckerInvoker] " + msg;
        Diagnostics.RunTrace.Mark(line);   // best-effort; Mark never throws
        Trace?.Invoke(line);
    }

    /// <summary>AIT install folder for a Revit year. AIT 10.5 installs to C:\Program Files\Autodesk\AIT\2026;
    /// earlier releases used C:\Program Files (x86)\Autodesk\AIT\2026. The first folder holding the
    /// Model Checker API wins; with neither installed, the (x86) path is returned for the error text.</summary>
    public static string AitDirectory(string revitVersionNumber)
    {
        string x86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Autodesk", "AIT", revitVersionNumber);
        string x64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Autodesk", "AIT", revitVersionNumber);
        foreach (string dir in new[] { x64, x86 })
            if (File.Exists(Path.Combine(dir, ApiAssembly + ".dll"))) return dir;
        return x86;
    }

    /// <summary>Whether AIT's Model Checker is installed for the given Revit year.</summary>
    public static bool IsInstalled(string revitVersionNumber) =>
        File.Exists(Path.Combine(AitDirectory(revitVersionNumber), ApiAssembly + ".dll"));

    /// <summary>
    /// Runs one checkset (.xml file path or http(s) URL) against <paramref name="doc"/> and
    /// optionally exports the report. <paramref name="htmlExportFolder"/> is a folder — AIT
    /// writes one "&lt;model name&gt;.html" per checked file into it; <paramref name="excelExportPath"/>
    /// is a full .xlsx path. Read-only: the report is returned/exported, never stored in the
    /// model. Throws on any AIT failure (missing install, bad checkset, run error).
    /// </summary>
    public static ModelCheckerRunResult RunCheckset(
        UIApplication uiApp, Document doc, string checksetPath,
        string? htmlExportFolder = null, string? excelExportPath = null, bool checkLinks = false)
    {
        string version = uiApp.Application.VersionNumber;
        // Named before anything AIT-owned is touched: if the process dies during load or check,
        // this is the line that says WHICH model it died on.
        Note($"begin '{doc.Title}' (Revit {version})");

        // Bootstrap under the cross-process lock; the CHECK below stays outside it, which is the
        // whole point of splitting the run. See AitBootstrapLock for what is and isn't shared.
        // Every phase is timed into its own Note: "where did this model's time go" has to be
        // answerable from surface.log alone, without subtracting breadcrumb timestamps by hand.
        var phase = Stopwatch.StartNew();
        object checkSet = null!;
        object runner = null!;
        AitBootstrapLock.Run(version, () =>
        {
            EnsureLoaded(version);

            Note("loading checkset " + checksetPath);
            object provider = Activator.CreateInstance(
                _checkSetProviderType!, Activator.CreateInstance(_preBuiltOptionsType!))!;
            checkSet = InvokeUnwrapped(
                _checkSetProviderType!.GetMethod("GetCheckSet", new[] { typeof(string) })!,
                provider, new object[] { checksetPath })!;

            Note("creating DocCheckRunner");
            object iniService = Activator.CreateInstance(_iniServiceType!)!;
            object revitIni = Activator.CreateInstance(
                _revitIniServiceType!, iniService, int.Parse(version))!;
            runner = Activator.CreateInstance(_docCheckRunnerType!, doc, revitIni)!;
        });

        Note($"bootstrap took {phase.ElapsedMilliseconds} ms (lock wait + load + checkset)");

        Note($"running checks (checkLinks={checkLinks})…");
        phase.Restart();
        // Run(bool checkLinks, CheckSet checkSet, ReportRun results = null, int curFileNum = 1, int totalFiles = 1)
        MethodInfo run = _docCheckRunnerType!.GetMethod("Run")!;
        object reportRun = InvokeUnwrapped(run, runner, new object?[] { checkLinks, checkSet, null, 1, 1 })!;
        Note($"check run complete in {phase.ElapsedMilliseconds} ms");

        (string overall, int total, int pass, int fail, int error, int notRun, int report, double pct) =
            SummarizeFileResults(reportRun);
        Note($"result: {overall} — {pass}/{total} passed, {fail} failed, {error} errors, {notRun} not run");

        if (!string.IsNullOrWhiteSpace(htmlExportFolder))
        {
            Note("exporting HTML to " + htmlExportFolder);
            phase.Restart();
            Export(_htmlExporterType!, reportRun, htmlExportFolder!);
            Note($"HTML export complete in {phase.ElapsedMilliseconds} ms");
        }
        if (!string.IsNullOrWhiteSpace(excelExportPath))
        {
            Note("exporting Excel to " + excelExportPath);
            phase.Restart();
            string writer = ExportExcel(reportRun, excelExportPath!);
            Note($"Excel export complete in {phase.ElapsedMilliseconds} ms ({writer})");
        }

        Note($"done '{doc.Title}'");
        return new ModelCheckerRunResult(
            overall, total, pass, fail, error, notRun, report, pct,
            string.IsNullOrWhiteSpace(htmlExportFolder) ? null : htmlExportFolder,
            string.IsNullOrWhiteSpace(excelExportPath) ? null : excelExportPath);
    }

    /// <summary>Sums counts over ReportRun.FileResults (host file first, links after); the
    /// overall verdict is the host file's OverallResult.</summary>
    private static (string, int, int, int, int, int, int, double) SummarizeFileResults(object reportRun)
    {
        var fileResults = (System.Collections.IEnumerable)reportRun.GetType()
            .GetProperty("FileResults")!.GetValue(reportRun)!;
        string overall = "(no results)";
        int total = 0, pass = 0, fail = 0, error = 0, notRun = 0, report = 0;
        double pct = 0; bool first = true;
        foreach (object fileReport in fileResults)
        {
            Type t = fileReport.GetType();
            int Get(string name) => (int)t.GetProperty(name)!.GetValue(fileReport)!;
            total += Get("TotalCount"); pass += Get("PassCount"); fail += Get("FailCount");
            error += Get("ErrorCount"); notRun += Get("NotRunCount"); report += Get("ReportCount");
            if (first)
            {
                overall = t.GetProperty("OverallResult")!.GetValue(fileReport)?.ToString() ?? "(null)";
                pct = (double)t.GetProperty("PassPercent")!.GetValue(fileReport)!;
                first = false;
            }
        }
        return (overall, total, pass, fail, error, notRun, report, pct);
    }

    /// <summary>
    /// Forces AIT's own Excel exporter instead of <see cref="InteropExcelReport"/>. Read from
    /// <c>KHBATCH_INTEROP_AIT_EXCEL=1</c> at startup — a field escape hatch that needs no build —
    /// and set directly by the profile spike, which writes one AIT report to compare against.
    /// </summary>
    internal static bool ForceAitExcel { get; set; } =
        Environment.GetEnvironmentVariable("KHBATCH_INTEROP_AIT_EXCEL") == "1";

    /// <summary>Writes the Excel report with our writer (see <see cref="InteropExcelReport"/> for
    /// why), falling back to AIT's exporter if ours fails — a slow report beats a missing one.
    /// Returns which writer produced the file, for the breadcrumb.</summary>
    private static string ExportExcel(object reportRun, string path)
    {
        if (!ForceAitExcel)
        {
            try
            {
                InteropExcelReport.Write(InteropExcelReport.Snapshot(reportRun, TotalDisplay), path);
                return "KH writer";
            }
            catch (Exception ex)
            {
                Note("KH Excel writer failed, falling back to AIT's: " + ex.Message);
            }
        }
        Export(_excelExporterType!, reportRun, path);
        return "AIT exporter";
    }

    /// <summary>AIT's own <c>Utility.GetTotalDisplay(ListItems)</c> — the Checks sheet's Count
    /// column, formatted exactly as AIT formats it.</summary>
    private static string TotalDisplay(object listItems) =>
        (string)InvokeUnwrapped(
            _totalDisplayMethod ?? throw new MissingMethodException("ADSK.AIT.ModelChecker.API.Utility", "GetTotalDisplay"),
            null!, new[] { listItems })!;

    private static void Export(Type exporterType, object reportRun, string location)
    {
        object exporter = Activator.CreateInstance(exporterType)!;
        object options = Activator.CreateInstance(_exportOptionsType!)!;
        _exportOptionsType!.GetProperty("ExportLocation")!.SetValue(options, location);
        _exportOptionsType.GetProperty("ExportLists")!.SetValue(options, true);
        InvokeUnwrapped(exporterType.GetMethod("Export")!, exporter, new[] { reportRun, options });
    }

    private static object? InvokeUnwrapped(MethodInfo method, object target, object?[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException; // surface AIT's own error, not the reflection wrapper
        }
    }

    private static void EnsureLoaded(string revitVersionNumber)
    {
        if (_docCheckRunnerType != null) return;

        _aitDir = AitDirectory(revitVersionNumber);
        if (!IsInstalled(revitVersionNumber))
            throw new FileNotFoundException(
                $"Autodesk Interoperability Tools (Model Checker) not found for Revit {revitVersionNumber} " +
                $"— expected {_aitDir}. Install BIM Interoperability Tools for this Revit version.");

        HookResolver();
        Assembly api = GetOrLoad(ApiAssembly);
        Assembly revitApi = GetOrLoad(RevitApiAssembly);
        Assembly commonApi = GetOrLoad(CommonApiAssembly);

        _checkSetProviderType = api.GetType("ADSK.AIT.ModelChecker.API.Services.Implementation.CheckSetProviderService", throwOnError: true)!;
        _preBuiltOptionsType = api.GetType("ADSK.AIT.ModelChecker.API.Services.Implementation.PreBuiltOptionsService", throwOnError: true)!;
        _htmlExporterType = api.GetType("ADSK.AIT.ModelChecker.API.Services.Implementation.ReportExporterHtml", throwOnError: true)!;
        _excelExporterType = api.GetType("ADSK.AIT.ModelChecker.API.Services.Implementation.ReportExporterExcel", throwOnError: true)!;
        _exportOptionsType = api.GetType("ADSK.AIT.ModelChecker.API.DataModel.ExportOptions", throwOnError: true)!;
        // Soft: only our Excel writer needs it, and without it that writer fails over to AIT's
        // exporter (ExportExcel). A missing helper must never stop a check from running.
        _totalDisplayMethod = api.GetType("ADSK.AIT.ModelChecker.API.Utility", throwOnError: false)?
            .GetMethod("GetTotalDisplay", BindingFlags.Public | BindingFlags.Static);
        _docCheckRunnerType = revitApi.GetType("ADSK.AIT.ModelChecker.Revit.API.Services.Implementation.DocCheckRunner", throwOnError: true)!;
        _iniServiceType = commonApi.GetType("ADSK.AIT.Common.API.Services.Implementation.IniService", throwOnError: true)!;
        _revitIniServiceType = commonApi.GetType("ADSK.AIT.Common.API.Services.Implementation.RevitIniService", throwOnError: true)!;
        Note("types resolved");
    }

    /// <summary>Prefer the assembly AIT's own add-in already loaded (its static state is then
    /// initialized and type identity matches AIT's other assemblies); otherwise load from the
    /// AIT folder — on .NET 8 into the same load context as any already-loaded AIT anchor, so
    /// mixed-context type mismatches can't occur.</summary>
    private static Assembly GetOrLoad(string simpleName)
    {
        Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
            string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
        if (loaded != null)
        {
            Note($"{simpleName}: already loaded ({DescribeContext(loaded)})");
            return loaded;
        }

        string path = Path.Combine(_aitDir, simpleName + ".dll");
        Note($"{simpleName}: loading from {path}");
#if NETFRAMEWORK
        return Assembly.LoadFrom(path);
#else
        Assembly? anchor = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
            (a.GetName().Name ?? "").StartsWith("AIT.", StringComparison.OrdinalIgnoreCase));
        var context = anchor != null
            ? System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(anchor)
            : System.Runtime.Loader.AssemblyLoadContext.Default;
        return (context ?? System.Runtime.Loader.AssemblyLoadContext.Default).LoadFromAssemblyPath(path);
#endif
    }

    private static string DescribeContext(Assembly asm)
    {
#if NETFRAMEWORK
        return asm.Location;
#else
        var ctx = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm);
        return $"{asm.Location}; context={ctx?.Name ?? "(unknown)"}";
#endif
    }

    /// <summary>AIT's dependencies (AIT.*.Core, Newtonsoft, EPPlus, Telerik, …) live only in
    /// the AIT folder, which the default load context doesn't probe — resolve by simple name
    /// from that folder only (returning null otherwise), so this can't hijack binds that
    /// belong to Revit, this add-in, or anything else sharing the process.</summary>
    private static void HookResolver()
    {
        if (_resolverHooked) return;
        _resolverHooked = true;
#if NETFRAMEWORK
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            ProbeLoad(new AssemblyName(args.Name).Name + ".dll");
#else
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
            ProbeLoad(name.Name + ".dll");
#endif
    }

    private static Assembly? ProbeLoad(string fileName)
    {
        if (string.IsNullOrEmpty(_aitDir)) return null;
        string candidate = Path.Combine(_aitDir, fileName);
        if (!File.Exists(candidate)) return null;
        try { return Assembly.LoadFrom(candidate); } catch { return null; }
    }
}
