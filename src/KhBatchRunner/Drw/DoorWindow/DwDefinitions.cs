using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>
/// The schedules a model holds: each one's preset, saved as JSON on its first legend, so the live
/// updater knows what to keep current after the model is reopened (or opened by someone else).
/// Kept on the legend rather than in a storage element of its own: delete the legends and the
/// schedule is gone with them, with nothing left to go stale.
/// </summary>
internal static class DwDefinitions
{
    private static readonly Guid SchemaGuid = new("5e2c8a71-3d94-4b06-8f1e-6a7b9c0d2e43");
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static Schema GetSchema()
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s != null) return s;
        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName("KhDoorWindowSchedule");
        b.SetVendorId("KANGHUN");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Vendor);
        b.SetDocumentation("DRW Helper Enlarge Door & Window: the preset a schedule was built with, as JSON.");
        b.AddSimpleField("PresetJson", typeof(string));
        return b.Finish();
    }

    /// <summary>Saves the set's preset on its first legend, with what belongs to this model's legends
    /// and never to the preset a PC keeps (another model's D1 is another door): the cell widths set
    /// by hand (<paramref name="widths"/>: item key → paper mm), how much wider Revit draws the
    /// table font than GDI measures it (<paramref name="textFit"/>, for the live updater's wrapping,
    /// which cannot measure), and each dimension as built and as moved by hand (<paramref name="dims"/>).
    /// Null keeps what was saved before.</summary>
    public static void Save(View firstLegend, DoorWindowPreset p, Dictionary<string, double>? widths = null, double? textFit = null,
        Dictionary<string, DwDim>? dims = null)
    {
        widths ??= Widths(firstLegend);
        textFit ??= TextFit(firstLegend);
        dims ??= Dims(firstLegend);
        JsonNode node = JsonSerializer.SerializeToNode(p, Json)!;
        if (widths.Count > 0)
        {
            var w = new JsonObject();
            foreach (var kv in widths.OrderBy(k => k.Key, StringComparer.Ordinal)) w[kv.Key] = Math.Round(kv.Value, 1);
            node[WidthsProperty] = w;
        }
        node[FitProperty] = Math.Round(textFit.Value, 4);
        if (dims.Count > 0)
        {
            var d = new JsonObject();
            foreach (var kv in dims.OrderBy(k => k.Key, StringComparer.Ordinal)) d[kv.Key] = JsonSerializer.SerializeToNode(kv.Value, Json);
            node[DimsProperty] = d;
        }
        var e = new Entity(GetSchema());
        e.Set("PresetJson", node.ToJsonString(Json));
        firstLegend.SetEntity(e);
    }

    /// <summary>The JSON properties beside the preset's own (a preset read from the JSON skips them).</summary>
    public const string WidthsProperty = "AdjustedCellWidths", FitProperty = "TextFit", DimsProperty = "Dimensions";

    private static JsonNode? Node(View firstLegend)
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s == null) return null;
        try
        {
            Entity e = firstLegend.GetEntity(s);
            return e.IsValid() ? JsonNode.Parse(e.Get<string>("PresetJson")) : null;
        }
        catch { return null; }
    }

    /// <summary>The cell widths set by hand that a set's first legend holds (item key → paper mm).</summary>
    public static Dictionary<string, double> Widths(View firstLegend)
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        if (Node(firstLegend)?[WidthsProperty] is JsonObject w)
            foreach (var kv in w)
                if (kv.Value is JsonValue v && v.TryGetValue(out double mm) && mm > 0) widths[kv.Key] = mm;
        return widths;
    }

    /// <summary>Each dimension the last build drew, by <see cref="DwDim.KeyOf"/>: where it put it,
    /// and where someone moved it since.</summary>
    public static Dictionary<string, DwDim> Dims(View firstLegend)
    {
        var dims = new Dictionary<string, DwDim>(StringComparer.Ordinal);
        if (Node(firstLegend)?[DimsProperty] is JsonObject d)
            foreach (var kv in d)
                try
                {
                    if (kv.Value?.Deserialize<DwDim>(Json) is { } dim) dims[kv.Key] = dim;
                }
                catch { /* unreadable: that dimension is drawn at the default */ }
        return dims;
    }

    /// <summary>The dimensions of set <paramref name="setKey"/> in this model.</summary>
    public static Dictionary<string, DwDim> Dims(Document doc, string setKey) =>
        FirstLegend(doc, setKey) is View first ? Dims(first) : new Dictionary<string, DwDim>(StringComparer.Ordinal);

    /// <summary>The text fit saved by the last build (1 before any).</summary>
    public static double TextFit(View firstLegend) =>
        Node(firstLegend)?[FitProperty] is JsonValue v && v.TryGetValue(out double f) && f >= 0.3 && f <= 4 ? f : 1;

    /// <summary>A set's first legend (null: not built in this model).</summary>
    public static View? FirstLegend(Document doc, string setKey) =>
        DwTag.All(doc, setKey).Where(t => t.T.Role == DwRole.Legend).OrderBy(t => t.T.Index).Select(t => t.E).FirstOrDefault() as View;

    /// <summary>The hand-set cell widths of set <paramref name="setKey"/> in this model.</summary>
    public static Dictionary<string, double> Widths(Document doc, string setKey) =>
        FirstLegend(doc, setKey) is View first ? Widths(first) : new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Every schedule in the model, by its preset.</summary>
    public static List<DoorWindowPreset> Load(Document doc)
    {
        var list = new List<DoorWindowPreset>();
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s == null) return list;
        foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                     .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate))
        {
            Entity e;
            try { e = v.GetEntity(s); } catch { continue; }
            if (!e.IsValid()) continue;
            try
            {
                if (JsonSerializer.Deserialize<DoorWindowPreset>(e.Get<string>("PresetJson"), Json) is { } p)
                {
                    p.Migrate();
                    if (list.All(o => o.SetKey != p.SetKey)) list.Add(p);
                }
            }
            catch { /* unreadable: that schedule is not kept live */ }
        }
        return list;
    }
}

/// <summary>
/// One dimension of a cell (width, height or sill), paper mm: how far its line stands off the edge
/// it measures, and where its text sits from the line's middle. "Built" is what the last build drew;
/// "Kept" what someone moved it to by hand (the user, 2026-10-22: a moved dimension survives a
/// rebuild), null where it was not moved.
/// </summary>
internal sealed class DwDim
{
    public const int Width = 0, Height = 1, Sill = 2;

    public double BuiltOff { get; set; }
    public double BuiltTx { get; set; }
    public double BuiltTy { get; set; }
    public double? Off { get; set; }
    public double? Tx { get; set; }
    public double? Ty { get; set; }

    [JsonIgnore]
    public bool Moved => Off != null || Tx != null;

    public static string KeyOf(string itemKey, int index) => itemKey + "#" + index;
}
