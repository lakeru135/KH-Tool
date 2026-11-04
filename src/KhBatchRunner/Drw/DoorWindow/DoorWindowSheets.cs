using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>A title block as the Sheets tab draws it: its lines and frame, paper mm from its
/// lower-left corner. Plain data, so the window builds without Revit.</summary>
internal sealed class DwTitleBlockData
{
    public string Name { get; set; } = "";
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    public List<(float X1, float Y1, float X2, float Y2)> Lines { get; set; } = new();
    public DwFrame Frame { get; set; }
}

/// <summary>
/// Enlarge Door &amp; Window's sheets: reading a title block's drawing frame (the AI Bridge's
/// <see cref="SheetFrame"/>), and making one sheet per legend with the legend placed at the
/// frame's top-left corner.
/// </summary>
internal static class DoorWindowSheets
{
    private static double Mm(double ft) => ft * 304.8;

    public static List<FamilySymbol> TitleBlocks(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .Cast<FamilySymbol>().OrderBy(s => s.FamilyName + " : " + s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static string Label(FamilySymbol s) => s.FamilyName + " : " + s.Name;

    public static FamilySymbol? Find(Document doc, string label) =>
        TitleBlocks(doc).FirstOrDefault(s => string.Equals(Label(s), label, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every title block type, drawn on a temporary sheet that is rolled back (call outside
    /// a transaction): its lines and frame for the Sheets tab.</summary>
    public static List<DwTitleBlockData> Read(Document doc) => Read(doc, TitleBlocks(doc));

    /// <summary>One title block type, as <see cref="Read(Document)"/> reads it (the dialog asks for
    /// the one picked, when the Sheets tab first shows it).</summary>
    public static DwTitleBlockData? ReadOne(Document doc, string name) =>
        Find(doc, name) is { } type ? Read(doc, new List<FamilySymbol> { type }).FirstOrDefault() : null;

    private static List<DwTitleBlockData> Read(Document doc, List<FamilySymbol> types)
    {
        var list = new List<DwTitleBlockData>();
        if (types.Count == 0) return list;
        using var t = new Transaction(doc, "Read title blocks");
        t.Start();
        try
        {
            foreach (FamilySymbol type in types)
            {
                try
                {
                    ViewSheet sheet = ViewSheet.Create(doc, type.Id);
                    doc.Regenerate();
                    if (TitleBlockOn(doc, sheet) is not { } tb || tb.get_BoundingBox(sheet) is not { } bb) continue;
                    var frame = SheetFrame.DrawingFrame(tb, sheet, bb, out _);
                    var segments = SheetFrame.TitleBlockSegments(tb, sheet, out _);
                    list.Add(new DwTitleBlockData
                    {
                        Name = Label(type),
                        WidthMm = Mm(bb.Max.X - bb.Min.X),
                        HeightMm = Mm(bb.Max.Y - bb.Min.Y),
                        Frame = new DwFrame(Mm(frame.L - bb.Min.X), Mm(frame.B - bb.Min.Y), Mm(frame.R - bb.Min.X), Mm(frame.T - bb.Min.Y)),
                        Lines = segments.Select(sg => ((float)Mm(sg.A.X - bb.Min.X), (float)Mm(sg.A.Y - bb.Min.Y),
                            (float)Mm(sg.B.X - bb.Min.X), (float)Mm(sg.B.Y - bb.Min.Y))).ToList()
                    });
                }
                catch (Exception ex) { Diagnostics.RunTrace.Mark($"Door & Window: title block {Label(type)} not read: {ex.Message}"); }
            }
        }
        finally { t.RollBack(); }
        return list;
    }

    public static Element? TitleBlockOn(Document doc, ViewSheet sheet) =>
        new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault();

    /// <summary>The frame of a sheet's title block, in sheet feet (in a transaction or not).</summary>
    public static (double L, double B, double R, double T)? FrameOn(Document doc, ViewSheet sheet)
    {
        if (doc.IsModifiable) doc.Regenerate();
        if (TitleBlockOn(doc, sheet) is not { } tb || tb.get_BoundingBox(sheet) is not { } bb) return null;
        return SheetFrame.DrawingFrame(tb, sheet, bb, out _);
    }

    /// <summary>A frame in paper mm, for the packing.</summary>
    public static DwFrame ToMm((double L, double B, double R, double T) f) => new(Mm(f.L), Mm(f.B), Mm(f.R), Mm(f.T));

    /// <summary>
    /// The schedule's sheets, one per legend: its own (tagged) sheets reused in order, new ones made
    /// from the title block, numbered on from the preset's start. Sheets beyond what the legends need
    /// are deleted when they hold nothing else, otherwise left and reported.
    /// </summary>
    public static List<ViewSheet> Ensure(Document doc, DoorWindowPreset p, FamilySymbol titleBlock, int count,
        List<(Element E, DwTagData T)> old, List<IReadOnlyDictionary<string, string>> rows)
    {
        List<ViewSheet> sheets = old.Where(o => o.T.Role == DwRole.Sheet && o.E is ViewSheet).OrderBy(o => o.T.Index)
            .Select(o => (ViewSheet)o.E).ToList();
        var taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
        if (!titleBlock.IsActive) titleBlock.Activate();
        while (sheets.Count < count)
        {
            ViewSheet sheet = ViewSheet.Create(doc, titleBlock.Id);
            sheet.SheetNumber = DwSheets.Number(p.SheetNumber, sheets.Count, taken);
            taken.Add(sheet.SheetNumber);
            sheet.Name = p.SheetName;
            DwTag.Set(sheet, new DwTagData(p.SetKey, "", DwRole.Sheet, sheets.Count + 1));
            sheets.Add(sheet);
            rows.Add(DrwRows.Row(LegendBuilder.Columns, sheet.SheetNumber + " - " + sheet.Name, "", "Created", "New sheet"));
        }
        // The preset's sheet parameters (the browser organization's folders among them), on new
        // sheets and reused ones alike, so a changed value moves them all.
        var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ViewSheet sheet in sheets.Take(count)) ApplyParameters(sheet, p, rows, said);
        foreach (ViewSheet spare in sheets.Skip(count).ToList())
        {
            bool onlyOurs = new FilteredElementCollector(doc, spare.Id).WhereElementIsNotElementType()
                .All(e => e.Category?.Id.Value is (long)BuiltInCategory.OST_TitleBlocks or (long)BuiltInCategory.OST_Viewports);
            if (onlyOurs) { rows.Add(DrwRows.Row(LegendBuilder.Columns, spare.SheetNumber, "", "Deleted", "Sheet no longer needed")); doc.Delete(spare.Id); }
            else rows.Add(DrwRows.Row(LegendBuilder.Columns, spare.SheetNumber, "", "Warning", "Sheet no longer needed but holds other content; left."));
        }
        return sheets.Take(count).ToList();
    }

    /// <summary>Puts <paramref name="legend"/> on <paramref name="sheet"/> (once), its top-left at the
    /// frame's top-left corner plus the margin.</summary>
    public static void Place(Document doc, ViewSheet sheet, View legend, DoorWindowPreset p, List<IReadOnlyDictionary<string, string>> rows)
    {
        if (FrameOn(doc, sheet) is not { } f) return;
        Viewport? vp = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
            .FirstOrDefault(v => v.ViewId == legend.Id);
        if (vp != null && vp.SheetId != sheet.Id)
        {
            rows.Add(DrwRows.Row(LegendBuilder.Columns, legend.Name, "", "Warning", $"Already on another sheet; left there."));
            return;
        }
        vp ??= Viewport.Create(doc, sheet.Id, legend.Id, new XYZ((f.L + f.R) / 2, (f.B + f.T) / 2, 0));
        // No title under the legend: the cells carry their own (the user, 2026-10-22).
        if (NoTitleType(doc, vp) is { } noTitle && vp.GetTypeId() != noTitle) vp.ChangeTypeId(noTitle);
        doc.Regenerate();
        // The grid's own top-left (legend point 0,0) goes to the frame corner. Not the viewport's
        // box: that also holds the label text boxes, which reach past the drawn cells (the first
        // sheet test put a 7-cell grid 170 mm "wider" than it was drawn).
        double m = p.SheetMarginMm / 304.8;
        XYZ at = ToSheet(legend, vp, XYZ.Zero) ?? new XYZ(vp.GetBoxOutline().MinimumPoint.X, vp.GetBoxOutline().MaximumPoint.Y, 0);
        var move = new XYZ(f.L + m - at.X, f.T - m - at.Y, 0);
        if (move.GetLength() > 1e-6) ElementTransformUtils.MoveElement(doc, vp.Id, move);
    }

    /// <summary>A viewport type that shows no title: the model's own (Show Title = No), else one
    /// made from the viewport's type as "No Title". Null when neither can be had.</summary>
    public static ElementId? NoTitleType(Document doc, Viewport vp)
    {
        var valid = vp.GetValidTypes().Select(doc.GetElement).OfType<ElementType>().ToList();
        ElementType? none = valid.Where(t => t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.AsInteger() == 0)
            .OrderByDescending(t => t.Name.Equals("No Title", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (none != null) return none.Id;
        try
        {
            if (doc.GetElement(vp.GetTypeId()) is not ElementType current) return null;
            string name = "No Title";
            for (int n = 2; valid.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = "No Title " + n;
            ElementType made = current.Duplicate(name);
            made.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.Set(0);
            return made.Id;
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("Door & Window: no 'No Title' viewport type: " + ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ sheet parameters

    /// <summary>
    /// The sheet parameters the Sheets tab offers: first those the current sheet browser
    /// organization groups by (read off the model's sheets' folders), then every other editable text
    /// or number parameter of a sheet; each with the values the model's sheets use. Sheet Number and
    /// Sheet Name are left out: the preset sets those.
    /// </summary>
    public static List<DwSheetParameterChoice> ParameterChoices(Document doc)
    {
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
        var list = new List<DwSheetParameterChoice>();
        if (sheets.Count == 0) return list;
        var organized = new List<ElementId>();
        try
        {
            if (BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc) is { } org)
                foreach (ViewSheet s in sheets.Take(50))
                    foreach (FolderItemInfo fi in org.GetFolderItems(s.Id))
                        if (!organized.Contains(fi.ElementId)) organized.Add(fi.ElementId);
        }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("Door & Window: sheet organization not read: " + ex.Message); }

        var skip = new[] { BuiltInParameter.SHEET_NUMBER, BuiltInParameter.SHEET_NAME }.Select(b => (long)b).ToHashSet();
        ViewSheet sample = sheets[0];
        IEnumerable<Parameter> Editable(ViewSheet s) => s.Parameters.Cast<Parameter>()
            .Where(prm => !prm.IsReadOnly && prm.StorageType is StorageType.String or StorageType.Integer or StorageType.Double
                          && !skip.Contains(prm.Id.Value) && prm.Definition != null);
        List<string> Values(ElementId id) => sheets.Select(s => s.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == id))
            .Where(x => x != null).Select(x => ValueOf(x!)).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).Take(100).ToList();

        foreach (ElementId id in organized)
            if (sample.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == id) is { Definition: not null } prm && !skip.Contains(id.Value))
                list.Add(new DwSheetParameterChoice { Name = prm.Definition.Name, Organization = true, Values = Values(id) });
        foreach (Parameter prm in Editable(sample).OrderBy(x => x.Definition.Name, StringComparer.CurrentCultureIgnoreCase))
            if (list.All(c => !c.Name.Equals(prm.Definition.Name, StringComparison.OrdinalIgnoreCase)))
                list.Add(new DwSheetParameterChoice { Name = prm.Definition.Name, Values = Values(prm.Id) });
        return list;
    }

    private static string ValueOf(Parameter prm) => prm.StorageType switch
    {
        StorageType.String => prm.AsString() ?? "",
        StorageType.Integer or StorageType.Double => prm.AsValueString() ?? "",
        _ => ""
    };

    /// <summary>Writes the preset's sheet parameters on <paramref name="sheet"/>; one it lacks or
    /// cannot take is reported once per build.</summary>
    public static void ApplyParameters(ViewSheet sheet, DoorWindowPreset p, List<IReadOnlyDictionary<string, string>> rows, HashSet<string> said)
    {
        foreach (DwSheetParameter sp in p.SheetParameters.Where(x => x.Name.Trim().Length > 0))
        {
            Parameter? prm = sheet.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Definition?.Name == sp.Name);
            string? why = prm == null ? "the sheets have no such parameter"
                : prm.IsReadOnly ? "it is read-only"
                : null;
            if (why == null && ValueOf(prm!) != sp.Value)
            {
                try
                {
                    bool ok = prm!.StorageType switch
                    {
                        StorageType.String => prm.Set(sp.Value),
                        StorageType.Integer when int.TryParse(sp.Value, out int n) => prm.Set(n),
                        StorageType.Integer or StorageType.Double => prm.SetValueString(sp.Value),
                        _ => false
                    };
                    if (!ok) why = $"'{sp.Value}' is not a value it takes";
                }
                catch (Exception ex) { why = ex.Message; }
            }
            if (why != null && said.Add(sp.Name))
                rows.Add(DrwRows.Row(LegendBuilder.Columns, sheet.SheetNumber, "", "Warning", $"Sheet parameter '{sp.Name}' not set: {why}."));
        }
    }

    /// <summary>
    /// Where a point of a legend falls on its sheet. A legend "does not return transforms"
    /// (GetModelToProjectionTransforms throws for one), so the mapping is worked out: the viewport's
    /// box is the legend's content at its scale, centre on centre, the content being every element's
    /// outline in the legend. Null when the legend shows nothing.
    /// </summary>
    public static XYZ? ToSheet(View legend, Viewport vp, XYZ model)
    {
        if (ContentBox(legend) is not { } c) { LastMappingError = "the legend shows nothing"; return null; }
        Outline box = vp.GetBoxOutline();
        XYZ boxCentre = (box.MinimumPoint + box.MaximumPoint) / 2, contentCentre = (c.Min + c.Max) / 2;
        XYZ d = (model - contentCentre) / Math.Max(1, legend.Scale);
        return new XYZ(boxCentre.X + d.X, boxCentre.Y + d.Y, 0);
    }

    /// <summary>The union of every element's outline in a legend, in legend (model) coordinates.</summary>
    public static (XYZ Min, XYZ Max)? ContentBox(View legend)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (Element e in new FilteredElementCollector(legend.Document, legend.Id).WhereElementIsNotElementType())
        {
            if (e.get_BoundingBox(legend) is not { } bb) continue;
            minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y);
            maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y);
        }
        return minX > maxX ? null : (new XYZ(minX, minY, 0), new XYZ(maxX, maxY, 0));
    }

    /// <summary>Why <see cref="ToSheet"/> last returned null (for the self-test's report).</summary>
    internal static string LastMappingError = "";
}
