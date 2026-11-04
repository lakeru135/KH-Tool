using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>What a tagged element is to its schedule.</summary>
internal enum DwRole
{
    Legend, Frame, Mark, Description, Title1, Title2, Elevation, Plan, Level, DimLine, Dimension, Row, Sheet,
    /// <summary>The set's own starter schedule (its rows come from it).</summary>
    Schedule,
    /// <summary>The lines drawn instead of a legend component for an instance-sized type.</summary>
    Outline,
    /// <summary>The level symbol on the floor line: its triangle's lines and its "FFL." text.</summary>
    FloorMark
}

/// <summary>
/// The mark every element Enlarge Door &amp; Window makes carries: which schedule (the preset's
/// name), which item (<see cref="DwItem.Key"/>; blank on a legend), its role and, for rows, its
/// index. Updates find their own elements by it and never touch anything drawn by hand.
/// </summary>
internal readonly record struct DwTagData(string Set, string Key, DwRole Role, int Index);

internal static class DwTag
{
    private static readonly Guid SchemaGuid = new("b3e7d0a4-5c21-4f8e-9a63-1d2c7e4f8b90");

    private static Schema GetSchema()
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s != null) return s;
        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName("KhDoorWindowElement");
        b.SetVendorId("KANGHUN");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Vendor);
        b.SetDocumentation("DRW Helper Enlarge Door & Window: which schedule, item and role an element belongs to.");
        b.AddSimpleField("Set", typeof(string));
        b.AddSimpleField("Key", typeof(string));
        b.AddSimpleField("Role", typeof(string));
        b.AddSimpleField("Index", typeof(int));
        return b.Finish();
    }

    public static void Set(Element e, DwTagData d)
    {
        Schema s = GetSchema();
        var en = new Entity(s);
        en.Set("Set", d.Set);
        en.Set("Key", d.Key);
        en.Set("Role", d.Role.ToString());
        en.Set("Index", d.Index);
        e.SetEntity(en);
    }

    public static DwTagData? Get(Element e)
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s == null) return null;
        Entity en;
        try { en = e.GetEntity(s); } catch { return null; }
        if (!en.IsValid()) return null;
        return Enum.TryParse(en.Get<string>("Role"), out DwRole role)
            ? new DwTagData(en.Get<string>("Set"), en.Get<string>("Key"), role, en.Get<int>("Index"))
            : null;
    }

    /// <summary>Every element of schedule <paramref name="set"/>, with its tag: its legends, what
    /// is in them, and its sheets — so the search stays small.</summary>
    public static List<(Element E, DwTagData T)> All(Document doc, string set)
    {
        var list = new List<(Element, DwTagData)>();
        if (Schema.Lookup(SchemaGuid) == null) return list;
        var legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate).ToList();
        foreach (View v in legends)
        {
            if (Get(v) is { } lt && lt.Set == set) list.Add(((Element)v, lt));
            foreach (Element e in new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType())
                if (Get(e) is { } t && t.Set == set) list.Add((e, t));
        }
        foreach (ViewSheet s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            if (Get(s) is { } st && st.Set == set) list.Add(((Element)s, st));
        return list;
    }
}
