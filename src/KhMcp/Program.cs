using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Web.Script.Serialization;
using KhBatchRunner.Mcp;

namespace KhMcp;

/// <summary>
/// A minimal MCP server over stdio (newline-delimited JSON-RPC 2.0): initialize, ping, tools/list,
/// tools/call. Tool calls go to KH-Tool's AI Bridge over the pipe in <see cref="McpContract"/>.
/// Diagnostics go to stderr only — stdout is the protocol.
/// </summary>
internal static class Program
{
    private const string LatestProtocol = "2025-06-18";

    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

    private sealed class RpcError : Exception
    {
        public int Code { get; }
        public RpcError(int code, string message) : base(message) => Code = code;
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
        if (args.Length > 0 && args[0] == "--schemas") return CheckSchemas() ? 0 : 1;
        if (args.Length > 1 && args[0] == "--compile-test") return CompileTest(args[1], args.Length > 2 ? args[2] : null);

        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            Dictionary<string, object?> message;
            try
            {
                message = Json.Deserialize<Dictionary<string, object?>>(line);
            }
            catch (Exception ex)
            {
                Write(output, Error(null, -32700, "Parse error: " + ex.Message));
                continue;
            }

            message.TryGetValue("id", out object? id);
            string? method = message.TryGetValue("method", out object? m) ? m as string : null;
            if (method == null || id == null) continue;   // a response or a notification: nothing to answer

            message.TryGetValue("params", out object? p);
            var parameters = p as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            try
            {
                Write(output, new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Handle(method, parameters) });
            }
            catch (RpcError ex)
            {
                Write(output, Error(id, ex.Code, ex.Message));
            }
            catch (Exception ex)
            {
                Write(output, Error(id, -32603, ex.GetType().Name + ": " + ex.Message));
            }
        }
        return 0;
    }

    private static void Write(StreamWriter output, object message) => output.WriteLine(Json.Serialize(message));

    private static Dictionary<string, object?> Error(object? id, int code, string text) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = text }
    };

    private static object Handle(string method, Dictionary<string, object?> p)
    {
        switch (method)
        {
            case "initialize":
                return new Dictionary<string, object?>
                {
                    ["protocolVersion"] = p.TryGetValue("protocolVersion", out object? v) && v is string s ? s : LatestProtocol,
                    ["capabilities"] = new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?>() },
                    ["serverInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = McpContract.ServerName,
                        ["version"] = typeof(Program).Assembly.GetName().Version?.ToString()
                    },
                    ["instructions"] = McpContract.Instructions
                };
            case "ping":
                return new Dictionary<string, object?>();
            case "tools/list":
                return new Dictionary<string, object?>
                {
                    ["tools"] = McpContract.Tools.Select(t => new Dictionary<string, object?>
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = Json.DeserializeObject(t.InputSchema),
                        ["annotations"] = new Dictionary<string, object?>
                        {
                            ["readOnlyHint"] = !t.Modifies,
                            ["destructiveHint"] = false
                        }
                    }).ToList()
                };
            case "tools/call":
                string name = p.TryGetValue("name", out object? n) ? n as string ?? "" : "";
                if (!McpContract.Tools.Any(t => t.Name == name)) throw new RpcError(-32602, "Unknown tool: " + name);
                p.TryGetValue("arguments", out object? arguments);
                return CallTool(name, arguments ?? new Dictionary<string, object?>());
            default:
                throw new RpcError(-32601, "Method not found: " + method);
        }
    }

    private static Dictionary<string, object?> CallTool(string name, object arguments)
    {
        var content = new List<object>();
        bool isError;
        try
        {
            if (name == "run_code" && Compiled(arguments) is { } refused)
                return new Dictionary<string, object?> { ["content"] = new List<object> { Text(refused) }, ["isError"] = true };
            string reply = Bridge(Json.Serialize(new Dictionary<string, object?> { ["tool"] = name, ["args"] = arguments }));
            var r = Json.Deserialize<Dictionary<string, object?>>(reply);
            isError = !(r.TryGetValue("ok", out object? ok) && ok is true);
            if (isError)
            {
                content.Add(Text(r.TryGetValue("error", out object? e) ? e as string ?? "Unknown error" : "Unknown error"));
            }
            else
            {
                r.TryGetValue("result", out object? result);
                content.Add(Text(Json.Serialize(result)));
                // export_view_png: send the picture itself, so the AI can look at its work.
                if (name == "export_view_png" && result is Dictionary<string, object?> d
                    && d.TryGetValue("path", out object? path) && path is string file && File.Exists(file))
                {
                    content.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "image",
                        ["mimeType"] = "image/png",
                        ["data"] = Convert.ToBase64String(File.ReadAllBytes(file))
                    });
                }
            }
        }
        catch (BridgeUnavailable ex)
        {
            isError = true;
            content.Add(Text(ex.Message));
        }
        return new Dictionary<string, object?> { ["content"] = content, ["isError"] = isError };
    }

    private static Dictionary<string, object?> Text(string text) => new() { ["type"] = "text", ["text"] = text };

    /// <summary>
    /// run_code, before it goes to the bridge: asks the bridge what to compile against (and whether
    /// AI Code is on), compiles the code, and puts the DLL into <paramref name="arguments"/> as
    /// "assembly" (base64). Null when it is ready to send; otherwise what to tell the AI.
    /// </summary>
    private static string? Compiled(object arguments)
    {
        if (arguments is not Dictionary<string, object?> args || !args.TryGetValue("code", out object? c) || c is not string code || code.Trim().Length == 0)
            return "run_code needs 'code': the body of Run(UIApplication app, Document doc, TextWriter log).";
        var r = Json.Deserialize<Dictionary<string, object?>>(Bridge(Json.Serialize(new Dictionary<string, object?>
        {
            ["tool"] = McpContract.CodeRefs,
            ["args"] = new Dictionary<string, object?>()
        })));
        if (!(r.TryGetValue("ok", out object? ok) && ok is true) || r["result"] is not Dictionary<string, object?> info)
            return r.TryGetValue("error", out object? e) ? e as string ?? "The bridge did not answer code_refs." : "The bridge did not answer code_refs.";
        if (info.TryGetValue("enabled", out object? on) && on is not true)
            return info.TryGetValue("refused", out object? why) && why is string s ? s : "AI Code is OFF in Revit.";
        var refs = (info["refs"] as System.Collections.ArrayList ?? new System.Collections.ArrayList()).Cast<object>().Select(o => o.ToString()).ToList();
        var usings = (args.TryGetValue("usings", out object? u) ? u as System.Collections.ArrayList : null)?.Cast<object>().Select(o => o.ToString()) ?? Enumerable.Empty<string>();
        (byte[]? dll, string errors) = CodeCompiler.Compile(code, usings, refs);
        if (dll == null) return errors;
        args["assembly"] = Convert.ToBase64String(dll);
        return null;
    }

    private sealed class BridgeUnavailable : Exception
    {
        public BridgeUnavailable(string message) : base(message) { }
    }

    /// <summary>One request, one reply line. No read timeout: an Enlarge may run for minutes, and the
    /// bridge answers a stuck Revit with its own timeout reply.</summary>
    private static string Bridge(string requestJson)
    {
        using var pipe = new NamedPipeClientStream(".", McpContract.PipeName(), PipeDirection.InOut);
        try
        {
            // Generous: the bridge serves one client at a time, and the previous call's connection
            // can take a moment to close (seen live: a call right after Datum Dimension missed 3 s).
            pipe.Connect(15000);
        }
        catch (TimeoutException)
        {
            throw new BridgeUnavailable(
                "KH-Tool's AI Bridge is not running. In Revit: KH-Tool tab → Setting → AI Bridge (turn it on), " +
                "then try again. Revit must be open with KH-Tool loaded.");
        }
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        writer.WriteLine(requestJson);
        return reader.ReadLine() ?? throw new BridgeUnavailable("The AI Bridge closed the connection without replying.");
    }

    /// <summary><c>KhMcp.exe --schemas</c>: the installer build's gate — every tool schema parses
    /// and every tool name is unique. Needs no Revit.</summary>
    private static bool CheckSchemas()
    {
        bool ok = true;
        foreach (McpContract.ToolDef t in McpContract.Tools)
        {
            try { Json.DeserializeObject(t.InputSchema); }
            catch (Exception ex) { Console.WriteLine($"{t.Name}: schema does not parse: {ex.Message}"); ok = false; }
        }
        foreach (string dup in McpContract.Tools.GroupBy(t => t.Name).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            Console.WriteLine($"{dup}: listed twice");
            ok = false;
        }
        Console.WriteLine(ok ? $"{McpContract.Tools.Length} tool schemas OK" : "tool schemas FAILED");
        return ok;
    }

    /// <summary>
    /// <c>KhMcp.exe --compile-test &lt;Revit folder&gt; [runtime folder]</c>: run_code's compiler with
    /// no Revit running — a sample that must compile and one whose error must come back on its own
    /// line 2. References: RevitAPI(UI).dll from the Revit folder and every DLL in the runtime folder
    /// (default: this .NET Framework's, right for Revit 2024; for 2025+ pass the .NET 8 shared runtime).
    /// </summary>
    private static int CompileTest(string revitDir, string? runtimeDir)
    {
        runtimeDir ??= Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = Directory.GetFiles(runtimeDir, "*.dll").Concat(new[] { "RevitAPI.dll", "RevitAPIUI.dll" }.Select(f => Path.Combine(revitDir, f))).ToList();
        (byte[]? dll, string errors) = CodeCompiler.Compile(
            "var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();\nlog.WriteLine(app.Application.VersionNumber);\nreturn walls;",
            Array.Empty<string>(), refs);
        bool ok = dll != null;
        Console.WriteLine(ok ? $"good sample: {dll!.Length} bytes" : "good sample FAILED:\n" + errors);
        (byte[]? bad, string badErrors) = CodeCompiler.Compile("int a = 1;\nint b = \"two\";\nreturn a;",Array.Empty<string>(), refs);
        bool line2 = bad == null && badErrors.Contains("line 2,");
        Console.WriteLine(line2 ? "bad sample: error on line 2 as expected" : "bad sample FAILED: " + badErrors);
        return ok && line2 ? 0 : 1;
    }

    /// <summary><c>KhMcp.exe --selftest</c>: proves the tool schemas parse and the bridge answers.</summary>
    private static int SelfTest()
    {
        foreach (McpContract.ToolDef t in McpContract.Tools) Json.DeserializeObject(t.InputSchema);
        Console.WriteLine($"{McpContract.Tools.Length} tool schemas OK; pipe {McpContract.PipeName()}");
        try
        {
            Console.WriteLine(Bridge(Json.Serialize(new Dictionary<string, object?>
            {
                ["tool"] = "kh_status",
                ["args"] = new Dictionary<string, object?>()
            })));
            return 0;
        }
        catch (BridgeUnavailable ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }
    }
}
