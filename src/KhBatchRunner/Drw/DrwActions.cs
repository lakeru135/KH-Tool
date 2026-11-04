using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.Drw.Enlarge;

namespace KhBatchRunner.Drw;

/// <summary>
/// DRW Helper's batch-capable tools, one action PER SAVED PRESET: "Enlarge Room — Default",
/// "Grid Dimension — A1 sheets", …. A batch cannot ask anyone anything, so the preset IS the
/// configuration, and naming the action after it makes the schedule key say which one runs.
/// Built fresh from disk by <see cref="ActionRegistry"/> on every lookup, so a split-run child or
/// a scheduled Revit gets the preset as it is saved now.
///
/// <para>Deliberately not batchable: Sync View (it reacts to a person switching views), and the
/// interactive parts of Enlarge — picking rooms/stairs from a list. In a batch Enlarge takes every
/// room or stair whose name contains the preset's <see cref="EnlargePreset.BatchFilter"/>.</para>
/// </summary>
internal static class DrwActions
{
    public const string EnlargeRoomPrefix = "Enlarge Room — ";
    public const string EnlargeStairPrefix = "Enlarge Stair — ";
    public const string DatumDimensionPrefix = ActionNames.DatumDimensionPrefix;
    public const string StairAnnotationPrefix = "Stair Annotation — ";
    public const string SingleFontPrefix = "Single Font — ";
    public const string PresetTaggerPrefix = "Preset Tagger — ";
    public const string WallMatTagPrefix = "Wall Mat Tag — ";

    /// <summary>What Datum Dimension was called before 0.2.6. Schedules and batch selections saved
    /// then still carry it; see <see cref="CanonicalName"/>.</summary>
    public const string LegacyGridDimensionPrefix = ActionNames.LegacyGridDimensionPrefix;

    public static IEnumerable<IBatchAction> All()
    {
        foreach (EnlargePreset p in EnlargeStores.Room.LoadAll()) yield return new EnlargeAction(p, stairs: false);
        foreach (EnlargePreset p in EnlargeStores.Stair.LoadAll()) yield return new EnlargeAction(p, stairs: true);
        foreach (DatumDimensionPreset p in DatumDimensionStore.Store.LoadAll()) yield return new DatumDimensionAction(p);
        foreach (StairAnnotationPreset p in StairAnnotationStore.Store.LoadAll()) yield return new StairAnnotationAction(p);
        foreach (SingleFontPreset p in SingleFontStore.Store.LoadAll()) yield return new SingleFontAction(p);
        foreach (PresetTaggerPreset p in PresetTaggerStore.Store.LoadAll()) yield return new PresetTaggerAction(p);
        foreach (WallMatTagPreset p in WallMatTagStore.Store.LoadAll()) yield return new WallMatTagAction(p);
    }

    /// <summary>
    /// The current name for a saved action name: "Grid Dimension — X" becomes "Datum Dimension — X",
    /// anything else is returned as it is. An action's Name IS its schedule key, so every place that
    /// matches a saved name to a live action goes through this (ActionRegistry.Create, the Schedule
    /// window's ApplyRunItemOrder) — renaming the tool must not stop a saved schedule running.
    /// </summary>
    /// <remarks>The mapping itself lives in <see cref="ActionNames.Canonical"/>, which the non-Revit
    /// controller also compiles.</remarks>
    public static string CanonicalName(string name) => ActionNames.Canonical(name);
}

internal abstract class DrwActionBase : BatchActionBase
{
    public override ActionCategory Category => ActionCategory.Action;

    // Rooms, stairs and grids are element INSTANCES: a closed workset would hide them and the run
    // would report a clean, empty result. Every DRW tool reads instances, so every one opens all.
    public override WorksetPosture Worksets => WorksetPosture.OpensAll;

    public override string WorksetPostureReason =>
        "Reads rooms, stairs or grids, which are element instances; a closed workset would silently " +
        "hide them and the run would create nothing while reporting success.";

    protected abstract List<IReadOnlyDictionary<string, string>> Execute(Document doc);

    // The engine calls RunDetailRows once per model; the work happens there and only there.
    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc) =>
        Execute(doc);

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        List<IReadOnlyDictionary<string, string>> rows = Execute(doc);
        return new Dictionary<string, string>
        {
            // Single Font's rows say Changed; the other tools' say Created.
            ["Created"] = rows.Count(r => r.TryGetValue("Status", out string? s) && s is "Created" or "Changed").ToString(),
            ["Skipped"] = rows.Count(r => r.TryGetValue("Status", out string? s) && s == "Skipped").ToString(),
            ["Failed"] = rows.Count(r => r.TryGetValue("Status", out string? s) && s == "Failed").ToString(),
            // A preset naming a type or template this model lacks: the run went on with a fallback.
            ["Warnings"] = rows.Count(r => r.TryGetValue("Status", out string? s) && s == "Warning").ToString()
        };
    }
}

internal sealed class EnlargeAction : DrwActionBase
{
    private readonly EnlargePreset _preset;
    private readonly bool _stairs;

    public EnlargeAction(EnlargePreset preset, bool stairs)
    {
        _preset = preset;
        _stairs = stairs;
    }

    public override string Name => (_stairs ? DrwActions.EnlargeStairPrefix : DrwActions.EnlargeRoomPrefix) + _preset.Name;
    public override IReadOnlyList<string> Columns => EnlargeCreator.Columns;

    protected override List<IReadOnlyDictionary<string, string>> Execute(Document doc)
    {
        List<EnlargeTarget> targets = _stairs
            ? EnlargeTargets.Stairs(doc, _preset.MarkDelimiter, _preset.OverrideMultistoryMarks,
                _preset.StairBeforeFirstStepM, _preset.StairAfterLastStepM)
            : EnlargeTargets.Rooms(doc, _preset.RoomLabel);
        string filter = _preset.BatchFilter?.Trim() ?? "";
        if (filter.Length > 0)
            targets = targets.Where(t => t.BaseName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        return new EnlargeCreator(doc, _preset, _stairs).Create(targets, writeMarks: _stairs && _preset.OverrideMultistoryMarks);
    }
}

internal sealed class DatumDimensionAction : DrwActionBase
{
    private readonly DatumDimensionPreset _preset;
    public DatumDimensionAction(DatumDimensionPreset preset) => _preset = preset;

    public override string Name => DrwActions.DatumDimensionPrefix + _preset.Name;
    public override IReadOnlyList<string> Columns => DatumDimensioner.Columns;

    // A batch always takes the name filter, over the kinds of view the preset ticks.
    protected override List<IReadOnlyDictionary<string, string>> Execute(Document doc) =>
        DatumDimensioner.Run(doc, DrwScope.ResolveDatumViews(doc, null, DrwViewScope.AllPlans, _preset.ViewNameContains,
            _preset.Plans, _preset.Sections, _preset.Elevations), _preset);
}

internal sealed class StairAnnotationAction : DrwActionBase
{
    private readonly StairAnnotationPreset _preset;
    public StairAnnotationAction(StairAnnotationPreset preset) => _preset = preset;

    public override string Name => DrwActions.StairAnnotationPrefix + _preset.Name;
    public override IReadOnlyList<string> Columns => StairAnnotator.Columns;

    protected override List<IReadOnlyDictionary<string, string>> Execute(Document doc) =>
        StairAnnotator.Run(doc, DrwScope.ResolveStairViews(doc, null, DrwViewScope.AllPlans, _preset.ViewNameContains,
            _preset.Sections), _preset);
}

internal sealed class SingleFontAction : DrwActionBase
{
    private readonly SingleFontPreset _preset;
    public SingleFontAction(SingleFontPreset preset) => _preset = preset;

    public override string Name => DrwActions.SingleFontPrefix + _preset.Name;
    public override IReadOnlyList<string> Columns => SingleFontEngine.Columns;

    // Types and families only — a closed workset hides no type, so nothing needs opening.
    public override WorksetPosture Worksets => WorksetPosture.ReadsNoInstances;

    public override string WorksetPostureReason =>
        "Changes element types and loaded families only; types are never hidden by a closed workset.";

    protected override List<IReadOnlyDictionary<string, string>> Execute(Document doc) =>
        SingleFontEngine.Run(doc, _preset);
}

internal sealed class PresetTaggerAction : DrwActionBase
{
    private readonly PresetTaggerPreset _preset;
    public PresetTaggerAction(PresetTaggerPreset preset) => _preset = preset;

    public override string Name => DrwActions.PresetTaggerPrefix + _preset.Name;
    public override IReadOnlyList<string> Columns => PresetTagger.Columns;

    // A batch always takes the name filter, over the kinds of view the preset ticks.
    protected override List<IReadOnlyDictionary<string, string>> Execute(Document doc) =>
        PresetTagger.Run(doc, DrwScope.ResolveTagViews(doc, null, DrwViewScope.AllPlans, _preset.ViewNameContains,
            _preset.Plans, _preset.CeilingPlans, _preset.Sections, _preset.Elevations), _preset);
}

internal sealed class WallMatTagAction : DrwActionBase
{
    private readonly WallMatTagPreset _preset;
    public WallMatTagAction(WallMatTagPreset preset) => _preset = preset;

    public override string Name => DrwActions.WallMatTagPrefix + _preset.Name;
    public override IReadOnlyList<string> Columns => WallMatTag.Columns;

    // A batch always takes the name filter. Its new tags read "?" until someone touches them in Revit.
    protected override List<IReadOnlyDictionary<string, string>> Execute(Document doc)
    {
        WallMatTagPreset p = DrwPresetStore<WallMatTagPreset>.Clone(_preset);
        p.Scope = DrwViewScope.AllPlans;
        return WallMatTag.Run(doc, WallMatTag.Views(doc, null, p), p);
    }
}
