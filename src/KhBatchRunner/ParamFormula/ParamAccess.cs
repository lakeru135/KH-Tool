using System.Globalization;
using Autodesk.Revit.DB;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// Turns Revit parameters into formula values and back. Values stay in Revit's internal units on
/// the way in and the way out, so a formula never depends on a project's display units — only text
/// output is formatted, in the project's own units, as Revit would show it.
/// </summary>
internal static class ParamAccess
{
    // ------------------------------------------------------------------ categories

    public static List<BuiltInCategory> Categories(IEnumerable<string> names)
    {
        var list = new List<BuiltInCategory>();
        foreach (string n in names)
            if (Enum.TryParse(n, out BuiltInCategory bic) && bic != BuiltInCategory.INVALID) list.Add(bic);
        return list;
    }

    public static string CategoryLabel(Document doc, string name)
    {
        if (!Enum.TryParse(name, out BuiltInCategory bic)) return name;
        try { return Category.GetCategory(doc, bic)?.Name ?? LabelUtils.GetLabelFor(bic); }
        catch { return name; }
    }

    /// <summary>Element instances (no types, no view-specific annotation of other views) in these categories.</summary>
    public static List<Element> Instances(Document doc, IEnumerable<string> categoryNames)
    {
        List<BuiltInCategory> cats = Categories(categoryNames);
        if (cats.Count == 0) return new List<Element>();
        return new FilteredElementCollector(doc)
            .WherePasses(new ElementMulticategoryFilter(cats))
            .WhereElementIsNotElementType()
            .ToElements().ToList();
    }

    /// <summary>Every element TYPE in these categories — what a type rule writes to, used or not.</summary>
    public static List<Element> TypesOf(Document doc, IEnumerable<string> categoryNames)
    {
        List<BuiltInCategory> cats = Categories(categoryNames);
        if (cats.Count == 0) return new List<Element>();
        return new FilteredElementCollector(doc)
            .WherePasses(new ElementMulticategoryFilter(cats))
            .WhereElementIsElementType()
            .ToElements().ToList();
    }

    // ------------------------------------------------------------------ the type's own name

    /// <summary>
    /// Marks a rule whose target is the TYPE NAME (in <see cref="FormulaRule.TargetGuid"/>, with
    /// <see cref="FormulaRule.TargetOnType"/>). Revit shows Type Name as a read-only parameter;
    /// writing it means renaming the type (<c>ElementType.Name</c>), which is what such a rule does.
    /// </summary>
    public const string TypeNameKey = "type-name";
    public const string TypeNameLabel = "Type Name";

    public static bool IsTypeName(FormulaRule rule) =>
        rule.TargetOnType && string.Equals(rule.TargetGuid, TypeNameKey, StringComparison.Ordinal);

    /// <summary>The parameter Revit reports the type's name through (read-only to Set; renamed via Name).</summary>
    public static bool IsTypeNameParam(Parameter p) =>
        p.Definition is InternalDefinition d &&
        (d.BuiltInParameter == BuiltInParameter.SYMBOL_NAME_PARAM || d.BuiltInParameter == BuiltInParameter.ALL_MODEL_TYPE_NAME);

    /// <summary>
    /// The set of types a name must be unique in, as Revit's own Rename enforces it: a loadable
    /// family's types per family; system types (walls, floors…) per class and category. The API does
    /// NOT enforce it — setting ElementType.Name to a name another type has succeeds and leaves two
    /// types called the same (seen in the self-test, Revit 2026.5) — so Parameter Helper does.
    /// </summary>
    public static string NameScope(ElementType t) =>
        t is FamilySymbol fs ? "family:" + fs.Family.Id.Value : "system:" + t.GetType().Name + ":" + (t.Category?.Id.Value ?? 0);

    /// <summary>Every type name in use, per <see cref="NameScope"/>, ignoring case as Revit does.</summary>
    public static Dictionary<string, HashSet<string>> TypeNames(Document doc)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (ElementType t in new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>())
        {
            string scope;
            try { scope = NameScope(t); } catch { continue; }
            if (!map.TryGetValue(scope, out HashSet<string>? names)) map[scope] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(t.Name);
        }
        return map;
    }

    /// <summary>Why Revit would refuse this as a name (forbidden characters, or empty), or null when it is fine.</summary>
    public static string? BadNameWhy(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "the formula gives an empty name — a type must have one";
        try
        {
            if (!NamingUtils.IsValidName(name))
                return $"'{name}' has characters Revit does not allow in a name ( \\ : {{ }} [ ] | ; < > ? ` ~ )";
        }
        catch { /* cannot tell: let Revit decide when it is set */ }
        return null;
    }

    /// <summary>Whether another type in the same scope already has this name (one type: the live path).</summary>
    public static bool NameTaken(Document doc, ElementType type, string name)
    {
        string scope = NameScope(type);
        return new FilteredElementCollector(doc).OfClass(type.GetType()).Cast<ElementType>()
            .Any(o => o.Id != type.Id && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) && NameScope(o) == scope);
    }

    /// <summary>Whether the target can be written: an ordinary writable parameter, or a type's name.</summary>
    public static bool Writable(Parameter p) => !p.IsReadOnly || (IsTypeNameParam(p) && p.Element is ElementType);

    // ------------------------------------------------------------------ what a spec measures

    public static Dim DimOf(ForgeTypeId? spec)
    {
        if (spec == null || spec.Empty()) return Dim.None;
        if (spec == SpecTypeId.Number || spec == SpecTypeId.Int.Integer) return Dim.None;
        if (spec == SpecTypeId.Length) return Dim.Length;
        if (spec == SpecTypeId.Area) return Dim.Area;
        if (spec == SpecTypeId.Volume) return Dim.Volume;
        try
        {
            if (!UnitUtils.IsMeasurableSpec(spec)) return Dim.None;
            IList<ForgeTypeId> units = UnitUtils.GetValidUnits(spec);
            // Length-like specs (bar diameter, section dimension, wall thickness…) measure in feet
            // internally and multiply into areas like any length.
            if (units.Contains(UnitTypeId.Millimeters)) return Dim.Length;
            if (units.Contains(UnitTypeId.SquareMeters)) return Dim.Area;
            if (units.Contains(UnitTypeId.CubicMeters)) return Dim.Volume;
        }
        catch { /* fall through: treat as its own unit */ }
        return new Dim(0, spec.TypeId);
    }

    /// <summary>The spec a measured result is shown in, for formatting it as text.</summary>
    public static ForgeTypeId SpecFor(Dim d)
    {
        if (d.Other.Length > 0 && d.L == 0) return new ForgeTypeId(d.Other);
        return d.L switch
        {
            1 => SpecTypeId.Length,
            2 => SpecTypeId.Area,
            3 => SpecTypeId.Volume,
            _ => SpecTypeId.Number
        };
    }

    public static ForgeTypeId? SpecOf(Parameter p)
    {
        try { return p.Definition?.GetDataType(); } catch { return null; }
    }

    public static bool IsYesNo(Parameter p) => SpecOf(p) == SpecTypeId.Boolean.YesNo;

    public static FormulaType TypeOf(Parameter p) => p.StorageType switch
    {
        StorageType.Double => new FormulaType(ValueKind.Number, DimOf(SpecOf(p))),
        StorageType.Integer => IsYesNo(p) ? FormulaType.Bool : FormulaType.Number,
        _ => FormulaType.Text   // String, and ElementId shown by its name
    };

    // ------------------------------------------------------------------ finding a parameter

    /// <summary>
    /// The parameter a formula names on this element: the instance's, else its type's (or only the
    /// type's for <c>[type:…]</c>). A name two parameters share on the same element is refused rather
    /// than guessed — the same rule DRW's view parameters follow.
    /// </summary>
    public static Parameter? Find(Element e, ParamRef r, out string? why)
    {
        why = null;
        if (!r.TypeOnly)
        {
            Parameter? own = FindOn(e, r.Name, out why);
            if (own != null || why != null) return own;
        }
        Element? type = e.Document.GetElement(e.GetTypeId());
        if (type != null)
        {
            Parameter? t = FindOn(type, r.Name, out why);
            if (t != null || why != null) return t;
        }
        why = $"{r} is not a parameter of this element{(r.TypeOnly ? "'s type" : " or its type")}";
        return null;
    }

    /// <summary>
    /// A parameter a TYPE rule reads: on the type and nowhere else, so <c>[Width]</c> and
    /// <c>[type:Width]</c> mean the same. A name only an instance has is refused, with the reason.
    /// </summary>
    public static Parameter? FindForType(Element type, ParamRef r, out string? why)
    {
        Parameter? p = FindOn(type, r.Name, out why);
        if (p != null || why != null) return p;
        why = $"[{r.Name}] is not a parameter of this type — a rule that writes a type parameter can only read type parameters";
        return null;
    }

    private static Parameter? FindOn(Element e, string name, out string? why)
    {
        why = null;
        IList<Parameter> found = e.GetParameters(name);
        if (found.Count == 1) return found[0];
        if (found.Count == 0) return null;
        why = $"[{name}] is ambiguous: {found.Count} parameters have this name on {(e is ElementType ? "the type" : "the element")}";
        return null;
    }

    /// <summary>
    /// The target: a shared parameter by GUID, otherwise the one parameter of that name — on the
    /// element for an instance rule, on its type for a type rule (<see cref="FormulaRule.TargetOnType"/>).
    /// </summary>
    public static Parameter? Target(Element e, FormulaRule rule, out string? why)
    {
        why = null;
        // A type rule's target lives on the type. Given an instance, look on its type.
        if (rule.TargetOnType && e is not ElementType)
        {
            if (e.Document.GetElement(e.GetTypeId()) is not ElementType t)
            {
                why = "this element has no type";
                return null;
            }
            e = t;
        }
        if (IsTypeName(rule))
        {
            Parameter? name = e.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM) ?? e.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME);
            if (name == null) why = "this type has no Type Name parameter";
            return name;
        }
        if (Guid.TryParse(rule.TargetGuid, out Guid g))
        {
            Parameter? byGuid = e.get_Parameter(g);
            if (byGuid == null) why = $"[{rule.TargetName}] (shared) is not on this element";
            return byGuid;
        }
        IList<Parameter> found = e.GetParameters(rule.TargetName);
        if (found.Count == 1) return found[0];
        if (found.Count == 0) { why = $"[{rule.TargetName}] is not {(e is ElementType ? "a type" : "an instance")} parameter of this element"; return null; }
        var project = found.Where(p => !p.IsShared &&
            (p.Definition as InternalDefinition)?.BuiltInParameter == BuiltInParameter.INVALID).ToList();
        if (project.Count == 1) return project[0];
        why = $"[{rule.TargetName}] is ambiguous: {found.Count} parameters have this name — pick it again in the rule";
        return null;
    }

    // ------------------------------------------------------------------ values

    /// <summary>The parameter's value. Throws <see cref="MissingValueException"/> when it has none.</summary>
    public static FormulaValue Read(Parameter p, ParamRef r)
    {
        switch (p.StorageType)
        {
            case StorageType.String:
                return FormulaValue.Str(p.AsString() ?? "");
            case StorageType.Double:
                if (!p.HasValue) throw new MissingValueException(r, $"{r} has no value");
                return FormulaValue.Num(p.AsDouble(), DimOf(SpecOf(p)));
            case StorageType.Integer:
                if (!p.HasValue) throw new MissingValueException(r, $"{r} has no value");
                return IsYesNo(p) ? FormulaValue.Yes(p.AsInteger() != 0) : FormulaValue.Num(p.AsInteger());
            case StorageType.ElementId:
                return FormulaValue.Str(p.AsValueString() ?? "");
            default:
                throw new MissingValueException(r, $"{r} has no readable value");
        }
    }

    /// <summary>A value as text, numbers formatted in the project's units as Revit shows them.</summary>
    public static string Format(Document doc, FormulaValue v)
    {
        switch (v.Kind)
        {
            case ValueKind.Text: return v.Text;
            case ValueKind.Bool: return v.Bool ? "Yes" : "No";
        }
        if (v.Dim.IsNumber) return v.Number.ToString("0.######", CultureInfo.InvariantCulture);
        try { return UnitFormatUtils.Format(doc.GetUnits(), SpecFor(v.Dim), v.Number, false); }
        catch { return v.Number.ToString("0.######", CultureInfo.InvariantCulture) + " (" + v.Dim + ")"; }
    }

    public static string Show(Parameter p)
    {
        try
        {
            if (p.StorageType == StorageType.String) return p.AsString() ?? "";
            if (!p.HasValue) return "";
            return p.AsValueString() ?? (p.StorageType == StorageType.Double
                ? p.AsDouble().ToString(CultureInfo.InvariantCulture)
                : p.AsInteger().ToString(CultureInfo.InvariantCulture));
        }
        catch { return ""; }
    }

    /// <summary>
    /// Why this result cannot go into this target, or null when it can. The same check the rule
    /// editor runs against a sample element and the applier runs on every element.
    /// </summary>
    public static string? Fits(Parameter target, FormulaType result)
    {
        switch (target.StorageType)
        {
            case StorageType.String:
                return null;   // anything can be written as text
            case StorageType.Double:
            {
                if (result.Kind != ValueKind.Number) return $"[{target.Definition.Name}] needs a number, the formula gives {result}";
                Dim want = DimOf(SpecOf(target));
                if (want == result.Dim) return null;
                return $"Inconsistent units: [{target.Definition.Name}] is a {want}, the formula gives {result.Dim}";
            }
            case StorageType.Integer:
                if (IsYesNo(target))
                    return result.Kind == ValueKind.Bool ? null : $"[{target.Definition.Name}] is yes/no, the formula gives {result}";
                return result.Kind == ValueKind.Number && result.Dim.IsNumber ? null
                    : $"[{target.Definition.Name}] needs a whole number, the formula gives {result}";
            default:
                return $"[{target.Definition.Name}] refers to an element; only text, number and yes/no targets are supported";
        }
    }

    /// <summary>
    /// Whether the target already holds this value — the test that keeps an unchanged element from
    /// being written (and, in a central model, borrowed).
    /// </summary>
    public static bool Holds(Document doc, Parameter target, FormulaValue v)
    {
        switch (target.StorageType)
        {
            case StorageType.String:
                return string.Equals(target.AsString() ?? "", Format(doc, v), StringComparison.Ordinal);
            case StorageType.Double:
                return target.HasValue && BinaryNode.Close(target.AsDouble(), v.Number);
            case StorageType.Integer:
                if (!target.HasValue) return false;
                return v.Kind == ValueKind.Bool
                    ? (target.AsInteger() != 0) == v.Bool
                    : target.AsInteger() == ToInt(v.Number);
        }
        return false;
    }

    public static bool Write(Document doc, Parameter target, FormulaValue v)
    {
        if (IsTypeNameParam(target) && target.Element is ElementType type)
        {
            // Renaming. Revit refuses a name another type already has, or characters it does not
            // allow — that exception is the caller's row note.
            string name = Format(doc, v).Trim();
            if (name.Length == 0) throw new ArgumentException("the formula gives an empty name — a type must have one");
            type.Name = name;
            return true;
        }
        return WriteValue(doc, target, v);
    }

    private static bool WriteValue(Document doc, Parameter target, FormulaValue v) => target.StorageType switch
    {
        StorageType.String => target.Set(Format(doc, v)),
        StorageType.Double => target.Set(v.Number),
        StorageType.Integer => target.Set(v.Kind == ValueKind.Bool ? (v.Bool ? 1 : 0) : ToInt(v.Number)),
        _ => false
    };

    private static int ToInt(double d) => (int)Math.Round(d, MidpointRounding.AwayFromZero);

    /// <summary>The result as the target will show it, for result rows.</summary>
    public static string Preview(Document doc, Parameter target, FormulaValue v)
    {
        if (target.StorageType == StorageType.Double && !v.Dim.IsNumber)
        {
            try { return UnitFormatUtils.Format(doc.GetUnits(), SpecOf(target) ?? SpecFor(v.Dim), v.Number, false); }
            catch { /* fall back below */ }
        }
        if (target.StorageType == StorageType.Integer && v.Kind == ValueKind.Number)
            return ToInt(v.Number).ToString(CultureInfo.InvariantCulture);
        return Format(doc, v);
    }
}
