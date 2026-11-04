using Autodesk.Revit.DB;

namespace KhBatchRunner.ParamFormula;

/// <summary>A parameter the rule editor offers, as a target or as something a formula can read.</summary>
public sealed class ParamChoice
{
    public string Name { get; init; } = "";
    /// <summary>Shared parameter GUID; blank for project and built-in parameters.</summary>
    public string Guid { get; init; } = "";
    public bool OnType { get; init; }
    public string Kind { get; init; } = "";
    public string Label { get; set; } = "";
    /// <summary>What to type into a formula to read it.</summary>
    public string Token => OnType ? $"[type:{Name}]" : $"[{Name}]";
    public override string ToString() => Label;
}

/// <summary>
/// The rule editor's view of the model: which parameters the chosen categories carry, and what a
/// rule gives on a real element — checked the way <see cref="FormulaApplier"/> will run it.
/// </summary>
internal static class RuleCheck
{
    /// <summary>
    /// Up to <paramref name="perCategory"/> elements from EACH category (one of every type first), so
    /// a category with few elements is never crowded out by one with thousands.
    /// </summary>
    public static List<Element> Samples(Document doc, IEnumerable<string> categories, int perCategory = 15)
    {
        var list = new List<Element>();
        foreach (string cat in categories.Distinct())
        {
            var all = ParamAccess.Instances(doc, new[] { cat });
            list.AddRange(all.GroupBy(e => e.GetTypeId()).Select(g => g.First()).Concat(all).Distinct().Take(perCategory));
        }
        return list;
    }

    /// <summary>The chosen categories that have no element in the model yet (nothing to read parameters from).</summary>
    public static List<string> EmptyCategories(Document doc, IEnumerable<string> categories) =>
        categories.Distinct().Where(c => !ParamAccess.Instances(doc, new[] { c }).Any()).ToList();

    private static string CategoryOf(Element e) => e.Category?.BuiltInCategory.ToString() ?? "";

    /// <summary>
    /// Keeps the choices every category has. Within one category a parameter counts if ANY sample has
    /// it (families of one category can differ); across categories it must be in all of them — a rule
    /// over Walls and Floors can only use what walls and floors both carry.
    /// </summary>
    private static List<ParamChoice> Shared(IEnumerable<Element> samples, Action<Element, Dictionary<string, ParamChoice>> collect)
    {
        Dictionary<string, ParamChoice>? common = null;
        foreach (var byCategory in samples.GroupBy(CategoryOf))
        {
            var map = new Dictionary<string, ParamChoice>(StringComparer.OrdinalIgnoreCase);
            foreach (Element e in byCategory) collect(e, map);
            if (common == null) common = map;
            else foreach (string key in common.Keys.Where(k => !map.ContainsKey(k)).ToList()) common.Remove(key);
        }
        return Label((common ?? new Dictionary<string, ParamChoice>()).Values);
    }

    /// <summary>
    /// Writable parameters a formula result can go into — the element's and its type's — that every
    /// chosen category carries: text, number, integer and yes/no. A name two parameters share is
    /// shown twice, qualified, so the user can pick the right one (resolved by GUID when shared).
    /// </summary>
    public static List<ParamChoice> Targets(Document doc, IEnumerable<Element> samples) =>
        Shared(samples, (e, map) =>
        {
            foreach (Parameter p in e.Parameters) AddTarget(map, p, onType: false);
            if (doc.GetElement(e.GetTypeId()) is ElementType t)
            {
                foreach (Parameter p in t.Parameters) AddTarget(map, p, onType: true);
                // The type's own name: read-only as a parameter, but a rule can rename the type.
                map["t|type-name"] = new ParamChoice
                {
                    Name = ParamAccess.TypeNameLabel, Guid = ParamAccess.TypeNameKey, OnType = true, Kind = "text, renames the type"
                };
            }
        });

    private static void AddTarget(Dictionary<string, ParamChoice> map, Parameter p, bool onType)
    {
        // Read-only covers the computed ones (Area, Length) and, on a family type, a value a formula in
        // the family drives.
        if (p.IsReadOnly || p.StorageType == StorageType.None || p.StorageType == StorageType.ElementId) return;
        if (p.Definition is not InternalDefinition def) return;
        // Revit fills these itself or they key other behaviour; a formula must not fight them.
        if (def.BuiltInParameter is BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM or BuiltInParameter.ELEM_TYPE_PARAM
            or BuiltInParameter.ELEM_FAMILY_PARAM or BuiltInParameter.ELEM_PARTITION_PARAM
            or BuiltInParameter.DESIGN_OPTION_ID or BuiltInParameter.PHASE_CREATED or BuiltInParameter.PHASE_DEMOLISHED
            or BuiltInParameter.SYMBOL_NAME_PARAM or BuiltInParameter.ALL_MODEL_TYPE_NAME)
            return;
        string guid = p.IsShared ? p.GUID.ToString() : "";
        string key = (onType ? "t|" : "i|") + def.Name + "|" + guid + "|" + (def.BuiltInParameter == BuiltInParameter.INVALID ? "" : "b");
        if (!map.ContainsKey(key))
            map[key] = new ParamChoice { Name = def.Name, Guid = guid, Kind = KindOf(p), OnType = onType };
    }

    /// <summary>Everything a formula can read that every chosen category carries: the element's parameters and its type's.</summary>
    public static List<ParamChoice> Sources(Document doc, IEnumerable<Element> samples) =>
        Shared(samples, (e, map) =>
        {
            foreach (Parameter p in e.Parameters) Add(map, p, false);
            if (doc.GetElement(e.GetTypeId()) is ElementType t)
                foreach (Parameter p in t.Parameters) Add(map, p, true);
        });

    private static void Add(Dictionary<string, ParamChoice> map, Parameter p, bool onType)
    {
        if (p.Definition == null || p.StorageType == StorageType.None) return;
        string key = (onType ? "t|" : "i|") + p.Definition.Name;
        if (!map.ContainsKey(key))
            map[key] = new ParamChoice { Name = p.Definition.Name, OnType = onType, Kind = KindOf(p),
                Guid = p.IsShared ? p.GUID.ToString() : "" };
    }

    private static string KindOf(Parameter p)
    {
        string kind = ParamAccess.TypeOf(p).ToString();
        return p.StorageType == StorageType.Integer && kind == "number" ? "whole number" : kind;
    }

    private static List<ParamChoice> Label(IEnumerable<ParamChoice> items)
    {
        var list = items.ToList();
        foreach (var g in list.GroupBy(c => (c.Name, c.OnType), new NameTypeComparer()))
        {
            bool clash = g.Count() > 1;
            foreach (ParamChoice c in g)
            {
                string where = c.OnType ? "type · " : "";
                c.Label = clash
                    ? $"{c.Name}  ({where}{(c.Guid == ParamAccess.TypeNameKey ? "the type's name" : c.Guid.Length > 0 ? "shared " + c.Guid.Substring(0, 8) : "project/built-in")} · {c.Kind})"
                    : $"{c.Name}  ({where}{c.Kind})";
            }
        }
        // Alphabetical, as people look a parameter up by name; where a name is on both the element and
        // its type, the element's comes first.
        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.OnType).ToList();
    }

    private sealed class NameTypeComparer : IEqualityComparer<(string Name, bool OnType)>
    {
        public bool Equals((string Name, bool OnType) a, (string Name, bool OnType) b) =>
            a.OnType == b.OnType && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Name, bool OnType) x) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(x.Name) ^ (x.OnType ? 1 : 0);
    }

    /// <summary>
    /// Checks a rule against real elements: the formula parses, its units agree, the result fits
    /// the target, and what it gives on the first element that has the target. Returns
    /// (ok, message) — the message is shown under the formula box as the user types.
    /// </summary>
    public static (bool Ok, string Message) Check(Document doc, FormulaRule rule, IReadOnlyList<Element> samples)
    {
        Formula f;
        try { f = Formula.Parse(rule.Formula); }
        catch (FormulaException ex) { return (false, ex.Message); }

        if (rule.Categories.Count == 0) return (false, "Choose at least one category.");
        if (string.IsNullOrWhiteSpace(rule.TargetName)) return (false, "Choose the parameter the result is written to.");
        if (f.Params().Any(p => (rule.TargetOnType || !p.TypeOnly) && string.Equals(p.Name, rule.TargetName, StringComparison.OrdinalIgnoreCase)))
            return (false, $"The formula reads its own target [{rule.TargetName}].");
        if (samples.Count == 0) return (true, "The formula reads correctly. No element of these categories is in the model to try it on.");

        string? lastWhy = null;
        IEnumerable<Element> tryOn = rule.TargetOnType
            ? samples.Select(e => doc.GetElement(e.GetTypeId())).OfType<ElementType>().GroupBy(t => t.Id).Select(g => g.First()).Cast<Element>()
            : samples;
        foreach (Element e in tryOn)
        {
            Parameter? target = ParamAccess.Target(e, rule, out string? why);
            if (target == null) { lastWhy = why; continue; }
            try
            {
                FormulaType type = f.Check(r =>
                {
                    Parameter? p = rule.TargetOnType ? ParamAccess.FindForType(e, r, out string? w) : ParamAccess.Find(e, r, out w);
                    return p == null ? throw new FormulaException(w ?? $"{r} not found") : ParamAccess.TypeOf(p);
                });
                string? fit = ParamAccess.Fits(target, type);
                if (fit != null) return (false, fit);
                string sample;
                try
                {
                    FormulaValue v = f.Evaluate(new FormulaContext(r =>
                    {
                        Parameter? p = rule.TargetOnType ? ParamAccess.FindForType(e, r, out string? w) : ParamAccess.Find(e, r, out w);
                        return p == null ? throw new FormulaException(w ?? "") : ParamAccess.Read(p, r);
                    }, x => ParamAccess.Format(doc, x)));
                    sample = ParamAccess.Preview(doc, target, v);
                    if (ParamAccess.IsTypeNameParam(target) && ParamAccess.BadNameWhy(sample) is string bad)
                        return (false, bad);
                }
                catch (MissingValueException ex) { sample = "(skipped: " + ex.Message + ")"; }
                return (true, $"OK — gives {type}{(rule.TargetOnType ? ", one value per type" : "")}. Example, {Name(doc, e)}: {sample}");
            }
            catch (FormulaException ex) { return (false, ex.Message); }
        }
        return (false, lastWhy ?? $"No {(rule.TargetOnType ? "type" : "element")} of these categories has [{rule.TargetName}].");
    }

    private static string Name(Document doc, Element e)
    {
        if (e is ElementType et)
            return $"{(string.IsNullOrEmpty(et.FamilyName) ? et.Name : et.FamilyName + " : " + et.Name)} (type id {e.Id.Value})";
        string n = doc.GetElement(e.GetTypeId()) is ElementType t ? t.Name : e.Name;
        return $"{n} (id {e.Id.Value})";
    }
}
