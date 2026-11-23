using System.IO;
using System.Text.Json;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Schedule;

/// <summary>
/// How much memory Revit held while each model was open for a whole-view export, the last few times
/// this machine ran it — so <see cref="InstanceBudget"/> can budget a run from what its models
/// measured instead of from the flat worst-case figure.
///
/// <para><b>Why.</b> The all-worksets budget is 24 GB per Revit because some models measured 18–49 GB
/// (see <c>InstanceBudget.AllWorksetsPerInstanceGb</c>). MEASURED 2026-11-13: six cloud models
/// exported to NWC in one Revit that peaked at 3.4–4.3 GB, on a 32 GB PC that the flat figure limits
/// to one Revit. The flat figure is right for a model nobody has measured and wrong for these.</para>
///
/// <para><b>What is stored.</b> Per <see cref="ModelKey"/>, the last <see cref="Keep"/> sampled peaks
/// of the Revit process's working set while that model was in hand. The figure includes whatever
/// earlier models of the same instance left behind, so it over-states rather than under-states. The
/// budget reads the HIGHEST of the kept figures: one light run does not erase a heavy one.</para>
///
/// <para>No record is the safe answer everywhere: a model without one keeps the flat budget. Written
/// by the child Revits of a split run at once, so the write is locked like
/// <see cref="Batch.WorksetAudit"/>'s. Never throws.</para>
/// </summary>
internal static class ModelMemory
{
    private const int Keep = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Redirects the store for the offline harness, so a test never reads or writes the
    /// real machine's record.</summary>
    internal static string? StorePathOverride { get; set; }

    public static string StorePath => StorePathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KhBatchRunner", "model-memory.json");

    public sealed class ModelRecord
    {
        public DateTime RecordedUtc { get; set; }
        public List<long> PeakMb { get; set; } = new();
    }

    /// <summary>Adds one measured peak for a model, keeping the most recent <see cref="Keep"/>.</summary>
    public static void Record(string modelKey, long peakMb)
    {
        if (string.IsNullOrEmpty(modelKey) || peakMb <= 0) return;
        try
        {
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Logging.LogFileLock.Write(path, () =>
            {
                Dictionary<string, ModelRecord> all = ReadAll(path);
                if (!all.TryGetValue(modelKey, out ModelRecord? rec))
                {
                    rec = new ModelRecord();
                    all[modelKey] = rec;
                }
                rec.PeakMb.Add(peakMb);
                if (rec.PeakMb.Count > Keep) rec.PeakMb.RemoveRange(0, rec.PeakMb.Count - Keep);
                rec.RecordedUtc = DateTime.UtcNow;
                WriteAtomically(path, JsonSerializer.Serialize(all, JsonOptions));
            });
        }
        catch (Exception ex)
        {
            // A budgeting hint must never be able to fail a run.
            Diagnostics.RunTrace.Mark("model memory could not be recorded: " + ex.Message);
        }
    }

    /// <summary>
    /// The highest kept peak for each model, in the order given; null for a model with no record.
    /// One read of the store for the whole list.
    /// </summary>
    public static List<long?> KnownPeaksMb(IReadOnlyList<CloudModel> models)
    {
        var peaks = new List<long?>(models.Count);
        Dictionary<string, ModelRecord> all;
        try { all = ReadAll(StorePath); }
        catch { all = new Dictionary<string, ModelRecord>(); }

        foreach (CloudModel model in models)
        {
            long? peak = null;
            try
            {
                if (all.TryGetValue(ModelKey.For(model), out ModelRecord? rec) && rec.PeakMb.Count > 0)
                    peak = rec.PeakMb.Max();
            }
            catch { /* unreadable: unknown */ }
            peaks.Add(peak);
        }
        return peaks;
    }

    private static Dictionary<string, ModelRecord> ReadAll(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, ModelRecord>();
            return JsonSerializer.Deserialize<Dictionary<string, ModelRecord>>(
                       File.ReadAllText(path), JsonOptions)
                   ?? new Dictionary<string, ModelRecord>();
        }
        catch { return new Dictionary<string, ModelRecord>(); }
    }

    private static void WriteAtomically(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
