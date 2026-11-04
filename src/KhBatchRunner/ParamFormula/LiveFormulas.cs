using System.Diagnostics;
using Application = Autodesk.Revit.ApplicationServices.Application;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// Parameter Helper's live mode: results follow edits as people make them.
///
/// <para><b>Inside the edit</b> (<see cref="FormulaUpdater"/>, Revit's Dynamic Model Update): an
/// element someone changed or placed gets its rules recalculated in the SAME transaction, so one
/// Undo takes back both. Writing there borrows nothing new — Revit is already changing that very
/// element in this transaction, so it is already the user's to change.</para>
///
/// <para><b>Just after the edit</b> (an Idling flush through <see cref="FormulaApplier"/>): when a
/// TYPE changes, its instances are not part of the user's transaction, and in a central model they
/// may belong to other people. They are queued and written a moment later in their own
/// transaction, borrowed through central first, exactly as Apply Now does. The same queue takes the
/// overflow when one edit touches more elements than <see cref="InlineLimit"/> or takes longer than
/// <see cref="InlineBudget"/>, so a big edit never stalls inside Revit's transaction.</para>
///
/// <para>The updater is registered OPTIONAL: someone who opens the model without this add-in gets
/// no "missing third-party updater" warning. It is never registered in an unattended (scheduled
/// batch) Revit — a batch run must do exactly what its actions say and nothing else.</para>
///
/// <para>Not covered, by Revit's design: changes that arrive from central (Sync, Reload Latest)
/// do not run updaters. Someone without the add-in can therefore leave values stale; Apply Now and
/// the out-of-date count in the rules window are how that is caught.</para>
/// </summary>
internal static class LiveFormulas
{
    public const int InlineLimit = 2000;
    public static readonly TimeSpan InlineBudget = TimeSpan.FromMilliseconds(150);
    /// <summary>How long a queued change waits before it is written, so a burst of edits is written once.</summary>
    public static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(2);

    private static FormulaUpdater? _updater;
    // Keyed by Key(doc), not by Document: Revit hands out a new Document wrapper for the same model
    // (UpdaterData.GetDocument, event args), and a wrapper is not a dependable dictionary key.
    private static readonly Dictionary<string, Pending> Queue = new(StringComparer.OrdinalIgnoreCase);
    // The rule set per open document, read once and refreshed when the rules are saved — the updater
    // runs inside other people's transactions and must not parse JSON on every one.
    private static readonly Dictionary<string, FormulaRuleSet> Rules = new(StringComparer.OrdinalIgnoreCase);
    // The rules' DataStorage element per model, so DocumentChanged can tell cheaply whether they moved.
    private static readonly Dictionary<string, ElementId?> StorageIds = new(StringComparer.OrdinalIgnoreCase);
    // The open models live mode knows, for the Idling safety check.
    private static readonly Dictionary<string, Document> Docs = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastCheck = DateTime.MinValue;
    /// <summary>How often Idling re-compares the cached rules with the model's.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(3);

    private sealed class Pending
    {
        public Document Doc = null!;
        public readonly HashSet<ElementId> Ids = new();
        public DateTime Since = DateTime.UtcNow;
    }

    private static string Key(Document doc) => doc.PathName.Length > 0 ? doc.PathName : "unsaved:" + doc.Title;

    /// <summary>What the last live write did, per model title, for the rules window to show.</summary>
    public static readonly Dictionary<string, string> LastActivity = new(StringComparer.OrdinalIgnoreCase);

    public static bool Registered => _updater != null;

    public static void Startup(UIControlledApplication app)
    {
        // Mirror Check's updater is registered on its own, but it is driven by this class's events
        // (see LiveMirror), so it only runs when the events below are subscribed.
        LiveMirror.Startup(app);
        try
        {
            _updater = new FormulaUpdater(app.ActiveAddInId);
            if (!UpdaterRegistry.IsUpdaterRegistered(_updater.GetUpdaterId()))
                UpdaterRegistry.RegisterUpdater(_updater, true);
            app.ControlledApplication.DocumentOpened += OnOpened;
            app.ControlledApplication.DocumentClosing += OnClosing;
            // The cached rules must follow the model: rules that arrive from central, and a rule save
            // that is undone or redone, change what the updater should run.
            app.ControlledApplication.DocumentReloadedLatest += OnReloadedLatest;
            app.ControlledApplication.DocumentSynchronizedWithCentral += OnSynchronized;
            app.ControlledApplication.DocumentChanged += OnChanged;
            app.Idling += OnIdling;
        }
        catch (Exception ex)
        {
            _updater = null;
            Diagnostics.RunTrace.Mark("Parameter Helper live mode not available: " + ex.Message);
        }
    }

    public static void Shutdown(UIControlledApplication app)
    {
        LiveMirror.Shutdown();
        if (_updater == null) return;
        try
        {
            app.ControlledApplication.DocumentOpened -= OnOpened;
            app.ControlledApplication.DocumentClosing -= OnClosing;
            app.ControlledApplication.DocumentReloadedLatest -= OnReloadedLatest;
            app.ControlledApplication.DocumentSynchronizedWithCentral -= OnSynchronized;
            app.ControlledApplication.DocumentChanged -= OnChanged;
            app.Idling -= OnIdling;
            UpdaterRegistry.UnregisterUpdater(_updater.GetUpdaterId());
        }
        catch { /* shutting down */ }
    }

    private static void OnOpened(object? sender, DocumentOpenedEventArgs e)
    {
        try { if (e.Document is { IsFamilyDocument: false, IsLinked: false } doc) Refresh(doc); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("Parameter Helper: rules not read on open: " + ex.Message); }
    }

    private static void OnReloadedLatest(object? sender, DocumentReloadedLatestEventArgs e) => SafeRefresh(e.Document, "Reload Latest");

    private static void OnSynchronized(object? sender, DocumentSynchronizedWithCentralEventArgs e) => SafeRefresh(e.Document, "Sync");

    /// <summary>
    /// Re-reads the rules when the element holding them was added, changed or deleted by any
    /// transaction — our own save, someone's Undo or Redo of it, or a merge from central.
    /// </summary>
    private static void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            Document doc = e.GetDocument();
            if (doc.IsFamilyDocument || doc.IsLinked) return;
            string key = Key(doc);
            bool touched;
            if (StorageIds.TryGetValue(key, out ElementId? id) && id != null)
                touched = e.GetModifiedElementIds().Contains(id) || e.GetDeletedElementIds().Contains(id) ||
                          e.GetAddedElementIds().Contains(id);
            else
                touched = e.GetAddedElementIds().Any(a => doc.GetElement(a) is Autodesk.Revit.DB.ExtensibleStorage.DataStorage);
            if (touched) SafeRefresh(doc, "rules changed");
        }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("Parameter Helper: DocumentChanged: " + ex.Message); }
    }

    private static void SafeRefresh(Document doc, string why)
    {
        try { if (doc is { IsFamilyDocument: false, IsLinked: false }) Refresh(doc); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark($"Parameter Helper: rules not re-read after {why}: {ex.Message}"); }
    }

    private static void OnClosing(object? sender, DocumentClosingEventArgs e)
    {
        Document doc = e.Document;
        Rules.Remove(Key(doc));
        Queue.Remove(Key(doc));
        StorageIds.Remove(Key(doc));
        Docs.Remove(Key(doc));
        LiveMirror.Forget(doc);
        try { if (_updater != null) UpdaterRegistry.RemoveDocumentTriggers(_updater.GetUpdaterId(), doc); }
        catch { /* closing anyway */ }
    }

    /// <summary>
    /// Reads the model's rules and sets the updater's triggers to match: none when live mode is off
    /// or no rule is enabled; otherwise any change to, or addition of, an element in a covered
    /// category (types included — a type change re-runs its instances). Mirror Check's setting is
    /// saved in the same rule set, so its triggers are set here too.
    /// </summary>
    public static void Refresh(Document doc)
    {
        if (_updater == null) return;
        UpdaterId id = _updater.GetUpdaterId();
        UpdaterRegistry.RemoveDocumentTriggers(id, doc);
        FormulaRuleSet set = RuleStore.Load(doc);
        Rules[Key(doc)] = set;
        StorageIds[Key(doc)] = RuleStore.Find(doc)?.Id;
        Docs[Key(doc)] = doc;
        LiveMirror.Refresh(doc, set);
        if (!set.Live) return;
        var cats = ParamAccess.Categories(set.Rules.Where(r => r.Enabled).SelectMany(r => r.Categories).Distinct());
        if (cats.Count == 0) return;
        var filter = new ElementMulticategoryFilter(cats);
        UpdaterRegistry.AddTrigger(id, doc, filter, Element.GetChangeTypeAny());
        UpdaterRegistry.AddTrigger(id, doc, filter, Element.GetChangeTypeElementAddition());
    }

    /// <summary>The rules the updater is running for this model (self-test: must match the model).</summary>
    internal static FormulaRuleSet? CachedRules(Document doc) => Rules.TryGetValue(Key(doc), out FormulaRuleSet? s) ? s : null;

    public static bool IsLive(Document doc) => Rules.TryGetValue(Key(doc), out FormulaRuleSet? s) && s.Live;

    // ------------------------------------------------------------------ inside the transaction

    internal static void Execute(UpdaterData data)
    {
        if (FormulaApplier.Writing) return;   // our own Apply is writing; it already did the work
        Document doc = data.GetDocument();
        if (!Rules.TryGetValue(Key(doc), out FormulaRuleSet? set) || !set.Live) return;

        var watch = Stopwatch.StartNew();
        var instances = new List<ElementId>();
        var types = new List<ElementId>();
        foreach (ElementId id in data.GetModifiedElementIds().Concat(data.GetAddedElementIds()))
        {
            Element? e = doc.GetElement(id);
            if (e is ElementType) types.Add(id);
            else if (e != null) instances.Add(id);
        }

        // A changed type: its own type-parameter results are written here, in the user's edit (the
        // type is already theirs to change). Its instances are someone else's business until borrowed
        // — queue them for the instance rules, which may read the type.
        int typeChanged = 0;
        foreach (ElementId t in types)
        {
            typeChanged += ApplyInline(doc, set, t);
            foreach (ElementId i in InstancesOf(doc, t))
                Enqueue(doc, i);
        }
        if (typeChanged > 0) Note(doc, $"{typeChanged} type value(s) recalculated in the last edit");

        if (instances.Count > InlineLimit)
        {
            foreach (ElementId i in instances) Enqueue(doc, i);
            Note(doc, $"{instances.Count} elements changed at once — recalculated just after the edit");
            return;
        }

        int done = 0, changed = 0;
        foreach (ElementId i in instances)
        {
            if (watch.Elapsed > InlineBudget)
            {
                // Out of time: the rest are written by the flush instead of holding up the edit.
                foreach (ElementId rest in instances.Skip(done)) Enqueue(doc, rest);
                break;
            }
            changed += ApplyInline(doc, set, i);
            done++;
        }
        if (changed > 0) Note(doc, $"{changed} value(s) recalculated in the last edit ({watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// Recalculates one element's rules inside the user's transaction; returns how many values changed.
    /// A rule that cannot run here (a missing value, a unit mismatch) is simply not applied — the
    /// updater must never fail someone's edit; Apply Now is where the reasons are shown.
    /// </summary>
    private static int ApplyInline(Document doc, FormulaRuleSet set, ElementId id)
    {
        Element? e = doc.GetElement(id);
        if (e?.Category == null) return 0;
        string cat = e.Category.BuiltInCategory.ToString();
        bool isType = e is ElementType;
        int changed = 0;
        var overlay = new Dictionary<string, FormulaValue>(StringComparer.OrdinalIgnoreCase);
        foreach ((FormulaRule rule, Formula formula) in RuleOrder.Sort(set.Rules).Ordered)
        {
            if (!rule.Categories.Contains(cat)) continue;
            if (rule.TargetOnType != isType) continue;   // type rules run on types, instance rules on instances
            try
            {
                Parameter? target = ParamAccess.Target(e, rule, out _);
                if (target == null || !ParamAccess.Writable(target)) continue;
                if (!isType && e.GroupId != ElementId.InvalidElementId) { Enqueue(doc, id); continue; }   // the flush knows the group rules
                FormulaType type = formula.Check(r =>
                {
                    if (!r.TypeOnly && overlay.TryGetValue(r.Name, out FormulaValue o)) return o.Type;
                    Parameter? p = isType ? ParamAccess.FindForType(e, r, out string? why) : ParamAccess.Find(e, r, out why);
                    return p == null ? throw new FormulaException(why ?? "") : ParamAccess.TypeOf(p);
                });
                if (ParamAccess.Fits(target, type) != null) continue;
                FormulaValue v = formula.Evaluate(new FormulaContext(r =>
                {
                    if (!r.TypeOnly && overlay.TryGetValue(r.Name, out FormulaValue o)) return o;
                    Parameter? p = isType ? ParamAccess.FindForType(e, r, out string? why) : ParamAccess.Find(e, r, out why);
                    return p == null ? throw new FormulaException(why ?? "") : ParamAccess.Read(p, r);
                }, x => ParamAccess.Format(doc, x)));
                overlay[rule.TargetName] = v;
                if (ParamAccess.Holds(doc, target, v)) continue;
                if (ParamAccess.IsTypeNameParam(target) && target.Element is ElementType t &&
                    (ParamAccess.BadNameWhy(ParamAccess.Format(doc, v).Trim()) != null ||
                     ParamAccess.NameTaken(doc, t, ParamAccess.Format(doc, v).Trim())))
                    continue;   // a taken name: Apply Now reports it; the live path never makes a duplicate
                if (ParamAccess.Write(doc, target, v)) changed++;
            }
            catch (FormulaException) { }
            catch (MissingValueException) { }
            catch (Exception ex) { Diagnostics.RunTrace.Mark("Parameter Helper live: " + ex.Message); }
        }
        return changed;
    }

    private static IEnumerable<ElementId> InstancesOf(Document doc, ElementId typeId)
    {
        try
        {
            return new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new FamilyInstanceFilter(doc, typeId)).ToElementIds();
        }
        catch
        {
            // Not a family symbol (a wall type, a floor type…): match on the type id instead.
            return new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(
                    new ElementId(BuiltInParameter.ELEM_TYPE_PARAM), typeId)))
                .ToElementIds();
        }
    }

    private static void Enqueue(Document doc, ElementId id)
    {
        if (!Queue.TryGetValue(Key(doc), out Pending? p)) Queue[Key(doc)] = p = new Pending { Doc = doc };
        if (p.Ids.Count == 0) p.Since = DateTime.UtcNow;
        p.Ids.Add(id);
    }

    internal static void Note(Document doc, string what)
    {
        try { LastActivity[doc.Title] = $"{DateTime.Now:HH:mm:ss}  {what}"; } catch { }
    }

    // ------------------------------------------------------------------ just after the transaction

    /// <summary>
    /// Re-reads the rules when the model's differ from the ones live mode is running. The events catch
    /// almost every way rules change, but not all: rolling back a TransactionGroup through the API
    /// raises no DocumentChanged (self-test, Revit 2026.5). This check, from Idling every few seconds,
    /// is the net under them. Returns whether the rules had to be re-read.
    /// </summary>
    internal static bool CheckCurrent(Document doc)
    {
        if (_updater == null || !doc.IsValidObject || doc.IsModifiable) return false;
        string key = Key(doc);
        string now = RuleStore.Load(doc).ToJson();
        if (Rules.TryGetValue(key, out FormulaRuleSet? cached) && cached.ToJson() == now) return false;
        Refresh(doc);
        return true;
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (DateTime.UtcNow - _lastCheck > CheckEvery)
        {
            _lastCheck = DateTime.UtcNow;
            foreach (Document d in Docs.Values.ToList())
            {
                try { if (d.IsValidObject && CheckCurrent(d)) Note(d, "rules re-read (they changed in the model)"); }
                catch (Exception ex) { Diagnostics.RunTrace.Mark("Parameter Helper: rules check: " + ex.Message); }
            }
        }
        LiveMirror.FlushDue();
        if (Queue.Count == 0) return;
        foreach (string key in Queue.Keys.ToList())
        {
            Pending p = Queue[key];
            Document doc = p.Doc;
            if (p.Ids.Count == 0 || !doc.IsValidObject) { Queue.Remove(key); continue; }
            if (DateTime.UtcNow - p.Since < FlushDelay) continue;
            if (doc.IsReadOnly || doc.IsModifiable) continue;   // read-only, or mid-edit: next tick
            if (!Rules.TryGetValue(key, out FormulaRuleSet? set) || !set.Live) { Queue.Remove(key); continue; }

            var ids = p.Ids.Where(i => doc.GetElement(i) != null).ToList();
            Queue.Remove(key);
            try
            {
                ApplyResult r = FormulaApplier.Apply(doc, set, ids, dryRun: false);
                Note(doc, $"live update: {r.Summary(false)}");
                // Elements owned by someone else stay queued for a later try — but not forever in a
                // loop: one retry after the next edit is enough; Apply Now reports the rest.
            }
            catch (Exception ex)
            {
                Note(doc, "live update failed: " + ex.Message);
                Diagnostics.RunTrace.Mark("Parameter Helper live flush: " + ex);
            }
        }
    }

    /// <summary>For the self-test: write everything queued now, without waiting for Idling.</summary>
    internal static ApplyResult? FlushNow(Document doc)
    {
        if (!Queue.TryGetValue(Key(doc), out Pending? p) || !Rules.TryGetValue(Key(doc), out FormulaRuleSet? set)) return null;
        var ids = p.Ids.Where(i => doc.GetElement(i) != null).ToList();
        Queue.Remove(Key(doc));
        return FormulaApplier.Apply(doc, set, ids, dryRun: false);
    }

    internal static int QueuedCount(Document doc) => Queue.TryGetValue(Key(doc), out Pending? p) ? p.Ids.Count : 0;
}

/// <summary>The Dynamic Model Update hook. All logic lives in <see cref="LiveFormulas"/>.</summary>
internal sealed class FormulaUpdater : IUpdater
{
    private static readonly Guid UpdaterGuid = new("d3a9e6b1-4c7f-4f2a-9e58-1b6c0d4e7a92");
    private readonly UpdaterId _id;

    public FormulaUpdater(AddInId addIn) { _id = new UpdaterId(addIn, UpdaterGuid); }

    public void Execute(UpdaterData data)
    {
        // Never throw out of an updater: Revit would disable it and tell the user.
        try { LiveFormulas.Execute(data); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("Parameter Helper updater: " + ex); }
    }

    public UpdaterId GetUpdaterId() => _id;
    public ChangePriority GetChangePriority() => ChangePriority.Annotations;
    public string GetUpdaterName() => "KH-Tool Parameter Helper";
    public string GetAdditionalInformation() =>
        "Recalculates Parameter Helper formula rules when elements change. Optional: models open " +
        "normally without it, and Apply Now catches up any values it missed.";
}
