#if EDITION_DRW
using System.Diagnostics;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.DoorWindow;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW TIMING", DW_DEV=1, 2026-10-21): where Enlarge Door &amp; Window spends its
/// time on a real model — the window's open, a build, live edits — and whether a Revit schedule's
/// cells can be read inside a live edit (the user chose a schedule as the source of the table rows).
/// Reports only; nothing here is a pass/fail rule yet.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void DwTiming(Document doc)
    {
        var sw = Stopwatch.StartNew();
        string Lap() { string s = $"{sw.Elapsed.TotalSeconds:0.00} s"; sw.Restart(); return s; }

        // ---- the window's open, as DrwEnlargeDoorWindowCommand does it
        var items = DoorWindowItems.Collect(doc, new DoorWindowPreset { IncludeUnmarked = true }, new List<string>());
        Info($"open: Collect, {items.Count} items / {items.Sum(i => i.Instances.Count)} instances: {Lap()}");
        int schedules = DwSchedule.All(doc).Count;
        Info($"open: {schedules} door/window schedule names: {Lap()}");
        if (LegendBuilder.Source(doc) is { } probeSource)
        {
            // What one legend component costs, step by step.
            using var t = new Transaction(doc, "component steps");
            t.Start();
            var ids = new List<ElementId> { probeSource.Comp.Id };
            ElementId a = ElementTransformUtils.CopyElements(probeSource.Legend, ids, probeSource.Legend, Transform.Identity, new CopyPasteOptions()).First();
            string viewCopy = Lap();
            ElementId b = ElementTransformUtils.CopyElement(doc, a, new XYZ(1, 0, 0)).First();
            string inView = Lap();
            ICollection<ElementId> eight = ElementTransformUtils.CopyElements(doc, Enumerable.Repeat(b, 1).Concat(new[] { a }).ToList(), new XYZ(0, 1, 0));
            string twoAtOnce = Lap();
            doc.GetElement(b).get_Parameter(BuiltInParameter.LEGEND_COMPONENT).Set(items[items.Count - 1].Primary.Id);
            string setType = Lap();
            doc.Regenerate();
            string regen = Lap();
            t.RollBack();
            Info($"component: view-to-view copy {viewCopy}, in-view copy {inView}, two in one call {twoAtOnce} ({eight.Count}), set type {setType}, regenerate {regen}; rollback {Lap()}");
        }
        if (LegendBuilder.Source(doc) is { } source)
        {
            using var t = new Transaction(doc, "plan widths");
            t.Start();
            LegendBuilder.Extents(doc, source.Legend, source.Comp, items, new DoorWindowPreset(), out _);
            t.RollBack();
            Info($"open: Extents (plan + elevation): {Lap()}");
        }
        int titleBlocks = DoorWindowSheets.TitleBlocks(doc).Count;
        var read = DoorWindowSheets.Read(doc);
        Info($"open: title blocks, {read.Count} of {titleBlocks}: {Lap()}");

        // ---- a door slid 10 mm along its wall, before any schedule exists: what Revit itself takes.
        // (Moving a whole Snowdon core wall took 42 s, then more than 5 minutes: wall joins, not us.)
        FamilyInstance? door = items.SelectMany(i => i.Instances).FirstOrDefault(i => i.Host is Wall { Location: LocationCurve { Curve: Line } });
        XYZ? nudge = door?.Host is Wall { Location: LocationCurve { Curve: Line wl } } ? wl.Direction * (10 / 304.8) : null;
        if (door != null && nudge != null)
        {
            Commit(doc, "move door", () => ElementTransformUtils.MoveElement(doc, door.Id, nudge));
            Info($"baseline: a door moved 10 mm along its wall, no schedule yet: {Lap()}");
        }

        // ---- a build, then live edits
        DoorWindowPreset p = DwPreset(doc);
        LegendBuilder.Run(doc, p);
        Info($"build: {Lap()}");
        LiveDoorWindow.ClearQueue(doc);
        DwItem item = DoorWindowItems.Collect(doc, p, new List<string>())
            .First(i => i.IsDoor && !i.Primary.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS).IsReadOnly);
        Commit(doc, "comment", () => item.Primary.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS).Set("timing"));
        Info($"live: a Type Comments edit: {Lap()} (updater {LiveDoorWindow.LastExecuteMs:0} ms)");
        if (door != null && nudge != null)
        {
            int calls = LiveDoorWindow.ExecuteCalls;
            Commit(doc, "move door", () => ElementTransformUtils.MoveElement(doc, door.Id, nudge));
            Info($"live: the same door moved again: {Lap()} (updater called {LiveDoorWindow.ExecuteCalls - calls}x)");
        }
        LiveDoorWindow.ClearQueue(doc);

        DwScheduleProbe(doc, item);
    }

    private static void Commit(Document doc, string name, Action edit)
    {
        using var t = new Transaction(doc, name);
        t.Start();
        edit();
        t.Commit();
    }

    /// <summary>
    /// A door schedule, one row per Type Mark: Type Mark, Type Comments, Count and a combined
    /// Width x Height. Its cells read before a commit and inside the live updater.
    /// </summary>
    private static void DwScheduleProbe(Document doc, DwItem item)
    {
        ViewSchedule s = null!;
        int markCol = -1;
        Commit(doc, "probe schedule", () =>
        {
            s = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Doors));
            ScheduleDefinition d = s.Definition;
            d.IsItemized = false;
            IList<SchedulableField> all = d.GetSchedulableFields();
            SchedulableField? Field(BuiltInParameter b) => all.FirstOrDefault(f => f.ParameterId.Value == (long)b);
            ScheduleField mark = d.AddField(Field(BuiltInParameter.ALL_MODEL_TYPE_MARK)!);
            d.AddField(Field(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)!);
            d.AddField(ScheduleFieldType.Count);
            d.AddSortGroupField(new ScheduleSortGroupField(mark.FieldId));
            Info("schedulable Width/Height: " + string.Join(", ", all.Where(f => f.GetName(doc) is "Width" or "Height")
                .Select(f => $"{f.GetName(doc)} {f.FieldType} {f.ParameterId.Value}")));
            var parts = all.Where(f => f.FieldType == ScheduleFieldType.ElementType && f.GetName(doc) is "Width" or "Height")
                .OrderByDescending(f => f.GetName(doc)).Select((f, k) =>
                {
                    TableCellCombinedParameterData c = TableCellCombinedParameterData.Create();
                    c.ParamId = f.ParameterId;
                    c.CategoryId = new ElementId(BuiltInCategory.OST_Doors);
                    c.Separator = k == 0 ? " x " : "";
                    return c;
                }).ToList();
            try { d.InsertCombinedParameterField(parts, "Size", d.GetFieldCount()); Info("combined Size field added"); }
            catch (Exception ex) { Info("combined field not added: " + ex.Message); }
        });
        string Dump(int max)
        {
            TableSectionData body = s.GetTableData().GetSectionData(SectionType.Body);
            return $"{body.NumberOfRows}x{body.NumberOfColumns}: " + string.Join(" | ", Enumerable.Range(0, Math.Min(max, body.NumberOfRows))
                .Select(r => string.Join(";", Enumerable.Range(0, body.NumberOfColumns).Select(c => s.GetCellText(SectionType.Body, r, c)))));
        }
        string Cell(int col)
        {
            TableSectionData body = s.GetTableData().GetSectionData(SectionType.Body);
            for (int r = 0; r < body.NumberOfRows; r++)
                if (s.GetCellText(SectionType.Body, r, markCol) == item.Mark) return s.GetCellText(SectionType.Body, r, col);
            return "(row not found)";
        }
        var sw = Stopwatch.StartNew();
        bool fresh = s.RefreshData();
        Info($"schedule: RefreshData {fresh} in {sw.ElapsedMilliseconds} ms; {Dump(4)}");
        markCol = 0;
        Info($"schedule: {s.GetScheduleInstances(-1).Count} instances let through; {item.Mark} reads '{Cell(1)}' / count {Cell(2)} / size '{Cell(3)}'");

        // Before a commit: edited, not regenerated, then regenerated.
        using (var t = new Transaction(doc, "probe edit"))
        {
            t.Start();
            item.Primary.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS).Set("PROBE-A");
            s.RefreshData();
            Info($"in a transaction, no regenerate: '{Cell(1)}' (out of date {s.IsDataOutOfDate()})");
            doc.Regenerate();
            s.RefreshData();
            Info($"in a transaction, regenerated: '{Cell(1)}'");
            t.RollBack();
        }

        // Inside the live updater.
        string seen = "";
        LiveDoorWindow.Probe = _ =>
        {
            LiveDoorWindow.Probe = null;   // once: its own refresh wakes the updater again
            try { bool ok = s.RefreshData(); seen = $"refresh {ok}, comment '{Cell(1)}', size '{Cell(3)}'"; }
            catch (Exception ex) { seen = "threw " + ex.GetType().Name + ": " + ex.Message; }
        };
        try
        {
            Parameter? pw = item.Primary.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? item.Primary.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
            Commit(doc, "probe in updater", () =>
            {
                item.Primary.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS).Set("PROBE-B");
                if (pw is { IsReadOnly: false }) pw.Set(pw.AsDouble() + 100 / 304.8);
            });
            Info($"inside the updater (comment PROBE-B, width +100): {seen}");
            s.RefreshData();
            Info($"after the commit: comment '{Cell(1)}', size '{Cell(3)}'");
        }
        finally { LiveDoorWindow.Probe = null; LiveDoorWindow.ClearQueue(doc); }

        // A hidden field: still a column in the body?
        Commit(doc, "hide mark", () => s.Definition.GetField(0).IsHidden = true);
        s.RefreshData();
        Info($"Type Mark hidden: {Dump(2)}");
    }
}
#endif
