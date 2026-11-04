using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

public sealed class StairAnnotationPreset : IDrwPreset
{
    public string Name { get; set; } = "Default";
    public bool PlacePath { get; set; } = true;
    public string PathTypeName { get; set; } = "";
    public bool PlaceNumbers { get; set; } = true;

    // Tread/riser numbers, named and ordered as in Revit's Properties palette (Graphics). They have
    // no type to choose — the "Number type" of 0.2.0–0.2.3 was an internal type Revit never shows —
    // so every setting is written on each new number instead.
    /// <summary>"Riser" or "Tread" — the palette's Tag Type.</summary>
    public string TagType { get; set; } = "Riser";
    public NumberSystemDisplayRule DisplayRule { get; set; } = NumberSystemDisplayRule.All;
    public StairsNumberSystemReferenceOption Reference { get; set; } = StairsNumberSystemReferenceOption.Center;
    public double ReferenceOffsetMm { get; set; }
    public NumberSystemJustifyOption Justify { get; set; } = NumberSystemJustifyOption.Center;
    public double JustifyOffsetMm { get; set; }
    public TagOrientation Orientation { get; set; } = TagOrientation.Horizontal;
    /// <summary>0 = leave Revit's size.</summary>
    public double NumberSizeMm { get; set; }

    // Dimensions (0.2.9.1): plans only; straight runs, parallel or at 90° (single, U, L). Off when a preset saved earlier
    // lacks the field, so its batches do not start dimensioning; a new dialog ticks it.
    public bool PlaceDimensions { get; set; }
    public string DimensionTypeName { get; set; } = "";
    /// <summary>Paper mm from the stair to the first string.</summary>
    public double DimOffsetMm { get; set; } = 10;
    /// <summary>Paper mm between strings when the type gives none (or SpacingFromType is off).</summary>
    public double DimSpacingMm { get; set; } = 8;
    /// <summary>Space the strings by the dimension type's Dimension Line Snap Distance — the default for
    /// every multi-layer dimension tool, and what a preset saved without the field reads.</summary>
    public bool SpacingFromType { get; set; } = true;
    public bool DimOverall { get; set; } = true;
    /// <summary>Prefix of the run segment: {N} treads, {D} tread depth in metres.</summary>
    public string RunLabel { get; set; } = "{N}T @ {D} = ";

    public DrwViewScope Scope { get; set; } = DrwViewScope.ActiveView;
    public string ViewNameContains { get; set; } = "";
    /// <summary>The name filter (and so a batch) also covers sections — tread numbers only there.
    /// Off by default, so a batch saved before 0.2.9 still touches plans only.</summary>
    public bool Sections { get; set; }
    /// <summary>Stair paths and tread numbers already in a view are left as they are either way.</summary>
    public bool SkipExisting { get; set; } = true;
}

public static class StairAnnotationStore
{
    public static readonly DrwPresetStore<StairAnnotationPreset> Store = new("StairAnnotationPresets");
}

/// <summary>
/// Places a stair path on every stair shown in each plan, and tread numbers on every run of the
/// stairs whose base is the plan's level (numbers belong where the stair starts; a stair seen from
/// the level above would otherwise be numbered twice). Anything already there is left alone, so
/// a re-run only fills gaps. In a section only tread numbers are placed (a stair path is a plan
/// annotation), on every stair the section shows — a section sees each stair once.
/// </summary>
internal static class StairAnnotator
{
    public static readonly IReadOnlyList<string> Columns = new[] { "View", "Stair", "Annotation", "Status", "Note" };

    public static List<Element> PathTypes(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(StairsPathType)).ToElements()
            .OrderBy(DrwLookup.TypeLabel, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A view Stair Annotation works in: a floor plan, or a section (numbers only).</summary>
    public static bool IsStairView(View v) =>
        !v.IsTemplate && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.Section);

    public static List<IReadOnlyDictionary<string, string>> Run(Document doc, IEnumerable<View> views, StairAnnotationPreset p)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        var warnings = new List<string>();
        ElementId pathType = DrwLookup.ResolveType(PathTypes(doc), p.PathTypeName, "Stair path type",
            p.PlacePath ? warnings : new List<string>());
        DimensionType? dimType = null;
        if (p.PlaceDimensions)
        {
            List<DimensionType> linear = DatumDimensioner.LinearTypes(doc);
            dimType = linear.FirstOrDefault(t => DrwLookup.TypeLabel(t) == p.DimensionTypeName)
                      ?? linear.FirstOrDefault(t => t.Name == p.DimensionTypeName);
            if (dimType == null && !string.IsNullOrWhiteSpace(p.DimensionTypeName))
                warnings.Add($"Dimension type '{p.DimensionTypeName}' is not in this model — Revit's default dimension type was used");
        }
        double gapMm = StairDimensioner.GapMm(doc, p, dimType, out string gapNote);
        if (p.PlaceDimensions && gapNote.Length > 0) warnings.Add(gapNote);
        if (warnings.Count > 0)
            rows.Add(DrwRows.Row(Columns, "", "", "Setup", "Warning", string.Join("; ", warnings)));

        using var group = new TransactionGroup(doc, "DRW Stair Annotation");
        group.Start();
        foreach (View view in views.Where(IsStairView))
        {
            using (Transaction t = DrwWarningSwallower.Start(doc, "Stair Annotation " + view.Name))
            {
                try
                {
                    var viewRows = AnnotateView(doc, view, p, pathType);
                    t.Commit();
                    rows.AddRange(viewRows);
                }
                catch (Exception ex)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    rows.Add(DrwRows.Row(Columns, view.Name, "", "", "Failed", ex.Message));
                }
            }
            // Dimensions after the view's transaction: each string takes its own strict one.
            if (p.PlaceDimensions && view is ViewPlan plan && plan.ViewType == ViewType.FloorPlan)
                rows.AddRange(DimensionView(doc, plan, p, dimType, gapMm));
        }
        group.Assimilate();
        return rows;
    }

    /// <summary>Dimensions on the stairs based on the plan's level (as tread numbers), one row per string.</summary>
    private static List<IReadOnlyDictionary<string, string>> DimensionView(Document doc, ViewPlan view,
        StairAnnotationPreset p, DimensionType? dimType, double gapMm)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        ElementId? level = view.GenLevel?.Id;
        if (level == null) return rows;
        foreach (Stairs st in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Stairs)).Cast<Stairs>())
        {
            ElementId baseLevel = st.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
            if (baseLevel != level) continue;
            string label = string.IsNullOrWhiteSpace(DrwParams.Mark(st)) ? $"Stair {st.Id}" : DrwParams.Mark(st)!;
            try { rows.AddRange(StairDimensioner.Place(doc, view, st, label, p, dimType, "", gapMm)); }
            catch (Exception ex) { rows.Add(DrwRows.Row(Columns, view.Name, label, "Dimensions", "Failed", ex.Message)); }
        }
        return rows;
    }

    /// <summary>
    /// Writes the palette's Graphics settings on a new number. Each is tried on its own, so one
    /// Revit refuses does not stop the rest; returns the names of any that did not take (blank = all).
    /// </summary>
    private static string ApplySettings(NumberSystem ns, StairAnnotationPreset p)
    {
        var failed = new List<string>();
        void Try(string name, Action set)
        {
            try { set(); } catch { failed.Add(name); }
        }
        static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        Try("Tag Type", () => { if (!SetByLabel(ns.get_Parameter(BuiltInParameter.NUMBER_SYSTEM_TAG_TYPE), p.TagType)) throw new InvalidOperationException(); });
        Try("Display Rule", () => ns.NumberDisplayRule = p.DisplayRule);
        Try("Offset from Reference", () => ns.ReferenceOffset = Mm(p.ReferenceOffsetMm));
        Try("Justify", () => ns.JustifyOption = p.Justify);
        Try("Justify Offset", () => ns.JustifyOffset = Mm(p.JustifyOffsetMm));
        Try("Orientation", () => ns.NumberOrientation = p.Orientation);
        if (p.NumberSizeMm > 0)
            Try("Number Size", () =>
            {
                Parameter? size = ns.get_Parameter(BuiltInParameter.NUMBER_SYSTEM_TEXT_SIZE);
                if (size == null || size.IsReadOnly || !size.Set(Mm(p.NumberSizeMm))) throw new InvalidOperationException();
            });
        return string.Join(", ", failed);
    }

    /// <summary>
    /// Sets an integer "choice" parameter by the text Revit shows for it ("Riser", "Tread"). Tag
    /// Type's integer values are not documented, so each small value is tried and read back; if
    /// none shows <paramref name="label"/>, the original value is put back.
    /// </summary>
    private static bool SetByLabel(Parameter? param, string label)
    {
        if (param == null || param.IsReadOnly || param.StorageType != StorageType.Integer) return false;
        if (string.Equals(param.AsValueString(), label, StringComparison.OrdinalIgnoreCase)) return true;
        int original = param.AsInteger();
        for (int v = 0; v <= 3; v++)
        {
            try
            {
                if (param.Set(v) && string.Equals(param.AsValueString(), label, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* not a value this parameter takes */ }
        }
        try { param.Set(original); } catch { /* leave it as Revit has it */ }
        return false;
    }

    private static List<IReadOnlyDictionary<string, string>> AnnotateView(Document doc, View view,
        StairAnnotationPreset p, ElementId pathType)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        bool section = view.ViewType == ViewType.Section;
        if (section && !p.PlaceNumbers) return rows;   // nothing a section takes
        List<Stairs> stairs = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Stairs)).Cast<Stairs>().ToList();
        if (stairs.Count == 0) return rows;

        var pathed = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).OfClass(typeof(StairsPath))
            .Cast<StairsPath>().Select(sp => sp.StairsId.HostElementId));
        var numbered = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).OfClass(typeof(NumberSystem))
            .Cast<NumberSystem>().Select(ns => ns.NumberedElementId.HostElementId));
        ElementId? level = view.GenLevel?.Id;

        foreach (Stairs st in stairs)
        {
            string label = string.IsNullOrWhiteSpace(DrwParams.Mark(st)) ? $"Stair {st.Id}" : DrwParams.Mark(st)!;

            if (p.PlacePath && !section)
            {
                if (pathed.Contains(st.Id))
                    rows.Add(DrwRows.Row(Columns, view.Name, label, "Stair path", "Skipped", "Already has a path"));
                else if (pathType == ElementId.InvalidElementId)
                    rows.Add(DrwRows.Row(Columns, view.Name, label, "Stair path", "Failed", "No stair path type in this model"));
                else
                {
                    try
                    {
                        StairsPath.Create(doc, new LinkElementId(st.Id), pathType, view.Id);
                        rows.Add(DrwRows.Row(Columns, view.Name, label, "Stair path", "Created", ""));
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                        when (ex.Message.IndexOf("not visible", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // The view's collector returns stairs its crop, a filter or a hidden
                        // category keeps off screen; nothing is wrong, there is nothing to annotate.
                        rows.Add(DrwRows.Row(Columns, view.Name, label, "Stair path", "Skipped", "Stair is not shown in this view"));
                    }
                    catch (Exception ex)
                    {
                        rows.Add(DrwRows.Row(Columns, view.Name, label, "Stair path", "Failed", ex.Message));
                    }
                }
            }

            if (!p.PlaceNumbers) continue;
            ElementId baseLevel = st.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
            // A plan numbers only the stairs starting on its level; a section has no level and
            // numbers every stair it shows, a multistory member by its own base level.
            if (section) level = baseLevel;
            else if (level == null || baseLevel != level) continue;

            bool multistory = st.MultistoryStairsId != ElementId.InvalidElementId;
            foreach (ElementId runId in st.GetStairsRuns())
            {
                if (numbered.Contains(runId))
                {
                    rows.Add(DrwRows.Row(Columns, view.Name, label, "Tread numbers", "Skipped", $"Run {runId} already numbered"));
                    continue;
                }
                try
                {
                    NumberSystem ns;
                    if (multistory)
                        ns = NumberSystem.Create(doc, view.Id, new LinkElementId(runId), p.Reference, new LinkElementId(level));
                    else
                    {
                        var run = (StairsRun)doc.GetElement(runId);
                        ns = NumberSystem.Create(doc, view.Id, new LinkElementId(runId), run.GetNumberSystemReference(p.Reference));
                    }
                    string unset = ApplySettings(ns, p);
                    rows.Add(DrwRows.Row(Columns, view.Name, label, "Tread numbers", "Created",
                        unset.Length == 0 ? $"Run {runId}" : $"Run {runId}; not set: {unset}"));
                }
                catch (Exception ex)
                {
                    rows.Add(DrwRows.Row(Columns, view.Name, label, "Tread numbers", "Failed", $"Run {runId}: {ex.Message}"));
                }
            }
        }
        return rows;
    }
}
