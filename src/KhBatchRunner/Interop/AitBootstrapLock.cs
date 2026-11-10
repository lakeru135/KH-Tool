namespace KhBatchRunner.Interop;

/// <summary>
/// Serializes the AIT <b>bootstrap</b> — assembly load, checkset load, and the
/// <c>RevitIniService</c> construction — across concurrent Revit instances. The check run itself is
/// deliberately NOT held: that is the part worth parallelizing, and holding it would turn an
/// auto-split Interop run back into a serial one wearing a costume.
///
/// <b>What the 1.3.7 investigation actually found</b> (evidence, not assumption — the 1.3.4 crash
/// was never root-caused, so it matters which of these is fact):
/// <list type="bullet">
/// <item><b>Shared, per user:</b> AIT keeps its settings in ONE file per Revit year,
/// <c>%APPDATA%\Autodesk\Rx\Revit Interoperability &lt;year&gt;\Revit.ini</c>, and every Revit
/// process on the machine reads that same file. <c>DocCheckRunner</c> is handed an
/// <c>IRevitIniService</c> over it, and <c>AIT.Common.API</c> does contain an ini WRITER
/// ("File path was null in ini writer").</item>
/// <item><b>Not shared:</b> the Model Checker's temp usage goes through
/// <c>Path.GetTempPath()</c>, and Revit gives every process its own GUID-named temp directory, so
/// concurrent instances do not collide there (the same fact that forced
/// <see cref="Diagnostics.RunTrace"/> out of %TEMP%). No writable shared database appears in the
/// check path — <c>LocalDbFolders</c> is a settings getter.</item>
/// <item><b>Therefore:</b> the INI is a <i>weak</i> crash candidate. The check path reads it; the
/// writer belongs to the settings/configurator surface, not to a run. This lock is cheap
/// insurance, not a proven fix, and it should not be described as one.</item>
/// </list>
///
/// The more plausible thing it guards is the reflection bootstrap itself: several cold processes
/// resolving and JIT-loading the same large AIT assemblies at the same instant, which is exactly
/// when the groups of a split run are all starting.
/// </summary>
internal static class AitBootstrapLock
{
    /// <summary>Long enough that a genuinely slow first load elsewhere is waited out, short enough
    /// that a wedged holder costs one model rather than the run.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs <paramref name="bootstrap"/> while holding the machine-wide AIT lock for this Revit
    /// year. Fail-soft in both directions: a lock that cannot be created, and a wait that times
    /// out, both fall through and run anyway — refusing to check a model because a mutex was busy
    /// would be a worse outcome than the contention it is avoiding.
    /// </summary>
    public static void Run(string revitVersionNumber, Action bootstrap)
    {
        Mutex? mutex = null;
        bool held = false;
        try
        {
            // Global\ so instances in different sessions still contend; per year because AIT
            // installs and loads independently per Revit year.
            mutex = new Mutex(false, @"Global\KhBatchRunner_AitBootstrap_" + revitVersionNumber);
            try { held = mutex.WaitOne(Timeout); }
            catch (AbandonedMutexException) { held = true; } // holder was killed mid-bootstrap
        }
        catch
        {
            // Name rejected, or no permission for Global\ under a locked-down policy. Proceed
            // unguarded — this is defence in depth, not a correctness requirement.
        }

        try
        {
            bootstrap();
        }
        finally
        {
            if (mutex != null)
            {
                try { if (held) mutex.ReleaseMutex(); } catch { /* best effort */ }
                mutex.Dispose();
            }
        }
    }
}
