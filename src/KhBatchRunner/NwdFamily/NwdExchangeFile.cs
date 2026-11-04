using System.IO;
using System.Text.Json;

namespace KhBatchRunner.NwdFamily;

/// <summary>
/// Reader for the <c>.khnwd.json</c> file written by the KH-NWD Tool Navisworks add-in
/// (KH-Tool › NWD Helper › Create Family from Navisworks turns it into an .rfa).
///
/// <para>The WRITER lives in the KH-NWD-Tool repo (src/Shared/KhExchange.cs). The two must agree on
/// this layout; <see cref="FormatVersion"/> is the guard — a file newer than this reader understands
/// is refused with "update", never half-read.</para>
/// <code>
/// {
///   "format": "KH-NWD Tool exchange", "version": 3, "units": "feet",
///   "familyName": "Pump P-101", "category": "MechanicalEquipment", "sourceFile": "Plant.nwd",
///   "origin": [x, y, z],                 // selection's plan centre / lowest point, Navisworks coordinates
///   "rotation": 0.5236,                  // v3, optional: plan angle (radians) the vertices were un-rotated by
///   "rebuildShapes": true,               // v3, optional: rebuild boxes / vertical cylinders as resizable extrusions
///   "parameters": [ { "name": "Manufacturer", "type": "text", "value": "Grundfos" } ],   // v3, optional
///   "meshes": [ { "name": "...", "layer": "...", "color": [r, g, b],
///                 "vertices": [x, y, z, ...],   // relative to origin
///                 "triangles": [i, j, k, ...] } ]
/// }
/// </code>
/// Lengths are feet (Revit internal units); vertices are relative to the origin, so the family
/// geometry sits at (0,0,0) and the origin is where the object was in Navisworks. Parameter types are
/// text, integer, number, length (feet) and yesno (a JSON true/false). A v2 file has no parameters and
/// no rebuilt shapes.
/// </summary>
internal sealed class NwdExchangeFile
{
    public const string FileFilter = "KH-NWD Tool export (*.khnwd.json)|*.khnwd.json|JSON files (*.json)|*.json";
    private const string Format = "KH-NWD Tool exchange";
    private const int FormatVersion = 3;

    public string FamilyName { get; private set; } = "";
    public string CategoryKey { get; private set; } = NwdCategories.Default;
    public string SourceFile { get; private set; } = "";
    public double OriginX { get; private set; }
    public double OriginY { get; private set; }
    public double OriginZ { get; private set; }
    /// <summary>Plan angle (radians) to turn the placed instance by; the family itself is built square.</summary>
    public double Rotation { get; private set; }
    public bool RebuildShapes { get; private set; }
    public List<NwdParameter> Parameters { get; } = new();
    public List<NwdMesh> Meshes { get; } = new();

    public static NwdExchangeFile Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument json = JsonDocument.Parse(stream);
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("format", out JsonElement format) || format.GetString() != Format)
            throw new InvalidDataException("Not a KH-NWD Tool export file.");
        int version = root.GetProperty("version").GetInt32();
        if (version > FormatVersion)
            throw new InvalidDataException(
                $"File format v{version} is newer than this add-in supports. Update KH-Tool.");

        JsonElement origin = root.GetProperty("origin");
        var file = new NwdExchangeFile
        {
            FamilyName = root.GetProperty("familyName").GetString() ?? "",
            CategoryKey = root.GetProperty("category").GetString() ?? NwdCategories.Default,
            SourceFile = root.TryGetProperty("sourceFile", out JsonElement src) ? src.GetString() ?? "" : "",
            OriginX = origin[0].GetDouble(),
            OriginY = origin[1].GetDouble(),
            OriginZ = origin[2].GetDouble(),
            Rotation = root.TryGetProperty("rotation", out JsonElement rot) && rot.ValueKind == JsonValueKind.Number ? rot.GetDouble() : 0,
            RebuildShapes = root.TryGetProperty("rebuildShapes", out JsonElement rebuild) && rebuild.ValueKind == JsonValueKind.True,
        };

        if (root.TryGetProperty("parameters", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Array)
            foreach (JsonElement p in parameters.EnumerateArray())
            {
                if (NwdParameter.TryRead(p) is { } parameter)
                    file.Parameters.Add(parameter);
            }

        foreach (JsonElement mesh in root.GetProperty("meshes").EnumerateArray())
        {
            JsonElement color = mesh.GetProperty("color");
            JsonElement vertices = mesh.GetProperty("vertices");
            JsonElement triangles = mesh.GetProperty("triangles");
            var m = new NwdMesh
            {
                Name = mesh.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                Layer = mesh.TryGetProperty("layer", out JsonElement l) ? l.GetString() ?? "" : "",
                R = color[0].GetByte(),
                G = color[1].GetByte(),
                B = color[2].GetByte(),
                Vertices = new double[vertices.GetArrayLength()],
                Triangles = new int[triangles.GetArrayLength()],
            };
            int k = 0;
            foreach (JsonElement v in vertices.EnumerateArray())
                m.Vertices[k++] = v.GetDouble();
            k = 0;
            foreach (JsonElement t in triangles.EnumerateArray())
                m.Triangles[k++] = t.GetInt32();
            file.Meshes.Add(m);
        }
        return file;
    }
}

internal enum NwdParameterType { Text, Integer, Number, Length, YesNo }

/// <summary>One Navisworks property that becomes a family type parameter.</summary>
internal sealed class NwdParameter
{
    public string Name { get; init; } = "";
    public NwdParameterType Type { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Integer, number, length (feet) or yes/no (1 / 0).</summary>
    public double Value { get; init; }

    /// <summary>Null for an entry this reader cannot use (no name, unknown type, value of the wrong kind).</summary>
    public static NwdParameter? TryRead(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object ||
            !p.TryGetProperty("name", out JsonElement n) || n.GetString() is not { Length: > 0 } name ||
            !p.TryGetProperty("type", out JsonElement t) || !p.TryGetProperty("value", out JsonElement v))
            return null;
        switch (t.GetString())
        {
            case "text":
                return new NwdParameter { Name = name, Type = NwdParameterType.Text, Text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() };
            case "integer" when v.ValueKind == JsonValueKind.Number:
                return new NwdParameter { Name = name, Type = NwdParameterType.Integer, Value = Math.Round(v.GetDouble()) };
            case "number" when v.ValueKind == JsonValueKind.Number:
                return new NwdParameter { Name = name, Type = NwdParameterType.Number, Value = v.GetDouble() };
            case "length" when v.ValueKind == JsonValueKind.Number:
                return new NwdParameter { Name = name, Type = NwdParameterType.Length, Value = v.GetDouble() };
            case "yesno" when v.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return new NwdParameter { Name = name, Type = NwdParameterType.YesNo, Value = v.ValueKind == JsonValueKind.True ? 1 : 0 };
            default:
                return null;
        }
    }
}

/// <summary>One Navisworks geometry item: flat xyz vertex array and flat triangle index array.</summary>
internal sealed class NwdMesh
{
    public string Name { get; init; } = "";
    public string Layer { get; init; } = "";
    public byte R { get; init; } = 192;
    public byte G { get; init; } = 192;
    public byte B { get; init; } = 192;
    public double[] Vertices { get; init; } = Array.Empty<double>();
    public int[] Triangles { get; init; } = Array.Empty<int>();
}
