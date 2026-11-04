using Autodesk.Revit.DB;

namespace KhBatchRunner.ExcelLink;

/// <summary>How Excel Link treats one schedule column — mirrored in the workbook's legend row.</summary>
public enum ColumnKind
{
    /// <summary>An instance parameter the import may write back ("Instance (may edit)").</summary>
    Instance,
    /// <summary>A type parameter — shown for context, never written ("Type (cannot edit)").</summary>
    Type,
    /// <summary>Calculated/combined/count/id-typed/read-only — never written ("Read-only (cannot edit)").</summary>
    ReadOnly,
}

/// <summary>One visible schedule column, classified, with everything needed to read (export) or
/// write (import) its parameter on a given element.</summary>
public sealed class ScheduleColumn
{
    public required string Heading { get; init; }
    public required ColumnKind Kind { get; init; }

    /// <summary>The schedule field's parameter id — negative = BuiltInParameter, positive = the
    /// shared/project ParameterElement id, InvalidElementId for non-parameter fields.</summary>
    public required ElementId ParameterId { get; init; }

    /// <summary>True to resolve the parameter on the element's TYPE (Kind == Type).</summary>
    public bool OnType { get; init; }

    public string LegendText => Kind switch
    {
        ColumnKind.Instance => ExcelLinkFormat.LegendInstance,
        ColumnKind.Type => ExcelLinkFormat.LegendType,
        _ => ExcelLinkFormat.LegendReadOnly,
    };
}

/// <summary>The workbook vocabulary shared by export and import (import validates against it).</summary>
public static class ExcelLinkFormat
{
    public const string LegendInstance = "Instance (may edit)";
    public const string LegendType = "Type (cannot edit)";
    public const string LegendReadOnly = "Read-only (cannot edit)";
    public const string HeaderElementId = "ElementId";
}

/// <summary>
/// Classifies a schedule's visible fields into <see cref="ColumnKind"/>s and resolves/formats the
/// backing parameters. Classification samples the first ~20 scheduled elements; the import
/// re-classifies against the live schedule and STILL guards every single write with
/// <see cref="Parameter.IsReadOnly"/> — the workbook legend is advice, never trusted.
/// </summary>
public static class ScheduleFieldClassifier
{
    private const int SampleSize = 20;

    public static List<ScheduleColumn> Classify(Document doc, ViewSchedule schedule, IReadOnlyList<Element> elements)
    {
        var columns = new List<ScheduleColumn>();
        ScheduleDefinition def = schedule.Definition;
        var sample = elements.Take(SampleSize).ToList();

        foreach (ScheduleFieldId fid in def.GetFieldOrder())
        {
            ScheduleField field = def.GetField(fid);
            if (field.IsHidden) continue;

            string heading = field.ColumnHeading;
            if (string.IsNullOrWhiteSpace(heading)) heading = field.GetName();

            columns.Add(BuildColumn(doc, field, heading, sample));
        }
        return columns;
    }

    private static ScheduleColumn BuildColumn(Document doc, ScheduleField field, string heading,
        IReadOnlyList<Element> sample)
    {
        ElementId pid = field.ParameterId;

        // Only plain parameter fields can ever be editable; Count/Formula/Percentage/Combined and
        // the specialty field types (Room/ProjectInfo/MaterialQuantity/…) are display-only here.
        bool instanceField = field.FieldType == ScheduleFieldType.Instance;
        bool typeField = field.FieldType == ScheduleFieldType.ElementType;
        if ((!instanceField && !typeField) || pid == ElementId.InvalidElementId)
            return new ScheduleColumn { Heading = heading, Kind = ColumnKind.ReadOnly, ParameterId = pid };

        if (typeField)
            return new ScheduleColumn { Heading = heading, Kind = ColumnKind.Type, ParameterId = pid, OnType = true };

        // Instance field: prove it on the sample. Any element resolving to a writable, supported
        // parameter makes the column Instance; resolving only on types makes it Type; a parameter
        // that is read-only or ElementId-typed everywhere stays ReadOnly.
        bool foundOnType = false, foundReadOnly = false;
        foreach (Element e in sample)
        {
            Parameter? p = Resolve(e, pid);
            if (p is not null)
            {
                if (!p.IsReadOnly && IsWritableStorage(p.StorageType))
                    return new ScheduleColumn { Heading = heading, Kind = ColumnKind.Instance, ParameterId = pid };
                foundReadOnly = true;
                continue;
            }
            Element? type = e.GetTypeId() == ElementId.InvalidElementId ? null : doc.GetElement(e.GetTypeId());
            if (type is not null && Resolve(type, pid) is not null) foundOnType = true;
        }
        if (foundReadOnly)
            return new ScheduleColumn { Heading = heading, Kind = ColumnKind.ReadOnly, ParameterId = pid };
        if (foundOnType)
            return new ScheduleColumn { Heading = heading, Kind = ColumnKind.Type, ParameterId = pid, OnType = true };
        return new ScheduleColumn { Heading = heading, Kind = ColumnKind.ReadOnly, ParameterId = pid };
    }

    private static bool IsWritableStorage(StorageType storage) =>
        storage is StorageType.String or StorageType.Double or StorageType.Integer;

    /// <summary>The column's parameter on <paramref name="e"/> (or on its type when the column says
    /// so), or null when the element doesn't carry it.</summary>
    public static Parameter? ResolveFor(Document doc, Element e, ScheduleColumn column)
    {
        if (column.ParameterId == ElementId.InvalidElementId) return null;
        if (!column.OnType) return Resolve(e, column.ParameterId);
        Element? type = e.GetTypeId() == ElementId.InvalidElementId ? null : doc.GetElement(e.GetTypeId());
        return type is null ? null : Resolve(type, column.ParameterId);
    }

    private static Parameter? Resolve(Element e, ElementId parameterId)
    {
        if (parameterId.Value < 0)
            return e.get_Parameter((BuiltInParameter)parameterId.Value);
        foreach (Parameter p in e.Parameters)
            if (p.Id == parameterId) return p;
        return null;
    }

    /// <summary>The display text a parameter exports as — the same formatter the import compares
    /// against, so unchanged cells are recognised as unchanged. Doubles/integers go through
    /// AsValueString (display units, "Yes"/"No"), strings raw.</summary>
    public static string Format(Parameter p)
    {
        try
        {
            return p.StorageType switch
            {
                StorageType.String => p.AsString() ?? "",
                StorageType.Double => p.AsValueString() ?? "",
                StorageType.Integer => p.AsValueString() ?? p.AsInteger().ToString(),
                StorageType.ElementId => p.AsValueString() ?? "",
                _ => p.AsValueString() ?? "",
            };
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Writes <paramref name="text"/> into a parameter using the inverse of
    /// <see cref="Format"/>. False (with a reason) instead of throwing for anything a user could
    /// plausibly type wrong — the import turns that into a per-cell result row.</summary>
    public static bool TryApply(Parameter p, string text, out string error)
    {
        error = "";
        try
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    if (p.Set(text)) return true;
                    error = "value rejected";
                    return false;

                case StorageType.Double:
                    // SetValueString parses in the parameter's display units — the exact inverse
                    // of AsValueString, so "3500", "3.5 m", "12' 6\"" all land correctly.
                    if (p.SetValueString(text)) return true;
                    error = "could not parse value (check units/format)";
                    return false;

                case StorageType.Integer:
                    bool ok;
                    try { ok = p.SetValueString(text); }
                    catch { ok = false; }
                    if (ok) return true;
                    string t = text.Trim();
                    if (t.Equals("Yes", StringComparison.OrdinalIgnoreCase)) return p.Set(1);
                    if (t.Equals("No", StringComparison.OrdinalIgnoreCase)) return p.Set(0);
                    if (int.TryParse(t, out int i)) return p.Set(i);
                    error = "could not parse value (expected a number or Yes/No)";
                    return false;

                default:
                    error = "unsupported value type";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
