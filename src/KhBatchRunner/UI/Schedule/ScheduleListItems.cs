using KhBatchRunner.Presets;

// Folder UI\Schedule\ groups the shared schedule panels; the NAMESPACE stays KhBatchRunner.UI —
// see ScheduleJobListPanel for why nesting it shadows KhBatchRunner.Schedule.
namespace KhBatchRunner.UI;

/// <summary>
/// A saved model set as a checklist row. Its <see cref="ToString"/> IS the row text — that is how
/// <see cref="System.Windows.Forms.CheckedListBox"/> renders an item.
///
/// <para>Was a private nested class in each of the Schedule* windows (and still is in
/// <c>BatchRunWindow</c>, which is not part of this release). Shared from 1.4.0 so that
/// <see cref="ModelSourcePanel"/> can be the single implementation of the two lists.</para>
/// </summary>
internal sealed class ModelSetItem
{
    public ModelPreset Preset { get; }
    public ModelSetItem(ModelPreset preset) => Preset = preset;
    public override string ToString() =>
        ModelSetAccess.Label(Preset, $"{Preset.Name}  ({Preset.Models.Count} models)");
}

/// <summary>
/// A combo entry whose stored VALUE differs from its label — an enum name persisted on the job
/// beside the wording shown to the user ("CloseRevit" / "Close Revit when complete").
///
/// <para>Shared from 1.4.0 for the four Schedule* windows. <c>BatchRunWindow</c> and
/// <c>ExportNwcWindow</c> keep their own private copies; they are outside this release's scope, and
/// a nested class shadows this one harmlessly.</para>
/// </summary>
internal sealed class NamedItem
{
    public string Value { get; }
    private readonly string _label;
    public NamedItem(string value, string label) { Value = value; _label = label; }
    public override string ToString() => _label;
}

/// <summary>One ad-hoc model in the list, labelled with where it came from.</summary>
internal sealed class AdHocItem
{
    public CloudModel Model { get; }
    public AdHocItem(CloudModel model) => Model = model;
    public override string ToString() =>
        Model.IsLocal ? $"{Model.DisplayName}  (local)" : $"{Model.DisplayName}  (Forma)";
}
