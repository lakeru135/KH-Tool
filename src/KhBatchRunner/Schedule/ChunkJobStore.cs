using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Persists EPHEMERAL auto-split chunk jobs as JSON under
/// <c>%AppData%\KhBatchRunner\Scheduled\chunks\*.json</c> — a subfolder deliberately separate
/// from <see cref="ScheduleStore"/>'s top-level folder so chunk jobs never appear in the user's
/// saved-schedule list (<see cref="ScheduleStore.LoadAll"/> is non-recursive) and are never
/// picked up as real scheduled tasks. Each chunk Revit reads its job here by id, runs it, then
/// deletes its own file (<see cref="Delete"/>). A best-effort <see cref="SweepOrphans"/> clears
/// files a killed instance left behind.
/// </summary>
public static class ChunkJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ChunkDirectory
    {
        get
        {
            string dir = Path.Combine(ScheduleStore.ScheduledDirectory, "chunks");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Save(ScheduledJob job) =>
        File.WriteAllText(PathFor(job.Id), JsonSerializer.Serialize(job, JsonOptions));

    /// <summary>Loads a chunk job by id and marks it ephemeral so the runner self-deletes it
    /// (instead of stamping it back to the schedule store). Null if the file is gone/malformed.</summary>
    public static ScheduledJob? GetById(string id)
    {
        string path = PathFor(id);
        if (!File.Exists(path)) return null;
        try
        {
            ScheduledJob? job = JsonSerializer.Deserialize<ScheduledJob>(File.ReadAllText(path), JsonOptions);
            if (job == null || string.IsNullOrWhiteSpace(job.Id)) return null;
            job.IsEphemeralChunk = true;
            return job;
        }
        catch { return null; }
    }

    public static void Delete(string id)
    {
        try
        {
            string path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best effort — a stale file is swept later */ }
    }

    /// <summary>Deletes chunk files older than <paramref name="maxAge"/> (default 12h) — cleanup for
    /// any chunk whose Revit was force-quit before it could delete its own file.</summary>
    public static void SweepOrphans(TimeSpan? maxAge = null)
    {
        TimeSpan age = maxAge ?? TimeSpan.FromHours(12);
        try
        {
            foreach (string file in Directory.EnumerateFiles(ChunkDirectory, "*.json"))
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

    private static string PathFor(string id) => Path.Combine(ChunkDirectory, id + ".json");
}
