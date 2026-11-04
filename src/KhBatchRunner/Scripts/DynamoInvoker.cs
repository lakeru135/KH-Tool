using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Scripts;

/// <summary>
/// Reflection bridge to Dynamo for Revit's journal-automation API
/// (<c>Dynamo.Applications.DynamoRevit.ExecuteCommand</c>): opens a <c>.dyn</c>, runs it
/// headless (no Dynamo window), and shuts the Dynamo model down so the next invocation gets a
/// fresh one. Loaded at runtime from the running Revit's own <c>AddIns\DynamoForRevit</c>
/// folder — deliberately no compile-time Dynamo reference, because each Revit year ships its
/// own Dynamo (2024 → 2.x on net48, 2025/2026 → 3.x on net8) while the tiny surface used here
/// (ExecuteCommand + the dyn* journal keys) is stable across both.
///
/// NOTE: Dynamo executes a graph against the ACTIVE document (its RevitDynamoModel binds to
/// <c>UIApplication.ActiveUIDocument</c>) — a background-opened document
/// (<c>Application.OpenDocumentFile</c>, no window) is not what the graph will see. Callers
/// must open-and-activate each model first (the modal-runner path), not the background/Idling
/// open the C# actions use.
/// </summary>
public static class DynamoInvoker
{
    private static Type? _dynamoRevitType;
    private static Type? _commandDataType;
    private static string _dynamoDir = "";
    private static bool _resolverHooked;

    /// <summary>Optional diagnostic sink — each invocation step is reported here (the self-test
    /// wires its log in) so a hang inside Dynamo can be located. No-op by default.</summary>
    public static Action<string>? Trace { get; set; }

    private static void Note(string msg) => Trace?.Invoke("[DynamoInvoker] " + msg);

    /// <summary>Runs one <c>.dyn</c> against the current active document. Returns Dynamo's
    /// command result ("Succeeded"/"Failed"/"Cancelled"); throws if Dynamo isn't installed,
    /// the graph file is missing, or the run itself throws.</summary>
    public static string RunGraph(UIApplication uiApp, string dynPath)
    {
        if (!File.Exists(dynPath))
            throw new FileNotFoundException($"Dynamo script not found: {dynPath}", dynPath);
        EnsureLoaded();

        // Journal automation only *evaluates* a graph saved with RunType=Automatic; a Manual
        // graph opens and sits idle (yet ExecuteCommand still returns "Succeeded"), so the run
        // silently does nothing. Run against a patched Automatic copy when needed — the team's
        // library graphs are saved Manual (the interactive default) and must not be touched.
        string runPath = EnsureAutomaticRunType(dynPath);
        try
        {
            Note("creating DynamoRevitCommandData");
            object cmdData = Activator.CreateInstance(_commandDataType!)!;
            _commandDataType!.GetProperty("Application")!.SetValue(cmdData, uiApp);
            _commandDataType.GetProperty("JournalData")!.SetValue(cmdData, new Dictionary<string, string>
            {
                ["dynPath"] = runPath,         // graph to open
                ["dynShowUI"] = "false",       // never show the Dynamo window
                ["dynAutomation"] = "true",    // automation (headless) mode
                ["dynPathExecute"] = "true",   // execute the opened graph
                ["dynModelShutDown"] = "true", // dispose the Dynamo model after the run (fresh per model)
            });

            object dynamoRevit = Activator.CreateInstance(_dynamoRevitType!)!;
            try
            {
                Note("invoking DynamoRevit.ExecuteCommand…");
                object? result = _dynamoRevitType!.GetMethod("ExecuteCommand")!
                    .Invoke(dynamoRevit, new[] { cmdData });
                Note("ExecuteCommand returned " + (result?.ToString() ?? "(null)"));
                return result?.ToString() ?? "(null)";
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException; // surface Dynamo's own error, not the reflection wrapper
            }
        }
        finally
        {
            if (!ReferenceEquals(runPath, dynPath))
                TryDeleteTempCopy(runPath);
        }
    }

    /// <summary>
    /// Journal automation runs a graph on open ONLY when it was saved with
    /// <c>View.Dynamo.RunType = "Automatic"</c>; a Manual-saved graph opens but never evaluates
    /// (and <c>ExecuteCommand</c> still reports success), so the batch silently no-ops. The
    /// team's library graphs are saved Manual — the normal interactive default — so this returns
    /// a path that WILL auto-run: the original when it's already Automatic, otherwise a patched
    /// copy under <c>%temp%\KhBatchRunner\dyn\&lt;guid&gt;\</c> that keeps the original filename
    /// (so any log the graph writes keyed off its own name stays recognisable). If the file can't
    /// be read/parsed, the original path is returned unchanged — no worse than before this shim.
    /// The user's file is never modified. The <c>View.Dynamo.RunType</c> key is present in both
    /// Dynamo 2.x (Revit 2024) and 3.x (2025/2026) graph JSON, so one path covers all targets.
    /// </summary>
    private static string EnsureAutomaticRunType(string dynPath)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(dynPath));
            if (root?["View"]?["Dynamo"] is not JsonObject dynamoObj)
            {
                Note($"no View.Dynamo section in {Path.GetFileName(dynPath)}; running original");
                return dynPath;
            }

            string? runType = (dynamoObj["RunType"] as JsonValue)?.GetValue<string>();
            if (string.Equals(runType, "Automatic", StringComparison.OrdinalIgnoreCase))
                return dynPath; // already auto-runs — nothing to do

            dynamoObj["RunType"] = "Automatic";
            string tempDir = Path.Combine(
                Path.GetTempPath(), "KhBatchRunner", "dyn", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, Path.GetFileName(dynPath));
            File.WriteAllText(tempPath, root!.ToJsonString());
            Note($"RunType '{runType ?? "(none)"}' → Automatic; patched temp copy {tempPath}");
            return tempPath;
        }
        catch (Exception ex)
        {
            Note($"could not patch RunType for {Path.GetFileName(dynPath)} ({ex.Message}); running original");
            return dynPath;
        }
    }

    /// <summary>Best-effort delete of a patched-copy folder created by
    /// <see cref="EnsureAutomaticRunType"/>. Guarded to only ever remove paths under our temp
    /// staging root so a bad call can't delete anything else.</summary>
    private static void TryDeleteTempCopy(string tempPath)
    {
        try
        {
            string stagingRoot = Path.Combine(Path.GetTempPath(), "KhBatchRunner", "dyn");
            string? dir = Path.GetDirectoryName(tempPath);
            if (dir != null && dir.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort — temp files get cleaned up eventually */ }
    }

    private static void EnsureLoaded()
    {
        if (_dynamoRevitType != null && _commandDataType != null) return;

        string revitDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
        _dynamoDir = Path.Combine(revitDir, "AddIns", "DynamoForRevit");
        // Dynamo 2.x and 3.x both keep the Revit host assembly in the Revit\ subfolder; fall
        // back to the root for older layouts.
        string dsPath = Path.Combine(_dynamoDir, "Revit", "DynamoRevitDS.dll");
        if (!File.Exists(dsPath)) dsPath = Path.Combine(_dynamoDir, "DynamoRevitDS.dll");
        if (!File.Exists(dsPath))
            throw new FileNotFoundException(
                $"Dynamo for Revit not found under {_dynamoDir} — is Dynamo installed with this Revit?", dsPath);

        HookResolver();
        Note("loading " + dsPath);
        Assembly ds = Assembly.LoadFrom(dsPath);
        Note("loaded " + ds.FullName);
        _dynamoRevitType = ds.GetType("Dynamo.Applications.DynamoRevit", throwOnError: true)!;
        _commandDataType = ds.GetType("Dynamo.Applications.DynamoRevitCommandData", throwOnError: true)!;
        Note("types resolved");
    }

    /// <summary>
    /// Dynamo's dependencies are split between <c>DynamoForRevit\</c> and its <c>Revit\</c>
    /// subfolder, and the default load context doesn't probe either — resolve by simple name
    /// from those two folders only (returning null otherwise), so this can't hijack binds that
    /// belong to Revit, this add-in, or anything else sharing the process.
    /// </summary>
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
        if (string.IsNullOrEmpty(_dynamoDir)) return null;
        foreach (string dir in new[] { Path.Combine(_dynamoDir, "Revit"), _dynamoDir })
        {
            string candidate = Path.Combine(dir, fileName);
            if (!File.Exists(candidate)) continue;
            try { return Assembly.LoadFrom(candidate); } catch { /* try the next folder */ }
        }
        return null;
    }
}
