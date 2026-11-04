using System.IO;
using System.Text.Json;

namespace KhBatchRunner.Batch;

/// <summary>
/// What was actually IN each workset, the last time this machine had it open — recorded per model so
/// a later run can leave a workset shut on evidence rather than on a guess.
///
/// <para><b>Why a record at all.</b> An export that does not want links still pays for them: opening
/// a workset that holds a link makes Revit download and open the linked cloud model. MEASURED
/// 2026-08-15 — <c>SITE2A-BLDD-SIGN</c> loaded SEVENTEEN other models, at 5–20 minutes per host, for a
/// run whose options set had <c>ExportLinks = False</c> and <c>ConvertLinkedCADFormats = False</c>.
/// Every one of those links was thrown away after being loaded.</para>
///
/// <para><b>Why it cannot simply be worked out at open time.</b> The obvious version — "look at what
/// is in the workset and skip it if it is only a link" — cannot be evaluated on the engine's
/// worksets-closed open. An element in a closed workset is NOT LOADED, so
/// <see cref="Autodesk.Revit.DB.FilteredElementCollector"/> returns nothing for it, and the test
/// would answer "holds only links" for every workset in the model, including the ones holding all the
/// geometry. That is the exact silent under-open this whole release sequence exists to close: element
/// TYPES stay loaded regardless of workset (which is why <c>RelinkLinksAction</c> can read
/// <c>RevitLinkType.WorksetId</c> on a closed document), but a type tells you a link is THERE, never
/// that it is ALONE.</para>
///
/// <para><b>So it is proved on an open that already happened.</b> Recording costs nothing: the first
/// run opens the workset anyway, and while it is open one collector pass says whether it holds
/// anything but link instances. The saving arrives on the NEXT run. A model with no record behaves
/// exactly as it did before this class existed — no record, no skip.</para>
///
/// <para>Keyed by <see cref="Presets.ModelKey"/> and stored by workset NAME rather than id: the
/// 1.3.8.7 lesson is that names are what survive a reopen, and a workset id means nothing across two
/// separate opens of a detached model.</para>
/// </summary>
internal static class WorksetAudit
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Beside the trail and the toast ledger — machine-local, per user, never roamed.</summary>
    public static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KhBatchRunner", "workset-audit.json");

    /// <summary>One model's findings: workset name → "everything in it was a link".</summary>
    public sealed class ModelRecord
    {
        public DateTime RecordedUtc { get; set; }
        public Dictionary<string, bool> LinkOnly { get; set; } = new();
    }

    /// <summary>
    /// The worksets this model is KNOWN to hold nothing but links in. Empty when there is no record,
    /// which is the safe answer — the caller then opens everything, as before.
    /// </summary>
    public static HashSet<string> LinkOnlyWorksets(string modelKey)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Dictionary<string, ModelRecord> all = ReadAll();
            if (!all.TryGetValue(modelKey, out ModelRecord? rec)) return names;
            foreach (KeyValuePair<string, bool> pair in rec.LinkOnly)
                if (pair.Value) names.Add(pair.Key);
        }
        catch { /* no record is the safe answer */ }
        return names;
    }

    /// <summary>
    /// Merges this run's findings into the store. Only worksets that were OPEN can be judged, so
    /// <paramref name="findings"/> covers those and nothing else — a workset absent from it keeps
    /// whatever was known before rather than being reset to "unknown".
    ///
    /// <para>A workset that GAINS geometry is corrected the next time it is open, which is the run
    /// after the one that stopped skipping it. That is the cost of proving it rather than guessing,
    /// and it is bounded: the wrong answer is a workset opened needlessly, never one left shut.</para>
    /// </summary>
    public static void Record(string modelKey, IReadOnlyDictionary<string, bool> findings)
    {
        if (string.IsNullOrEmpty(modelKey) || findings.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            // Read-modify-write must be atomic against the sibling Revits of a split run, which all
            // finish models at once and all write here. Same lock the shared run CSVs use.
            Logging.LogFileLock.Write(StorePath, () =>
            {
                Dictionary<string, ModelRecord> all = ReadAll();
                if (!all.TryGetValue(modelKey, out ModelRecord? rec))
                {
                    rec = new ModelRecord();
                    all[modelKey] = rec;
                }
                foreach (KeyValuePair<string, bool> pair in findings) rec.LinkOnly[pair.Key] = pair.Value;
                rec.RecordedUtc = DateTime.UtcNow;
                WriteAtomically(StorePath, JsonSerializer.Serialize(all, JsonOptions));
            });
        }
        catch (Exception ex)
        {
            // A performance hint must never be able to fail a run.
            Diagnostics.RunTrace.Mark("workset audit could not be recorded: " + ex.Message);
        }
    }

    private static Dictionary<string, ModelRecord> ReadAll()
    {
        try
        {
            if (!File.Exists(StorePath)) return new Dictionary<string, ModelRecord>();
            return JsonSerializer.Deserialize<Dictionary<string, ModelRecord>>(
                       File.ReadAllText(StorePath), JsonOptions)
                   ?? new Dictionary<string, ModelRecord>();
        }
        catch { return new Dictionary<string, ModelRecord>(); }
    }

    /// <summary>Write to a temp file and move over the target, so a process dying mid-write leaves
    /// the previous store intact rather than a truncated one nothing can parse.</summary>
    private static void WriteAtomically(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
