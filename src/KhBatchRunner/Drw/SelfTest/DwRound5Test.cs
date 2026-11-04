#if EDITION_DRW
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.DoorWindow;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SelfTest;

/// <summary>
/// The fifth round of the user's hand test (2026-10-22): the layout defaults from their own 2024
/// settings, Size and Qty as the schedule's Width x Height and Count fields, the level symbol on the
/// floor line, dimensions moved by hand kept on rebuild, and the legend's extents ending at its
/// cells (families v3: the label boxes narrow again).
/// </summary>
internal static partial class DrwSelfTest
{
    /// <summary>Layout defaults; Size and Qty from the schedule; the extents end at the cells.</summary>
    private static void DwRound5Schedule(Document doc)
    {
        var d = new DoorWindowPreset();
        Check("layout defaults are the user's 2024 settings",
            d.Scale == 50 && d.CellWidthMm == 70 && d.ElevationBoxMm == 70 && d.PlanBoxMm == 30 && d.HeaderMm == 10 && d.RowLineMm == 7.5
            && d.LabelWidthMm == 20 && d.TextInsetMm == 2.5 && d.Columns == 4 && d.RowsPerLegend == 2 && d.FloorGapMm == 5
            && d.DimOffsetMm == 5 && d.WallStubMm == 15 && d.FloorLabel == "FFL.");

        DoorWindowPreset p = DwPreset(doc);
        ViewSchedule s = DwSchedule.Find(doc, p)!;
        Check("the starter has the Width x Height field, headed as the Size row", DwSchedule.SizeField(s) is { } sf && sf.ColumnHeading == p.SizeLabel);
        Check("…and Revit's Count field, headed as the Qty row", DwSchedule.CountField(s) is { } cf && cf.ColumnHeading == p.QtyLabel);
        DwScheduleData data = DwSchedule.Read(s);
        List<DwRowDef> defs = DoorWindowItems.Rows(p, data);
        int size = defs.FindIndex(r => r.Kind == DwRowKind.Size), qty = defs.FindIndex(r => r.Kind == DwRowKind.Qty);
        Check("the rows' Size and Qty are those schedule columns", size >= 0 && defs[size].Column == data.SizeColumn && qty >= 0 && defs[qty].Column == data.CountColumn,
            string.Join("|", defs.Select(r => $"{r.Label}:{r.Kind}:{r.Column}")));

        var rows = LegendBuilder.Run(doc, p);
        Check("no Failed rows", rows.All(r => r["Status"] != "Failed"), string.Join("; ", rows.Where(r => r["Status"] == "Failed").Select(r => r["Note"])));
        var tagged = DwTag.All(doc, p.SetKey);
        List<DwItem> items = DoorWindowItems.Collect(doc, p, new List<string>(), data);
        int badQty = 0, badSize = 0;
        foreach (DwItem item in items)
        {
            var rowEls = tagged.Where(x => x.T.Key == item.Key && x.T.Role == DwRole.Row).OrderBy(x => x.T.Index).Select(x => x.E).ToList();
            if (rowEls.Count != defs.Count) { badQty++; continue; }
            string q = rowEls[qty].LookupParameter("KH_DW Value")?.AsString() ?? "";
            if (q != item.Instances.Count.ToString()) { badQty++; if (badQty <= 3) Info($"{item.Mark}: Qty '{q}' vs {item.Instances.Count} placed"); }
            string z = rowEls[size].LookupParameter("KH_DW Value")?.AsString() ?? "";
            string cell = data.Cell(item.Mark, data.SizeColumn);
            string want = item.Width <= 0 || item.Height <= 0 ? p.Blank
                : item.InstanceSized ? DoorWindowItems.Size(doc, item.Width, item.Height, DwSizeFormat.ProjectUnits) : cell;
            if (z != want) { badSize++; if (badSize <= 3) Info($"{item.Mark}: Size '{z}' vs '{want}' (cell '{cell}')"); }
        }
        Check("each Qty row is Revit's count, the placed instances", badQty == 0, $"{badQty} off");
        Check("each Size row is the schedule's Width x Height (the placed size when instance-sized)", badSize == 0, $"{badSize} off");
        // (An instance field, Mark, may say "<varies>" as the schedule does; the Size row never.)
        Check("no Size row says '<varies>'", tagged.Where(x => x.T.Role == DwRole.Row && x.T.Index == size)
            .All(x => (x.E.LookupParameter("KH_DW Value")?.AsString() ?? "").IndexOf("<varies>", StringComparison.OrdinalIgnoreCase) < 0));

        // The extents (what a viewport and Zoom to Fit use) end at the cells: no label box reaching past them.
        View legend = (View)tagged.Where(x => x.T.Role == DwRole.Legend).OrderBy(x => x.T.Index).First().E;
        var frames = tagged.Where(x => x.T.Role == DwRole.Frame && x.E.OwnerViewId == legend.Id).Select(x => x.E).ToList();
        double right = frames.Max(f => ((LocationPoint)f.Location).Point.X + f.LookupParameter("Cell Width").AsDouble());
        double left = frames.Min(f => ((LocationPoint)f.Location).Point.X);
        var box = DoorWindowSheets.ContentBox(legend)!.Value;
        double k = p.Scale / 304.8;
        Info($"legend content {SpMm((box.Max.X - box.Min.X) / p.Scale * 304.8 / 304.8)} paper, cells {SpMm((right - left) / p.Scale)} paper");
        Check("the legend's extents end at its cells (within 3 mm on paper)", box.Max.X <= right + 3 * k && box.Min.X >= left - 3 * k,
            $"{(box.Max.X - right) / k:0.#} mm past the right, {(left - box.Min.X) / k:0.#} mm past the left");
        if (box.Max.X > right + 3 * k || box.Min.X < left - 3 * k)
            foreach (Element e in new FilteredElementCollector(doc, legend.Id).WhereElementIsNotElementType())
                if (e.get_BoundingBox(legend) is { } bb && (bb.Max.X > right + 3 * k || bb.Min.X < left - 3 * k))
                    Info($"  past the cells: {e.GetType().Name} {e.Name} {(DwTag.Get(e)?.Role.ToString() ?? "")} {(bb.Max.X - right) / k:0.#} / {(left - bb.Min.X) / k:0.#} mm");
        Check("in sync after the build", LiveDoorWindow.Sync(doc, p, write: false) == null, LiveDoorWindow.Sync(doc, p, write: false) ?? "");

        // Elevations are drawn from the back, the way the plan beside them reads (the user, 2026-10-26).
        int ViewOf(Element e) => e.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW)?.AsInteger() ?? 0;
        var elevations = tagged.Where(x => x.T.Role == DwRole.Elevation).Select(x => x.E).ToList();
        Check($"all {elevations.Count} elevations are drawn from the back", elevations.Count > 0 && elevations.All(e => ViewOf(e) == LegendBuilder.ViewElevation),
            string.Join(", ", elevations.Select(ViewOf).Distinct()));
        if (elevations.Count > 0)
        {
            string flipped = DwTag.Get(elevations[0])!.Value.Key;
            using (var t = new Transaction(doc, "front by hand"))
            {
                t.Start();
                elevations[0].get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW).Set(-7);
                t.Commit();
            }
            string? why = LiveDoorWindow.Sync(doc, p, write: false);
            Check("…one turned to the front is seen as out of date", why != null && why.Contains("from the back"), why ?? "in sync");
            rows = LegendBuilder.Run(doc, p);
            Check("…and the rebuild draws it from the back again", DwTag.All(doc, p.SetKey)
                .Where(x => x.T.Role == DwRole.Elevation && x.T.Key == flipped).All(x => ViewOf(x.E) == LegendBuilder.ViewElevation));
            tagged = DwTag.All(doc, p.SetKey);
        }

        // A type whose nominal edges are not found is centred in its cell by its outline (the user's
        // rolling gate, 2026-10-22, sat in the right half of a cell twice its width).
        var unfound = new HashSet<string>(rows.Where(r => r["Note"].StartsWith("Nominal edges not found")).Select(r => r["Item"]), StringComparer.Ordinal);
        int offCentre = 0;
        foreach (DwItem item in items.Where(i => unfound.Contains(i.Mark)))
        {
            Element frame = tagged.First(x => x.T.Key == item.Key && x.T.Role == DwRole.Frame).E;
            Element? elev = tagged.FirstOrDefault(x => x.T.Key == item.Key && x.T.Role == DwRole.Elevation).E;
            if (elev?.get_BoundingBox(doc.GetElement(elev.OwnerViewId) as View) is not { } eb) continue;
            double mid = ((LocationPoint)frame.Location).Point.X + frame.LookupParameter("Cell Width").AsDouble() / 2;
            if (Math.Abs((eb.Min.X + eb.Max.X) / 2 - mid) > 0.5 * k) { offCentre++; Info($"{item.Mark}: elevation {((eb.Min.X + eb.Max.X) / 2 - mid) / k:0.#} mm off the cell's middle"); }
        }
        if (unfound.Count == 0) Info("no type here without its nominal edges; centring not exercised");
        Check("a type without nominal edges is centred by its outline", offCentre == 0, $"{offCentre} of {unfound.Count} off");

        // Unticked: the fields go, and a build does not put them back.
        using (var t = new Transaction(doc, "untick"))
        {
            t.Start();
            DwSchedule.SetSizeQty(s, false, p.SizeLabel, false, p.QtyLabel);
            t.Commit();
        }
        Check("unticking removes both fields", DwSchedule.SizeField(s) == null && DwSchedule.CountField(s) == null);
        LegendBuilder.Run(doc, p);
        Check("…and a build does not add them back", DwSchedule.SizeField(s) == null && DwSchedule.CountField(s) == null);
        Check("…its rows then have no Size or Qty", DoorWindowItems.Rows(p, DwSchedule.Read(s)).All(r => r.Kind == DwRowKind.Cell));
        using (var t = new Transaction(doc, "tick"))
        {
            t.Start();
            DwSchedule.SetSizeQty(s, true, p.SizeLabel, true, p.QtyLabel);
            t.Commit();
        }
        Check("ticking adds them again", DwSchedule.SizeField(s) != null && DwSchedule.CountField(s) != null);
    }

    /// <summary>The level symbol: a triangle on each floor line's left end and its text above.</summary>
    private static void DwRound5Floor(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc);
        LegendBuilder.Run(doc, p);
        var tagged = DwTag.All(doc, p.SetKey);
        double k = p.Scale / 304.8;
        int bad = 0, cells = 0;
        foreach (var g in tagged.Where(x => x.T.Role == DwRole.Frame))
        {
            cells++;
            var mine = tagged.Where(x => x.T.Key == g.T.Key).ToList();
            var lines = mine.Where(x => x.T.Role == DwRole.FloorMark && x.E is DetailCurve).Select(x => ((DetailCurve)x.E).GeometryCurve).ToList();
            var text = mine.Where(x => x.T.Role == DwRole.FloorMark && x.E is TextNote).Select(x => (TextNote)x.E).ToList();
            var floor = mine.First(x => x.T.Role == DwRole.Level).E as DetailCurve;
            double floorY = floor!.GeometryCurve.GetEndPoint(0).Y, cellX = ((LocationPoint)g.E.Location).Point.X;
            double apexY = lines.Count == 3 ? lines.SelectMany(l => new[] { l.GetEndPoint(0).Y, l.GetEndPoint(1).Y }).Min() : double.NaN;
            bool ok = lines.Count == 3 && text.Count == 1 && text[0].Text.Trim() == p.FloorLabel
                      && Math.Abs(apexY - floorY) < 1e-6
                      && lines.All(l => l.GetEndPoint(0).X < cellX + 10 * k)
                      && text[0].Coord.Y > floorY && text[0].Coord.X < cellX + 15 * k;
            if (!ok) { bad++; if (bad <= 3) Info($"{g.T.Key}: {lines.Count} lines, {text.Count} text, apex {(apexY - floorY) / k:0.##} mm off"); }
        }
        Check($"each of {cells} cells has the level symbol and '{p.FloorLabel}' at its floor line's left end", cells > 0 && bad == 0, $"{bad} off");
        Check("…in sync after the build", LiveDoorWindow.Sync(doc, p, write: false) == null, LiveDoorWindow.Sync(doc, p, write: false) ?? "");
        DwExport(doc, (View)tagged.First(x => x.T.Role == DwRole.Legend).E, "dw-round5-floor");

        p.FloorLabel = "";
        LegendBuilder.Run(doc, p);
        Check("a blank label draws no symbol", !DwTag.All(doc, p.SetKey).Any(x => x.T.Role == DwRole.FloorMark));
    }

    /// <summary>A dimension line and a dimension text moved by hand survive a rebuild; reset puts
    /// them back at the Layout's stand-off.</summary>
    private static void DwRound5Dims(Document doc)
    {
        DoorWindowPreset p = DwPreset(doc);
        LegendBuilder.Run(doc, p);
        double k = p.Scale / 304.8;
        var tagged = DwTag.All(doc, p.SetKey);
        DwItem? item = DoorWindowItems.Collect(doc, p, new List<string>()).FirstOrDefault(i => !i.InstanceSized && i.Width > 0 && i.Height > 0);
        Check("an item with dimensions", item != null);
        if (item == null) return;
        Dimension? Find(List<(Element E, DwTagData T)> all, int index) =>
            all.Where(x => x.T.Key == item.Key && x.T.Role == DwRole.Dimension).Select(x => (Dimension)x.E)
                .FirstOrDefault(d => LegendBuilder.MeasureDim(doc, d, k)?.Index == index);
        Dimension width = Find(tagged, DwDim.Width)!, height = Find(tagged, DwDim.Height)!;
        var w0 = LegendBuilder.MeasureDim(doc, width, k)!.Value;
        var h0 = LegendBuilder.MeasureDim(doc, height, k)!.Value;
        Check("the width dimension stands off by the Layout's value", Math.Abs(w0.Off - p.DimOffsetMm) < 0.05, $"{w0.Off:0.##} mm");

        using (var t = new Transaction(doc, "move by hand"))
        {
            t.Start();
            ElementTransformUtils.MoveElement(doc, width.Id, new XYZ(0, 4 * k, 0));
            height.TextPosition = height.TextPosition + new XYZ(0, -6 * k, 0);
            t.Commit();
        }
        var w1 = LegendBuilder.MeasureDim(doc, width, k)!.Value;
        var h1 = LegendBuilder.MeasureDim(doc, height, k)!.Value;
        Info($"moved: width line {w0.Off:0.##} -> {w1.Off:0.##} mm; height text {h0.Ty:0.##} -> {h1.Ty:0.##} mm");

        var rows = LegendBuilder.Run(doc, p);
        var again = DwTag.All(doc, p.SetKey);
        var w2 = LegendBuilder.MeasureDim(doc, Find(again, DwDim.Width)!, k)!.Value;
        var h2 = LegendBuilder.MeasureDim(doc, Find(again, DwDim.Height)!, k)!.Value;
        Check("a rebuild keeps the width dimension where it was moved", Math.Abs(w2.Off - w1.Off) < 0.1, $"{w2.Off:0.##} vs {w1.Off:0.##} mm");
        Check("…and the height dimension's text where it was dragged", Math.Abs(h2.Tx - h1.Tx) < 0.1 && Math.Abs(h2.Ty - h1.Ty) < 0.1,
            $"({h2.Tx:0.##}, {h2.Ty:0.##}) vs ({h1.Tx:0.##}, {h1.Ty:0.##})");
        Check("…the height line itself where it was built", Math.Abs(h2.Off - p.DimOffsetMm) < 0.05, $"{h2.Off:0.##} mm");
        Check("…the results say so", rows.Any(r => r["Item"] == item.Mark && r["Note"].Contains("moved by hand")));
        Check("…remembered in the model", DwDefinitions.Dims(doc, p.SetKey).Count(kv => kv.Value.Moved && kv.Key.StartsWith(item.Key + "#")) == 2);

        // A second rebuild changes nothing.
        LegendBuilder.Run(doc, p);
        var w3 = LegendBuilder.MeasureDim(doc, Find(DwTag.All(doc, p.SetKey), DwDim.Width)!, k)!.Value;
        Check("a second rebuild keeps it", Math.Abs(w3.Off - w1.Off) < 0.1, $"{w3.Off:0.##} mm");

        Check("reset forgets them", LegendBuilder.ForgetHandSetWidths(doc, p) && !DwDefinitions.Dims(doc, p.SetKey).Values.Any(d => d.Moved));
        LegendBuilder.Run(doc, p);
        var reset = DwTag.All(doc, p.SetKey);
        var w4 = LegendBuilder.MeasureDim(doc, Find(reset, DwDim.Width)!, k)!.Value;
        var h4 = LegendBuilder.MeasureDim(doc, Find(reset, DwDim.Height)!, k)!.Value;
        Check("…and the rebuild draws them at the Layout's stand-off", Math.Abs(w4.Off - p.DimOffsetMm) < 0.05
                                                                       && Math.Abs(h4.Tx - h0.Tx) < 0.1 && Math.Abs(h4.Ty - h0.Ty) < 0.1,
            $"width {w4.Off:0.##} mm, height text ({h4.Tx:0.##}, {h4.Ty:0.##}) vs ({h0.Tx:0.##}, {h0.Ty:0.##})");
        Check("…in sync", LiveDoorWindow.Sync(doc, p, write: false) == null, LiveDoorWindow.Sync(doc, p, write: false) ?? "");
    }
    /// <summary>The Layout tab: each field labelled with its number in the sample cell, the three cell
    /// sizes shown as model distances, and a number clicked in the sample cell focusing its field.</summary>
    private static void DwLayoutNumbers()
    {
        using var w = new KhBatchRunner.Drw.Dialogs.DoorWindowWindow(DoorWindowFixture(), new DoorWindowPreset());
        w.Show();
        System.Windows.Forms.Application.DoEvents();
        var labels = new List<string>();
        var placed = new List<System.Windows.Forms.Label>();
        void Walk(System.Windows.Forms.Control c)
        {
            if (c is System.Windows.Forms.Label l) { labels.Add(l.Text); placed.Add(l); }
            foreach (System.Windows.Forms.Control child in c.Controls) Walk(child);
        }
        Walk(w);
        string[] want = { "1. Cell width:", "2. Elevation box height:", "3. Plan box height:", "4. Header strip height:", "5. Wall either side in plan:",
            "6. Line height:", "7. Label column width:", "8. Gap before the value:", "9. Floor line above the box bottom:", "10. Dimensions stand off by:" };
        Check("each Layout field carries its number in the sample cell", want.All(labels.Contains), string.Join(", ", want.Where(x => !labels.Contains(x))));
        // In the tab's top-to-bottom order (the user, 2026-10-22: 8 came before 5, 6 and 7).

        Check("the cell sizes show their model distance at 1:50",
            w.RealDistances.SequenceEqual(new[] { "= 3.50 m", "= 3.50 m", "= 1.50 m" }), string.Join(" / ", w.RealDistances));
        w.ClickPart(KhBatchRunner.Drw.Dialogs.DoorWindowDiagram.Part.PlanBox);
        System.Windows.Forms.Application.DoEvents();
        var d = w.Diagram;
        System.Drawing.Point at = d.KeyPoint(KhBatchRunner.Drw.Dialogs.DoorWindowDiagram.Part.PlanBox);
        Check("the sample cell's key line for 3 is where it was drawn", d.PartAt(at) == KhBatchRunner.Drw.Dialogs.DoorWindowDiagram.Part.PlanBox, at.ToString());
        // In the tab's top-to-bottom order (the user, 2026-10-22: 8 came before 5, 6 and 7); read
        // with the Layout tab showing, so its labels are laid out.
        var numbered = placed.Where(l => want.Contains(l.Text)).OrderBy(l => l.PointToScreen(System.Drawing.Point.Empty).Y)
            .Select(l => int.Parse(l.Text.Substring(0, l.Text.IndexOf('.')))).ToList();
        Check("…numbered 1 to 10 down the tab", numbered.SequenceEqual(Enumerable.Range(1, 10)), string.Join(", ", numbered));
        Check("clicking it focuses the Plan box height field", w.FocusedPart == KhBatchRunner.Drw.Dialogs.DoorWindowDiagram.Part.PlanBox, w.FocusedPart?.ToString() ?? "none");
        // Cancel closes the modeless window (the user, 2026-10-22: it did nothing).
        w.PressCancel();
        System.Windows.Forms.Application.DoEvents();
        Check("Cancel closes the window", !w.Visible && w.IsDisposed, $"visible {w.Visible}, disposed {w.IsDisposed}");
        if (!w.IsDisposed) w.Close();
    }
}
#endif
