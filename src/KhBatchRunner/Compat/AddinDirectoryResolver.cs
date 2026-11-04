#if NETFRAMEWORK
using System;
using System.IO;
using System.Reflection;

namespace KhBatchRunner.Compat;

/// <summary>
/// net48 (Revit 2024) only. Revit 2024 runs every add-in in one shared .NET Framework
/// AppDomain, and it ships no <c>System.Text.Json</c> (nor a binding redirect for it) — so
/// our bundled <c>System.Text.Json.dll</c> and its transitives must be resolved from our own
/// add-in folder. Without this, if another add-in has already loaded a different
/// <c>System.Text.Json</c> version, our strong-named bind fails outright.
///
/// The handler probes only this add-in's own directory by simple name, so it can only ever
/// resolve assemblies we physically shipped (the file-exists check is the implicit allow-list),
/// and it ignores the requested version — the binding-redirect emulation an add-in can't get
/// by editing <c>Revit.exe.config</c>. Anything it doesn't ship falls through to Revit's
/// normal resolution by returning null.
///
/// .NET 8 (Revit 2025/2026) resolves dependencies via <c>KhBatchRunner.deps.json</c>, so this
/// whole file is compiled out there.
/// </summary>
internal static class AddinDirectoryResolver
{
    private static bool _registered;
    private static readonly object Gate = new object();

    private static readonly string AddinDir =
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;

    /// <summary>Idempotent — safe to call more than once.</summary>
    internal static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            _registered = true;
        }
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            string simpleName = new AssemblyName(args.Name).Name;
            string candidate = Path.Combine(AddinDir, simpleName + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }
        catch
        {
            return null; // Never let a resolver throw — fall through to Revit's own resolution.
        }
    }
}
#endif
