using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Actions;

/// <summary>A workset rename the action wants to apply — surfaced to the user for approval
/// (per model) before anything is renamed.</summary>
public sealed class WorksetRenameProposal
{
    public required WorksetId Id { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    /// <summary>Which rule wants it ("3 categorie(s) → 10_STRUCT").</summary>
    public required string RuleLabel { get; init; }
    /// <summary>Set by the approval dialog; unapproved renames are skipped and the target
    /// workset is CREATED instead, so the rule still lands its elements correctly.</summary>
    public bool Approved { get; set; } = true;
}

/// <summary>
/// Model Batch Action — "Correct Workset": enforces the team workset convention per model.
/// <list type="number">
/// <item><b>Category rules</b> — every element of the rule's categories moves onto the rule's
/// target workset. Target resolution: exact-name workset → use; else a workset whose name
/// CONTAINS the rule keyword → RENAME to the target (per-model user approval via
/// <see cref="ApprovalCallback"/>); else create it.</item>
/// <item><b>RVT link rule</b> — every top-level link (its type + all instances) belongs alone in a
/// workset named <c>&lt;keyword&gt;_&lt;link model name&gt;</c> (".rvt" stripped). A workset holding
/// exactly ONE link is RENAMED to that name (per-model user approval, the same proposal/dialog flow
/// as the category renames); a workset holding several links keeps whichever link already matches
/// and moves the rest onto their own worksets (created as needed). Stray link parts are consolidated
/// onto the link's workset, and any non-link/CAD element instances left behind on a link workset are
/// swept to a "WIP" workset (unless a category rule already claims them).</item>
/// </list>
/// Two modes: <b>Check</b> (Category=Check — read-only rows "would rename/create/move …") and
/// <b>Fix</b> (Category=Action — one transaction per model; the engine syncs afterwards).
/// Moving elements EDITS them, so Fix declares the affected worksets via
/// <see cref="SelectWorksetsToOpen"/> (the engine reopens with them open — closed worksets
/// block edits, the Internal Relink lesson). Per-item failures (grouped elements, worksets
/// owned by others) degrade to rows, never fail the model.
/// </summary>
public sealed class CorrectWorksetAction : BatchActionBase
{
    public const string DisplayName = ActionNames.CorrectWorkset;

    /// <summary>Where the link-workset sweep parks non-link element instances that don't belong to a
    /// category rule but were sitting on a link's workset.</summary>
    private const string WipWorksetName = "WIP";

    private readonly IReadOnlyList<CorrectWorksetRule> _rules;
    private readonly bool _applyLinkRule;
    private readonly string _linkKeyword;
    private readonly bool _applyCadRule;
    private readonly string _cadWorksetName;
    private readonly bool _fix;
    private string _note = "";

    /// <summary>Shown the per-model rename proposals in FIX mode; returns after the user ticked
    /// the ones to apply (sets <see cref="WorksetRenameProposal.Approved"/>). Null = approve all
    /// (headless/tests). A WinForms dialog here is safe mid-batch — DialogSuppressor only
    /// auto-answers Revit's own dialogs.</summary>
    public Action<Document, IReadOnlyList<WorksetRenameProposal>>? ApprovalCallback { get; set; }

    public CorrectWorksetAction(IReadOnlyList<CorrectWorksetRule> rules, bool applyLinkRule,
        string linkKeyword, bool fix, bool applyCadRule = false, string cadWorksetName = "")
    {
        _rules = rules;
        _applyLinkRule = applyLinkRule;
        _linkKeyword = (linkKeyword ?? "").Trim();
        _applyCadRule = applyCadRule;
        _cadWorksetName = (cadWorksetName ?? "").Trim();
        _fix = fix;
    }

    public override string Name => DisplayName;

    // Check mode is read-only (engine never syncs); Fix syncs to central via the engine's Persist.
    public override ActionCategory Category => _fix ? ActionCategory.Action : ActionCategory.Check;

    public override IReadOnlyList<string> Columns { get; } =
        new[] { "Rule", "Item", "From", "To", "Result" };

    public override string PerModelNote => _note;

    // Fix EDITS elements/links/worksets — their worksets must be open (closed = not editable).
    // CHECK does not, and that asymmetry is real: the same model can pass Check and then have Fix
    // find work to do, because Check counted only what a closed open loaded. Audited and left
    // as-is by the user on 2026-08-16 — see WorksetPostureReason.
    public override WorksetPosture Worksets =>
        _fix ? WorksetPosture.PlansOwn : WorksetPosture.PartialByDesign;

    public override string WorksetPostureReason => _fix
        ? "Fix reopens naming both ends of every edit — a write touching a closed workset can fail " +
          "silently or throw."
        : "Check collects RevitLinkInstance, ImportInstance and category instances on the closed " +
          "open, so it under-reports off-target elements and can disagree with Fix on the same " +
          "model. Known and ACCEPTED by the user on 2026-08-16 (the export was the priority); not " +
          "an oversight. Fixing it means giving Check the same reopen Fix gets.";

    /// <summary>Worksets holding anything this run may move OR move things onto — both ends of every
    /// edit must be open (a write touching a closed workset can fail silently or throw): every
    /// off-target rule element plus its target workset, the link types/instances plus each link's
    /// Expected destination and the "WIP" sweep workset, and the linked-CAD elements.</summary>
    public override ICollection<WorksetId>? SelectWorksetsToOpen(Document doc)
    {
        if (!doc.IsWorkshared) return null;
        var ids = new HashSet<WorksetId>();
        Dictionary<string, Workset> byName = ReadWorksets(doc);

        foreach (CorrectWorksetRule rule in _rules)
        {
            List<BuiltInCategory> cats = ParseCategories(rule);
            if (cats.Count == 0) continue;
            foreach (Element e in CollectRuleElements(doc, cats))
                ids.Add(e.WorksetId);
            // The rule's target workset is a move DESTINATION — open it when it already exists.
            if (byName.TryGetValue(rule.TargetWorkset.Trim(), out Workset? targetWs)) ids.Add(targetWs.Id);
        }

        if (_applyLinkRule && _linkKeyword.Length > 0)
        {
            foreach (RevitLinkType type in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
            {
                if (type.IsNestedLink) continue;
                ids.Add(type.WorksetId);
                // The link's Expected destination workset, when it already exists.
                if (byName.TryGetValue(LinkWorksetNaming.Expected(_linkKeyword, type.Name), out Workset? expectedWs))
                    ids.Add(expectedWs.Id);
            }
            foreach (RevitLinkInstance inst in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                ids.Add(inst.WorksetId);
            // The sweep's WIP destination, when it already exists.
            if (byName.TryGetValue(WipWorksetName, out Workset? wipWs)) ids.Add(wipWs.Id);
        }

        if (_applyCadRule && _cadWorksetName.Length > 0)
        {
            foreach (Element e in LinkedCadElements(doc))
                ids.Add(e.WorksetId);
        }

        return ids;
    }

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        _ = DoCorrect(doc); // summary row only — the engine calls either Run or RunDetailRows
        return new Dictionary<string, string>
        {
            ["Rule"] = $"{_rules.Count} rule(s)",
            ["Item"] = "",
            ["From"] = "",
            ["To"] = "",
            ["Result"] = _note,
        };
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(
        UIApplication uiApp, Document doc) => DoCorrect(doc);

    /// <summary>One correction pass; the engine calls exactly one of Run/RunDetailRows per model.</summary>
    private List<IReadOnlyDictionary<string, string>> DoCorrect(Document doc)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        _note = "";

        if (!doc.IsWorkshared)
        {
            _note = "not workshared — skipped";
            rows.Add(Row("-", doc.Title, "", "", "not workshared — skipped"));
            return rows;
        }

        // Live view of the user worksets (name → workset), refreshed as renames/creates land.
        Dictionary<string, Workset> byName = ReadWorksets(doc);

        // ---- resolve every rule's target + collect the rename proposals -------------------
        var proposals = new List<WorksetRenameProposal>();
        var ruleResolutions = new List<(CorrectWorksetRule Rule, string Target, WorksetRenameProposal? Rename, bool NeedsCreate)>();
        foreach (CorrectWorksetRule rule in _rules)
        {
            string target = rule.TargetWorkset.Trim();
            if (target.Length == 0) { rows.Add(Row(rule.Label, "", "", "", "rule has no target workset — skipped")); continue; }

            if (byName.ContainsKey(target))
            {
                ruleResolutions.Add((rule, target, null, false));
                continue;
            }
            Workset? keywordMatch = rule.Keyword.Trim().Length == 0
                ? null
                : byName.Values.FirstOrDefault(w =>
                    w.Name.IndexOf(rule.Keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            if (keywordMatch != null)
            {
                var p = new WorksetRenameProposal
                    { Id = keywordMatch.Id, From = keywordMatch.Name, To = target, RuleLabel = rule.Label };
                proposals.Add(p);
                ruleResolutions.Add((rule, target, p, false));
            }
            else
            {
                ruleResolutions.Add((rule, target, null, true));
            }
        }

        // ---- link rule plan: build each link (top-level type + all its instances) as one unit,
        //      find its HOME workset, group homes and decide keep / rename / move ---------------
        var userWsIds = new HashSet<WorksetId>();
        foreach (Workset w in byName.Values) userWsIds.Add(w.Id);
        List<LinkPlan> plans = _applyLinkRule && _linkKeyword.Length > 0
            ? PlanLinks(doc, byName, userWsIds)
            : new List<LinkPlan>();

        // Link RENAMES ride the SAME proposal list + approval dialog as the category renames, so a
        // headless run (null callback) auto-approves them and the interactive dialog lists both.
        List<WorksetRenameProposal> linkRenames = plans
            .Where(p => p.Disp == LinkDisp.Rename).Select(p => p.Rename!).ToList();

        // Two links whose names strip to the same Expected can't each own a workset — they co-locate.
        List<IGrouping<string, LinkPlan>> sharedExpected = plans
            .GroupBy(p => p.Expected, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2).ToList();

        // Union of the category rules' categories + the linked-CAD element ids: the link-workset sweep
        // leaves these to their own step (rules / CAD rule) so nothing is handled twice.
        var ruleCats = new List<BuiltInCategory>();
        foreach (CorrectWorksetRule rule in _rules)
            foreach (BuiltInCategory bic in ParseCategories(rule))
                if (!ruleCats.Contains(bic)) ruleCats.Add(bic);
        bool doCad = _applyCadRule && _cadWorksetName.Length > 0;
        var cadIds = doCad
            ? new HashSet<ElementId>(LinkedCadElements(doc).Select(e => e.Id))
            : new HashSet<ElementId>();

        // ---- CHECK mode: report what WOULD happen, touch nothing ---------------------------
        if (!_fix)
        {
            int would = 0;
            foreach ((CorrectWorksetRule rule, string target, WorksetRenameProposal? rename, bool create) in ruleResolutions)
            {
                if (rename != null) { rows.Add(Row(rule.Label, rename.From, rename.From, rename.To, "would RENAME workset")); would++; }
                else if (create) { rows.Add(Row(rule.Label, target, "", target, "would CREATE workset")); would++; }

                List<BuiltInCategory> cats = ParseCategories(rule);
                int offTarget = CollectRuleElements(doc, cats)
                    .Count(e => !WsName(doc, e.WorksetId).Equals(target, StringComparison.OrdinalIgnoreCase));
                rows.Add(Row(rule.Label, $"{offTarget} element(s)", "(various)", target,
                    offTarget == 0 ? "already correct" : "would MOVE"));
                if (offTarget > 0) would++;
            }

            // Links — mirror the exact keep / rename / move plan the Fix pass will run.
            foreach (LinkPlan p in plans)
            {
                string src = p.HomeName.Length > 0 ? p.HomeName : "(no user workset)";
                if (p.Disp == LinkDisp.Pass)
                {
                    int strays = StrayCount(p);
                    string text = p.HomeGroupSize > 1 ? "already correct (keeps workset)" : "already correct";
                    if (strays > 0) { text += $" — would MOVE {strays} stray part(s)"; would++; }
                    rows.Add(Row("RVT link", p.Type.Name, src, p.Expected, text));
                }
                else if (p.Disp == LinkDisp.Rename)
                {
                    rows.Add(Row("RVT link", p.HomeName, p.HomeName, p.Expected, "would RENAME workset"));
                    would++;
                }
                else // Move
                {
                    string suffix = p.CannotRename
                        ? $" (workset '{p.Expected}' exists — cannot rename)"
                        : byName.ContainsKey(p.Expected) ? "" : " (create workset)";
                    rows.Add(Row("RVT link", p.Type.Name, src, p.Expected, "would MOVE" + suffix));
                    would++;
                }
            }
            foreach (IGrouping<string, LinkPlan> g in sharedExpected)
                rows.Add(Row("RVT link", $"{g.Count()} links", "(various)", g.Key,
                    $"WARNING: {g.Count()} links share expected workset '{g.Key}' — co-located"));

            // Link-workset sweep → WIP: count non-link/CAD/rule instances on the worksets that WILL be
            // final link worksets and already exist now (created-but-empty targets contribute nothing).
            var sweepIds = new HashSet<WorksetId>();
            foreach (LinkPlan p in plans)
            {
                if ((p.Disp == LinkDisp.Pass || p.Disp == LinkDisp.Rename) && p.HomeId != null) sweepIds.Add(p.HomeId);
                else if (p.Disp == LinkDisp.Move && byName.TryGetValue(p.Expected, out Workset? ews)) sweepIds.Add(ews.Id);
            }
            int sweepCount = CollectSweepCandidates(doc, sweepIds, ruleCats, cadIds).Count;
            if (sweepCount > 0)
            {
                rows.Add(Row("Link workset sweep", $"{sweepCount} element(s)", "(various)", WipWorksetName, "would MOVE"));
                would++;
            }

            if (doCad)
            {
                int cadOff = LinkedCadElements(doc)
                    .Count(e => !WsName(doc, e.WorksetId).Equals(_cadWorksetName, StringComparison.OrdinalIgnoreCase));
                rows.Add(Row("Linked CAD", $"{cadOff} element(s)", "(various)", _cadWorksetName,
                    cadOff == 0 ? "already correct" : "would MOVE" + (byName.ContainsKey(_cadWorksetName) ? "" : " (create workset)")));
                if (cadOff > 0) would++;
            }
            _note = would == 0 ? "already correct" : $"{would} correction(s) needed (check only — nothing changed)";
            return rows;
        }

        // ---- FIX mode: user approves the renames, then one transaction applies everything --
        List<WorksetRenameProposal> approvalList = proposals.Concat(linkRenames).ToList();
        if (approvalList.Count > 0)
            ApprovalCallback?.Invoke(doc, approvalList);

        int renamed = 0, created = 0, moved = 0, movedLinks = 0, strayParts = 0, swept = 0, movedCad = 0, failed = 0;
        using (var t = new Transaction(doc, "Correct worksets"))
        {
            t.Start();

            // 1. Category renames (an unapproved/failed rename falls back to creating the target).
            foreach (WorksetRenameProposal p in proposals)
            {
                if (!p.Approved) { rows.Add(Row(p.RuleLabel, p.From, p.From, p.To, "rename skipped by user — target will be created")); continue; }
                try
                {
                    WorksetTable.RenameWorkset(doc, p.Id, p.To);
                    renamed++;
                    rows.Add(Row(p.RuleLabel, p.From, p.From, p.To, "RENAMED"));
                }
                catch (Exception ex)
                {
                    failed++;
                    rows.Add(Row(p.RuleLabel, p.From, p.From, p.To, "rename FAILED: " + ex.Message));
                }
            }
            byName = ReadWorksets(doc);

            // 2. Link renames — re-check the name collision NOW (an earlier rename may have taken the
            //    name); on collision / unapproved / failure the link degrades to a MOVE onto Expected.
            foreach (LinkPlan p in plans.Where(x => x.Disp == LinkDisp.Rename))
            {
                WorksetRenameProposal prop = p.Rename!;
                if (!prop.Approved)
                {
                    rows.Add(Row("RVT link", prop.From, prop.From, prop.To, "rename skipped by user — target will be created"));
                    p.Disp = LinkDisp.Move;
                    continue;
                }
                if (byName.ContainsKey(prop.To))
                {
                    rows.Add(Row("RVT link", prop.From, prop.From, prop.To, $"workset '{prop.To}' exists — moved instead"));
                    p.Disp = LinkDisp.Move;
                    continue;
                }
                try
                {
                    WorksetTable.RenameWorkset(doc, prop.Id, prop.To);
                    renamed++;
                    rows.Add(Row("RVT link", prop.From, prop.From, prop.To, "RENAMED"));
                    byName = ReadWorksets(doc);
                    p.FinalDestId = p.HomeId; // the renamed home IS the link's workset now
                }
                catch (Exception ex)
                {
                    failed++;
                    rows.Add(Row("RVT link", prop.From, prop.From, prop.To, "rename FAILED: " + ex.Message));
                    p.Disp = LinkDisp.Move;
                }
            }
            byName = ReadWorksets(doc);

            // 3. Create whatever targets still don't exist (rule fallbacks, link Expected, WIP, CAD).
            foreach ((CorrectWorksetRule rule, string target, _, _) in ruleResolutions)
                created += EnsureWorkset(doc, byName, target, rule.Label, rows, ref failed);
            foreach (LinkPlan p in plans.Where(x => x.Disp == LinkDisp.Move))
            {
                created += EnsureWorkset(doc, byName, p.Expected, "RVT link", rows, ref failed);
                if (byName.TryGetValue(p.Expected, out Workset? dw)) p.FinalDestId = dw.Id;
            }

            // Final link worksets = every workset a link ends up on; sweep those for stray elements.
            // (Link moves only add link parts, so the candidate set is stable through the moves below.)
            var finalDestIds = new HashSet<WorksetId>();
            foreach (LinkPlan p in plans) if (p.FinalDestId != null) finalDestIds.Add(p.FinalDestId);
            List<Element> sweepElems = CollectSweepCandidates(doc, finalDestIds, ruleCats, cadIds);
            if (sweepElems.Count > 0)
                created += EnsureWorkset(doc, byName, WipWorksetName, "Link workset sweep", rows, ref failed);

            if (doCad) created += EnsureWorkset(doc, byName, _cadWorksetName, "Linked CAD", rows, ref failed);

            // 4. Move rule elements.
            foreach ((CorrectWorksetRule rule, string target, _, _) in ruleResolutions)
            {
                if (!byName.TryGetValue(target, out Workset? targetWs)) continue; // create failed — already reported
                List<BuiltInCategory> cats = ParseCategories(rule);
                int ruleMoved = 0, ruleSkipped = 0;
                foreach (Element e in CollectRuleElements(doc, cats))
                {
                    if (e.WorksetId == targetWs.Id) continue;
                    Parameter? p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    // Read-only partition param = element can't move (group member, owned, …).
                    if (p is null || p.IsReadOnly) { ruleSkipped++; continue; }
                    try { p.Set(targetWs.Id.IntegerValue); ruleMoved++; }
                    catch { ruleSkipped++; }
                }
                moved += ruleMoved;
                rows.Add(Row(rule.Label, $"{ruleMoved} element(s)", "(various)", target,
                    "MOVED" + (ruleSkipped > 0 ? $"; {ruleSkipped} skipped (in groups / not editable)" : "")));
            }

            // 5. Move each link's parts (type + instances) onto its destination workset. A Pass/renamed
            //    link only consolidates its strays; a Move link relocates the whole link.
            foreach (LinkPlan p in plans)
            {
                if (p.FinalDestId == null) continue; // create failed — already reported
                WorksetId dest = p.FinalDestId!;
                int linkMoved = 0, linkSkipped = 0;
                foreach (Element e in p.Parts)
                {
                    if (e.WorksetId == dest) continue;
                    Parameter? param = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (param is null || param.IsReadOnly) { linkSkipped++; continue; }
                    try { param.Set(dest.IntegerValue); linkMoved++; } catch { linkSkipped++; }
                }
                if (p.Disp == LinkDisp.Pass || p.Disp == LinkDisp.Rename) // keeps its (possibly renamed) home
                {
                    strayParts += linkMoved;
                    if (linkMoved > 0 || linkSkipped > 0)
                        rows.Add(Row("RVT link", p.Type.Name, p.Expected, p.Expected,
                            $"consolidated {linkMoved} stray part(s)" + (linkSkipped > 0 ? $"; {linkSkipped} skipped (not editable)" : "")));
                }
                else
                {
                    string src = p.HomeName.Length > 0 ? p.HomeName : "(no user workset)";
                    if (linkSkipped > 0) { failed++; rows.Add(Row("RVT link", p.Type.Name, src, p.Expected, "move PARTIAL — some parts not editable")); }
                    else { movedLinks++; rows.Add(Row("RVT link", p.Type.Name, src, p.Expected, "MOVED" + (p.CannotRename ? $" (workset '{p.Expected}' existed — cannot rename)" : ""))); }
                }
            }

            // A home workset every link moved OUT of is kept in place (never deleted) — note it empty.
            foreach (IGrouping<int, LinkPlan> g in plans.Where(p => p.HomeId != null).GroupBy(p => p.HomeId!.IntegerValue))
            {
                if (g.Any(p => p.FinalDestId != null && p.FinalDestId == p.HomeId)) continue; // a link still lives here
                LinkPlan any = g.First();
                if (any.HomeId != null && !WorksetHasInstances(doc, any.HomeId))
                    rows.Add(Row("RVT link", any.HomeName, any.HomeName, "", "workset now empty — left in place"));
            }

            // 6. Link-workset sweep: non-link, non-CAD, non-rule element INSTANCES still sitting on a
            //    link workset move to WIP. Element TYPES other than link/CAD are left alone (instances
            //    only, matching CollectRuleElements; link/CAD types move via their own steps).
            if (sweepElems.Count > 0 && byName.TryGetValue(WipWorksetName, out Workset? wipWs))
            {
                int sweepSkipped = 0;
                foreach (Element e in sweepElems)
                {
                    if (e.WorksetId == wipWs.Id) continue;
                    Parameter? param = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (param is null || param.IsReadOnly) { sweepSkipped++; continue; }
                    try { param.Set(wipWs.Id.IntegerValue); swept++; } catch { sweepSkipped++; }
                }
                rows.Add(Row("Link workset sweep", $"{swept} element(s)", "(various)", WipWorksetName,
                    "MOVED" + (sweepSkipped > 0 ? $"; {sweepSkipped} skipped (not editable)" : "")));
            }

            // 7. Move ALL linked CAD (type + instances) onto the one CAD workset.
            if (doCad && byName.TryGetValue(_cadWorksetName, out Workset? cadWs))
            {
                int cadSkipped = 0;
                foreach (Element e in LinkedCadElements(doc))
                {
                    if (e.WorksetId == cadWs.Id) continue;
                    Parameter? p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p is null || p.IsReadOnly) { cadSkipped++; continue; }
                    try { p.Set(cadWs.Id.IntegerValue); movedCad++; }
                    catch { cadSkipped++; }
                }
                rows.Add(Row("Linked CAD", $"{movedCad} element(s)", "(various)", _cadWorksetName,
                    "MOVED" + (cadSkipped > 0 ? $"; {cadSkipped} skipped (view-specific / not editable)" : "")));
            }

            // Degenerate co-location warning — surfaced in the Fix log too.
            foreach (IGrouping<string, LinkPlan> g in sharedExpected)
                rows.Add(Row("RVT link", $"{g.Count()} links", "(various)", g.Key,
                    $"WARNING: {g.Count()} links share expected workset '{g.Key}' — co-located"));

            t.Commit();
        }

        _note = $"{renamed} renamed, {created} created, {moved} element(s) moved, {movedLinks} link(s) moved"
                + (strayParts > 0 ? $", {strayParts} stray part(s) consolidated" : "")
                + (swept > 0 ? $", {swept} swept to WIP" : "")
                + (doCad ? $", {movedCad} CAD moved" : "")
                + (failed > 0 ? $", {failed} failed" : "");
        return rows;
    }

    /// <summary>Creates <paramref name="name"/> when absent; returns 1 when created (0 otherwise);
    /// failures report a row and bump <paramref name="failed"/>.</summary>
    private static int EnsureWorkset(Document doc, Dictionary<string, Workset> byName, string name,
        string ruleLabel, List<IReadOnlyDictionary<string, string>> rows, ref int failed)
    {
        if (byName.ContainsKey(name)) return 0;
        try
        {
            Workset ws = Workset.Create(doc, name);
            byName[name] = ws;
            rows.Add(Row(ruleLabel, name, "", name, "CREATED workset"));
            return 1;
        }
        catch (Exception ex)
        {
            failed++;
            rows.Add(Row(ruleLabel, name, "", name, "create FAILED: " + ex.Message));
            return 0;
        }
    }

    /// <summary>Model-element instances of the given categories — never element types, never
    /// view-specific annotation.</summary>
    private static IEnumerable<Element> CollectRuleElements(Document doc, List<BuiltInCategory> cats)
    {
        if (cats.Count == 0) return Enumerable.Empty<Element>();
        return new FilteredElementCollector(doc)
            .WherePasses(new ElementMulticategoryFilter(cats))
            .WhereElementIsNotElementType()
            .Where(e => !e.ViewSpecific);
    }

    private static List<BuiltInCategory> ParseCategories(CorrectWorksetRule rule)
    {
        var cats = new List<BuiltInCategory>();
        foreach (string s in rule.Categories)
            if (Enum.TryParse(s, out BuiltInCategory bic))
                cats.Add(bic);
        return cats;
    }

    private static IEnumerable<RevitLinkInstance> InstancesOn(Document doc, ElementId typeId) =>
        new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>().Where(i => i.GetTypeId() == typeId);

    /// <summary>Builds a <see cref="LinkPlan"/> per top-level link, then groups them by HOME workset
    /// and decides keep (Pass) / rename / move: a workset with exactly one link is renamed to the
    /// link's Expected name (or moved if that name is taken), while a workset holding several links
    /// keeps whichever already matches and moves the rest. Homeless links (no user workset) move.</summary>
    private List<LinkPlan> PlanLinks(Document doc, Dictionary<string, Workset> byName, HashSet<WorksetId> userWsIds)
    {
        var plans = new List<LinkPlan>();
        foreach (RevitLinkType type in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
        {
            if (type.IsNestedLink) continue;
            List<RevitLinkInstance> instances = InstancesOn(doc, type.Id).ToList();
            WorksetId? home = HomeWorkset(type, instances, userWsIds);
            plans.Add(new LinkPlan
            {
                Type = type,
                Parts = instances.Cast<Element>().Append(type).ToList(),
                Expected = LinkWorksetNaming.Expected(_linkKeyword, type.Name),
                HomeId = home,
                HomeName = home is null ? "" : WsName(doc, home),
            });
        }

        // Homeless links (every part on a non-user workset) simply move onto their Expected workset.
        foreach (LinkPlan p in plans.Where(p => p.HomeId is null))
            p.Disp = LinkDisp.Move;

        // Group the homed links by their home workset and decide keep / rename / move per the convention.
        foreach (IGrouping<int, LinkPlan> group in plans.Where(p => p.HomeId != null)
                     .GroupBy(p => p.HomeId!.IntegerValue))
        {
            List<LinkPlan> links = group.ToList();
            int n = links.Count;
            string wName = links[0].HomeName;
            foreach (LinkPlan p in links) p.HomeGroupSize = n;

            // The link(s), if any, whose Expected already equals this workset's name — they stay.
            List<LinkPlan> stayers = links
                .Where(p => p.Expected.Equals(wName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (n == 1)
            {
                LinkPlan p = links[0];
                if (stayers.Count == 1) { p.Disp = LinkDisp.Pass; p.FinalDestId = p.HomeId; }
                else if (byName.ContainsKey(p.Expected)) { p.Disp = LinkDisp.Move; p.CannotRename = true; }
                else
                {
                    p.Disp = LinkDisp.Rename;
                    p.Rename = new WorksetRenameProposal
                        { Id = p.HomeId!, From = wName, To = p.Expected, RuleLabel = "RVT link" };
                }
            }
            else
            {
                // n > 1: matching links keep the workset, the rest move to their own Expected.
                foreach (LinkPlan p in links)
                    if (stayers.Contains(p)) { p.Disp = LinkDisp.Pass; p.FinalDestId = p.HomeId; }
                    else p.Disp = LinkDisp.Move;
            }
        }

        return plans;
    }

    /// <summary>A link's home workset: the type's workset when it's a user workset, else the first
    /// instance's user workset; null when every part sits on a non-user (system) workset.</summary>
    private static WorksetId? HomeWorkset(RevitLinkType type, List<RevitLinkInstance> instances, HashSet<WorksetId> userWsIds)
    {
        if (userWsIds.Contains(type.WorksetId)) return type.WorksetId;
        foreach (RevitLinkInstance inst in instances)
            if (userWsIds.Contains(inst.WorksetId)) return inst.WorksetId;
        return null;
    }

    /// <summary>Parts of a Pass link sitting off its home workset (to be consolidated onto it).</summary>
    private static int StrayCount(LinkPlan p) =>
        p.HomeId is null ? p.Parts.Count : p.Parts.Count(e => !(e.WorksetId == p.HomeId));

    /// <summary>Element INSTANCES still on a final link workset that this run isn't otherwise moving —
    /// not a link part (RevitLinkInstance / RevitLinkType), not a rule category (those follow their
    /// rule), not linked CAD (that follows the CAD rule). Element TYPES are excluded on purpose:
    /// the sweep is instances-only (like <see cref="CollectRuleElements"/>); link/CAD types are moved
    /// by their own steps.</summary>
    private static List<Element> CollectSweepCandidates(Document doc, ICollection<WorksetId> worksetIds,
        List<BuiltInCategory> ruleCats, HashSet<ElementId> cadIds)
    {
        var result = new List<Element>();
        if (worksetIds.Count == 0) return result;
        ElementFilter? notRuleCat = ruleCats.Count > 0 ? new ElementMulticategoryFilter(ruleCats, true) : null;
        var seen = new HashSet<ElementId>();
        foreach (WorksetId wsId in worksetIds)
        {
            FilteredElementCollector col = new FilteredElementCollector(doc)
                .WherePasses(new ElementWorksetFilter(wsId))
                .WhereElementIsNotElementType();
            if (notRuleCat != null) col = col.WherePasses(notRuleCat);
            foreach (Element e in col)
            {
                if (e.ViewSpecific) continue;
                if (e is RevitLinkInstance) continue; // a link part — the link step moves it
                if (cadIds.Contains(e.Id)) continue;   // linked CAD — the CAD step moves it
                if (seen.Add(e.Id)) result.Add(e);     // a workset may repeat across links — de-dupe
            }
        }
        return result;
    }

    /// <summary>True when the workset still holds at least one element instance.</summary>
    private static bool WorksetHasInstances(Document doc, WorksetId wsId) =>
        new FilteredElementCollector(doc)
            .WherePasses(new ElementWorksetFilter(wsId))
            .WhereElementIsNotElementType().Any();

    /// <summary>Every LINKED-CAD element to move onto the CAD workset: each non-view-specific linked
    /// <see cref="ImportInstance"/> (IsLinked — excludes IMPORTED, non-linked CAD) plus its
    /// <see cref="CADLinkType"/>. View-specific (single-view) CAD imports are skipped (they can't
    /// move worksets meaningfully).</summary>
    private static IEnumerable<Element> LinkedCadElements(Document doc)
    {
        var typeIds = new HashSet<ElementId>();
        foreach (ImportInstance inst in new FilteredElementCollector(doc)
                     .OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
        {
            if (!inst.IsLinked || inst.ViewSpecific) continue;
            typeIds.Add(inst.GetTypeId());
            yield return inst;
        }
        foreach (ElementId tid in typeIds)
        {
            Element? t = doc.GetElement(tid);
            if (t != null) yield return t;
        }
    }

    private static Dictionary<string, Workset> ReadWorksets(Document doc)
    {
        var map = new Dictionary<string, Workset>(StringComparer.OrdinalIgnoreCase);
        foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            map[w.Name] = w;
        return map;
    }

    private static string WsName(Document doc, WorksetId id)
    {
        try { return doc.GetWorksetTable().GetWorkset(id).Name; }
        catch { return ""; }
    }

    private static IReadOnlyDictionary<string, string> Row(string rule, string item, string from, string to, string result) =>
        new Dictionary<string, string>
        {
            ["Rule"] = rule,
            ["Item"] = item,
            ["From"] = from,
            ["To"] = to,
            ["Result"] = result,
        };

    /// <summary>How a link's home workset is handled: kept as-is, renamed to Expected, or the link
    /// moved onto Expected (created or existing).</summary>
    private enum LinkDisp { Pass, Rename, Move }

    /// <summary>One top-level link (its <see cref="RevitLinkType"/> + all its instances) planned as a
    /// single unit — its Expected name, its home workset and the chosen disposition.</summary>
    private sealed class LinkPlan
    {
        public required RevitLinkType Type { get; init; }
        /// <summary>The link's instances followed by its type — everything moved as one.</summary>
        public required List<Element> Parts { get; init; }
        /// <summary>The one true workset name for this link — see <see cref="LinkWorksetNaming.Expected"/>.</summary>
        public required string Expected { get; init; }
        /// <summary>The user workset the link lives in (type's, else first instance's); null when the
        /// link sits entirely on system worksets.</summary>
        public required WorksetId? HomeId { get; init; }
        public required string HomeName { get; init; }
        /// <summary>How many homed links share this link's home workset (drives the case-3 wording).</summary>
        public int HomeGroupSize { get; set; } = 1;
        public LinkDisp Disp { get; set; } = LinkDisp.Move;
        /// <summary>Move because the Expected workset already exists (a rename would collide).</summary>
        public bool CannotRename { get; set; }
        /// <summary>Set when <see cref="Disp"/> is Rename — rides the shared approval proposal list.</summary>
        public WorksetRenameProposal? Rename { get; set; }
        /// <summary>Resolved during Fix: the workset the link's parts land on (home when kept/renamed,
        /// else the Expected workset). Null means its target couldn't be created.</summary>
        public WorksetId? FinalDestId { get; set; }
    }
}
