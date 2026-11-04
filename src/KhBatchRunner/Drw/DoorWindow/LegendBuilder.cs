using System.IO;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>
/// Builds (and rebuilds) a door/window schedule as legends: one cell per Type Mark — frame, the
/// type drawn as a legend component in elevation and in plan, its width/height/sill dimensioned,
/// header labels, and one KH_DW_Row per table row — laid out <see cref="DoorWindowPreset.Columns"/>
/// across and <see cref="DoorWindowPreset.RowsPerLegend"/> down per legend.
///
/// <para>A rerun rebuilds everything the schedule made (found by <see cref="DwTag"/>), reusing its
/// legends so viewports on sheets survive. Header labels someone dragged keep their offset from
/// their cell's corner. Everything new is made BEFORE the old is deleted: an existing legend
/// component is the only way to make another, and the old ones may be the only ones there are.</para>
///
/// <para>Two API facts this rests on (spike 2026-10-20, memory legend-component-api): a dimension
/// on a legend component's own geometry never resolves, so dimensions go on invisible detail lines
/// laid on the edges found exactly the type's width/height apart; and a model without a legend gets
/// one from the bundled seed, as the API cannot make a legend from nothing.</para>
/// </summary>
internal static class LegendBuilder
{
    public const string HeaderFamily = "KH_DW_Header";
    public const string RowFamily = "KH_DW_Row";
    public const string MarkFamily = "KH_DW Header Center";
    public const string DescriptionFamily = "KH_DW Header Left";
    public const string TitleFamily = "KH_DW Label Left";
    public const string SeedFile = "KH_DW_Seed.rvt";
    public static readonly string[] Families = { HeaderFamily, RowFamily, MarkFamily, DescriptionFamily, TitleFamily };
    /// <summary>The bundled KH_DW families' version (<see cref="BundledFamilies.VersionParameter"/>):
    /// 2 = header text 3 mm, table text 2 mm, the Value label never wrapping (2026-10-22); 3 = that
    /// label's box narrow again, "Wrap between parameters only" on instead: v2's 1000 mm box was
    /// counted in the legend's extents and pushed its centre a metre right (the user, 2026-10-22).</summary>
    public const int FamilyVersion = 3;

    /// <summary>The elevation is drawn as seen from the back (LEGEND_COMPONENT_VIEW -6; -7 is the
    /// front): the front view is mirrored against the plan legend beside it (the user, 2026-10-26:
    /// "front are not aligne with plan legend"). No option.</summary>
    internal const int ViewElevation = -6;
    private const int ViewFloorPlan = -8;
    public static readonly string[] Columns = { "Legend", "Item", "Status", "Note" };

    private sealed class Ctx
    {
        public Document Doc = null!;
        public DoorWindowPreset P = null!;
        public double K;                       // model feet per paper mm
        public Element SourceComp = null!;
        public View SourceView = null!;
        public FamilySymbol Header = null!, Row = null!, Mark = null!, Description = null!, Title = null!;
        public double RowLineFt, LabelWidthFt;
        /// <summary>The cell being built: its row's elevation box.</summary>
        public double ElevMm;
        public DimensionType? DimType;
        public GraphicsStyle? Invisible;
        public Dictionary<(string Key, DwRole Role), XYZ> KeptOffsets = new();
        public List<IReadOnlyDictionary<string, string>> Rows = new();
        /// <summary>The set's schedule as read for this build (null: none, only Size and Qty rows).</summary>
        public DwScheduleData? Schedule;
        /// <summary>How much wider Revit draws the table font than GDI measures it (<see cref="Extents"/>).</summary>
        public double Fit = 1;
        /// <summary>Each dimension as built and as moved by hand (<see cref="DwDim.KeyOf"/>).</summary>
        public Dictionary<string, DwDim> Dims = new(StringComparer.Ordinal);
        /// <summary>The dimensions this build drew, by their <see cref="DwDim.KeyOf"/>.</summary>
        public List<(Dimension D, string Key)> NewDims = new();
        /// <summary>The level text's type (null: no level symbol).</summary>
        public TextNoteType? FloorText;
        public double Mm(double mm) => mm * K;
    }

    /// <summary>Builds or rebuilds the schedule <paramref name="p"/> names, as one undo step.</summary>
    public static List<IReadOnlyDictionary<string, string>> Run(Document doc, DoorWindowPreset p)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        using var group = new TransactionGroup(doc, "Enlarge Door & Window");
        group.Start();
        try
        {
            // The set's own schedule first: made from the starter, or put back in shape.
            var made = new List<string>();
            using (var t = new Transaction(doc, "Door & window schedule"))
            {
                t.Start();
                DwSchedule.Ensure(doc, p, made);
                t.Commit();
            }
            foreach (string m in made) rows.Add(DrwRows.Row(Columns, "", "", "Changed", "Schedule: " + m));
            var warnings = new List<string>();
            DwScheduleData? schedule = DoorWindowItems.Schedule(doc, p, warnings);
            List<DwItem> items = DoorWindowItems.Collect(doc, p, warnings, schedule);
            foreach (string w in warnings) rows.Add(DrwRows.Row(Columns, "", "", "Warning", w));
            if (items.Count == 0)
            {
                rows.Add(DrwRows.Row(Columns, "", "", "Skipped", $"No placed {(p.Category == DwCategory.Door ? "door" : "window")} types with a Type Mark."));
                group.Assimilate();
                return rows;
            }

            (List<string> loaded, List<string> updated) = BundledFamilies.LoadMissingOrOlder(doc, Families, FamilyVersion);
            if (loaded.Count > 0) rows.Add(DrwRows.Row(Columns, "", "", "Loaded", string.Join(", ", loaded)));
            if (updated.Count > 0) rows.Add(DrwRows.Row(Columns, "", "", "Updated", "Newer copies reloaded: " + string.Join(", ", updated)));
            string? missing = Families.FirstOrDefault(f => Symbol(doc, f) == null);
            if (missing != null)
            {
                rows.Add(DrwRows.Row(Columns, "", "", "Failed", $"Family '{missing}' is not in the model and this build has no copy of it."));
                group.RollBack();
                return rows;
            }

            View? seedLegend = null;
            var seedFamilies = new List<ElementId>();
            if (AnyComponent(doc) == null)
            {
                var before = new HashSet<ElementId>(new FilteredElementCollector(doc).OfClass(typeof(Family)).ToElementIds());
                seedLegend = CopySeed(doc, rows);
                seedFamilies = new FilteredElementCollector(doc).OfClass(typeof(Family)).ToElementIds().Where(id => !before.Contains(id)).ToList();
                if (seedLegend == null) { group.RollBack(); return rows; }
            }

            using (Transaction t = DrwWarningSwallower.Start(doc, "Build door & window legends"))
            {
                var ctx = Context(doc, p, rows);
                ctx.Schedule = schedule;
                Build(ctx, items);
                if (seedLegend != null) DropSeed(doc, seedLegend, seedFamilies, rows);
                t.Commit();
            }
            group.Assimilate();
            // The live triggers follow the set's schedule fields (and its Live setting).
            LiveDoorWindow.Watch(doc);
        }
        catch (Exception ex)
        {
            rows.Add(DrwRows.Row(Columns, "", "", "Failed", ex.Message));
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
        }
        return rows;
    }

    // ------------------------------------------------------------------ setup

    private static Ctx Context(Document doc, DoorWindowPreset p, List<IReadOnlyDictionary<string, string>> rows)
    {
        var c = new Ctx
        {
            Doc = doc,
            P = p,
            K = Math.Max(1, p.Scale) / 304.8,
            Rows = rows,
            Header = Symbol(doc, HeaderFamily)!,
            Row = Symbol(doc, RowFamily)!,
            Mark = Symbol(doc, MarkFamily)!,
            Description = Symbol(doc, DescriptionFamily)!,
            Title = Symbol(doc, TitleFamily)!,
        };
        foreach (FamilySymbol s in new[] { c.Header, c.Row, c.Mark, c.Description, c.Title })
            if (!s.IsActive) s.Activate();
        c.SourceComp = AnyComponent(doc)!;
        c.SourceView = (View)doc.GetElement(c.SourceComp.OwnerViewId);
        c.DimType = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
            .Where(d => d.StyleType == DimensionStyleType.Linear)
            .OrderByDescending(d => d.Name == p.DimensionTypeName).FirstOrDefault();
        c.Invisible = InvisibleLines(doc);
        if (p.FloorLabel.Trim().Length > 0) c.FloorText = FloorTextType(doc);

        c.RowLineFt = c.Mm(p.RowLineMm);
        c.LabelWidthFt = c.Mm(p.LabelWidthMm);
        return c;
    }

    private static FamilySymbol? Symbol(Document doc, string family) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(s => string.Equals(s.FamilyName, family, StringComparison.OrdinalIgnoreCase));

    private static Element? AnyComponent(Document doc) =>
        new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LegendComponents)
            .WhereElementIsNotElementType().FirstOrDefault();

    private static GraphicsStyle? InvisibleLines(Document doc)
    {
        Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        foreach (Category sub in lines.SubCategories)
            if (sub.Id.Value == (long)BuiltInCategory.OST_InvisibleLines)
                return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
        return null;
    }

    // ------------------------------------------------------------------ the seed

    /// <summary>The bundled seed's legend, copied in (its own transaction). The copy brings one door
    /// family; <see cref="DropSeed"/> removes both once the schedule has components of its own.</summary>
    private static View? CopySeed(Document doc, List<IReadOnlyDictionary<string, string>> rows)
    {
        string folder = Path.Combine(Path.GetTempPath(), "DrwHelper-Seed-" + Guid.NewGuid().ToString("N"));
        string? path = BundledFamilies.Extract(SeedFile, folder);
        if (path == null)
        {
            rows.Add(DrwRows.Row(Columns, "", "", "Failed", "The model has no legend to start from, and this build carries no seed. Make one legend (View > Legends) and run again."));
            return null;
        }
        Document seed = doc.Application.OpenDocumentFile(path);
        try
        {
            View legend = new FilteredElementCollector(seed).OfClass(typeof(View)).Cast<View>().First(v => v.ViewType == ViewType.Legend && !v.IsTemplate);
            using var t = new Transaction(doc, "Copy legend seed");
            t.Start();
            View copied = (View)doc.GetElement(ElementTransformUtils.CopyElements(seed, new List<ElementId> { legend.Id }, doc,
                Transform.Identity, CopyOptions()).First(id => doc.GetElement(id) is View));
            t.Commit();
            rows.Add(DrwRows.Row(Columns, copied.Name, "", "Seeded", "The model had no legend; one was copied in to start from and is removed again."));
            return copied;
        }
        finally
        {
            seed.Close(false);
            try { Directory.Delete(folder, true); } catch { /* temp */ }
        }
    }

    /// <summary>Removes the seed legend and every family its copy brought in (its door, the
    /// template's view title…) that nothing in the model uses now.</summary>
    private static void DropSeed(Document doc, View seedLegend, List<ElementId> families, List<IReadOnlyDictionary<string, string>> rows)
    {
        doc.Delete(seedLegend.Id);
        foreach (ElementId fid in families.Where(id => doc.GetElement(id) != null))
        {
            bool used = new FilteredElementCollector(doc).WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Any(i => i.Symbol.Family.Id == fid);
            if (used) continue;
            using var st = new SubTransaction(doc);
            st.Start();
            try { doc.Delete(fid); st.Commit(); }
            catch { st.RollBack(); rows.Add(DrwRows.Row(Columns, "", "", "Warning", $"The seed's family '{doc.GetElement(fid)?.Name}' could not be removed; purge it if unwanted.")); }
        }
    }

    /// <summary>Copies between models keep the destination's types: Revit would otherwise stop and
    /// ask (the OK / Cancel the user saw while the seed was being tested).</summary>
    public static CopyPasteOptions CopyOptions()
    {
        var o = new CopyPasteOptions();
        o.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
        return o;
    }

    private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }

    // ------------------------------------------------------------------ build

    private sealed class Cell
    {
        public DwItem Item = null!;
        public List<(string Label, string Value, int Lines)> Table = new();
        public double TableFt;
        /// <summary>The cell's width, paper mm.</summary>
        public double WidthMm;
    }

    private static void Build(Ctx c, List<DwItem> items)
    {
        Document doc = c.Doc;
        DoorWindowPreset p = c.P;
        List<(Element E, DwTagData T)> old = DwTag.All(doc, p.SetKey);
        RememberMovedLabels(c, old);

        // Cell widths set by hand: those remembered from earlier builds, and any frame whose Cell
        // Width someone changed since the last one (the user, 2026-10-22: "need to memory the cell
        // width that have been adjust"). An item no longer scheduled drops its width.
        View? firstOld = old.Where(o => o.T.Role == DwRole.Legend).OrderBy(o => o.T.Index).Select(o => o.E).FirstOrDefault() as View;
        Dictionary<string, double> adjusted = firstOld != null ? DwDefinitions.Widths(firstOld) : new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, mm) in HandSetWidths(doc, old)) adjusted[key] = mm;
        var scheduled = new HashSet<string>(items.Select(i => i.Key), StringComparer.Ordinal);
        foreach (string gone in adjusted.Keys.Where(k => !scheduled.Contains(k)).ToList()) adjusted.Remove(gone);

        // Dimensions moved by hand: kept, as dragged header labels are (the user, 2026-10-22).
        if (firstOld != null) c.Dims = DwDefinitions.Dims(firstOld);
        double oldOffset = DwDefinitions.Load(doc).FirstOrDefault(d => d.SetKey == p.SetKey)?.DimOffsetMm ?? p.DimOffsetMm;
        RememberMovedDims(c, old, oldOffset);
        foreach (string gone in c.Dims.Keys.Where(k => !scheduled.Contains(k.Substring(0, Math.Max(0, k.LastIndexOf('#'))))).ToList()) c.Dims.Remove(gone);

        // Merged cells: a door wider than a cell spans several side by side; each row of cells takes
        // the elevation box its tallest item needs, so rows line up and short rows stay short.
        double lineMm = p.RowLineMm;

        // With a title block, the frame decides cells across and how much a legend holds: one
        // legend per sheet. Read on a sheet made and rolled back, so the schedule's own sheets are
        // only made (or removed) once it is known how many there are.
        FamilySymbol? titleBlock = p.TitleBlockName.Length > 0 ? DoorWindowSheets.Find(doc, p.TitleBlockName) : null;
        if (p.TitleBlockName.Length > 0 && titleBlock == null)
            c.Rows.Add(DrwRows.Row(Columns, "", "", "Warning", $"Title block '{p.TitleBlockName}' is not in this model; legends are made without sheets."));
        int columns = p.Columns;
        double maxHeightMm = 0;
        if (titleBlock != null)
        {
            using var st = new SubTransaction(doc);
            st.Start();
            if (!titleBlock.IsActive) titleBlock.Activate();
            var probeSheet = ViewSheet.Create(doc, titleBlock.Id);
            var frame = DoorWindowSheets.FrameOn(doc, probeSheet);
            st.RollBack();
            if (frame is { } f) (columns, maxHeightMm) = DwSheets.Grid(DoorWindowSheets.ToMm(f), p);
        }

        // In plan a legend component also draws wall either side of the opening, so it can be wider
        // than the type: whichever is wider, elevation (with its dimensions) or plan, sets the width.
        // Then each value is broken into lines at the cell's own value column.
        Dictionary<ElementId, DwExtent> drawn = Extents(doc, c.SourceView, c.SourceComp, items, p, out c.Fit);
        var cells = items.Select(i =>
        {
            DwExtent e = drawn[i.Primary.Id];
            double need = DoorWindowLayout.NeedWidthMm(i.Width / c.K, e.PlanWidth / c.K, e.ElevationWidth / c.K, p);
            double? set = adjusted.TryGetValue(i.Key, out double a) ? a : null;
            double width = DoorWindowLayout.CellWidthMm(need, set, p, columns, out bool spills);
            if (spills)
                c.Rows.Add(DrwRows.Row(Columns, "", i.Mark, "Info", $"Cell width set by hand ({set:0} mm) is narrower than its drawing ({need:0} mm) at 1:{p.Scale}; kept, the drawing may cross the cell edge."));
            double valueMm = DoorWindowLayout.ValueWidthMm(width, p);
            return new Cell
            {
                Item = i,
                WidthMm = width,
                Table = DoorWindowItems.Rows(p, c.Schedule).Select(r =>
                {
                    (string text, int lines) = DwWrap.Wrap(DoorWindowItems.Value(i, r, p, c.Schedule), p.RowFont, p.RowTextMm, valueMm, c.Fit);
                    return (r.Label, text, lines);
                }).ToList()
            };
        }).ToList();
        foreach (Cell cell in cells) cell.TableFt = cell.Table.Sum(r => r.Lines) * c.RowLineFt;
        var needs = cells.Select(x => new DwNeed(
            Math.Max(1, (int)Math.Round(x.WidthMm / Math.Max(1, p.CellWidthMm))),
            DoorWindowLayout.ElevationNeedMm((Math.Max(x.Item.Height, drawn[x.Item.Primary.Id].ElevationHeight) + x.Item.Sill) / c.K, p),
            x.Table.Sum(r => r.Lines) * lineMm,
            x.WidthMm)).ToList();
        DwPack pack = DoorWindowLayout.Pack(needs, p, columns, maxHeightMm);
        foreach (DwSlot slot in pack.Slots)
        {
            string mark = cells[slot.Item].Item.Mark;
            if (adjusted.ContainsKey(cells[slot.Item].Item.Key))
                c.Rows.Add(DrwRows.Row(Columns, "", mark, "Info", $"Cell width set by hand: {slot.WidthMm:0} mm (kept; Layout > Reset clears it)."));
            else if (slot.Span > 1)
                c.Rows.Add(DrwRows.Row(Columns, "", mark, "Info", $"Wider than a cell at 1:{p.Scale}: spans {slot.Span} cells."));
        }
        foreach (var (sheetRows, si) in pack.Rows.Select((r, i) => (r, i)))
            foreach (var (r, ri) in sheetRows.Select((r, i) => (r, i)).Where(x => x.r.ElevationMm > p.ElevationBoxMm))
                c.Rows.Add(DrwRows.Row(Columns, "", "", "Info", $"Legend {si + 1}, row {ri + 1}: elevation box grown to {r.ElevationMm:0} mm for its tallest item."));

        // Legends: the schedule's own, in order, then new ones duplicated from any legend.
        List<View> legends = old.Where(o => o.T.Role == DwRole.Legend).OrderBy(o => o.T.Index).Select(o => (View)o.E).ToList();
        // Legends and sheets are reused (viewports and sheet numbers survive); the rest is rebuilt.
        var oldIds = new HashSet<ElementId>(old.Where(o => o.T.Role is not (DwRole.Legend or DwRole.Sheet)).Select(o => o.E.Id));
        int needed = pack.Sheets;
        HashSet<string> names = DrwNaming.ExistingViewNames(doc);
        while (legends.Count < needed)
        {
            View legend = (View)doc.GetElement(c.SourceView.Duplicate(ViewDuplicateOption.Duplicate));
            legend.Name = DrwNaming.Unique(DrwNaming.Clean(p.LegendName.Replace("{n}", (legends.Count + 1).ToString())), names);
            DwTag.Set(legend, new DwTagData(p.SetKey, "", DwRole.Legend, legends.Count + 1));
            legends.Add(legend);
            c.Rows.Add(DrwRows.Row(Columns, legend.Name, "", "Created", "New legend"));
        }
        foreach (View legend in legends) legend.Scale = Math.Max(1, p.Scale);

        // Two passes around ONE regenerate: every cell's frame, labels and components first, then
        // what needs them drawn (edges, plan centre). A regenerate costs the whole model (0.65 s on
        // Snowdon); two per cell made a 32-item build take 54 s (2026-10-21).
        var started = new List<(DwSlot Slot, Placed Placed)>();
        foreach (DwSlot slot in pack.Slots)
        {
            Cell cell = cells[slot.Item];
            DwPackRow row = pack.Rows[slot.Sheet][slot.Row];
            View legend = legends[slot.Sheet];
            // A table shorter than its row's tallest: its last row takes the rest, so the cell's
            // outline meets its neighbours'.
            int spare = (int)Math.Round((row.TableMm - needs[slot.Item].TableMm) / lineMm);
            if (spare > 0 && cell.Table.Count > 0)
            {
                var last = cell.Table[cell.Table.Count - 1];
                cell.Table[cell.Table.Count - 1] = (last.Label, last.Value, last.Lines + spare);
            }
            c.ElevMm = row.ElevationMm;
            var origin = new XYZ(c.Mm(slot.XMm), -c.Mm(row.TopMm), 0);
            try { started.Add((slot, StartCell(c, legend, origin, c.Mm(slot.WidthMm), cell))); }
            catch (Exception ex) { c.Rows.Add(DrwRows.Row(Columns, legend.Name, cell.Item.Mark, "Failed", ex.Message)); }
        }
        doc.Regenerate();
        foreach ((DwSlot slot, Placed placed) in started)
        {
            c.ElevMm = pack.Rows[slot.Sheet][slot.Row].ElevationMm;
            try
            {
                FinishCell(c, placed);
                c.Rows.Add(DrwRows.Row(Columns, placed.Legend.Name, placed.Cell.Item.Mark, "Created", string.Join("; ", placed.Cell.Item.Warnings)));
            }
            catch (Exception ex)
            {
                c.Rows.Add(DrwRows.Row(Columns, placed.Legend.Name, placed.Cell.Item.Mark, "Failed", ex.Message));
            }
        }

        // Only now the old content goes: the new components were copied while it still existed.
        foreach (ElementId id in oldIds)
            if (doc.GetElement(id) != null) doc.Delete(id);

        // Texts moved by hand go back where they were, once the new dimensions are drawn; then each
        // dimension's place as built is recorded, so the next build can tell what was moved.
        doc.Regenerate();
        bool texts = false;
        foreach (var (d, key) in c.NewDims)
            if (c.Dims.TryGetValue(key, out DwDim? kept) && kept.Tx is double tx && kept.Ty is double ty)
            {
                try { d.TextPosition = d.Origin + new XYZ(c.Mm(tx), c.Mm(ty), 0); texts = true; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { /* a dimension that cannot move its text */ }
            }
        if (texts) doc.Regenerate();
        foreach (var (d, key) in c.NewDims)
            if (MeasureDim(doc, d, c.K) is { } m)
            {
                DwDim dim = c.Dims.TryGetValue(key, out DwDim? had) ? had : c.Dims[key] = new DwDim();
                (dim.BuiltOff, dim.BuiltTx, dim.BuiltTy) = (m.Off, m.Tx, m.Ty);
            }
        foreach (var g in c.Dims.Where(kv => kv.Value.Moved).GroupBy(kv => kv.Key.Substring(0, kv.Key.LastIndexOf('#'))))
            c.Rows.Add(DrwRows.Row(Columns, "", g.Key.Substring(2), "Info", $"{g.Count()} dimension(s) moved by hand: kept (Layout > Reset clears them)."));
        // What the live updater keeps current, kept with the schedule itself, with this model's
        // hand edits and the text fit its wrapping needs.
        DwDefinitions.Save(legends[0], p, adjusted, c.Fit, c.Dims);

        // Sheets: one per legend, each legend at its frame's top-left. Done before spare legends
        // are judged, so a spare sheet removed here takes its spare legend's viewport with it.
        if (titleBlock != null)
        {
            List<ViewSheet> sheets = DoorWindowSheets.Ensure(doc, p, titleBlock, needed, old, c.Rows);
            for (int i = 0; i < needed; i++) DoorWindowSheets.Place(doc, sheets[i], legends[i], p, c.Rows);
            c.Rows.Add(DrwRows.Row(Columns, "", "", "Info",
                $"{cells.Count} items on {needed} sheet(s): " + string.Join(" + ", pack.ItemsPerSheet()) + $", {columns} cells across."));
        }

        // Legends no longer needed: emptied ones go unless a sheet shows them.
        foreach (View spare in legends.Skip(needed))
        {
            bool onSheet = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Any(vp => vp.ViewId == spare.Id);
            if (onSheet) c.Rows.Add(DrwRows.Row(Columns, spare.Name, "", "Warning", "No longer needed but placed on a sheet; left empty."));
            else { c.Rows.Add(DrwRows.Row(Columns, spare.Name, "", "Deleted", "No longer needed")); doc.Delete(spare.Id); }
        }
    }

    /// <summary>
    /// The frames whose Cell Width someone changed by hand since the build that made them: each
    /// frame's tag index holds the width it was built with (0.1 mm; 0 on frames from before
    /// 2026-10-22, never read as changed). Item key → the width it has now, paper mm.
    /// </summary>
    internal static IEnumerable<(string Key, double Mm)> HandSetWidths(Document doc, List<(Element E, DwTagData T)> tagged)
    {
        foreach (var (e, t) in tagged)
        {
            if (t.Role != DwRole.Frame || t.Index <= 0) continue;
            if (FrameWidthMm(doc, e) is not double now) continue;
            if (Math.Abs(now - t.Index / 10.0) > 0.5) yield return (t.Key, now);
        }
    }

    /// <summary>Forgets set <paramref name="p"/>'s hand edits, cell widths and moved dimensions
    /// (its own transaction; a build then lays it out in whole cells, dimensions at the Layout's
    /// stand-off). Each frame and dimension is first marked as built where it is now, so that build
    /// does not take it as moved by hand once more. False when the set is not built in this model.</summary>
    internal static bool ForgetHandSetWidths(Document doc, DoorWindowPreset p)
    {
        if (DwDefinitions.FirstLegend(doc, p.SetKey) is not View first) return false;
        DoorWindowPreset saved = DwDefinitions.Load(doc).FirstOrDefault(d => d.SetKey == p.SetKey) ?? p;
        using var t = new Transaction(doc, "Door & window: reset hand edits");
        t.Start();
        List<(Element E, DwTagData T)> tagged = DwTag.All(doc, p.SetKey);
        foreach (var (e, tag) in tagged.Where(x => x.T.Role == DwRole.Frame))
            if (FrameWidthMm(doc, e) is double mm)
                DwTag.Set(e, tag with { Index = (int)Math.Round(mm * 10) });
        var dims = new Dictionary<string, DwDim>(StringComparer.Ordinal);
        foreach (var (e, tag) in tagged.Where(x => x.T.Role == DwRole.Dimension))
            if (e is Dimension d && doc.GetElement(d.OwnerViewId) is View v && MeasureDim(doc, d, Math.Max(1, v.Scale) / 304.8) is { } m)
                dims[DwDim.KeyOf(tag.Key, m.Index)] = new DwDim { BuiltOff = m.Off, BuiltTx = m.Tx, BuiltTy = m.Ty };
        DwDefinitions.Save(first, saved, new Dictionary<string, double>(StringComparer.Ordinal), null, dims);
        t.Commit();
        return true;
    }

    /// <summary>
    /// The old dimensions someone moved since the last build: a line further from (or nearer to) the
    /// edge it measures than it was built, or its text dragged off its place (both by more than
    /// 0.2 mm on paper). Kept in <see cref="Ctx.Dims"/> for the cells drawn next. A dimension from a
    /// build before these were recorded is judged against <paramref name="oldOffset"/> (its text is
    /// not known, so not kept).
    /// </summary>
    private static void RememberMovedDims(Ctx c, List<(Element E, DwTagData T)> old, double oldOffset)
    {
        foreach (var (e, t) in old)
        {
            if (t.Role != DwRole.Dimension || e is not Dimension d || c.Doc.GetElement(d.OwnerViewId) is not View v) continue;
            if (MeasureDim(c.Doc, d, Math.Max(1, v.Scale) / 304.8) is not { } m) continue;
            string key = DwDim.KeyOf(t.Key, m.Index);
            if (!c.Dims.TryGetValue(key, out DwDim? dim))
            {
                if (Math.Abs(m.Off - oldOffset) > 0.2) c.Dims[key] = new DwDim { BuiltOff = oldOffset, Off = m.Off };
                continue;
            }
            if (Math.Abs(m.Off - dim.BuiltOff) > 0.2) dim.Off = m.Off;
            if (Math.Abs(m.Tx - dim.BuiltTx) > 0.2 || Math.Abs(m.Ty - dim.BuiltTy) > 0.2) (dim.Tx, dim.Ty) = (m.Tx, m.Ty);
        }
    }

    /// <summary>
    /// A cell dimension as drawn, paper mm (<paramref name="k"/>: model feet per paper mm): which one
    /// (<see cref="DwDim"/>: a horizontal one is the width; a vertical one the sill when one of its
    /// lines is the long floor line, else the height), how far its line stands off the edge it
    /// measures (above the top / right of the right edge), and its text from the line's middle.
    /// </summary>
    internal static (int Index, double Off, double Tx, double Ty)? MeasureDim(Document doc, Dimension d, double k)
    {
        if (d.Curve is not Line line || d.Origin is not XYZ origin) return null;
        var refs = new List<Line>();
        foreach (Reference r in d.References)
            if (doc.GetElement(r) is DetailCurve { GeometryCurve: Line l }) refs.Add(l);
        if (refs.Count < 2) return null;
        int index;
        double off;
        if (Math.Abs(line.Direction.X) > 0.9)
        {
            index = DwDim.Width;
            off = origin.Y - refs.Max(l => Math.Max(l.GetEndPoint(0).Y, l.GetEndPoint(1).Y));
        }
        else
        {
            Line shortest = refs.OrderBy(l => l.Length).First();
            index = refs.Max(l => l.Length) > 1.5 * shortest.Length ? DwDim.Sill : DwDim.Height;
            off = origin.X - Math.Max(shortest.GetEndPoint(0).X, shortest.GetEndPoint(1).X);
        }
        XYZ text;
        try { text = d.TextPosition - origin; }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { text = XYZ.Zero; }
        return (index, off / k, text.X / k, text.Y / k);
    }

    /// <summary>A frame's Cell Width on paper, mm (its legend's scale), or null.</summary>
    internal static double? FrameWidthMm(Document doc, Element frame) =>
        frame.LookupParameter("Cell Width") is { StorageType: StorageType.Double } w && doc.GetElement(frame.OwnerViewId) is View v
            ? w.AsDouble() * 304.8 / Math.Max(1, v.Scale) : null;

    /// <summary>A header label's offset from its frame's corner, for each label someone moved
    /// (any offset other than where the builder puts it).</summary>
    private static void RememberMovedLabels(Ctx c, List<(Element E, DwTagData T)> old)
    {
        // Judged against where that cell's OWN frame put the label (its box sizes are read off the
        // frame), so a box that grew since does not make every label look hand-moved.
        var frames = old.Where(o => o.T.Role == DwRole.Frame && o.E.Location is LocationPoint)
            .GroupBy(o => o.T.Key).ToDictionary(g => g.Key, g => g.First().E);
        foreach (var (e, t) in old)
        {
            if (t.Role is not (DwRole.Mark or DwRole.Description or DwRole.Title1 or DwRole.Title2)) continue;
            if (e.Location is not LocationPoint lp || !frames.TryGetValue(t.Key, out Element frame)) continue;
            XYZ offset = lp.Point - ((LocationPoint)frame.Location).Point;
            double Len(string n) => frame.LookupParameter(n)?.AsDouble() ?? 0;
            XYZ was = DefaultOffset(c, t.Role, Len("Box 1 Height"), Len("Box 2 Height"), Len("Header Height"));
            if (offset.DistanceTo(was) > c.Mm(0.2)) c.KeptOffsets[(t.Key, t.Role)] = offset;
        }
    }

    /// <summary>Where the builder puts a header label, from its frame's top-left, in feet. The KH_DW
    /// labels hang from their middle (the first export put every one across the line it was meant
    /// to sit under), so each is placed by its centre line.</summary>
    private static XYZ DefaultOffset(Ctx c, DwRole role, double box1, double box2, double header)
    {
        double headerMid = box1 + box2 + header / 2;
        return role switch
        {
            DwRole.Mark => new XYZ(c.Mm(8), -headerMid, 0),
            DwRole.Description => new XYZ(c.Mm(17), -headerMid, 0),
            DwRole.Title1 => new XYZ(c.Mm(1.5), -c.Mm(2.5), 0),
            DwRole.Title2 => new XYZ(c.Mm(1.5), -(box1 + c.Mm(2.5)), 0),
            _ => XYZ.Zero
        };
    }

    /// <summary>A cell after <see cref="StartCell"/>: what <see cref="FinishCell"/> places by.</summary>
    private sealed class Placed
    {
        public View Legend = null!;
        public XYZ O = XYZ.Zero;
        public double W;
        public Cell Cell = null!;
        /// <summary>Null for an instance-sized type: drawn as an outline.</summary>
        public Element? Elevation, Plan;
    }

    /// <summary>The cell's frame, header labels and its two components, not yet placed.</summary>
    private static Placed StartCell(Ctx c, View legend, XYZ o, double w, Cell cell)
    {
        Document doc = c.Doc;
        DoorWindowPreset p = c.P;
        DwItem item = cell.Item;
        void Tag(Element e, DwRole role, int index = 0) => DwTag.Set(e, new DwTagData(p.SetKey, item.Key, role, index));
        double elevH = c.Mm(c.ElevMm), planH = c.Mm(p.PlanBoxMm);

        // Frame.
        FamilyInstance frame = doc.Create.NewFamilyInstance(o, c.Header, legend);
        SetLen(frame, "Cell Width", w);
        SetLen(frame, "Box 1 Height", p.ElevationFirst ? elevH : planH);
        SetLen(frame, "Box 2 Height", p.ElevationFirst ? planH : elevH);
        SetLen(frame, "Header Height", c.Mm(p.HeaderMm));
        // The width it was built with (0.1 mm): a different one later was set by hand.
        Tag(frame, DwRole.Frame, (int)Math.Round(w / c.K * 10));

        // Header labels, loose so they can be dragged.
        Label(c, legend, c.Mark, o, DwRole.Mark, item.Mark, Tag, item.Key);
        Label(c, legend, c.Description, o, DwRole.Description, DoorWindowItems.Description(item, p, c.Schedule), Tag, item.Key);
        Label(c, legend, c.Title, o, DwRole.Title1, p.ElevationFirst ? p.ElevationTitle : p.PlanTitle, Tag, item.Key);
        Label(c, legend, c.Title, o, DwRole.Title2, p.ElevationFirst ? p.PlanTitle : p.ElevationTitle, Tag, item.Key);

        // An instance-sized type has no legend component: Revit would draw it at the family's
        // default size, not the placed one. FinishCell draws its outline instead.
        if (item.InstanceSized) return new Placed { Legend = legend, O = o, W = w, Cell = cell };
        Element elev = Component(c, legend, item.Primary, ViewElevation);
        Tag(elev, DwRole.Elevation);
        Element plan = Component(c, legend, item.Primary, ViewFloorPlan, HostLength(item, c.P, c.K));
        Tag(plan, DwRole.Plan);
        return new Placed { Legend = legend, O = o, W = w, Cell = cell, Elevation = elev, Plan = plan };
    }

    /// <summary>The host wall a plan component draws: the opening and <see cref="DoorWindowPreset.WallStubMm"/>
    /// of paper either side (the default 7' was often shorter than the door, the user, 2026-10-22), feet.</summary>
    internal static double HostLength(DwItem item, DoorWindowPreset p, double k) =>
        Math.Max(item.Width, 0) + 2 * Math.Max(0, p.WallStubMm) * k;

    /// <summary>After the regenerate: the components placed by what they draw, dimensions, rows.</summary>
    private static void FinishCell(Ctx c, Placed placed)
    {
        Document doc = c.Doc;
        DoorWindowPreset p = c.P;
        View legend = placed.Legend;
        XYZ o = placed.O;
        double w = placed.W;
        Cell cell = placed.Cell;
        DwItem item = cell.Item;
        void Tag(Element e, DwRole role, int index = 0) => DwTag.Set(e, new DwTagData(p.SetKey, item.Key, role, index));

        double elevTop = p.ElevationFirst ? 0 : c.Mm(p.PlanBoxMm);
        double planTop = p.ElevationFirst ? c.Mm(c.ElevMm) : 0;
        double elevH = c.Mm(c.ElevMm), planH = c.Mm(p.PlanBoxMm);
        double tableTop = c.Mm(c.ElevMm + p.PlanBoxMm + p.HeaderMm);

        // Elevation: the type's nominal edges stand on a floor line, centred across the box.
        double floorY = o.Y - elevTop - elevH + c.Mm(p.FloorGapMm);
        double targetLeft = o.X + w / 2 - item.Width / 2, targetBottom = floorY + item.Sill;
        if (placed.Elevation is { } elev)
        {
            (double left, double bottom, bool exact) = Edges(legend, elev, item.Width, item.Height);
            if (!exact) c.Rows.Add(DrwRows.Row(Columns, legend.Name, item.Mark, "Warning", "Nominal edges not found in the elevation; dimensioned to its outline instead."));
            ElementTransformUtils.MoveElement(doc, elev.Id, new XYZ(targetLeft - left, targetBottom - bottom, 0));
        }
        else
        {
            Outline(c, legend, targetLeft, targetBottom, item.Width, item.Height, Tag);
            OutlinePlan(c, legend, new XYZ(o.X + w / 2, o.Y - planTop - planH / 2, 0), item, Tag);
        }

        DetailCurve level = doc.Create.NewDetailCurve(legend, Line.CreateBound(new XYZ(o.X + c.Mm(2), floorY, 0), new XYZ(o.X + w - c.Mm(2), floorY, 0)));
        Tag(level, DwRole.Level);
        FloorMark(c, legend, new XYZ(o.X + c.Mm(5), floorY, 0), Tag);

        // Dimensions on invisible lines laid on the nominal edges — none for a type of no size.
        if (item.Width <= 0 || item.Height <= 0)
            c.Rows.Add(DrwRows.Row(Columns, legend.Name, item.Mark, "Warning", "Width or height not found; not dimensioned."));
        else Dimensions(c, legend, o, w, item, targetLeft, targetBottom, floorY, level, Tag);
        PlanAndRows(c, legend, o, w, cell, placed.Plan, planTop, planH, tableTop, Tag);
    }

    private static void Dimensions(Ctx c, View legend, XYZ o, double w, DwItem item, double targetLeft, double targetBottom,
        double floorY, DetailCurve level, Action<Element, DwRole, int> Tag)
    {
        DoorWindowPreset p = c.P;
        double x0 = targetLeft, x1 = targetLeft + item.Width, y0 = targetBottom, y1 = targetBottom + item.Height;
        DetailCurve lL = DimLine(c, legend, new XYZ(x0, y0, 0), new XYZ(x0, y1, 0), Tag);
        DetailCurve lR = DimLine(c, legend, new XYZ(x1, y0, 0), new XYZ(x1, y1, 0), Tag);
        DetailCurve lB = DimLine(c, legend, new XYZ(x0, y0, 0), new XYZ(x1, y0, 0), Tag);
        DetailCurve lT = DimLine(c, legend, new XYZ(x0, y1, 0), new XYZ(x1, y1, 0), Tag);
        // Where each stands off: as moved by hand, else the Layout's.
        double Off(int index) => c.Mm(c.Dims.TryGetValue(DwDim.KeyOf(item.Key, index), out DwDim? d) && d.Off is double mm ? mm : p.DimOffsetMm);
        double ow = Off(DwDim.Width), oh = Off(DwDim.Height), os = Off(DwDim.Sill);
        Dim(c, legend, lL, lR, new XYZ(x0, y1 + ow, 0), new XYZ(x1, y1 + ow, 0), item.Key, DwDim.Width, Tag);
        Dim(c, legend, lB, lT, new XYZ(x1 + oh, y0, 0), new XYZ(x1 + oh, y1, 0), item.Key, DwDim.Height, Tag);
        if (item.Sill > 1e-6) Dim(c, legend, level, lB, new XYZ(x1 + os, floorY, 0), new XYZ(x1 + os, y0, 0), item.Key, DwDim.Sill, Tag);
    }

    /// <summary>
    /// The level symbol at the floor line's left end (the user, 2026-10-22): a triangle of three
    /// lines, 2.5 mm across and 2 mm tall on paper, its point on the line at <paramref name="at"/>,
    /// and <see cref="DoorWindowPreset.FloorLabel"/> beside it above the line. None when the label is blank.
    /// </summary>
    private static void FloorMark(Ctx c, View legend, XYZ at, Action<Element, DwRole, int> tag)
    {
        if (c.FloorText == null) return;
        XYZ left = at + new XYZ(-c.Mm(1.25), c.Mm(2), 0), right = at + new XYZ(c.Mm(1.25), c.Mm(2), 0);
        foreach ((XYZ a, XYZ b) in new[] { (at, left), (left, right), (right, at) })
            tag(c.Doc.Create.NewDetailCurve(legend, Line.CreateBound(a, b)), DwRole.FloorMark, 0);
        var options = new TextNoteOptions(c.FloorText.Id)
        {
            HorizontalAlignment = HorizontalTextAlignment.Left,
            VerticalAlignment = VerticalTextAlignment.Bottom
        };
        TextNote note = TextNote.Create(c.Doc, legend.Id, at + new XYZ(c.Mm(2), c.Mm(0.6), 0), c.P.FloorLabel.Trim(), options);
        tag(note, DwRole.FloorMark, 1);
    }

    /// <summary>The level text's type: "KH_DW 2mm" (TH Sarabun New 2 mm, transparent), made from the
    /// model's first text type when missing.</summary>
    private static TextNoteType? FloorTextType(Document doc)
    {
        const string name = "KH_DW 2mm";
        var types = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
        if (types.FirstOrDefault(t => t.Name == name) is { } had) return had;
        if (types.FirstOrDefault() is not { } from) return null;
        var made = (TextNoteType)from.Duplicate(name);
        made.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(2 / 304.8);
        made.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set("TH Sarabun New");
        made.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1);
        made.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.Set(1.0);
        return made;
    }

    private static void PlanAndRows(Ctx c, View legend, XYZ o, double w, Cell cell, Element? plan, double planTop, double planH,
        double tableTop, Action<Element, DwRole, int> Tag)
    {
        Document doc = c.Doc;
        // Plan: centred in its box (drawn by the regenerate between the passes).
        if (plan?.get_BoundingBox(legend) is { } pb)
        {
            XYZ centre = (pb.Min + pb.Max) / 2;
            ElementTransformUtils.MoveElement(doc, plan.Id,
                new XYZ(o.X + w / 2 - centre.X, o.Y - planTop - planH / 2 - centre.Y, 0));
        }

        // Table rows.
        double y = o.Y - tableTop;
        for (int r = 0; r < cell.Table.Count; r++)
        {
            var (label, value, lines) = cell.Table[r];
            FamilyInstance row = doc.Create.NewFamilyInstance(new XYZ(o.X, y, 0), c.Row, legend);
            SetLen(row, "Row Width", w);
            SetLen(row, "Label Width", c.LabelWidthFt);
            SetLen(row, "Line Height", c.RowLineFt);
            SetLen(row, "Text Inset", c.Mm(c.P.TextInsetMm));
            row.LookupParameter("Lines")?.Set(lines);
            // The table's last row draws the cell's thick bottom border (KH_DW_Row, line weight 3).
            row.LookupParameter("Is Last Row")?.Set(r == cell.Table.Count - 1 ? 1 : 0);
            row.LookupParameter("KH_DW Label")?.Set(label);
            row.LookupParameter("KH_DW Value")?.Set(value);
            Tag(row, DwRole.Row, r);
            y -= lines * c.RowLineFt;
        }
    }

    /// <summary>A rectangle of detail lines: an instance-sized type's elevation at its placed size.</summary>
    private static void Outline(Ctx c, View legend, double x0, double y0, double width, double height, Action<Element, DwRole, int> tag)
    {
        XYZ a = new(x0, y0, 0), b = new(x0 + width, y0, 0), d = new(x0 + width, y0 + height, 0), e = new(x0, y0 + height, 0);
        foreach ((XYZ p, XYZ q) in new[] { (a, b), (b, d), (d, e), (e, a) })
            tag(c.Doc.Create.NewDetailCurve(legend, Line.CreateBound(p, q)), DwRole.Outline, 0);
    }

    /// <summary>An instance-sized type in plan: the opening's jambs across its host wall's thickness
    /// (100 mm with none), and the wall's faces running <see cref="DoorWindowPreset.WallStubMm"/> out
    /// either side, closed at their ends; centred on <paramref name="centre"/>.</summary>
    private static void OutlinePlan(Ctx c, View legend, XYZ centre, DwItem item, Action<Element, DwRole, int> tag)
    {
        double half = item.Width / 2, t = (item.WallThickness > 0 ? item.WallThickness : 100 / 304.8) / 2;
        double stub = c.Mm(Math.Max(0, c.P.WallStubMm));
        void Line2(double x1, double y1, double x2, double y2) =>
            tag(c.Doc.Create.NewDetailCurve(legend, Autodesk.Revit.DB.Line.CreateBound(centre + new XYZ(x1, y1, 0), centre + new XYZ(x2, y2, 0))), DwRole.Outline, 0);
        foreach (int side in new[] { -1, 1 })
        {
            Line2(side * half, -t, side * half, t);                               // jamb
            if (stub <= 0) continue;
            Line2(side * half, t, side * (half + stub), t);                       // wall faces
            Line2(side * half, -t, side * (half + stub), -t);
            Line2(side * (half + stub), -t, side * (half + stub), t);             // wall end
        }
    }

    private static void Label(Ctx c, View legend, FamilySymbol symbol, XYZ o, DwRole role, string text,
        Action<Element, DwRole, int> tag, string key)
    {
        double elev = c.Mm(c.ElevMm), plan = c.Mm(c.P.PlanBoxMm);
        XYZ offset = c.KeptOffsets.TryGetValue((key, role), out XYZ kept) ? kept
            : DefaultOffset(c, role, c.P.ElevationFirst ? elev : plan, c.P.ElevationFirst ? plan : elev, c.Mm(c.P.HeaderMm));
        FamilyInstance label = c.Doc.Create.NewFamilyInstance(o + offset, symbol, legend);
        label.LookupParameter("KH_DW Label")?.Set(text);
        tag(label, role, 0);
    }

    /// <summary>
    /// What each item's drawn type takes as legend components draw it, in feet:
    /// <list type="bullet">
    /// <item>its width in plan (wall either side of the opening included);</item>
    /// <item>in elevation, twice the larger reach either side of its nominal centre, and its height
    /// above its nominal bottom.</item>
    /// </list>
    /// A frame, trim, a sidelight, or a family drawn at its default size (width set per instance) can
    /// draw wider or taller than the nominal size; cells sized by the nominal size overlapped their
    /// neighbours (the user, 2026-10-21). Placed in <paramref name="sourceView"/>, measured, deleted.
    /// Inside a transaction.
    /// </summary>
    /// <para>An instance-sized type is not placed: its outline is its placed size, the plan its
    /// width and the wall stubs. <paramref name="fit"/>: how much wider Revit draws the table font
    /// than GDI measures it, read off two text notes placed in the same regenerate (1 when they
    /// cannot be).</para>
    internal static Dictionary<ElementId, DwExtent> Extents(Document doc, View sourceView, Element sourceComp, IEnumerable<DwItem> items,
        DoorWindowPreset p, out double fit)
    {
        double k = Math.Max(1, p.Scale) / 304.8;
        var all = items.GroupBy(i => i.Primary.Id).Select(g => g.First()).ToList();
        // All placed, one regenerate, all measured: a regenerate per type took 21 s on Snowdon.
        var probes = all.Where(i => !i.InstanceSized)
            .Select(i => (Item: i,
                          Plan: Component(doc, sourceView, sourceComp, sourceView, i.Primary, ViewFloorPlan, HostLength(i, p, k)),
                          Front: Component(doc, sourceView, sourceComp, sourceView, i.Primary, ViewElevation))).ToList();
        TextProbe? text = TextProbe.Place(doc, sourceView, p);
        doc.Regenerate();
        fit = text?.Fit(sourceView) ?? 1;
        text?.Delete();
        var extents = new Dictionary<ElementId, DwExtent>();
        foreach (DwItem i in all.Where(i => i.InstanceSized))
            extents[i.Primary.Id] = new DwExtent(HostLength(i, p, k), i.Width, i.Height);
        foreach (var (item, plan, front) in probes)
        {
            BoundingBoxXYZ? pb = plan.get_BoundingBox(sourceView);
            BoundingBoxXYZ? fb = front.get_BoundingBox(sourceView);
            double width = 0, height = 0;
            if (fb != null)
            {
                // As FinishCell places it: the found left edge (or, not found, where the outline's
                // middle puts it) goes to the cell's centre less half the nominal width. Measured from that anchor,
                // so a door whose edges are not found (type 68 on Snowdon spilled 454 mm) is sized
                // by where it will really sit.
                (double left, double bottom, bool _) = Edges(sourceView, front, item.Width, item.Height);
                double centre = left + Math.Max(0, item.Width) / 2;
                width = 2 * Math.Max(centre - fb.Min.X, fb.Max.X - centre);
                height = fb.Max.Y - bottom;
            }
            extents[item.Primary.Id] = new DwExtent(pb == null ? 0 : pb.Max.X - pb.Min.X, width, height);
        }
        doc.Delete(probes.SelectMany(x => new[] { x.Plan.Id, x.Front.Id }).ToList());
        return extents;
    }

    /// <summary>Any legend component in the model and its legend (what new ones are copied from).</summary>
    internal static (Element Comp, View Legend)? Source(Document doc) =>
        AnyComponent(doc) is { } comp ? (comp, (View)doc.GetElement(comp.OwnerViewId)) : null;

    private static Element Component(Ctx c, View legend, FamilySymbol type, int viewValue, double hostLength = 0) =>
        Component(c.Doc, c.SourceView, c.SourceComp, legend, type, viewValue, hostLength);

    private static Element Component(Document doc, View sourceView, Element sourceComp, View legend, FamilySymbol type, int viewValue,
        double hostLength = 0)
    {
        ElementId id = ElementTransformUtils.CopyElements(sourceView, new List<ElementId> { sourceComp.Id }, legend,
            Transform.Identity, new CopyPasteOptions()).First();
        Element comp = doc.GetElement(id);
        comp.get_Parameter(BuiltInParameter.LEGEND_COMPONENT).Set(type.Id);
        comp.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW).Set(viewValue);
        if (hostLength > 0 && comp.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_LENGTH) is { IsReadOnly: false } host)
            host.Set(hostLength);
        return comp;
    }

    /// <summary>
    /// Two text notes in the table font, one a sample and one the sample twice, placed before the
    /// build's one regenerate: the difference of their widths against GDI's says how much wider
    /// Revit draws the font (box padding cancels out). Latin and Thai each; the wider fit counts.
    /// </summary>
    private sealed class TextProbe
    {
        private const string Latin = "Clear Glass Tempered Aluminium 1200 ", Thai = "กระจกใสนิรภัย วัสดุกรอบบาน ";
        private readonly Document _doc;
        private readonly DoorWindowPreset _p;
        private readonly TextNoteType _type;
        private readonly List<(string Text, TextNote One, TextNote Two)> _notes = new();

        private TextProbe(Document doc, DoorWindowPreset p, TextNoteType type) { _doc = doc; _p = p; _type = type; }

        public static TextProbe? Place(Document doc, View view, DoorWindowPreset p)
        {
            try
            {
                if (new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElement() is not TextNoteType any) return null;
                var type = (TextNoteType)any.Duplicate("KH_DW measure " + Guid.NewGuid().ToString("N").Substring(0, 8));
                type.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set(p.RowFont);
                type.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(p.RowTextMm / 304.8);
                type.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.Set(1.0);
                type.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.Set(0);
                type.get_Parameter(BuiltInParameter.TEXT_STYLE_ITALIC)?.Set(0);
                var probe = new TextProbe(doc, p, type);
                double y = 0;
                foreach (string sample in new[] { Latin, Thai })
                {
                    probe._notes.Add((sample,
                        TextNote.Create(doc, view.Id, new XYZ(0, y -= 10, 0), sample.TrimEnd(), type.Id),
                        TextNote.Create(doc, view.Id, new XYZ(0, y -= 10, 0), (sample + sample).TrimEnd(), type.Id)));
                }
                return probe;
            }
            catch (Exception ex)
            {
                Diagnostics.RunTrace.Mark("Door & Window: text not measured: " + ex.Message);
                return null;
            }
        }

        /// <summary>After the regenerate: Revit's width over GDI's (1 when unreadable), within 0.3–4.
        /// Revit sizes text by its capital height, GDI by the em: TH Sarabun New's small capitals
        /// against its tall em make Revit draw it over twice as wide (2026-10-22).</summary>
        public double Fit(View view)
        {
            double fit = 0;
            foreach (var (text, one, two) in _notes)
            {
                if (one.get_BoundingBox(view) is not { } a || two.get_BoundingBox(view) is not { } b) continue;
                double revit = ((b.Max.X - b.Min.X) - (a.Max.X - a.Min.X)) * 304.8 / Math.Max(1, view.Scale);
                double gdi = DwWrap.MeasureMm((text + text).TrimEnd(), _p.RowFont, _p.RowTextMm) - DwWrap.MeasureMm(text.TrimEnd(), _p.RowFont, _p.RowTextMm);
                if (revit > 0 && gdi > 0) fit = Math.Max(fit, revit / gdi);
            }
            return fit <= 0 ? 1 : Math.Max(0.3, Math.Min(4, fit));
        }

        public void Delete()
        {
            try
            {
                _doc.Delete(_notes.SelectMany(n => new[] { n.One.Id, n.Two.Id }).ToList());
                _doc.Delete(_type.Id);
            }
            catch (Exception ex) { Diagnostics.RunTrace.Mark("Door & Window: measuring notes not removed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Where the type's nominal left edge and bottom are in the elevation: the pair of vertical
    /// edges exactly its width apart (the outermost such pair), and of horizontal edges its height
    /// apart. Frames and trim stand outside the nominal size, so the outline would be wrong; it is
    /// the fallback only. With no left edge found, the left is put half the width short of the
    /// outline's middle, so the drawing is centred by its outline: a rolling gate with no Width
    /// parameter was anchored by its outline's left edge at the cell's middle, sat in the right
    /// half, and was given a cell twice its width (373 mm at 1:50; the user, 2026-10-22).
    /// </summary>
    private static (double Left, double Bottom, bool Exact) Edges(View legend, Element comp, double width, double height)
    {
        double Centred(BoundingBoxXYZ? b) => b == null ? 0 : (b.Min.X + b.Max.X) / 2 - Math.Max(0, width) / 2;
        if (width <= 0 || height <= 0)
        {
            BoundingBoxXYZ? box = comp.get_BoundingBox(legend);
            return (Centred(box), box?.Min.Y ?? 0, false);
        }
        var opts = new Options { View = legend, ComputeReferences = false };
        var lines = new List<Line>();
        Collect(comp.get_Geometry(opts), lines, 0);
        var vert = lines.Where(l => Math.Abs(l.Direction.Y) > 0.999).Select(l => l.Origin.X).ToList();
        var horiz = lines.Where(l => Math.Abs(l.Direction.X) > 0.999).Select(l => l.Origin.Y).ToList();
        const double tol = 1 / 304.8;
        double? left = vert.Where(a => vert.Any(b => Math.Abs(b - a - width) < tol)).Select(a => (double?)a).Min();
        double? bottom = horiz.Where(a => horiz.Any(b => Math.Abs(b - a - height) < tol)).Select(a => (double?)a).Min();
        if (left != null && bottom != null) return (left.Value, bottom.Value, true);
        BoundingBoxXYZ? bb = comp.get_BoundingBox(legend);
        return (left ?? Centred(bb), bottom ?? bb?.Min.Y ?? 0, false);
    }

    private static void Collect(GeometryElement? ge, List<Line> lines, int depth)
    {
        if (ge == null || depth > 4) return;
        foreach (GeometryObject g in ge)
        {
            switch (g)
            {
                case GeometryInstance gi: Collect(gi.GetInstanceGeometry(), lines, depth + 1); break;
                case Line l: lines.Add(l); break;
                case Solid s:
                    foreach (Edge e in s.Edges) if (e.AsCurve() is Line el) lines.Add(el);
                    break;
            }
        }
    }

    private static DetailCurve DimLine(Ctx c, View legend, XYZ a, XYZ b, Action<Element, DwRole, int> tag)
    {
        DetailCurve line = c.Doc.Create.NewDetailCurve(legend, Line.CreateBound(a, b));
        if (c.Invisible != null) line.LineStyle = c.Invisible;
        tag(line, DwRole.DimLine, 0);
        return line;
    }

    private static void Dim(Ctx c, View legend, DetailCurve a, DetailCurve b, XYZ from, XYZ to, string itemKey, int index, Action<Element, DwRole, int> tag)
    {
        var refs = new ReferenceArray();
        refs.Append(a.GeometryCurve.Reference);
        refs.Append(b.GeometryCurve.Reference);
        Dimension d = c.DimType != null
            ? c.Doc.Create.NewDimension(legend, Line.CreateBound(from, to), refs, c.DimType)
            : c.Doc.Create.NewDimension(legend, Line.CreateBound(from, to), refs);
        tag(d, DwRole.Dimension, index);
        c.NewDims.Add((d, DwDim.KeyOf(itemKey, index)));
    }

    private static void SetLen(Element e, string name, double feet)
    {
        Parameter? p = e.LookupParameter(name);
        if (p != null && !p.IsReadOnly) p.Set(feet);
    }
}
