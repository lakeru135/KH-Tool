using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Common;

/// <summary>Metres and paper millimetres to Revit's internal feet.</summary>
internal static class DrwUnits
{
    public static double FromMeters(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
    public static double FromMillimeters(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}

/// <summary>Finds types and templates by the names a preset stores.</summary>
internal static class DrwLookup
{
    public const string None = "<None>";

    public static List<ViewFamilyType> ViewTypes(Document doc, ViewFamily family) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .Where(t => t.ViewFamily == family)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The view type a preset names (label or bare name), else the first of the family.</summary>
    public static ElementId ViewType(Document doc, ViewFamily family, string stored) =>
        TypeByLabel(ViewTypes(doc, family), stored);

    public static List<View> Templates(Document doc, ViewType viewType) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.IsTemplate && v.ViewType == viewType)
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The named template, or invalid for blank / "&lt;None&gt;" / not found.</summary>
    public static ElementId Template(Document doc, ViewType viewType, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == None) return ElementId.InvalidElementId;
        return Templates(doc, viewType).FirstOrDefault(v => v.Name == name)?.Id ?? ElementId.InvalidElementId;
    }

    /// <summary>
    /// "Family : Type" — how every DRW type list shows a type, because type names repeat across
    /// families ("Standard" is a stair path type in more than one family).
    /// </summary>
    public static string TypeLabel(Element e) =>
        e is ElementType t && !string.IsNullOrWhiteSpace(t.FamilyName) ? $"{t.FamilyName} : {t.Name}" : e.Name;

    /// <summary>
    /// The type a preset names: by "Family : Type" label first, then by bare type name (presets
    /// saved before labels), else the first type, else invalid.
    /// </summary>
    public static ElementId TypeByLabel(IEnumerable<Element> types, string stored)
    {
        List<Element> all = types.ToList();
        return (all.FirstOrDefault(t => TypeLabel(t) == stored)
                ?? all.FirstOrDefault(t => t.Name == stored)
                ?? all.FirstOrDefault())?.Id ?? ElementId.InvalidElementId;
    }

    /// <summary>
    /// <see cref="TypeByLabel"/>, saying so in <paramref name="warnings"/> when a preset names a type
    /// this model does not have. A batch run over many models would otherwise use another type
    /// silently and report success.
    /// </summary>
    public static ElementId ResolveType(IEnumerable<Element> types, string stored, string what, List<string> warnings)
    {
        List<Element> all = types.ToList();
        ElementId id = TypeByLabel(all, stored);
        bool found = all.Any(t => TypeLabel(t) == stored || t.Name == stored);
        if (!string.IsNullOrWhiteSpace(stored) && !found)
        {
            Element? used = all.FirstOrDefault(t => t.Id == id);
            warnings.Add(used != null
                ? $"{what} '{stored}' is not in this model — used '{TypeLabel(used)}'"
                : $"{what} '{stored}' is not in this model, and it has none of that kind");
        }
        return id;
    }

    /// <summary>
    /// The scale a view template imposes, or null when it leaves View Scale to each view — i.e. the
    /// scale parameter is among the template's parameters and not unticked ("not controlled").
    /// </summary>
    public static int? ControlledScale(View template)
    {
        ICollection<ElementId> all = template.GetTemplateParameterIds();
        ICollection<ElementId> free = template.GetNonControlledTemplateParameterIds();
        bool controls = new[]
            {
                BuiltInParameter.VIEW_SCALE, BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC,
                BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL
            }
            .Select(b => new ElementId(b))
            .Any(id => all.Contains(id) && !free.Contains(id));
        return controls ? template.Scale : null;
    }

    /// <summary><see cref="Template"/>, saying so when a named template is not in this model.</summary>
    public static ElementId ResolveTemplate(Document doc, ViewType viewType, string stored, string what, List<string> warnings)
    {
        ElementId id = Template(doc, viewType, stored);
        if (!string.IsNullOrWhiteSpace(stored) && stored != None && id == ElementId.InvalidElementId)
            warnings.Add($"{what} '{stored}' is not in this model — the views get no template");
        return id;
    }

    public static List<string> Labels(IEnumerable<Element> types) => types.Select(TypeLabel).ToList();

    public static List<string> Names(IEnumerable<Element> elements) => elements.Select(e => e.Name).ToList();
}

/// <summary>View names: composing them from a preset's prefixes and keeping them unique.</summary>
internal static class DrwNaming
{
    private static readonly char[] Forbidden = { '{', '}', '[', ']', ':', '\\', '|', ';', '<', '>', '?', '`', '~' };

    /// <summary>
    /// <c>"Enlarge Plan - Kitchen - Level 1"</c>: the prefixes run together, then the base name,
    /// then an optional suffix. Empty parts are dropped rather than leaving stray separators.
    /// </summary>
    public static string Compose(string prefix, string kindPrefix, string baseName, string suffix = "")
    {
        string head = string.Join(" ", new[] { prefix, kindPrefix }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        string name = head.Length > 0 ? head + " - " + baseName : baseName;
        if (!string.IsNullOrWhiteSpace(suffix)) name += " - " + suffix.Trim();
        return Clean(name);
    }

    public static string Clean(string name)
    {
        foreach (char c in Forbidden) name = name.Replace(c, '_');
        return name.Trim();
    }

    public static HashSet<string> ExistingViewNames(Document doc) =>
        new(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate).Select(v => v.Name),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The name, or the name with " (2)", " (3)"… — and records it as taken.</summary>
    public static string Unique(string name, HashSet<string> taken)
    {
        string candidate = name;
        for (int i = 2; taken.Contains(candidate); i++) candidate = $"{name} ({i})";
        taken.Add(candidate);
        return candidate;
    }
}

/// <summary>A project parameter that applies to views, as the Parameters dropdown lists it.</summary>
internal sealed class DrwViewParameter
{
    public string Name { get; init; } = "";
    /// <summary>The shared parameter's GUID; blank for a project (non-shared) parameter.</summary>
    public string Guid { get; init; } = "";
    public string DataType { get; init; } = "";
    /// <summary>What the dropdown shows; qualified only when the name alone is ambiguous.</summary>
    public string Label { get; set; } = "";
}

/// <summary>Writes a preset's "external parameter" values onto a view.</summary>
internal static class DrwParams
{
    /// <summary>
    /// Every project parameter bound to the Views category, labelled for the dropdown. Two with the
    /// same name (a project and a shared parameter, or two shared ones with different GUIDs) each
    /// get a qualifier — "Drawing Type  (Shared · Text · 3f2a1c9e)" / "(Project · Text)" — so the
    /// user can see there are two and pick the right one.
    /// </summary>
    public static List<DrwViewParameter> ViewParameters(Document doc)
    {
        var list = new List<DrwViewParameter>();
        Category? views = Category.GetCategory(doc, BuiltInCategory.OST_Views);
        if (views == null) return list;

        DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
        {
            if (it.Current is not InstanceBinding binding || !binding.Categories.Contains(views)) continue;
            if (it.Key is not InternalDefinition def) continue;
            string guid = doc.GetElement(def.Id) is SharedParameterElement sp ? sp.GuidValue.ToString() : "";
            string type;
            try { type = LabelUtils.GetLabelForSpec(def.GetDataType()); } catch { type = ""; }
            list.Add(new DrwViewParameter { Name = def.Name, Guid = guid, DataType = type });
        }

        foreach (IGrouping<string, DrwViewParameter> g in list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool clash = g.Count() > 1;
            foreach (DrwViewParameter p in g)
            {
                string kind = p.Guid.Length > 0 ? "Shared" : "Project";
                p.Label = clash
                    ? $"{p.Name}  ({kind} · {p.DataType}{(p.Guid.Length > 0 ? " · " + p.Guid.Substring(0, 8) : "")})"
                    : $"{p.Name}  ({p.DataType})";
            }
        }
        return list.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Finds the view's parameter a preset row means, without guessing. A GUID picks exactly one
    /// shared parameter. Without one, only a project (non-shared, non-built-in) parameter of that
    /// name qualifies, and if the name still matches more than one, nothing is written — the old
    /// <c>LookupParameter(name)</c> took whichever came first and could silently fill the wrong one.
    /// </summary>
    private static Parameter? Resolve(Element e, string name, string guid, out string? why)
    {
        why = null;
        if (System.Guid.TryParse(guid, out Guid g))
        {
            Parameter? byGuid = e.get_Parameter(g);
            if (byGuid == null) why = $"'{name}' (shared {guid.Substring(0, Math.Min(8, guid.Length))}) not found on this view";
            return byGuid;
        }

        var matches = e.Parameters.Cast<Parameter>()
            .Where(p => string.Equals(p.Definition?.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) { why = $"'{name}' not found"; return null; }
        if (matches.Count == 1) return matches[0];

        var project = matches.Where(p => !p.IsShared &&
            (p.Definition as InternalDefinition)?.BuiltInParameter == BuiltInParameter.INVALID).ToList();
        if (project.Count == 1) return project[0];
        why = $"'{name}' is ambiguous: {matches.Count} parameters have this name — pick it again in the Parameters tab";
        return null;
    }

    /// <summary>Null on success, otherwise why the value was not written. Never throws.</summary>
    public static string? Set(Element e, string name, string value) => Set(e, name, "", value);

    public static string? Set(Element e, string name, string guid, string value)
    {
        try
        {
            Parameter? p = Resolve(e, name, guid, out string? why);
            if (p == null) return why;
            if (p.IsReadOnly) return $"'{name}' is read-only";
            switch (p.StorageType)
            {
                case StorageType.String:
                    p.Set(value);
                    return null;
                case StorageType.Integer:
                    string v = value.Trim().ToLowerInvariant();
                    if (v is "yes" or "true" or "y") { p.Set(1); return null; }
                    if (v is "no" or "false" or "n") { p.Set(0); return null; }
                    if (int.TryParse(v, out int i)) { p.Set(i); return null; }
                    return $"'{name}' needs a whole number or yes/no";
                case StorageType.Double:
                    // SetValueString reads the value in the project's display units, as typed in Revit.
                    if (p.SetValueString(value)) return null;
                    return $"'{name}' could not read '{value}' as a number";
                default:
                    return $"'{name}' is an element reference; only text, number and yes/no are supported";
            }
        }
        catch (Exception ex) { return $"'{name}': {ex.Message}"; }
    }

    public static string? Mark(Element e) =>
        e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();

    public static bool SetMark(Element e, string mark)
    {
        Parameter? p = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
        if (p == null || p.IsReadOnly) return false;
        return p.Set(mark);
    }
}

/// <summary>
/// Deletes warnings as they are raised, so a batch of views or dimensions never stops on
/// "elements overlap" style dialogs. Errors are left for Revit to report and roll back.
/// </summary>
internal sealed class DrwWarningSwallower : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
            if (f.GetSeverity() == FailureSeverity.Warning)
                accessor.DeleteWarning(f);
        return FailureProcessingResult.Continue;
    }

    public static Transaction Start(Document doc, string name)
    {
        var t = new Transaction(doc, name);
        FailureHandlingOptions o = t.GetFailureHandlingOptions();
        o.SetFailuresPreprocessor(new DrwWarningSwallower());
        t.SetFailureHandlingOptions(o);
        t.Start();
        return t;
    }
}

/// <summary>
/// For a transaction that must say whether ONE thing worked: warnings are deleted as
/// <see cref="DrwWarningSwallower"/> does, but an error rolls the transaction back quietly (no
/// Revit dialog) and its text is kept in <see cref="Errors"/> for the result row.
/// </summary>
internal sealed class DrwStrictFailures : IFailuresPreprocessor
{
    public List<string> Errors { get; } = new();

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        bool error = false;
        foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
        {
            if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
            else
            {
                error = true;
                Errors.Add(f.GetDescriptionText());
            }
        }
        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }

    public static Transaction Start(Document doc, string name, out DrwStrictFailures failures)
    {
        failures = new DrwStrictFailures();
        var t = new Transaction(doc, name);
        FailureHandlingOptions o = t.GetFailureHandlingOptions();
        o.SetFailuresPreprocessor(failures);
        o.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(o);
        t.Start();
        return t;
    }
}

/// <summary>Fits a view's crop region to a set of model points plus a margin.</summary>
internal static class DrwCrop
{
    /// <summary>
    /// Works in the view's own crop coordinates (<see cref="View.CropBox"/>'s transform), so the
    /// same call fits a plan, a section or an elevation: X is the view's right, Y its up. The crop
    /// depth (Z) is left alone; far clip is set separately where it matters.
    /// </summary>
    public static void Fit(View view, IEnumerable<XYZ> points, double margin) => Fit(view, points, margin, margin, margin);

    /// <summary><see cref="Fit(View, IEnumerable{XYZ}, double)"/> with its own margin at the sides,
    /// below and above (the view's up) — how elevations take a section's crop and height.</summary>
    public static void Fit(View view, IEnumerable<XYZ> points, double side, double below, double above)
    {
        BoundingBoxXYZ box = view.CropBox;
        Transform inverse = box.Transform.Inverse;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (XYZ p in points)
        {
            XYZ q = inverse.OfPoint(p);
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
            minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }
        if (minX > maxX) return;
        box.Min = new XYZ(minX - side, minY - below, box.Min.Z);
        box.Max = new XYZ(maxX + side, maxY + above, box.Max.Z);
        view.CropBoxActive = true;
        view.CropBox = box;
    }

    /// <summary>Clip an elevation's far side at <paramref name="depth"/> from its view plane.</summary>
    public static void FarClip(View view, double depth)
    {
        Parameter? mode = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
        if (mode != null && !mode.IsReadOnly && mode.AsInteger() == 0) mode.Set(2);   // 2 = clip without line
        Parameter? offset = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
        if (offset != null && !offset.IsReadOnly) offset.Set(Math.Max(depth, 0.1));
    }
}

/// <summary>Which views a Datum Dimension / Stair Annotation run covers.</summary>
public enum DrwViewScope
{
    /// <summary>The active view only (interactive).</summary>
    ActiveView,
    /// <summary>Views selected in the Project Browser (interactive).</summary>
    SelectedViews,
    /// <summary>Every view of the covered kinds whose name contains a filter — the only scope a
    /// batch can use. Named for plans because Stair Annotation's are; Datum Dimension also covers
    /// the sections and elevations its preset ticks.</summary>
    AllPlans
}

internal static class DrwScope
{
    public static bool IsPlan(View v, bool includeStructural = true) =>
        !v.IsTemplate && (v.ViewType == ViewType.FloorPlan || (includeStructural && v.ViewType == ViewType.EngineeringPlan));

    /// <summary>
    /// The plan views for a run. <paramref name="uiDoc"/> is null in a batch, where only
    /// <see cref="DrwViewScope.AllPlans"/> means anything, so every other scope falls back to it.
    /// </summary>
    public static List<ViewPlan> Resolve(Document doc, UIDocument? uiDoc, DrwViewScope scope, string nameContains)
    {
        if (uiDoc != null && scope == DrwViewScope.ActiveView)
            return doc.ActiveView is ViewPlan vp && IsPlan(vp) ? new List<ViewPlan> { vp } : new List<ViewPlan>();

        if (uiDoc != null && scope == DrwViewScope.SelectedViews)
            return uiDoc.Selection.GetElementIds().Select(doc.GetElement)
                .OfType<ViewPlan>().Where(v => IsPlan(v)).ToList();

        string filter = nameContains?.Trim() ?? "";
        return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => IsPlan(v))
            .Where(v => filter.Length == 0 || v.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The views for a Stair Annotation run: floor plans, and sections — interactively whenever
    /// active or selected, by the name filter (and so in a batch) only when <paramref name="sections"/>.
    /// </summary>
    public static List<View> ResolveStairViews(Document doc, UIDocument? uiDoc, DrwViewScope scope, string nameContains,
        bool sections) =>
        ResolveDatumViews(doc, uiDoc, scope, nameContains, plans: true, sections, elevations: false)
            .Where(v => v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.Section).ToList();

    /// <summary>
    /// The views for a Preset Tagger run: plans, ceiling plans, sections and elevations. Interactive
    /// scopes take the active or selected views of any of those kinds; the name-filter scope (and so a
    /// batch) takes only the kinds ticked.
    /// </summary>
    public static List<View> ResolveTagViews(Document doc, UIDocument? uiDoc, DrwViewScope scope, string nameContains,
        bool plans, bool ceilingPlans, bool sections, bool elevations)
    {
        bool tagView(View v) => Drw.Annotation.PresetTagger.IsTagView(v);
        if (uiDoc != null && scope == DrwViewScope.ActiveView)
            return doc.ActiveView is View av && tagView(av) ? new List<View> { av } : new List<View>();

        if (uiDoc != null && scope == DrwViewScope.SelectedViews)
            return uiDoc.Selection.GetElementIds().Select(doc.GetElement).OfType<View>().Where(tagView).ToList();

        string filter = nameContains?.Trim() ?? "";
        return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(tagView)
            .Where(v => v.ViewType switch
            {
                ViewType.CeilingPlan => ceilingPlans,
                ViewType.Section => sections,
                ViewType.Elevation => elevations,
                _ => plans
            })
            .Where(v => filter.Length == 0 || v.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A view whose datums Datum Dimension and Match Datums work on.</summary>
    public static bool IsDatumView(View v) =>
        !v.IsTemplate && (IsPlan(v) || v.ViewType == ViewType.Section || v.ViewType == ViewType.Elevation);

    /// <summary>
    /// The views for a Datum Dimension run. Interactive scopes take the active or selected views of
    /// any datum kind; the name-filter scope (and so a batch) takes only the kinds ticked.
    /// </summary>
    public static List<View> ResolveDatumViews(Document doc, UIDocument? uiDoc, DrwViewScope scope, string nameContains,
        bool plans, bool sections, bool elevations)
    {
        if (uiDoc != null && scope == DrwViewScope.ActiveView)
            return doc.ActiveView is View av && IsDatumView(av) ? new List<View> { av } : new List<View>();

        if (uiDoc != null && scope == DrwViewScope.SelectedViews)
            return uiDoc.Selection.GetElementIds().Select(doc.GetElement).OfType<View>().Where(IsDatumView).ToList();

        string filter = nameContains?.Trim() ?? "";
        return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(IsDatumView)
            .Where(v => IsPlan(v) ? plans : v.ViewType == ViewType.Section ? sections : elevations)
            .Where(v => filter.Length == 0 || v.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>One result row, in the shape <see cref="UI.ActionResultsWindow"/> and the batch logs take.</summary>
internal static class DrwRows
{
    public static IReadOnlyDictionary<string, string> Row(IReadOnlyList<string> columns, params string[] values)
    {
        var row = new Dictionary<string, string>();
        for (int i = 0; i < columns.Count; i++) row[columns[i]] = i < values.Length ? values[i] : "";
        return row;
    }
}
