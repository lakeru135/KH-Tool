#if !EDITION_DRW
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Actions;
using KhBatchRunner.Commands;
using KhBatchRunner.ParamFormula;

namespace KhBatchRunner.Mcp;

/// <summary>
/// KH-Tool's own tools for the AI (Phase 3): Parameter Helper's rules and apply, Mirror Check, and
/// the Multi Model Check / Action buttons run on the active model. Each calls the same code its
/// ribbon button calls — FormulaApplier, MirrorApplier, the registered IBatchAction — so what the AI
/// does is what the user gets from the button.
/// </summary>
internal static partial class McpTools
{
    // ------------------------------------------------------------------ Parameter Helper

    private static FormulaRuleSet ProjectRules(Document doc)
    {
        if (doc.IsFamilyDocument) throw new McpError("Parameter Helper runs on project models only - the active document is a family.");
        try { return RuleStore.Load(doc); }
        catch (Exception ex) { throw new McpError(ex.Message); }
    }

    private static JsonObject RuleJson(FormulaRule r, RuleOrder.Result order) => new()
    {
        ["id"] = r.Id,
        ["enabled"] = r.Enabled,
        ["categories"] = Names(r.Categories),
        ["target"] = r.TargetName,
        ["target_on_type"] = r.TargetOnType,
        ["formula"] = r.Formula,
        ["note"] = r.Note.Length > 0 ? r.Note : null,
        ["refused"] = order.Refused.TryGetValue(r.Id, out string? why) ? why : null
    };

    private static JsonNode ParamRules(Document doc)
    {
        FormulaRuleSet set = ProjectRules(doc);
        RuleOrder.Result order = RuleOrder.Sort(set.Rules);
        return new JsonObject
        {
            ["live"] = set.Live,
            ["rules"] = new JsonArray(set.Rules.Select(r => (JsonNode)RuleJson(r, order)).ToArray()),
            ["mirror_check"] = new JsonObject
            {
                ["live"] = set.MirrorCheck,
                ["parameter"] = MirrorParams.NameOf(doc, MirrorParams.Target(doc, set)) ?? MirrorParams.Name
            }
        };
    }

    /// <summary>A category the AI names ("walls", "Generic Models", "OST_Walls") as the BuiltInCategory
    /// name the rules store; refused when Revit has no such category.</summary>
    private static string RuleCategory(Document doc, string name)
    {
        string key = CategoryKey(name);
        if (Enum.TryParse(key, true, out BuiltInCategory bic)) return bic.ToString();
        foreach (Category c in doc.Settings.Categories)
            if (c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) && Enum.IsDefined(typeof(BuiltInCategory), (int)c.Id.Value))
                return ((BuiltInCategory)(int)c.Id.Value).ToString();
        throw new McpError($"No category '{name}'. Use the category's name (e.g. 'Walls', 'Doors') or its OST_ name.");
    }

    /// <summary>
    /// Adds, replaces or deletes one rule, and/or turns live mode on or off, then saves the rules
    /// into the model the way the Formula Rules window does (checked against real elements first,
    /// refused when borrowed by someone else).
    /// </summary>
    private static JsonObject ParamRuleSet(Document doc, JsonObject a)
    {
        FormulaRuleSet set = ProjectRules(doc);
        string opened = set.ToJson();
        string? id = OptStr(a, "id");
        bool delete = (bool?)a["delete"] ?? false;
        bool? live = (bool?)a["live"];
        string? target = OptStr(a, "target");
        string? formula = OptStr(a, "formula");
        FormulaRule? existing = id == null ? null
            : set.Rules.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
              ?? throw new McpError($"No rule with id '{id}'. See param_rules.");

        string what;
        FormulaRule? rule = null;
        string? check = null;
        if (delete)
        {
            if (existing == null) throw new McpError("Name the rule to delete by its id.");
            set.Rules.Remove(existing);
            what = $"Deleted rule {existing}";
        }
        else if (target != null || formula != null || a["categories"] != null || a["enabled"] != null)
        {
            rule = existing?.Clone() ?? new FormulaRule();
            List<string> cats = StrList(a, "categories");
            if (cats.Count > 0) rule.Categories = cats.Select(c => RuleCategory(doc, c)).Distinct().ToList();
            if (formula != null) rule.Formula = formula;
            if ((bool?)a["enabled"] is bool en) rule.Enabled = en;
            if (OptStr(a, "note") is string note) rule.Note = note;
            if (target != null)
            {
                rule.TargetName = target;
                // The target as the editor offers it: a shared one keeps its GUID, a type one is on the type.
                List<ParamChoice> choices = RuleCheck.Targets(doc, RuleCheck.Samples(doc, rule.Categories));
                bool? onType = (bool?)a["target_on_type"];
                ParamChoice? hit = choices.FirstOrDefault(c => c.Name.Equals(target, StringComparison.OrdinalIgnoreCase)
                                                               && (onType == null || c.OnType == onType));
                rule.TargetGuid = hit?.Guid ?? "";
                rule.TargetOnType = hit?.OnType ?? onType ?? false;
                if (hit != null) rule.TargetName = hit.Name;
            }
            else if ((bool?)a["target_on_type"] is bool t) rule.TargetOnType = t;
            if (existing == null && (rule.Categories.Count == 0 || rule.TargetName.Length == 0 || rule.Formula.Length == 0))
                throw new McpError("A new rule needs categories, target and formula.");

            (bool ok, string message) = RuleCheck.Check(doc, rule, RuleCheck.Samples(doc, rule.Categories));
            if (!ok) throw new McpError("The rule was not saved: " + message);
            check = message;
            if (existing != null) set.Rules[set.Rules.IndexOf(existing)] = rule;
            else set.Rules.Add(rule);
            RuleOrder.Result order = RuleOrder.Sort(set.Rules);
            if (order.Refused.TryGetValue(rule.Id, out string? refused)) throw new McpError("The rule was not saved: " + refused);
            what = (existing == null ? "Added rule " : "Replaced rule ") + rule;
        }
        else if (live == null) throw new McpError("Give a rule (categories, target, formula), an id with delete:true, or live.");
        else what = "";

        if (live is bool l) set.Live = l;
        if (set.ToJson() == opened) return new JsonObject { ["saved"] = false, ["note"] = "The rules already say that; nothing to save." };

        var (saved, changedSince, why) = ParameterHelper.TrySave(doc, set, opened, replace: false);
        if (!saved) throw new McpError(changedSince ? "Someone changed the rules since they were read; call param_rules again." : why ?? "Not saved.");
        RuleOrder.Result after = RuleOrder.Sort(set.Rules);
        return new JsonObject
        {
            ["saved"] = true,
            ["change"] = what.Length > 0 ? what : null,
            ["check"] = check,
            ["rule"] = rule == null ? null : RuleJson(rule, after),
            ["live"] = set.Live,
            ["next"] = rule != null ? "Saved, not applied: call param_apply (dry_run first) to write the values." : null
        };
    }

    private static List<ElementId> ElementIds(List<string> keys) => keys.Select(k =>
        long.TryParse(k, out long v) && v > 0 ? new ElementId(v) : throw new McpError($"'{k}' is not an element id.")).ToList();

    private static JsonObject ParamApply(Document doc, List<string> ruleKeys, List<string> elementKeys, bool dryRun)
    {
        FormulaRuleSet set = ProjectRules(doc).Clone();
        if (ruleKeys.Count > 0)
        {
            bool named(FormulaRule r) => ruleKeys.Any(k => k.Equals(r.Id, StringComparison.OrdinalIgnoreCase)
                                                           || k.Equals(r.TargetName, StringComparison.OrdinalIgnoreCase));
            List<string> unknown = ruleKeys.Where(k => !set.Rules.Any(r => k.Equals(r.Id, StringComparison.OrdinalIgnoreCase)
                                                                          || k.Equals(r.TargetName, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0) throw new McpError($"No rule '{string.Join("', '", unknown)}' (by id or target). See param_rules.");
            foreach (FormulaRule r in set.Rules) r.Enabled &= named(r);
        }
        if (!set.Rules.Any(r => r.Enabled))
            throw new McpError(set.Rules.Count == 0 ? "This model has no formula rules. Add one with param_rule_set." : "None of those rules is switched on.");
        ICollection<ElementId>? only = elementKeys.Count > 0 ? ElementIds(elementKeys) : null;

        ApplyResult applied = FormulaApplier.Apply(doc, set, only, dryRun);
        return Rows(applied.Table(), new JsonObject
        {
            ["result"] = applied.Summary(dryRun),
            ["rules"] = Names(set.Rules.Where(x => x.Enabled).Select(x => x.ToString())),
            ["dry_run"] = dryRun
        });
    }

    // ------------------------------------------------------------------ Mirror Check

    private static JsonObject MirrorCheck(UIApplication app, Document doc, bool dryRun, bool setUp, bool? live)
    {
        FormulaRuleSet set = ProjectRules(doc);
        Guid target = MirrorParams.Target(doc, set);
        bool own = target == MirrorParams.Guid;
        MirrorParams.State p = MirrorParams.Read(doc, target);
        string? setUpDone = null;
        if (own && !p.Ready)
        {
            if (p.Clash != null) throw new McpError(p.Clash);
            if (!setUp)
            {
                if (!dryRun)
                    throw new McpError($"{MirrorParams.Name} is not set up in this model (" +
                                       (!p.Present ? "missing" : p.NotVarying ? "does not vary between groups" : $"{p.MissingCategories} categories lack it") +
                                       "). Ask the user, then call again with set_up:true - it adds the shared text parameter to every model category.");
            }
            else if (!dryRun)
            {
                string? why = MirrorParams.Ensure(app.Application, doc);
                if (why != null) throw new McpError("Set up failed: " + why);
                setUpDone = $"Set up {MirrorParams.Name} on every model category.";
            }
        }

        if (live is bool l && l != set.MirrorCheck && !dryRun)
        {
            string opened = set.ToJson();
            set.MirrorCheck = l;
            var (saved, _, why) = ParameterHelper.TrySave(doc, set, opened, replace: true);
            if (!saved) throw new McpError("Could not save the live setting: " + why);
        }
        else if (setUpDone != null) LiveFormulas.Refresh(doc);   // the triggers need the parameter to exist

        var (result, counts) = MirrorApplier.Apply(doc, null, dryRun, target);
        return Rows(result.Table(), new JsonObject
        {
            ["parameter"] = MirrorParams.NameOf(doc, target) ?? MirrorParams.Name,
            ["set_up"] = setUpDone,
            ["live"] = set.MirrorCheck,
            ["counts"] = new JsonObject
            {
                ["instances"] = counts.Instances,
                ["mirrored"] = counts.Mirrored,
                ["hand_flipped"] = counts.HandFlipped,
                ["facing_flipped"] = counts.FacingFlipped,
                ["flagged"] = counts.Flagged,
                ["out_of_date"] = counts.OutOfDate,
                ["without_parameter"] = counts.NoParameters
            },
            ["result"] = result.Summary(dryRun),
            ["dry_run"] = dryRun
        });
    }

    // ------------------------------------------------------------------ Multi Model Check / Action

    private static JsonNode ListActions() => new JsonArray(ActionRegistry.All().Select(x => (JsonNode)new JsonObject
    {
        ["name"] = x.Name,
        ["changes_model"] = x.ModifiesModel,
        ["columns"] = Names(x.Columns)
    }).ToArray());

    /// <summary>A registered action on the active model, as the button's "Run on active model" does
    /// (ActiveModelRunner): no logs, no save. A write action is one AI undo step; a check is not one.</summary>
    private static JsonNode RunAction(UIApplication app, Document doc, string name)
    {
        if (doc.IsFamilyDocument) throw new McpError("Actions run on project models only - the active document is a family.");
        IReadOnlyList<IBatchAction> all = ActionRegistry.All();
        // Create() falls back to the FIRST action on a miss; the AI must get the one it named or none.
        IBatchAction action = all.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpError($"No action '{name}'. Actions: {string.Join(" | ", all.Select(x => x.Name))}.");
        action = ActionRegistry.Create(action.Name);

        JsonObject run()
        {
            var rows = action.RunDetailRows(app, doc);
            var extra = new JsonObject { ["action"] = action.Name, ["changes_model"] = action.ModifiesModel };
            if (action.PerModelNote.Length > 0) extra["note"] = action.PerModelNote;
            return Rows(rows, extra);
        }
        return action.ModifiesModel ? Change(doc, action.Name, run) : run();
    }
}
#endif
