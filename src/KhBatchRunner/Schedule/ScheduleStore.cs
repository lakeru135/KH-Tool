using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Persists scheduled jobs as JSON under <c>%AppData%\KhBatchRunner\Scheduled\*.json</c>
/// (one file per job, named by job id). Read at Revit startup by the scheduled runner.
/// </summary>
public static class ScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ScheduledDirectory
    {
        get
        {
            string dir = Settings.SettingsPaths.Folder("Scheduled");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Where a job's logs go when it names no destination of its own. Lives here, next to the
    /// directory it derives from, rather than on <c>ScheduledRunner</c> — a job's default log
    /// location is a property of the store, and a non-Revit host that composes jobs needs it
    /// without pulling in the Revit-bound runner. <c>ScheduledRunner</c> delegates to this.
    /// </summary>
    public static string DefaultDestinationFolder()
    {
        string dir = Path.Combine(ScheduledDirectory, "logs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static List<ScheduledJob> LoadAll()
    {
        var jobs = new List<ScheduledJob>();
        foreach (string file in Directory.EnumerateFiles(ScheduledDirectory, "*.json"))
        {
            try
            {
                string text = File.ReadAllText(file);
                ScheduledJob? job = TryMigrateLegacy(text) ??
                    JsonSerializer.Deserialize<ScheduledJob>(text, JsonOptions);
                if (job != null && !string.IsNullOrWhiteSpace(job.Id))
                    jobs.Add(job);
            }
            catch { /* skip malformed */ }
        }
        return jobs;
    }

    /// <summary>
    /// Upgrades a pre-<see cref="ScheduledJob.CurrentSchemaVersion"/> JSON file into the current
    /// shape and re-saves it so the migration runs only once. Handles both legacy shapes:
    /// <list type="bullet">
    /// <item>v1 — single ModelSetName/ActionName/ScriptSetName/OpenMode/LogPath fields.</item>
    /// <item>v2 — ModelSetNames + separate ActionNames/ScriptSetNames lists (actions-then-scripts).</item>
    /// </list>
    /// In both cases the ordered <see cref="ScheduledJob.RunItems"/> is rebuilt (actions first,
    /// then script sets — matching how v2 actually ran), since that property is now the stored
    /// source of truth and old files don't carry it. Returns null for files already on the current
    /// schema (caller falls back to the normal deserializer, which reads RunItems directly).
    /// </summary>
    private static ScheduledJob? TryMigrateLegacy(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        int version = root.TryGetProperty("SchemaVersion", out JsonElement v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 1;
        if (version >= ScheduledJob.CurrentSchemaVersion)
            return null; // already current — the normal deserializer reads RunItems

        // Deserialize the common fields with the current serializer (unknown/removed props are
        // ignored), then rebuild RunItems from whichever legacy shape this file used. ActionNames/
        // ScriptSetNames are [JsonIgnore]-computed now, so the deserializer leaves RunItems empty.
        ScheduledJob job = JsonSerializer.Deserialize<ScheduledJob>(json, JsonOptions) ?? new ScheduledJob();

        var items = new List<RunItem>();
        AppendNames(root, "ActionNames", RunItemKind.Action, items);       // v2 arrays
        AppendNames(root, "ScriptSetNames", RunItemKind.ScriptSet, items);
        if (items.Count == 0)                                              // v1 singular fields
        {
            if (root.TryGetProperty("ActionName", out JsonElement a) && a.GetString() is { Length: > 0 } an)
                items.Add(new RunItem(an, RunItemKind.Action));
            if (root.TryGetProperty("ScriptSetName", out JsonElement s) &&
                s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } sn)
                items.Add(new RunItem(sn, RunItemKind.ScriptSet));
        }
        job.RunItems = items;

        // v1-only field renames the current deserializer couldn't pick up (singular ModelSetName,
        // LogPath). v2 already used the current plural names, so these no-op there.
        if (root.TryGetProperty("ModelSetName", out JsonElement modelSetName))
        {
            string name = modelSetName.GetString() ?? "";
            if (job.ModelSetNames.Count == 0 && name.Length > 0)
                job.ModelSetNames = new List<string> { name };
            if (string.IsNullOrEmpty(job.Name)) job.Name = name;
            if (string.IsNullOrEmpty(job.DestinationFolder) && root.TryGetProperty("LogPath", out JsonElement lp))
                job.DestinationFolder = Path.GetDirectoryName(lp.GetString() ?? "") ?? "";
            if (string.IsNullOrEmpty(job.ProjectLogName) && root.TryGetProperty("ActionName", out JsonElement pl))
                job.ProjectLogName = pl.GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(job.Id))
            return null;

        job.SchemaVersion = ScheduledJob.CurrentSchemaVersion;
        Save(job); // upgrade the file in place so this only runs once
        return job;
    }

    private static void AppendNames(JsonElement root, string prop, RunItemKind kind, List<RunItem> items)
    {
        if (root.TryGetProperty(prop, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } n)
                    items.Add(new RunItem(n, kind));
    }

    public static ScheduledJob? GetById(string id) =>
        LoadAll().FirstOrDefault(j => j.Id == id);

    public static void Save(ScheduledJob job) =>
        File.WriteAllText(PathFor(job.Id), JsonSerializer.Serialize(job, JsonOptions));

    /// <summary>
    /// Copies the state that belongs to the SCHEDULE rather than to the editor —
    /// <see cref="ScheduledJob.Enabled"/> and <see cref="ScheduledJob.LastRunAt"/> — from the stored
    /// job of the same id onto a freshly rebuilt one. A no-op for an id that is not stored yet, so a
    /// brand new schedule keeps its defaults.
    ///
    /// <para><b>Why this exists.</b> All four Schedule* windows' <c>TryBuildJob</c> construct a
    /// BRAND NEW <see cref="ScheduledJob"/> on every save, carrying over nothing but the
    /// <see cref="ScheduledJob.Id"/>. Any field the editor has no control for is therefore reset the
    /// moment the job is edited — so without this, opening a paused schedule and pressing Save would
    /// silently re-arm it, and (already true before 1.4.7) would blank its "Last run" column.</para>
    ///
    /// <para>One helper at the one choke point both save paths share, rather than the same two lines
    /// threaded through four copies of <c>TryBuildJob</c> — where the fifth copy is the one that
    /// forgets.</para>
    /// </summary>
    public static void CarryForwardState(ScheduledJob rebuilt)
    {
        ScheduledJob? stored = GetById(rebuilt.Id);
        if (stored == null) return;
        rebuilt.Enabled = stored.Enabled;
        rebuilt.LastRunAt = stored.LastRunAt;
    }

    public static void Delete(string id)
    {
        string path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(string id) => Path.Combine(ScheduledDirectory, id + ".json");
}
