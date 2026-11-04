using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// Mirror Check's live mode: when someone mirrors, flips, places or otherwise changes a family
/// instance, its KH_Mirror State is written in the SAME transaction (Dynamic Model
/// Update), so one Undo takes back both. Nothing new is borrowed: Revit is already changing that
/// element in this edit, so it is already the user's.
///
/// <para>Built like <see cref="LiveFormulas"/>, and driven by it: the on/off setting is
/// <see cref="FormulaRuleSet.MirrorCheck"/>, saved in the model with the rules, so LiveFormulas'
/// open / Sync / Reload Latest / DocumentChanged / Idling re-reads call <see cref="Refresh"/>, and
/// its Idling tick calls <see cref="FlushDue"/>. One event hub, not two.</para>
///
/// <para>A big edit (more than <see cref="LiveFormulas.InlineLimit"/> elements, or longer than
/// <see cref="LiveFormulas.InlineBudget"/>) and group members whose values do not vary are queued
/// and written a moment later through <see cref="MirrorApplier"/>, borrowing through central first.</para>
///
/// <para>The updater is OPTIONAL (no "missing third-party updater" warning for people without the
/// add-in) and never registered in an unattended batch Revit. Changes that arrive from central do
/// not run updaters; the out-of-date count and Check All Now catch those.</para>
/// </summary>
internal static class LiveMirror
{
    private static MirrorUpdater? _updater;
    // Per open model: the parameter live mode writes, when it is on (absent = off).
    private static readonly Dictionary<string, Guid> On = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (Document Doc, HashSet<ElementId> Ids, DateTime Since)> Queue = new(StringComparer.OrdinalIgnoreCase);

    public static bool Registered => _updater != null;

    private static string Key(Document doc) => doc.PathName.Length > 0 ? doc.PathName : "unsaved:" + doc.Title;

    public static void Startup(UIControlledApplication app)
    {
        try
        {
            _updater = new MirrorUpdater(app.ActiveAddInId);
            if (!UpdaterRegistry.IsUpdaterRegistered(_updater.GetUpdaterId()))
                UpdaterRegistry.RegisterUpdater(_updater, true);
        }
        catch (Exception ex)
        {
            _updater = null;
            Diagnostics.RunTrace.Mark("Mirror Check live mode not available: " + ex.Message);
        }
    }

    public static void Shutdown()
    {
        if (_updater == null) return;
        try { UpdaterRegistry.UnregisterUpdater(_updater.GetUpdaterId()); }
        catch { /* shutting down */ }
    }

    /// <summary>
    /// Sets the updater's triggers to match the model: any change to, or addition of, a family
    /// instance while Mirror Check is on and its parameters exist; none otherwise.
    /// </summary>
    public static void Refresh(Document doc, FormulaRuleSet set)
    {
        if (_updater == null) return;
        UpdaterId id = _updater.GetUpdaterId();
        UpdaterRegistry.RemoveDocumentTriggers(id, doc);
        On.Remove(Key(doc));
        Guid target = MirrorParams.Target(doc, set);
        if (!set.MirrorCheck || SharedParameterElement.Lookup(doc, target) == null) return;
        On[Key(doc)] = target;
        var filter = new ElementClassFilter(typeof(FamilyInstance));
        UpdaterRegistry.AddTrigger(id, doc, filter, Element.GetChangeTypeAny());
        UpdaterRegistry.AddTrigger(id, doc, filter, Element.GetChangeTypeElementAddition());
    }

    public static void Forget(Document doc)
    {
        On.Remove(Key(doc));
        Queue.Remove(Key(doc));
        try { if (_updater != null) UpdaterRegistry.RemoveDocumentTriggers(_updater.GetUpdaterId(), doc); }
        catch { /* closing anyway */ }
    }

    public static bool IsLive(Document doc) => On.ContainsKey(Key(doc));

    /// <summary>The parameter live mode writes in this model, or null when live is off.</summary>
    public static Guid? LiveTarget(Document doc) => On.TryGetValue(Key(doc), out Guid g) ? g : null;

    // ------------------------------------------------------------------ inside the transaction

    internal static void Execute(UpdaterData data)
    {
        if (MirrorApplier.Writing) return;   // Check All Now is writing; it already did the work
        Document doc = data.GetDocument();
        if (LiveTarget(doc) is not Guid target) return;

        var watch = Stopwatch.StartNew();
        var ids = data.GetModifiedElementIds().Concat(data.GetAddedElementIds()).ToList();
        if (ids.Count > LiveFormulas.InlineLimit)
        {
            foreach (ElementId i in ids) Enqueue(doc, i);
            LiveFormulas.Note(doc, $"Mirror Check: {ids.Count} elements changed at once — checked just after the edit");
            return;
        }
        int done = 0, changed = 0;
        foreach (ElementId i in ids)
        {
            if (watch.Elapsed > LiveFormulas.InlineBudget)
            {
                foreach (ElementId rest in ids.Skip(done)) Enqueue(doc, rest);
                break;
            }
            done++;
            if (doc.GetElement(i) is not FamilyInstance fi) continue;
            int n = MirrorApplier.WriteInline(fi, target);
            if (n < 0) Enqueue(doc, i);
            else changed += n;
        }
        if (changed > 0) LiveFormulas.Note(doc, $"Mirror Check: {changed} element(s) updated in the last edit ({watch.ElapsedMilliseconds} ms)");
    }

    private static void Enqueue(Document doc, ElementId id)
    {
        string key = Key(doc);
        if (!Queue.TryGetValue(key, out var p) || p.Ids.Count == 0) p = (doc, new HashSet<ElementId>(), DateTime.UtcNow);
        p.Ids.Add(id);
        Queue[key] = p;
    }

    // ------------------------------------------------------------------ just after the transaction

    /// <summary>Called from LiveFormulas' Idling tick: writes what has waited long enough.</summary>
    internal static void FlushDue()
    {
        if (Queue.Count == 0) return;
        foreach (string key in Queue.Keys.ToList())
        {
            var p = Queue[key];
            Document doc = p.Doc;
            if (p.Ids.Count == 0 || !doc.IsValidObject) { Queue.Remove(key); continue; }
            if (DateTime.UtcNow - p.Since < LiveFormulas.FlushDelay) continue;
            if (doc.IsReadOnly || doc.IsModifiable) continue;   // read-only, or mid-edit: next tick
            if (LiveTarget(doc) is not Guid target) { Queue.Remove(key); continue; }
            var ids = p.Ids.Where(i => doc.GetElement(i) != null).ToList();
            Queue.Remove(key);
            try
            {
                ApplyResult r = MirrorApplier.Apply(doc, ids, dryRun: false, target).Result;
                LiveFormulas.Note(doc, "Mirror Check live update: " + r.Summary(false));
            }
            catch (Exception ex)
            {
                LiveFormulas.Note(doc, "Mirror Check live update failed: " + ex.Message);
                Diagnostics.RunTrace.Mark("Mirror Check live flush: " + ex);
            }
        }
    }

    /// <summary>For the self-test: write everything queued now, without waiting for Idling.</summary>
    internal static ApplyResult? FlushNow(Document doc)
    {
        if (!Queue.TryGetValue(Key(doc), out var p)) return null;
        var ids = p.Ids.Where(i => doc.GetElement(i) != null).ToList();
        Queue.Remove(Key(doc));
        return MirrorApplier.Apply(doc, ids, dryRun: false, LiveTarget(doc)).Result;
    }

    internal static int QueuedCount(Document doc) => Queue.TryGetValue(Key(doc), out var p) ? p.Ids.Count : 0;
}

/// <summary>The Dynamic Model Update hook. All logic lives in <see cref="LiveMirror"/>.</summary>
internal sealed class MirrorUpdater : IUpdater
{
    private static readonly Guid UpdaterGuid = new("8e2f4b17-6a3c-4d9e-b5f1-0c7a9d3e2b64");
    private readonly UpdaterId _id;

    public MirrorUpdater(AddInId addIn) { _id = new UpdaterId(addIn, UpdaterGuid); }

    public void Execute(UpdaterData data)
    {
        // Never throw out of an updater: Revit would disable it and tell the user.
        try { LiveMirror.Execute(data); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("Mirror Check updater: " + ex); }
    }

    public UpdaterId GetUpdaterId() => _id;
    public ChangePriority GetChangePriority() => ChangePriority.Annotations;
    public string GetUpdaterName() => "KH-Tool Mirror Check";
    public string GetAdditionalInformation() =>
        "Keeps the KH_Mirror State parameter of family instances up to date as they change." +
        " Optional: models open normally without it, and Check All Now catches up.";
}
