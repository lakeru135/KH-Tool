using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// One Parameter Helper rule: on every element of <see cref="Categories"/>, write the value of
/// <see cref="Formula"/> into the instance parameter <see cref="TargetName"/> — what a schedule's
/// calculated value shows, but stored on the element so it can be tagged, filtered and exported.
/// </summary>
public sealed class FormulaRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    /// <summary>BuiltInCategory names ("OST_Walls"): names survive between Revit years, ids do not.</summary>
    public List<string> Categories { get; set; } = new();
    public string TargetName { get; set; } = "";
    /// <summary>The shared parameter's GUID; blank for a project parameter (found by name, never guessed).</summary>
    public string TargetGuid { get; set; } = "";
    /// <summary>
    /// The target is a TYPE parameter: one value per type, written on every type of the categories.
    /// Its formula may read type parameters only — a value shared by every instance cannot depend on
    /// any one instance.
    /// </summary>
    public bool TargetOnType { get; set; }
    public string Formula { get; set; } = "";
    public string Note { get; set; } = "";

    public FormulaRule Clone() => new()
    {
        Id = Id, Enabled = Enabled, Categories = new List<string>(Categories), TargetName = TargetName,
        TargetGuid = TargetGuid, TargetOnType = TargetOnType, Formula = Formula, Note = Note
    };

    public override string ToString() => $"{TargetName}{(TargetOnType ? " (type)" : "")} = {Formula}";
}

/// <summary>Every rule of one model, as it is saved in the model (and in an exported .json).</summary>
public sealed class FormulaRuleSet
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>Recalculate as people edit (the live updater). Off unless someone turns it on.</summary>
    public bool Live { get; set; }
    /// <summary>
    /// Mirror Check (1.5.3): keep the KH_Mirrored / flipped flags of family instances up to date as
    /// people edit. Saved here, beside the rules, so it travels with the model the same way. A
    /// KH-Tool older than 1.5.3 ignores it when reading, but drops it if it saves the rules.
    /// </summary>
    public bool MirrorCheck { get; set; }
    /// <summary>
    /// The shared text parameter Mirror Check writes, by GUID; blank = its own KH_Mirror State. When
    /// the chosen one is no longer in the model, Mirror Check falls back to its own.
    /// </summary>
    public string MirrorTargetGuid { get; set; } = "";
    /// <summary>The chosen parameter's name when it was chosen — shown when it has gone missing.</summary>
    public string MirrorTargetName { get; set; } = "";
    public List<FormulaRule> Rules { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static FormulaRuleSet FromJson(string json)
    {
        FormulaRuleSet set = JsonSerializer.Deserialize<FormulaRuleSet>(json, Json) ?? new FormulaRuleSet();
        set.Rules ??= new List<FormulaRule>();
        foreach (FormulaRule r in set.Rules) r.Categories ??= new List<string>();
        return set;
    }

    public FormulaRuleSet Clone() => FromJson(ToJson());
}

/// <summary>
/// The order rules run in, and the rules that cannot run. A rule that reads another rule's target
/// (on a category both cover) runs after it, so a chain settles in one pass. A cycle — a rule
/// reading its own target, or A reading B while B reads A — is refused: there is no order in which
/// it would stop changing.
/// </summary>
public static class RuleOrder
{
    public sealed class Result
    {
        public List<(FormulaRule Rule, Formula Formula)> Ordered { get; } = new();
        /// <summary>Rules that cannot run, keyed by rule id, with the reason for the user.</summary>
        public Dictionary<string, string> Refused { get; } = new();
    }

    public static Result Sort(IEnumerable<FormulaRule> rules)
    {
        var result = new Result();
        var parsed = new List<(FormulaRule Rule, Formula Formula)>();
        foreach (FormulaRule r in rules.Where(r => r.Enabled))
        {
            if (string.IsNullOrWhiteSpace(r.TargetName)) { result.Refused[r.Id] = "No target parameter."; continue; }
            if (r.Categories.Count == 0) { result.Refused[r.Id] = "No category."; continue; }
            try { parsed.Add((r, Formula.Parse(r.Formula))); }
            catch (FormulaException ex) { result.Refused[r.Id] = ex.Message; }
        }

        // An edge "a before b" whenever b reads a's target on a category they share.
        bool Feeds(FormulaRule a, (FormulaRule Rule, Formula Formula) b) =>
            a.Categories.Intersect(b.Rule.Categories, StringComparer.Ordinal).Any() &&
            b.Formula.Params().Any(p => string.Equals(p.Name, a.TargetName, StringComparison.OrdinalIgnoreCase));

        foreach (var p in parsed.Where(p => Feeds(p.Rule, p)))
            result.Refused[p.Rule.Id] = $"It reads its own target [{p.Rule.TargetName}].";

        var open = parsed.Where(p => !result.Refused.ContainsKey(p.Rule.Id)).ToList();
        // Two rules writing the same target on the same category: whichever ran last would win, and
        // which one that is would depend on list order nobody looks at.
        foreach (var p in open)
            foreach (var q in open)
                if (!ReferenceEquals(p.Rule, q.Rule) && open.IndexOf(q) > open.IndexOf(p) &&
                    string.Equals(p.Rule.TargetName, q.Rule.TargetName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Rule.TargetGuid, q.Rule.TargetGuid, StringComparison.OrdinalIgnoreCase) &&
                    p.Rule.TargetOnType == q.Rule.TargetOnType &&
                    p.Rule.Categories.Intersect(q.Rule.Categories, StringComparer.Ordinal).Any())
                    result.Refused[q.Rule.Id] = $"Another rule already writes [{q.Rule.TargetName}] on the same category.";
        open = open.Where(p => !result.Refused.ContainsKey(p.Rule.Id)).ToList();

        // Kahn's algorithm, keeping the user's list order among rules that do not depend on each other.
        var pending = new List<(FormulaRule Rule, Formula Formula)>(open);
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(b => !pending.Any(a => !ReferenceEquals(a.Rule, b.Rule) && Feeds(a.Rule, b)));
            if (ready.Rule == null)
            {
                string names = string.Join(" → ", pending.Select(p => "[" + p.Rule.TargetName + "]"));
                foreach (var p in pending) result.Refused[p.Rule.Id] = "These rules read each other in a loop: " + names + ".";
                break;
            }
            result.Ordered.Add(ready);
            pending.Remove(ready);
        }
        return result;
    }
}
