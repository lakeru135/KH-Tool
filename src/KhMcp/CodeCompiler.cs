using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KhMcp;

/// <summary>
/// run_code's compiler: the AI's method body, wrapped in <c>KhAiCode.Run</c>, compiled to a DLL in
/// memory against the assemblies the Revit it will run in has (the bridge's code_refs). Here, out
/// of Revit, so Roslyn and its dependencies never meet the versions Revit and other add-ins load.
/// </summary>
internal static class CodeCompiler
{
    public static readonly string[] DefaultUsings =
    {
        "System", "System.Linq", "System.Collections.Generic", "System.IO", "System.Text",
        "Autodesk.Revit.DB", "Autodesk.Revit.UI"
    };

    /// <summary>Metadata per reference file, kept while KhMcp runs (reading RevitAPI.dll's
    /// metadata is the slow part of a compile).</summary>
    private static readonly Dictionary<string, MetadataReference> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The source compiled: usings, then the body under <c>#line 1</c> so errors carry the
    /// AI's own line numbers. The trailing <c>return null;</c> lets a body without a return compile
    /// (one that returns gets only an unreachable-code warning).</summary>
    public static string Wrap(string code, IEnumerable<string> usings) =>
        string.Concat(DefaultUsings.Concat(usings).Distinct().Select(u => $"using {u.Trim().TrimEnd(';')};\n")) +
        "public static class KhAiCode\n{\n" +
        "    public static object Run(UIApplication app, Document doc, TextWriter log)\n    {\n" +
        "#line 1\n" + code + "\n#line hidden\n        return null;\n    }\n}\n";

    /// <summary>The DLL, or the errors (line: message, one per line) and null.</summary>
    public static (byte[]? Dll, string Errors) Compile(string code, IEnumerable<string> usings, IEnumerable<string> refs)
    {
        var references = new List<MetadataReference>();
        foreach (string file in refs)
        {
            if (!Cache.TryGetValue(file, out MetadataReference? r))
            {
                // Only managed assemblies: a runtime folder also holds native DLLs (coreclr.dll),
                // which Roslyn reports as CS0009 errors.
                try
                {
                    System.Reflection.AssemblyName.GetAssemblyName(file);
                    r = MetadataReference.CreateFromFile(file);
                }
                catch { continue; }
                Cache[file] = r;
            }
            references.Add(r);
        }
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Wrap(code, usings), new CSharpParseOptions(LanguageVersion.CSharp12));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "KhAiCode_" + Guid.NewGuid().ToString("N"), new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Disable));
        using var dll = new MemoryStream();
        var result = compilation.Emit(dll);
        if (result.Success) return (dll.ToArray(), "");
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20).Select(d =>
        {
            FileLinePositionSpan at = d.Location.GetMappedLineSpan();
            string where = at.HasMappedPath || d.Location.IsInSource ? $"line {at.StartLinePosition.Line + 1}, col {at.StartLinePosition.Character + 1}: " : "";
            return $"{where}{d.Id} {d.GetMessage()}";
        });
        return (null, "Compile errors (nothing ran):\n" + string.Join("\n", errors));
    }
}
