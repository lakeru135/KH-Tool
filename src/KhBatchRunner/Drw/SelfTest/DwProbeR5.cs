#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.DoorWindow;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// Dev-only (scenario "DW PROBE R5", DW_DEV=1, 2026-10-22): what the fifth round rests on. A combined
/// Width x Height field and a Count field in the set's starter schedule: which schedulable fields
/// they take, what their cells read per Type Mark (instance-sized types too), and whether the
/// combined field takes format options. Rolled back.
/// </summary>
internal static partial class DrwSelfTest
{
    private static void DwProbeR5(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc, door: true);
        using var t = new Transaction(doc, "probe r5");
        t.Start();
        ViewSchedule s = DwSchedule.MakeStarter(doc, p);
        ScheduleDefinition d = s.Definition;
        IList<SchedulableField> all = d.GetSchedulableFields();
        foreach (SchedulableField f in all.Where(f => f.GetName(doc) is "Width" or "Height" or "Rough Width" or "Rough Height" or "Count"))
            Info($"  schedulable '{f.GetName(doc)}' {f.FieldType} param {f.ParameterId.Value}");

        SchedulableField? W = all.FirstOrDefault(f => f.GetName(doc) == "Width" && f.FieldType == ScheduleFieldType.ElementType)
                              ?? all.FirstOrDefault(f => f.GetName(doc) == "Width");
        SchedulableField? H = all.FirstOrDefault(f => f.GetName(doc) == "Height" && f.FieldType == ScheduleFieldType.ElementType)
                              ?? all.FirstOrDefault(f => f.GetName(doc) == "Height");
        try
        {
            var w = TableCellCombinedParameterData.Create();
            w.ParamId = W!.ParameterId;
            w.Separator = " x ";
            var h = TableCellCombinedParameterData.Create();
            h.ParamId = H!.ParameterId;
            ScheduleField size = d.InsertCombinedParameterField(new List<TableCellCombinedParameterData> { w, h }, "Size", 2);
            size.ColumnHeading = "ขนาด";
            Info($"  combined field: type {size.FieldType}, name '{size.GetName()}', param {size.ParameterId.Value}");
            try
            {
                var fo = new FormatOptions(UnitTypeId.Millimeters) { UseDefault = false };
                size.SetFormatOptions(fo);
                Info("  combined SetFormatOptions: ok");
            }
            catch (Exception ex) { Info("  combined SetFormatOptions: " + ex.GetType().Name + ": " + ex.Message); }
            foreach (TableCellCombinedParameterData x in size.GetCombinedParameters())
                Info($"  combined part param {x.ParamId.Value} sep '{x.Separator}' prefix '{x.Prefix}' suffix '{x.Suffix}'");
        }
        catch (Exception ex) { Info("  combined field: " + ex.GetType().Name + ": " + ex.Message); }

        try
        {
            SchedulableField? count = all.FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count);
            ScheduleField qty = d.AddField(count ?? new SchedulableField(ScheduleFieldType.Count));
            qty.ColumnHeading = "Qty.";
            Info($"  count field: type {qty.FieldType}, from list {count != null}");
        }
        catch (Exception ex) { Info("  count field: " + ex.GetType().Name + ": " + ex.Message); }

        doc.Regenerate();
        DwScheduleData data = DwSchedule.Read(s);
        Info("  headings: " + string.Join(" | ", data.Headings));
        foreach (var kv in data.ByMark.Take(80))
            Info($"  {kv.Key}: " + string.Join(" | ", kv.Value));
        var instanceSized = DoorWindowItems.Collect(doc, p, new List<string>()).Where(i => i.InstanceSized).Select(i => i.Mark);
        Info("  instance-sized marks: " + string.Join(", ", instanceSized));
        t.RollBack();

        // The Value label: its box and parameters (round 4 made it 1000 mm wide, which pushed the
        // legend's extents a metre past its cells).
        string? path = KhBatchRunner.Drw.Annotation.BundledFamilies.Extract(LegendBuilder.TitleFamily + ".rfa", Path.Combine(Path.GetTempPath(), "dw-probe-r5"));
        if (path == null) { Info("  no bundled " + LegendBuilder.TitleFamily); return; }
        Document fam = doc.Application.OpenDocumentFile(path);
        try
        {
            foreach (TextElement label in new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>().Where(x => x is not TextNote))
            {
                Info($"  label {label.GetType().Name} width {label.Width * 304.8:0.#} mm, align {label.HorizontalAlignment}");
                foreach (Parameter q in label.Parameters.Cast<Parameter>().OrderBy(q => q.Definition.Name))
                    Info($"    '{q.Definition.Name}' bip={(q.Definition as InternalDefinition)?.BuiltInParameter} {q.StorageType} ro={q.IsReadOnly} = {q.AsValueString() ?? q.AsString()}");
                foreach (var prop in label.GetType().GetProperties())
                    try { Info($"    .{prop.Name} = {prop.GetValue(label)}"); } catch { }
            }
        }
        finally { fam.Close(false); }
    }

    /// <summary>A narrow Value / title label box with "Wrap between parameters only" on: does it still
    /// show written line breaks, never wrap on its own, and keep the legend's extents to its cells?</summary>
    private static void DwProbeR5Label(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc, door: true);
        LegendBuilder.Run(doc, p);
        View legend = (View)DwTag.All(doc, p.SetKey).First(m => m.T.Role == DwRole.Legend).E;
        (XYZ Min, XYZ Max) Box() => DoorWindowSheets.ContentBox(legend)!.Value;
        double Wmm((XYZ Min, XYZ Max) b) => (b.Max.X - b.Min.X) * 304.8 / legend.Scale;
        Info($"  legend content width before: {Wmm(Box()):0} mm paper");

        void Narrow(Document fam)
        {
            using var t = new Transaction(fam, "narrow");
            t.Start();
            foreach (TextElement label in new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>().Where(x => x is not TextNote))
            {
                label.Width = 5 / 304.8;
                label.get_Parameter(BuiltInParameter.TAG_NO_BREAK_PARAM_STRINGS)?.Set(1);
            }
            t.Commit();
        }
        Family titleFam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().First(f => f.Name == LegendBuilder.TitleFamily);
        Family rowFam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().First(f => f.Name == LegendBuilder.RowFamily);
        Document title = doc.EditFamily(titleFam);
        try { Narrow(title); title.LoadFamily(doc, new DwOverwrite()); } finally { title.Close(false); }
        Document row = doc.EditFamily(rowFam);
        try
        {
            Family nested = new FilteredElementCollector(row).OfClass(typeof(Family)).Cast<Family>().First(f => f.Name == LegendBuilder.TitleFamily);
            Document inner = row.EditFamily(nested);
            try { Narrow(inner); inner.LoadFamily(row, new DwOverwrite()); } finally { inner.Close(false); }
            row.LoadFamily(doc, new DwOverwrite());
        }
        finally { row.Close(false); }

        var rows = DwTag.All(doc, p.SetKey).Where(m => m.T.Role == DwRole.Row).Select(m => (FamilyInstance)m.E).Take(2).ToList();
        using (var t = new Transaction(doc, "values"))
        {
            t.Start();
            rows[0].LookupParameter("KH_DW Value")?.Set("A long first line that would wrap in a narrow box\nsecond line");
            rows[1].LookupParameter("KH_DW Value")?.Set("short");
            t.Commit();
        }
        Info($"  legend content width after: {Wmm(Box()):0} mm paper");
        BoundingBoxXYZ? rb = rows[0].get_BoundingBox(legend);
        if (rb != null) Info($"  long row's box width {(rb.Max.X - rb.Min.X) * 304.8 / legend.Scale:0} mm paper (row width {p.CellWidthMm})");
        DwExport(doc, legend, "dw-probe-r5-label");
    }
}
#endif
