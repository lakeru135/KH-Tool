#if EDITION_DRW
using System.IO;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.DoorWindow;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// The fourth round of the user's hand test (2026-10-22): host wall stubs, families v2 (3 / 2 mm
/// text, reloaded over older copies), values wrapped by their cell's width, instance-sized types
/// drawn as outlines, cell widths set by hand remembered, legends placed with no title and the
/// set's sheet parameters written.
/// </summary>
internal static partial class DrwSelfTest
{
    private const string LongComment = "Aluminium frame, powder coated bronze, tempered clear glass 6 mm, stainless steel lever handle and hinges";

    /// <summary>Families v2, wall stubs, wrapping by the cell's width.</summary>
    private static void DwRound4Build(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc);
        List<DwItem> items0 = DoorWindowItems.Collect(doc, p, new List<string>());
        Check("doors to build with", items0.Count > 0);
        if (items0.Count == 0) return;
        // A long Type Comments (the starter's หมายเหตุ row), so it must wrap.
        using (var t = new Transaction(doc, "long comment"))
        {
            t.Start();
            items0[0].Primary.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set(LongComment);
            t.Commit();
        }
        var rows = LegendBuilder.Run(doc, p);
        Check("no Failed rows", rows.All(r => r["Status"] != "Failed"), string.Join("; ", rows.Where(r => r["Status"] == "Failed").Select(r => r["Note"])));
        foreach (string f in LegendBuilder.Families)
            Check($"{f} is version {LegendBuilder.FamilyVersion} in the model", BundledFamilies.VersionIn(doc, f) == LegendBuilder.FamilyVersion,
                BundledFamilies.VersionIn(doc, f)?.ToString() ?? "missing");

        var tagged = DwTag.All(doc, p.SetKey);
        var items = DoorWindowItems.Collect(doc, p, new List<string>()).ToDictionary(i => i.Key);
        double k = p.Scale / 304.8;
        int badHost = 0;
        foreach (var (e, t) in tagged.Where(x => x.T.Role == DwRole.Plan))
        {
            double host = e.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_LENGTH)?.AsDouble() ?? 0;
            double want = items[t.Key].Width + 2 * p.WallStubMm * k;
            if (Math.Abs(host - want) > 1 / 304.8) { badHost++; if (badHost <= 3) Info($"{items[t.Key].Mark}: host {SpMm(host)} vs {SpMm(want)}"); }
        }
        Check("each plan's host wall = the opening + a stub each side", badHost == 0, $"{badHost} off");

        // Every row's value: as many lines as its row has, each fitting its cell's value column.
        View legend = tagged.Where(x => x.T.Role == DwRole.Legend).Select(x => (View)x.E).First();
        double fit = DwDefinitions.TextFit(legend);
        Info($"text fit (Revit / GDI) {fit:0.000}");
        Check("the text fit was measured", Math.Abs(fit - 1) > 1e-6 && fit > 0.3 && fit < 4, fit.ToString("0.000"));
        int badLines = 0, tooWide = 0, wrapped = 0;
        foreach (var (e, t) in tagged.Where(x => x.T.Role == DwRole.Row))
        {
            string value = e.LookupParameter("KH_DW Value")?.AsString() ?? "";
            int lines = e.LookupParameter("Lines")?.AsInteger() ?? 1;
            string[] parts = value.Split('\n');
            if (parts.Length > 1) wrapped++;
            if (parts.Length > lines) { badLines++; if (badLines <= 3) Info($"{t.Key} row {t.Index}: {parts.Length} lines in a row of {lines}"); }
            var frame = tagged.First(x => x.T.Key == t.Key && x.T.Role == DwRole.Frame).E;
            double valueMm = DoorWindowLayout.ValueWidthMm(LegendBuilder.FrameWidthMm(doc, frame) ?? p.CellWidthMm, p);
            foreach (string line in parts.Where(l => l.Contains(' ')))
                if (DwWrap.MeasureMm(line, p.RowFont, p.RowTextMm) * fit > valueMm + 0.5) { tooWide++; if (tooWide <= 3) Info($"'{line}' wider than {valueMm:0} mm"); }
        }
        Check("the long Type Comments wrapped, with line breaks", wrapped > 0);
        Check("each row is as tall as its value's lines", badLines == 0, $"{badLines} off");
        Check("each value line fits its cell's value column", tooWide == 0, $"{tooWide} too wide");
        Check("in sync after the build", LiveDoorWindow.Sync(doc, p, write: false) == null, LiveDoorWindow.Sync(doc, p, write: false) ?? "");
        DwExport(doc, legend, "dw-round4-1");

        // A model holding the old (v1) KH_DW_Row, as committed before families v2 (the runner puts it
        // in KhBatchTest\dw-v1-<year>): the build reloads the bundled one over it.
        string v1 = Path.Combine(Path.GetDirectoryName(_report)!, "dw-v1-" + doc.Application.VersionNumber, LegendBuilder.RowFamily + ".rfa");
        if (!File.Exists(v1)) { Info("no v1 family at " + v1 + "; reload not tested"); }
        else
        {
            using (var t = new Transaction(doc, "old family"))
            {
                t.Start();
                doc.LoadFamily(v1, new DwOverwrite(), out Family _);
                t.Commit();
            }
            Check("…the v1 row family loaded reads as version 1", BundledFamilies.VersionIn(doc, LegendBuilder.RowFamily) == 1,
                BundledFamilies.VersionIn(doc, LegendBuilder.RowFamily)?.ToString() ?? "missing");
            rows = LegendBuilder.Run(doc, p);
            Check("…a build reloads the bundled one and says so", rows.Any(r => r["Status"] == "Updated" && r["Note"].Contains(LegendBuilder.RowFamily))
                                                                  && BundledFamilies.VersionIn(doc, LegendBuilder.RowFamily) == LegendBuilder.FamilyVersion,
                string.Join("; ", rows.Where(r => r["Status"] is "Updated" or "Loaded").Select(r => r["Note"])));
            Check("…and the rebuilt legend is in sync", LiveDoorWindow.Sync(doc, p, write: false) == null);
        }
        // Only the version number off (the family itself current): put right, with no reload.
        FamilySymbol row = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().First(s => s.FamilyName == LegendBuilder.RowFamily);
        using (var t = new Transaction(doc, "version off"))
        {
            t.Start();
            row.LookupParameter(BundledFamilies.VersionParameter)?.Set(1);
            t.Commit();
        }
        LegendBuilder.Run(doc, p);
        Check("…a version number edited down is put back", BundledFamilies.VersionIn(doc, LegendBuilder.RowFamily) == LegendBuilder.FamilyVersion);
    }

    /// <summary>A cell width set by hand: the updater queues a rebuild, which keeps the width and
    /// moves the next cell along; reset forgets it.</summary>
    private static void DwRound4Widths(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc);
        LegendBuilder.Run(doc, p);
        LiveDoorWindow.ClearQueue(doc);
        var tagged = DwTag.All(doc, p.SetKey);
        var frames = tagged.Where(x => x.T.Role == DwRole.Frame).Select(x => x.E)
            .OrderBy(e => e.OwnerViewId.Value).ThenByDescending(e => ((LocationPoint)e.Location).Point.Y).ThenBy(e => ((LocationPoint)e.Location).Point.X).ToList();
        Check("frames to widen", frames.Count >= 2);
        if (frames.Count < 2) return;
        Element first = frames[0];
        string key = DwTag.Get(first)!.Value.Key;
        double was = LegendBuilder.FrameWidthMm(doc, first) ?? 0, k = p.Scale / 304.8;
        Element next = frames[1];
        double nextX = ((LocationPoint)next.Location).Point.X;
        Check("a frame records the width it was built with", Math.Abs(DwTag.Get(first)!.Value.Index / 10.0 - was) < 0.1, $"{DwTag.Get(first)!.Value.Index / 10.0} vs {was}");

        using (var t = new Transaction(doc, "widen a cell by hand"))
        {
            t.Start();
            first.LookupParameter("Cell Width").Set((was + 30) * k);
            t.Commit();
        }
        Check("the updater queues the set's rebuild", LiveDoorWindow.QueuedCount(doc) > 0, LiveDoorWindow.LastTrigger);
        Check("Sync says the width was set by hand", (LiveDoorWindow.Sync(doc, p, write: false) ?? "").Contains("set by hand"));
        LiveDoorWindow.Flush(doc, new[] { p.SetKey });
        LiveDoorWindow.ClearQueue(doc);

        var again = DwTag.All(doc, p.SetKey);
        Element f2 = again.First(x => x.T.Role == DwRole.Frame && x.T.Key == key).E;
        double now = LegendBuilder.FrameWidthMm(doc, f2) ?? 0;
        Check("the rebuild keeps the width set by hand", Math.Abs(now - (was + 30)) < 0.5, $"{was:0} -> {now:0} mm");
        Check("…and remembers it in the model", DwDefinitions.Widths(doc, p.SetKey).TryGetValue(key, out double kept) && Math.Abs(kept - (was + 30)) < 0.5);
        string nextKey = DwTag.Get(next)?.Key ?? "";
        Element? n2 = again.FirstOrDefault(x => x.T.Role == DwRole.Frame && x.T.Key == nextKey).E;
        if (n2 != null && n2.OwnerViewId == f2.OwnerViewId && Math.Abs(((LocationPoint)n2.Location).Point.Y - ((LocationPoint)f2.Location).Point.Y) < 1e-6)
            Check("…the next cell moved along by 30 mm", Math.Abs(((LocationPoint)n2.Location).Point.X - nextX - 30 * k) < 1e-6,
                $"{SpMm(((LocationPoint)n2.Location).Point.X - nextX)} model mm");
        else Info("the next cell went on to the next row of cells");
        Check("…and stays in sync", LiveDoorWindow.Sync(doc, p, write: false) == null, LiveDoorWindow.Sync(doc, p, write: false) ?? "");

        // A second rebuild changes nothing; reset forgets the width.
        LegendBuilder.Run(doc, p);
        Element f3 = DwTag.All(doc, p.SetKey).First(x => x.T.Role == DwRole.Frame && x.T.Key == key).E;
        Check("a second rebuild keeps it", Math.Abs((LegendBuilder.FrameWidthMm(doc, f3) ?? 0) - (was + 30)) < 0.5);

        // Narrower than its drawing: kept too, with a note (the user, 2026-10-22: it jumped back).
        using (var t = new Transaction(doc, "narrow a cell by hand"))
        {
            t.Start();
            f3.LookupParameter("Cell Width").Set(20 * k);
            t.Commit();
        }
        LiveDoorWindow.ClearQueue(doc);
        var narrowRows = LegendBuilder.Run(doc, p);
        Element f5 = DwTag.All(doc, p.SetKey).First(x => x.T.Role == DwRole.Frame && x.T.Key == key).E;
        Check("a width narrower than the drawing is kept", Math.Abs((LegendBuilder.FrameWidthMm(doc, f5) ?? 0) - 20) < 0.5,
            $"{LegendBuilder.FrameWidthMm(doc, f5):0} mm");
        Check("…and the results say the drawing may cross it", narrowRows.Any(r => r["Note"].Contains("narrower than its drawing")));
        Check("reset forgets it", LegendBuilder.ForgetHandSetWidths(doc, p) && DwDefinitions.Widths(doc, p.SetKey).Count == 0);
        LegendBuilder.Run(doc, p);
        Element f4 = DwTag.All(doc, p.SetKey).First(x => x.T.Role == DwRole.Frame && x.T.Key == key).E;
        Check("…and the rebuild is back to whole cells", Math.Abs((LegendBuilder.FrameWidthMm(doc, f4) ?? 0) - was) < 0.5,
            $"{LegendBuilder.FrameWidthMm(doc, f4):0} vs {was:0}");
        LiveDoorWindow.ClearQueue(doc);
    }

    /// <summary>A door whose width or height is an instance parameter: drawn as an outline at the
    /// placed size, no legend component, dimensions on it.</summary>
    private static void DwRound4Instance(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc);
        List<DwItem> items = DoorWindowItems.Collect(doc, p, new List<string>());
        // Revit will not make a built-in Width an instance parameter by API, so the model's own
        // instance-sized families (Snowdon has some) are what is checked.
        DwItem? item = items.FirstOrDefault(i => i.InstanceSized);
        Info($"instance-sized: {string.Join(", ", items.Where(i => i.InstanceSized).Select(i => $"{i.Mark} {SpMm(i.Width)} x {SpMm(i.Height)}"))}");
        Check("the model has an instance-sized door, its placed size read", item is { Width: > 0, Height: > 0 });
        if (item == null) return;
        var rows = LegendBuilder.Run(doc, p);
        Check("no Failed rows", rows.All(r => r["Status"] != "Failed"), string.Join("; ", rows.Where(r => r["Status"] == "Failed").Select(r => r["Note"])));
        Check("the results say it is drawn as an outline", rows.Any(r => r["Item"] == item.Mark && r["Note"].Contains("outline")));
        var mine = DwTag.All(doc, p.SetKey).Where(x => x.T.Key == item.Key).ToList();
        Check("no legend component for it", !mine.Any(x => x.T.Role is DwRole.Elevation or DwRole.Plan));
        var lines = mine.Where(x => x.T.Role == DwRole.Outline).Select(x => ((DetailCurve)x.E).GeometryCurve).OfType<Line>().ToList();
        Check("its outline: 4 elevation lines and the plan's jambs and stubs", lines.Count == 4 + 2 * 4, $"{lines.Count} lines");
        var dims = mine.Where(x => x.T.Role == DwRole.Dimension).Select(x => ((Dimension)x.E).Value ?? 0).ToList();
        Check("its dimensions read the placed size", dims.Any(v => Math.Abs(v - item.Width) < 1 / 304.8) && dims.Any(v => Math.Abs(v - item.Height) < 1 / 304.8),
            string.Join("/", dims.Select(SpMm)) + $" vs {SpMm(item.Width)} x {SpMm(item.Height)}");
        // The elevation rectangle is the placed size and centred in its cell.
        Element frame = mine.First(x => x.T.Role == DwRole.Frame).E;
        double left = ((LocationPoint)frame.Location).Point.X, cw = frame.LookupParameter("Cell Width").AsDouble();
        var verticals = lines.Take(4).Where(l => Math.Abs(l.Direction.Y) > 0.999).Select(l => l.Origin.X).ToList();
        Check("…centred across its cell", verticals.Count == 2 && Math.Abs((verticals.Min() + verticals.Max()) / 2 - (left + cw / 2)) < 1e-6
                                         && Math.Abs(verticals.Max() - verticals.Min() - item.Width) < 1e-6);
        Check("…and in sync", LiveDoorWindow.Sync(doc, p, write: false) == null, LiveDoorWindow.Sync(doc, p, write: false) ?? "");
        DwExport(doc, (View)doc.GetElement(frame.OwnerViewId), "dw-round4-instance");
    }

    /// <summary>Sheets: legends with no title; the set's sheet parameters written.</summary>
    private static void DwRound4Sheets(Document doc)
    {
        List<DwTitleBlockData> blocks = DoorWindowSheets.Read(doc);
        Check("a title block", blocks.Count > 0);
        if (blocks.Count == 0) return;
        List<DwSheetParameterChoice> choices = DoorWindowSheets.ParameterChoices(doc);
        Info($"sheet parameters offered: {string.Join(", ", choices.Take(12).Select(c => (c.Organization ? "*" : "") + c.Name))}");
        Check("the sheets' editable parameters are offered", choices.Count > 0);
        Check("the organization's come first", choices.SkipWhile(c => c.Organization).All(c => !c.Organization));
        DwSheetParameterChoice? text = choices.FirstOrDefault(c => !c.Organization && c.Name == LabelUtils.GetLabelFor(BuiltInParameter.SHEET_DRAWN_BY))
                                       ?? choices.FirstOrDefault(c => !c.Organization);
        DoorWindowPreset p = DwPreset(doc);
        p.TitleBlockName = blocks.OrderByDescending(b => b.Frame.Width * b.Frame.Height).First().Name;
        p.SheetNumber = "DW-401";
        p.SheetParameters = new List<DwSheetParameter> { new() { Name = text?.Name ?? "", Value = "KH self-test" }, new() { Name = "No Such Parameter", Value = "x" } };
        var rows = LegendBuilder.Run(doc, p);
        Check("no Failed rows", rows.All(r => r["Status"] != "Failed"));
        var tagged = DwTag.All(doc, p.SetKey);
        var sheets = tagged.Where(x => x.T.Role == DwRole.Sheet).Select(x => (ViewSheet)x.E).ToList();
        Check("sheets made", sheets.Count > 0);
        if (text != null)
            Check($"'{text.Name}' written on every sheet", sheets.All(s => s.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Definition?.Name == text.Name)?.AsString() == "KH self-test"));
        Check("a parameter the sheets lack is reported once", rows.Count(r => r["Status"] == "Warning" && r["Note"].Contains("No Such Parameter")) == 1);
        var vps = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
            .Where(v => tagged.Any(x => x.T.Role == DwRole.Legend && x.E.Id == v.ViewId)).ToList();
        Check("each legend placed with a no-title viewport type", vps.Count > 0 && vps.All(v =>
            doc.GetElement(v.GetTypeId())?.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.AsInteger() == 0),
            string.Join(", ", vps.Select(v => doc.GetElement(v.GetTypeId())?.Name)));
    }
}
#endif
