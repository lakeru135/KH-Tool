#if EDITION_DRW
using Autodesk.Revit.DB;
using System.IO;
using KhBatchRunner.Drw.DoorWindow;
using KhBatchRunner.Drw.Dialogs;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>Enlarge Door &amp; Window Phase 2: title block, fit, sheets.</summary>
internal static partial class DrwSelfTest
{
    private static void DwSheetNumbers()
    {
        var none = new HashSet<string>();
        Check("A-12 is the first number", DwSheets.Number("A-12", 0, none) == "A-12");
        Check("then A-13", DwSheets.Number("A-12", 1, none) == "A-13");
        Check("A-09 counts on to A-10, keeping the width", DwSheets.Number("A-09", 1, none) == "A-10");
        Check("a number already used is skipped", DwSheets.Number("A-12", 1, new HashSet<string> { "A-13" }) == "A-14");
        Check("no digits: 'DW', then 'DW 2'", DwSheets.Number("DW", 0, none) == "DW" && DwSheets.Number("DW", 1, none) == "DW 2");
        var frame = new DwFrame(10, 40, 410, 287);
        var (cols, h) = DwSheets.Grid(frame, new DoorWindowPreset { CellWidthMm = 120, SheetMarginMm = 5 });
        Check("an A3 frame 400 mm wide takes 3 cells of 120 mm", cols == 3, cols.ToString());
        Check("and 237 mm of height (frame less two margins)", Math.Abs(h - 237) < 1e-6, h.ToString("0.0"));
    }

    private static void DwSheetsTab()
    {
        // Doors and windows are separate sets: each counts only its own.
        foreach ((DwCategory cat, string count) in new[] { (DwCategory.Door, "3 items"), (DwCategory.Window, "4 items") })
        {
            using var w = new KhBatchRunner.Drw.Dialogs.DoorWindowWindow(DoorWindowFixture(), new DoorWindowPreset { TitleBlockName = "A3 Thai : A3", Category = cat, CellWidthMm = 120 });
            w.CreateControl();
            Check($"{cat}: nothing is packed until the Sheets tab shows", w.SheetSummaryText.Length == 0, w.SheetSummaryText);
            w.ShowSheets();
            string summary = w.SheetSummaryText;
            Info($"{cat} summary: " + summary);
            // A3 frame 400 x 237 inside the margins: 3 cells of 120 across.
            Check($"{cat}: the Sheets tab says how many sheets", summary.StartsWith(count) && summary.Contains("3 cells across"), summary);
            if (cat == DwCategory.Door)
            {
                // The rows: the schedule's fields (Description is the header's), its Width x Height
                // field the Size row and its Count field the Qty row.
                List<string> rows = w.RowsPreview;
                Info("rows: " + string.Join(" / ", rows));
                Check("Door rows come from the schedule, Size first and Qty last; the material row shows its Description",
                    rows.Count == 5 && rows[0] == "ขนาด | Schedule: Width x Height | Schedule cell | 900 x 2100"
                    && rows[1] == "วัสดุวงกบ | Schedule: material | Material: Description | ไม้เนื้อแข็ง   (schedule shows: Wood - Teak)"
                    && rows[3] == "หมายเหตุ | Schedule | Schedule cell | -" && rows[4] == "Qty. | Schedule: Count | Schedule cell | 24", string.Join(" / ", rows));
                List<string> materialChoices = w.ValueFromChoices("วัสดุวงกบ"), plainChoices = w.ValueFromChoices("หมายเหตุ");
                Check("only a material row offers Value from: its properties and the type's parameters, no instance ones",
                    materialChoices.Contains("Material: Mark") && materialChoices.Contains("Type: Frame Spec")
                    && !materialChoices.Any(c => c.StartsWith("Instance: ")) && plainChoices.Count == 0,
                    string.Join(", ", materialChoices) + " / " + string.Join(", ", plainChoices));
                w.PickValueFrom("วัสดุวงกบ", "Material: Mark");
                System.Windows.Forms.Application.DoEvents();
                Check("picking Material: Mark shows the material's Mark", w.RowsPreview[1].EndsWith("| M-01   (schedule shows: Wood - Teak)"), w.RowsPreview[1]);
                w.PickValueFrom("วัสดุวงกบ", "Type: Frame Spec");
                System.Windows.Forms.Application.DoEvents();
                Check("picking Type: Frame Spec shows the type's text", w.RowsPreview[1].EndsWith("| วงกบไม้ 2x4   (schedule shows: Wood - Teak)"), w.RowsPreview[1]);

                // The schedule edited in Revit (a field added): the window, told, follows.
                DwScheduleData edited = DoorWindowFixture().Schedules[0];
                edited.Headings.Add("ลูกฟัก");
                edited.FieldIds.Add(503);
                foreach (var cells in edited.ByMark.Values) cells.Add("กระจกใส 6 มม.");
                w.SetSchedule(edited);
                Check("a schedule re-read adds its new field as a row", w.RowsPreview.Count == 6 && w.RowsPreview[5].StartsWith("ลูกฟัก"),
                    string.Join(" / ", w.RowsPreview));
                Check("and the Sheets tab follows", w.SheetSummaryText.StartsWith(count), w.SheetSummaryText);

                // Sheet parameters: the browser organization's are listed first and fill a preset
                // that has none, with the value most sheets use; the user's own rows are kept.
                w.SetSheetChoices(new List<DwSheetParameterChoice>
                {
                    new() { Name = "Sheet Group", Organization = true, Values = { "A - Architecture", "S - Structure" } },
                    new() { Name = "Drawn By", Values = { "KH" } }
                });
                Check("the organization's parameter fills an empty list, with its first value",
                    w.SheetParameterRows.SequenceEqual(new[] { "Sheet Group = A - Architecture" }), string.Join(", ", w.SheetParameterRows));
                using var kept = new KhBatchRunner.Drw.Dialogs.DoorWindowWindow(DoorWindowFixture(), new DoorWindowPreset
                {
                    Category = cat, SheetParameters = { new DwSheetParameter { Name = "Drawn By", Value = "KH" } }
                });
                kept.CreateControl();
                kept.SetSheetChoices(new List<DwSheetParameterChoice> { new() { Name = "Sheet Group", Organization = true, Values = { "A" } }, new() { Name = "Drawn By" } });
                Check("a preset's own sheet parameters are kept as they are", kept.SheetParameterRows.SequenceEqual(new[] { "Drawn By = KH" }),
                    string.Join(", ", kept.SheetParameterRows));
            }
        }

        // Cell widths set by hand: the preview packs them as the build does.
        {
            var widths = DoorWindowFixture();
            foreach (DwItemRow r in widths.Items.Where(r => r.IsDoor)) r.AdjustedWidthMm = 0;
            widths.Items.First(r => r.IsDoor).AdjustedWidthMm = 300;
            using var w = new KhBatchRunner.Drw.Dialogs.DoorWindowWindow(widths, new DoorWindowPreset { TitleBlockName = "A3 Thai : A3", Category = DwCategory.Door });
            w.CreateControl();
            w.ShowSheets();
            Info("with a 300 mm cell: " + w.SheetSummaryText);
            Check("a 300 mm cell takes a row of its own on a 3 x 120 mm frame", w.SheetSummaryText.StartsWith("3 items"), w.SheetSummaryText);
        }
        var p3 = new DoorWindowPreset { CellWidthMm = 120 };
        DwPack packed = DoorWindowLayout.Pack(new List<DwNeed> { new(1, 50, 20, 150), new(1, 50, 20, 120), new(1, 50, 20, 120) }, p3, 3, 0);
        Check("pack by width: a 150 mm cell moves the next along to 150, the third to the next row",
            packed.Slots[1].XMm == 150 && packed.Slots[1].Row == 0 && packed.Slots[2].Row == 1 && packed.Slots[2].XMm == 0,
            string.Join(" | ", packed.Slots.Select(s => $"r{s.Row} x{s.XMm} w{s.WidthMm}")));
        var (wrappedText, wrappedLines) = DwWrap.Wrap("Aluminium frame powder coated bronze tempered glass", "TH Sarabun New", 2, 30);
        Check("a value wraps at the width it is given, with line breaks", wrappedLines > 1 && wrappedText.Split('\n').Length == wrappedLines, wrappedText.Replace("\n", " / "));
        Check("a wider column takes fewer lines", DwWrap.Lines("Aluminium frame powder coated bronze tempered glass", "TH Sarabun New", 2, 80) < wrappedLines);

        Check("per sheet: short runs in full", DoorWindowLayout.PerSheet(new List<int> { 3, 3, 2 }) == "3 + 3 + 2");
        Check("per sheet: a long run counted", DoorWindowLayout.PerSheet(Enumerable.Repeat(3, 10).Append(2).ToList()) == "10 × 3 + 2");

        // The user's KH A3 run (2026-10-20): 32 tall items, many sheets; the summary was cut off at
        // the bottom of the Preview group. Shown off-screen so the layout really runs.
        var data = DoorWindowFixture();
        DwItemRow tall = data.Items.First(i => i.Mark == "W1");
        data.Items = Enumerable.Range(1, 32).Select(n => new DwItemRow
        {
            Mark = "W" + n, Category = tall.Category, Types = tall.Types, Qty = 1, Size = tall.Size,
            WidthMm = tall.WidthMm, HeightSillMm = tall.HeightSillMm, PlanWidthMm = tall.PlanWidthMm
        }).ToList();
        using var many = new KhBatchRunner.Drw.Dialogs.DoorWindowWindow(data, new DoorWindowPreset { TitleBlockName = "A3 Thai : A3", Category = DwCategory.Window })
        {
            StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000)
        };
        many.ShowSheets();
        many.Show();
        System.Windows.Forms.Application.DoEvents();
        Info("summary: " + many.SheetSummaryText);
        Check("32 items: the summary shows whole inside the Preview group", many.SheetSummaryFits(), many.SheetSummaryText);
        many.Close();
    }

    /// <summary>
    /// The frame rule on the sample title blocks (unchanged from the first rule) and on the user's KH
    /// title blocks from KhBatchTest	itleblocks, which draw a grid of long lines across the drawing
    /// area: the frame must be the border and the title strip's edge, not one grid square.
    /// </summary>
    private static void DwTitleBlockFrames(Document doc)
    {
        var before = new Dictionary<string, (double L, double B, double R, double T)>
        {
            ["B 11 x 17 Horizontal : B 11 x 17 Horizontal"] = (6, 44, 425, 273),
            ["C 17 x 22 Horizontal : C 17 x 22 Horizontal"] = (25, 13, 457, 419),
            ["D 22 x 34 Horizontal : D 22 x 34 Horizontal"] = (38, 13, 749, 546)
        };
        foreach (DwTitleBlockData b in DoorWindowSheets.Read(doc).Where(b => before.ContainsKey(b.Name)))
        {
            var (l, bo, r, t) = before[b.Name];
            Check($"{b.Name}: frame as before", Math.Abs(b.Frame.L - l) < 1 && Math.Abs(b.Frame.B - bo) < 1 && Math.Abs(b.Frame.R - r) < 1 && Math.Abs(b.Frame.T - t) < 1,
                $"{b.Frame.L:0},{b.Frame.B:0} - {b.Frame.R:0},{b.Frame.T:0}");
        }

        string folder = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(doc.PathName)) ?? "", "titleblocks");
        string[] files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.rfa") : Array.Empty<string>();
        if (files.Length == 0) { Info("no title blocks in " + folder + " - the KH part is skipped"); return; }
        using (var tx = new Transaction(doc, "Load title blocks"))
        {
            tx.Start();
            foreach (string f in files) doc.LoadFamily(f, out Family _);
            tx.Commit();
        }
        var names = new HashSet<string>(files.Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
        foreach (DwTitleBlockData b in DoorWindowSheets.Read(doc).Where(b => names.Contains(b.Name.Split(':')[0].Trim())))
        {
            // Long lines, for the record: where the grid and the strip lie.
            var vx = b.Lines.Where(s => Math.Abs(s.X1 - s.X2) < 0.5).GroupBy(s => Math.Round((s.X1 + s.X2) / 2))
                .Where(g => g.Sum(s => Math.Abs(s.Y2 - s.Y1)) >= 0.7 * b.HeightMm).Select(g => g.Key).OrderBy(x => x);
            var hy = b.Lines.Where(s => Math.Abs(s.Y1 - s.Y2) < 0.5).GroupBy(s => Math.Round((s.Y1 + s.Y2) / 2))
                .Where(g => g.Sum(s => Math.Abs(s.X2 - s.X1)) >= 0.7 * b.WidthMm).Select(g => g.Key).OrderBy(y => y);
            Info($"'{b.Name}': {b.WidthMm:0} x {b.HeightMm:0} mm, {b.Lines.Count} lines; long X {string.Join(" ", vx)}; long Y {string.Join(" ", hy)}");
            Info($"  frame {b.Frame.L:0},{b.Frame.B:0} - {b.Frame.R:0},{b.Frame.T:0} ({b.Frame.Width:0} x {b.Frame.Height:0})");
            Check($"'{b.Name}': the frame is most of the sheet, not a grid square",
                b.Frame.Width >= 0.6 * b.WidthMm && b.Frame.Height >= 0.7 * b.HeightMm, $"{b.Frame.Width:0} x {b.Frame.Height:0}");
        }
    }

    private static void DwSheetsBuild(Document doc)
    {
        List<DwTitleBlockData> blocks = DoorWindowSheets.Read(doc);
        foreach (var b in blocks.Take(5))
            Info($"title block '{b.Name}': {b.WidthMm:0} x {b.HeightMm:0} mm, frame {b.Frame.L:0},{b.Frame.B:0} - {b.Frame.R:0},{b.Frame.T:0}, {b.Lines.Count} lines");
        Check("the model's title blocks are read", blocks.Count > 0);
        if (blocks.Count == 0) return;
        DwTitleBlockData tb = blocks.OrderByDescending(b => b.Frame.Width * b.Frame.Height).First();

        DoorWindowPreset p = DwPreset(doc);
        p.TitleBlockName = tb.Name;
        p.SheetNumber = "DW-101";
        p.SheetName = "Door & Window Schedule";
        var rows = LegendBuilder.Run(doc, p);
        foreach (var r in rows.Where(r => r["Status"] is "Info" or "Warning" or "Failed").Take(8)) Info($"row {r["Status"]}: {r["Legend"]} {r["Item"]} {r["Note"]}");
        Check("no Failed rows", rows.All(r => r["Status"] != "Failed"));

        var tagged = DwTag.All(doc, p.SetKey);
        var legends = tagged.Where(t => t.T.Role == DwRole.Legend).OrderBy(t => t.T.Index).Select(t => (View)t.E).ToList();
        var sheets = tagged.Where(t => t.T.Role == DwRole.Sheet).OrderBy(t => t.T.Index).Select(t => (ViewSheet)t.E).ToList();
        Check("one sheet per legend", sheets.Count == legends.Count && sheets.Count > 0, $"{sheets.Count} sheets, {legends.Count} legends");
        Check("numbered on from the start", sheets.Select(s => s.SheetNumber).SequenceEqual(sheets.Select((_, i) => DwSheets.Number("DW-101", i, new HashSet<string>()))),
            string.Join(", ", sheets.Select(s => s.SheetNumber)));

        int placed = 0, inside = 0;
        for (int i = 0; i < Math.Min(sheets.Count, legends.Count); i++)
        {
            Viewport? vp = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(v => v.ViewId == legends[i].Id);
            if (vp?.SheetId != sheets[i].Id) continue;
            placed++;
            // Every drawn cell's corners, on the sheet, inside the frame (and the margin kept).
            var f = DoorWindowSheets.FrameOn(doc, sheets[i]);
            double tol = 1 / 304.8, m = p.SheetMarginMm / 304.8;
            var corners = new List<XYZ>();
            foreach (var (e, t) in tagged.Where(t => t.T.Role == DwRole.Frame && t.E.OwnerViewId == legends[i].Id))
            {
                XYZ o = ((LocationPoint)e.Location).Point;
                double w = e.LookupParameter("Cell Width").AsDouble();
                double h = e.LookupParameter("Box 1 Height").AsDouble() + e.LookupParameter("Box 2 Height").AsDouble() + e.LookupParameter("Header Height").AsDouble();
                foreach (XYZ q in new[] { o, o + new XYZ(w, -h, 0) })
                    if (DoorWindowSheets.ToSheet(legends[i], vp, q) is { } sp) corners.Add(sp);
            }
            if (corners.Count == 0) { Info("no legend-to-sheet mapping: " + DoorWindowSheets.LastMappingError); continue; }
            // The mapping's premise: the viewport box is the legend's content at its scale.
            if (DoorWindowSheets.ContentBox(legends[i]) is { } cb)
            {
                Outline vb = vp.GetBoxOutline();
                Info($"{sheets[i].SheetNumber}: box {SpMm(vb.MaximumPoint.X - vb.MinimumPoint.X)} x {SpMm(vb.MaximumPoint.Y - vb.MinimumPoint.Y)} mm, " +
                     $"content / scale {SpMm((cb.Max.X - cb.Min.X) / legends[i].Scale)} x {SpMm((cb.Max.Y - cb.Min.Y) / legends[i].Scale)} mm");
            }
            bool ok = f is { } fr && corners.Count > 0 && corners.All(q => q.X >= fr.L + m - tol && q.X <= fr.R + tol && q.Y <= fr.T - m + tol && q.Y >= fr.B - tol);
            if (ok) inside++;
            else Info($"{sheets[i].SheetNumber}: cells x {SpMm(corners.Min(q => q.X))}..{SpMm(corners.Max(q => q.X))}, y {SpMm(corners.Min(q => q.Y))}..{SpMm(corners.Max(q => q.Y))} vs frame " +
                      (f is { } q2 ? $"{SpMm(q2.L)},{SpMm(q2.B)} - {SpMm(q2.R)},{SpMm(q2.T)}" : "none"));
            if (corners.Count > 0 && f is { } top)
                Check($"{sheets[i].SheetNumber}: the grid's top-left sits at the frame corner + margin",
                    Math.Abs(corners.Min(q => q.X) - (top.L + m)) < tol && Math.Abs(corners.Max(q => q.Y) - (top.T - m)) < tol);
        }
        Check("each legend is on its own sheet", placed == legends.Count, $"{placed} of {legends.Count}");
        Check("every drawn cell sits inside its sheet's frame", inside == placed, $"{inside} of {placed}");
        if (sheets.Count > 0) DwExport(doc, sheets[0], "dw-sheet-1");

        // Rerun: the same sheets, nothing doubled.
        var ids = sheets.Select(s => s.Id).ToList();
        int before = tagged.Count;
        rows = LegendBuilder.Run(doc, p);
        var again = DwTag.All(doc, p.SetKey);
        Check("rerun: no Failed rows", rows.All(r => r["Status"] != "Failed"));
        Check("rerun: the same sheets are reused", again.Where(t => t.T.Role == DwRole.Sheet).OrderBy(t => t.T.Index).Select(t => t.E.Id).SequenceEqual(ids));
        Check("rerun: nothing doubled", again.Count == before, $"{before} -> {again.Count}");
        Check("rerun: one viewport per legend", again.Where(t => t.T.Role == DwRole.Legend)
            .All(l => new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Count(v => v.ViewId == l.E.Id) == 1));
        Check("the schedule with sheets is in sync", LiveDoorWindow.Sync(doc, p, write: false) == null);
    }
}
#endif
