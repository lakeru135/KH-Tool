using System.Diagnostics;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Common;

namespace KhBatchRunner.ParamFormula;

public enum ApplyStatus { Changed, WouldChange, Unchanged, Skipped, Error }

/// <summary>One element × rule outcome. Unchanged ones are only counted, never listed.</summary>
public sealed class ApplyRow
{
    public string Rule { get; init; } = "";
    public long ElementId { get; init; }
    public string Element { get; init; } = "";
    public string Category { get; init; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public ApplyStatus Status { get; set; }
    public string Note { get; set; } = "";
}

public sealed class ApplyResult
{
    public List<ApplyRow> Rows { get; } = new();
    public int Unchanged { get; set; }
    /// <summary>Elements of a rule's categories that do not carry its target at all (not listed, but counted).</summary>
    public int NoTarget { get; set; }
    public int Changed => Rows.Count(r => r.Status == ApplyStatus.Changed);
    public int WouldChange => Rows.Count(r => r.Status == ApplyStatus.WouldChange);
    public int Skipped => Rows.Count(r => r.Status == ApplyStatus.Skipped);
    public int Errors => Rows.Count(r => r.Status == ApplyStatus.Error);
    public int Elements { get; set; }
    public TimeSpan Elapsed { get; set; }

    public string Summary(bool dryRun) =>
        (dryRun ? $"{WouldChange} would change" : $"{Changed} changed") +
        $", {Unchanged} already right, {Skipped} skipped, {Errors} error(s)" +
        (NoTarget > 0 ? $", {NoTarget} without the target parameter" : "") +
        $" — {Elements} element(s) in {Elapsed.TotalSeconds:0.0}s";

    public static readonly IReadOnlyList<string> Columns = new[] { "Rule", "Element", "Category", "Id", "From", "To", "Status", "Note" };

    public List<IReadOnlyDictionary<string, string>> Table() => Rows.Select(r => DrwRows.Row(Columns,
        r.Rule, r.Element, r.Category, r.ElementId.ToString(), r.From, r.To, r.Status.ToString(), r.Note)).ToList();
}

/// <summary>
/// Works every enabled rule out on every element it covers, then writes ONLY the values that
/// differ. That second half matters in a central model: writing a value an element already has
/// still borrows the element and locks it away from the rest of the team.
///
/// <para>Order of work: (1) compute everything, read-only, with chained rules seeing earlier rules'
/// new values; (2) in a workshared model, ask central for exactly the elements that will change
/// (CheckoutElements, outside any transaction) — anything owned by someone else or changed in
/// central is reported and left alone, so one locked element can never roll back everyone else's;
/// (3) one transaction writes the rest; (4) every write is read back after the commit before its
/// row says Changed (a commit is not proof of the edit).</para>
/// </summary>
internal static class FormulaApplier
{
    /// <summary>Set while an apply is writing, so the live updater does not recompute our own writes.</summary>
    public static bool Writing { get; private set; }

    private sealed class Planned
    {
        public Element Element = null!;
        public Parameter Target = null!;
        public FormulaValue Value;
        public ApplyRow Row = null!;
        public string TargetName = "";
        public string TargetGuid = "";
        public bool OnType;
    }

    /// <param name="only">Restrict to these elements (a selection, or what the live updater queued); null = every element.</param>
    /// <param name="dryRun">Compute and report what would change; touch nothing, borrow nothing.</param>
    public static ApplyResult Apply(Document doc, FormulaRuleSet set, ICollection<ElementId>? only, bool dryRun)
    {
        var watch = Stopwatch.StartNew();
        var result = new ApplyResult();
        List<Planned> plan = Compute(doc, set, only, result, dryRun);

        if (!dryRun && plan.Count > 0)
        {
            plan = Borrow(doc, plan);
            if (plan.Count > 0) Write(doc, plan);
        }
        result.Elapsed = watch.Elapsed;
        return result;
    }

    // ------------------------------------------------------------------ 1. compute

    private static List<Planned> Compute(Document doc, FormulaRuleSet set, ICollection<ElementId>? only,
        ApplyResult result, bool dryRun)
    {
        RuleOrder.Result order = RuleOrder.Sort(set.Rules);
        foreach (FormulaRule r in set.Rules.Where(r => r.Enabled && order.Refused.ContainsKey(r.Id)))
            result.Rows.Add(new ApplyRow { Rule = r.ToString(), Status = ApplyStatus.Error, Note = "Rule not run: " + order.Refused[r.Id] });

        HashSet<ElementId>? scope = only == null ? null : new HashSet<ElementId>(only);
        // New values earlier rules will write, so a chained rule reads them (element id, name).
        var overlay = new Dictionary<(ElementId, string), FormulaValue>();
        var plan = new List<Planned>();
        var seen = new HashSet<ElementId>();
        Dictionary<string, HashSet<string>>? typeNames = null;   // built once, only if a rule renames types

        // A type rule covers the types of what was selected (or a selected type itself).
        HashSet<ElementId>? typeScope = scope == null ? null : new HashSet<ElementId>(
            scope.Select(id => doc.GetElement(id)).Where(x => x != null)
                .Select(x => x is ElementType ? x!.Id : x!.GetTypeId())
                .Where(id => id != ElementId.InvalidElementId));

        foreach ((FormulaRule rule, Formula formula) in order.Ordered)
        {
            string ruleText = rule.ToString();
            bool onType = rule.TargetOnType;
            IEnumerable<Element> covered = onType ? ParamAccess.TypesOf(doc, rule.Categories) : ParamAccess.Instances(doc, rule.Categories);
            int evaluated = 0;
            var lacking = new List<ApplyRow>();
            foreach (Element e in covered)
            {
                if (onType ? typeScope != null && !typeScope.Contains(e.Id) : scope != null && !scope.Contains(e.Id)) continue;
                seen.Add(e.Id);
                Parameter? target = ParamAccess.Target(e, rule, out string? why);
                if (target == null)
                {
                    // An element of the category that simply lacks the parameter (the project parameter is
                    // bound to some families only) is not news on every run; an ambiguous one is.
                    if (why != null && why.Contains("ambiguous")) result.Rows.Add(Row(doc, e, ruleText, ApplyStatus.Error, why));
                    else result.NoTarget++;
                    continue;
                }
                if (!ParamAccess.Writable(target))
                {
                    result.Rows.Add(Row(doc, e, ruleText, ApplyStatus.Skipped, onType
                        ? $"[{rule.TargetName}] is read-only on this type (a formula in the family may drive it)"
                        : $"[{rule.TargetName}] is read-only on this element"));
                    continue;
                }
                if (!onType && InGroupWithoutVariance(e, target))
                {
                    result.Rows.Add(Row(doc, e, ruleText, ApplyStatus.Skipped, IsBuiltIn(target)
                        ? $"In group '{GroupName(doc, e)}': Revit changes [{rule.TargetName}] of a group member only in Edit Group"
                        : $"In group '{GroupName(doc, e)}': [{rule.TargetName}] is not set to vary between group instances"));
                    continue;
                }

                FormulaValue value;
                try
                {
                    var ctx = new FormulaContext(r =>
                    {
                        (Parameter? p, FormulaValue? v) = Source(e, r, onType, overlay);
                        return v ?? ParamAccess.Read(p!, r);
                    }, v => ParamAccess.Format(doc, v));
                    // The type check runs per element: the same name can be a length on one family and
                    // text on another.
                    FormulaType type = formula.Check(r =>
                    {
                        (Parameter? p, FormulaValue? v) = Source(e, r, onType, overlay);
                        return v?.Type ?? ParamAccess.TypeOf(p!);
                    });
                    string? fit = ParamAccess.Fits(target, type);
                    if (fit != null) { result.Rows.Add(Row(doc, e, ruleText, ApplyStatus.Error, fit)); continue; }
                    value = formula.Evaluate(ctx);
                }
                catch (MissingValueException ex)
                {
                    result.Rows.Add(Row(doc, e, ruleText, ApplyStatus.Skipped, ex.Message));
                    continue;
                }
                catch (ParamNotFoundException ex)
                {
                    ApplyRow lack = Row(doc, e, ruleText, ApplyStatus.Error, ex.Message);
                    result.Rows.Add(lack);
                    lacking.Add(lack);
                    continue;
                }
                catch (FormulaException ex)
                {
                    result.Rows.Add(Row(doc, e, ruleText, ApplyStatus.Error, ex.Message));
                    continue;
                }

                evaluated++;
                // Keyed by the element that OWNS the target: the type, for a type rule.
                overlay[(target.Element.Id, rule.TargetName.ToLowerInvariant())] = value;
                if (ParamAccess.Holds(doc, target, value)) { result.Unchanged++; continue; }

                // Renaming a type: refuse a name another type in the same family (or system family)
                // has, or that an earlier type in this run was just given. Revit's API would allow it.
                if (ParamAccess.IsTypeNameParam(target) && target.Element is ElementType renamed)
                {
                    typeNames ??= ParamAccess.TypeNames(doc);
                    string scopeKey = ParamAccess.NameScope(renamed);
                    if (!typeNames.TryGetValue(scopeKey, out HashSet<string>? names))
                        typeNames[scopeKey] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string newName = ParamAccess.Format(doc, value).Trim();
                    // Forbidden characters or an empty name: refused here, so Preview shows it too.
                    if (ParamAccess.BadNameWhy(newName) is string bad)
                    {
                        result.Rows.Add(Row(doc, e, ruleText, ApplyStatus.Error, bad + $" — the type keeps '{renamed.Name}'"));
                        continue;
                    }
                    if (names.Contains(newName) && !string.Equals(newName, renamed.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Rows.Add(Row(doc, e, ruleText, ApplyStatus.Error,
                            $"Another type is already called '{newName}' — type names must be unique, so this one keeps '{renamed.Name}'"));
                        continue;
                    }
                    names.Remove(renamed.Name);
                    names.Add(newName);
                }

                ApplyRow row = Row(doc, e, ruleText, dryRun ? ApplyStatus.WouldChange : ApplyStatus.Changed, "");
                row.From = ParamAccess.Show(target);
                row.To = ParamAccess.Preview(doc, target, value);
                result.Rows.Add(row);
                plan.Add(new Planned { Element = target.Element, Target = target, Value = value, Row = row, TargetName = rule.TargetName, OnType = onType,
                    TargetGuid = ParamAccess.IsTypeName(rule) ? rule.TargetGuid : GuidOf(target) });
            }
            // Lacking a parameter that others of the rule have is the element's business (a curtain wall
            // type has no Width): skipped. Lacking it everywhere means the rule is wrong: it stays an error.
            if (evaluated > 0)
                foreach (ApplyRow lack in lacking) lack.Status = ApplyStatus.Skipped;
        }
        result.Elements = seen.Count;
        return plan;
    }

    /// <summary>
    /// Where a formula's parameter comes from on this element: a value an earlier rule in this run is
    /// about to write (the overlay, keyed by the element that owns the parameter — the type, for a
    /// type parameter), else the parameter itself. A type rule reads its type only.
    /// </summary>
    private static (Parameter? Param, FormulaValue? Pending) Source(Element e, ParamRef r, bool onType,
        Dictionary<(ElementId, string), FormulaValue> overlay)
    {
        string key = r.Name.ToLowerInvariant();
        if (onType)
        {
            if (overlay.TryGetValue((e.Id, key), out FormulaValue tv)) return (null, tv);
            Parameter? tp = ParamAccess.FindForType(e, r, out string? twhy);
            return tp == null ? throw NotFound(twhy, r) : (tp, null);
        }
        if (!r.TypeOnly && overlay.TryGetValue((e.Id, key), out FormulaValue v)) return (null, v);
        Parameter? p = ParamAccess.Find(e, r, out string? why);
        ElementId typeId = e.GetTypeId();
        // Resolved on the type (or no such parameter yet on either): a type rule earlier in this run
        // may be writing it.
        if ((p == null || p.Element.Id != e.Id) && typeId != ElementId.InvalidElementId &&
            overlay.TryGetValue((typeId, key), out FormulaValue t)) return (null, t);
        return p == null ? throw NotFound(why, r) : (p, null);
    }

    private static FormulaException NotFound(string? why, ParamRef r) =>
        why != null && why.Contains("ambiguous") ? new FormulaException(why) : new ParamNotFoundException(why ?? $"{r} not found");

    /// <summary>
    /// A grouped element whose target does not vary between group instances: writing it would change
    /// every copy of the group, or make Revit offer to ungroup. Left alone and reported.
    /// </summary>
    private static bool InGroupWithoutVariance(Element e, Parameter target)
    {
        if (e.GroupId == ElementId.InvalidElementId) return false;
        if (target.Definition is InternalDefinition def)
        {
            // Built-ins cannot be set to vary, and Revit refuses them on a group member outside Edit
            // Group (Comments on grouped doors, Revit 2026.5, 2026-10-19).
            if (def.BuiltInParameter != BuiltInParameter.INVALID) return true;
            try { return !def.VariesAcrossGroups; } catch { return true; }
        }
        return true;
    }

    private static bool IsBuiltIn(Parameter p) =>
        p.Definition is InternalDefinition d && d.BuiltInParameter != BuiltInParameter.INVALID;

    private static string GroupName(Document doc, Element e)
    {
        try { return doc.GetElement(e.GroupId)?.Name ?? ""; } catch { return ""; }
    }

    private static ApplyRow Row(Document doc, Element e, string rule, ApplyStatus status, string note) => new()
    {
        Rule = rule,
        ElementId = e.Id.Value,
        Element = Describe(doc, e),
        Category = e.Category?.Name ?? "",
        Status = status,
        Note = note
    };

    internal static string Describe(Document doc, Element e)
    {
        string name = e.Name;
        if ((e as ElementType ?? doc.GetElement(e.GetTypeId())) is ElementType t)
            name = string.IsNullOrEmpty(t.FamilyName) ? t.Name : $"{t.FamilyName} : {t.Name}";
        string mark = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
        return mark.Length > 0 ? $"{name} ({mark})" : name;
    }

    // ------------------------------------------------------------------ 2. borrow (workshared only)

    /// <summary>
    /// Asks central for the elements that will change — only those, in one call — and returns the
    /// plan without the ones that cannot be edited now. Revit may borrow a whole group to borrow one
    /// member; grouped targets that do not vary were already left out, so that is rare.
    /// </summary>
    private static List<Planned> Borrow(Document doc, List<Planned> plan) =>
        Borrowing.Keep(doc, plan, p => p.Element.Id, Skip);

    private static void Skip(Planned p, string why)
    {
        p.Row.Status = ApplyStatus.Skipped;
        p.Row.Note = why;
    }

    // ------------------------------------------------------------------ 3–4. write, then prove it

    private static void Write(Document doc, List<Planned> plan, bool retried = false)
    {
        Writing = true;
        try
        {
            using Transaction t = DrwStrictFailures.Start(doc, "Parameter Helper: apply formulas", out DrwStrictFailures failures);
            foreach (Planned p in plan)
            {
                try
                {
                    if (!ParamAccess.Write(doc, p.Target, p.Value)) Fail(p, "Revit refused the value");
                }
                catch (Exception ex) { Fail(p, ex.Message); }
            }
            TransactionStatus st = t.Commit();
            if (st != TransactionStatus.Committed)
            {
                // One message per failing element, often the same one: say each once.
                List<string> errors = failures.Errors.Distinct().ToList();
                string why = errors.Count > 0 ? string.Join("; ", errors) : st.ToString();
                // A grouped element Revit will not change outside Edit Group (seen 2026-10-19 on Snowdon
                // Towers: built-in Comments on grouped doors) must not cost every other element its value.
                List<Planned> grouped = plan.Where(p => p.Row.Status == ApplyStatus.Changed &&
                                                        p.Element.GroupId != ElementId.InvalidElementId).ToList();
                if (!retried && grouped.Count > 0)
                {
                    foreach (Planned p in grouped)
                        Skip(p, $"In group '{GroupName(doc, p.Element)}': Revit allows this change only in Edit Group");
                    List<Planned> rest = plan.Where(p => p.Row.Status == ApplyStatus.Changed).ToList();
                    Writing = false;
                    if (rest.Count > 0) Write(doc, rest, retried: true);
                    return;
                }
                foreach (Planned p in plan.Where(p => p.Row.Status == ApplyStatus.Changed))
                    Fail(p, "Nothing was written — Revit rolled the change back: " + why);
                return;
            }
        }
        finally { Writing = false; }

        // Read every value back from a fresh element: a failure handler can roll back or delete
        // what a commit reported as done.
        foreach (Planned p in plan.Where(p => p.Row.Status == ApplyStatus.Changed))
        {
            Element? again = doc.GetElement(p.Element.Id);
            Parameter? now = again == null ? null : ParamAccess.Target(again, new FormulaRule { TargetName = p.TargetName, TargetGuid = p.TargetGuid, TargetOnType = p.OnType }, out _);
            if (now == null || !ParamAccess.Holds(doc, now, p.Value))
                Fail(p, "Written but not kept — the value read back is " + (now == null ? "missing" : "'" + ParamAccess.Show(now) + "'"));
        }
    }

    private static string GuidOf(Parameter p) => p.IsShared ? p.GUID.ToString() : "";

    private static void Fail(Planned p, string why)
    {
        p.Row.Status = ApplyStatus.Error;
        p.Row.Note = why;
    }
}
