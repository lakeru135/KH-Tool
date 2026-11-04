#if !EDITION_DRW
using System.Diagnostics;
using System.IO;
using Application = Autodesk.Revit.ApplicationServices.Application;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// Mirror Check's cases in Parameter Helper's self-test. A small metric model: walls in a row,
/// each with a door from the template. Measures what Revit reports after flips, mirrors (with and
/// without a copy, twice) and a mirrored group, and checks that KH_Mirror State always says what
/// the element is, in the same transaction when live.
/// </summary>
internal static partial class ParamHelperSelfTest
{
    private sealed class MirrorModel
    {
        public Document Doc = null!;
        public List<(ElementId Wall, ElementId Door)> Pairs = new();
    }

    private static void Mirror(Application app, string template)
    {
        Scenario("Mirror: a text parameter may vary between groups in every model category (probe)", () => VaryProbe(app, template));
        Scenario("Mirror: families with and without flip controls — mirror, 45° mirror, rotate, flip (probe)", () => FlipControlProbe(app, template));
        Scenario("Mirror: a same-named parameter Mirror Check did not make is refused, not duplicated", () => MirrorClash(app, template));
        MirrorModel m = BuildMirrorModel(app, template, 6, 0);
        Document doc = m.Doc;
        Log($"    Mirror Check updater registered: {LiveMirror.Registered}");
        Scenario("Mirror: setup binds KH_Mirror State; a partial setup is completed; again adds nothing", () => MirrorSetup(app, m));
        Scenario("Mirror: nothing mirrored writes nothing (blank)", () => MirrorFresh(m));
        Scenario("Mirror: live — flip hand and flip facing are labelled in the same transaction", () => MirrorFlips(m));
        Scenario("Mirror: live — mirror with a copy labels the copy; without, the original; twice clears it", () => MirrorMirrors(m));
        Scenario("Mirror: live — a door mirrored with its group is labelled, the original group's is not", () => MirrorGroup(m));
        Scenario("Mirror: live off writes nothing; Check All Now writes only what differs", () => MirrorLiveOff(m));
        Scenario("Mirror: a shared text parameter of the user's own — chosen, extended, written; a deleted one falls back", () => MirrorChosen(app, m));
        string seed = Path.Combine(_dir, "PH-mirror-seed.rvt");
        SaveAs(doc, seed);
        TryClose(doc);
        Scenario("Mirror: in a central, Check All Now borrows only the doors whose state changes", () => MirrorCentral(app, seed));
        Scenario("Mirror: cost of live on 300 doors", () => MirrorCost(app, template, 300));
    }

    // ------------------------------------------------------------------ model

    private static MirrorModel BuildMirrorModel(Application app, string template, int doors, double rowY)
    {
        var m = new MirrorModel { Doc = app.NewProjectDocument(template) };
        Document doc = m.Doc;
        using var t = new Transaction(doc, "mirror fixture");
        t.Start();
        Level level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
        WallType wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(w => w.Kind == WallKind.Basic);
        FamilySymbol door = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Doors)
            .Cast<FamilySymbol>().FirstOrDefault() ?? throw new InvalidOperationException("The template has no door type.");
        if (!door.IsActive) door.Activate();
        doc.Regenerate();
        for (int i = 0; i < doors; i++)
        {
            double x = (i % 30) * 6 * Ft, y = rowY + (i / 30) * 8 * Ft;
            Wall w = Wall.Create(doc, Line.CreateBound(new XYZ(x, y, 0), new XYZ(x + 4 * Ft, y, 0)), wt.Id, level.Id, 3 * Ft, 0, false, false);
            FamilyInstance d = doc.Create.NewFamilyInstance(new XYZ(x + 2 * Ft, y, 0), door, w, level, StructuralType.NonStructural);
            m.Pairs.Add((w.Id, d.Id));
        }
        t.Commit();
        Log($"    {doors} walls, each with a '{door.FamilyName} : {door.Name}'");
        return m;
    }

    private static FamilyInstance Door(Document doc, ElementId id) => (FamilyInstance)doc.GetElement(id);

    /// <summary>What the target (default KH_Mirror State) holds: "missing", "" (blank) or the label.</summary>
    private static string Label(Element e, Guid? target = null)
    {
        Parameter? p = MirrorParams.On(e, target ?? MirrorParams.Guid);
        return p == null ? "missing" : p.HasValue ? p.AsString() ?? "" : "";
    }

    /// <summary>True when KH_Mirror State says what the element is.</summary>
    private static bool Agrees(FamilyInstance fi, out string text, Guid? target = null)
    {
        string want = MirrorApplier.LabelOf(fi), got = Label(fi, target);
        text = $"Mirrored={fi.Mirrored} Hand={fi.HandFlipped} Facing={fi.FacingFlipped} → '{got}'" + (got == want ? "" : $" (want '{want}')");
        return got == want;
    }

    private static void SaveMirrorLive(Document doc, bool on)
    {
        FormulaRuleSet set = RuleStore.Load(doc);
        set.MirrorCheck = on;
        using (var t = new Transaction(doc, "save mirror setting"))
        {
            t.Start();
            RuleStore.Save(doc, set);
            t.Commit();
        }
        LiveFormulas.Refresh(doc);
    }

    private static void Edit(Document doc, string name, Action body)
    {
        using var t = new Transaction(doc, name);
        // Warnings (overlapping walls, identical instances) are deleted, never shown: a dialog would
        // stall the unattended run.
        FailureHandlingOptions o = t.GetFailureHandlingOptions();
        o.SetFailuresPreprocessor(new SwallowWarnings());
        t.SetFailureHandlingOptions(o);
        t.Start();
        body();
        t.Commit();
    }

    private sealed class SwallowWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (FailureMessageAccessor f in a.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }

    private static Plane MirrorPlane(Document doc, ElementId wall, double metres = 1)
    {
        // Parallel to the wall, to its side: the mirrored door lands twice as far away, on the other side.
        var line = (Line)((LocationCurve)doc.GetElement(wall).Location).Curve;
        return Plane.CreateByNormalAndOrigin(XYZ.BasisY, line.Origin + new XYZ(0, metres * Ft, 0));
    }

    // ------------------------------------------------------------------ cases

    /// <summary>
    /// Binds a throw-away parameter to one model category at a time and asks Revit to let it vary
    /// between groups. Measured on 2026.5: a Yes/No parameter is refused in all 125 categories,
    /// which is why KH_Mirror State is text. This checks text is allowed in every one.
    /// </summary>
    private static void VaryProbe(Application app, string template)
    {
        Document doc = app.NewProjectDocument(template);
        string spf = Path.Combine(_dir, $"PH-vary-probe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(spf, "");
        string oldSpf = app.SharedParametersFilename;
        var refused = new List<string>();
        int allowed = 0, yesNoAllowed = 0;
        try
        {
            app.SharedParametersFilename = spf;
            DefinitionGroup g = app.OpenSharedParameterFile().Groups.Create("probe");
            int n = 0;
            foreach (Category c in MirrorParams.Categories(doc))
            {
                foreach (ForgeTypeId spec in new[] { SpecTypeId.String.Text, SpecTypeId.Boolean.YesNo })
                {
                    Definition def = g.Definitions.Create(new ExternalDefinitionCreationOptions($"Probe {n++}", spec));
                    var cats = new CategorySet();
                    cats.Insert(c);
                    using var t = new Transaction(doc, "probe");
                    t.Start();
                    doc.ParameterBindings.Insert(def, app.Create.NewInstanceBinding(cats), GroupTypeId.Data);
                    var sp = SharedParameterElement.Lookup(doc, ((ExternalDefinition)def).GUID);
                    bool text = spec == SpecTypeId.String.Text;
                    try
                    {
                        sp.GetDefinition().SetAllowVaryBetweenGroups(doc, true);
                        if (text) allowed++; else yesNoAllowed++;
                    }
                    catch (Exception ex) { if (text) refused.Add($"{c.Name}: {ex.Message.Split('\n')[0]}"); }
                    t.RollBack();
                }
            }
        }
        finally { app.SharedParametersFilename = oldSpf; }
        Log($"    text: {allowed} categories allow it, {refused.Count} refuse; Yes/No: {yesNoAllowed} allow it");
        foreach (string r in refused) Log("      " + r);
        Check(refused.Count == 0 && allowed > 0, "a text parameter may vary between groups in every model category");
        TryClose(doc);
    }

    /// <summary>
    /// What Revit reports for families WITHOUT flip controls (furniture, generic models), next to one
    /// with them: mirror, mirror about a 45° plane, rotate, flip. Logs the flags after every step and
    /// checks only that KH_Mirror State agrees — the log is the finding.
    /// </summary>
    private static void FlipControlProbe(Application app, string template)
    {
        Document doc = app.NewProjectDocument(template);
        MirrorParams.Ensure(app, doc);
        var symbols = new List<FamilySymbol>();
        foreach (bool controls in new[] { false, true })
        {
            string? rfa = MakeGenericFamily(app, controls);
            if (rfa != null) symbols.AddRange(LoadSymbols(doc, rfa));
        }
        string people = @"C:\ProgramData\Autodesk\RVT " + app.VersionNumber +
                        @"\Libraries\English\US\Route Analysis\M_People Content in Route Analysis.rfa";
        if (File.Exists(people)) symbols.AddRange(LoadSymbols(doc, people).Take(1));
        Check(symbols.Count > 0, $"{symbols.Count} non-hosted famil(ies) to probe");

        Level level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
        int i = 0;
        foreach (FamilySymbol sym in symbols)
        {
            FamilyInstance? fi = null;
            double x = (i++) * 10 * Ft;
            Edit(doc, "probe: place", () =>
            {
                if (!sym.IsActive) sym.Activate();
                fi = doc.Create.NewFamilyInstance(new XYZ(x, 0, 0), sym, level, StructuralType.NonStructural);
            });
            if (fi == null) { Log($"    {sym.FamilyName}: could not be placed"); continue; }
            ElementId id = fi.Id;
            string name = $"{sym.Category?.Name} '{sym.FamilyName}'";
            void Step(string what)
            {
                FamilyInstance f = Door(doc, id);
                MirrorApplier.Apply(doc, new List<ElementId> { id }, dryRun: false);
                f = Door(doc, id);
                Log($"    {name}, {what}: CanFlipHand={f.CanFlipHand} CanFlipFacing={f.CanFlipFacing} " +
                    $"Mirrored={f.Mirrored} Hand={f.HandFlipped} Facing={f.FacingFlipped} → '{Label(f)}'");
                Check(Agrees(f, out string t), $"{name}, {what}: the label agrees ({t})");
            }
            Step("placed");
            Plane across = Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(x, 2 * Ft, 0));
            Edit(doc, "probe: mirror", () => ElementTransformUtils.MirrorElements(doc, new List<ElementId> { id }, across, false));
            Step("mirrored");
            Check(Door(doc, id).Mirrored, $"{name}: mirrored reads Mirrored, flip controls or not");
            Edit(doc, "probe: mirror back", () => ElementTransformUtils.MirrorElements(doc, new List<ElementId> { id }, across, false));
            Step("mirrored back");
            XYZ c = ((LocationPoint)Door(doc, id).Location).Point;
            Plane diag = Plane.CreateByNormalAndOrigin(new XYZ(1, 1, 0).Normalize(), c);
            Edit(doc, "probe: mirror 45", () => ElementTransformUtils.MirrorElements(doc, new List<ElementId> { id }, diag, false));
            Step("mirrored about a 45° plane");
            Edit(doc, "probe: rotate", () => ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(c, c + XYZ.BasisZ), Math.PI / 3));
            Step("then rotated 60°");
            Edit(doc, "probe: mirror back 45", () => ElementTransformUtils.MirrorElements(doc, new List<ElementId> { id },
                Plane.CreateByNormalAndOrigin(XYZ.BasisX, ((LocationPoint)Door(doc, id).Location).Point), false));
            Step("then mirrored again (twice in all)");
            FamilyInstance now = Door(doc, id);
            if (now.CanFlipHand)
            {
                Edit(doc, "probe: flip hand", () => Door(doc, id).flipHand());
                Step("then hand-flipped");
            }
            if (Door(doc, id).CanFlipFacing)
            {
                Edit(doc, "probe: flip facing", () => Door(doc, id).flipFacing());
                Step("then facing-flipped");
            }
        }
        TryClose(doc);
    }

    private static IEnumerable<FamilySymbol> LoadSymbols(Document doc, string rfa)
    {
        Family? fam = null;
        Edit(doc, "probe: load", () => doc.LoadFamily(rfa, out fam));
        return fam == null ? Enumerable.Empty<FamilySymbol>()
            : fam.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().ToList();
    }

    /// <summary>
    /// A Generic Model family: an L-shaped extrusion (so a mirror image looks different), optionally
    /// with a hand and a facing flip control. Saved in the work folder; null if it could not be made.
    /// </summary>
    private static string? MakeGenericFamily(Application app, bool controls)
    {
        string rft = @"C:\ProgramData\Autodesk\RVT " + app.VersionNumber + @"\Family Templates\English\Metric Generic Model.rft";
        if (!File.Exists(rft)) { Log("    no Metric Generic Model.rft"); return null; }
        string path = Path.Combine(_dir, controls ? "PH-probe-with-flip-controls.rfa" : "PH-probe-no-flip-controls.rfa");
        Document fam = app.NewFamilyDocument(rft);
        try
        {
            using (var t = new Transaction(fam, "probe family"))
            {
                t.Start();
                double a = 1 * Ft, b = 0.4 * Ft;
                var pts = new[] { new XYZ(0, 0, 0), new XYZ(a, 0, 0), new XYZ(a, b, 0), new XYZ(b, b, 0), new XYZ(b, a, 0), new XYZ(0, a, 0) };
                var loop = new CurveArray();
                for (int k = 0; k < pts.Length; k++) loop.Append(Line.CreateBound(pts[k], pts[(k + 1) % pts.Length]));
                var profile = new CurveArrArray();
                profile.Append(loop);
                SketchPlane sp = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                fam.FamilyCreate.NewExtrusion(true, profile, sp, 0.5 * Ft);
                if (controls)
                {
                    Autodesk.Revit.DB.View plan = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate);
                    fam.FamilyCreate.NewControl(ControlShape.DoubleHorizontalArrow, plan, new XYZ(0.5 * a, -0.3 * Ft, 0));
                    fam.FamilyCreate.NewControl(ControlShape.DoubleVerticalArrow, plan, new XYZ(-0.3 * Ft, 0.5 * a, 0));
                }
                t.Commit();
            }
            fam.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            return path;
        }
        catch (Exception ex) { Log($"    probe family ({(controls ? "with" : "without")} controls) not made: {ex.Message}"); return null; }
        finally { try { fam.Close(false); } catch { } }
    }

    private static void MirrorClash(Application app, string template)
    {
        Document doc = app.NewProjectDocument(template);
        string spf = Path.Combine(_dir, $"PH-mirror-clash-{Guid.NewGuid():N}.txt");
        File.WriteAllText(spf, "");
        string oldSpf = app.SharedParametersFilename;
        try
        {
            app.SharedParametersFilename = spf;
            DefinitionGroup g = app.OpenSharedParameterFile().Groups.Create("someone else's");
            Definition def = g.Definitions.Create(new ExternalDefinitionCreationOptions(MirrorParams.Name, SpecTypeId.String.Text));
            var cats = new CategorySet();
            cats.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Doors));
            Edit(doc, "their parameter", () => doc.ParameterBindings.Insert(def, app.Create.NewInstanceBinding(cats), GroupTypeId.Data));
        }
        finally { app.SharedParametersFilename = oldSpf; }

        string? why = MirrorParams.Ensure(app, doc);
        Check(why != null && why.Contains(MirrorParams.Name), "set-up is refused with the reason: " + why);
        Check(SharedParameterElement.Lookup(doc, MirrorParams.Guid) == null, "and Mirror Check's parameter was not made");
        Check(app.SharedParametersFilename == oldSpf, "the user's shared parameter file is put back");
        TryClose(doc);
    }

    private static void MirrorSetup(Application app, MirrorModel m)
    {
        Document doc = m.Doc;
        string oldSpf = app.SharedParametersFilename;
        Check(!MirrorParams.Read(doc).Present, "a new model does not have the parameter");

        // A model set up for doors only (as a model would be if Revit added a category later).
        string? why = MirrorParams.Ensure(app, doc, new List<Category> { Category.GetCategory(doc, BuiltInCategory.OST_Doors) });
        Check(why == null, "doors-only set-up: " + (why ?? "ok"));
        MirrorParams.State partial = MirrorParams.Read(doc);
        Check(partial.Present && partial.MissingCategories > 0 && !partial.Ready,
            $"it reads as partly set up: {partial.MissingCategories} categories missing");

        why = MirrorParams.Ensure(app, doc);
        MirrorParams.State full = MirrorParams.Read(doc);
        int wanted = MirrorParams.Categories(doc).Count;
        Check(why == null && full.Ready && full.BoundCategories == wanted,
            $"set-up again completes it: {full.BoundCategories} of {wanted} model categories ({why ?? "ok"})");
        Check(!full.NotVarying && SharedParameterElement.Lookup(doc, MirrorParams.Guid).GetDefinition().VariesAcrossGroups,
            "it varies between group instances");
        Check(app.SharedParametersFilename == oldSpf, "the user's shared parameter file is put back");

        int sharedBefore = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).GetElementCount();
        Check(MirrorParams.Ensure(app, doc) == null, "a third set-up succeeds");
        int sharedAfter = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).GetElementCount();
        Check(sharedAfter == sharedBefore, $"and adds no parameter ({sharedBefore} → {sharedAfter})");

        FamilyInstance d0 = Door(doc, m.Pairs[0].Door);
        Check(MirrorParams.On(d0, MirrorParams.Guid) is { StorageType: StorageType.String }, "a door carries it, as text");
    }

    private static void MirrorFresh(MirrorModel m)
    {
        Document doc = m.Doc;
        var (preview, counts) = MirrorApplier.Apply(doc, null, dryRun: true);
        Log($"    {counts.Instances} instances, mirrored {counts.Mirrored}, hand {counts.HandFlipped}, facing {counts.FacingFlipped}");
        Check(counts.Instances == m.Pairs.Count && counts.Flagged == 0, "no door is mirrored or flipped yet");
        Check(preview.WouldChange == 0 && counts.OutOfDate == 0, "Preview: nothing to write — " + preview.Summary(true));
        var (applied, _) = MirrorApplier.Apply(doc, null, dryRun: false);
        Check(applied.Changed == 0 && applied.Errors == 0, "Check All Now writes nothing: " + applied.Summary(false));
        Check(Label(Door(doc, m.Pairs[0].Door)) == "", "the state stays blank");
    }

    private static void MirrorFlips(MirrorModel m)
    {
        Document doc = m.Doc;
        if (!LiveMirror.Registered) { Fail(_scenario, "Mirror Check updater not registered in this session"); return; }
        SaveMirrorLive(doc, true);
        Check(LiveMirror.IsLive(doc), "live is on for this model");

        FamilyInstance a = Door(doc, m.Pairs[0].Door);
        Check(a.CanFlipHand && a.CanFlipFacing, "the template door can flip both ways");
        var watch = Stopwatch.StartNew();
        Edit(doc, "test: flip hand", () => a.flipHand());
        Log($"    flip + live: {watch.ElapsedMilliseconds} ms");
        a = Door(doc, a.Id);
        // Measured, 2026.5: Revit counts ONE flip as a mirror image — Mirrored is true after flipHand alone.
        Check(a.Mirrored && a.HandFlipped, "Revit counts a single hand flip as mirrored");
        Check(Agrees(a, out string ta) && Label(a) == "Mirrored, Hand Flipped", "flip hand: " + ta);
        Check(LiveMirror.QueuedCount(doc) == 0, "written in the edit, nothing queued");

        FamilyInstance b = Door(doc, m.Pairs[1].Door);
        Edit(doc, "test: flip facing", () => b.flipFacing());
        b = Door(doc, b.Id);
        Check(Agrees(b, out string tb) && Label(b) == "Mirrored, Facing Flipped", "flip facing: " + tb);

        // Flip hand back: the label is cleared to blank.
        Edit(doc, "test: flip hand back", () => Door(doc, a.Id).flipHand());
        a = Door(doc, a.Id);
        Check(Agrees(a, out string ta2) && Label(a) == "", "flip hand back clears it: " + ta2);
    }

    private static void MirrorMirrors(MirrorModel m)
    {
        Document doc = m.Doc;
        (ElementId wall, ElementId door) = m.Pairs[2];

        // With a copy: the copy is new, and is labelled as it is placed.
        ICollection<ElementId> made = new List<ElementId>();
        Edit(doc, "test: mirror with copy", () =>
            made = ElementTransformUtils.MirrorElements(doc, new List<ElementId> { wall, door }, MirrorPlane(doc, wall), true));
        FamilyInstance? copy = made.Select(doc.GetElement).OfType<FamilyInstance>().FirstOrDefault();
        Check(copy != null, $"the mirror made a door copy ({made.Count} element(s))");
        if (copy != null)
        {
            Check(copy.Mirrored, "Revit says the copy is mirrored");
            Check(Agrees(copy, out string tc) && Label(copy).StartsWith("Mirrored"), "the copy is labelled: " + tc);
        }
        FamilyInstance orig = Door(doc, door);
        Check(!orig.Mirrored && Agrees(orig, out _) && Label(orig) == "", "the original is untouched");

        // Without a copy: the original moves (clear of the copy) and is now mirrored.
        Plane far = MirrorPlane(doc, wall, 3);
        Edit(doc, "test: mirror, no copy", () =>
            ElementTransformUtils.MirrorElements(doc, new List<ElementId> { wall, door }, far, false));
        orig = Door(doc, door);
        Check(orig.Mirrored, "Revit says the moved original is mirrored");
        Check(Agrees(orig, out string tm) && Label(orig).StartsWith("Mirrored"), "the original is labelled: " + tm);

        // Twice, about the same plane: back where it was.
        Edit(doc, "test: mirror back", () =>
            ElementTransformUtils.MirrorElements(doc, new List<ElementId> { wall, door }, far, false));
        orig = Door(doc, door);
        Check(!orig.Mirrored, "mirrored twice, Revit says it is not mirrored");
        Check(Agrees(orig, out string tb) && Label(orig) == "", "the label went back to blank: " + tb);
    }

    private static void MirrorGroup(MirrorModel m)
    {
        Document doc = m.Doc;
        (ElementId wall, ElementId door) = m.Pairs[3];
        Group? g = null;
        Edit(doc, "test: group", () => g = doc.Create.NewGroup(new List<ElementId> { wall, door }));
        Check(g != null && Door(doc, door).GroupId == g!.Id, "the wall and its door are a group");

        // A mirrored COPY of the group: the same group type, one instance mirrored, one not.
        ICollection<ElementId> made = new List<ElementId>();
        Edit(doc, "test: mirror the group, copy", () =>
            made = ElementTransformUtils.MirrorElements(doc, new List<ElementId> { g!.Id }, MirrorPlane(doc, wall), true));
        int queued = LiveMirror.QueuedCount(doc);
        ApplyResult? flushed = LiveMirror.FlushNow(doc);
        Log($"    after mirroring the group: {queued} queued; flush: {flushed?.Summary(false) ?? "none"}");

        Group? copy = made.Select(doc.GetElement).OfType<Group>().FirstOrDefault();
        Check(copy != null && copy.GroupType.Id == ((Group)doc.GetElement(g!.Id)).GroupType.Id, "the copy is another instance of the same group type");
        FamilyInstance? inCopy = copy?.GetMemberIds().Select(doc.GetElement).OfType<FamilyInstance>().FirstOrDefault();
        FamilyInstance inOrig = Door(doc, door);
        if (inCopy != null)
        {
            Log($"    door in the mirrored copy: Mirrored={inCopy.Mirrored}, Hand={inCopy.HandFlipped}, Facing={inCopy.FacingFlipped}");
            Check(inCopy.Mirrored && Agrees(inCopy, out string tc) && Label(inCopy).StartsWith("Mirrored"),
                "the door in the mirrored group copy is labelled: " + Label(inCopy));
        }
        else Check(false, "the mirrored group copy holds a door");
        Check(!inOrig.Mirrored && Label(inOrig) == "", "the door in the original group stays blank");
        var (left, _) = MirrorApplier.Apply(doc, null, dryRun: true);
        Check(left.WouldChange == 0 && left.Skipped == 0, "nothing left out of date in the model: " + left.Summary(true));
    }

    private static void MirrorLiveOff(MirrorModel m)
    {
        Document doc = m.Doc;
        SaveMirrorLive(doc, false);
        Check(!LiveMirror.IsLive(doc), "live is off");
        FamilyInstance d = Door(doc, m.Pairs[4].Door);
        Edit(doc, "test: flip, live off", () => d.flipHand());
        d = Door(doc, d.Id);
        Check(d.HandFlipped && Label(d) == "", "flipped, but the label was not written");

        var (preview, counts) = MirrorApplier.Apply(doc, null, dryRun: true);
        Check(preview.WouldChange == 1 && counts.OutOfDate == 1 && preview.Rows.Single().ElementId == d.Id.Value,
            "Preview finds that one door: " + preview.Summary(true));
        var (applied, _) = MirrorApplier.Apply(doc, null, dryRun: false);
        Check(applied.Changed == 1 && applied.Errors == 0, "Check All Now writes exactly it: " + applied.Summary(false));
        Check(Agrees(Door(doc, d.Id), out string t), "and it agrees: " + t);
        var (again, _) = MirrorApplier.Apply(doc, null, dryRun: true);
        Check(again.WouldChange == 0, "a second Preview finds nothing");

        // Selection: only the selected element is looked at.
        FamilyInstance e = Door(doc, m.Pairs[5].Door);
        Edit(doc, "test: flip another, live off", () => e.flipFacing());
        var (only, oc) = MirrorApplier.Apply(doc, new List<ElementId> { m.Pairs[0].Door }, dryRun: true);
        Check(oc.Instances == 1 && only.WouldChange == 0, "a selection of another door sees nothing to do");
        MirrorApplier.Apply(doc, null, dryRun: false);
    }

    private static Guid? _chosenGuid;

    /// <summary>Adds shared parameters to the model through a throw-away shared parameter file.</summary>
    private static Dictionary<string, Guid> MakeShared(Application app, Document doc,
        params (string Name, ForgeTypeId Spec, bool OnType, BuiltInCategory Cat)[] defs)
    {
        string spf = Path.Combine(_dir, $"PH-office-{Guid.NewGuid():N}.txt");
        File.WriteAllText(spf, "");
        string oldSpf = app.SharedParametersFilename;
        var made = new Dictionary<string, Guid>();
        try
        {
            app.SharedParametersFilename = spf;
            DefinitionGroup g = app.OpenSharedParameterFile().Groups.Create("office");
            Edit(doc, "office parameters", () =>
            {
                foreach (var d in defs)
                {
                    var def = (ExternalDefinition)g.Definitions.Create(new ExternalDefinitionCreationOptions(d.Name, d.Spec));
                    var cats = new CategorySet();
                    cats.Insert(Category.GetCategory(doc, d.Cat));
                    Autodesk.Revit.DB.Binding b = d.OnType ? app.Create.NewTypeBinding(cats) : app.Create.NewInstanceBinding(cats);
                    doc.ParameterBindings.Insert(def, b, GroupTypeId.IdentityData);
                    made[d.Name] = def.GUID;
                }
            });
        }
        finally { app.SharedParametersFilename = oldSpf; }
        return made;
    }

    private static void SaveMirrorTarget(Document doc, Guid? target, string name, bool live)
    {
        FormulaRuleSet set = RuleStore.Load(doc);
        set.MirrorCheck = live;
        set.MirrorTargetGuid = target?.ToString() ?? "";
        set.MirrorTargetName = target == null ? "" : name;
        using (var t = new Transaction(doc, "save mirror target"))
        {
            t.Start();
            RuleStore.Save(doc, set);
            t.Commit();
        }
        LiveFormulas.Refresh(doc);
    }

    private static void MirrorChosen(Application app, MirrorModel m)
    {
        Document doc = m.Doc;
        var made = MakeShared(app, doc,
            ("Office Hand", SpecTypeId.String.Text, false, BuiltInCategory.OST_Doors),
            ("Office Flag", SpecTypeId.Boolean.YesNo, false, BuiltInCategory.OST_Doors),
            ("Office Type Note", SpecTypeId.String.Text, true, BuiltInCategory.OST_Doors));
        Guid office = made["Office Hand"];
        _chosenGuid = office;

        // Which parameters may be chosen.
        List<MirrorTarget> cands = MirrorParams.Candidates(doc);
        Log("    candidates: " + string.Join("; ", cands.Select(c => c.ToString())));
        MirrorTarget? oh = cands.FirstOrDefault(c => c.Guid == office.ToString());
        Check(oh != null && oh.Categories == 1 && !oh.Varies, "a shared text instance parameter is offered, with its 1 category, not varying");
        Check(cands.All(c => c.Name != "Office Flag" && c.Name != "Office Type Note" && c.Guid != MirrorParams.Guid.ToString()),
            "Yes/No, type-bound and Mirror Check's own are not offered");

        // Something typed by hand into it on a door that is neither mirrored nor flipped.
        FamilyInstance plain = Door(doc, m.Pairs[2].Door);
        Check(!MirrorApplier.StateOf(plain).Any, "the door used for typed text is neither mirrored nor flipped");
        Edit(doc, "typed by hand", () => MirrorParams.On(plain, office)!.Set("typed by hand"));
        var (pv, _) = MirrorApplier.Apply(doc, null, dryRun: true, office);
        Check(pv.Rows.Any(r => r.ElementId == plain.Id.Value && r.From == "typed by hand" && r.To == "(blank)" && r.Rule == "Office Hand"),
            "Preview shows the typed text would be cleared: " + pv.Summary(true));

        // Chosen, live, NOT extended: a door in a mirrored group copy cannot take a value of its own.
        SaveMirrorTarget(doc, office, "Office Hand", live: true);
        Check(LiveMirror.LiveTarget(doc) == office, "live writes the chosen parameter");
        // Door 0 was flipped and flipped back, so it is clean: its copy in a mirrored group is mirrored.
        // (Door 1 is facing-flipped: mirroring it would cancel out.)
        (ElementId wall, ElementId door) = m.Pairs[0];
        Check(!MirrorApplier.StateOf(Door(doc, door)).Any, "the door to group is neither mirrored nor flipped");
        Group? g = null;
        Edit(doc, "test: group 2", () => g = doc.Create.NewGroup(new List<ElementId> { wall, door }));
        ICollection<ElementId> made2 = new List<ElementId>();
        Edit(doc, "test: mirror group 2, copy", () =>
            made2 = ElementTransformUtils.MirrorElements(doc, new List<ElementId> { g!.Id }, MirrorPlane(doc, wall), true));
        ApplyResult? fl = LiveMirror.FlushNow(doc);
        Group copy = made2.Select(doc.GetElement).OfType<Group>().First();
        FamilyInstance inCopy = copy.GetMemberIds().Select(doc.GetElement).OfType<FamilyInstance>().First();
        Check(inCopy.Mirrored && fl != null && fl.Rows.Any(r => r.ElementId == inCopy.Id.Value && r.Status == ApplyStatus.Skipped && r.Note.Contains("vary")),
            "not extended: the mirrored group's door is skipped with the reason: " + fl?.Summary(false));

        // Extend: every model category, varying; its group and its values kept; a second time does nothing.
        ForgeTypeId groupBefore = SharedParameterElement.Lookup(doc, office).GetDefinition().GetGroupTypeId();
        string? why = MirrorParams.Extend(doc, office);
        MirrorParams.State st = MirrorParams.Read(doc, office);
        Check(why == null && st.MissingCategories == 0 && !st.NotVarying,
            $"Extend: on all model categories ({st.BoundCategories}) and varying ({why ?? "ok"})");
        Check(SharedParameterElement.Lookup(doc, office).GetDefinition().GetGroupTypeId() == groupBefore, "its parameter group is unchanged");
        Check(Label(Door(doc, plain.Id), office) == "typed by hand", "values already in it survive Extend");
        Check(Label(Door(doc, m.Pairs[0].Door)) != "missing", "KH_Mirror State is still there");
        int shared = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).GetElementCount();
        Check(MirrorParams.Extend(doc, office) == null &&
              new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).GetElementCount() == shared, "a second Extend changes nothing");

        // Check All Now: the group copy's door is labelled, the typed text is cleared.
        var (all, _) = MirrorApplier.Apply(doc, null, dryRun: false, office);
        Check(all.Errors == 0 && all.Skipped == 0, "Check All Now on the chosen parameter: " + all.Summary(false));
        Check(Agrees(Door(doc, inCopy.Id), out string tg, office) && Label(Door(doc, inCopy.Id), office).StartsWith("Mirrored"),
            "the mirrored group's door is labelled now: " + tg);
        Check(Label(Door(doc, plain.Id), office) == "", "the typed text was cleared");

        // Live on the chosen parameter: KH_Mirror State is left alone.
        string ownBefore = Label(Door(doc, plain.Id));
        Edit(doc, "test: flip facing, chosen", () => Door(doc, plain.Id).flipFacing());
        FamilyInstance pf = Door(doc, plain.Id);
        Check(Agrees(pf, out string tl, office) && Label(pf, office) == "Mirrored, Facing Flipped", "live writes the chosen parameter: " + tl);
        Check(Label(pf) == ownBefore, "and leaves KH_Mirror State as it was");
        Edit(doc, "test: flip facing back", () => Door(doc, plain.Id).flipFacing());

        // A chosen parameter deleted from the model: Mirror Check falls back to its own.
        Guid temp = MakeShared(app, doc, ("Office Temp", SpecTypeId.String.Text, false, BuiltInCategory.OST_Doors))["Office Temp"];
        SaveMirrorTarget(doc, temp, "Office Temp", live: true);
        Check(LiveMirror.LiveTarget(doc) == temp, "a second chosen parameter is live");
        Edit(doc, "delete Office Temp", () => doc.Delete(SharedParameterElement.Lookup(doc, temp).Id));
        LiveFormulas.Refresh(doc);
        Check(MirrorParams.Target(doc, RuleStore.Load(doc)) == MirrorParams.Guid && LiveMirror.LiveTarget(doc) == MirrorParams.Guid,
            "deleted: the target falls back to KH_Mirror State, and live writes it");

        // Leave the model choosing Office Hand, live off, for the central case.
        SaveMirrorTarget(doc, office, "Office Hand", live: false);
    }

    private static void MirrorCentral(Application app, string seed)
    {
        string dir = Path.Combine(_dir, "mirror-central");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string central = Path.Combine(dir, "PH-mirror-central.rvt"), local = Path.Combine(dir, "PH-mirror-local.rvt");
        Document seedDoc = app.OpenDocumentFile(seed);
        seedDoc.EnableWorksharing("Shared Levels and Grids", "Workset1");
        var so = new SaveAsOptions { OverwriteExistingFile = true };
        so.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
        seedDoc.SaveAs(central, so);
        TryClose(seedDoc);
        WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central), ModelPathUtils.ConvertUserVisiblePathToModelPath(local));
        Document doc = OpenLocal(app, local);
        Check(!RuleStore.Load(doc).MirrorCheck, "the setting travelled with the model (off)");
        Check(_chosenGuid != null && RuleStore.Load(doc).MirrorTargetGuid == _chosenGuid.ToString() &&
              MirrorParams.Target(doc, RuleStore.Load(doc)) == _chosenGuid, "and so did the chosen parameter");

        // Someone without the add-in flips a door and syncs.
        var doors = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).OfCategory(BuiltInCategory.OST_Doors)
            .Cast<FamilyInstance>().OrderBy(f => f.Id.Value).ToList();
        FamilyInstance d = doors.First(f => !f.HandFlipped && f.GroupId == ElementId.InvalidElementId);
        Edit(doc, "edit without add-in", () => d.flipHand());
        Sync(doc, "flip, no label");
        int ownedBefore = doors.Count(f => WorksharingUtils.GetCheckoutStatus(doc, f.Id) == CheckoutStatus.OwnedByCurrentUser);
        Check(ownedBefore == 0, "after sync nothing is borrowed");

        // As the command does: write the parameter the model's setting names (Office Hand here).
        var (applied, _) = MirrorApplier.Apply(doc, null, dryRun: false, MirrorParams.Target(doc, RuleStore.Load(doc)));
        Check(applied.Changed == 1 && applied.Rows.Single().ElementId == d.Id.Value, "Check All Now writes that one door: " + applied.Summary(false));
        int owned = doors.Count(f => WorksharingUtils.GetCheckoutStatus(doc, f.Id) == CheckoutStatus.OwnedByCurrentUser);
        Check(owned == 1, $"only that door is borrowed ({owned} of {doors.Count})");
        Sync(doc, "labels");
        TryClose(doc);
    }

    private static void MirrorCost(Application app, string template, int count)
    {
        MirrorModel m = BuildMirrorModel(app, template, count, 0);
        Document doc = m.Doc;
        MirrorParams.Ensure(app, doc);
        var all = m.Pairs.SelectMany(p => new[] { p.Wall, p.Door }).ToList();

        var preview = Stopwatch.StartNew();
        var (r, _) = MirrorApplier.Apply(doc, null, dryRun: true);
        Log($"    Preview on {count} doors: {preview.ElapsedMilliseconds} ms — {r.Summary(true)}");

        SaveMirrorLive(doc, false);
        var off = Stopwatch.StartNew();
        Edit(doc, "test: mirror all, live off", () => ElementTransformUtils.MirrorElements(doc, all, MirrorPlane(doc, m.Pairs[0].Wall), false));
        Log($"    mirroring {count} walls + doors, live off: {off.ElapsedMilliseconds} ms (Revit's own cost)");
        var catchUp = Stopwatch.StartNew();
        var (c, _) = MirrorApplier.Apply(doc, null, dryRun: false);
        Log($"    Check All Now afterwards: {catchUp.ElapsedMilliseconds} ms — {c.Summary(false)}");

        SaveMirrorLive(doc, true);
        var on = Stopwatch.StartNew();
        Edit(doc, "test: mirror all, live on", () => ElementTransformUtils.MirrorElements(doc, all, MirrorPlane(doc, m.Pairs[0].Wall), false));
        long onMs = on.ElapsedMilliseconds;
        int queued = LiveMirror.QueuedCount(doc);
        ApplyResult? flushed = LiveMirror.FlushNow(doc);
        Log($"    mirroring {count} walls + doors, live on: {onMs} ms, queued for later: {queued}; flush: {flushed?.Summary(false) ?? "none"}");
        var (left, _) = MirrorApplier.Apply(doc, null, dryRun: true);
        Check(left.WouldChange == 0, "every door agrees after the live edit: " + left.Summary(true));
        TryClose(doc);
    }
}
#endif
