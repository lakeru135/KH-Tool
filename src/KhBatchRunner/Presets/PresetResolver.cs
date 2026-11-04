using KhBatchRunner.Forma;
using KhBatchRunner.Auth;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Presets;

/// <summary>
/// Materializes a <see cref="ModelPreset"/> into a concrete model list. A plain set is returned
/// unchanged; a <b>folder-backed</b> set (one or more <see cref="ModelPreset.AllSourceFolders"/>) is
/// RE-SCANNED from its Forma folder(s) at run time (via <see cref="FolderHosts.BuildPreset"/>) and the
/// results are unioned (deduped by cloud model GUID), so models added to the folders are picked up
/// automatically. If every scan fails or finds nothing, the preset's save-time
/// <see cref="ModelPreset.Models"/> snapshot is returned as an offline fallback and a note is
/// produced for the caller to log.
///
/// The returned preset always has empty source folders (already materialized), so resolving twice
/// is a harmless no-op.
/// </summary>
public static class PresetResolver
{
    /// <param name="tokens">
    /// Where the Forma bearer token comes from. Defaults to the signed-in Revit session
    /// (<see cref="SessionTokenProvider"/>) — what every in-Revit caller wants and what this
    /// method always did. The standalone controller passes its own provider instead, since it
    /// has no Revit session of its own to borrow from.
    /// </param>
    public static ModelPreset Resolve(ModelPreset preset, out string? note, ITokenProvider? tokens = null)
    {
        note = null;
        if (!preset.IsFolderBacked)
            return preset;

        var folders = preset.AllSourceFolders;
        var union = new List<CloudModel>();
        var seen = new HashSet<Guid>();
        var failures = new List<string>();
        var denied = new List<string>();
        var skipped = new List<string>();

        DataManagementClient client;
        try { client = new DataManagementClient(tokens ?? new SessionTokenProvider()); }
        catch (Exception ex)
        {
            if (AccessDenied.Is(ex))
            {
                note = DenialNote(preset, FirstLine(ex));
                return Materialized(preset, new List<CloudModel>());
            }
            note = $"Folder set '{preset.Name}': could not sign in to Forma ({ex.Message}) — using the " +
                   $"{preset.Models.Count} model(s) saved with the set.";
            return Materialized(preset, preset.Models);
        }

        foreach (Forma.FolderRef folder in folders)
        {
            try
            {
                ModelPreset scanned = FolderHosts.BuildPreset(client, folder, preset.IncludeSubfolders);
                skipped.AddRange(FolderHosts.LastSkipped);
                foreach (CloudModel m in scanned.Models)
                    if (m.ModelGuid == Guid.Empty || seen.Add(m.ModelGuid))
                        union.Add(m);
            }
            catch (Exception ex)
            {
                string detail = $"{folder.Name} ({FirstLine(ex)})";
                if (AccessDenied.Is(ex)) denied.Add(detail); else failures.Add(detail);
            }
        }

        // REFUSED, not merely unreadable: return NOTHING rather than the saved snapshot.
        //
        // The snapshot fallback exists for a set that could not be re-scanned but whose models are
        // still openable — a flaky connection, Forma briefly down. It is exactly wrong here. An
        // account that cannot list the folder cannot open the models inside it either, so falling
        // back hands the run a list of stale GUIDs and buys one failed open per model, across as
        // many parallel Revit instances as the split decided to start. Returning empty stops the
        // job at "No models resolved", which is the true answer.
        if (union.Count == 0 && denied.Count > 0)
        {
            note = DenialNote(preset, string.Join("; ", denied));
            return Materialized(preset, new List<CloudModel>());
        }

        // Every folder failed OR none held models → fall back to the saved snapshot.
        if (union.Count == 0)
        {
            note = failures.Count > 0
                ? $"Folder set '{preset.Name}': could not re-scan {string.Join("; ", failures)} — using " +
                  $"the {preset.Models.Count} model(s) saved with the set."
                : $"Folder set '{preset.Name}': the folder scan found no models — using the " +
                  $"{preset.Models.Count} model(s) saved with the set.";
            return Materialized(preset, preset.Models);
        }

        // Some folders were refused but others resolved — run what this account can reach, and say
        // plainly which folders it cannot, because a short report is otherwise indistinguishable
        // from a folder that legitimately emptied.
        if (denied.Count > 0)
            note = DenialNote(preset, string.Join("; ", denied)) +
                   $" Continuing with {union.Count} model(s) from the folder(s) it can read.";

        // Some folders resolved but others failed — use what we got and warn about the rest.
        if (failures.Count > 0)
            note = $"Folder set '{preset.Name}': {union.Count} model(s) from {folders.Count - failures.Count} " +
                   $"folder(s); could not scan {string.Join("; ", failures)}.";

        // Models the scan found and deliberately left out — a wrong Revit year, or a file that was
        // never initiated to cloud worksharing. Always reported: a set that quietly returns fewer
        // models than the folder holds is the kind of thing nobody notices until a report is short.
        if (skipped.Count > 0)
        {
            string left = $"Folder set '{preset.Name}': left out {skipped.Count} model(s) this Revit " +
                          $"cannot run — {string.Join("; ", skipped)}.";
            note = note == null ? left : note + " " + left;
        }

        return Materialized(preset, union);
    }

    /// <summary>One wording for a refusal, naming the account, so every path says the same thing.</summary>
    private static string DenialNote(ModelPreset preset, string detail) =>
        $"Folder set '{preset.Name}': {AccessDenied.Account} does not have access to {detail}. " +
        "The models saved with the set were NOT used as a fallback — an account that cannot read " +
        "the folder cannot open the models in it either. Check who Revit is signed in as.";

    private static string FirstLine(Exception ex) =>
        (ex.Message ?? "").Split('\n')[0].Trim();

    private static ModelPreset Materialized(ModelPreset source, List<CloudModel> models) => new()
    {
        Name = source.Name,
        ProjectCode = source.ProjectCode,
        SourceFolder = null,          // materialized — don't re-resolve
        SourceFolders = new List<Forma.FolderRef>(),
        Models = models
    };
}
