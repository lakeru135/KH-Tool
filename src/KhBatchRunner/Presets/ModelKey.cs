namespace KhBatchRunner.Presets;

/// <summary>
/// The one canonical identity for a model, used wherever a run has to decide "is this the same
/// model?" — de-duplicating a combined selection, recording which Revit instance owns which models,
/// and reconciling what actually ran against what was asked for.
///
/// A single definition matters because the codebase already carries two: <see cref="ModelPresetCombiner"/>
/// keys on project+model GUID (or the local path), while <see cref="PresetResolver"/> de-dups on the
/// model GUID alone and lets every <see cref="System.Guid.Empty"/> (i.e. every local model) through.
/// The resolver's list is always passed through the combiner before a run sees it, so the combined
/// output is already canonical — but anything comparing models must use THIS key, or a recovery pass
/// could mistake one model for another and either skip it or run it twice.
/// </summary>
public static class ModelKey
{
    /// <summary>
    /// Stable, case-insensitive identity. Local models key on their path (Windows paths are
    /// case-insensitive, so the key is lowercased — the combiner's older inline version was not,
    /// which let the same file added twice with different casing survive as two entries).
    /// </summary>
    public static string For(CloudModel model) =>
        model.IsLocal
            ? "local:" + model.LocalPath!.ToLowerInvariant()
            : $"cloud:{model.ProjectGuid}:{model.ModelGuid}";
}
