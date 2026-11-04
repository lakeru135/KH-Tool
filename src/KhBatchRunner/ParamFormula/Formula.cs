using System.Globalization;
using System.Text;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// What a value measures, as Revit's formulas see it. Lengths multiply into areas and volumes, so
/// they are kept as a power of length (<see cref="L"/>: 1 length, 2 area, 3 volume, -1 per-length).
/// Anything else that carries a unit (an angle, a currency, a mass…) is <see cref="Other"/>: Revit's
/// spec id. Two such values may only meet in + - and comparisons when they are the same spec, and
/// only a plain number may scale one — the same rules that make Revit say "Inconsistent units".
/// </summary>
public readonly struct Dim : IEquatable<Dim>
{
    public int L { get; }
    public string Other { get; }

    public Dim(int l, string? other = null) { L = l; Other = other ?? ""; }

    public static readonly Dim None = new(0);
    public static readonly Dim Length = new(1);
    public static readonly Dim Area = new(2);
    public static readonly Dim Volume = new(3);

    public bool IsNumber => L == 0 && Other.Length == 0;

    public bool Equals(Dim o) => L == o.L && string.Equals(Other, o.Other, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is Dim d && Equals(d);
    public override int GetHashCode() => L * 397 ^ Other.GetHashCode();
    public static bool operator ==(Dim a, Dim b) => a.Equals(b);
    public static bool operator !=(Dim a, Dim b) => !a.Equals(b);

    public override string ToString()
    {
        if (Other.Length > 0) return L == 0 ? ShortSpec(Other) : $"{ShortSpec(Other)}·length^{L}";
        return L switch
        {
            0 => "number",
            1 => "length",
            2 => "area",
            3 => "volume",
            _ => $"length^{L}"
        };
    }

    /// <summary>"autodesk.spec.aec:angle-2.0.0" reads as "angle".</summary>
    private static string ShortSpec(string id)
    {
        int colon = id.LastIndexOf(':');
        string s = colon >= 0 ? id.Substring(colon + 1) : id;
        int dash = s.IndexOf('-');
        return dash > 0 ? s.Substring(0, dash) : s;
    }
}

public enum ValueKind { Number, Text, Bool }

/// <summary>The type of a value without the value: what a type check works with.</summary>
public readonly struct FormulaType : IEquatable<FormulaType>
{
    public ValueKind Kind { get; }
    public Dim Dim { get; }

    public FormulaType(ValueKind kind, Dim dim = default) { Kind = kind; Dim = kind == ValueKind.Number ? dim : Dim.None; }

    public static readonly FormulaType Number = new(ValueKind.Number, Dim.None);
    public static readonly FormulaType Text = new(ValueKind.Text);
    public static readonly FormulaType Bool = new(ValueKind.Bool);

    public bool Equals(FormulaType o) => Kind == o.Kind && Dim == o.Dim;
    public override bool Equals(object? obj) => obj is FormulaType t && Equals(t);
    public override int GetHashCode() => (int)Kind * 31 ^ Dim.GetHashCode();
    public override string ToString() => Kind switch
    {
        ValueKind.Text => "text",
        ValueKind.Bool => "yes/no",
        _ => Dim.ToString()
    };
}

/// <summary>
/// One value in a formula. Numbers are in Revit's INTERNAL units (feet, square feet…), never the
/// display units, so a formula means the same thing in every project whatever its unit settings.
/// </summary>
public readonly struct FormulaValue
{
    public ValueKind Kind { get; }
    public double Number { get; }
    public string Text { get; }
    public bool Bool { get; }
    public Dim Dim { get; }

    private FormulaValue(ValueKind kind, double n, string s, bool b, Dim d)
    { Kind = kind; Number = n; Text = s; Bool = b; Dim = d; }

    public static FormulaValue Num(double n, Dim d = default) => new(ValueKind.Number, n, "", false, d);
    public static FormulaValue Str(string s) => new(ValueKind.Text, 0, s ?? "", false, Dim.None);
    public static FormulaValue Yes(bool b) => new(ValueKind.Bool, 0, "", b, Dim.None);

    public FormulaType Type => new(Kind, Dim);

    public override string ToString() => Kind switch
    {
        ValueKind.Text => Text,
        ValueKind.Bool => Bool ? "Yes" : "No",
        _ => Number.ToString("0.######", CultureInfo.InvariantCulture) + (Dim.IsNumber ? "" : " (" + Dim + ")")
    };
}

/// <summary>A formula the parser or the type check refused; the message is for the user.</summary>
public class FormulaException : Exception
{
    public FormulaException(string message) : base(message) { }
}

/// <summary>
/// A formula names a parameter this element (or type) does not have. Not ambiguous, not a unit error:
/// the element simply lacks it — a curtain wall type has no Width. Reported as Skipped when other
/// elements of the rule have it, as an Error when none does (then the rule itself is wrong).
/// </summary>
public sealed class ParamNotFoundException : FormulaException
{
    public ParamNotFoundException(string message) : base(message) { }
}

/// <summary>
/// A source parameter had no value on this element. Not a mistake in the formula: the element is
/// skipped and its row says which parameter was empty.
/// </summary>
public sealed class MissingValueException : Exception
{
    public ParamRef Param { get; }
    public MissingValueException(ParamRef p, string why) : base(why) { Param = p; }
}

/// <summary>
/// A parameter named in a formula: <c>[Width]</c> (the instance's, else its type's),
/// <c>[type:Width]</c> (the type's only) or a bare <c>Width</c> when the name has no spaces.
/// </summary>
public sealed class ParamRef : IEquatable<ParamRef>
{
    public string Name { get; }
    public bool TypeOnly { get; }

    public ParamRef(string name, bool typeOnly) { Name = name.Trim(); TypeOnly = typeOnly; }

    public bool Equals(ParamRef? o) => o != null && TypeOnly == o.TypeOnly &&
        string.Equals(Name, o.Name, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => Equals(obj as ParamRef);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name) ^ (TypeOnly ? 1 : 0);
    public override string ToString() => TypeOnly ? $"[type:{Name}]" : $"[{Name}]";
}

// ================================================================================================
// Syntax tree
// ================================================================================================

public abstract class FormulaNode
{
    internal abstract FormulaType Check(Func<ParamRef, FormulaType> typeOf);
    internal abstract FormulaValue Eval(FormulaContext ctx);
    internal virtual void CollectParams(List<ParamRef> into) { }
}

/// <summary>What evaluation needs from its host: parameter values, and how to show a measured
/// number as text (Revit formats it in the project's units).</summary>
public sealed class FormulaContext
{
    public Func<ParamRef, FormulaValue> Lookup { get; }
    public Func<FormulaValue, string> Format { get; }

    public FormulaContext(Func<ParamRef, FormulaValue> lookup, Func<FormulaValue, string>? format = null)
    {
        Lookup = lookup;
        Format = format ?? (v => v.Kind == ValueKind.Number
            ? v.Number.ToString("0.######", CultureInfo.InvariantCulture)
            : v.ToString());
    }
}

internal sealed class NumNode : FormulaNode
{
    public double Value; public Dim Dim;
    internal override FormulaType Check(Func<ParamRef, FormulaType> typeOf) => new(ValueKind.Number, Dim);
    internal override FormulaValue Eval(FormulaContext ctx) => FormulaValue.Num(Value, Dim);
}

internal sealed class StrNode : FormulaNode
{
    public string Value = "";
    internal override FormulaType Check(Func<ParamRef, FormulaType> typeOf) => FormulaType.Text;
    internal override FormulaValue Eval(FormulaContext ctx) => FormulaValue.Str(Value);
}

internal sealed class BoolNode : FormulaNode
{
    public bool Value;
    internal override FormulaType Check(Func<ParamRef, FormulaType> typeOf) => FormulaType.Bool;
    internal override FormulaValue Eval(FormulaContext ctx) => FormulaValue.Yes(Value);
}

internal sealed class ParamNode : FormulaNode
{
    public ParamRef Ref = new("", false);
    internal override FormulaType Check(Func<ParamRef, FormulaType> typeOf) => typeOf(Ref);
    internal override FormulaValue Eval(FormulaContext ctx) => ctx.Lookup(Ref);
    internal override void CollectParams(List<ParamRef> into) { if (!into.Contains(Ref)) into.Add(Ref); }
}

internal sealed class UnaryNode : FormulaNode
{
    public FormulaNode X = null!;
    internal override FormulaType Check(Func<ParamRef, FormulaType> typeOf)
    {
        FormulaType t = X.Check(typeOf);
        if (t.Kind != ValueKind.Number) throw new FormulaException($"'-' needs a number, not {t}.");
        return t;
    }
    internal override FormulaValue Eval(FormulaContext ctx)
    {
        FormulaValue v = X.Eval(ctx);
        return FormulaValue.Num(-v.Number, v.Dim);
    }
    internal override void CollectParams(List<ParamRef> into) => X.CollectParams(into);
}

internal sealed class BinaryNode : FormulaNode
{
    public string Op = "";
    public FormulaNode A = null!, B = null!;

    internal override void CollectParams(List<ParamRef> into) { A.CollectParams(into); B.CollectParams(into); }

    internal override FormulaType Check(Func<ParamRef, FormulaType> typeOf)
    {
        FormulaType a = A.Check(typeOf), b = B.Check(typeOf);
        switch (Op)
        {
            case "&":
                return FormulaType.Text;   // anything can be joined into text
            case "+":
            case "-":
                if (a.Kind == ValueKind.Text && b.Kind == ValueKind.Text && Op == "+")
                    throw new FormulaException("Use '&' to join text, not '+'.");
                RequireNumbers(a, b);
                if (a.Dim != b.Dim) throw Inconsistent(a, b);
                return a;
            case "*":
                RequireNumbers(a, b);
                return new FormulaType(ValueKind.Number, Multiply(a.Dim, b.Dim));
            case "/":
                RequireNumbers(a, b);
                return new FormulaType(ValueKind.Number, Divide(a.Dim, b.Dim));
            case "^":
                RequireNumbers(a, b);
                if (!b.Dim.IsNumber) throw new FormulaException("A power must be a plain number.");
                if (a.Dim.IsNumber) return FormulaType.Number;
                if (B is not NumNode n || n.Value != Math.Floor(n.Value) || a.Dim.Other.Length > 0)
                    throw new FormulaException($"A {a.Dim} can only be raised to a whole-number power written in the formula (e.g. ^2).");
                return new FormulaType(ValueKind.Number, new Dim(a.Dim.L * (int)n.Value));
            case "=":
            case "<>":
                if (a.Kind != b.Kind) throw new FormulaException($"Cannot compare {a} with {b}.");
                if (a.Kind == ValueKind.Number && a.Dim != b.Dim) throw Inconsistent(a, b);
                return FormulaType.Bool;
            default: // < > <= >=
                RequireNumbers(a, b);
                if (a.Dim != b.Dim) throw Inconsistent(a, b);
                return FormulaType.Bool;
        }
    }

    internal override FormulaValue Eval(FormulaContext ctx)
    {
        FormulaValue a = A.Eval(ctx), b = B.Eval(ctx);
        switch (Op)
        {
            case "&": return FormulaValue.Str(AsText(a, ctx) + AsText(b, ctx));
            case "+": return FormulaValue.Num(a.Number + b.Number, a.Dim);
            case "-": return FormulaValue.Num(a.Number - b.Number, a.Dim);
            case "*": return FormulaValue.Num(a.Number * b.Number, Multiply(a.Dim, b.Dim));
            case "/":
                if (b.Number == 0) throw new FormulaException("Division by zero.");
                return FormulaValue.Num(a.Number / b.Number, Divide(a.Dim, b.Dim));
            case "^":
                return FormulaValue.Num(Math.Pow(a.Number, b.Number),
                    a.Dim.IsNumber ? Dim.None : new Dim(a.Dim.L * (int)b.Number));
            case "=": return FormulaValue.Yes(Same(a, b));
            case "<>": return FormulaValue.Yes(!Same(a, b));
            case "<": return FormulaValue.Yes(a.Number < b.Number && !Close(a.Number, b.Number));
            case ">": return FormulaValue.Yes(a.Number > b.Number && !Close(a.Number, b.Number));
            case "<=": return FormulaValue.Yes(a.Number <= b.Number || Close(a.Number, b.Number));
            case ">=": return FormulaValue.Yes(a.Number >= b.Number || Close(a.Number, b.Number));
        }
        throw new FormulaException("Unknown operator " + Op);
    }

    internal static string AsText(FormulaValue v, FormulaContext ctx) => v.Kind switch
    {
        ValueKind.Text => v.Text,
        ValueKind.Bool => v.Bool ? "Yes" : "No",
        _ => ctx.Format(v)
    };

    private static bool Same(FormulaValue a, FormulaValue b) => a.Kind switch
    {
        ValueKind.Text => string.Equals(a.Text, b.Text, StringComparison.Ordinal),
        ValueKind.Bool => a.Bool == b.Bool,
        _ => Close(a.Number, b.Number)
    };

    /// <summary>Equal for a model's purposes: internal-unit doubles that differ by rounding noise.</summary>
    internal static bool Close(double a, double b) =>
        Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));

    private static void RequireNumbers(FormulaType a, FormulaType b)
    {
        if (a.Kind != ValueKind.Number || b.Kind != ValueKind.Number)
            throw new FormulaException($"Cannot do arithmetic with {(a.Kind != ValueKind.Number ? a : b)}.");
    }

    internal static FormulaException Inconsistent(FormulaType a, FormulaType b)
    {
        string hint = (a.Dim.IsNumber && b.Dim.L > 0) || (b.Dim.IsNumber && a.Dim.L > 0)
            ? " Write the unit on the number, e.g. 100 mm or 2.5 m²."
            : "";
        return new FormulaException($"Inconsistent units: {a} and {b}.{hint}");
    }

    internal static Dim Multiply(Dim a, Dim b)
    {
        if (a.Other.Length > 0 && b.Other.Length > 0)
            throw new FormulaException($"Inconsistent units: cannot multiply {a} by {b}.");
        return new Dim(a.L + b.L, a.Other.Length > 0 ? a.Other : b.Other);
    }

    internal static Dim Divide(Dim a, Dim b)
    {
        if (b.Other.Length > 0)
        {
            if (a.Other == b.Other) return new Dim(a.L - b.L);
            throw new FormulaException($"Inconsistent units: cannot divide {a} by {b}.");
        }
        return new Dim(a.L - b.L, a.Other);
    }
}

internal sealed class CallNode : FormulaNode
{
    public string Name = "";
    public List<FormulaNode> Args = new();

    internal override void CollectParams(List<ParamRef> into) { foreach (FormulaNode a in Args) a.CollectParams(into); }

    private void Count(int min, int max)
    {
        if (Args.Count < min || Args.Count > max)
            throw new FormulaException(min == max
                ? $"{Name}() takes {min} value{(min == 1 ? "" : "s")}, not {Args.Count}."
                : $"{Name}() takes {min} to {(max == int.MaxValue ? "any number of" : max.ToString())} values, not {Args.Count}.");
    }

    internal override FormulaType Check(Func<ParamRef, FormulaType> typeOf)
    {
        var t = Args.Select(a => a.Check(typeOf)).ToList();
        switch (Name)
        {
            case "if":
                Count(3, 3);
                if (t[0].Kind != ValueKind.Bool) throw new FormulaException($"if() needs a yes/no test first, not {t[0]}.");
                if (!t[1].Equals(t[2])) throw new FormulaException($"if() must give the same kind of value both ways: {t[1]} or {t[2]}.");
                return t[1];
            case "and":
            case "or":
                Count(1, int.MaxValue);
                if (t.Any(x => x.Kind != ValueKind.Bool)) throw new FormulaException($"{Name}() takes yes/no values only.");
                return FormulaType.Bool;
            case "not":
                Count(1, 1);
                if (t[0].Kind != ValueKind.Bool) throw new FormulaException("not() takes a yes/no value.");
                return FormulaType.Bool;
            case "round":
            case "roundup":
            case "rounddown":
                Count(1, 2);
                if (t[0].Kind != ValueKind.Number) throw new FormulaException($"{Name}() takes a number.");
                if (Args.Count == 1)
                {
                    if (!t[0].Dim.IsNumber)
                        throw new FormulaException($"{Name}() of a {t[0].Dim} needs a step: {Name}(x, 5 mm).");
                    return t[0];
                }
                if (t[1].Kind != ValueKind.Number || t[1].Dim != t[0].Dim)
                    throw new FormulaException($"{Name}(x, step): the step must be a {t[0].Dim} like x.");
                return t[0];
            case "abs":
                Count(1, 1);
                if (t[0].Kind != ValueKind.Number) throw new FormulaException("abs() takes a number.");
                return t[0];
            case "sqrt":
                Count(1, 1);
                if (t[0].Kind != ValueKind.Number || t[0].Dim.Other.Length > 0 || t[0].Dim.L % 2 != 0)
                    throw new FormulaException($"sqrt() of a {t[0]} has no unit Revit knows.");
                return new FormulaType(ValueKind.Number, new Dim(t[0].Dim.L / 2));
            case "min":
            case "max":
                Count(1, int.MaxValue);
                if (t.Any(x => x.Kind != ValueKind.Number)) throw new FormulaException($"{Name}() takes numbers.");
                if (t.Any(x => x.Dim != t[0].Dim)) throw BinaryNode.Inconsistent(t[0], t.First(x => x.Dim != t[0].Dim));
                return t[0];
        }
        throw new FormulaException($"Unknown function {Name}(). Known: if, and, or, not, round, roundup, rounddown, abs, sqrt, min, max.");
    }

    internal override FormulaValue Eval(FormulaContext ctx)
    {
        switch (Name)
        {
            case "if":
                return Args[0].Eval(ctx).Bool ? Args[1].Eval(ctx) : Args[2].Eval(ctx);
            case "and":
                foreach (FormulaNode a in Args) if (!a.Eval(ctx).Bool) return FormulaValue.Yes(false);
                return FormulaValue.Yes(true);
            case "or":
                foreach (FormulaNode a in Args) if (a.Eval(ctx).Bool) return FormulaValue.Yes(true);
                return FormulaValue.Yes(false);
            case "not":
                return FormulaValue.Yes(!Args[0].Eval(ctx).Bool);
        }

        var v = Args.Select(a => a.Eval(ctx)).ToList();
        FormulaValue x = v[0];
        switch (Name)
        {
            case "round":
            case "roundup":
            case "rounddown":
            {
                double step = v.Count > 1 ? v[1].Number : 1.0;
                if (step == 0) throw new FormulaException($"{Name}(): the step cannot be zero.");
                double q = x.Number / step;
                // Round half away from zero, as Revit does; snap noise first so 2.4999999999 is 2.5.
                double snapped = Math.Round(q, 9);
                double r = Name == "round" ? Math.Round(snapped, MidpointRounding.AwayFromZero)
                    : Name == "roundup" ? Math.Ceiling(snapped) : Math.Floor(snapped);
                return FormulaValue.Num(r * step, x.Dim);
            }
            case "abs": return FormulaValue.Num(Math.Abs(x.Number), x.Dim);
            case "sqrt":
                if (x.Number < 0) throw new FormulaException("sqrt() of a negative number.");
                return FormulaValue.Num(Math.Sqrt(x.Number), new Dim(x.Dim.L / 2));
            case "min": return v.Aggregate((p, q) => q.Number < p.Number ? q : p);
            case "max": return v.Aggregate((p, q) => q.Number > p.Number ? q : p);
        }
        throw new FormulaException("Unknown function " + Name);
    }
}

// ================================================================================================
// Parser
// ================================================================================================

/// <summary>
/// A parsed formula. Grammar, loosest to tightest binding:
/// comparison (<c>= &lt;&gt; &lt; &gt; &lt;= &gt;=</c>) · <c>&amp;</c> (join text) · <c>+ -</c> ·
/// <c>* /</c> · unary <c>-</c> · <c>^</c> (right-associative) · atoms: numbers with an optional unit
/// (<c>100 mm</c>, <c>2.5 m²</c>), <c>"text"</c>, <c>yes</c>/<c>no</c>, parameters
/// (<c>[Unconnected Height]</c>, <c>[type:Width]</c>, <c>Width</c>), function calls, parentheses.
/// </summary>
public sealed class Formula
{
    public string Source { get; }
    internal FormulaNode Root { get; }

    private Formula(string source, FormulaNode root) { Source = source; Root = root; }

    /// <summary>Unit words after a number, as a factor into internal units (feet) and a power of length.</summary>
    private static readonly Dictionary<string, (double Factor, int L)> Units = new(StringComparer.Ordinal)
    {
        ["mm"] = (1 / 304.8, 1), ["cm"] = (1 / 30.48, 1), ["m"] = (1 / 0.3048, 1),
        ["ft"] = (1, 1), ["in"] = (1 / 12.0, 1),
        ["mm2"] = (Math.Pow(1 / 304.8, 2), 2), ["mm²"] = (Math.Pow(1 / 304.8, 2), 2),
        ["cm2"] = (Math.Pow(1 / 30.48, 2), 2), ["cm²"] = (Math.Pow(1 / 30.48, 2), 2),
        ["m2"] = (Math.Pow(1 / 0.3048, 2), 2), ["m²"] = (Math.Pow(1 / 0.3048, 2), 2),
        ["ft2"] = (1, 2), ["ft²"] = (1, 2),
        ["mm3"] = (Math.Pow(1 / 304.8, 3), 3), ["mm³"] = (Math.Pow(1 / 304.8, 3), 3),
        ["cm3"] = (Math.Pow(1 / 30.48, 3), 3), ["cm³"] = (Math.Pow(1 / 30.48, 3), 3),
        ["m3"] = (Math.Pow(1 / 0.3048, 3), 3), ["m³"] = (Math.Pow(1 / 0.3048, 3), 3),
        ["ft3"] = (1, 3), ["ft³"] = (1, 3),
    };

    public static Formula Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormulaException("The formula is empty.");
        var p = new Parser(text);
        FormulaNode root = p.ParseComparison();
        p.ExpectEnd();
        return new Formula(text, root);
    }

    /// <summary>The value's type for these parameter types, or a <see cref="FormulaException"/> saying why it has none.</summary>
    public FormulaType Check(Func<ParamRef, FormulaType> typeOf) => Root.Check(typeOf);

    public FormulaValue Evaluate(FormulaContext ctx) => Root.Eval(ctx);

    /// <summary>Every parameter the formula reads, in first-mention order.</summary>
    public IReadOnlyList<ParamRef> Params()
    {
        var list = new List<ParamRef>();
        Root.CollectParams(list);
        return list;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) { _s = s; }

        private void SkipSpace() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        private bool Peek(string tok)
        {
            SkipSpace();
            return string.CompareOrdinal(_s, _i, tok, 0, tok.Length) == 0;
        }

        private bool Take(string tok)
        {
            if (!Peek(tok)) return false;
            _i += tok.Length;
            return true;
        }

        private FormulaException Error(string what) =>
            new($"{what} at character {_i + 1}: …{Snippet()}");

        private string Snippet()
        {
            int start = Math.Max(0, _i - 10);
            string s = _s.Substring(start, Math.Min(_s.Length - start, 20));
            return s.Length == 0 ? "(end)" : s;
        }

        public void ExpectEnd()
        {
            SkipSpace();
            if (_i < _s.Length) throw Error("Unexpected text");
        }

        public FormulaNode ParseComparison()
        {
            FormulaNode left = ParseConcat();
            while (true)
            {
                string? op = Take("<>") ? "<>" : Take("<=") ? "<=" : Take(">=") ? ">=" :
                    Take("=") ? "=" : Take("<") ? "<" : Take(">") ? ">" : null;
                if (op == null) return left;
                left = new BinaryNode { Op = op, A = left, B = ParseConcat() };
            }
        }

        private FormulaNode ParseConcat()
        {
            FormulaNode left = ParseAdd();
            while (Take("&")) left = new BinaryNode { Op = "&", A = left, B = ParseAdd() };
            return left;
        }

        private FormulaNode ParseAdd()
        {
            FormulaNode left = ParseMul();
            while (true)
            {
                string? op = Take("+") ? "+" : Take("-") ? "-" : null;
                if (op == null) return left;
                left = new BinaryNode { Op = op, A = left, B = ParseMul() };
            }
        }

        private FormulaNode ParseMul()
        {
            FormulaNode left = ParseUnary();
            while (true)
            {
                string? op = Take("*") ? "*" : Take("/") ? "/" : null;
                if (op == null) return left;
                left = new BinaryNode { Op = op, A = left, B = ParseUnary() };
            }
        }

        private FormulaNode ParseUnary()
        {
            if (Take("-")) return new UnaryNode { X = ParseUnary() };
            if (Take("+")) return ParseUnary();
            return ParsePower();
        }

        private FormulaNode ParsePower()
        {
            FormulaNode b = ParseAtom();
            if (Take("^")) return new BinaryNode { Op = "^", A = b, B = ParseUnary() };
            return b;
        }

        private FormulaNode ParseAtom()
        {
            SkipSpace();
            if (_i >= _s.Length) throw Error("The formula ends too early");
            char c = _s[_i];

            if (c == '(')
            {
                _i++;
                FormulaNode inner = ParseComparison();
                if (!Take(")")) throw Error("Missing ')'");
                return inner;
            }
            if (c == '"') return ParseString();
            if (c == '[') return ParseBracketParam();
            if (char.IsDigit(c) || c == '.') return ParseNumber();
            if (char.IsLetter(c) || c == '_') return ParseWord();
            throw Error($"Unexpected '{c}'");
        }

        private FormulaNode ParseString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == '"')
                {
                    if (_i < _s.Length && _s[_i] == '"') { sb.Append('"'); _i++; continue; } // "" = a quote
                    return new StrNode { Value = sb.ToString() };
                }
                sb.Append(c);
            }
            throw Error("Text is missing its closing '\"'");
        }

        private FormulaNode ParseBracketParam()
        {
            int start = ++_i;
            int end = _s.IndexOf(']', start);
            if (end < 0) throw Error("Missing ']' after a parameter name");
            string inside = _s.Substring(start, end - start).Trim();
            _i = end + 1;
            bool typeOnly = false;
            if (inside.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                typeOnly = true;
                inside = inside.Substring(5).Trim();
            }
            if (inside.Length == 0) throw Error("Empty parameter name '[]'");
            return new ParamNode { Ref = new ParamRef(inside, typeOnly) };
        }

        private FormulaNode ParseNumber()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            // An exponent: 1e3, 2.5E-2.
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E') && _i + 1 < _s.Length &&
                (char.IsDigit(_s[_i + 1]) || ((_s[_i + 1] == '-' || _s[_i + 1] == '+') && _i + 2 < _s.Length && char.IsDigit(_s[_i + 2]))))
            {
                _i += 2;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            string num = _s.Substring(start, _i - start);
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw Error($"'{num}' is not a number");

            // An optional unit word straight after it: "100 mm", "2.5m²".
            int save = _i;
            SkipSpace();
            int wordStart = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '²' || _s[_i] == '³')) _i++;
            string word = _s.Substring(wordStart, _i - wordStart);
            if (word.Length > 0 && Units.TryGetValue(word, out var unit))
                return new NumNode { Value = value * unit.Factor, Dim = new Dim(unit.L) };
            _i = save;
            return new NumNode { Value = value, Dim = Dim.None };
        }

        private FormulaNode ParseWord()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            string word = _s.Substring(start, _i - start);
            string lower = word.ToLowerInvariant();

            if (Peek("("))
            {
                Take("(");
                var call = new CallNode { Name = lower };
                if (!Take(")"))
                {
                    // Revit's formulas separate arguments with ','; ';' is accepted for locales that
                    // use ',' as the decimal mark.
                    do call.Args.Add(ParseComparison());
                    while (Take(",") || Take(";"));
                    if (!Take(")")) throw Error($"Missing ')' to close {word}(");
                }
                return call;
            }
            if (lower is "yes" or "true") return new BoolNode { Value = true };
            if (lower is "no" or "false") return new BoolNode { Value = false };
            return new ParamNode { Ref = new ParamRef(word, false) };
        }
    }
}
