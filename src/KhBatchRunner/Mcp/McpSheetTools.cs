#if !EDITION_DRW
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Mcp;

/// <summary>
/// The AI Bridge's sheet tools: placing views on a sheet and lining viewports up. No ribbon tool
/// does this yet, so the logic lives here; the rows they return have the DRW tools' shape
/// (View / Sheet / Status / Note), so the AI reads every tool's result the same way.
/// </summary>
internal static partial class McpTools
{
    private static double PaperFt(double mm) => mm / 304.8;

    private static IReadOnlyDictionary<string, string> SheetRow(string view, string sheet, string status, string note) =>
        new Dictionary<string, string> { ["View"] = view, ["Sheet"] = sheet, ["Status"] = status, ["Note"] = note };

    private static string SheetLabel(ViewSheet s) => s.SheetNumber + " - " + s.Name;

    private static ViewSheet FindSheet(Document doc, string key) =>
        FindView(doc, key) as ViewSheet ?? throw new McpError($"'{key}' is a view, not a sheet. See list_sheets.");

    /// <summary>Optional area_mm {left,bottom,right,top} in paper mm, as feet on the sheet.</summary>
    private static (double L, double B, double R, double T)? Area(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        double get(string k) => (double?)o[k] ?? throw new McpError($"area_mm needs '{k}'.");
        (double l, double b, double r, double t) = (get("left"), get("bottom"), get("right"), get("top"));
        if (r <= l || t <= b) throw new McpError("area_mm: right must be greater than left, and top greater than bottom.");
        return (PaperFt(l), PaperFt(b), PaperFt(r), PaperFt(t));
    }

    /// <summary>The viewport showing a view, or null when it is on no sheet.</summary>
    private static Viewport? ViewportOf(Document doc, View view) =>
        new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(vp => vp.ViewId == view.Id);

    // ------------------------------------------------------------------ create_sheet

    private static Element? TitleBlockOn(Document doc, ViewSheet sheet) =>
        new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().FirstElement();

    private static JsonObject CreateSheet(Document doc, string number, string? name, string? titleBlock, string? like)
    {
        List<ViewSheet> sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsTemplate).ToList();
        if (sheets.Any(s => s.SheetNumber.Equals(number, StringComparison.OrdinalIgnoreCase)))
            throw new McpError($"Sheet number '{number}' is already used.");
        List<FamilySymbol> types = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();

        // Named, else the 'like' sheet's, else the one most sheets carry, else the first loaded.
        FamilySymbol? type;
        string why;
        if (titleBlock != null)
        {
            type = types.FirstOrDefault(t => DrwLookup.TypeLabel(t).Equals(titleBlock, StringComparison.OrdinalIgnoreCase))
                   ?? types.FirstOrDefault(t => t.Name.Equals(titleBlock, StringComparison.OrdinalIgnoreCase))
                   ?? throw new McpError($"No title block '{titleBlock}'. Loaded: " +
                                         string.Join(" | ", types.Select(t => DrwLookup.TypeLabel(t))) + ".");
            why = "as named";
        }
        else if (like != null)
        {
            ViewSheet model = FindSheet(doc, like);
            type = (TitleBlockOn(doc, model) as FamilyInstance)?.Symbol
                   ?? throw new McpError($"Sheet '{SheetLabel(model)}' has no title block to copy.");
            why = "as on " + SheetLabel(model);
        }
        else
        {
            type = sheets.Select(s => (TitleBlockOn(doc, s) as FamilyInstance)?.Symbol).OfType<FamilySymbol>()
                       .GroupBy(s => s.Id).OrderByDescending(g => g.Count()).Select(g => g.First()).FirstOrDefault()
                   ?? types.FirstOrDefault();
            why = "the one most sheets use";
        }

        using var t = new Transaction(doc, "Create sheet");
        t.Start();
        ViewSheet sheet = ViewSheet.Create(doc, type?.Id ?? ElementId.InvalidElementId);
        sheet.SheetNumber = number;
        if (name != null) sheet.Name = name;
        t.Commit();
        return new JsonObject
        {
            ["sheet"] = SheetLabel(sheet),
            ["id"] = sheet.Id.Value,
            ["title_block"] = type == null ? "none (no title block is loaded)" : $"{DrwLookup.TypeLabel(type)} ({why})"
        };
    }

    // ------------------------------------------------------------------ place_viewports

    private static JsonObject PlaceViewports(Document doc, ViewSheet sheet, List<View> views, string layout, double gapMm,
        double marginMm, (double L, double B, double R, double T)? area, string? viewportType)
    {
        if (views.Count == 0) throw new McpError("Name the views to place.");
        if (layout is not ("grid" or "row" or "column")) throw new McpError("layout is grid, row or column.");
        string sheetName = SheetLabel(sheet);
        var rows = new List<IReadOnlyDictionary<string, string>>();

        // The usable area: given, or the title block less the margin, or the sheet's own outline.
        string areaFrom;
        (double L, double B, double R, double T) box;
        if (area is { } given) { box = given; areaFrom = "area_mm"; }
        else if (new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType().FirstElement() is { } titleBlock
                 && titleBlock.get_BoundingBox(sheet) is { } tb)
        {
            double m = PaperFt(marginMm);
            (double L, double B, double R, double T) frame = SheetFrame.DrawingFrame(titleBlock, sheet, tb, out string found);
            box = (frame.L + m, frame.B + m, frame.R - m, frame.T - m);
            areaFrom = "title block drawing area" + found;
        }
        else
        {
            BoundingBoxUV o = sheet.Outline;
            box = (o.Min.U, o.Min.V, o.Max.U, o.Max.V);
            areaFrom = "sheet outline (no title block)";
        }
        var centre = new XYZ((box.L + box.R) / 2, (box.B + box.T) / 2, 0);
        var occupiedReport = new JsonArray();
        List<(double L, double B, double R, double T)> occupied = Occupied(doc, sheet, occupiedReport);

        using var t = new Transaction(doc, "Place viewports");
        t.Start();

        var placed = new List<(View View, Viewport Vp)>();
        foreach (View v in views.Distinct())
        {
            if (v is ViewSchedule) { rows.Add(SheetRow(v.Name, sheetName, "Skipped", "Schedules are not placed by this tool.")); continue; }
            if (v is ViewSheet) { rows.Add(SheetRow(v.Name, sheetName, "Skipped", "A sheet cannot go on a sheet.")); continue; }
            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id))
            {
                Viewport? on = ViewportOf(doc, v);
                string why = on == null ? "Revit will not place this view (an empty legend or drafting view?)."
                    : on.SheetId == sheet.Id ? "Already on this sheet."
                    : "Already on sheet " + (doc.GetElement(on.SheetId) is ViewSheet other ? SheetLabel(other) : "?") +
                      " (a view sits on one sheet only; duplicate it first).";
                rows.Add(SheetRow(v.Name, sheetName, "Skipped", why));
                continue;
            }
            Viewport vp = Viewport.Create(doc, sheet.Id, v.Id, centre);
            placed.Add((v, vp));
        }

        string typeNote = "";
        if (viewportType != null && placed.Count > 0)
        {
            ElementId typeId = placed[0].Vp.GetValidTypes()
                .FirstOrDefault(id => doc.GetElement(id)?.Name.Equals(viewportType, StringComparison.OrdinalIgnoreCase) == true)
                ?? ElementId.InvalidElementId;
            if (typeId == ElementId.InvalidElementId)
                rows.Add(SheetRow("", sheetName, "Warning", $"No viewport type '{viewportType}'; Revit's default was kept. Types: " +
                    string.Join(" | ", placed[0].Vp.GetValidTypes().Select(id => doc.GetElement(id)?.Name).Where(n => n != null))));
            else
            {
                foreach ((View _, Viewport vp) in placed) vp.ChangeTypeId(typeId);
                typeNote = $" Type {viewportType}.";
            }
        }
        doc.Regenerate();

        double gap = PaperFt(gapMm);
        double width = box.R - box.L, height = box.T - box.B;

        // Shelf layout for an empty sheet: rows filled left to right up to the width allowed, each row
        // centred, the block centred in the area. A row is one shelf; a column puts every viewport on its own.
        double maxRow = layout == "row" ? double.MaxValue : layout == "column" ? 0 : width;
        var shelves = new List<List<(View View, Viewport Vp, double W, double H)>>();
        foreach ((View v, Viewport vp) in placed)
        {
            Outline o = vp.GetBoxOutline();
            var item = (v, vp, o.MaximumPoint.X - o.MinimumPoint.X, o.MaximumPoint.Y - o.MinimumPoint.Y);
            List<(View View, Viewport Vp, double W, double H)>? shelf = shelves.LastOrDefault();
            if (shelf == null || shelf.Sum(s => s.W) + gap * shelf.Count + item.Item3 > maxRow) shelves.Add(shelf = new());
            shelf.Add(item);
        }

        double blockH = shelves.Sum(s => s.Max(i => i.H)) + gap * Math.Max(0, shelves.Count - 1);
        double blockW = shelves.Count == 0 ? 0 : shelves.Max(s => s.Sum(i => i.W) + gap * (s.Count - 1));
        bool overflows = blockW > width + 1e-6 || blockH > height + 1e-6;

        // A sheet that already carries viewports, schedules or notes — or views too many for the shelf
        // layout: each new view takes the first free spot that fits (top-left first, in the order
        // given), never covering what is there; a view with no room left is reported, not placed,
        // so the caller can put it on the next sheet.
        // Content inside the drawing area: a new sheet's title-block revision table sits in the strip.
        bool hadContent = occupied.Any(o => o.R > box.L && o.L < box.R && o.T > box.B && o.B < box.T);
        if (hadContent || overflows)
        {
            foreach ((View v, Viewport vp) in placed)
            {
                // Fitted with its title, which hangs outside the box.
                (double L, double B, double R, double T) fp = Footprint(vp);
                XYZ c = vp.GetBoxCenter();
                double w = fp.R - fp.L, h = fp.T - fp.B;
                if (FreeSpot(box, occupied, w, h, gap) is { } spot)
                {
                    vp.SetBoxCenter(new XYZ(spot.X + (c.X - fp.L), spot.Y - (fp.T - c.Y), 0));
                    doc.Regenerate();
                    occupied.Add(Footprint(vp));
                    rows.Add(SheetRow(v.Name, sheetName, "Created",
                        $"{Math.Round(w * 304.8)} x {Math.Round(h * 304.8)} mm on paper, 1:{v.Scale}; placed in the first free space.{typeNote}"));
                }
                else
                {
                    doc.Delete(vp.Id);
                    rows.Add(SheetRow(v.Name, sheetName, "Skipped",
                        $"No free space of {Math.Round(w * 304.8)} x {Math.Round(h * 304.8)} mm left on this sheet (it keeps clear of what is already there). Use another sheet, a smaller scale or a tighter crop."));
                }
            }
            t.Commit();
            return Rows(rows, new JsonObject
            {
                ["sheet"] = sheetName,
                ["layout"] = hadContent ? "free space (the sheet already had content)"
                                        : $"free space (the views do not all fit as one {layout})",
                ["already_on_sheet"] = occupiedReport,
                ["area"] = $"{areaFrom}: {Math.Round(width * 304.8)} x {Math.Round(height * 304.8)} mm, x {Math.Round(box.L * 304.8)}-{Math.Round(box.R * 304.8)}, y {Math.Round(box.B * 304.8)}-{Math.Round(box.T * 304.8)}"
            });
        }

        double y = centre.Y + blockH / 2;
        foreach (List<(View View, Viewport Vp, double W, double H)> shelf in shelves)
        {
            double rowH = shelf.Max(i => i.H);
            double x = centre.X - (shelf.Sum(i => i.W) + gap * (shelf.Count - 1)) / 2;
            foreach ((View v, Viewport vp, double w, double h) in shelf)
            {
                // Top-aligned within the row.
                vp.SetBoxCenter(new XYZ(x + w / 2, y - h / 2, 0));
                rows.Add(SheetRow(v.Name, sheetName, "Created",
                    $"{Math.Round(w * 304.8)} x {Math.Round(h * 304.8)} mm on paper, 1:{v.Scale}.{typeNote}"));
                x += w + gap;
            }
            y -= rowH + gap;
        }
        t.Commit();

        return Rows(rows, new JsonObject
        {
            ["sheet"] = sheetName,
            ["layout"] = layout,
            ["area"] = $"{areaFrom}: {Math.Round(width * 304.8)} x {Math.Round(height * 304.8)} mm, x {Math.Round(box.L * 304.8)}-{Math.Round(box.R * 304.8)}, y {Math.Round(box.B * 304.8)}-{Math.Round(box.T * 304.8)}"
        });
    }

    /// <summary>A viewport's box and its title, as one rectangle on the sheet.</summary>
    private static (double L, double B, double R, double T) Footprint(Viewport vp)
    {
        Outline box = vp.GetBoxOutline();
        Outline label;
        try { label = vp.GetLabelOutline(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { label = box; }   // a viewport type with no title
        return (Math.Min(box.MinimumPoint.X, label.MinimumPoint.X), Math.Min(box.MinimumPoint.Y, label.MinimumPoint.Y),
                Math.Max(box.MaximumPoint.X, label.MaximumPoint.X), Math.Max(box.MaximumPoint.Y, label.MaximumPoint.Y));
    }

    /// <summary>What already sits on the sheet, title block aside: viewports with their titles,
    /// schedules, text, images, lines — anything a new viewport must not cover.</summary>
    private static List<(double L, double B, double R, double T)> Occupied(Document doc, ViewSheet sheet, JsonArray? report = null)
    {
        var list = new List<(double L, double B, double R, double T)>();
        foreach (Element e in new FilteredElementCollector(doc, sheet.Id).WhereElementIsNotElementType())
        {
            // The title block bounds the area; a guide grid spans the whole sheet and covers nothing.
            long cat = e.Category?.Id.Value ?? 0;
            if (cat == (long)BuiltInCategory.OST_TitleBlocks || cat == (long)BuiltInCategory.OST_GuideGrid) continue;
            (double L, double B, double R, double T) r;
            string name;
            if (e is Viewport vp) { r = Footprint(vp); name = "viewport " + (doc.GetElement(vp.ViewId)?.Name ?? "?"); }
            else if (e.get_BoundingBox(sheet) is { } bb)
            {
                r = (bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y);
                name = e is ScheduleSheetInstance si ? "schedule " + (doc.GetElement(si.ScheduleId)?.Name ?? "?")
                    : (e.Category?.Name ?? e.GetType().Name) + " " + e.Id.Value;
            }
            else continue;
            list.Add(r);
            report?.Add($"{name}: x {Math.Round(r.L * 304.8)}-{Math.Round(r.R * 304.8)}, y {Math.Round(r.B * 304.8)}-{Math.Round(r.T * 304.8)} mm");
        }
        return list;
    }

    /// <summary>The top-left corner of the first spot (scanning top to bottom, left to right in 5 mm
    /// steps) where a w × h box fits inside the area, a gap clear of everything occupied.</summary>
    private static XYZ? FreeSpot((double L, double B, double R, double T) area,
        List<(double L, double B, double R, double T)> occupied, double w, double h, double gap)
    {
        double step = PaperFt(5);
        for (double top = area.T; top - h >= area.B - 1e-9; top -= step)
            for (double left = area.L; left + w <= area.R + 1e-9; left += step)
            {
                double l = left, r = left + w, b = top - h, t = top;
                bool clear = occupied.All(o => r + gap <= o.L || l - gap >= o.R || t + gap <= o.B || b - gap >= o.T);
                if (clear) return new XYZ(left, top, 0);
            }
        return null;
    }

    // ------------------------------------------------------------------ align_viewports

    /// <summary>Where a model point lands on the viewport's sheet.</summary>
    private static XYZ SheetPoint(View view, Viewport vp, XYZ model)
    {
        Transform toProjection = view.GetModelToProjectionTransforms().First().GetModelToProjectionTransform();
        return vp.GetProjectionToSheetTransform().OfPoint(toProjection.OfPoint(model));
    }

    /// <summary>The point the reference's crop is centred on, or the project origin when it has no crop.</summary>
    private static XYZ AnchorPoint(View view)
    {
        if (!view.CropBoxActive) return XYZ.Zero;
        BoundingBoxXYZ crop = view.CropBox;
        return crop.Transform.OfPoint((crop.Min + crop.Max) / 2);
    }

    private static JsonObject AlignViewports(Document doc, string referenceKey, List<string> viewKeys, string mode)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the views whose viewports move (or ['*'] for the rest of the reference's sheet).");
        string[] modes = { "model", "left", "right", "top", "bottom", "center_x", "center_y" };
        if (!modes.Contains(mode)) throw new McpError("mode is one of " + string.Join(", ", modes) + ".");

        View reference = FindView(doc, referenceKey);
        Viewport refVp = ViewportOf(doc, reference) ?? throw new McpError($"'{reference.Name}' is not on a sheet.");
        var rows = new List<IReadOnlyDictionary<string, string>>();

        var targets = new List<(View View, Viewport Vp)>();
        if (IsAll(viewKeys))
        {
            foreach (Viewport vp in new FilteredElementCollector(doc, refVp.SheetId).OfClass(typeof(Viewport)).Cast<Viewport>())
                if (vp.Id != refVp.Id && doc.GetElement(vp.ViewId) is View v) targets.Add((v, vp));
        }
        else
        {
            foreach (View v in FindViews(doc, viewKeys).Distinct())
            {
                if (v.Id == reference.Id) continue;
                if (ViewportOf(doc, v) is { } vp) targets.Add((v, vp));
                else rows.Add(SheetRow(v.Name, "", "Skipped", "Not on a sheet."));
            }
        }
        if (targets.Count == 0 && rows.Count == 0) throw new McpError("No other viewport to align.");

        using var t = new Transaction(doc, "Align viewports");
        t.Start();
        doc.Regenerate();
        XYZ anchor = AnchorPoint(reference);
        XYZ refSheet = mode == "model" ? SheetPoint(reference, refVp, anchor) : XYZ.Zero;
        Outline refBox = refVp.GetBoxOutline();

        foreach ((View v, Viewport vp) in targets)
        {
            string sheet = doc.GetElement(vp.SheetId) is ViewSheet s ? SheetLabel(s) : "";
            XYZ move;
            if (mode == "model")
            {
                string? why = v.Scale != reference.Scale ? $"1:{v.Scale}, the reference is 1:{reference.Scale}"
                    : (reference is ViewPlan) != (v is ViewPlan) ?"a plan and a section/elevation show the model differently"
                    : reference is not ViewPlan && reference.ViewDirection.DotProduct(v.ViewDirection) < 1 - 1e-6 ? "it does not face the same way"
                    : null;
                if (why != null) { rows.Add(SheetRow(v.Name, sheet, "Skipped", why + "; try an edge mode.")); continue; }
                move = refSheet - SheetPoint(v, vp, anchor);
            }
            else
            {
                Outline o = vp.GetBoxOutline();
                double cx(Outline b) => (b.MinimumPoint.X + b.MaximumPoint.X) / 2;
                double cy(Outline b) => (b.MinimumPoint.Y + b.MaximumPoint.Y) / 2;
                move = mode switch
                {
                    "left" => new XYZ(refBox.MinimumPoint.X - o.MinimumPoint.X, 0, 0),
                    "right" => new XYZ(refBox.MaximumPoint.X - o.MaximumPoint.X, 0, 0),
                    "top" => new XYZ(0, refBox.MaximumPoint.Y - o.MaximumPoint.Y, 0),
                    "bottom" => new XYZ(0, refBox.MinimumPoint.Y - o.MinimumPoint.Y, 0),
                    "center_x" => new XYZ(cx(refBox) - cx(o), 0, 0),
                    _ => new XYZ(0, cy(refBox) - cy(o), 0)
                };
            }
            move = new XYZ(move.X, move.Y, 0);
            double mm = move.GetLength() * 304.8;
            if (mm < 0.05) { rows.Add(SheetRow(v.Name, sheet, "Skipped", "Already aligned.")); continue; }
            vp.SetBoxCenter(vp.GetBoxCenter() + move);
            rows.Add(SheetRow(v.Name, sheet, "Changed", $"Moved {mm:0.#} mm on paper."));
        }
        t.Commit();

        return Rows(rows, new JsonObject
        {
            ["reference"] = reference.Name,
            ["reference_sheet"] = doc.GetElement(refVp.SheetId) is ViewSheet rs ? SheetLabel(rs) : null,
            ["mode"] = mode
        });
    }
}
#endif
