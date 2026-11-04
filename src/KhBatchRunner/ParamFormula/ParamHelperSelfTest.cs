#if !EDITION_DRW
using System.Diagnostics;
using System.IO;
using Application = Autodesk.Revit.ApplicationServices.Application;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// Parameter Helper's headless self-test. Launch Revit with <c>PARAMHELPER_SELFTEST=&lt;report&gt;</c>
/// and <c>PARAMHELPER_SELFTEST_DIR=&lt;empty work folder&gt;</c>. On the first Idling tick it:
/// <list type="number">
/// <item>runs the formula language and rule-order cases (no model);</item>
/// <item>builds a small metric project from Revit's template — walls, shared parameters bound to
/// Walls — and runs Apply, Preview, chains, units, groups, the in-model store and live mode on it,
/// checking every result by reading the model back;</item>
/// <item>turns a copy into a file-based CENTRAL model with two local copies and runs the
/// worksharing cases: rules travelling by Sync/Reload Latest, Apply borrowing only what changes,
/// stale values after Reload Latest, and elements changed in central since the local was updated;</item>
/// <item>times Apply and one live edit on a larger model (the Phase 0 cost question).</item>
/// </list>
/// Writes PASS/FAIL lines and a RESULT line, then shuts Revit down.
///
/// <para>Not testable here: an element owned by ANOTHER user. Ownership in a file-based central is
/// per Revit user name, the API cannot change the user name, and two locals opened by one user are
/// both "current user". That path (CheckoutElements not returning the id) is exercised by its
/// return value only; it needs two people on a real central.</para>
/// </summary>
internal static partial class ParamHelperSelfTest
{
    public const string ReportVariable = "PARAMHELPER_SELFTEST";
    public const string DirVariable = "PARAMHELPER_SELFTEST_DIR";
    /// <summary>Optional: how many walls the timing case builds (default 2000).</summary>
    public const string ScaleVariable = "PARAMHELPER_SELFTEST_WALLS";
    /// <summary>Optional: "mirror" runs Mirror Check's cases only.</summary>
    public const string OnlyVariable = "PARAMHELPER_SELFTEST_ONLY";

    private static UIControlledApplication? _app;
    private static EventHandler<IdlingEventArgs>? _handler;
    private static string _report = "", _dir = "";
    private static int _pass, _fail;
    private static readonly List<string> Failures = new();
    private static string _scenario = "";

    public static bool IsRequested(out string report, out string dir)
    {
        report = Environment.GetEnvironmentVariable(ReportVariable) ?? "";
        dir = Environment.GetEnvironmentVariable(DirVariable) ?? "";
        return report.Length > 0 && dir.Length > 0;
    }

    public static void ArmOnIdling(UIControlledApplication app, string report, string dir)
    {
        _app = app;
        _report = report;
        _dir = dir;
        _handler = OnIdling;
        app.Idling += _handler;
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (_app != null && _handler != null) _app.Idling -= _handler;
        _handler = null;
        try
        {
            try { File.Delete(_report); } catch { }
            Directory.CreateDirectory(_dir);
            Log($"=== Parameter Helper (KH-Tool {Product.Version}) self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            var uiApp = (UIApplication)sender!;
            Log($"Revit {uiApp.Application.VersionBuild}, user '{uiApp.Application.Username}', live updater registered: {LiveFormulas.Registered}");
            RunAll(uiApp.Application);
        }
        catch (Exception ex) { Fail("harness", "FATAL: " + ex); }
        finally
        {
            Log($"RESULT: {_pass} passed, {_fail} failed");
            foreach (string f in Failures) Log("  FAILED " + f);
            Schedule.RevitShutdown.RequestCurrent("Parameter Helper self-test complete");
        }
    }

    // =============================================================================================
    private static void RunAll(Application app)
    {
        // PARAMHELPER_SELFTEST_ONLY=mirror runs Mirror Check's cases alone (a few minutes, not the whole suite).
        if (string.Equals(Environment.GetEnvironmentVariable(OnlyVariable), "mirror", StringComparison.OrdinalIgnoreCase))
        {
            Mirror(app, FindTemplate(app));
            return;
        }
        // PARAMHELPER_SELFTEST_ONLY=nwd runs NWD Helper's Create-rfa-from-NWD cases (NwdFamily/NwdFamilySelfTest.cs).
        if (string.Equals(Environment.GetEnvironmentVariable(OnlyVariable), "nwd", StringComparison.OrdinalIgnoreCase))
        {
            Nwd(app);
            return;
        }
        if (string.Equals(Environment.GetEnvironmentVariable(OnlyVariable), "varyprobe", StringComparison.OrdinalIgnoreCase))
        {
            Scenario("Mirror: vary probe", () => VaryProbe(app, FindTemplate(app)));
            return;
        }
        Scenario("Formula: arithmetic and precedence", Arithmetic);
        Scenario("Formula: units multiply, add only alike, literals need a unit", Units);
        Scenario("Formula: if / and / or / not / text joining", Logic);
        Scenario("Formula: round, roundup, rounddown, steps, sqrt, min, max", Functions);
        Scenario("Formula: errors are refused with a reason", Errors);
        Scenario("Rules: order follows chains; loops, self-reads, double targets refused", Ordering);
        Scenario("Rules: JSON round trip", JsonRoundTrip);

        string template = FindTemplate(app);
        Log("Template: " + template);
        string seed = Path.Combine(_dir, "PH-seed.rvt");
        Document doc = BuildModel(app, template, wallCount: 8);
        SaveAs(doc, seed);

        Scenario("Model: Apply writes area = length × height on every wall, read back", () => ApplyArea(doc));
        Scenario("Model: second Apply changes nothing", () => ApplyAgainUnchanged(doc));
        Scenario("Model: Preview reports and touches nothing", () => PreviewTouchesNothing(doc));
        Scenario("Model: text, yes/no and whole-number targets", () => OtherTargets(doc));
        Scenario("Model: a chained rule listed first still sees the new value", () => Chain(doc));
        Scenario("Model: [type:…] reads the type; text formatted in project units", () => TypeParam(doc));
        Scenario("Model: unit mismatch with the target is an error, nothing written", () => TargetMismatch(doc));
        Scenario("Model: grouped element with a non-varying target is skipped", () => Grouped(doc));
        Scenario("Model: a grouped built-in Revit refuses costs no other element its value", () => GroupedBuiltIn(doc));
        Scenario("Model: selection limits Apply to the chosen elements", () => OnlySelected(doc));
        Scenario("Model: rules saved in the model round-trip, one storage element", () => StoreRoundTrip(doc));
        Scenario("Model: rule check reports example values and errors", () => RuleCheckSample(doc));
        Scenario("Type: a type rule writes every wall type; second run unchanged", () => TypeTarget(doc));
        Scenario("Type: a type rule reading an instance parameter is refused", () => TypeReadsInstance(doc));
        Scenario("Type: an instance rule listed first sees the new type value", () => TypeChain(doc));
        Scenario("Type: a selection writes only the selected walls' types", () => TypeSelection(doc));
        Scenario("Type: editor lists type targets; parameter lists are alphabetical", () => TypeLists(doc));
        Scenario("Type Name: a rule renames types; a taken name is refused, the rest still renamed", () => TypeNameRename(doc));
        Scenario("Type Name: characters Revit forbids are refused before anything is written", () => TypeNameBadChars(doc));
        Scenario("Categories: the lists hold only what every chosen category carries", () => SharedCategories(doc));
        Scenario("Rules: someone else's change since the editor opened is caught; Undo re-reads the rules", () => ChangedSinceOpened(doc));
        Scenario("Live: an edited wall is recalculated in the same transaction", () => LiveInline(doc));
        Scenario("Live: a changed type queues its walls; the flush writes them", () => LiveTypeChange(doc));
        Scenario("Live: an edited type recalculates its type value in the same transaction", () => LiveTypeTarget(doc));
        Scenario("Live: off means no recalculation", () => LiveOff(doc));
        TryClose(doc);

        Scenario("Central: rules saved in local A arrive in local B after Reload Latest", () => Central(app, seed));
        Scenario("Central: two people create rules at once; nobody's are lost; live follows the model", () => CentralTwoEditors(app, seed));

        Mirror(app, template);

        int walls = int.TryParse(Environment.GetEnvironmentVariable(ScaleVariable), out int n) && n > 0 ? n : 2000;
        Scenario($"Cost: Apply and live edit on {walls} walls", () => Cost(app, template, walls));
    }

    // ================================================================== formula language

    private static FormulaValue Eval(string f, Dictionary<string, FormulaValue>? p = null) =>
        Formula.Parse(f).Evaluate(new FormulaContext(r => p != null && p.TryGetValue(r.Name, out FormulaValue v)
            ? v : throw new FormulaException("no " + r)));

    private static FormulaType TypeOf(string f, Dictionary<string, FormulaType> p) =>
        Formula.Parse(f).Check(r => p.TryGetValue(r.Name, out FormulaType t) ? t : throw new FormulaException("no " + r));

    private static void Arithmetic()
    {
        Near(Eval("2 + 3 * 4").Number, 14, "2 + 3 * 4");
        Near(Eval("(2+3)*4").Number, 20, "(2+3)*4");
        Near(Eval("2^3^2").Number, 512, "^ is right-associative");
        Near(Eval("-2^2").Number, -4, "-2^2 = -(2^2)");
        Near(Eval("10 / 4").Number, 2.5, "10 / 4");
        Near(Eval("1.5e3").Number, 1500, "1.5e3");
    }

    private static readonly double Ft = 1 / 0.3048;   // feet in a metre

    private static void Units()
    {
        FormulaValue v = Eval("100 mm + 1 m");
        Check(v.Dim == Dim.Length, "100 mm + 1 m is a length");
        Near(v.Number, 1.1 * Ft, "100 mm + 1 m = 1.1 m");
        var p = new Dictionary<string, FormulaValue>
        {
            ["W"] = FormulaValue.Num(2 * Ft, Dim.Length), ["H"] = FormulaValue.Num(3 * Ft, Dim.Length)
        };
        FormulaValue a = Eval("[W] * [H]", p);
        Check(a.Dim == Dim.Area, "length × length is an area");
        Near(a.Number, 6 * Ft * Ft, "2 m × 3 m = 6 m²");
        Near(Eval("[W] * [H] * 2 m", p).Number, 12 * Ft * Ft * Ft, "× a length again = volume");
        Check(Eval("[W] * [H] / [W]", p).Dim == Dim.Length, "area / length is a length");
        Near(Eval("6 m² / 2 m").Number, 3 * Ft, "6 m² / 2 m");
        Check(Eval("[W] / [H]", p).Dim.IsNumber, "length / length is a number");
        Near(Eval("12 m2").Number, 12 * Ft * Ft, "m2 spelling");

        var t = new Dictionary<string, FormulaType> { ["L"] = new(ValueKind.Number, Dim.Length), ["A"] = new(ValueKind.Number, Dim.Area) };
        Throws(() => TypeOf("[L] + 100", t), "Inconsistent units", "length + plain number");
        Throws(() => TypeOf("[L] + 100", t), "100 mm", "the error suggests writing the unit");
        Throws(() => TypeOf("[L] + [A]", t), "Inconsistent units", "length + area");
        Throws(() => TypeOf("[L] > [A]", t), "Inconsistent units", "length > area");
        Check(TypeOf("[L] + 100 mm", t).Dim == Dim.Length, "length + 100 mm is fine");
        var angle = new Dim(0, "autodesk.spec.aec:angle-2.0.0");
        var t2 = new Dictionary<string, FormulaType> { ["Ang"] = new(ValueKind.Number, angle), ["L"] = new(ValueKind.Number, Dim.Length) };
        Check(TypeOf("[Ang] * 2", t2).Dim == angle, "angle × number stays an angle");
        Throws(() => TypeOf("[Ang] + [L]", t2), "Inconsistent", "angle + length");
        Check(TypeOf("[Ang] / [Ang]", t2).Dim.IsNumber, "angle / angle is a number");
    }

    private static void Logic()
    {
        Check(Eval("if(and(1 < 2, not(no)), \"A\", \"B\")").Text == "A", "if/and/not");
        Check(Eval("if(or(1 > 2, 3 = 4), 1, 2)").Number == 2, "or");
        Check(Eval("\"Fire \" & 60 & \" min\"").Text == "Fire 60 min", "& joins text and numbers");
        Check(Eval("\"say \"\"hi\"\"\"").Text == "say \"hi\"", "\"\" is a quote inside text");
        Check(Eval("\"a\" = \"a\"").Bool, "text equality");
        Check(Eval("0.1 + 0.2 = 0.3").Bool, "= ignores floating-point noise");
        Check(Eval("yes").Bool && !Eval("no").Bool, "yes/no literals");
        var t = new Dictionary<string, FormulaType> { ["T"] = FormulaType.Text };
        Throws(() => TypeOf("if(1, 2, 3)", t), "yes/no test", "if() needs a test");
        Throws(() => TypeOf("if(1 < 2, 2, \"x\")", t), "same kind", "if() branches must agree");
        Throws(() => TypeOf("[T] + \"x\"", t), "Use '&'", "+ on text suggests &");
    }

    private static void Functions()
    {
        Near(Eval("round(2.5)").Number, 3, "round half up");
        Near(Eval("round(-2.5)").Number, -3, "round half away from zero");
        Near(Eval("roundup(2.1)").Number, 3, "roundup");
        Near(Eval("rounddown(2.9)").Number, 2, "rounddown");
        Near(Eval("round(1234 mm, 5 mm)").Number, 1235 / 304.8, "round to a 5 mm step");
        Near(Eval("roundup(1201 mm, 100 mm)").Number, 1300 / 304.8, "roundup to 100 mm");
        Near(Eval("round(2.4999999999999)").Number, 3.0, "rounding noise at .5 rounds as .5 (2.4999999999999 is 2.5)");
        Near(Eval("round(2.49)").Number, 2.0, "a real 2.49 rounds down");
        Check(Eval("sqrt(9 m²)").Dim == Dim.Length, "sqrt of an area is a length");
        Near(Eval("sqrt(9 m²)").Number, 3 * Ft, "sqrt(9 m²) = 3 m");
        Near(Eval("min(3, 1, 2)").Number, 1, "min");
        Near(Eval("max(1 m, 500 mm)").Number, Ft, "max of lengths");
        Near(Eval("abs(-2 m)").Number, 2 * Ft, "abs");
        var t = new Dictionary<string, FormulaType> { ["L"] = new(ValueKind.Number, Dim.Length) };
        Throws(() => TypeOf("round([L])", t), "needs a step", "round(length) asks for a step");
        Throws(() => TypeOf("sqrt([L])", t), "no unit", "sqrt(length)");
        Throws(() => TypeOf("min([L], 2)", t), "Inconsistent", "min of length and number");
    }

    private static void Errors()
    {
        Throws(() => Formula.Parse(""), "empty", "empty formula");
        Throws(() => Formula.Parse("(1 + 2"), "Missing ')'", "unclosed bracket");
        Throws(() => Formula.Parse("[Width"), "Missing ']'", "unclosed parameter");
        Throws(() => Formula.Parse("1 + * 2"), "Unexpected", "two operators");
        Throws(() => Formula.Parse("\"abc"), "closing", "unclosed text");
        Throws(() => Formula.Parse("1 2"), "Unexpected text", "two numbers");
        Throws(() => TypeOf("foo(1)", new()), "Unknown function", "unknown function");
        Throws(() => Eval("1 / 0"), "Division by zero", "division by zero");
        Check(Formula.Parse("[A] + [type:B] + C + [A]").Params().Count == 3, "parameters collected once each");
        Check(Formula.Parse("[type:B]").Params()[0].TypeOnly, "[type:…] marks a type parameter");
    }

    private static FormulaRule R(string target, string formula, params string[] cats) =>
        new() { TargetName = target, Formula = formula, Categories = cats.Length > 0 ? cats.ToList() : new List<string> { "OST_Walls" } };

    private static void Ordering()
    {
        FormulaRule b = R("B", "[A] * 2"), a = R("A", "[X] + 1");
        RuleOrder.Result r = RuleOrder.Sort(new[] { b, a });
        Check(r.Ordered.Count == 2 && r.Ordered[0].Rule == a, "A runs before B, which reads it");
        FormulaRule other = R("C", "[A] * 2", "OST_Doors");
        r = RuleOrder.Sort(new[] { other, a });
        Check(r.Ordered[0].Rule == other, "no dependency across categories: list order kept");

        FormulaRule p = R("P", "[Q] + 1"), q = R("Q", "[P] + 1");
        r = RuleOrder.Sort(new[] { p, q });
        Check(r.Ordered.Count == 0 && r.Refused.Count == 2 && r.Refused[p.Id].Contains("loop"), "a loop is refused");
        FormulaRule l1 = R("L1", "[L3] + 1"), l2 = R("L2", "[L1] + 1"), l3 = R("L3", "[L2] + 1");
        r = RuleOrder.Sort(new[] { l1, l2, l3 });
        Check(r.Ordered.Count == 0 && r.Refused.Count == 3, "a three-rule loop (L1 → L2 → L3 → L1) is refused");
        FormulaRule self = R("S", "[S] + 1");
        Check(RuleOrder.Sort(new[] { self }).Refused[self.Id].Contains("own target"), "a self-read is refused");
        FormulaRule d1 = R("D", "1"), d2 = R("D", "2");
        r = RuleOrder.Sort(new[] { d1, d2 });
        Check(r.Ordered.Count == 1 && r.Refused.ContainsKey(d2.Id), "two rules on one target: the second is refused");
        FormulaRule off = R("E", "[[");
        off.Enabled = false;
        Check(RuleOrder.Sort(new[] { off }).Refused.Count == 0, "a switched-off rule is not even parsed");
        Check(RuleOrder.Sort(new[] { R("F", "1 +") }).Refused.Count == 1, "a broken formula is refused, not thrown");
    }

    private static void JsonRoundTrip()
    {
        var set = new FormulaRuleSet { Live = true };
        set.Rules.Add(R("Panel Area", "[Width] * [Height]", "OST_Walls", "OST_Doors"));
        set.Rules[0].TargetGuid = Guid.NewGuid().ToString();
        set.Rules[0].Note = "ทดสอบ — unicode";
        FormulaRuleSet back = FormulaRuleSet.FromJson(set.ToJson());
        Check(back.Live && back.Rules.Count == 1, "live flag and rule count");
        FormulaRule x = back.Rules[0];
        Check(x.TargetName == "Panel Area" && x.Formula == "[Width] * [Height]" && x.Categories.Count == 2 &&
              x.TargetGuid == set.Rules[0].TargetGuid && x.Note == set.Rules[0].Note && x.Id == set.Rules[0].Id,
            "every field survives, including Thai text");
    }

    // ================================================================== model fixture

    private const string PArea = "PH Area", PLabel = "PH Label", PFlag = "PH Flag", PCount = "PH Count",
        PDouble = "PH Height x2", PFixed = "PH Group Fixed", PTypeArea = "PH Type Area", PTypeLabel = "PH Type Label",
        PShared = "PH Shared";

    private static string FindTemplate(Application app)
    {
        string? t = app.DefaultProjectTemplate;
        if (!string.IsNullOrEmpty(t) && File.Exists(t) && t.Contains("_M_")) return t;
        string metric = @"C:\ProgramData\Autodesk\RVT " + app.VersionNumber + @"\Templates\Default_M_ENU.rte";
        if (File.Exists(metric)) return metric;
        if (!string.IsNullOrEmpty(t) && File.Exists(t)) return t;
        throw new InvalidOperationException("No project template found.");
    }

    private static Document BuildModel(Application app, string template, int wallCount)
    {
        Document doc = app.NewProjectDocument(template);
        string spf = Path.Combine(_dir, $"PH-shared-{Guid.NewGuid():N}.txt");
        File.WriteAllText(spf, "");
        string oldSpf = app.SharedParametersFilename;
        try
        {
            app.SharedParametersFilename = spf;
            DefinitionFile file = app.OpenSharedParameterFile();
            DefinitionGroup group = file.Groups.Create("Parameter Helper test");
            using var t = new Transaction(doc, "fixture");
            t.Start();
            var cats = new CategorySet();
            cats.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Walls));
            foreach ((string name, ForgeTypeId spec, bool varies) in new[]
            {
                (PArea, SpecTypeId.Area, true), (PLabel, SpecTypeId.String.Text, true),
                (PFlag, SpecTypeId.Boolean.YesNo, true), (PCount, SpecTypeId.Int.Integer, true),
                (PDouble, SpecTypeId.Length, true), (PFixed, SpecTypeId.String.Text, false)
            })
            {
                Definition def = group.Definitions.Create(new ExternalDefinitionCreationOptions(name, spec));
                doc.ParameterBindings.Insert(def, app.Create.NewInstanceBinding(cats), GroupTypeId.Data);
                if (varies)
                {
                    var sp = SharedParameterElement.Lookup(doc, ((ExternalDefinition)def).GUID);
                    try { sp.GetDefinition().SetAllowVaryBetweenGroups(doc, true); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { Log($"    ({name}: Revit does not let this kind vary between groups)"); }
                }
            }

            // Two TYPE parameters, for type-target rules.
            foreach ((string name, ForgeTypeId spec) in new[] { (PTypeArea, SpecTypeId.Area), (PTypeLabel, SpecTypeId.String.Text) })
            {
                Definition def = group.Definitions.Create(new ExternalDefinitionCreationOptions(name, spec));
                doc.ParameterBindings.Insert(def, app.Create.NewTypeBinding(cats), GroupTypeId.Data);
            }

            // One parameter bound to BOTH Walls and Floors, for the several-categories cases.
            var wallsAndFloors = new CategorySet();
            wallsAndFloors.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Walls));
            wallsAndFloors.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Floors));
            Definition shared = group.Definitions.Create(new ExternalDefinitionCreationOptions(PShared, SpecTypeId.String.Text));
            doc.ParameterBindings.Insert(shared, app.Create.NewInstanceBinding(wallsAndFloors), GroupTypeId.Data);

            Level level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var basic = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Where(w => w.Kind == WallKind.Basic).Take(2).ToList();
            MakeWalls(doc, level, basic, wallCount, 0);
            // One floor, well away from the walls.
            FloorType floorType = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(f => !f.IsFoundationSlab);
            var loop = CurveLoop.Create(new List<Curve>
            {
                Line.CreateBound(new XYZ(0, -100, 0), new XYZ(20, -100, 0)), Line.CreateBound(new XYZ(20, -100, 0), new XYZ(20, -80, 0)),
                Line.CreateBound(new XYZ(20, -80, 0), new XYZ(0, -80, 0)), Line.CreateBound(new XYZ(0, -80, 0), new XYZ(0, -100, 0))
            });
            Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id);
            t.Commit();
        }
        finally { app.SharedParametersFilename = oldSpf; }
        return doc;
    }

    /// <summary>Walls 2 m … long, 3 m … high, in rows, so each has its own length and height.</summary>
    private static List<ElementId> MakeWalls(Document doc, Level level, IList<WallType> types, int count, double rowY)
    {
        var ids = new List<ElementId>();
        for (int i = 0; i < count; i++)
        {
            double len = (2 + (i % 7) * 0.5) * Ft, h = (3 + (i % 5) * 0.25) * Ft;
            double x = (i % 50) * 8 * Ft, y = rowY + (i / 50) * 3 * Ft;
            Wall w = Wall.Create(doc, Line.CreateBound(new XYZ(x, y, 0), new XYZ(x + len, y, 0)), types[i % types.Count].Id, level.Id, h, 0, false, false);
            ids.Add(w.Id);
        }
        return ids;
    }

    private static void SaveAs(Document doc, string path)
    {
        var o = new SaveAsOptions { OverwriteExistingFile = true };
        doc.SaveAs(path, o);
    }

    private static List<Wall> Walls(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().OrderBy(w => w.Id.Value).ToList();

    private static double Len(Wall w) => w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH).AsDouble();
    private static double Height(Wall w) => w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble();
    private static Parameter P(Element e, string name) => e.GetParameters(name).Single();

    private static FormulaRuleSet Set(params FormulaRule[] rules)
    {
        var s = new FormulaRuleSet();
        s.Rules.AddRange(rules);
        return s;
    }

    private static readonly FormulaRule AreaRule = R(PArea, "[Length] * [Unconnected Height]");

    // ================================================================== model cases

    private static void ApplyArea(Document doc)
    {
        ApplyResult r = FormulaApplier.Apply(doc, Set(AreaRule), null, dryRun: false);
        Log("    " + r.Summary(false));
        List<Wall> walls = Walls(doc);
        Check(r.Changed == walls.Count, $"{walls.Count} walls changed (got {r.Changed})");
        Check(r.Errors == 0 && r.Skipped == 0, "no errors or skips");
        foreach (Wall w in walls)
            Near(P(w, PArea).AsDouble(), Len(w) * Height(w), $"wall {w.Id.Value}: area = length × height (read back)");
    }

    private static void ApplyAgainUnchanged(Document doc)
    {
        ApplyResult r = FormulaApplier.Apply(doc, Set(AreaRule), null, dryRun: false);
        Check(r.Changed == 0 && r.Unchanged == Walls(doc).Count, $"0 changed, all unchanged ({r.Summary(false)})");
    }

    private static void PreviewTouchesNothing(Document doc)
    {
        Wall w = Walls(doc)[0];
        double before = P(w, PArea).AsDouble();
        SetHeight(doc, w, Height(w) + 1 * Ft);
        ApplyResult r = FormulaApplier.Apply(doc, Set(AreaRule), null, dryRun: true);
        Check(r.WouldChange == 1 && r.Changed == 0, $"one would change ({r.Summary(true)})");
        Near(P(w, PArea).AsDouble(), before, "the value is untouched by Preview");
        ApplyRow row = r.Rows.Single(x => x.Status == ApplyStatus.WouldChange);
        Check(row.ElementId == w.Id.Value && row.From.Length > 0 && row.To.Length > 0, $"row names the wall, from '{row.From}' to '{row.To}'");
        FormulaApplier.Apply(doc, Set(AreaRule), null, dryRun: false);
        Near(P(w, PArea).AsDouble(), Len(w) * Height(w), "Apply then fixes it");
    }

    private static void SetHeight(Document doc, Wall w, double h)
    {
        using var t = new Transaction(doc, "test: height");
        t.Start();
        w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(h);
        t.Commit();
    }

    private static void OtherTargets(Document doc)
    {
        var set = Set(
            R(PLabel, "if([Unconnected Height] > 3.4 m, \"TALL\", \"LOW\") & \" \" & round([Length] / 1 mm)"),
            R(PFlag, "[Length] > 3.5 m"),
            R(PCount, "round([Length] / 1 m)"));
        ApplyResult r = FormulaApplier.Apply(doc, set, null, dryRun: false);
        Check(r.Errors == 0, $"no errors ({r.Summary(false)})");
        foreach (Wall w in Walls(doc))
        {
            double lenMm = Len(w) * 304.8, hM = Height(w) * 0.3048;
            string want = (hM > 3.4 + 1e-9 ? "TALL" : "LOW") + " " + Math.Round(Math.Round(lenMm, 9), MidpointRounding.AwayFromZero);
            Check(P(w, PLabel).AsString() == want, $"wall {w.Id.Value}: label '{P(w, PLabel).AsString()}' = '{want}'");
            Check(P(w, PFlag).AsInteger() == (lenMm > 3500 + 1e-6 ? 1 : 0), $"wall {w.Id.Value}: yes/no");
            Check(P(w, PCount).AsInteger() == (int)Math.Round(Math.Round(lenMm / 1000, 9), MidpointRounding.AwayFromZero), $"wall {w.Id.Value}: whole number {P(w, PCount).AsInteger()}");
        }
    }

    private static void Chain(Document doc)
    {
        // Listed BEFORE the rule it reads, and the target values are wrong to start with.
        using (var t = new Transaction(doc, "test: spoil"))
        {
            t.Start();
            foreach (Wall w in Walls(doc)) P(w, PArea).Set(1.0);
            t.Commit();
        }
        var set = Set(R(PDouble, "[" + PArea + "] / [Length] * 2"), AreaRule);
        ApplyResult r = FormulaApplier.Apply(doc, set, null, dryRun: false);
        Check(r.Errors == 0, r.Summary(false));
        foreach (Wall w in Walls(doc))
            Near(P(w, PDouble).AsDouble(), 2 * Height(w), $"wall {w.Id.Value}: 2 × height from the NEW area");
    }

    private static void TypeParam(Document doc)
    {
        Wall w = Walls(doc)[0];
        double width = w.WallType.Width;
        ApplyResult r = FormulaApplier.Apply(doc, Set(R(PLabel, "\"W=\" & [type:Width]")), null, dryRun: false);
        string got = P(w, PLabel).AsString() ?? "";
        string want = "W=" + UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, width, false);
        Check(got == want, $"label '{got}' = '{want}' ({r.Summary(false)})");
        Check(!got.Contains("0.6") || width * 304.8 > 500, "not written in feet");
    }

    private static void TargetMismatch(Document doc)
    {
        Wall w = Walls(doc)[0];
        double before = P(w, PArea).AsDouble();
        ApplyResult r = FormulaApplier.Apply(doc, Set(R(PArea, "[Length]")), null, dryRun: false);
        Check(r.Changed == 0 && r.Errors == Walls(doc).Count, $"every wall an error ({r.Summary(false)})");
        Check(r.Rows.All(x => x.Note.Contains("Inconsistent units")), "the note says Inconsistent units: " + r.Rows.FirstOrDefault()?.Note);
        Near(P(w, PArea).AsDouble(), before, "nothing written");
    }

    private static void Grouped(Document doc)
    {
        Wall w = Walls(doc)[1];
        using (var tg = new TransactionGroup(doc, "test: group"))
        {
            tg.Start();
            using (var t = new Transaction(doc, "group"))
            {
                t.Start();
                doc.Create.NewGroup(new List<ElementId> { w.Id });
                t.Commit();
            }
            ApplyResult r = FormulaApplier.Apply(doc, Set(R(PFixed, "\"x\" & [Length]"), R(PLabel, "\"in group \" & [Length]")), null, dryRun: false);
            ApplyRow? skip = r.Rows.FirstOrDefault(x => x.ElementId == w.Id.Value && x.Rule.StartsWith(PFixed));
            Check(skip?.Status == ApplyStatus.Skipped && skip.Note.Contains("group"), "fixed target in a group skipped: " + skip?.Note);
            Check(string.IsNullOrEmpty(P(doc.GetElement(w.Id), PFixed).AsString()), "and not written");
            Check(P(doc.GetElement(w.Id), PLabel).AsString()?.StartsWith("in group") == true, "a target that varies by group IS written");
            Check(r.Rows.Count(x => x.Rule.StartsWith(PFixed) && x.Status == ApplyStatus.Changed) == Walls(doc).Count - 1,
                "every ungrouped wall written");
            tg.RollBack();
        }
    }

    /// <summary>
    /// Built-in Comments on grouped doors: Revit 2026.5 refused it outside Edit Group (Snowdon Towers,
    /// 2026-10-19) and the one transaction rolled back all 134 doors. Two placed copies of the group, as
    /// there were there; whatever Revit decides for the grouped walls, no row is an error and every
    /// ungrouped wall is written.
    /// </summary>
    private static void GroupedBuiltIn(Document doc)
    {
        List<Wall> walls = Walls(doc);
        Wall w = walls[1];
        using var tg = new TransactionGroup(doc, "test: grouped built-in");
        tg.Start();
        using (var t = new Transaction(doc, "group"))
        {
            t.Start();
            Group g = doc.Create.NewGroup(new List<ElementId> { w.Id });
            ElementTransformUtils.CopyElement(doc, g.Id, new XYZ(0, 200, 0));
            t.Commit();
        }
        List<Wall> grouped = Walls(doc).Where(x => x.GroupId != ElementId.InvalidElementId).ToList();
        ApplyResult r = FormulaApplier.Apply(doc, Set(R("Comments", "\"c \" & [Length]")), null, dryRun: false);
        Check(r.Errors == 0, $"no row is an error ({r.Summary(false)}): " + r.Rows.FirstOrDefault(x => x.Status == ApplyStatus.Error)?.Note);
        int free = Walls(doc).Count - grouped.Count;
        Check(r.Changed >= free && Walls(doc).Where(x => x.GroupId == ElementId.InvalidElementId)
                  .All(x => x.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).AsString()?.StartsWith("c ") == true),
            $"every ungrouped wall written ({free})");
        Log($"    grouped walls: {string.Join(", ", r.Rows.Where(x => grouped.Any(gw => gw.Id.Value == x.ElementId)).Select(x => x.Status + (x.Note.Length > 0 ? " - " + x.Note : "")).Distinct())}");
        tg.RollBack();
    }

    private static void OnlySelected(Document doc)
    {
        List<Wall> walls = Walls(doc);
        using var tg = new TransactionGroup(doc, "test: selection");
        tg.Start();
        using (var t = new Transaction(doc, "spoil"))
        {
            t.Start();
            foreach (Wall w in walls) P(w, PArea).Set(1.0);
            t.Commit();
        }
        ApplyResult r = FormulaApplier.Apply(doc, Set(AreaRule), new List<ElementId> { walls[2].Id, walls[3].Id }, dryRun: false);
        Check(r.Changed == 2 && r.Elements == 2, $"two changed ({r.Summary(false)})");
        Near(P(walls[0], PArea).AsDouble(), 1.0, "an unselected wall untouched");
        Near(P(walls[2], PArea).AsDouble(), Len(walls[2]) * Height(walls[2]), "a selected wall written");
        tg.RollBack();
    }

    private static void StoreRoundTrip(Document doc)
    {
        Check(RuleStore.Load(doc).Rules.Count == 0, "a new model has no rules");
        var set = Set(AreaRule.Clone(), R(PLabel, "\"x\""));
        set.Live = false;
        for (int i = 0; i < 2; i++)
        {
            using var t = new Transaction(doc, "save rules");
            t.Start();
            RuleStore.Save(doc, set);
            t.Commit();
        }
        FormulaRuleSet back = RuleStore.Load(doc);
        Check(back.Rules.Count == 2 && back.Rules[0].Formula == AreaRule.Formula, "rules read back from the model");
        int stores = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.ExtensibleStorage.DataStorage))
            .WherePasses(new Autodesk.Revit.DB.ExtensibleStorage.ExtensibleStorageFilter(RuleStore.SchemaGuid)).GetElementCount();
        Check(stores == 1, $"saving twice keeps ONE storage element ({stores})");
        Check(RuleStore.Find(doc)?.Name == RuleStore.StorageName, "the storage element is named");
        Check(RuleStore.WhyNotEditable(doc) == null, "not workshared: always editable");
    }

    private static void RuleCheckSample(Document doc)
    {
        var samples = RuleCheck.Samples(doc, new[] { "OST_Walls" });
        (bool ok, string msg) = RuleCheck.Check(doc, AreaRule, samples);
        Check(ok && msg.Contains("area") && msg.Contains("Example"), "a good rule: " + msg);
        (ok, msg) = RuleCheck.Check(doc, R(PArea, "[Length] + 1"), samples);
        Check(!ok && msg.Contains("Inconsistent"), "a unit error: " + msg);
        (ok, msg) = RuleCheck.Check(doc, R(PArea, "[Lenght] * 2 m"), samples);
        Check(!ok && msg.Contains("Lenght"), "a misspelt parameter: " + msg);
        var targets = RuleCheck.Targets(doc, samples);
        Check(targets.Any(c => c.Name == PArea) && targets.All(c => c.Name != "Length"), "targets: writable only (PH Area yes, Length no)");
        var sources = RuleCheck.Sources(doc, samples);
        Check(sources.Any(c => c.Name == "Width" && c.OnType && c.Token == "[type:Width]"), "sources include the type's Width as [type:Width]");
    }

    private static FormulaRule TypeRule(string target, string formula)
    {
        FormulaRule r = R(target, formula);
        r.TargetOnType = true;
        return r;
    }

    private static List<WallType> BasicTypes(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic).ToList();

    private static string Mm(Document doc, double feet) => UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, feet, false);

    private static readonly FormulaRule TypeLabelRule = TypeRule(PTypeLabel, "\"W=\" & [Width]");

    private static void TypeTarget(Document doc)
    {
        using var tg = new TransactionGroup(doc, "test: type target");
        tg.Start();
        ApplyResult r = FormulaApplier.Apply(doc, Set(TypeLabelRule.Clone()), null, dryRun: false);
        Log("    " + r.Summary(false));
        List<WallType> basic = BasicTypes(doc);
        foreach (WallType t in basic)
            Check(P(t, PTypeLabel).AsString() == "W=" + Mm(doc, t.Width), $"type '{t.Name}': '{P(t, PTypeLabel).AsString()}' (read back)");
        Check(r.Changed >= basic.Count, $"every basic wall type written ({r.Changed} of at least {basic.Count}), not only the used ones");
        Check(r.Rows.Where(x => x.Status == ApplyStatus.Changed).All(x => doc.GetElement(new ElementId(x.ElementId)) is ElementType),
            "every changed row is a type, not a wall");
        Check(r.Errors == 0, "no errors: a wall type without Width (curtain, stacked) is not a mistake in the rule");
        Check(r.Rows.Where(x => x.Status == ApplyStatus.Skipped).All(x => x.Note.Contains("Width")) && r.Skipped > 0,
            $"those {r.Skipped} types are skipped and the note names Width: {r.Rows.FirstOrDefault(x => x.Status == ApplyStatus.Skipped)?.Note}");
        ApplyResult again = FormulaApplier.Apply(doc, Set(TypeLabelRule.Clone()), null, dryRun: false);
        Check(again.Changed == 0, "second run changes nothing: " + again.Summary(false));
        tg.RollBack();
    }

    private static void TypeReadsInstance(Document doc)
    {
        ApplyResult r = FormulaApplier.Apply(doc, Set(TypeRule(PTypeArea, "[Length] * 3 m")), null, dryRun: false);
        Check(r.Changed == 0 && r.Errors > 0 && r.Skipped == 0, "nothing written, every type an ERROR (no type has it): " + r.Summary(false));
        Check(r.Rows.Any(x => x.Note.Contains("can only read type parameters")), "the note says why: " + r.Rows.FirstOrDefault()?.Note);
        var samples = RuleCheck.Samples(doc, new[] { "OST_Walls" });
        (bool ok, string msg) = RuleCheck.Check(doc, TypeRule(PTypeArea, "[Length] * 3 m"), samples);
        Check(!ok && msg.Contains("type parameter"), "the editor refuses it too: " + msg);
        (ok, msg) = RuleCheck.Check(doc, TypeRule(PTypeArea, "[Width] * 3 m"), samples);
        Check(ok && msg.Contains("one value per type"), "and accepts [Width] * 3 m: " + msg);
    }

    private static void TypeChain(Document doc)
    {
        using var tg = new TransactionGroup(doc, "test: type chain");
        tg.Start();
        using (var t = new Transaction(doc, "spoil"))
        {
            t.Start();
            foreach (WallType wt in BasicTypes(doc)) P(wt, PTypeLabel).Set("old");
            t.Commit();
        }
        var set = Set(R(PLabel, "[type:" + PTypeLabel + "] & \"!\""), TypeLabelRule.Clone());
        ApplyResult r = FormulaApplier.Apply(doc, set, null, dryRun: false);
        Check(r.Errors == 0, r.Summary(false));
        foreach (Wall w in Walls(doc))
            Check(P(w, PLabel).AsString() == "W=" + Mm(doc, w.WallType.Width) + "!", $"wall {w.Id.Value}: '{P(w, PLabel).AsString()}' uses the NEW type value");
        tg.RollBack();
    }

    private static void TypeSelection(Document doc)
    {
        using var tg = new TransactionGroup(doc, "test: type selection");
        tg.Start();
        using (var t = new Transaction(doc, "spoil"))
        {
            t.Start();
            foreach (WallType wt in BasicTypes(doc)) P(wt, PTypeLabel).Set("x");
            t.Commit();
        }
        Wall w0 = Walls(doc)[0];
        ApplyResult r = FormulaApplier.Apply(doc, Set(TypeLabelRule.Clone()), new List<ElementId> { w0.Id }, dryRun: false);
        Check(r.Changed == 1 && r.Rows.Single(x => x.Status == ApplyStatus.Changed).ElementId == w0.WallType.Id.Value,
            "only the selected wall's type: " + r.Summary(false));
        Check(BasicTypes(doc).Count(t => P(t, PTypeLabel).AsString() == "x") == BasicTypes(doc).Count - 1, "every other type untouched");
        tg.RollBack();
    }

    private static void TypeLists(Document doc)
    {
        var samples = RuleCheck.Samples(doc, new[] { "OST_Walls" });
        var targets = RuleCheck.Targets(doc, samples);
        Check(targets.Any(c => c.Name == PTypeArea && c.OnType), "type parameter offered as a target, marked type");
        Check(targets.Any(c => c.Name == PArea && !c.OnType), "instance parameter still offered");
        Check(targets.All(c => c.Name != "Width"), "computed Width (read-only) is not a target");
        var names = RuleCheck.Sources(doc, samples).Select(c => c.Name).ToList();
        Check(names.SequenceEqual(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
            "sources are alphabetical: " + string.Join(", ", names.Take(8)) + " ...");
        var targetNames = targets.Select(c => c.Name).ToList();
        Check(targetNames.SequenceEqual(targetNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)), "targets are alphabetical");
    }

    private static FormulaRule TypeNameRule(string formula)
    {
        FormulaRule r = TypeRule(ParamAccess.TypeNameLabel, formula);
        r.TargetGuid = ParamAccess.TypeNameKey;
        return r;
    }

    private static void TypeNameRename(Document doc)
    {
        var samples = RuleCheck.Samples(doc, new[] { "OST_Walls" });
        Check(RuleCheck.Targets(doc, samples).Any(c => c.Guid == ParamAccess.TypeNameKey && c.OnType && c.Name == "Type Name"),
            "Type Name is offered as a target");
        (bool ok, string msg) = RuleCheck.Check(doc, TypeNameRule("\"PH \" & round([Width] / 1 mm) & \" mm\""), samples);
        Check(ok, "the editor accepts a rename rule: " + msg);
        Check(RuleOrder.Sort(new[] { TypeNameRule("[Type Name] & \" x\"") }).Refused.Count == 1, "a rule reading the name it writes is refused");

        using var tg = new TransactionGroup(doc, "test: rename types");
        tg.Start();
        var rule = TypeNameRule("\"PH \" & round([Width] / 1 mm) & \" mm\"");
        ApplyResult r = FormulaApplier.Apply(doc, Set(rule), null, dryRun: false);
        Log("    " + r.Summary(false));
        foreach (ApplyRow row in r.Rows.Where(x => x.Status != ApplyStatus.Changed)) Log($"      {row.ElementId} {row.Status}: {row.Note}");
        List<WallType> basic = BasicTypes(doc);
        string Want(WallType t) => "PH " + Math.Round(Math.Round(t.Width * 304.8, 9), MidpointRounding.AwayFromZero) + " mm";
        var changed = r.Rows.Where(x => x.Status == ApplyStatus.Changed).Select(x => x.ElementId).ToHashSet();
        Check(changed.Count > 0, "some types were renamed");
        foreach (WallType t in basic.Where(t => changed.Contains(t.Id.Value)))
            Check(t.Name == Want(t), $"type {t.Id.Value} renamed to '{t.Name}' (read back)");
        int distinctWanted = basic.Select(Want).Distinct().Count();
        Check(changed.Count == distinctWanted, $"one type per new name renamed ({changed.Count} of {distinctWanted} names)");
        var refused = r.Rows.Where(x => x.Status == ApplyStatus.Error).ToList();
        Check(refused.Count == basic.Count - distinctWanted && refused.All(x => x.Note.Length > 0),
            $"the {refused.Count} whose name was taken are errors with a reason: {refused.FirstOrDefault()?.Note}");
        Check(basic.Select(t => t.Name).Distinct().Count() == basic.Count, "type names are still unique");
        ApplyResult again = FormulaApplier.Apply(doc, Set(rule), null, dryRun: false);
        Check(again.Changed == 0, "second run renames nothing: " + again.Summary(false));
        // An existing name, not just one given earlier in the run: the first wall's type asks for the
        // second wall's type's name.
        Wall wa = Walls(doc)[0], wb = Walls(doc)[1];
        string taken = wb.WallType.Name, before = wa.WallType.Name;
        ApplyResult clash = FormulaApplier.Apply(doc, Set(TypeNameRule("\"" + taken + "\"")), new List<ElementId> { wa.Id }, dryRun: false);
        Check(clash.Changed == 0 && clash.Errors == 1 && clash.Rows[0].Note.Contains("already called"),
            "a name another type already has is refused: " + clash.Rows.FirstOrDefault()?.Note);
        Check(((Wall)doc.GetElement(wa.Id)).WallType.Name == before, "and the type keeps its name");
        ApplyResult empty = FormulaApplier.Apply(doc, Set(TypeNameRule("\"\" & \"\"")), null, dryRun: false);
        Check(empty.Changed == 0 && empty.Rows.Any(x => x.Note.Contains("empty name")), "an empty name is refused: " + empty.Rows.FirstOrDefault()?.Note);
        tg.RollBack();
    }

    private static void TypeNameBadChars(Document doc)
    {
        Wall w0 = Walls(doc)[0];
        string before = w0.WallType.Name;
        var rule = TypeNameRule("\"A:B \" & round([Width] / 1 mm)");
        ApplyResult preview = FormulaApplier.Apply(doc, Set(rule), new List<ElementId> { w0.Id }, dryRun: true);
        Check(preview.WouldChange == 0 && preview.Errors == 1 && preview.Rows[0].Note.Contains("does not allow"),
            "Preview already reports it: " + preview.Rows.FirstOrDefault()?.Note);
        ApplyResult r = FormulaApplier.Apply(doc, Set(rule), new List<ElementId> { w0.Id }, dryRun: false);
        Check(r.Changed == 0 && r.Errors == 1, "Apply writes nothing: " + r.Summary(false));
        Check(((Wall)doc.GetElement(w0.Id)).WallType.Name == before, "the type keeps its name");
        (bool ok, string msg) = RuleCheck.Check(doc, rule, RuleCheck.Samples(doc, new[] { "OST_Walls" }));
        Check(!ok && msg.Contains("does not allow"), "the editor says so too: " + msg);
    }

    private static void SharedCategories(Document doc)
    {
        var both = new[] { "OST_Walls", "OST_Floors" };
        var samples = RuleCheck.Samples(doc, both);
        Check(samples.Any(e => e is Floor) && samples.Any(e => e is Wall), "samples come from both categories");
        var targets = RuleCheck.Targets(doc, samples);
        Check(targets.Any(c => c.Name == PShared && !c.OnType), "a parameter both carry is offered: " + PShared);
        Check(targets.All(c => c.Name != PLabel), "a Walls-only parameter is not: " + PLabel);
        var sources = RuleCheck.Sources(doc, samples).Select(c => c.Name).ToList();
        Check(!sources.Contains("Unconnected Height") && sources.Contains("Comments"),
            "sources: Comments (both) yes, Unconnected Height (walls only) no");
        Check(RuleCheck.Targets(doc, RuleCheck.Samples(doc, new[] { "OST_Walls" })).Any(c => c.Name == PLabel),
            "with Walls alone, the Walls-only parameter is offered again");
        Check(RuleCheck.EmptyCategories(doc, new[] { "OST_Walls", "OST_Doors" }).SequenceEqual(new[] { "OST_Doors" }),
            "a category with no elements is named as such");

        using var tg = new TransactionGroup(doc, "test: two categories");
        tg.Start();
        var walls = R(PLabel, "\"x\"", "OST_Walls", "OST_Floors");
        ApplyResult r = FormulaApplier.Apply(doc, Set(walls), null, dryRun: false);
        Check(r.NoTarget == 1 && r.Summary(false).Contains("1 without the target"),
            "the floor, which has no " + PLabel + ", is counted and said: " + r.Summary(false));
        ApplyResult s2 = FormulaApplier.Apply(doc, Set(R(PShared, "\"both\"", "OST_Walls", "OST_Floors")), null, dryRun: false);
        Check(s2.Changed == Walls(doc).Count + 1, "a shared target is written on walls AND the floor: " + s2.Summary(false));
        tg.RollBack();
    }

    private static void ChangedSinceOpened(Document doc)
    {
        var s0 = Set(AreaRule.Clone());
        SaveLive(doc, s0);
        string opened = RuleStore.Load(doc).ToJson();          // what "the window" saw
        Check(!RuleStore.ChangedSince(doc, opened), "unchanged rules are not reported");

        using var tg = new TransactionGroup(doc, "test: someone else saved");
        tg.Start();
        var theirs = Set(AreaRule.Clone(), R(PLabel, "\"theirs\""));
        using (var t = new Transaction(doc, "their save")) { t.Start(); RuleStore.Save(doc, theirs); t.Commit(); }
        Check(RuleStore.ChangedSince(doc, opened), "their save is noticed");
        var mine = Set(AreaRule.Clone(), R(PFlag, "yes"));
        var first = Commands.ParameterHelper.TrySave(doc, mine, opened, replace: false);
        Check(!first.Saved && first.ChangedSinceOpened, "Save asks instead of overwriting");
        Check(RuleStore.Load(doc).Rules.Any(x => x.TargetName == PLabel), "their rules are still there");
        var second = Commands.ParameterHelper.TrySave(doc, mine, opened, replace: true);
        Check(second.Saved && RuleStore.Load(doc).Rules.Any(x => x.TargetName == PFlag), "after 'Replace', mine are saved");
        Check(LiveFormulas.CachedRules(doc)?.ToJson() == RuleStore.Load(doc).ToJson(), "live mode runs what was just saved");
        tg.RollBack();
        // A rolled-back group raises no DocumentChanged (found here); the Idling check catches it.
        bool reread = LiveFormulas.CheckCurrent(doc);
        Check(reread && LiveFormulas.CachedRules(doc)?.ToJson() == RuleStore.Load(doc).ToJson(),
            "after an Undo (group rolled back) the Idling check makes live mode run the model's rules again");
        Check(!LiveFormulas.CheckCurrent(doc), "and a second check finds nothing to do");
    }

    private static void LiveTypeTarget(Document doc)
    {
        var set = Set(AreaRule.Clone(), TypeRule(PTypeLabel, "\"W=\" & [Width] & \" \" & [Type Comments]"));
        set.Live = true;
        SaveLive(doc, set);
        WallType type = Walls(doc)[0].WallType;
        var watch = Stopwatch.StartNew();
        using (var t = new Transaction(doc, "test: type comments again"))
        {
            t.Start();
            type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS).Set("LIVE");
            t.Commit();
        }
        Log($"    type edit + live: {watch.ElapsedMilliseconds} ms");
        string got = P(doc.GetElement(type.Id), PTypeLabel).AsString() ?? "";
        Check(got == "W=" + Mm(doc, type.Width) + " LIVE", "the type's own value followed in the same transaction: " + got);
        LiveFormulas.FlushNow(doc);   // its walls were queued for the instance rules
    }

    private static void SaveLive(Document doc, FormulaRuleSet set)
    {
        using (var t = new Transaction(doc, "save rules"))
        {
            t.Start();
            RuleStore.Save(doc, set);
            t.Commit();
        }
        LiveFormulas.Refresh(doc);
    }

    private static void LiveInline(Document doc)
    {
        if (!LiveFormulas.Registered) { Fail(_scenario, "live updater not registered in this session"); return; }
        var set = Set(AreaRule.Clone());
        set.Live = true;
        SaveLive(doc, set);
        Check(LiveFormulas.IsLive(doc), "live is on for this model");
        Wall w = Walls(doc)[4];
        var watch = Stopwatch.StartNew();
        SetHeight(doc, w, 4.2 * Ft);
        Log($"    one edit + live recalculation: {watch.ElapsedMilliseconds} ms");
        Near(P(doc.GetElement(w.Id), PArea).AsDouble(), Len(w) * 4.2 * Ft, "area followed the height in the same transaction");
        Check(LiveFormulas.QueuedCount(doc) == 0, "nothing queued for an ordinary edit");

        // A new wall gets its value as it is placed.
        ElementId added;
        using (var t = new Transaction(doc, "test: new wall"))
        {
            t.Start();
            Wall nw = Wall.Create(doc, Line.CreateBound(new XYZ(0, -20, 0), new XYZ(10, -20, 0)), w.WallType.Id, w.LevelId, 10, 0, false, false);
            added = nw.Id;
            t.Commit();
        }
        Wall n = (Wall)doc.GetElement(added);
        Near(P(n, PArea).AsDouble(), Len(n) * Height(n), "a new wall got its area when placed");
    }

    private static void LiveTypeChange(Document doc)
    {
        var set = Set(AreaRule.Clone(), R(PLabel, "[type:Type Comments] & \"!\""));
        set.Live = true;
        SaveLive(doc, set);
        WallType type = Walls(doc)[0].WallType;
        int instances = Walls(doc).Count(w => w.WallType.Id == type.Id);
        using (var t = new Transaction(doc, "test: type comments"))
        {
            t.Start();
            type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS).Set("CHANGED");
            t.Commit();
        }
        Check(LiveFormulas.QueuedCount(doc) == instances, $"{instances} walls queued, not written inside the type edit ({LiveFormulas.QueuedCount(doc)})");
        ApplyResult? r = LiveFormulas.FlushNow(doc);
        Check(r != null && r.Changed == instances, "the flush wrote them: " + r?.Summary(false));
        Check(Walls(doc).Where(w => w.WallType.Id == type.Id).All(w => P(w, PLabel).AsString() == "CHANGED!"), "every wall of the type reads CHANGED!");
    }

    private static void LiveOff(Document doc)
    {
        var set = Set(AreaRule.Clone());
        set.Live = false;
        SaveLive(doc, set);
        Wall w = Walls(doc)[5];
        double before = P(w, PArea).AsDouble();
        SetHeight(doc, w, Height(w) + 0.5 * Ft);
        Near(P(doc.GetElement(w.Id), PArea).AsDouble(), before, "area did NOT follow");
    }

    // ================================================================== central model

    private static void Central(Application app, string seed)
    {
        string centralDir = Path.Combine(_dir, "central");
        if (Directory.Exists(centralDir)) Directory.Delete(centralDir, true);
        Directory.CreateDirectory(centralDir);
        string central = Path.Combine(centralDir, "PH-central.rvt");
        string localA = Path.Combine(centralDir, "PH-local-A.rvt");
        string localB = Path.Combine(centralDir, "PH-local-B.rvt");

        // Seed → workshared central.
        Document seedDoc = app.OpenDocumentFile(seed);
        seedDoc.EnableWorksharing("Shared Levels and Grids", "Workset1");
        var so = new SaveAsOptions { OverwriteExistingFile = true };
        so.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
        seedDoc.SaveAs(central, so);
        seedDoc.Close(false);
        WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central), ModelPathUtils.ConvertUserVisiblePathToModelPath(localA));
        WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central), ModelPathUtils.ConvertUserVisiblePathToModelPath(localB));
        Log("    central + two locals created");

        // --- A: save rules, apply everything, sync and relinquish.
        Document a = OpenLocal(app, localA);
        Check(a.IsWorkshared && !a.IsDetached, "local A is a workshared local");
        Check(RuleStore.WhyNotEditable(a) == null, "rules are free to edit in A");
        var set = Set(AreaRule.Clone(), R(PLabel, "\"L=\" & round([Length] / 1 mm)"));
        using (var t = new Transaction(a, "save rules"))
        {
            t.Start();
            RuleStore.Save(a, set);
            t.Commit();
        }
        ApplyResult ra = FormulaApplier.Apply(a, set, null, dryRun: false);
        Log("    A apply: " + ra.Summary(false));
        Check(ra.Errors == 0 && ra.Skipped == 0, "A applied without errors");
        Sync(a, "A: rules + values");
        Check(NotOwnedCount(a) == Walls(a).Count, "after sync A owns no wall");
        // A stays open and is NOT reloaded below: it plays the colleague whose local is out of date.
        // (Revit refuses to close the session's active document from the API, so nothing is reopened.)

        // --- B: reload, rules arrive; change ONE wall; Apply borrows only that one.
        Document b = OpenLocal(app, localB);
        b.ReloadLatest(new ReloadLatestOptions());
        FormulaRuleSet inB = RuleStore.Load(b);
        Check(inB.Rules.Count == 2 && inB.Rules[0].Formula == AreaRule.Formula, "B has the rules after Reload Latest");
        ApplyResult rb0 = FormulaApplier.Apply(b, inB, null, dryRun: true);
        Check(rb0.WouldChange == 0, "B: nothing out of date after reload (" + rb0.Summary(true) + ")");

        List<Wall> wallsB = Walls(b);
        Wall w0 = wallsB[0];
        // A setting typed into Properties by someone WITHOUT the add-in: height changes, area does not.
        using (var t = new Transaction(b, "edit without add-in"))
        {
            t.Start();
            w0.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(Height(w0) + 1 * Ft);
            t.Commit();
        }
        Sync(b, "B: one height (no recalculation)");
        Check(NotOwnedCount(b) == wallsB.Count, "after sync B owns nothing");
        ApplyResult rb1 = FormulaApplier.Apply(b, inB, null, dryRun: true);
        Check(rb1.WouldChange == 1 && rb1.Rows.Single(x => x.Status == ApplyStatus.WouldChange).ElementId == w0.Id.Value,
            "Preview finds the one stale wall: " + rb1.Summary(true));
        ApplyResult rb2 = FormulaApplier.Apply(b, inB, null, dryRun: false);
        Check(rb2.Changed == 1, "Apply writes only it: " + rb2.Summary(false));
        int owned = Walls(b).Count(w => WorksharingUtils.GetCheckoutStatus(b, w.Id) == CheckoutStatus.OwnedByCurrentUser);
        Check(owned == 1, $"only that wall is borrowed ({owned} of {wallsB.Count})");
        Near(P(b.GetElement(w0.Id), PArea).AsDouble(), Len(w0) * Height((Wall)b.GetElement(w0.Id)), "and its area is right");
        Sync(b, "B: apply");
        long changedInCentral = wallsB[1].Id.Value;
        // B changes a second wall's height and syncs; A has not seen it yet.
        using (var t = new Transaction(b, "edit 2"))
        {
            t.Start();
            Wall w1 = (Wall)b.GetElement(new ElementId(changedInCentral));
            w1.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(Height(w1) + 0.5 * Ft);
            t.Commit();
        }
        Sync(b, "B: second height");

        // --- A again, NOT reloaded: a new rule wants to write every wall, one of which changed in central.
        Document a2 = a;
        var set2 = Set(AreaRule.Clone(), R(PLabel, "\"v2 \" & round([Length] / 1 mm)"));
        ApplyResult r3 = FormulaApplier.Apply(a2, set2, null, dryRun: false);
        Log("    A (stale) apply: " + r3.Summary(false));
        foreach (ApplyRow row in r3.Rows.Where(x => x.Status != ApplyStatus.Changed)) Log($"      {row.ElementId} {row.Status}: {row.Note}");
        // Two walls changed in central since A last synced: w0 (height + B's applied area) and w1 (height).
        var inCentral = new HashSet<long> { w0.Id.Value, changedInCentral };
        foreach (long id in inCentral)
        {
            ApplyRow? stale = r3.Rows.FirstOrDefault(x => x.ElementId == id && x.Rule.StartsWith(PLabel));
            Check(stale != null && stale.Status == ApplyStatus.Skipped && stale.Note.Contains("central"),
                $"wall {id}, changed in central, is skipped, not forced: {stale?.Status} {stale?.Note}");
        }
        Check(r3.Rows.Count(x => x.Status == ApplyStatus.Changed && x.Rule.StartsWith(PLabel)) == Walls(a2).Count - inCentral.Count,
            "every other wall was written");
        Check(r3.Rows.All(x => !x.Note.StartsWith("Nothing was written")), "one stale wall did not roll back the rest");
        a2.ReloadLatest(new ReloadLatestOptions());
        ApplyResult r4 = FormulaApplier.Apply(a2, set2, null, dryRun: false);
        Check(r4.Rows.Any(x => x.ElementId == changedInCentral && x.Status == ApplyStatus.Changed), "after Reload Latest it is written: " + r4.Summary(false));
        Sync(a2, "A: v2");

        // A type rule in a central model borrows only the types it changes.
        ApplyResult rt = FormulaApplier.Apply(a2, Set(TypeRule(PTypeLabel, "\"C=\" & [Width]")), null, dryRun: false);
        var changedTypes = rt.Rows.Where(x => x.Status == ApplyStatus.Changed).Select(x => x.ElementId).ToHashSet();
        int ownedTypes = new FilteredElementCollector(a2).OfClass(typeof(WallType)).ToElementIds()
            .Count(id => WorksharingUtils.GetCheckoutStatus(a2, id) == CheckoutStatus.OwnedByCurrentUser);
        Check(rt.Changed > 0 && ownedTypes == changedTypes.Count, $"type rule: {changedTypes.Count} types changed, {ownedTypes} borrowed ({rt.Summary(false)})");
        ApplyResult rt2 = FormulaApplier.Apply(a2, Set(TypeRule(PTypeLabel, "\"C=\" & [Width]")), null, dryRun: true);
        Check(rt2.WouldChange == 0, "and nothing left to change: " + rt2.Summary(true));
        Sync(a2, "A: type rule");
        TryClose(b);
        TryClose(a2);
    }

    private static void TryClose(Document doc)
    {
        try { doc.Close(false); }
        catch (Exception ex) { Log($"    (left open: {doc.Title} — {ex.Message})"); }
    }

    /// <summary>
    /// Two people, C and D, both create the FIRST rules in their own locals before either syncs, then
    /// edit them in turn. Covers: two storage elements merged (nobody's rules lost) and folded back
    /// to one; a save refused because the rules changed in central; live mode following rules that
    /// arrive by Reload Latest.
    /// </summary>
    private static void CentralTwoEditors(Application app, string seed)
    {
        string dir = Path.Combine(_dir, "central2");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string central = Path.Combine(dir, "PH-central2.rvt");
        Document seedDoc = app.OpenDocumentFile(seed);
        seedDoc.EnableWorksharing("Shared Levels and Grids", "Workset1");
        var so = new SaveAsOptions { OverwriteExistingFile = true };
        so.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
        seedDoc.SaveAs(central, so);
        TryClose(seedDoc);
        string lc = Path.Combine(dir, "PH-local-C.rvt"), ld = Path.Combine(dir, "PH-local-D.rvt");
        WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central), ModelPathUtils.ConvertUserVisiblePathToModelPath(lc));
        WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central), ModelPathUtils.ConvertUserVisiblePathToModelPath(ld));
        Document c = OpenLocal(app, lc), d = OpenLocal(app, ld);
        string empty = new FormulaRuleSet().ToJson();

        // Both create the first rules at the same time.
        var setC = Set(AreaRule.Clone());
        setC.Live = true;
        var setD = Set(R(PLabel, "\"from D\""));
        Check(Commands.ParameterHelper.TrySave(c, setC, empty, false).Saved, "C saves the first rules");
        Sync(c, "C: first rules");
        Check(Commands.ParameterHelper.TrySave(d, setD, empty, false).Saved, "D, not yet reloaded, saves its own first rules too");
        Sync(d, "D: first rules");   // sync brings C's element in as well
        Check(RuleStore.FindAll(d).Count == 2, $"D now holds two rule elements ({RuleStore.FindAll(d).Count})");
        FormulaRuleSet merged = RuleStore.Load(d);
        Check(merged.Rules.Count == 2 && merged.Rules.Any(x => x.TargetName == PArea) && merged.Rules.Any(x => x.TargetName == PLabel),
            "Load merges them: C's rule AND D's rule, nobody's lost");
        Check(merged.Rules[0].TargetName == PArea && merged.Live, "the older (C's) comes first and its Live setting stands");
        Check(Commands.ParameterHelper.TrySave(d, merged, RuleStore.Load(d).ToJson(), false).Saved, "D saves the merged set");
        Check(RuleStore.FindAll(d).Count == 1, "and it is folded back into ONE element");
        Sync(d, "D: merged");

        // C has not reloaded: its rules are out of date in central. Saving is refused, not forced.
        var stale = Commands.ParameterHelper.TrySave(c, Set(AreaRule.Clone()), RuleStore.Load(c).ToJson(), false);
        Check(!stale.Saved && (stale.Why ?? "").Contains("Reload Latest"), "C's save is refused: " + stale.Why);

        // C reloads: live mode now runs the merged rules.
        c.ReloadLatest(new ReloadLatestOptions());
        Check(LiveFormulas.CachedRules(c)?.ToJson() == RuleStore.Load(c).ToJson() && RuleStore.Load(c).Rules.Count == 2,
            "after Reload Latest C's live mode runs the merged rules (DocumentReloadedLatest)");

        // D changes the formula; C reloads and its next edit follows the NEW formula.
        var doubled = RuleStore.Load(d);
        doubled.Rules.First(x => x.TargetName == PArea).Formula = "[Length] * [Unconnected Height] * 2";
        Check(Commands.ParameterHelper.TrySave(d, doubled, RuleStore.Load(d).ToJson(), false).Saved, "D changes the area formula");
        Sync(d, "D: doubled");
        c.ReloadLatest(new ReloadLatestOptions());
        Check(LiveFormulas.CachedRules(c)?.Rules.First(x => x.TargetName == PArea).Formula.EndsWith("* 2") == true,
            "C's live mode picked up the new formula");
        Wall w = Walls(c)[3];
        using (var t = new Transaction(c, "C edits a wall"))
        {
            t.Start();
            w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(Height(w) + 0.25 * Ft);
            t.Commit();
        }
        Wall wc = (Wall)c.GetElement(w.Id);
        Near(P(wc, PArea).AsDouble(), Len(wc) * Height(wc) * 2, "C's edit is recalculated with D's NEW formula");
        Sync(c, "C: edit");
        TryClose(c);
        TryClose(d);
    }

    private static Document OpenLocal(Application app, string path)
    {
        var o = new OpenOptions();
        var config = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
        o.SetOpenWorksetsConfiguration(config);
        return app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), o);
    }

    private static void Sync(Document doc, string comment)
    {
        var s = new SynchronizeWithCentralOptions { Comment = comment, SaveLocalAfter = true, SaveLocalBefore = false };
        s.SetRelinquishOptions(new RelinquishOptions(true));
        doc.SynchronizeWithCentral(new TransactWithCentralOptions(), s);
    }

    private static int NotOwnedCount(Document doc) =>
        Walls(doc).Count(w => WorksharingUtils.GetCheckoutStatus(doc, w.Id) == CheckoutStatus.NotOwned);

    // ================================================================== cost

    private static void Cost(Application app, string template, int walls)
    {
        var build = Stopwatch.StartNew();
        Document doc = BuildModel(app, template, walls);
        Log($"    built {walls} walls in {build.Elapsed.TotalSeconds:0.0}s");
        var set = Set(AreaRule.Clone(), R(PLabel, "if([Unconnected Height] > 3.4 m, \"TALL\", \"LOW\")"),
            R(PDouble, "[" + PArea + "] / [Length] * 2"));

        ApplyResult first = FormulaApplier.Apply(doc, set, null, dryRun: false);
        Log($"    first Apply (writes everything): {first.Summary(false)}");
        ApplyResult again = FormulaApplier.Apply(doc, set, null, dryRun: true);
        Log($"    Preview (nothing to write): {again.Summary(true)}");
        Check(first.Errors == 0 && again.WouldChange == 0, "a full apply settles in one pass");

        set.Live = true;
        SaveLive(doc, set);
        Wall w = Walls(doc)[walls / 2];
        var times = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var one = Stopwatch.StartNew();
            SetHeight(doc, w, (3.1 + i * 0.1) * Ft);
            times.Add(one.ElapsedMilliseconds);
        }
        Log($"    one wall edited, live on: {string.Join(", ", times)} ms");
        set.Live = false;
        SaveLive(doc, set);
        var off = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var one = Stopwatch.StartNew();
            SetHeight(doc, w, (3.6 + i * 0.1) * Ft);
            off.Add(one.ElapsedMilliseconds);
        }
        Log($"    one wall edited, live off: {string.Join(", ", off)} ms");

        var still = Stopwatch.StartNew();
        using (var t = new Transaction(doc, "test: move all, live off"))
        {
            t.Start();
            ElementTransformUtils.MoveElements(doc, Walls(doc).Select(x => x.Id).ToList(), new XYZ(0, -1, 0));
            t.Commit();
        }
        Log($"    moving all {walls} walls, live off: {still.ElapsedMilliseconds} ms (Revit's own cost)");

        set.Live = true;
        SaveLive(doc, set);
        var many = Stopwatch.StartNew();
        using (var t = new Transaction(doc, "test: move all"))
        {
            t.Start();
            ElementTransformUtils.MoveElements(doc, Walls(doc).Select(x => x.Id).ToList(), new XYZ(0, 1, 0));
            t.Commit();
        }
        Log($"    moving all {walls} walls, live on: {many.ElapsedMilliseconds} ms, queued for later: {LiveFormulas.QueuedCount(doc)}");
        var flush = Stopwatch.StartNew();
        ApplyResult? flushed = LiveFormulas.FlushNow(doc);
        Log($"    the deferred flush afterwards: {flush.ElapsedMilliseconds} ms — {flushed?.Summary(false)}");
        Check(times.Max() < 2000, "a live single edit stays under 2 s");
        TryClose(doc);
    }

    // ================================================================== harness

    private static void Scenario(string name, Action body)
    {
        _scenario = name;
        int before = _fail;
        var watch = Stopwatch.StartNew();
        Log("--- " + name);
        try { body(); }
        catch (Exception ex) { Fail(name, "threw " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
        Log($"    {(before == _fail ? "ok" : "FAILED")} ({watch.Elapsed.TotalSeconds:0.0}s)");
    }

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Log("  PASS " + what); }
        else Fail(_scenario, what);
    }

    private static void Near(double got, double want, string what) =>
        Check(Math.Abs(got - want) <= 1e-6 * Math.Max(1, Math.Abs(want)), $"{what} (got {got:0.######}, want {want:0.######})");

    private static void Throws(Action a, string contains, string what)
    {
        try { a(); Check(false, what + ": no error"); }
        catch (FormulaException ex) { Check(ex.Message.Contains(contains), $"{what}: \"{ex.Message}\""); }
        catch (Exception ex) { Check(false, $"{what}: wrong exception {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Fail(string scenario, string what)
    {
        _fail++;
        Failures.Add($"[{scenario}] {what}");
        Log("  FAIL " + what);
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(_report, line + Environment.NewLine); } catch { }
    }
}
#endif
