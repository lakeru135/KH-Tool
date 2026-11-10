#if !EDITION_DRW
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.Drw.Enlarge;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Mcp;

internal static partial class McpTools
{
    // ------------------------------------------------------------------ DRW tools

    private static JsonObject EnlargeRooms(Document doc, string? presetName, List<string> rooms)
    {
        if (rooms.Count == 0) throw new McpError("Name the rooms to enlarge (or ['*'] for the preset's batch filter).");
        EnlargePreset preset = LoadPreset(EnlargeStores.Room, presetName, "enlarge_room", out string used);
        List<EnlargeTarget> targets = MatchTargets(EnlargeTargets.Rooms(doc, preset.RoomLabel), rooms, preset.BatchFilter,
            "room", out JsonArray notFound);

        List<IReadOnlyDictionary<string, string>> rows = new EnlargeCreator(doc, preset, stairs: false).Create(targets, writeMarks: false);
        return Rows(rows, new JsonObject
        {
            ["preset"] = used,
            ["rooms"] = new JsonArray(targets.Select(t => (JsonNode)t.Display).ToArray()),
            ["not_found"] = notFound
        });
    }

    /// <summary>The targets the AI named — by id, number, name, "number name" or the listed name,
    /// then by a part of the listed name — or, for ['*'], those the preset's batch filter takes.</summary>
    private static List<EnlargeTarget> MatchTargets(List<EnlargeTarget> all, List<string> keys, string? batchFilter,
        string noun, out JsonArray notFound)
    {
        var targets = new List<EnlargeTarget>();
        notFound = new JsonArray();
        if (IsAll(keys))
        {
            string filter = batchFilter?.Trim() ?? "";
            targets = all.Where(t => filter.Length == 0 || t.BaseName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
        else
        {
            foreach (string key in keys)
            {
                bool eq(string? s) => s != null && s.Equals(key, StringComparison.OrdinalIgnoreCase);
                List<EnlargeTarget> hits = all.Where(t => eq(t.SourceId.Value.ToString()) || eq(t.Number) || eq(t.BaseName)
                                                         || eq(t.Number + " " + t.BaseName) || eq(t.Display)).ToList();
                if (hits.Count == 0)
                    hits = all.Where(t => t.Display.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 0) notFound.Add(key);
                foreach (EnlargeTarget t in hits)
                    if (!targets.Any(x => x.SourceId == t.SourceId)) targets.Add(t);
            }
        }
        if (targets.Count == 0)
            throw new McpError(all.Count == 0
                ? $"This model has no {noun}s to enlarge."
                : $"No {noun} matched. {char.ToUpperInvariant(noun[0])}{noun.Substring(1)}s here: " +
                  string.Join(" | ", all.Select(t => t.Display).Take(40)) + ".");
        return targets;
    }

    private static List<EnlargeTarget> StairTargets(Document doc, EnlargePreset p) =>
        EnlargeTargets.Stairs(doc, p.MarkDelimiter, p.OverrideMultistoryMarks, p.StairBeforeFirstStepM, p.StairAfterLastStepM);

    private static JsonNode ListStairs(Document doc, string? presetName)
    {
        EnlargePreset preset = LoadPreset(EnlargeStores.Stair, presetName, "enlarge_stair", out _);
        return new JsonArray(StairTargets(doc, preset).Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.SourceId.Value,
            ["name"] = t.Display,
            ["kind"] = t.Kind,
            ["levels"] = new JsonArray(t.PlanLevels.Select(l => (JsonNode)l.Name).ToArray())
        }).ToArray());
    }

    private static JsonObject EnlargeStairs(Document doc, string? presetName, List<string> stairs, bool writeMarks)
    {
        if (stairs.Count == 0) throw new McpError("Name the stairs to enlarge (or ['*'] for the preset's batch filter).");
        EnlargePreset preset = LoadPreset(EnlargeStores.Stair, presetName, "enlarge_stair", out string used);
        List<EnlargeTarget> targets = MatchTargets(StairTargets(doc, preset), stairs, preset.BatchFilter, "stair", out JsonArray notFound);

        // The ribbon asks before renumbering; here the AI must have asked, so marks change only when told to.
        bool marks = writeMarks && preset.OverrideMultistoryMarks;
        List<IReadOnlyDictionary<string, string>> rows = new EnlargeCreator(doc, preset, stairs: true).Create(targets, marks);
        return Rows(rows, new JsonObject
        {
            ["preset"] = used,
            ["stairs"] = new JsonArray(targets.Select(t => (JsonNode)t.Display).ToArray()),
            ["not_found"] = notFound,
            ["marks_written"] = marks
        });
    }

    private static string CategoryKey(string s)
    {
        if (s.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) return "OST_" + s.Substring(4);
        string k = s.Trim().Replace(" ", "");
        // "doors" → "OST_Doors": the BuiltInCategory names are plural PascalCase.
        return "OST_" + char.ToUpperInvariant(k[0]) + k.Substring(1);
    }

    private static JsonObject TagViews(Document doc, string? presetName, List<string> viewKeys, List<string> categories,
        bool autoTypes, bool dryRun)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the views to tag (or ['*'] for the preset's own name filter).");
        PresetTaggerPreset preset = LoadPreset(PresetTaggerStore.Store, presetName, "preset_tagger", out string used);
        preset.DryRun = dryRun;

        if (categories.Count > 0)
        {
            var keys = categories.Select(CategoryKey).ToList();
            var rules = new List<TagRule>();
            foreach (string key in keys)
            {
                List<TagRule> existing = preset.Rules.Where(r => r.Category.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
                rules.AddRange(existing.Count > 0 ? existing : new List<TagRule> { new() { Category = key } });
            }
            preset.Rules = rules;
        }

        var chosen = new JsonObject();
        if (autoTypes)
        {
            foreach (TagRule rule in preset.Rules.Where(r => r.Enabled && !r.TagsMaterial() && string.IsNullOrWhiteSpace(r.TagType)))
            {
                if (!Enum.TryParse(rule.Category, true, out BuiltInCategory cat)) continue;
                // The category's own tag (M_Window Tag) before a multi-category one, which sorts
                // first by name but shows the wrong label.
                long own = (long?)PresetTagger.TagCategory(cat) ?? -1;
                FamilySymbol? first = PresetTagger.TagTypes(doc, cat)
                    .OrderBy(s => s.Category?.Id.Value == own ? 0 : 1).FirstOrDefault();
                if (first == null) continue;
                rule.TagType = DrwLookup.TypeLabel(first);
                chosen[rule.Category] = rule.TagType;
            }
        }

        List<View> views = IsAll(viewKeys)
            ? DrwScope.ResolveTagViews(doc, null, DrwViewScope.AllPlans, preset.ViewNameContains,
                preset.Plans, preset.CeilingPlans, preset.Sections, preset.Elevations)
            : FindViews(doc, viewKeys);
        List<string> skipped = views.Where(v => !PresetTagger.IsTagView(v)).Select(v => v.Name).ToList();
        views = views.Where(PresetTagger.IsTagView).ToList();
        if (views.Count == 0) throw new McpError("None of those views can take tags (plans, ceiling plans, sections, elevations).");

        return Rows(PresetTagger.Run(doc, views, preset), new JsonObject
        {
            ["preset"] = used,
            ["views"] = new JsonArray(views.Select(v => (JsonNode)v.Name).ToArray()),
            ["views_skipped"] = new JsonArray(skipped.Select(n => (JsonNode)n).ToArray()),
            ["auto_tag_types"] = chosen,
            ["dry_run"] = dryRun
        });
    }

    private static JsonObject DimensionDatums(Document doc, string? presetName, List<string> viewKeys)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the views to dimension (or ['*'] for the preset's own name filter).");
        DatumDimensionPreset preset = LoadPreset(DatumDimensionStore.Store, presetName, "datum_dimension", out string used);
        List<View> views = IsAll(viewKeys)
            ? DrwScope.ResolveDatumViews(doc, null, DrwViewScope.AllPlans, preset.ViewNameContains,
                preset.Plans, preset.Sections, preset.Elevations)
            : FindViews(doc, viewKeys);
        List<string> skipped = views.Where(v => !DrwScope.IsDatumView(v)).Select(v => v.Name).ToList();
        views = views.Where(DrwScope.IsDatumView).ToList();
        if (views.Count == 0) throw new McpError("None of those views has datums to dimension (plans, sections, elevations).");

        return Rows(DatumDimensioner.Run(doc, views, preset), new JsonObject
        {
            ["preset"] = used,
            ["views"] = new JsonArray(views.Select(v => (JsonNode)v.Name).ToArray()),
            ["views_skipped"] = new JsonArray(skipped.Select(n => (JsonNode)n).ToArray())
        });
    }

    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode)n).ToArray());

    private static JsonObject MatchDatums(Document doc, string sourceKey, List<string> viewKeys)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the target views (or ['*'] for every parallel view at the source's scale).");
        View source = FindView(doc, sourceKey);
        if (!DatumMatcher.IsMatchView(source))
            throw new McpError($"'{source.Name}' is not a plan, ceiling plan, section or elevation.");
        List<View> candidates = DatumMatcher.Candidates(doc, source);

        // The ribbon only offers candidates; a named view that is not one is reported, not forced.
        var skipped = new JsonArray();
        List<View> targets;
        if (IsAll(viewKeys)) targets = candidates;
        else
        {
            targets = new List<View>();
            foreach (View v in FindViews(doc, viewKeys))
            {
                if (candidates.Any(c => c.Id == v.Id)) { if (!targets.Contains(v)) targets.Add(v); }
                else skipped.Add($"{v.Name}: " + (v.Id == source.Id ? "it is the source"
                    : !DatumMatcher.IsMatchView(v) ? "not a plan, ceiling plan, section or elevation"
                    : v.Scale != source.Scale ? $"1:{v.Scale}, the source is 1:{source.Scale}"
                    : "not parallel to the source"));
            }
        }
        if (targets.Count == 0)
            throw new McpError($"No target view can take '{source.Name}''s datums." +
                               (skipped.Count > 0 ? " " + string.Join("; ", skipped.Select(n => (string?)n)) : "") +
                               (candidates.Count > 0 ? " Parallel views at 1:" + source.Scale + ": " +
                                   string.Join(" | ", candidates.Select(c => c.Name).Take(30)) : ""));

        return Rows(DatumMatcher.Run(doc, source, targets), new JsonObject
        {
            ["source"] = source.Name,
            ["views"] = Names(targets.Select(v => v.Name)),
            ["views_skipped"] = skipped
        });
    }

    private static JsonObject AnnotateStairs(Document doc, string? presetName, List<string> viewKeys)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the views to annotate (or ['*'] for the preset's own name filter).");
        StairAnnotationPreset preset = LoadPreset(StairAnnotationStore.Store, presetName, "stair_annotation", out string used);
        List<View> views = IsAll(viewKeys)
            ? DrwScope.ResolveStairViews(doc, null, DrwViewScope.AllPlans, preset.ViewNameContains, preset.Sections)
            : FindViews(doc, viewKeys);
        List<string> skipped = views.Where(v => !StairAnnotator.IsStairView(v)).Select(v => v.Name).ToList();
        views = views.Where(StairAnnotator.IsStairView).ToList();
        if (views.Count == 0) throw new McpError("None of those views is a floor plan or section.");

        return Rows(StairAnnotator.Run(doc, views, preset), new JsonObject
        {
            ["preset"] = used,
            ["views"] = Names(views.Select(v => v.Name)),
            ["views_skipped"] = Names(skipped)
        });
    }

    private static JsonObject TagWallMaterials(Document doc, string? presetName, List<string> viewKeys, bool dryRun)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the views to tag (or ['*'] for the preset's own name filter).");
        WallMatTagPreset preset = DrwPresetStore<WallMatTagPreset>.Clone(
            LoadPreset(WallMatTagStore.Store, presetName, "wall_mat_tag", out string used));
        preset.DryRun = dryRun;
        preset.Scope = DrwViewScope.AllPlans;

        bool wallView(View v) => DrwScope.IsPlan(v) || (!v.IsTemplate && v.ViewType is ViewType.Section or ViewType.Elevation);
        List<View> views = IsAll(viewKeys) ? WallMatTag.Views(doc, null, preset) : FindViews(doc, viewKeys);
        List<string> skipped = views.Where(v => !wallView(v)).Select(v => v.Name).ToList();
        views = views.Where(wallView).ToList();
        if (views.Count == 0) throw new McpError("None of those views is a plan, section or elevation.");

        var made = new List<ElementId>();
        JsonObject result = Rows(WallMatTag.Run(doc, views, preset, made), new JsonObject
        {
            ["preset"] = used,
            ["views"] = Names(views.Select(v => v.Name)),
            ["views_skipped"] = Names(skipped),
            ["dry_run"] = dryRun
        });
        if (made.Count > 0)
            result["material_tags_to_touch"] =
                $"{made.Count} new material tag(s) will read '?' until touched in Revit (Revit's API cannot link a new material tag " +
                "to its material). Ask the user to select them and nudge them with an arrow key and back.";
        return result;
    }

    private static JsonObject SingleFont(Document doc, string? presetName, string? font, bool dryRun)
    {
        SingleFontPreset preset = DrwPresetStore<SingleFontPreset>.Clone(
            LoadPreset(SingleFontStore.Store, presetName, "single_font", out string used));
        if (font != null) preset.FontName = font;
        preset.DryRun = dryRun;
        if (!SingleFontEngine.FontInstalled(preset.FontName))
            throw new McpError($"The font '{preset.FontName}' is not installed on this PC.");

        return Rows(SingleFontEngine.Run(doc, preset), new JsonObject
        {
            ["preset"] = used,
            ["font"] = preset.FontName,
            ["dry_run"] = dryRun
        });
    }
}
#endif
