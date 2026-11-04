using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.ParamFormula;
using KhBatchRunner.UI;

// WinForms (UseWindowsForms) and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>Shared guards and the result window for the Parameter Helper panel.</summary>
internal static class ParameterHelper
{
    public const string Title = "Parameter Helper";

    public static Document? ActiveProject(ExternalCommandData data)
    {
        Document? doc = data.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            TaskDialog.Show(Title, "No model is open.\n\nOpen the project model whose parameters you want to calculate.");
            return null;
        }
        if (doc.IsFamilyDocument)
        {
            TaskDialog.Show(Title, "Parameter Helper runs on project models only — the active document is a family.");
            return null;
        }
        return doc;
    }

    public static FormulaRuleSet? Load(Document doc)
    {
        try { return RuleStore.Load(doc); }
        catch (Exception ex)
        {
            TaskDialog.Show(Title, ex.Message);
            return null;
        }
    }

    public static void ShowResults(UIDocument uiDoc, string title, ApplyResult result, bool dryRun)
    {
        string note = result.Summary(dryRun) +
                      (dryRun ? ". Nothing was changed." : result.Changed > 0 ? ". One Undo reverses the whole apply." : ".") +
                      (result.Unchanged > 0 ? " Elements already right are not listed." : "");
        using var window = new ActionResultsWindow(title, uiDoc.Document.Title, ApplyResult.Columns, result.Table(), note,
            !dryRun && result.Changed > 0, row => Select(uiDoc, row));
        window.ShowDialog();
    }

    /// <summary>Selects and zooms to the element a result row names; returns why it could not, or null.</summary>
    private static string? Select(UIDocument uiDoc, IReadOnlyDictionary<string, string> row)
    {
        if (!row.TryGetValue("Id", out string? text) || !long.TryParse(text, out long value) || value <= 0)
            return "This row is about a rule, not an element.";
        var id = new ElementId(value);
        if (uiDoc.Document.GetElement(id) == null) return "That element is no longer in the model.";
        try
        {
            uiDoc.Selection.SetElementIds(new List<ElementId> { id });
            uiDoc.ShowElements(id);
            return null;
        }
        catch (Exception ex) { return "Revit could not show it: " + ex.Message; }
    }

    /// <summary>
    /// Saves the rules into the model. In order: (1) someone else's change since the editor opened is
    /// reported, not overwritten, unless <paramref name="replace"/>; (2) central is asked that the rules
    /// are free (not borrowed by someone else, not changed in central since the last Reload Latest);
    /// (3) one transaction writes them, and live mode re-reads them. No dialogs: the window decides.
    /// </summary>
    public static (bool Saved, bool ChangedSinceOpened, string? Why) TrySave(Document doc, FormulaRuleSet set, string openedJson, bool replace)
    {
        try
        {
            if (!replace && RuleStore.ChangedSince(doc, openedJson)) return (false, true, null);
            string? why = RuleStore.WhyNotEditable(doc);
            if (why != null) return (false, false, why);
            using (var t = new Transaction(doc, "Parameter Helper: save rules"))
            {
                t.Start();
                RuleStore.Save(doc, set);
                if (t.Commit() != TransactionStatus.Committed) return (false, false, "Revit did not accept the change.");
            }
            LiveFormulas.Refresh(doc);
            return (true, false, null);
        }
        catch (Exception ex) { return (false, false, ex.Message); }
    }
}

/// <summary>The model-backed host the rules windows talk to.</summary>
internal sealed class RevitRulesHost : IParameterRulesHost
{
    private readonly UIDocument _uiDoc;
    private Document Doc => _uiDoc.Document;
    private readonly Dictionary<string, List<Element>> _samples = new(StringComparer.Ordinal);

    public RevitRulesHost(UIDocument uiDoc) { _uiDoc = uiDoc; }

    public string ModelTitle => Doc.Title;
    public bool IsWorkshared => Doc.IsWorkshared;

    public string? RulesBorrowedBy
    {
        get
        {
            if (!Doc.IsWorkshared) return null;
            var ds = RuleStore.Find(Doc);
            if (ds == null) return null;
            try
            {
                return WorksharingUtils.GetCheckoutStatus(Doc, ds.Id, out string owner) == CheckoutStatus.OwnedByOtherUser
                    ? (string.IsNullOrEmpty(owner) ? "another user" : owner) : null;
            }
            catch { return null; }
        }
    }

    public string CategoryLabel(string categoryName) => ParamAccess.CategoryLabel(Doc, categoryName);

    private List<Element> Samples(IList<string> categories)
    {
        string key = string.Join("|", categories.OrderBy(c => c, StringComparer.Ordinal));
        if (!_samples.TryGetValue(key, out List<Element>? list)) _samples[key] = list = RuleCheck.Samples(Doc, categories);
        return list;
    }

    public List<ParamChoice> Targets(IList<string> categories) => RuleCheck.Targets(Doc, Samples(categories));
    public List<ParamChoice> Sources(IList<string> categories) => RuleCheck.Sources(Doc, Samples(categories));
    public (bool Ok, string Message) Check(FormulaRule rule) => RuleCheck.Check(Doc, rule, Samples(rule.Categories));
    public ApplyResult Preview(FormulaRuleSet set) => FormulaApplier.Apply(Doc, set, null, dryRun: true);
    public void ShowResults(string title, ApplyResult result, bool dryRun) => ParameterHelper.ShowResults(_uiDoc, title, result, dryRun);
    public string LiveActivity => LiveFormulas.LastActivity.TryGetValue(Doc.Title, out string? s) ? s : "";
    public List<string> EmptyCategories(IList<string> categories) => RuleCheck.EmptyCategories(Doc, categories);

    public (bool Saved, bool ChangedSinceOpened, string? Why) TrySave(FormulaRuleSet set, string openedJson, bool replace) =>
        ParameterHelper.TrySave(Doc, set, openedJson, replace);
}

/// <summary>
/// Parameter Helper → "Formula Rules": view and edit the rules saved in the active model, preview
/// what they would change, and save (optionally applying them straight away).
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ParameterRulesCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        Document? doc = ParameterHelper.ActiveProject(commandData);
        if (doc == null) return Result.Cancelled;
        FormulaRuleSet? set = ParameterHelper.Load(doc);
        if (set == null) return Result.Cancelled;

        UIDocument uiDoc = commandData.Application.ActiveUIDocument!;
        using var window = new ParameterRulesWindow(new RevitRulesHost(uiDoc), set);
        // The window saves before it closes (so a refused save never loses the edits); OK means saved.
        if (window.ShowDialog() != DialogResult.OK) return Result.Cancelled;

        if (window.ApplyAfterSave)
        {
            ApplyResult r = FormulaApplier.Apply(doc, window.Result, null, dryRun: false);
            ParameterHelper.ShowResults(uiDoc, "Apply formulas", r, dryRun: false);
        }
        return Result.Succeeded;
    }
}

/// <summary>
/// Parameter Helper → "Apply Now": run the active model's rules — on the selected elements when
/// something is selected, otherwise on every element they cover — writing only values that differ.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ParameterApplyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        Document? doc = ParameterHelper.ActiveProject(commandData);
        if (doc == null) return Result.Cancelled;
        FormulaRuleSet? set = ParameterHelper.Load(doc);
        if (set == null) return Result.Cancelled;
        if (!set.Rules.Any(r => r.Enabled))
        {
            TaskDialog.Show(ParameterHelper.Title,
                "This model has no formula rules switched on.\n\nAdd them with Formula Rules first.");
            return Result.Cancelled;
        }

        UIDocument uiDoc = commandData.Application.ActiveUIDocument!;
        ICollection<ElementId> selected = uiDoc.Selection.GetElementIds();
        ICollection<ElementId>? only = selected.Count > 0 ? selected : null;
        ApplyResult r = FormulaApplier.Apply(doc, set, only, dryRun: false);
        ParameterHelper.ShowResults(uiDoc,
            only == null ? "Apply formulas" : $"Apply formulas — {selected.Count} selected element(s)", r, dryRun: false);
        return Result.Succeeded;
    }
}

/// <summary>The model-backed host the Mirror Check window talks to.</summary>
internal sealed class RevitMirrorHost : IMirrorCheckHost
{
    private readonly UIDocument _uiDoc;
    private Document Doc => _uiDoc.Document;

    public RevitMirrorHost(UIDocument uiDoc) { _uiDoc = uiDoc; }

    public string ModelTitle => Doc.Title;

    public List<MirrorTarget> Targets() => MirrorParams.Candidates(Doc);

    private static Guid Parse(string targetGuid) =>
        targetGuid.Length > 0 && Guid.TryParse(targetGuid, out Guid g) ? g : MirrorParams.Guid;

    public MirrorStatus Status(string? targetGuid)
    {
        FormulaRuleSet set = RuleStore.Load(Doc);
        Guid target;
        string? missing = null;
        if (targetGuid == null)
        {
            target = MirrorParams.Target(Doc, set);
            // The model's setting names a parameter that has since been deleted: say so.
            if (set.MirrorTargetGuid.Length > 0 && target == MirrorParams.Guid &&
                !string.Equals(set.MirrorTargetGuid, MirrorParams.Guid.ToString(), StringComparison.OrdinalIgnoreCase))
                missing = set.MirrorTargetName.Length > 0 ? set.MirrorTargetName : "the parameter chosen before";
        }
        else target = Parse(targetGuid);

        bool own = target == MirrorParams.Guid;
        MirrorParams.State p = MirrorParams.Read(Doc, target);
        int all = MirrorParams.Categories(Doc).Count;
        string text;
        if (own)
            text = p.Ready ? $"Set up: {MirrorParams.Name} on {p.BoundCategories} categories."
                : !p.Present ? $"{MirrorParams.Name} is not set up in this model yet."
                : p.NotVarying ? $"{MirrorParams.Name} does not vary between group instances, so grouped elements cannot be labelled — set up again."
                : $"{p.MissingCategories} model categor{(p.MissingCategories == 1 ? "y does" : "ies do")} not carry {MirrorParams.Name} yet — set up again.";
        else
        {
            string name = MirrorParams.NameOf(Doc, target) ?? "?";
            text = $"'{name}' is on {all - p.MissingCategories} of {all} model categories" +
                   (p.NotVarying ? " and does not vary between group instances (grouped elements are skipped)." : ".");
        }
        if (missing != null)
            text = $"'{missing}', chosen for this model, is no longer in it — {MirrorParams.Name} is used instead. " + text;

        return new MirrorStatus
        {
            TargetGuid = own ? "" : target.ToString(),
            MissingTarget = missing,
            TargetMissingCategories = p.MissingCategories,
            TargetVaries = !p.NotVarying,
            ParamsReady = own ? p.Ready : p.Present,
            Problem = own ? p.Clash : null,
            ParamsText = text,
            Live = set.MirrorCheck,
            Counts = MirrorApplier.Apply(Doc, null, dryRun: true, target).Counts
        };
    }

    public string? SetUp()
    {
        string? why = MirrorParams.Ensure(_uiDoc.Application.Application, Doc);
        if (why == null) LiveFormulas.Refresh(Doc);   // the triggers need the parameter to exist
        return why;
    }

    public ApplyResult Preview(string targetGuid) => MirrorApplier.Apply(Doc, null, dryRun: true, Parse(targetGuid)).Result;
    public void ShowResults(string title, ApplyResult result, bool dryRun) => ParameterHelper.ShowResults(_uiDoc, title, result, dryRun);
    public string LiveActivity => LiveFormulas.LastActivity.TryGetValue(Doc.Title, out string? s) ? s : "";

    public (bool Saved, string? Why) Save(bool live, string targetGuid, string targetName, bool extend)
    {
        try
        {
            if (targetGuid.Length > 0 && extend)
            {
                string? why = MirrorParams.Extend(Doc, Parse(targetGuid));
                if (why != null) return (false, why);
            }
            FormulaRuleSet set = RuleStore.Load(Doc);
            string opened = set.ToJson();
            set.MirrorCheck = live;
            set.MirrorTargetGuid = targetGuid;
            set.MirrorTargetName = targetGuid.Length > 0 ? targetName : "";
            if (set.ToJson() == opened)
            {
                LiveFormulas.Refresh(Doc);   // only the parameter's categories changed
                return (true, null);
            }
            var r = ParameterHelper.TrySave(Doc, set, opened, replace: false);
            return (r.Saved, r.Why);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

/// <summary>
/// Parameter Helper → "Mirror Check": choose the parameter (KH_Mirror State, or a shared text
/// parameter of the user's own), switch live mode on or off for the model, and write every family
/// instance's state now.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ParameterMirrorCheckCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;
        Document? doc = ParameterHelper.ActiveProject(commandData);
        if (doc == null) return Result.Cancelled;
        if (ParameterHelper.Load(doc) == null) return Result.Cancelled;   // unreadable rules: said so already

        UIDocument uiDoc = commandData.Application.ActiveUIDocument!;
        using var window = new MirrorCheckWindow(new RevitMirrorHost(uiDoc));
        if (window.ShowDialog() != DialogResult.OK) return Result.Cancelled;
        if (window.CheckAfterSave)
        {
            FormulaRuleSet? set = ParameterHelper.Load(doc);
            if (set == null) return Result.Cancelled;
            ApplyResult r = MirrorApplier.Apply(doc, null, dryRun: false, MirrorParams.Target(doc, set)).Result;
            ParameterHelper.ShowResults(uiDoc, "Mirror Check — Check All Now", r, dryRun: false);
        }
        return Result.Succeeded;
    }
}
