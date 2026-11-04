namespace KhBatchRunner.Presets;

/// <summary>
/// Combines one or more saved model sets plus ad-hoc models into a single de-duplicated
/// <see cref="ModelPreset"/> for a run — shared by Schedule Run's multi-set jobs and the
/// Batch Run ("DYN multi player") panel.
/// </summary>
public static class ModelPresetCombiner
{
    public static ModelPreset Combine(string name, IEnumerable<string> modelSetNames, IEnumerable<CloudModel> adHocModels) =>
        Combine(name, modelSetNames, adHocModels, out _);

    /// <summary>Combines the named sets (each folder-backed set RE-SCANNED via
    /// <see cref="PresetResolver"/>) plus ad-hoc models into one de-duplicated preset.
    /// <paramref name="notes"/> collects any folder-scan fallback messages for the caller to log.</summary>
    public static ModelPreset Combine(string name, IEnumerable<string> modelSetNames,
        IEnumerable<CloudModel> adHocModels, out List<string> notes) =>
        Combine(name, modelSetIds: null, modelSetNames, adHocModels, out notes);

    /// <summary>Id-aware combine used by scheduled jobs: resolves each referenced set by its stable
    /// <see cref="ModelPreset.Id"/> first (so a renamed set still runs), falling back to the name when
    /// no id is stored (older jobs) or the id no longer matches. <paramref name="modelSetIds"/> is
    /// index-parallel to <paramref name="modelSetNames"/>; either may be shorter/empty.</summary>
    public static ModelPreset Combine(string name, IReadOnlyList<string>? modelSetIds,
        IEnumerable<string> modelSetNames, IEnumerable<CloudModel> adHocModels, out List<string> notes)
    {
        var models = new List<CloudModel>();
        var seen = new HashSet<string>();
        notes = new List<string>();

        // One shared definition of "same model" — the recovery pass compares against this list, so
        // its key and this one must not diverge. See ModelKey (it also lowercases a local path,
        // which the inline version here did not).
        void AddIfNew(CloudModel m)
        {
            if (seen.Add(ModelKey.For(m))) models.Add(m);
        }

        List<ModelPreset> allPresets = PresetStore.LoadAll();
        var names = modelSetNames.ToList();
        var ids = modelSetIds?.ToList() ?? new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            string? id = i < ids.Count ? ids[i] : null;
            ModelPreset? preset = !string.IsNullOrWhiteSpace(id)
                ? allPresets.FirstOrDefault(p => p.Id == id)
                : null;
            preset ??= allPresets.FirstOrDefault(p => p.Name == names[i]);
            if (preset == null)
            {
                // Said out loud since 1.5.0. A referenced set that no longer exists was skipped in
                // silence, so a job that had lost half its models still reported a clean run over
                // the half that remained — and nothing anywhere named the set that went missing.
                notes.Add($"Model set '{names[i]}' was not found and could not be run. " +
                          "It may have been renamed or deleted.");
                continue;
            }
            ModelPreset resolved = PresetResolver.Resolve(preset, out string? note);
            if (note != null) notes.Add(note);
            foreach (CloudModel m in resolved.Models) AddIfNew(m);
        }
        foreach (CloudModel m in adHocModels) AddIfNew(m);

        string? preflight = AccessPreflight(models);
        if (preflight != null) notes.Add(preflight);

        return new ModelPreset { Name = name, Models = models };
    }

    /// <summary>
    /// One line naming the models this account provably cannot open, or null when there is nothing
    /// to say. Reporting only — nothing is removed from the run.
    ///
    /// <para><b>Why report before running rather than discover model by model.</b> A model-list set
    /// makes no Forma call at all before a run: its models go straight to <c>BatchEngine</c>, so an
    /// account without access learns that one failed open at a time, having already paid for the
    /// split's Revit start-ups (measured at ~9.5 minutes each). One line at the top of the log,
    /// before any of that, is the difference between a diagnosable run and a mystery.</para>
    ///
    /// <para><b>Uses only a snapshot someone else already took.</b> This method is on the UI thread
    /// for every interactive run, so it must never make a REST call of its own — the callers that
    /// want a fresh answer warm <see cref="AccountAccess"/> first. With no snapshot, every model is
    /// Unknown and this says nothing, which is the correct behaviour for "we have not checked".</para>
    ///
    /// <para>Advisory, never a filter: the models are still attempted. The check is by Data
    /// Management project id, which older sets do not carry, so removing models on the strength of
    /// it would silently shrink runs that are actually fine.</para>
    /// </summary>
    private static string? AccessPreflight(List<CloudModel> models)
    {
        AccessSnapshot? snapshot = AccountAccess.Cached;
        if (snapshot is not { Reachable: true }) return null;

        // Worth a line on its own, before any model is judged: a schedule is a Windows task, so it
        // fired tonight exactly as it did last night, under a different Autodesk account. That is
        // the fact that explains everything else in the log, and nothing used to record it.
        string change = snapshot.AccountChanged
            ? $"The Autodesk account changed since the last run: now {snapshot.SignedInUser}, " +
              $"previously {snapshot.PreviousUser}. Schedules run regardless of who is signed in. "
            : "";

        var denied = models
            .Where(m => !m.IsLocal
                        && !string.IsNullOrWhiteSpace(m.DmProjectId)
                        && !snapshot.ProjectIds.Contains(m.DmProjectId))
            .Select(m => m.DisplayName)
            .ToList();
        if (denied.Count == 0) return change.Length > 0 ? change.TrimEnd() : null;

        const int show = 5;
        string named = string.Join(", ", denied.Take(show));
        if (denied.Count > show) named += $", and {denied.Count - show} more";
        return change +
               $"{denied.Count} of {models.Count} model(s) are in Forma projects {AccessDenied.Account} " +
               $"cannot open: {named}. They are still attempted, and each will fail to open.";
    }
}
