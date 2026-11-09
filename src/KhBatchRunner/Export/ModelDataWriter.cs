using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using KhBatchRunner.Logging;

namespace KhBatchRunner.Export;

/// <summary>Room count and area on one level — one row of <c>history\rooms_gfa.csv</c>.</summary>
internal sealed record LevelArea(string Level, int Rooms, double AreaM2);

/// <summary>
/// Writes the two data files of "Export IFC + Data": <c>elements.csv</c> and <c>rooms.csv</c>.
///
/// <para><b>Streamed, row by row.</b> <see cref="CsvReportWriter"/> builds the whole file in memory,
/// which is fine for a report of a few hundred rows and not for a large model's every element.</para>
///
/// <para><b>ElementId is the first column</b> because it is the key the BIM report joins on (the
/// IFC carries the same id in <c>IfcElement.Tag</c>). Numbers are written invariant-culture, so a
/// PC set to a decimal-comma locale still writes <c>12.5</c>.</para>
/// </summary>
internal static class ModelDataWriter
{
    private static readonly string[] ElementColumns =
        { "ElementId", "UniqueId", "IfcGuid", "Category", "Family", "Type", "Level", "Workset", "PhaseCreated" };

    private static readonly string[] RoomColumns =
        { "ElementId", "UniqueId", "Number", "Name", "Level", "Area_m2", "Placed" };

    /// <summary>
    /// One row per MODEL element in <paramref name="view"/> (or the whole document when null) — the
    /// same scope the IFC was exported from, so the table and the 3D model describe the same things.
    /// Returns the row count.
    /// </summary>
    public static int WriteElements(Document doc, View3D? view, string path, IReadOnlyList<string> extra)
    {
        FilteredElementCollector scope = view is null
            ? new FilteredElementCollector(doc)
            : new FilteredElementCollector(doc, view.Id);

        var names = new NameCache(doc);
        int rows = 0;
        using var w = OpenCsv(path, ElementColumns.Concat(extra));
        foreach (Element e in scope.WhereElementIsNotElementType())
        {
            if (e.ViewSpecific || e.Category is not { CategoryType: CategoryType.Model }) continue;

            ElementType? type = doc.GetElement(e.GetTypeId()) as ElementType;
            var cells = new List<string>(ElementColumns.Length + extra.Count)
            {
                e.Id.Value.ToString(CultureInfo.InvariantCulture),
                e.UniqueId,
                IfcGuid.For(doc, e),
                e.Category.Name,
                type?.FamilyName ?? "",
                type?.Name ?? "",
                names.Level(e),
                names.Workset(e),
                names.Name(e.CreatedPhaseId),
            };
            foreach (string p in extra) cells.Add(ParamText(e, type, p));
            WriteRow(w, cells);
            rows++;
        }
        return rows;
    }

    /// <summary>
    /// Every room in the document, placed or not, with area in m². Rooms are not in a 3D view, so this
    /// reads the whole document. Returns the per-level totals of PLACED rooms — the GFA rows.
    /// </summary>
    public static List<LevelArea> WriteRooms(Document doc, string path, IReadOnlyList<string> extra)
    {
        var totals = new Dictionary<string, (int Count, double Area)>(StringComparer.Ordinal);
        using var w = OpenCsv(path, RoomColumns.Concat(extra));
        foreach (SpatialElement room in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType()
                     .OfType<SpatialElement>())
        {
            double area = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);
            bool placed = room.Location != null && room.Area > 0;
            string level = room.Level?.Name ?? "";
            ElementType? type = doc.GetElement(room.GetTypeId()) as ElementType;

            var cells = new List<string>(RoomColumns.Length + extra.Count)
            {
                room.Id.Value.ToString(CultureInfo.InvariantCulture),
                room.UniqueId,
                room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "",
                room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                level,
                area.ToString("0.###", CultureInfo.InvariantCulture),
                placed ? "true" : "false",
            };
            foreach (string p in extra) cells.Add(ParamText(room, type, p));
            WriteRow(w, cells);

            if (!placed) continue;
            totals.TryGetValue(level, out var t);
            totals[level] = (t.Count + 1, t.Area + area);
        }
        return totals.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new LevelArea(kv.Key, kv.Value.Count, kv.Value.Area))
            .ToList();
    }

    /// <summary>A parameter's display text, instance first then type; blank when neither has it.</summary>
    private static string ParamText(Element e, ElementType? type, string name)
    {
        Parameter? p = e.LookupParameter(name) ?? type?.LookupParameter(name);
        if (p is null || !p.HasValue) return "";
        try
        {
            return p.StorageType switch
            {
                StorageType.String => p.AsString() ?? "",
                StorageType.ElementId => p.AsValueString() ?? "",
                _ => p.AsValueString() ?? "",
            };
        }
        catch { return ""; }
    }

    private static StreamWriter OpenCsv(string path, IEnumerable<string> header)
    {
        // UTF-8 WITH a BOM so Excel opens Thai/Japanese names correctly — same as the run logs.
        var w = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        WriteRow(w, header);
        return w;
    }

    private static void WriteRow(TextWriter w, IEnumerable<string> cells)
    {
        bool first = true;
        foreach (string c in cells)
        {
            if (!first) w.Write(',');
            w.Write(CsvFormat.Escape(c));
            first = false;
        }
        w.Write("\r\n");
    }

    /// <summary>Level / workset / phase names, looked up once per id rather than once per element.</summary>
    private sealed class NameCache
    {
        private readonly Document _doc;
        private readonly Dictionary<long, string> _names = new();
        private readonly WorksetTable? _worksets;

        public NameCache(Document doc)
        {
            _doc = doc;
            _worksets = doc.IsWorkshared ? doc.GetWorksetTable() : null;
        }

        public string Name(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return "";
            if (_names.TryGetValue(id.Value, out string? n)) return n;
            n = _doc.GetElement(id)?.Name ?? "";
            _names[id.Value] = n;
            return n;
        }

        /// <summary>The element's level: its own LevelId, else the reference/schedule level
        /// parameters that hosted and level-less families carry instead.</summary>
        public string Level(Element e)
        {
            if (e.LevelId != ElementId.InvalidElementId) return Name(e.LevelId);
            foreach (BuiltInParameter bip in new[]
                     {
                         BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                         BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                         BuiltInParameter.FAMILY_LEVEL_PARAM,
                         BuiltInParameter.LEVEL_PARAM,
                     })
            {
                ElementId? id = e.get_Parameter(bip)?.AsElementId();
                if (id != null && id != ElementId.InvalidElementId) return Name(id);
            }
            return "";
        }

        public string Workset(Element e)
        {
            if (_worksets is null) return "";
            try { return _worksets.GetWorkset(e.WorksetId)?.Name ?? ""; }
            catch { return ""; }
        }
    }
}
