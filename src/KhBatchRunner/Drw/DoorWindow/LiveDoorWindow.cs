using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>
/// Enlarge Door &amp; Window's automatic update, after Parameter Helper's live mode.
///
/// <para><b>Inside the edit</b> (<see cref="DoorWindowUpdater"/>): when a door or window type is
/// edited, an instance is placed, deleted or swapped to another type, one of the instance
/// parameters a set's schedule shows changes, or a set's schedule itself is edited, each live set
/// has its table values, labels, Qty and header text rewritten in the SAME transaction, so one Undo
/// takes back both. A door that only moves (with its wall, 42 s on Snowdon before 2026-10-21) no
/// longer wakes it.</para>
///
/// <para><b>Just after the edit</b> (Idling): a change the cells cannot absorb in place — a Type
/// Mark added, removed or merged, a type of another size, a value now wrapping to more lines, a
/// field added to the schedule — queues a rebuild of that set (<see cref="LegendBuilder.Run"/>),
/// done a moment later in its own undo step, once a burst of edits has settled.</para>
///
/// <para>A set with Live off is left alone until a build or <see cref="ApplyNow"/>. Registered
/// OPTIONAL, so a model opened without DRW Helper raises no warning; never in an unattended batch
/// Revit. Changes from central (Sync, Reload Latest) run no updaters — Apply now catches those up.</para>
/// </summary>
internal static class LiveDoorWindow
{
    public static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(2);

    private static DoorWindowUpdater? _updater;
    private sealed class Pending
    {
        public Document Doc = null!;
        public readonly HashSet<string> Sets = new(StringComparer.Ordinal);
        public DateTime Since = DateTime.UtcNow;
    }
    private static readonly Dictionary<string, Pending> Queue = new(StringComparer.OrdinalIgnoreCase);
    private static bool _flushing;

    /// <summary>Each set's own elements, by model and set key: found once by scanning every legend
    /// (<see cref="DwTag.All"/>), then reused while every one still carries its tag.</summary>
    private static readonly Dictionary<string, Dictionary<string, List<ElementId>>> Tagged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the last live update did, per model title (for diagnostics and the tests).</summary>
    public static readonly Dictionary<string, string> LastActivity = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(Document doc) => doc.PathName.Length > 0 ? doc.PathName : "unsaved:" + doc.Title;

    private static readonly BuiltInCategory[] Categories = { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows };

    /// <summary>The parameters that put an instance on another type.</summary>
    private static readonly BuiltInParameter[] TypeSwaps =
        { BuiltInParameter.ELEM_TYPE_PARAM, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM, BuiltInParameter.SYMBOL_ID_PARAM, BuiltInParameter.ELEM_FAMILY_PARAM };

    public static void Startup(UIControlledApplication app)
    {
        try
        {
            _updater = new DoorWindowUpdater(app.ActiveAddInId);
            UpdaterId id = _updater.GetUpdaterId();
            if (!UpdaterRegistry.IsUpdaterRegistered(id)) UpdaterRegistry.RegisterUpdater(_updater, true);
            var cats = new ElementMulticategoryFilter(Categories);
            var types = new LogicalAndFilter(cats, new ElementIsElementTypeFilter(false));
            var instances = new LogicalAndFilter(cats, new ElementIsElementTypeFilter(true));
            // Types: any edit (rare, and every one can change a row). Instances: placed, deleted or
            // swapped to another type; their schedule fields are added per model (Watch).
            UpdaterRegistry.AddTrigger(id, types, Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, instances, Element.GetChangeTypeElementAddition());
            UpdaterRegistry.AddTrigger(id, instances, Element.GetChangeTypeElementDeletion());
            foreach (BuiltInParameter b in TypeSwaps)
                UpdaterRegistry.AddTrigger(id, instances, Element.GetChangeTypeParameter(new ElementId(b)));
            // A set's schedule edited: a heading renamed, a field added, a filter changed.
            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(ViewSchedule)), Element.GetChangeTypeAny());
            // A material edited: a row may show its Description and the like (2026-10-21).
            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(Material)), Element.GetChangeTypeAny());
            app.Idling += OnIdling;
            app.ControlledApplication.DocumentOpened += OnOpened;
            app.ControlledApplication.DocumentChanged += OnChanged;
        }
        catch (Exception ex)
        {
            _updater = null;
            Diagnostics.RunTrace.Mark("Enlarge Door & Window live update not available: " + ex.Message);
        }
    }

    public static void Shutdown(UIControlledApplication app)
    {
        if (_updater == null) return;
        try
        {
            app.Idling -= OnIdling;
            app.ControlledApplication.DocumentOpened -= OnOpened;
            app.ControlledApplication.DocumentChanged -= OnChanged;
            UpdaterRegistry.UnregisterUpdater(_updater.GetUpdaterId());
        }
        catch { /* shutting down */ }
    }

    private static void OnOpened(object? sender, DocumentOpenedEventArgs e)
    {
        try { if (e.Document is { IsFamilyDocument: false, IsLinked: false } doc) Watch(doc); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("Enlarge Door & Window: live triggers not set: " + ex.Message); }
    }

    /// <summary>
    /// The instance parameters this model's live sets show (their schedules' instance fields,
    /// combined fields' parts included), as triggers for this model only. Called when a model opens
    /// and after every build or Live change; outside the updater (the registry cannot change there).
    /// </summary>
    internal static void Watch(Document doc)
    {
        // Never fails a build: without these triggers only instance-field edits wait for Apply now.
        try { WatchFields(doc); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("Enlarge Door & Window: live triggers not set: " + ex.Message); }
    }

    private static void WatchFields(Document doc)
    {
        if (_updater == null) return;
        UpdaterId id = _updater.GetUpdaterId();
        UpdaterRegistry.RemoveDocumentTriggers(id, doc);
        Tagged.Remove(Key(doc));
        var parameters = new HashSet<long>();
        foreach (DoorWindowPreset p in DwDefinitions.Load(doc).Where(p => p.Live))
        {
            if (DwSchedule.Find(doc, p) is not { } s) continue;
            ScheduleDefinition d = s.Definition;
            foreach (string from in DwValueFrom.Get(s).Values.Where(v => v.StartsWith(DwValueFrom.InstancePrefix)))
            {
                string name = from.Substring(DwValueFrom.InstancePrefix.Length);
                FamilyInstance? one = new FilteredElementCollector(doc).OfCategory(p.Category == DwCategory.Door ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault(i => i.LookupParameter(name) != null);
                if (one?.LookupParameter(name) is { } prm) parameters.Add(prm.Id.Value);
            }
            foreach (ScheduleField f in d.GetFieldOrder().Select(d.GetField).Where(f => !f.IsHidden))
            {
                if (f.FieldType == ScheduleFieldType.Instance) parameters.Add(f.ParameterId.Value);
                if (f.IsCombinedParameterField)
                    foreach (TableCellCombinedParameterData part in f.GetCombinedParameters()) parameters.Add(part.ParamId.Value);
            }
        }
        parameters.Remove(ElementId.InvalidElementId.Value);
        Watched[Key(doc)] = parameters;
        // A frame's Cell Width changed by hand in a legend (the user, 2026-10-22): the rebuild
        // remembers it and moves the cells after it along.
        FamilySymbol? frameType = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(f => f.FamilyName == LegendBuilder.HeaderFamily);
        FamilyInstance? frame = frameType == null ? null : new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>().FirstOrDefault(i => i.Symbol.Id == frameType.Id);
        if (frame?.LookupParameter("Cell Width") is { } cw)
            UpdaterRegistry.AddTrigger(id, doc, new FamilyInstanceFilter(doc, frameType!.Id), Element.GetChangeTypeParameter(cw.Id));
        if (parameters.Count == 0) return;
        var instances = new LogicalAndFilter(new ElementMulticategoryFilter(Categories), new ElementIsElementTypeFilter(true));
        foreach (long prm in parameters)
            UpdaterRegistry.AddTrigger(id, doc, instances, Element.GetChangeTypeParameter(new ElementId(prm)));
    }

    /// <summary>Called by the updater, inside the user's transaction.</summary>
    internal static void Execute(UpdaterData data)
    {
        Document doc = data.GetDocument();
        if (doc.IsFamilyDocument || doc.IsLinked) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _inUpdater = true;
        try { Run(doc, data); }
        finally { _inUpdater = false; LastExecuteMs = watch.Elapsed.TotalMilliseconds; ExecuteCalls++; }
    }

    /// <summary>The instance parameters watched per model (<see cref="Watch"/>).</summary>
    private static readonly Dictionary<string, HashSet<long>> Watched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What woke the last updater call, element by element (diagnostics and the tests).</summary>
    internal static string LastTrigger = "";

    private static string Triggers(Document doc, UpdaterData data)
    {
        var said = new List<string>();
        if (data.GetAddedElementIds().Count > 0) said.Add($"{data.GetAddedElementIds().Count} added");
        if (data.GetDeletedElementIds().Count > 0) said.Add($"{data.GetDeletedElementIds().Count} deleted");
        Watched.TryGetValue(Key(doc), out HashSet<long>? watched);
        var parameters = TypeSwaps.Select(b => (long)b).Concat(watched ?? Enumerable.Empty<long>()).ToList();
        foreach (ElementId id in data.GetModifiedElementIds().Take(5))
        {
            Element? e = doc.GetElement(id);
            string what = e is ViewSchedule ? "schedule" : e is ElementType ? "type" : "instance";
            var hits = parameters.Where(prm => data.IsChangeTriggered(id, Element.GetChangeTypeParameter(new ElementId(prm))))
                .Select(prm => prm < 0 ? ((BuiltInParameter)prm).ToString() : prm.ToString()).ToList();
            said.Add($"{what} {id.Value} {e?.Name}" + (hits.Count > 0 ? " [" + string.Join(", ", hits) + "]" : ""));
        }
        return string.Join("; ", said);
    }

    /// <summary>How long the last updater call took, and how many calls so far (the self-test's timings).</summary>
    internal static double LastExecuteMs;
    internal static int ExecuteCalls;
    /// <summary>Sets brought up to date by the updater so far: the real work, as opposed to a call
    /// that found nothing of ours changed (a door moved makes Revit touch the model's other
    /// schedules, which wakes the updater for a moment).</summary>
    internal static int SyncCalls;

    /// <summary>Dev hook: called inside the updater before anything else (DW TIMING's schedule probe).</summary>
    internal static Action<Document>? Probe;

    /// <summary>After each transaction (and on Idling): the next edit starts fresh.</summary>
    private static void OnChanged(object? sender, DocumentChangedEventArgs e) => EditOver();

    private static void EditOver()
    {
        RefreshedByUs.Clear();
        _callsSinceIdle = 0;
    }

    /// <summary>Schedules this updater refreshed during the current edit: reading one marks it
    /// changed, which would wake the updater again, and again — Revit hung in that loop on the
    /// first self-test of the schedule trigger (2026-10-21).</summary>
    private static readonly HashSet<long> RefreshedByUs = new();
    /// <summary>Updater calls during the current edit: a backstop, should anything else loop.</summary>
    private static int _callsSinceIdle;
    /// <summary>Inside the updater: only its own schedule reads count as "ours" (a read from Apply
    /// now or a test, outside any edit, made the next real schedule edit look like one).</summary>
    private static bool _inUpdater;
    private const int MaxCallsPerEdit = 4;

    private static void Run(Document doc, UpdaterData data)
    {
        if (++_callsSinceIdle > MaxCallsPerEdit) return;
        // Once per edit: the probe's own schedule read would wake the updater again (Revit showed
        // its loop dialog during DW TIMING, 2026-10-21).
        if (_callsSinceIdle == 1) Probe?.Invoke(doc);
        var changed = data.GetModifiedElementIds().Concat(data.GetAddedElementIds()).Select(doc.GetElement).Where(e => e != null).ToList();
        LastTrigger = Triggers(doc, data);
        // Woken only by our own schedule reads: nothing new.
        if (data.GetDeletedElementIds().Count == 0 && changed.Count > 0
            && changed.All(e => e is ViewSchedule && RefreshedByUs.Contains(e.Id.Value))) return;
        List<DoorWindowPreset> sets = DwDefinitions.Load(doc).Where(p => p.Live).ToList();
        if (sets.Count == 0) return;
        // Only schedules changed: just the sets drawn from them.
        bool doorsOrWindows = data.GetDeletedElementIds().Count > 0
                              || changed.Any(e => e.Category?.Id.Value is (long)BuiltInCategory.OST_Doors or (long)BuiltInCategory.OST_Windows);
        bool materials = changed.Any(e => e is Material);
        // A changed schedule wakes only its own set: the one whose tag it carries; so does a frame
        // whose width was changed by hand.
        var schedules = new HashSet<string>(changed.Where(e => e is ViewSchedule || e is FamilyInstance { Category: not null } fi
                && fi.Category.Id.Value is not ((long)BuiltInCategory.OST_Doors or (long)BuiltInCategory.OST_Windows)).Select(DwTag.Get)
            .Where(t => t is { Role: DwRole.Schedule or DwRole.Frame }).Select(t => t!.Value.Set), StringComparer.Ordinal);
        foreach (DoorWindowPreset p in sets)
        {
            if (!doorsOrWindows && !materials && !schedules.Contains(p.SetKey)) continue;
            SyncCalls++;
            string? why = Sync(doc, p, write: true);
            if (why == null) continue;
            Enqueue(doc, p.SetKey);
            LastActivity[doc.Title] = $"'{p.Name}': rebuild queued ({why})";
        }
    }

    private static void Enqueue(Document doc, string set)
    {
        string key = Key(doc);
        if (!Queue.TryGetValue(key, out Pending? q)) Queue[key] = q = new Pending { Doc = doc };
        q.Doc = doc;
        q.Sets.Add(set);
        q.Since = DateTime.UtcNow;
    }

    /// <summary>A set's own elements: the cached ones while every one still carries the set's tag,
    /// else a fresh scan (after a build, an undo, a rolled-back test).</summary>
    private static List<(Element E, DwTagData T)> Elements(Document doc, string set)
    {
        if (!Tagged.TryGetValue(Key(doc), out var bySet)) Tagged[Key(doc)] = bySet = new Dictionary<string, List<ElementId>>(StringComparer.Ordinal);
        if (bySet.TryGetValue(set, out List<ElementId>? ids))
        {
            var cached = new List<(Element, DwTagData)>(ids.Count);
            foreach (ElementId id in ids)
            {
                if (doc.GetElement(id) is not { } e || DwTag.Get(e) is not { } t || t.Set != set) { cached = null; break; }
                cached.Add((e, t));
            }
            if (cached != null) return cached;
        }
        List<(Element E, DwTagData T)> all = DwTag.All(doc, set);
        bySet[set] = all.Select(x => x.E.Id).ToList();
        return all;
    }

    /// <summary>
    /// Brings set <paramref name="p"/> up to date in place, or says why it cannot be: null when its
    /// cells already match the model (after writing any values that changed, with
    /// <paramref name="write"/>), otherwise the reason a rebuild is needed. A set never built in
    /// this model (no legends) is left alone, and so is one whose schedule is gone (said in
    /// <see cref="LastActivity"/>; rebuilding would not bring it back).
    /// </summary>
    internal static string? Sync(Document doc, DoorWindowPreset p, bool write)
    {
        List<(Element E, DwTagData T)> tagged = Elements(doc, p.SetKey);
        if (!tagged.Any(t => t.T.Role == DwRole.Legend)) return null;
        if (DwSchedule.Find(doc, p) is not { } s)
        {
            LastActivity[doc.Title] = $"'{p.Name}': its schedule is gone; not updated (building again makes a new starter)";
            return null;
        }
        DwScheduleData schedule = DwSchedule.Read(s);
        if (_inUpdater) RefreshedByUs.Add(s.Id.Value);
        List<DwItem> items = DoorWindowItems.Collect(doc, p, new List<string>(), schedule);
        List<DwRowDef> defs = DoorWindowItems.Rows(p, schedule);
        // How much wider Revit draws the table font than GDI measures it, as the last build read it
        // (an updater cannot regenerate to measure it).
        double fit = tagged.Where(t => t.T.Role == DwRole.Legend).OrderBy(t => t.T.Index).Select(t => t.E).FirstOrDefault() is View first
            ? DwDefinitions.TextFit(first) : 1;

        var byKey = tagged.Where(t => t.T.Role is not (DwRole.Legend or DwRole.Sheet)).GroupBy(t => t.T.Key).ToDictionary(g => g.Key, g => g.ToList());
        var keys = new HashSet<string>(items.Select(i => i.Key), StringComparer.Ordinal);
        if (!keys.SetEquals(byKey.Keys)) return "the Type Marks changed";

        foreach (DwItem item in items)
        {
            var mine = byKey[item.Key];
            if (mine.Where(t => t.T.Role is DwRole.Elevation or DwRole.Plan)
                .Any(t => t.E.get_Parameter(BuiltInParameter.LEGEND_COMPONENT)?.AsElementId() != item.Primary.Id))
                return $"{item.Mark} is drawn from another type now";
            // Drawn from the back (legends built before 2026-10-26 were drawn from the front, and a
            // hand change to the front is put back).
            if (mine.Any(t => t.T.Role == DwRole.Elevation
                              && t.E.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW)?.AsInteger() != LegendBuilder.ViewElevation))
                return $"{item.Mark}'s elevation is not drawn from the back";
            // Drawn by a legend component, or as an outline when its size is an instance parameter.
            if (item.InstanceSized == mine.Any(t => t.T.Role is DwRole.Elevation or DwRole.Plan))
                return $"{item.Mark} is drawn {(item.InstanceSized ? "as an outline" : "by its family")} now";
            // The frame says how wide the cell is now; a width set by hand is remembered by a rebuild.
            (Element E, DwTagData T) frame = mine.FirstOrDefault(t => t.T.Role == DwRole.Frame);
            if (frame.E != null && LegendBuilder.HandSetWidths(doc, new List<(Element, DwTagData)> { frame }).Any())
                return $"{item.Mark}'s cell width was set by hand";
            double valueMm = DoorWindowLayout.ValueWidthMm(frame.E != null && LegendBuilder.FrameWidthMm(doc, frame.E) is double cellMm ? cellMm : p.CellWidthMm, p);
            if (item.Width > 0 && item.Height > 0)
            {
                // The dimensions measure the invisible lines laid on the type's edges, which do not
                // move by themselves: a type that changed size shows up as lines the wrong distance
                // apart. Read from the lines, not Dimension.Value — inside an updater the values are
                // not recalculated yet (a Type Comments edit read as "changed size", 2026-10-20).
                var lines = mine.Where(t => t.T.Role == DwRole.DimLine).Select(t => ((DetailCurve)t.E).GeometryCurve).OfType<Line>().ToList();
                var xs = lines.Where(l => Math.Abs(l.Direction.Y) > 0.999).Select(l => l.Origin.X).ToList();
                var ys = lines.Where(l => Math.Abs(l.Direction.X) > 0.999).Select(l => l.Origin.Y).ToList();
                const double tol = 1 / 304.8;
                bool sizeOk = xs.Count >= 2 && ys.Count >= 2
                              && Math.Abs(xs.Max() - xs.Min() - item.Width) < tol && Math.Abs(ys.Max() - ys.Min() - item.Height) < tol;
                if (sizeOk && mine.FirstOrDefault(t => t.T.Role == DwRole.Level).E is DetailCurve floor)
                    sizeOk = Math.Abs(ys.Min() - floor.GeometryCurve.GetEndPoint(0).Y - item.Sill) < tol;
                if (!sizeOk)
                {
                    double Mm(double ft) => Math.Round(ft * 304.8);
                    return $"{item.Mark} changed size (lines {xs.Count}/{ys.Count}: " +
                           (xs.Count >= 2 && ys.Count >= 2 ? $"{Mm(xs.Max() - xs.Min())} x {Mm(ys.Max() - ys.Min())}" : "-") +
                           $", type {Mm(item.Width)} x {Mm(item.Height)} sill {Mm(item.Sill)})";
                }
            }

            var rows = mine.Where(t => t.T.Role == DwRole.Row).OrderBy(t => t.T.Index).Select(t => t.E).ToList();
            if (rows.Count != defs.Count) return "the table rows changed";
            for (int i = 0; i < rows.Count; i++)
            {
                (string value, int lines) = DwWrap.Wrap(DoorWindowItems.Value(item, defs[i], p, schedule), p.RowFont, p.RowTextMm, valueMm, fit);
                // The last row may be taller than its text: it takes up what the row of cells has
                // spare beside a taller neighbour (LegendBuilder), so only "too short" counts there.
                int has = rows[i].LookupParameter("Lines")?.AsInteger() ?? 1;
                bool last = i == rows.Count - 1;
                if (last ? has < lines : has != lines) return $"{item.Mark}: '{defs[i].Label}' now takes {lines} line(s)";
                if (write)
                {
                    SetText(rows[i], "KH_DW Value", value);
                    SetText(rows[i], "KH_DW Label", defs[i].Label);
                }
            }
            if (write)
            {
                foreach (var (e, t) in mine)
                {
                    if (t.Role == DwRole.Mark) SetText(e, "KH_DW Label", item.Mark);
                    else if (t.Role == DwRole.Title1) SetText(e, "KH_DW Label", p.ElevationFirst ? p.ElevationTitle : p.PlanTitle);
                    else if (t.Role == DwRole.Title2) SetText(e, "KH_DW Label", p.ElevationFirst ? p.PlanTitle : p.ElevationTitle);
                    else if (t.Role == DwRole.Description) SetText(e, "KH_DW Label", DoorWindowItems.Description(item, p, schedule));
                }
            }
        }
        return null;
    }

    private static void SetText(Element e, string name, string value)
    {
        Parameter? prm = e.LookupParameter(name);
        if (prm != null && !prm.IsReadOnly && prm.AsString() != value) prm.Set(value);
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        EditOver();
        if (_flushing || Queue.Count == 0) return;
        foreach (string key in Queue.Keys.ToList())
        {
            Pending q = Queue[key];
            if (DateTime.UtcNow - q.Since < FlushDelay) continue;
            Queue.Remove(key);
            Document doc = q.Doc;
            if (!doc.IsValidObject || doc.IsReadOnly || doc.IsModifiable) continue;
            _flushing = true;
            try { Flush(doc, q.Sets); }
            catch (Exception ex) { LastActivity[doc.Title] = "rebuild failed: " + ex.Message; }
            finally { _flushing = false; }
        }
    }

    /// <summary>Rebuilds the queued sets of one model (each as its own undo step).</summary>
    internal static void Flush(Document doc, IEnumerable<string> sets)
    {
        List<DoorWindowPreset> defs = DwDefinitions.Load(doc);
        foreach (string set in sets)
        {
            DoorWindowPreset? p = defs.FirstOrDefault(d => d.SetKey == set);
            if (p == null) continue;
            var rows = LegendBuilder.Run(doc, p);
            int failed = rows.Count(r => r["Status"] == "Failed");
            LastActivity[doc.Title] = $"'{p.Name}' rebuilt" + (failed > 0 ? $", {failed} failed: " + rows.First(r => r["Status"] == "Failed")["Note"] : "");
        }
    }

    /// <summary>
    /// Apply now: every set in the model brought up to date, live or not — values written in place
    /// (one undo step), then each set that needs it rebuilt (one more each). Outside a transaction.
    /// Says what it did.
    /// </summary>
    internal static string ApplyNow(Document doc)
    {
        List<DoorWindowPreset> sets = DwDefinitions.Load(doc);
        if (sets.Count == 0) return "No door & window legends in this model yet: build them first.";
        var rebuild = new List<(DoorWindowPreset P, string Why)>();
        using (var t = new Transaction(doc, "Update door & window legends"))
        {
            t.Start();
            foreach (DoorWindowPreset p in sets)
                if (Sync(doc, p, write: true) is { } why) rebuild.Add((p, why));
            t.Commit();
        }
        var said = new List<string>();
        foreach (var (p, why) in rebuild)
        {
            var rows = LegendBuilder.Run(doc, p);
            int failed = rows.Count(r => r["Status"] == "Failed");
            said.Add($"'{p.Name}' rebuilt ({why})" + (failed > 0 ? $", {failed} failed" : ""));
        }
        ClearQueue(doc);
        int inPlace = sets.Count - rebuild.Count;
        if (inPlace > 0) said.Insert(0, $"{inPlace} set(s) updated in place");
        return string.Join("; ", said) + ".";
    }

    /// <summary>Turns a built set's live update on or off in the model (its saved definition).
    /// Outside a transaction. False when the set is not built in this model.</summary>
    internal static bool SetLive(Document doc, DoorWindowPreset p, bool live)
    {
        List<(Element E, DwTagData T)> tagged = Elements(doc, p.SetKey);
        if (tagged.Where(t => t.T.Role == DwRole.Legend).OrderBy(t => t.T.Index).Select(t => t.E).FirstOrDefault() is not View first) return false;
        DoorWindowPreset saved = DwDefinitions.Load(doc).FirstOrDefault(d => d.SetKey == p.SetKey) ?? p;
        saved.Live = live;
        using (var t = new Transaction(doc, live ? "Door & window legends: live on" : "Door & window legends: live off"))
        {
            t.Start();
            DwDefinitions.Save(first, saved);
            t.Commit();
        }
        Watch(doc);
        return true;
    }

    /// <summary>Forgets what is queued for a model (the self-test, whose rolled-back scenarios
    /// leave queue entries behind for changes that no longer exist).</summary>
    internal static void ClearQueue(Document doc) => Queue.Remove(Key(doc));

    internal static int QueuedCount(Document doc) => Queue.TryGetValue(Key(doc), out Pending? p) ? p.Sets.Count : 0;
}

/// <summary>The Dynamic Model Update hook. All logic lives in <see cref="LiveDoorWindow"/>.</summary>
internal sealed class DoorWindowUpdater : IUpdater
{
    private static readonly Guid UpdaterGuid = new("8c41f2d7-6a3e-4b95-a0d2-7e9f1b3c5a68");
    private readonly UpdaterId _id;

    public DoorWindowUpdater(AddInId addIn) { _id = new UpdaterId(addIn, UpdaterGuid); }

    public void Execute(UpdaterData data)
    {
        // Never throw out of an updater: Revit would disable it and tell the user.
        try { LiveDoorWindow.Execute(data); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("Enlarge Door & Window updater: " + ex); }
    }

    public UpdaterId GetUpdaterId() => _id;
    public ChangePriority GetChangePriority() => ChangePriority.Annotations;
    public string GetUpdaterName() => "DRW Helper Enlarge Door & Window";
    public string GetAdditionalInformation() =>
        "Keeps door & window schedule legends current as doors and windows change. Optional: models open " +
        "normally without it, and Apply now in Enlarge Door & Window catches up anything it missed.";
}
