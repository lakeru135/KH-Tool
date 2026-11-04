using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Persists <see cref="RunManifest"/>s as JSON under
/// <c>%AppData%\KhBatchRunner\Scheduled\runs\*.json</c> — a sibling of <see cref="ChunkJobStore"/>'s
/// <c>chunks\</c> folder and, like it, deliberately outside <see cref="ScheduleStore"/>'s top-level
/// directory so a run record can never be mistaken for a saved schedule
/// (<see cref="ScheduleStore.LoadAll"/> is non-recursive).
///
/// Unlike a chunk job, a manifest is NOT deleted when its run finishes: a completed manifest is the
/// record of what ran, and an abandoned one is the only trace of a run whose supervisor was closed
/// mid-way. <see cref="SweepOld"/> ages them out instead.
/// </summary>
public static class RunManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Guards the read-modify-write in <see cref="Update"/> against the supervisor thread
    /// and the launching thread touching the same manifest at once.</summary>
    private static readonly object Gate = new();

    public static string RunsDirectory
    {
        get
        {
            string dir = Path.Combine(ScheduleStore.ScheduledDirectory, "runs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Save(RunManifest manifest)
    {
        lock (Gate)
        {
            try { WriteAtomically(PathFor(manifest.RunId), JsonSerializer.Serialize(manifest, JsonOptions)); }
            catch { /* the run matters more than its bookkeeping */ }
        }
    }

    public static RunManifest? Load(string runId)
    {
        try
        {
            string path = PathFor(runId);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch { return null; }
    }

    /// <summary>Applies <paramref name="mutate"/> to the manifest and writes it back under the lock,
    /// so a supervisor tick and a round launch can't clobber each other's changes.</summary>
    public static void Update(RunManifest manifest, Action<RunManifest> mutate)
    {
        lock (Gate)
        {
            mutate(manifest);
            try { WriteAtomically(PathFor(manifest.RunId), JsonSerializer.Serialize(manifest, JsonOptions)); }
            catch { /* best effort */ }
        }
    }

    public static IReadOnlyList<RunManifest> LoadAll()
    {
        var all = new List<RunManifest>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(RunsDirectory, "*.json"))
            {
                try
                {
                    RunManifest? m = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(file), JsonOptions);
                    if (m != null) all.Add(m);
                }
                catch { /* skip a malformed record rather than failing the whole listing */ }
            }
        }
        catch { /* directory missing — nothing recorded yet */ }
        return all;
    }

    /// <summary>
    /// Marks any manifest still claiming to be <see cref="RunState.Running"/> whose supervising
    /// process is gone as <see cref="RunState.Abandoned"/>. Called at startup: it is what turns
    /// "the user closed Revit during recovery" from silence into a visible, explainable state.
    /// </summary>
    public static void MarkAbandoned()
    {
        foreach (RunManifest m in LoadAll())
        {
            if (m.State != RunState.Running) continue;
            if (m.SupervisorPid != 0 && IsAlive(m.SupervisorPid)) continue;   // genuinely still going
            m.State = RunState.Abandoned;
            Save(m);
        }
    }

    /// <summary>Deletes run records older than <paramref name="maxAge"/> (default 30 days) so the
    /// folder doesn't grow without bound. Longer than the chunk sweep on purpose — these are the
    /// history, not scratch files.</summary>
    public static void SweepOld(TimeSpan? maxAge = null)
    {
        TimeSpan age = maxAge ?? TimeSpan.FromDays(30);
        try
        {
            foreach (string file in Directory.EnumerateFiles(RunsDirectory, "*.json"))
            {
                try
                {
                    if (DateTime.Now - File.GetLastWriteTime(file) > age) File.Delete(file);
                }
                catch { /* skip locked/in-use */ }
            }
        }
        catch { /* directory missing — nothing to sweep */ }
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }   // no such process
    }

    /// <summary>
    /// Writes beside the destination and swaps the complete file into place. Directly truncating
    /// the manifest allowed an abrupt Revit/native crash to leave a correctly-sized file containing
    /// only zero bytes, erasing both the assignment and retry evidence.
    /// </summary>
    private static void WriteAtomically(string path, string json)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort cleanup */ }
        }
    }

    private static string PathFor(string runId) => Path.Combine(RunsDirectory, runId + ".json");
}
