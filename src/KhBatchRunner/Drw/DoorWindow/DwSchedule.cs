using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>
/// A door or window schedule as Enlarge Door &amp; Window reads it: the table rows are its shown
/// fields (heading = the label on the sheet, in its field order), each item's values are its cells
/// on the row of its Type Mark. Plain data, so the dialog builds without Revit.
/// </summary>
internal sealed class DwScheduleData
{
    public string Name { get; set; } = "";
    public bool IsDoor { get; set; }
    /// <summary>The shown fields' headings except Type Mark, in order.</summary>
    public List<string> Headings { get; set; } = new();
    /// <summary>Type Mark → its cells, lined up with <see cref="Headings"/>.</summary>
    public Dictionary<string, List<string>> ByMark { get; set; } = new(StringComparer.Ordinal);
    /// <summary>What is off about it (said, never blocking: only a missing Type Mark blocks, and
    /// <see cref="DwSchedule.Ensure"/> repairs that).</summary>
    public List<string> Problems { get; set; } = new();
    /// <summary>Type Mark is not a shown field: the rows cannot be told apart.</summary>
    public bool Blocked { get; set; }
    /// <summary>The column (in <see cref="Headings"/>) of its combined Width x Height field, the Size
    /// row, and of its Count field, the Qty row; -1 when it has none.</summary>
    public int SizeColumn { get; set; } = -1;
    public int CountColumn { get; set; } = -1;
    /// <summary>The door/window instances its filters let through (null = not known: all).</summary>
    public HashSet<long>? Instances { get; set; }

    /// <summary>Columns whose field is a material parameter (e.g. Frame Material).</summary>
    public HashSet<int> MaterialColumns { get; set; } = new();
    /// <summary>Type Mark → material column → the material's id.</summary>
    public Dictionary<string, Dictionary<int, long>> MaterialOf { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Each material used: property name → its text (Name, Description, Mark, Comments, …
    /// and the material's project parameters).</summary>
    public Dictionary<long, Dictionary<string, string>> Materials { get; set; } = new();
    /// <summary>The properties a material row can show, in a sensible order.</summary>
    public List<string> MaterialProperties { get; set; } = new();

    /// <summary>Each column's field: its parameter id (the key "Value from" is saved under).</summary>
    public List<long> FieldIds { get; set; } = new();
    /// <summary>Each column's "Value from": "cell", "material:&lt;property&gt;", "type:&lt;parameter&gt;"
    /// or "instance:&lt;parameter&gt;" (<see cref="DwValueFrom"/>; defaults filled in).</summary>
    public Dictionary<int, string> ValueFrom { get; set; } = new();
    /// <summary>The placed types' and instances' parameter names, for the picks.</summary>
    public List<string> TypeParameters { get; set; } = new();
    public List<string> InstanceParameters { get; set; } = new();
    /// <summary>Type Mark → parameter name → text, for the names a "Value from" uses: of the drawn
    /// type, and of the placed instances ("Varies" when they differ).</summary>
    public Dictionary<string, Dictionary<string, string>> TypeValues { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, string>> InstanceValues { get; set; } = new(StringComparer.Ordinal);

    public string Cell(string mark, int column) =>
        ByMark.TryGetValue(mark, out List<string>? cells) && column < cells.Count ? cells[column] : "";

    /// <summary>A material column's value for a mark: the chosen property, else the material's
    /// name, else "" (the caller writes the blank text).</summary>
    public string Material(string mark, int column, string property)
    {
        if (!MaterialOf.TryGetValue(mark, out var cols) || !cols.TryGetValue(column, out long id)
            || !Materials.TryGetValue(id, out var props)) return "";
        return props.TryGetValue(property, out string? v) && v.Trim().Length > 0 ? v
            : props.TryGetValue("Name", out string? n) ? n : "";
    }

    /// <summary>A column's value for a mark, by its "Value from".</summary>
    public string Value(string mark, int column)
    {
        string from = ValueFrom.TryGetValue(column, out string? f) ? f : DwValueFrom.Cell;
        string Lookup(Dictionary<string, Dictionary<string, string>> values, string name) =>
            values.TryGetValue(mark, out var byName) && byName.TryGetValue(name, out string? v) ? v : "";
        return from switch
        {
            _ when from.StartsWith(DwValueFrom.MaterialPrefix) => Material(mark, column, from.Substring(DwValueFrom.MaterialPrefix.Length)),
            _ when from.StartsWith(DwValueFrom.TypePrefix) => Lookup(TypeValues, from.Substring(DwValueFrom.TypePrefix.Length)),
            _ when from.StartsWith(DwValueFrom.InstancePrefix) => Lookup(InstanceValues, from.Substring(DwValueFrom.InstancePrefix.Length)),
            _ => Cell(mark, column)
        };
    }
}

/// <summary>
/// Where each schedule row's value comes from, saved IN THE MODEL on the set's schedule and keyed
/// by the field's parameter (the user, 2026-10-21: one setting everyone shares, that survives a
/// heading renamed or a field moved). A field with no entry takes the default: a material field
/// shows the material's Description, any other field its cell.
/// </summary>
internal static class DwValueFrom
{
    public const string Cell = "cell";
    public const string MaterialPrefix = "material:", TypePrefix = "type:", InstancePrefix = "instance:";
    private static readonly Guid SchemaGuid = new("4f7a2c91-8d36-4b5e-a1f0-2c9e7d3b6a58");

    private static Autodesk.Revit.DB.ExtensibleStorage.Schema GetSchema()
    {
        var s = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);
        if (s != null) return s;
        var b = new Autodesk.Revit.DB.ExtensibleStorage.SchemaBuilder(SchemaGuid);
        b.SetSchemaName("KhDoorWindowValueFrom");
        b.SetVendorId("KANGHUN");
        b.SetReadAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Public);
        b.SetWriteAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Vendor);
        b.SetDocumentation("DRW Helper Enlarge Door & Window: per schedule field, where its legend row's value comes from.");
        b.AddSimpleField("Map", typeof(string));
        return b.Finish();
    }

    /// <summary>Field parameter id → choice, as saved on the schedule.</summary>
    public static Dictionary<long, string> Get(ViewSchedule s)
    {
        var map = new Dictionary<long, string>();
        var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);
        if (schema == null) return map;
        try
        {
            var e = s.GetEntity(schema);
            if (!e.IsValid()) return map;
            foreach (string line in (e.Get<string>("Map") ?? "").Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && long.TryParse(line.Substring(0, tab), out long id)) map[id] = line.Substring(tab + 1);
            }
        }
        catch { /* unreadable: defaults */ }
        return map;
    }

    /// <summary>Saves one field's choice (in a transaction).</summary>
    public static void Set(ViewSchedule s, long fieldId, string choice)
    {
        Dictionary<long, string> map = Get(s);
        map[fieldId] = choice;
        var e = new Autodesk.Revit.DB.ExtensibleStorage.Entity(GetSchema());
        e.Set("Map", string.Join("\n", map.Select(kv => kv.Key + "\t" + kv.Value)));
        s.SetEntity(e);
    }

    /// <summary>The map's entry saying the Size and Count fields were added once (no field has this
    /// id), so a field the user removes is not added back.</summary>
    private const long SizeQtyKey = long.MinValue;

    public static bool SizeQtyAdded(ViewSchedule s) => Get(s).ContainsKey(SizeQtyKey);

    /// <summary>In a transaction.</summary>
    public static void MarkSizeQtyAdded(ViewSchedule s)
    {
        if (!SizeQtyAdded(s)) Set(s, SizeQtyKey, "added");
    }

    /// <summary>"Material: Description" / "Type: Frame Spec" / "Schedule cell", for the window.</summary>
    public static string Show(string choice) =>
        choice.StartsWith(MaterialPrefix) ? "Material: " + choice.Substring(MaterialPrefix.Length)
        : choice.StartsWith(TypePrefix) ? "Type: " + choice.Substring(TypePrefix.Length)
        : choice.StartsWith(InstancePrefix) ? "Instance: " + choice.Substring(InstancePrefix.Length)
        : "Schedule cell";

    public static string Parse(string shown) =>
        shown.StartsWith("Material: ") ? MaterialPrefix + shown.Substring(10)
        : shown.StartsWith("Type: ") ? TypePrefix + shown.Substring(6)
        : shown.StartsWith("Instance: ") ? InstancePrefix + shown.Substring(10)
        : Cell;
}

/// <summary>
/// The schedule a set draws its rows from: the set's own starter, made and tagged on first use
/// (the user, 2026-10-21: always the starter, opened for them to edit). Probed 2026-10-21 (DW
/// TIMING): after <see cref="ViewSchedule.RefreshData"/> the cells are current inside the live
/// updater too, so rows stay one undo step with the edit. A hidden field has no column in the body:
/// Type Mark must be shown.
/// </summary>
internal static class DwSchedule
{
    private const long TypeMark = (long)BuiltInParameter.ALL_MODEL_TYPE_MARK;
    public const string DefaultMaterialProperty = "Description";

    public static bool IsDoorWindow(ViewSchedule s, out bool isDoor)
    {
        long cat = s.Definition.CategoryId.Value;
        isDoor = cat == (long)BuiltInCategory.OST_Doors;
        return isDoor || cat == (long)BuiltInCategory.OST_Windows;
    }

    /// <summary>The model's door (or window) schedules, by name.</summary>
    public static List<ViewSchedule> All(Document doc, bool? door = null) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule
                        && IsDoorWindow(s, out bool d) && (door == null || d == door))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The set's own schedule: the one tagged with its key (found after a rename too).</summary>
    public static ViewSchedule? Find(Document doc, DoorWindowPreset p) =>
        All(doc, p.Category == DwCategory.Door).FirstOrDefault(s => DwTag.Get(s) is { Role: DwRole.Schedule } t && t.Set == p.SetKey);

    public static string StarterName(DoorWindowPreset p) =>
        $"KH {(p.Category == DwCategory.Door ? "Door" : "Window")} Schedule (Enlarge) - {p.Name}";

    /// <summary>
    /// The set's schedule, made from the starter when it has none, and repaired (Type Mark shown,
    /// one row per Type Mark) when edited out of shape. Says in <paramref name="did"/> what it made
    /// or changed. In a transaction.
    /// </summary>
    public static ViewSchedule Ensure(Document doc, DoorWindowPreset p, List<string> did)
    {
        ViewSchedule? s = Find(doc, p);
        if (s == null)
        {
            s = MakeStarter(doc, p);
            did.Add($"made the starter schedule '{s.Name}'");
        }
        foreach (string change in Fix(s)) did.Add($"'{s.Name}': {change}");
        // Schedules made before the Size and Qty rows were fields get them once (a field the user
        // removes later stays removed).
        if (!DwValueFrom.SizeQtyAdded(s))
        {
            if (p.SizeRow && SizeField(s) == null) { AddSize(s, p.SizeLabel); did.Add($"'{s.Name}': added the Size field (Width x Height)"); }
            if (p.QtyRow && CountField(s) == null) { AddCount(s, p.QtyLabel); did.Add($"'{s.Name}': added the Count field"); }
            DwValueFrom.MarkSizeQtyAdded(s);
        }
        p.ScheduleName = s.Name;
        return s;
    }

    private static List<ScheduleField> Shown(ScheduleDefinition d) =>
        d.GetFieldOrder().Select(d.GetField).Where(f => !f.IsHidden).ToList();

    /// <summary>Reads <paramref name="s"/> (refreshing it first; in a transaction or not).</summary>
    public static DwScheduleData Read(ViewSchedule s)
    {
        IsDoorWindow(s, out bool isDoor);
        Document doc = s.Document;
        var data = new DwScheduleData { Name = s.Name, IsDoor = isDoor };
        ScheduleDefinition d = s.Definition;
        List<ScheduleField> shown = Shown(d);
        int markCol = shown.FindIndex(f => f.ParameterId.Value == TypeMark);
        if (markCol < 0) { data.Problems.Add("Type Mark is not a shown field."); data.Blocked = true; }
        if (d.IsItemized) data.Problems.Add("It lists every instance ('Itemize every instance' is ticked).");
        if (d.GetSortGroupFieldCount() == 0 || d.GetField(d.GetSortGroupField(0).FieldId).ParameterId.Value != TypeMark)
            data.Problems.Add("It is not sorted by Type Mark first.");
        var fields = shown.Where((_, i) => i != markCol).ToList();
        data.Headings = fields.Select(f => f.ColumnHeading).ToList();
        data.SizeColumn = fields.FindIndex(f => f.FieldType == ScheduleFieldType.CombinedParameter);
        data.CountColumn = fields.FindIndex(f => f.FieldType == ScheduleFieldType.Count);
        if (markCol < 0) return data;

        s.RefreshData();
        TableSectionData body = s.GetTableData().GetSectionData(SectionType.Body);
        int columns = Math.Min(body.NumberOfColumns, shown.Count);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        for (int r = 0; r < body.NumberOfRows; r++)
        {
            var cells = Enumerable.Range(0, columns).Select(c => s.GetCellText(SectionType.Body, r, c)).ToList();
            while (cells.Count < shown.Count) cells.Add("");
            // The heading row(s), grouped headings and blank spacers are in the body too.
            if (cells.Select((t, c) => t.Length == 0 || t == shown[c].ColumnHeading).All(x => x)) continue;
            string mark = cells[markCol].Trim();
            cells.RemoveAt(markCol);
            if (data.ByMark.ContainsKey(mark)) { if (mark.Length > 0) duplicates.Add(mark); continue; }
            data.ByMark[mark] = cells;
        }
        if (duplicates.Count > 0)
            data.Problems.Add($"Type Mark {string.Join(", ", duplicates.Take(5))} has more than one row; the first is used.");
        // What its filters let through (a collector on the schedule view lists them).
        try { data.Instances = new HashSet<long>(new FilteredElementCollector(doc, s.Id).ToElementIds().Select(id => id.Value)); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { data.Instances = null; }
        if (data.Instances is { Count: 0 }) data.Instances = null;   // not told: take every placed one
        ReadValues(s, isDoor, fields, data);
        return data;
    }

    /// <summary>
    /// What a row can show besides its cell, and where each row's value comes from:
    /// <list type="bullet">
    /// <item>material columns (a field whose parameter names a material: Frame Material…) and, per
    /// Type Mark, that material and its properties — a schedule cell only says its name, the user
    /// wants its Description and the like (2026-10-21);</item>
    /// <item>the placed types' and instances' parameter names, and per Type Mark the values of the
    /// ones a "Value from" uses;</item>
    /// <item>each column's "Value from" (<see cref="DwValueFrom"/>, saved on the schedule).</item>
    /// </list>
    /// Read from each mark's drawn type (the one with the most instances, as the legend draws it).
    /// </summary>
    private static void ReadValues(ViewSchedule s, bool isDoor, List<ScheduleField> fields, DwScheduleData data)
    {
        Document doc = s.Document;
        var primaries = new FilteredElementCollector(doc).OfCategory(isDoor ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows)
            .WhereElementIsNotElementType().OfType<FamilyInstance>().Where(i => i.Symbol != null && i.SuperComponent == null
                                                                                && (data.Instances == null || data.Instances.Contains(i.Id.Value)))
            .GroupBy(i => i.Symbol.Id).GroupBy(g => DoorWindowItems.TypeMark(g.First().Symbol), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Count()).First().ToList(), StringComparer.OrdinalIgnoreCase);
        data.FieldIds = fields.Select(f => f.ParameterId.Value).ToList();

        for (int c = 0; c < fields.Count; c++)
        {
            ScheduleField f = fields[c];
            if (f.FieldType is not (ScheduleFieldType.ElementType or ScheduleFieldType.Instance)) continue;
            bool onType = f.FieldType == ScheduleFieldType.ElementType;
            // A material field: its spec says so, or (spec not readable) a value names a material.
            bool material = IsMaterialSpec(SpecOf(f));
            foreach (KeyValuePair<string, List<FamilyInstance>> kv in primaries)
            {
                // The instance's copy first for an instance field, unless it is empty: a parameter
                // bound to types still answers on the instance, with no value (2026-10-21).
                Parameter? ofType = ParameterOf(kv.Value[0].Symbol, f.ParameterId);
                Parameter? ofInstance = onType ? null : ParameterOf(kv.Value[0], f.ParameterId);
                Parameter? prm = ofInstance is { HasValue: true, StorageType: StorageType.ElementId } i && i.AsElementId() != ElementId.InvalidElementId
                    ? ofInstance : ofType ?? ofInstance;
                if (prm == null || prm.StorageType != StorageType.ElementId) continue;
                if (IsMaterial(prm)) material = true;   // its own spec, as the starter judges it
                if (doc.GetElement(prm.AsElementId()) is not Material m) continue;
                material = true;
                if (!data.MaterialOf.TryGetValue(kv.Key, out var cols)) data.MaterialOf[kv.Key] = cols = new Dictionary<int, long>();
                cols[c] = m.Id.Value;
                if (!data.Materials.ContainsKey(m.Id.Value)) data.Materials[m.Id.Value] = Properties(m);
            }
            if (material) data.MaterialColumns.Add(c);
        }
        string[] first = { "Name", "Description", "Mark", "Comments", "Manufacturer", "Model", "Keynote" };
        var seen = data.Materials.Values.SelectMany(p => p.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        data.MaterialProperties = first.Where(n => seen.Contains(n, StringComparer.OrdinalIgnoreCase) || n is "Name" or "Description")
            .Concat(seen.Where(n => !first.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            .ToList();

        // Each column's "Value from": as saved, else the default.
        Dictionary<long, string> saved = DwValueFrom.Get(s);
        // Only a material column takes its value from elsewhere: a saved choice on any other is
        // read as its cell, so an old pick cannot stick.
        for (int c = 0; c < fields.Count; c++)
            data.ValueFrom[c] = !data.MaterialColumns.Contains(c) ? DwValueFrom.Cell
                : saved.TryGetValue(data.FieldIds[c], out string? v) && !v.StartsWith(DwValueFrom.InstancePrefix) ? v
                : DwValueFrom.MaterialPrefix + DefaultMaterialProperty;

        // Parameter names for the picks, and the values of those in use.
        var types = primaries.Values.Select(l => (Element)l[0].Symbol).ToList();
        var instances = primaries.Values.SelectMany(l => l).Cast<Element>().ToList();
        data.TypeParameters = Names(types);
        data.InstanceParameters = Names(instances.Take(50));
        foreach (string from in data.ValueFrom.Values.Distinct())
        {
            if (from.StartsWith(DwValueFrom.TypePrefix))
            {
                string name = from.Substring(DwValueFrom.TypePrefix.Length);
                foreach (var kv in primaries) Put(data.TypeValues, kv.Key, name, Text(kv.Value[0].Symbol.LookupParameter(name)));
            }
            else if (from.StartsWith(DwValueFrom.InstancePrefix))
            {
                string name = from.Substring(DwValueFrom.InstancePrefix.Length);
                foreach (var kv in primaries)
                {
                    var values = kv.Value.Select(i => Text(i.LookupParameter(name))).Distinct(StringComparer.Ordinal).ToList();
                    Put(data.InstanceValues, kv.Key, name, values.Count == 1 ? values[0] : "Varies");
                }
            }
        }
    }

    private static void Put(Dictionary<string, Dictionary<string, string>> into, string mark, string name, string value)
    {
        if (!into.TryGetValue(mark, out var byName)) into[mark] = byName = new Dictionary<string, string>(StringComparer.Ordinal);
        byName[name] = value;
    }

    private static List<string> Names(IEnumerable<Element> elements) =>
        elements.SelectMany(e => e.Parameters.Cast<Parameter>())
            .Where(p => p.Definition != null && p.StorageType != StorageType.None)
            .Select(p => p.Definition.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>A parameter as Revit shows it; an element reference by the element's name.</summary>
    private static string Text(Parameter? p)
    {
        if (p == null || !p.HasValue) return "";
        return p.StorageType switch
        {
            StorageType.String => p.AsString() ?? "",
            StorageType.ElementId => p.Element.Document.GetElement(p.AsElementId())?.Name ?? p.AsValueString() ?? "",
            _ => p.AsValueString() ?? ""
        };
    }

    /// <summary>A schedule field's parameter on an element: built-in by its enum, shared by its
    /// GUID, project/family by id.</summary>
    private static Parameter? ParameterOf(Element e, ElementId id)
    {
        // A plain cast: BuiltInParameter is Int64 in 2026 and Int32 in 2024, so Enum.IsDefined with
        // either width throws in the other year.
        if (id.Value < 0)
        {
            try { return e.get_Parameter((BuiltInParameter)id.Value); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { return null; }
        }
        if (e.Document.GetElement(id) is SharedParameterElement shared && e.get_Parameter(shared.GuidValue) is { } byGuid) return byGuid;
        return e.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == id);
    }

    /// <summary>A material spec, by name: Revit 2026 reports a shared Material parameter as
    /// "autodesk.spec.aec:material-1.0.0", not the Reference.Material id (the self-test's bound
    /// parameter, 2026-10-21).</summary>
    private static bool IsMaterialSpec(string typeId) =>
        typeId.Length > 0 && (typeId == SpecTypeId.Reference.Material.TypeId
                              || typeId.StartsWith("autodesk.spec.aec:material", StringComparison.OrdinalIgnoreCase)
                              || typeId.StartsWith("autodesk.spec.reference:material", StringComparison.OrdinalIgnoreCase));

    private static string SpecOf(ScheduleField f)
    {
        try { return f.GetSpecTypeId()?.TypeId ?? ""; }
        catch { return ""; }
    }

    private static bool IsMaterial(Parameter p)
    {
        try { return IsMaterialSpec(p.Definition.GetDataType()?.TypeId ?? ""); }
        catch { return false; }
    }

    private static readonly HashSet<BuiltInParameter> NotText = new()
    {
        BuiltInParameter.MATERIAL_PARAM_GLOW, BuiltInParameter.MATERIAL_PARAM_SHININESS, BuiltInParameter.MATERIAL_PARAM_SMOOTHNESS,
        BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY, BuiltInParameter.IFC_EXPORT_ELEMENT_AS, BuiltInParameter.IFC_GUID,
        BuiltInParameter.DESIGN_OPTION_ID, BuiltInParameter.MATERIAL_PARAM_COLOR
    };

    /// <summary>A material's name and its text-like parameters, as Revit shows them.</summary>
    private static Dictionary<string, string> Properties(Material m)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Name"] = m.Name };
        foreach (Parameter p in m.Parameters)
        {
            if (p.Definition == null || !p.HasValue || p.StorageType is StorageType.None or StorageType.ElementId) continue;
            // Rendering and IFC housekeeping is no table text.
            if (p.Definition is InternalDefinition { BuiltInParameter: var b } && NotText.Contains(b)) continue;
            string v = p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
            if (v.Trim().Length > 0 && !props.ContainsKey(p.Definition.Name)) props[p.Definition.Name] = v;
        }
        return props;
    }

    /// <summary>
    /// The set's starter: Type Mark; Description (the cell's header); every material parameter the
    /// placed types have (Frame Material headed วัสดุวงกบ, Door/Panel Material วัสดุกรอบบาน); the
    /// Thai rows that exist by name; Type Comments headed หมายเหตุ. One row per Type Mark, tagged as
    /// the set's. In a transaction.
    /// </summary>
    public static ViewSchedule MakeStarter(Document doc, DoorWindowPreset p)
    {
        bool door = p.Category == DwCategory.Door;
        ViewSchedule s = ViewSchedule.CreateSchedule(doc, new ElementId(door ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows));
        var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        string name = StarterName(p);
        for (int n = 2; names.Contains(name); n++) name = StarterName(p) + " " + n;
        s.Name = name;
        ScheduleDefinition d = s.Definition;
        IList<SchedulableField> all = d.GetSchedulableFields();
        SchedulableField? ByParam(BuiltInParameter b) => all.FirstOrDefault(f => f.ParameterId.Value == (long)b && f.FieldType == ScheduleFieldType.ElementType);
        SchedulableField? ByName(string n) => all.FirstOrDefault(f => string.Equals(f.GetName(doc), n, StringComparison.OrdinalIgnoreCase));
        ScheduleField mark = d.AddField(ByParam(BuiltInParameter.ALL_MODEL_TYPE_MARK)!);
        if (ByParam(BuiltInParameter.ALL_MODEL_DESCRIPTION) is { } desc) d.AddField(desc);
        if (p.SizeRow) AddSize(s, p.SizeLabel);

        // The placed types' material parameters, as the schedule's type fields: a parameter whose
        // spec is Material, or whose value on any placed type is a material.
        List<FamilySymbol> placed = new FilteredElementCollector(doc).OfCategory(door ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows)
            .WhereElementIsNotElementType().OfType<FamilyInstance>().Select(i => i.Symbol).Where(t => t != null)
            .GroupBy(t => t.Id).Select(g => g.First()).ToList();
        bool IsMaterialField(SchedulableField f) => placed.Any(t =>
            ParameterOf(t, f.ParameterId) is { StorageType: StorageType.ElementId } prm
            && (IsMaterial(prm) || doc.GetElement(prm.AsElementId()) is Material));
        foreach (SchedulableField f in all.Where(f => (f.FieldType is ScheduleFieldType.ElementType or ScheduleFieldType.Instance)
                                                      && f.ParameterId.Value != TypeMark))
            {
                if (!IsMaterialField(f)) continue;
                string field = f.GetName(doc);
                ScheduleField added = d.AddField(f);
                added.ColumnHeading = field.IndexOf("Frame", StringComparison.OrdinalIgnoreCase) >= 0 ? "วัสดุวงกบ"
                    : field.IndexOf("Panel", StringComparison.OrdinalIgnoreCase) >= 0 || field.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0
                      || field.IndexOf("Sash", StringComparison.OrdinalIgnoreCase) >= 0 ? "วัสดุกรอบบาน"
                    : field;
            }
        // The Thai table's rows, where the model has a parameter of that name.
        foreach (string thai in new[] { "วัสดุวงกบ", "วัสดุกรอบบาน", "ลูกฟัก", "อุปกรณ์" })
            if (ByName(thai) is { } f) d.AddField(f);
        if (ByParam(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS) is { } tc) d.AddField(tc).ColumnHeading = "หมายเหตุ";
        if (p.QtyRow) AddCount(s, p.QtyLabel);
        d.IsItemized = false;
        d.AddSortGroupField(new ScheduleSortGroupField(mark.FieldId));
        DwTag.Set(s, new DwTagData(p.SetKey, "", DwRole.Schedule, 0));
        DwValueFrom.MarkSizeQtyAdded(s);
        return s;
    }

    /// <summary>The schedule's combined field (the Size row), or null.</summary>
    public static ScheduleField? SizeField(ViewSchedule s) =>
        s.Definition.GetFieldOrder().Select(s.Definition.GetField).FirstOrDefault(f => f.FieldType == ScheduleFieldType.CombinedParameter);

    /// <summary>The schedule's Count field (the Qty row), or null.</summary>
    public static ScheduleField? CountField(ViewSchedule s) =>
        s.Definition.GetFieldOrder().Select(s.Definition.GetField).FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count);

    /// <summary>
    /// A combined field "Width x Height" of the type's Width and Height, headed <paramref name="heading"/>,
    /// after Type Mark and Description (the user, 2026-10-22: the size as Revit writes it). Its
    /// lengths follow the project's units: a combined field takes no format options (probed: "not a
    /// valid display unit"). An instance-sized type reads "&lt;varies&gt;"; the legend writes its
    /// placed size instead. In a transaction.
    /// </summary>
    public static ScheduleField? AddSize(ViewSchedule s, string heading)
    {
        ScheduleDefinition d = s.Definition;
        Document doc = s.Document;
        IList<SchedulableField> all = d.GetSchedulableFields();
        SchedulableField? Find(string name) =>
            all.FirstOrDefault(f => f.GetName(doc) == name && f.FieldType == ScheduleFieldType.ElementType)
            ?? all.FirstOrDefault(f => f.GetName(doc) == name && f.FieldType == ScheduleFieldType.Instance);
        if (Find("Width") is not { } w || Find("Height") is not { } h) return null;
        var width = TableCellCombinedParameterData.Create();
        width.ParamId = w.ParameterId;
        width.Separator = " x ";
        var height = TableCellCombinedParameterData.Create();
        height.ParamId = h.ParameterId;
        IList<ScheduleFieldId> order = d.GetFieldOrder();
        int at = order.Select(d.GetField).ToList().FindIndex(f =>
            f.ParameterId.Value is not (TypeMark or (long)BuiltInParameter.ALL_MODEL_DESCRIPTION));
        ScheduleField size = d.InsertCombinedParameterField(new List<TableCellCombinedParameterData> { width, height }, "Width x Height",
            at < 0 ? order.Count : at);
        size.ColumnHeading = heading;
        return size;
    }

    /// <summary>Revit's Count field, headed <paramref name="heading"/>, last. In a transaction.</summary>
    public static ScheduleField AddCount(ViewSchedule s, string heading)
    {
        ScheduleDefinition d = s.Definition;
        SchedulableField count = d.GetSchedulableFields().FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count)
                                 ?? new SchedulableField(ScheduleFieldType.Count);
        ScheduleField f = d.AddField(count);
        f.ColumnHeading = heading;
        return f;
    }

    /// <summary>Adds or removes the Size and Count fields to match the window's ticks. Says what it
    /// changed. In a transaction.</summary>
    public static List<string> SetSizeQty(ViewSchedule s, bool size, string sizeLabel, bool qty, string qtyLabel)
    {
        var did = new List<string>();
        ScheduleDefinition d = s.Definition;
        if (size && SizeField(s) == null) { if (AddSize(s, sizeLabel) != null) did.Add("added the Size field"); else did.Add("no Width and Height to combine"); }
        if (!size && SizeField(s) is { } sf) { d.RemoveField(sf.FieldId); did.Add("removed the Size field"); }
        if (qty && CountField(s) == null) { AddCount(s, qtyLabel); did.Add("added the Count field"); }
        if (!qty && CountField(s) is { } cf) { d.RemoveField(cf.FieldId); did.Add("removed the Count field"); }
        DwValueFrom.MarkSizeQtyAdded(s);
        return did;
    }

    /// <summary>Makes <paramref name="s"/> one row per Type Mark: shows Type Mark, sorts by it
    /// alone, lists types not instances. Says what it changed. In a transaction.</summary>
    public static List<string> Fix(ViewSchedule s)
    {
        var did = new List<string>();
        ScheduleDefinition d = s.Definition;
        ScheduleField? mark = d.GetFieldOrder().Select(d.GetField).FirstOrDefault(f => f.ParameterId.Value == TypeMark);
        if (mark == null)
        {
            SchedulableField f = d.GetSchedulableFields().First(x => x.ParameterId.Value == TypeMark);
            mark = d.InsertField(f, 0);
            did.Add("added Type Mark");
        }
        else if (mark.IsHidden) { mark.IsHidden = false; did.Add("showed Type Mark"); }
        if (d.IsItemized) { d.IsItemized = false; did.Add("unticked 'Itemize every instance'"); }
        bool sortedRight = d.GetSortGroupFieldCount() == 1 && d.GetSortGroupField(0).FieldId == mark.FieldId
                           && !d.GetSortGroupField(0).ShowHeader && !d.GetSortGroupField(0).ShowFooter;
        if (!sortedRight)
        {
            while (d.GetSortGroupFieldCount() > 0) d.RemoveSortGroupField(0);
            d.AddSortGroupField(new ScheduleSortGroupField(mark.FieldId));
            did.Add("sorted by Type Mark only");
        }
        return did;
    }
}
