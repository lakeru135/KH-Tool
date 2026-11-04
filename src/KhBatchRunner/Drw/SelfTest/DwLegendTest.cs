#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.DoorWindow;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>Enlarge Door &amp; Window's legend builder, checked by reading the model back.</summary>
internal static partial class DrwSelfTest
{
    /// <summary>
    /// A set drawn from its own starter schedule (made here by <see cref="DwSchedule.Ensure"/>) plus
    /// the instance Mark. Without <paramref name="materials"/> the starter's material fields are
    /// taken out again, so the rows are ขนาด | หมายเหตุ | Mark | Qty., Description in the header.
    /// </summary>
    private static DoorWindowPreset DwPreset(Document doc, bool door = true, bool materials = false)
    {
        var p = new DoorWindowPreset
        {
            Name = "SelfTest DW", Category = door ? DwCategory.Door : DwCategory.Window,
            LegendName = (door ? "Door" : "Window") + " Schedule {n}"
        };
        using (var t = new Transaction(doc, "self-test schedule"))
        {
            t.Start();
            ViewSchedule s = DwSchedule.Ensure(doc, p, new List<string>());
            ScheduleDefinition d = s.Definition;
            if (!materials)
            {
                doc.Regenerate();
                DwScheduleData read = DwSchedule.Read(s);
                var drop = new HashSet<string>(read.MaterialColumns.Select(c => read.Headings[c]));
                foreach (ScheduleFieldId id in d.GetFieldOrder().Where(id => drop.Contains(d.GetField(id).ColumnHeading)).ToList())
                    d.RemoveField(id);
            }
            // Mark before the Count field (the Qty row stays last).
            if (!d.GetFieldOrder().Any(id => d.GetField(id).ParameterId.Value == (long)BuiltInParameter.ALL_MODEL_MARK))
            {
                int at = d.GetFieldOrder().Select(d.GetField).ToList().FindIndex(f => f.FieldType == ScheduleFieldType.Count);
                d.InsertField(d.GetSchedulableFields().First(f => f.ParameterId.Value == (long)BuiltInParameter.ALL_MODEL_MARK
                                                                && f.FieldType == ScheduleFieldType.Instance), at < 0 ? d.GetFieldCount() : at).ColumnHeading = "Mark";
            }
            t.Commit();
        }
        return p;
    }

    private static DwScheduleData DwRead(Document doc, DoorWindowPreset p) => DwSchedule.Read(DwSchedule.Find(doc, p)!);

    private static void DwLegendBuild(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc);
        var warnings = new List<string>();
        DwScheduleData schedule = DwRead(doc, p);
        Check("the starter schedule reads one row per Type Mark", schedule.Problems.Count == 0, string.Join(" ", schedule.Problems));
        Info($"schedule '{schedule.Name}': {string.Join(" | ", schedule.Headings)}; {schedule.ByMark.Count} marks, " +
             $"{schedule.Instances?.Count.ToString() ?? "all"} instances let through");
        List<DwItem> items = DoorWindowItems.Collect(doc, p, warnings, schedule);
        List<DwRowDef> defs = DoorWindowItems.Rows(p, schedule);
        Check("rows: Size, the schedule's fields but Type Mark and Description, Qty",
            string.Join("|", defs.Select(r => r.Label)) == "ขนาด|หมายเหตุ|Mark|Qty.", string.Join("|", defs.Select(r => r.Label)));
        Check("only doors", items.All(i => i.IsDoor));
        Info($"{items.Count} items: " + string.Join(", ", items.Take(20).Select(i => $"{i.Mark}({i.Instances.Count})")));
        foreach (string w in warnings.Take(5)) Info("warning: " + w);
        Check("the model has door/window items to schedule", items.Count > 0);
        if (items.Count == 0) return;

        var rows = LegendBuilder.Run(doc, p);
        foreach (var r in rows.Where(r => r["Status"] != "Created").Take(15)) Info($"row {r["Status"]}: {r["Legend"]} {r["Item"]} {r["Note"]}");
        Check("no Failed rows", rows.All(r => r["Status"] != "Failed"), string.Join("; ", rows.Where(r => r["Status"] == "Failed").Select(r => r["Item"] + " " + r["Note"])));
        Info($"{rows.Count(r => r["Note"].StartsWith("Nominal edges"))} items dimensioned to their outline (edges not found)");

        var tagged = DwTag.All(doc, p.SetKey);
        int perLegend = p.Columns * p.RowsPerLegend;
        int legends = tagged.Count(t => t.T.Role == DwRole.Legend);
        Check("at most " + perLegend + " items per legend", legends >= (items.Count + perLegend - 1) / perLegend, $"{legends} legends");
        Check("one frame per item", tagged.Count(t => t.T.Role == DwRole.Frame) == items.Count);
        // An instance-sized type is drawn as an outline instead (2026-10-22).
        int drawnByFamily = items.Count(i => !i.InstanceSized);
        Info($"{items.Count - drawnByFamily} item(s) instance-sized, drawn as outlines");
        Check("an elevation and a plan component per item drawn by its family",
            tagged.Count(t => t.T.Role == DwRole.Elevation) == drawnByFamily && tagged.Count(t => t.T.Role == DwRole.Plan) == drawnByFamily);

        // Each item: its components show its type, its rows hold its values.
        int badType = 0, badRow = 0, badDim = 0;
        foreach (DwItem item in items)
        {
            var mine = tagged.Where(t => t.T.Key == item.Key).ToList();
            foreach (var comp in mine.Where(t => t.T.Role is DwRole.Elevation or DwRole.Plan))
                if (comp.E.get_Parameter(BuiltInParameter.LEGEND_COMPONENT).AsElementId() != item.Primary.Id) badType++;
            var rowEls = mine.Where(t => t.T.Role == DwRole.Row).OrderBy(t => t.T.Index).ToList();
            // Read independently of the schedule: the type's own Type Comments, the instance count.
            string comments = item.Primary.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString() ?? "";
            var want = new[] { DoorWindowItems.Value(item, defs[0], p, schedule), comments.Trim().Length > 0 ? comments : p.Blank, null, item.Instances.Count.ToString() };
            for (int i = 0; i < defs.Count; i++)
            {
                string? got = i < rowEls.Count ? rowEls[i].E.LookupParameter("KH_DW Value")?.AsString() : null;
                string? label = i < rowEls.Count ? rowEls[i].E.LookupParameter("KH_DW Label")?.AsString() : null;
                if (label != defs[i].Label || want[i] != null && got != want[i]) { badRow++; if (badRow <= 3) Info($"{item.Mark} row {i}: '{label}' '{got}' != '{defs[i].Label}' '{want[i]}'"); }
            }
            if (item.Width <= 0 || item.Height <= 0) continue;   // no size: warned, not dimensioned
            var dims = mine.Where(t => t.T.Role == DwRole.Dimension).Select(t => ((Dimension)t.E).Value ?? 0).ToList();
            bool w = dims.Any(v => Math.Abs(v - item.Width) < 1 / 304.8), h = dims.Any(v => Math.Abs(v - item.Height) < 1 / 304.8);
            if (!w || !h) { badDim++; if (badDim <= 3) Info($"{item.Mark}: dims {string.Join("/", dims.Select(SpMm))} vs {SpMm(item.Width)} x {SpMm(item.Height)}"); }
        }
        Check("every component shows its item's type", badType == 0, $"{badType} wrong");
        Check("every row holds its value (Qty = instances)", badRow == 0, $"{badRow} wrong");
        Check("every sized item's dimensions read its width and height", badDim == 0, $"{badDim} items off");

        DwExport(doc, tagged.Where(t => t.T.Role == DwRole.Legend).Select(t => (View)t.E).First(), "dw-legend-1");

        // Rerun after edits: a type 100 mm wider, a dragged Mark label.
        DwItem first = items[0];
        Parameter? pw = first.Primary.get_Parameter(first.IsDoor ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH)
                        ?? first.Primary.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
        var mark = tagged.First(t => t.T.Key == first.Key && t.T.Role == DwRole.Mark).E;
        var frame = tagged.First(t => t.T.Key == first.Key && t.T.Role == DwRole.Frame).E;
        double k = p.Scale / 304.8;
        using (var t = new Transaction(doc, "edits"))
        {
            t.Start();
            if (pw != null && !pw.IsReadOnly) pw.Set(pw.AsDouble() + 100 / 304.8);
            ElementTransformUtils.MoveElement(doc, mark.Id, new XYZ(5 * k, 2 * k, 0));
            t.Commit();
        }
        XYZ movedOffset = ((LocationPoint)mark.Location).Point - ((LocationPoint)frame.Location).Point;
        int before = tagged.Count;

        rows = LegendBuilder.Run(doc, p);
        Check("rerun: no Failed rows", rows.All(r => r["Status"] != "Failed"));
        var again = DwTag.All(doc, p.SetKey);
        Check("rerun: the same number of elements, nothing doubled", again.Count == before, $"{before} -> {again.Count}");
        Check("rerun: the legends are reused", again.Count(t => t.T.Role == DwRole.Legend) == legends);
        var items2 = DoorWindowItems.Collect(doc, p, new List<string>());
        DwItem first2 = items2.First(i => i.Key == first.Key);
        var dims2 = again.Where(t => t.T.Key == first.Key && t.T.Role == DwRole.Dimension).Select(t => ((Dimension)t.E).Value ?? 0).ToList();
        if (pw != null && !pw.IsReadOnly)
            Check("rerun: the wider type's dimension reads its new width", dims2.Any(v => Math.Abs(v - first2.Width) < 1 / 304.8),
                $"{string.Join("/", dims2.Select(SpMm))} vs {SpMm(first2.Width)}");
        var mark2 = again.First(t => t.T.Key == first.Key && t.T.Role == DwRole.Mark).E;
        var frame2 = again.First(t => t.T.Key == first.Key && t.T.Role == DwRole.Frame).E;
        XYZ offset2 = ((LocationPoint)mark2.Location).Point - ((LocationPoint)frame2.Location).Point;
        Check("rerun: the dragged Mark label keeps its offset", offset2.DistanceTo(movedOffset) < 1e-6,
            $"{SpMm(movedOffset.X)},{SpMm(movedOffset.Y)} -> {SpMm(offset2.X)},{SpMm(offset2.Y)}");
    }

    /// <summary>The live updater, as Revit runs it: commits that change doors/windows, then the queue.</summary>
    private static void DwLegendLive(Document doc)
    {
        Check("the live updater is registered in this session", LiveDoorWindow.LastActivity != null &&
            UpdaterRegistry.GetRegisteredUpdaterInfos(doc).Any(u => u.UpdaterName == "DRW Helper Enlarge Door & Window"));
        LiveDoorWindow.ClearQueue(doc);   // earlier scenarios' edits, rolled back since
        DoorWindowPreset p = DwPreset(doc);
        var rows = LegendBuilder.Run(doc, p);
        Check("built", rows.All(r => r["Status"] != "Failed"));
        Check("a fresh build is in sync", LiveDoorWindow.Sync(doc, p, write: false) == null);
        Check("the definition is kept in the model", DwDefinitions.Load(doc).Any(d => d.SetKey == p.SetKey));

        var items = DoorWindowItems.Collect(doc, p, new List<string>());
        DwItem item = items.First(i => i.Width > 0 && !i.Primary.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS).IsReadOnly
                                       && i.Instances.Any(x => x.Host is Wall));
        Element Row(int index) => DwTag.All(doc, p.SetKey).First(t => t.T.Key == item.Key && t.T.Role == DwRole.Row && t.T.Index == index).E;
        string Value(int index) => Row(index).LookupParameter("KH_DW Value")?.AsString() ?? "";
        string before = Value(1);
        void Edit(string name, Action edit)
        {
            using var t = new Transaction(doc, name);
            t.Start();
            edit();
            t.Commit();
        }

        // A value edit: read from the schedule inside the updater, written in the same transaction.
        Edit("Type Comments edit", () => item.Primary.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS).Set("กระจกใส 6 มม."));
        Check("a Type Comments edit reaches its row in the same transaction", Value(1) == "กระจกใส 6 มม.", $"'{before}' -> '{Value(1)}'");
        Check("nothing queued for a value edit", LiveDoorWindow.QueuedCount(doc) == 0);
        Info("last activity: " + (LiveDoorWindow.LastActivity.TryGetValue(doc.Title, out string? a1) ? a1 : "-"));

        // The row changed inside the edit's own transaction (checked just above, straight after its
        // Commit), which is what makes one Undo take back both. Undo itself has no API: checked by hand.

        // An instance field the schedule shows (Mark): watched for this model since the build. On a
        // Type Mark placed once, so the schedule's row shows that one value (Marks must differ).
        FamilyInstance one = item.Instances[0];
        if (items.FirstOrDefault(i => i.Instances.Count == 1) is { } single)
        {
            string MarkRow() => DwTag.All(doc, p.SetKey).First(t => t.T.Key == single.Key && t.T.Role == DwRole.Row && t.T.Index == 2)
                .E.LookupParameter("KH_DW Value")?.AsString() ?? "";
            Edit("Mark edit", () => single.Instances[0].get_Parameter(BuiltInParameter.ALL_MODEL_MARK).Set("SELFTEST-M"));
            Check("an instance Mark edit reaches the Mark row", MarkRow() == "SELFTEST-M", $"{single.Mark}: '{MarkRow()}'");
        }
        else Info("no Type Mark placed once; the instance field path is skipped");

        // A heading renamed in the schedule: the label follows in place.
        ViewSchedule sched = DwSchedule.Find(doc, p)!;
        ScheduleField commentsField = sched.Definition.GetFieldOrder().Select(sched.Definition.GetField)
            .First(f => f.ParameterId.Value == (long)BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
        Edit("heading", () => commentsField.ColumnHeading = "Remarks");
        Check("a heading renamed in the schedule renames the row's label", Row(1).LookupParameter("KH_DW Label")?.AsString() == "Remarks",
            Row(1).LookupParameter("KH_DW Label")?.AsString() ?? "");
        Check("nothing queued for a heading", LiveDoorWindow.QueuedCount(doc) == 0);

        // A door slid 10 mm along its wall: not a change any row shows, so the updater is not woken.
        // (Moving the wall itself did the same, but a Snowdon core wall took minutes to rejoin.)
        if (one.Host is Wall { Location: LocationCurve { Curve: Line wl } })
        {
            int calls = LiveDoorWindow.ExecuteCalls, syncs = LiveDoorWindow.SyncCalls;
            Edit("move door", () => ElementTransformUtils.MoveElement(doc, one.Id, wl.Direction * (10 / 304.8)));
            Info($"door moved: {LiveDoorWindow.ExecuteCalls - calls} updater call(s), last {LiveDoorWindow.LastExecuteMs:0.0} ms, woken by: {LiveDoorWindow.LastTrigger}");
            Check("a door moved along its wall updates nothing", LiveDoorWindow.SyncCalls == syncs, $"{LiveDoorWindow.SyncCalls - syncs} set(s) synced");
        }

        // Live off: an edit is left alone; Apply now catches up.
        Check("live off is saved in the model", LiveDoorWindow.SetLive(doc, p, false) && !DwDefinitions.Load(doc).First(d => d.SetKey == p.SetKey).Live);
        Edit("Type Comments while off", () => item.Primary.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS).Set("live off"));
        Check("with live off the row is left alone", Value(1) == "กระจกใส 6 มม.", Value(1));
        string applied = LiveDoorWindow.ApplyNow(doc);
        Info("apply now: " + applied);
        Check("Apply now brings it up to date", Value(1) == "live off", Value(1));
        LiveDoorWindow.SetLive(doc, p, true);
        p = DwDefinitions.Load(doc).First(d => d.SetKey == p.SetKey);

        // A size edit: the cell cannot absorb it in place, so a rebuild is queued.
        Parameter? pw = item.Primary.get_Parameter(item.IsDoor ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH)
                        ?? item.Primary.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
        if (pw == null || pw.IsReadOnly) { Info("width not writable; size path skipped"); return; }
        Edit("width edit", () => pw.Set(pw.AsDouble() + 100 / 304.8));
        Check("a size edit queues a rebuild", LiveDoorWindow.QueuedCount(doc) == 1,
            LiveDoorWindow.LastActivity.TryGetValue(doc.Title, out string? a2) ? a2 : "-");
        LiveDoorWindow.Flush(doc, new[] { p.SetKey });
        DwItem wider = DoorWindowItems.Collect(doc, p, new List<string>()).First(i => i.Key == item.Key);
        var dims = DwTag.All(doc, p.SetKey).Where(t => t.T.Key == item.Key && t.T.Role == DwRole.Dimension).Select(t => ((Dimension)t.E).Value ?? 0).ToList();
        Check("after the rebuild the dimension reads the new width", dims.Any(v => Math.Abs(v - wider.Width) < 1 / 304.8),
            $"{string.Join("/", dims.Select(SpMm))} vs {SpMm(wider.Width)}");
        Check("and the schedule is in sync again", LiveDoorWindow.Sync(doc, p, write: false) == null);

        // A field added to the schedule: a new row, so the set is rebuilt.
        LiveDoorWindow.ClearQueue(doc);
        Edit("add field", () => sched.Definition.AddField(sched.Definition.GetSchedulableFields()
            .First(f => f.ParameterId.Value == (long)BuiltInParameter.ALL_MODEL_MODEL && f.FieldType == ScheduleFieldType.ElementType)));
        Check("a field added to the schedule queues a rebuild", LiveDoorWindow.QueuedCount(doc) == 1,
            LiveDoorWindow.LastActivity.TryGetValue(doc.Title, out string? a3) ? a3 : "-");
        LiveDoorWindow.Flush(doc, new[] { p.SetKey });
        Check("after it the cells have one row more", DwTag.All(doc, p.SetKey).Count(t => t.T.Key == item.Key && t.T.Role == DwRole.Row) == 5);
        LiveDoorWindow.ClearQueue(doc);
    }

    /// <summary>
    /// The set's own starter (2026-10-21): made and tagged once, found again after a rename; its
    /// material fields show the material's Description, and a Description edited reaches the row in
    /// the same transaction. Snowdon's own Door Schedule is read too, for the record (the user picked
    /// it, Fixed it, and could still not build).
    /// </summary>
    private static void DwStarter(Document doc)
    {
        if (DwSchedule.All(doc, true).FirstOrDefault(s => s.Name == "Door Schedule") is { } theirs)
        {
            DwScheduleData read = DwSchedule.Read(theirs);
            Info($"Snowdon 'Door Schedule': blocked {read.Blocked}; {read.ByMark.Count} marks; problems: {string.Join(" ", read.Problems)}; " +
                 $"fields: {string.Join(" | ", read.Headings)}");
        }

        // Snowdon's door types have no material-typed parameter (its "Material" columns are text):
        // one is bound here, a type parameter on doors, and set on every placed door type.
        DwBindFrameMaterial(doc);

        var p = new DoorWindowPreset { Name = "SelfTest starter", Category = DwCategory.Door, LegendName = "Starter Door {n}" };
        var did = new List<string>();
        ViewSchedule s1, s2;
        using (var t = new Transaction(doc, "ensure"))
        {
            t.Start();
            s1 = DwSchedule.Ensure(doc, p, did);
            t.Commit();
        }
        Info("made: " + string.Join("; ", did));
        Check("Ensure makes the set's starter, tagged as the set's", did.Any(d => d.StartsWith("made")) && DwTag.Get(s1) is { Role: DwRole.Schedule } t1 && t1.Set == p.SetKey);
        did.Clear();
        using (var t = new Transaction(doc, "ensure again"))
        {
            t.Start();
            s1.Name = "Renamed by the user";
            s2 = DwSchedule.Ensure(doc, p, did);
            t.Commit();
        }
        Check("a second Ensure, after a rename, finds the same schedule and changes nothing", s2.Id == s1.Id && did.Count == 0, string.Join("; ", did));

        DwScheduleData data = DwSchedule.Read(s2);
        Info($"starter fields: {string.Join(" | ", data.Headings)}; material columns: {string.Join(", ", data.MaterialColumns.Select(c => data.Headings[c]))}; " +
             $"{data.Materials.Count} material(s); properties: {string.Join(", ", data.MaterialProperties)}");
        Check("the starter reads one row per Type Mark", data.Problems.Count == 0 && !data.Blocked, string.Join(" ", data.Problems));
        if (data.MaterialColumns.Count == 0) { Info("no door type here has a material parameter: the material path is skipped"); return; }
        Check("the starter's material fields are read as material columns", data.MaterialColumns.Count > 0);
        Check("and each mark's material is read", data.MaterialOf.Count > 0 && data.Materials.Count > 0,
            $"{data.MaterialOf.Count} marks, {data.Materials.Count} materials");
        if (data.MaterialOf.Count == 0) return;

        // A material row: the material's Description, and an edit to it reaches the row inline.
        int col = data.MaterialColumns.First();
        string heading = data.Headings[col];
        string mark = data.MaterialOf.First(kv => kv.Value.ContainsKey(col)).Key;
        var material = (Material)doc.GetElement(new ElementId(data.MaterialOf[mark][col]));
        Parameter description = material.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);
        using (var t = new Transaction(doc, "material description"))
        {
            t.Start();
            description.Set("SELFTEST วัสดุ 1");
            t.Commit();
        }
        var rows = LegendBuilder.Run(doc, p);
        Check("built from the starter", rows.All(r => r["Status"] != "Failed"), string.Join("; ", rows.Where(r => r["Status"] == "Failed").Select(r => r["Note"])));
        var defs = DoorWindowItems.Rows(p, DwSchedule.Read(s2));
        int index = defs.FindIndex(r => r.Label == heading);
        string Row() => DwTag.All(doc, p.SetKey).First(x => x.T.Key == "D|" + mark && x.T.Role == DwRole.Row && x.T.Index == index)
            .E.LookupParameter("KH_DW Value")?.AsString() ?? "";
        Check($"the '{heading}' row of {mark} shows the material's Description", Row() == "SELFTEST วัสดุ 1", Row());
        using (var t = new Transaction(doc, "material description again"))
        {
            t.Start();
            description.Set("SELFTEST วัสดุ 2");
            t.Commit();
        }
        Check("a material's Description edited reaches the row in the same transaction", Row() == "SELFTEST วัสดุ 2", Row());
        // "Value from" is saved on the schedule, by the field: Name instead of Description.
        long fieldId = DwSchedule.Read(s2).FieldIds[col];
        using (var t = new Transaction(doc, "value from: name"))
        {
            t.Start();
            DwValueFrom.Set(s2, fieldId, DwValueFrom.MaterialPrefix + "Name");
            t.Commit();
        }
        LegendBuilder.Run(doc, p);
        Check("Value from Material: Name shows the material's name", Row() == material.Name, Row());

        // A type TEXT parameter instead (the user's other case): picked for the same row.
        FamilySymbol type = DoorWindowItems.Collect(doc, p, new List<string>()).First(i => i.Mark == mark).Primary;
        string typeText = type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString() ?? "";
        using (var t = new Transaction(doc, "value from: type"))
        {
            t.Start();
            type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set("SELFTEST type text");
            DwValueFrom.Set(s2, fieldId, DwValueFrom.TypePrefix + "Type Comments");
            t.Commit();
        }
        LegendBuilder.Run(doc, p);
        Check("Value from Type: Type Comments shows the type's text", Row() == "SELFTEST type text", Row());
        using (var t = new Transaction(doc, "type text edit"))
        {
            t.Start();
            type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set("SELFTEST type text 2");
            t.Commit();
        }
        Check("and an edit to that type text reaches the row in the same transaction", Row() == "SELFTEST type text 2", Row());
        LiveDoorWindow.ClearQueue(doc);
    }

    /// <summary>A shared Material parameter "SELFTEST Frame Material" bound to door TYPES (from a
    /// temporary shared parameter file), set on each placed door type to a material of its own.</summary>
    private static void DwBindFrameMaterial(Document doc)
    {
        Autodesk.Revit.ApplicationServices.Application app = doc.Application;
        string old = app.SharedParametersFilename;
        string file = Path.Combine(Path.GetTempPath(), "drw-selftest-shared-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "");
        try
        {
            app.SharedParametersFilename = file;
            DefinitionFile defs = app.OpenSharedParameterFile();
            ExternalDefinition def = (ExternalDefinition)defs.Groups.Create("SelfTest").Definitions.Create(
                new ExternalDefinitionCreationOptions("SELFTEST Frame Material", SpecTypeId.Reference.Material));
            using var t = new Transaction(doc, "self-test material parameter");
            t.Start();
            CategorySet cats = app.Create.NewCategorySet();
            cats.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Doors));
            doc.ParameterBindings.Insert(def, app.Create.NewTypeBinding(cats), GroupTypeId.Materials);
            doc.Regenerate();
            var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Take(20).ToList();
            int k = 0, set = 0;
            foreach (FamilySymbol type in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType()
                         .OfType<FamilyInstance>().Select(i => i.Symbol).GroupBy(s => s.Id).Select(g => g.First()))
            {
                Parameter? prm = type.get_Parameter(def.GUID) ?? type.LookupParameter("SELFTEST Frame Material");
                if (prm != null && !prm.IsReadOnly && prm.Set(materials[k % materials.Count].Id)) set++;
                k++;
            }
            t.Commit();
            Info($"bound 'SELFTEST Frame Material' to door types; set on {set} of {k} type(s)");
        }
        finally
        {
            app.SharedParametersFilename = old;
            try { File.Delete(file); } catch { /* temp */ }
        }
    }

    /// <summary>The user's split (2026-10-21): a door set and a window set of the same name, each
    /// its own legends, neither touching the other's.</summary>
    private static void DwLegendSplit(Document doc)
    {
        DoorWindowPreset doors = DwPreset(doc, door: true), windows = DwPreset(doc, door: false);
        Check("doors built", LegendBuilder.Run(doc, doors).All(r => r["Status"] != "Failed"));
        var doorEls = DwTag.All(doc, doors.SetKey);
        Check("windows built", LegendBuilder.Run(doc, windows).All(r => r["Status"] != "Failed"));
        var winEls = DwTag.All(doc, windows.SetKey);
        Check("the door set holds only doors, the window set only windows",
            doorEls.Where(t => t.T.Key.Length > 0).All(t => t.T.Key.StartsWith("D|")) && winEls.Where(t => t.T.Key.Length > 0).All(t => t.T.Key.StartsWith("W|"))
            && doorEls.Count > 0 && winEls.Count > 0);
        Check("building windows left the door set as it was", DwTag.All(doc, doors.SetKey).Select(t => t.E.Id).OrderBy(i => i.Value)
            .SequenceEqual(doorEls.Select(t => t.E.Id).OrderBy(i => i.Value)));
        var legends = DwTag.All(doc, doors.SetKey).Concat(winEls).Where(t => t.T.Role == DwRole.Legend).Select(t => t.E.Name).ToList();
        Info("legends: " + string.Join(", ", legends));
        Check("two sets in the model's definitions", DwDefinitions.Load(doc).Count(d => d.Name == "SelfTest DW") == 2);
        Check("both in sync", LiveDoorWindow.Sync(doc, doors, false) == null && LiveDoorWindow.Sync(doc, windows, false) == null);
    }

    /// <summary>Narrow cells (40 mm at 1:50 = 2 m), so the wider types must span two or more.</summary>
    private static void DwLegendMerged(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc);
        p.CellWidthMm = 40;
        p.ElevationBoxMm = 30;   // smaller than most need: rows must grow
        var rows = LegendBuilder.Run(doc, p);
        Check("no Failed rows", rows.All(r => r["Status"] != "Failed"));
        foreach (var r in rows.Where(r => r["Status"] == "Info").Take(6)) Info($"{r["Item"]} {r["Note"]}");
        var items = DoorWindowItems.Collect(doc, p, new List<string>()).ToDictionary(i => i.Key);
        var tagged = DwTag.All(doc, p.SetKey);
        double k = p.Scale / 304.8, cell = p.CellWidthMm * k;

        int badSpan = 0, spanning = 0, shortBox = 0;
        var boxes = new List<(ElementId Legend, double L, double T, double R, double B, string Mark)>();
        foreach (var (e, t) in tagged.Where(t => t.T.Role == DwRole.Frame))
        {
            DwItem item = items[t.Key];
            double width = e.LookupParameter("Cell Width").AsDouble();
            int span = (int)Math.Round(width / cell);
            // At least what the elevation needs; the plan may ask for more (wall either side).
            if (span < DoorWindowLayout.Span(item.Width / k, p, p.Columns)) badSpan++;
            var plan = tagged.FirstOrDefault(x => x.T.Key == t.Key && x.T.Role == DwRole.Plan).E?.get_BoundingBox((View)doc.GetElement(e.OwnerViewId));
            if (plan != null && plan.Max.X - plan.Min.X > width + 1e-6) { badSpan++; if (badSpan <= 3) Info($"{item.Mark}: plan {SpMm(plan.Max.X - plan.Min.X)} wider than its cell {SpMm(width)}"); }
            if (span > 1) spanning++;
            if (Math.Abs(width - span * cell) > 1e-6 && !item.InstanceSized) { badSpan++; if (badSpan <= 3) Info($"{item.Mark}: width {SpMm(width)} not whole cells of {SpMm(cell)}"); }
            double elev = e.LookupParameter("Box 1 Height").AsDouble();
            if (elev + 1e-6 < DoorWindowLayout.ElevationNeedMm((item.Height + item.Sill) / k, p) * k) shortBox++;
            XYZ o = ((LocationPoint)e.Location).Point;
            double h = elev + e.LookupParameter("Box 2 Height").AsDouble() + e.LookupParameter("Header Height").AsDouble();
            boxes.Add((e.OwnerViewId, o.X, o.Y, o.X + width, o.Y - h, item.Mark));
        }
        // The user's overlap (2026-10-21): every elevation as DRAWN stays inside its own frame.
        int spill = 0;
        foreach (var (e, t) in tagged.Where(t => t.T.Role == DwRole.Frame))
        {
            View legend = (View)doc.GetElement(e.OwnerViewId);
            double left = ((LocationPoint)e.Location).Point.X, right = left + e.LookupParameter("Cell Width").AsDouble();
            var elev = tagged.FirstOrDefault(x => x.T.Key == t.Key && x.T.Role == DwRole.Elevation).E?.get_BoundingBox(legend);
            if (elev != null && (elev.Min.X < left - 1e-6 || elev.Max.X > right + 1e-6))
            {
                spill++;
                if (spill <= 3) Info($"{items[t.Key].Mark}: elevation {SpMm(elev.Min.X - left)}..{SpMm(elev.Max.X - left)} in a cell {SpMm(right - left)} wide");
            }
        }
        Check("every elevation as drawn stays inside its cell", spill == 0, $"{spill} spill over");
        Info($"{spanning} of {items.Count} items span more than one cell");
        Check("some items span cells at this width", spanning > 0);
        Check("each frame is whole cells, wide enough for its elevation and its plan", badSpan == 0, $"{badSpan} off");
        Check("each row's elevation box fits its tallest item", shortBox == 0, $"{shortBox} short");
        var overlaps = (from a in boxes from b in boxes
                        where a.Legend == b.Legend && string.CompareOrdinal(a.Mark, b.Mark) < 0
                              && a.L < b.R - 1e-6 && b.L < a.R - 1e-6 && a.B < b.T - 1e-6 && b.B < a.T - 1e-6
                        select a.Mark + "/" + b.Mark).ToList();
        Check("no two cells overlap", overlaps.Count == 0, string.Join(", ", overlaps.Take(5)));
        Check("the merged schedule is in sync", LiveDoorWindow.Sync(doc, p, write: false) == null,
            LiveDoorWindow.Sync(doc, p, write: false) ?? "");
        DwExport(doc, tagged.Where(t => t.T.Role == DwRole.Legend).Select(t => (View)t.E).First(), "dw-merged-1");
    }

    private static void DwLegendSeed(Document doc)
    {
        using (var t = new Transaction(doc, "no legends"))
        {
            t.Start();
            var legendIds = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate).Select(v => v.Id).ToList();
            doc.Delete(legendIds);
            t.Commit();
        }
        int components = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LegendComponents).WhereElementIsNotElementType().GetElementCount();
        Check("fixture: the model has no legend components left", components == 0, components.ToString());
        var familiesBefore = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(Family)).Select(f => f.Name));

        DoorWindowPreset p = DwPreset(doc);
        var rows = LegendBuilder.Run(doc, p);
        foreach (var r in rows.Where(r => r["Status"] is "Seeded" or "Failed" or "Loaded" or "Warning").Take(10)) Info($"row {r["Status"]}: {r["Note"]}");
        Check("seeded: a Seeded row and no Failed rows", rows.Any(r => r["Status"] == "Seeded") && rows.All(r => r["Status"] != "Failed"));
        var legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate).ToList();
        Check("seeded: the seed legend is gone again, the schedule's legends remain",
            legends.All(v => v.Name != "KH DW Seed") && legends.Count > 0, string.Join(", ", legends.Select(v => v.Name)));
        var added = new FilteredElementCollector(doc).OfClass(typeof(Family)).Select(f => f.Name)
            .Where(n => !familiesBefore.Contains(n)).ToList();
        Info("families added: " + string.Join(", ", added));
        Check("seeded: nothing but the KH_DW families is left behind",
            added.All(n => LegendBuilder.Families.Contains(n) || n.StartsWith("KH_DW")), string.Join(", ", added));
    }

    private static void DwExport(Document doc, View view, string name)
    {
        try
        {
            string dir = Path.GetDirectoryName(_report)!;
            var o = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(dir, name),
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 3000,
                ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = ImageFileType.PNG
            };
            o.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(o);
            Info("exported " + name + " (" + view.Name + ")");
        }
        catch (Exception ex) { Info("export failed: " + ex.Message); }
    }
}
#endif
