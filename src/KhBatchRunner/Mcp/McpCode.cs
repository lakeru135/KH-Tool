#if !EDITION_DRW
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Mcp;

/// <summary>
/// The AI Bridge's <c>run_code</c> (the user, 2026-10-22: "add the tool that you may run the code
/// without I need to click"). KhMcp.exe compiles the AI's C# outside Revit against the assemblies
/// this Revit has loaded (<see cref="Refs"/>), and sends the DLL; it is loaded and run here, on
/// the API thread, inside one undoable step.
///
/// <para>Its own switch, AI Code, beside AI Bridge: off at every Revit start, so code runs without
/// asking only while the user has said so this session. Every run, refused or not, is logged to
/// <see cref="LogFolder"/>.</para>
/// </summary>
internal static class McpCode
{
    /// <summary>The AI Code toggle. Off at every start.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>The ribbon button, so the toggle can say whether it is on. Set by <see cref="App"/>.</summary>
    public static PushButton? Button { get; set; }

    private const string OnText = "AI Code\nON";
    private const string OffText = "AI Code\nOFF";

    public static string CaptionFor(bool on) => on ? OnText : OffText;

    public static void Toggle()
    {
        Enabled = !Enabled;
        if (Button != null) Button.ItemText = CaptionFor(Enabled);
    }

    /// <summary>Test-rig hook, as <see cref="McpBridge.AutoStartIfAsked"/>: KHMCP_CODE=1 turns AI
    /// Code on at startup.</summary>
    public static void AutoStartIfAsked()
    {
        if (Environment.GetEnvironmentVariable("KHMCP_CODE") != "1") return;
        Enabled = true;
        if (Button != null) Button.ItemText = CaptionFor(true);
    }

    public static string LogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KH-Tool", "ai-code");

    public const string RefusedText =
        "AI Code is OFF. Ask the user to turn it on in Revit: KH-Tool tab > Setting > AI Code (it is off at every Revit start).";

    /// <summary>What KhMcp.exe compiles against: this Revit's version, whether AI Code is on, and
    /// the file of every managed assembly the code may use: those loaded here, and on .NET 8 the
    /// whole shared runtime (its facades, such as System.Runtime, are often not loaded yet).</summary>
    public static JsonObject Refs(UIApplication app)
    {
        _refs ??= FindRefs();
        return new JsonObject
        {
            ["revit"] = app.Application.VersionNumber,
            ["enabled"] = Enabled,
            ["refused"] = Enabled ? null : RefusedText,
            ["refs"] = new JsonArray(_refs.Select(r => (JsonNode)r).ToArray())
        };
    }

    private static List<string>? _refs;

    private static List<string> FindRefs()
    {
        var byName = new Dictionary<string, (Version V, string Path)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(path); }
            catch { return; }   // a native DLL
            Version v = name.Version ?? new Version();
            if (!byName.TryGetValue(name.Name ?? path, out var had) || had.V < v) byName[name.Name ?? path] = (v, path);
        }
#if !NETFRAMEWORK
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (string f in Directory.GetFiles(runtime, "*.dll")) Add(f);
#endif
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic) continue;
            string location;
            try { location = a.Location; }
            catch { continue; }
            if (location.Length > 0 && File.Exists(location)) Add(location);
        }
        return byName.Values.Select(x => x.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Runs the compiled code: <c>KhAiCode.Run(UIApplication, Document, TextWriter)</c>. Its return
    /// value (as text), what it wrote to the log writer, and how long it took. Called inside the
    /// bridge's TransactionGroup, so everything it commits is one Ctrl+Z.
    /// </summary>
    public static JsonObject Run(UIApplication app, Document? doc, byte[] assembly, string title, string code)
    {
        if (!Enabled)
        {
            Log(title, code, "REFUSED (AI Code off)");
            throw new McpTools.McpError(RefusedText);
        }
        var output = new StringWriter();
        var watch = Stopwatch.StartNew();
#if NETFRAMEWORK
        // .NET Framework cannot unload it: each run leaves a few KB behind until Revit closes.
        Assembly asm = Assembly.Load(assembly);
#else
        var context = new System.Runtime.Loader.AssemblyLoadContext("KH AI code", isCollectible: true);
        Assembly asm = context.LoadFromStream(new MemoryStream(assembly));
#endif
        try
        {
            MethodInfo run = asm.GetType("KhAiCode")?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                             ?? throw new McpTools.McpError("The compiled code has no KhAiCode.Run.");
            object? value;
            try { value = run.Invoke(null, new object[] { app, doc, output }); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                Exception e = ex.InnerException;
                // Its own lines only: the frames inside KhAiCode, with the line numbers of the AI's code.
                string where = string.Join("\n", (e.StackTrace ?? "").Split('\n').Where(l => l.Contains("KhAiCode")).Select(l => l.Trim()));
                string text = $"{e.GetType().Name}: {e.Message}" + (where.Length > 0 ? "\n" + where : "")
                              + (output.ToString().Length > 0 ? "\nlog before it failed:\n" + output : "");
                Log(title, code, "ERROR " + text);
                throw new McpTools.McpError(text);
            }
            string result = Describe(value);
            Log(title, code, $"OK {watch.Elapsed.TotalSeconds:0.0} s\n{result}\n{output}");
            return new JsonObject
            {
                ["result"] = result,
                ["log"] = output.ToString(),
                ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2)
            };
        }
        finally
        {
#if !NETFRAMEWORK
            context.Unload();
#endif
        }
    }

    /// <summary>A return value as text: a list one item per line, else ToString.</summary>
    private static string Describe(object? value) => value switch
    {
        null => "",
        string s => s,
        System.Collections.IEnumerable list => string.Join("\n", list.Cast<object?>().Select(o => o?.ToString() ?? "null")),
        _ => value.ToString() ?? ""
    };

    private static void Log(string title, string code, string outcome)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            File.AppendAllText(Path.Combine(LogFolder, DateTime.Now.ToString("yyyyMMdd") + ".log"),
                $"==== {DateTime.Now:HH:mm:ss} {title}\n{code}\n---- {outcome}\n\n");
        }
        catch { /* the log never stops a run */ }
    }
}
#endif
