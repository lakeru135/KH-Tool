using System.Diagnostics;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Common;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// Mirror Check's "Check All Now": works out every family instance's mirrored / flipped state and
/// writes <c>KH_Mirror State</c> ONLY where it differs — the same order of work as
/// <see cref="FormulaApplier"/>: compute read-only, borrow exactly the elements that change, write in
/// one transaction, read every value back after the commit.
///
/// <para>Blank means neither, so a model where nothing is mirrored or flipped writes nothing and
/// borrows nothing.</para>
/// </summary>
internal static class MirrorApplier
{
    /// <summary>Set while Check All Now is writing, so the live updater leaves our own writes alone.</summary>
    public static bool Writing { get; private set; }

    private sealed class Planned
    {
        public Element Element = null!;
        public string Value = "";
        public ApplyRow Row = null!;
    }

    /// <summary>What the element is: (mirrored, hand flipped, facing flipped, any of them).</summary>
    public static (bool Mirrored, bool Hand, bool Facing, bool Any) StateOf(FamilyInstance fi)
    {
        bool m = fi.Mirrored, h = fi.HandFlipped, f = fi.FacingFlipped;
        return (m, h, f, m || h || f);
    }

    /// <summary>The value KH_Mirror State should hold for this element.</summary>
    public static string LabelOf(FamilyInstance fi)
    {
        var s = StateOf(fi);
        return MirrorParams.Label(s.Mirrored, s.Hand, s.Facing);
    }

    private static string Current(Parameter p) => p.HasValue ? p.AsString() ?? "" : "";

    public static IEnumerable<FamilyInstance> Instances(Document doc, ICollection<ElementId>? only)
    {
        IEnumerable<FamilyInstance> all = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>();
        if (only == null) return all;
        var scope = new HashSet<ElementId>(only);
        return all.Where(f => scope.Contains(f.Id));
    }

    /// <param name="only">Restrict to these elements (a selection, or what the live updater queued); null = every family instance.</param>
    /// <param name="dryRun">Count and report what would change; touch nothing, borrow nothing.</param>
    /// <param name="target">The shared text parameter to write; null = Mirror Check's own KH_Mirror State.</param>
    public static (ApplyResult Result, MirrorCounts Counts) Apply(Document doc, ICollection<ElementId>? only, bool dryRun, Guid? target = null)
    {
        var watch = Stopwatch.StartNew();
        var result = new ApplyResult();
        var counts = new MirrorCounts();
        Guid g = target ?? MirrorParams.Guid;
        List<Planned> plan = Compute(doc, only, result, counts, dryRun, g);
        if (!dryRun && plan.Count > 0)
        {
            plan = Borrowing.Keep(doc, plan, p => p.Element.Id, Skip);
            if (plan.Count > 0) Write(doc, plan, g);
        }
        result.Elapsed = watch.Elapsed;
        return (result, counts);
    }

    private static List<Planned> Compute(Document doc, ICollection<ElementId>? only, ApplyResult result, MirrorCounts counts, bool dryRun, Guid target)
    {
        var plan = new List<Planned>();
        string name = MirrorParams.NameOf(doc, target) ?? MirrorParams.Name;
        foreach (FamilyInstance fi in Instances(doc, only))
        {
            counts.Instances++;
            var s = StateOf(fi);
            if (s.Mirrored) counts.Mirrored++;
            if (s.Hand) counts.HandFlipped++;
            if (s.Facing) counts.FacingFlipped++;
            if (s.Any) counts.Flagged++;

            Parameter? p = MirrorParams.On(fi, target);
            if (p == null) { counts.NoParameters++; result.NoTarget++; continue; }
            string want = MirrorParams.Label(s.Mirrored, s.Hand, s.Facing);
            string now = Current(p);
            if (now == want) { result.Unchanged++; continue; }
            counts.OutOfDate++;
            var row = new ApplyRow
            {
                Rule = name,
                ElementId = fi.Id.Value,
                Element = FormulaApplier.Describe(doc, fi),
                Category = fi.Category?.Name ?? "",
                From = now.Length > 0 ? now : "(blank)",
                To = want.Length > 0 ? want : "(blank)",
                Status = dryRun ? ApplyStatus.WouldChange : ApplyStatus.Changed
            };
            result.Rows.Add(row);
            if (InGroupWithoutVariance(fi, p))
            {
                row.Status = ApplyStatus.Skipped;
                row.Note = $"In a group: [{name}] is not set to vary between group instances" +
                           (target == MirrorParams.Guid ? " — set up the parameter again" : " — let Mirror Check extend it");
                continue;
            }
            plan.Add(new Planned { Element = fi, Value = want, Row = row });
        }
        result.Elements = counts.Instances;
        return plan;
    }

    private static bool InGroupWithoutVariance(Element e, Parameter p)
    {
        if (e.GroupId == ElementId.InvalidElementId) return false;
        try { return p.Definition is InternalDefinition def && !def.VariesAcrossGroups; } catch { return true; }
    }

    private static void Skip(Planned p, string why)
    {
        p.Row.Status = ApplyStatus.Skipped;
        p.Row.Note = why;
    }

    private static void Fail(Planned p, string why)
    {
        p.Row.Status = ApplyStatus.Error;
        p.Row.Note = why;
    }

    private static void Write(Document doc, List<Planned> plan, Guid target)
    {
        Writing = true;
        try
        {
            using Transaction t = DrwStrictFailures.Start(doc, "Mirror Check: update mirror state", out DrwStrictFailures failures);
            foreach (Planned p in plan)
            {
                try
                {
                    Parameter? param = MirrorParams.On(p.Element, target);
                    if (param == null || !param.Set(p.Value)) Fail(p, "Revit refused the value");
                }
                catch (Exception ex) { Fail(p, ex.Message); }
            }
            TransactionStatus st = t.Commit();
            if (st != TransactionStatus.Committed)
            {
                string why = failures.Errors.Count > 0 ? string.Join("; ", failures.Errors.Distinct()) : st.ToString();
                foreach (Planned p in plan.Where(p => p.Row.Status == ApplyStatus.Changed))
                    Fail(p, "Nothing was written — Revit rolled the change back: " + why);
                return;
            }
        }
        finally { Writing = false; }

        // A commit is not proof of the edit: read every value back from a fresh element.
        foreach (Planned p in plan.Where(p => p.Row.Status == ApplyStatus.Changed))
        {
            Element? again = doc.GetElement(p.Element.Id);
            Parameter? now = again == null ? null : MirrorParams.On(again, target);
            if (now == null || Current(now) != p.Value)
                Fail(p, "Written but not kept — the value read back is " + (now == null ? "missing" : "'" + Current(now) + "'"));
        }
    }

    /// <summary>
    /// Brings one element's KH_Mirror State up to date inside someone else's transaction (the live
    /// updater). Writes nothing it does not have to, and never throws. Returns 1 if it changed, 0 if
    /// not, -1 when the element must wait for the flush (a group member whose value cannot vary).
    /// </summary>
    public static int WriteInline(FamilyInstance fi, Guid target)
    {
        try
        {
            Parameter? p = MirrorParams.On(fi, target);
            if (p == null) return 0;
            string want = LabelOf(fi);
            if (Current(p) == want) return 0;
            if (InGroupWithoutVariance(fi, p)) return -1;
            return p.Set(want) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("Mirror Check live: " + ex.Message);
            return 0;
        }
    }
}
