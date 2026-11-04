using System.IO;
using System.Text.Json;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Batch;

/// <summary>How a cloud model is opened: with the engine's explicit workset configuration, or with
/// plain <c>OpenOptions</c> that name no worksets at all.</summary>
internal enum CloudOpenMode { WorksetConfig, Plain }

/// <summary>
/// Whether each cloud model is workshared, as this machine last learned it — so a NON-workshared cloud
/// model can be opened without a workset configuration, which is the open that crashes Revit.
///
/// <para><b>Why it matters.</b> MEASURED 2026-11-03 on Revit 2026.5 (26.5.10.38): the "RG Test" set is
/// seven Forma cloud models whose journal reads <c>Nonworkshared Cloud Central</c>. The engine's first,
/// uncached open of one — <c>OpenDocumentFile</c> with <c>CloseAllWorksets</c> — logged
/// <c>catch SelectedPartitionsForEdit and do decommitDocument</c> and died with 0xc0000005 in
/// coreclr.dll, twice, on two different models, taking the user's Revit and the batch with it. The
/// same model's CACHED reopen survived. An uncached open of a third RG model with plain
/// <c>new OpenOptions()</c> opened in 54 s with <c>IsWorkshared=False</c>. A non-workshared model has
/// no worksets to choose, so asking for none loses nothing.</para>
///
/// <para><b>Nothing here is a scan-time snapshot of a model set</b> — the 1.3.5.4 lesson
/// (<see cref="Forma.FormaModelItem.BlockedReason"/>). Every fact is either asked of Forma at the start of
/// THIS run (<see cref="RefreshFromForma"/>), observed on a real open (<see cref="RecordOpened"/>), or
/// left by a crash this machine just survived (<see cref="MarkCrash"/>). And unknown changes
/// nothing: a model with no fact opens exactly as it did before this class existed.</para>
///
/// <para>Machine-local beside <see cref="WorksetAudit"/>, and for the same reason: the parent Revit
/// that asks Forma, the child Revit that opens, and the supervisor that sees the crash are three
/// processes on one PC, and this file is the one thing they all reach.</para>
/// </summary>
internal static class CloudOpenFacts
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Beside the trail and the workset audit — machine-local, per user, never roamed.</summary>
    public static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KhBatchRunner", "cloud-model-facts.json");

    /// <summary>One model's facts.</summary>
    public sealed class Fact
    {
        /// <summary>True/false once known; null while only a crash is known.</summary>
        public bool? Workshared { get; set; }
        /// <summary>"acc" (tip version modelType) or "opened" (a real open said so).</summary>
        public string Source { get; set; } = "";
        public DateTime RecordedUtc { get; set; }
        /// <summary>Revit died opening this model and nothing says it is workshared; open it plain
        /// next. Cleared by the next successful open, which records what the model really is.</summary>
        public bool CrashRetry { get; set; }
    }

    /// <summary>
    /// The decision, with Revit taken out so the harness can pin it (<c>tools/LivenessSim</c>).
    ///
    /// <list type="number">
    /// <item>Known WORKSHARED wins over everything, a crash included. A plain open of a workshared
    /// model lets Revit pick the worksets itself — the all-worksets path
    /// <c>LeftOpenDocsActivator</c> already records crashing — so a crash on one of these
    /// stays the supervisor's ordinary retry, never a plain open.</item>
    /// <item>Known non-workshared, or a crash with nothing known → plain.</item>
    /// <item>Unknown → the engine's workset configuration, exactly as before.</item>
    /// </list>
    /// </summary>
    internal static (CloudOpenMode Mode, string Source) Decide(Fact? fact)
    {
        if (fact is null) return (CloudOpenMode.WorksetConfig, "");
        if (fact.Workshared == true) return (CloudOpenMode.WorksetConfig, fact.Source);
        if (fact.Workshared == false) return (CloudOpenMode.Plain, fact.Source);
        return fact.CrashRetry ? (CloudOpenMode.Plain, "crash-retry") : (CloudOpenMode.WorksetConfig, "");
    }

    /// <summary>Forma's <c>modelType</c> as a fact: "multiuser" → workshared, "singleuser" → not, anything
    /// else (including "") → unknown, which is never treated as evidence.</summary>
    internal static bool? FromModelType(string? modelType) =>
        string.Equals(modelType, "multiuser", StringComparison.OrdinalIgnoreCase) ? true
        : string.Equals(modelType, "singleuser", StringComparison.OrdinalIgnoreCase) ? false
        : null;

    /// <summary>How <paramref name="model"/> should be opened right now. Local models are never
    /// asked about — their header is read at open time (<c>BasicFileInfo</c>).</summary>
    public static (CloudOpenMode Mode, string Source) PlanFor(CloudModel model)
    {
        if (model.IsLocal) return (CloudOpenMode.WorksetConfig, "");
        ReadAll().TryGetValue(ModelKey.For(model), out Fact? fact);
        // Switched off: forget what Forma and earlier opens said, but still honour a crash mark — the
        // negative control has to prove crash → plain retry, not crash → crash.
        if (IsDisabled() && fact != null) fact = new Fact { CrashRetry = fact.CrashRetry };
        return Decide(fact);
    }

    /// <summary>The fact on record for <paramref name="model"/>, or null.</summary>
    public static Fact? Get(CloudModel model)
    {
        if (model.IsLocal) return null;
        return ReadAll().TryGetValue(ModelKey.For(model), out Fact? fact) ? fact : null;
    }

    /// <summary>
    /// <c>KHBATCH_OPENPLAN=off</c> forces the old options on every open — the negative control the
    /// loop test uses to prove the crash → plain retry path, and a field switch if plain opens ever
    /// misbehave on some hub.
    /// </summary>
    internal static bool IsDisabled() =>
        string.Equals(Environment.GetEnvironmentVariable("KHBATCH_OPENPLAN"), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a real open just showed. Clears any crash mark: the model is now known.</summary>
    public static void RecordOpened(CloudModel model, bool workshared) =>
        Update(model, f =>
        {
            f.Workshared = workshared;
            f.Source = "opened";
            f.CrashRetry = false;
        });

    /// <summary>The supervisor saw Revit die opening <paramref name="model"/>: unless the model is
    /// known workshared, its next open is plain. Returns whether it is known workshared — the one case
    /// the crash stays the supervisor's ordinary retry.</summary>
    public static bool MarkCrash(CloudModel model)
    {
        bool workshared = false;
        Update(model, f =>
        {
            workshared = f.Workshared == true;
            if (!workshared) f.CrashRetry = true;
        });
        return workshared;
    }

    /// <summary>
    /// Asks Forma for each cloud model's tip <c>modelType</c> and records the answers — called once at the
    /// start of a run, in the Revit that launches it. Bounded by <paramref name="budget"/> in total so a
    /// slow hub costs seconds, not the run; a model Forma does not answer for keeps whatever was known.
    /// Never throws.
    /// </summary>
    public static int RefreshFromForma(IEnumerable<CloudModel> models, TimeSpan budget)
    {
        int learned = 0;
        try
        {
            var todo = models.Where(m => !m.IsLocal &&
                                         !string.IsNullOrWhiteSpace(m.DmProjectId) &&
                                         !string.IsNullOrWhiteSpace(m.ItemUrn)).ToList();
            if (todo.Count == 0) return 0;

            var client = new Forma.DataManagementClient(new Auth.SessionTokenProvider());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (CloudModel m in todo)
            {
                if (sw.Elapsed > budget) break;
                bool? workshared;
                try { workshared = FromModelType(client.GetTipModelType(m.DmProjectId, m.ItemUrn)); }
                catch { continue; }
                if (workshared is null) continue;
                // An "opened" fact is newer evidence than nothing, but Forma is asked fresh each run,
                // so it is the one that tracks a model whose worksharing was enabled since.
                Update(m, f => { f.Workshared = workshared; f.Source = "acc"; });
                learned++;
            }
            Diagnostics.RunTrace.Mark($"cloud open facts: Forma answered for {learned} of {todo.Count} model(s) " +
                                      $"in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("cloud open facts: Forma lookup skipped: " + ex.Message);
        }
        return learned;
    }

    private static void Update(CloudModel model, Action<Fact> change)
    {
        if (model.IsLocal) return;
        string key = ModelKey.For(model);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            // Parent, children and supervisor all write here; same lock the shared run CSVs use.
            Logging.LogFileLock.Write(StorePath, () =>
            {
                Dictionary<string, Fact> all = ReadAll();
                if (!all.TryGetValue(key, out Fact? fact))
                {
                    fact = new Fact();
                    all[key] = fact;
                }
                change(fact);
                fact.RecordedUtc = DateTime.UtcNow;
                WriteAtomically(StorePath, JsonSerializer.Serialize(all, JsonOptions));
            });
        }
        catch (Exception ex)
        {
            // A hint must never be able to fail a run.
            Diagnostics.RunTrace.Mark("cloud open facts could not be recorded: " + ex.Message);
        }
    }

    private static Dictionary<string, Fact> ReadAll()
    {
        try
        {
            if (!File.Exists(StorePath)) return new Dictionary<string, Fact>();
            return JsonSerializer.Deserialize<Dictionary<string, Fact>>(File.ReadAllText(StorePath), JsonOptions)
                   ?? new Dictionary<string, Fact>();
        }
        catch { return new Dictionary<string, Fact>(); }
    }

    /// <summary>Temp file then move, so a process dying mid-write leaves the previous store intact.</summary>
    private static void WriteAtomically(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
