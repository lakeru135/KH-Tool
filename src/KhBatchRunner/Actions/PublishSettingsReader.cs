using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace KhBatchRunner.Actions;

/// <summary>
/// Reads a model's cloud Publish Settings out of Extensible Storage — the same
/// <c>ExportViewSheetSetListSchema</c> Revit's "Publish Settings" dialog writes when you tick
/// which <see cref="ViewSheetSet"/>s publish to Forma — and turns it into, per sheet, the print
/// sets it belongs to split into TRUE (ticked to publish) and FALSE (present but unticked).
/// This is the C# port of the office Dynamo "Sheet Annotation And Publish Set Export" script so
/// <see cref="DrawingProgressAction"/> can produce the same columns. Read-only.
/// </summary>
internal static class PublishSettingsReader
{
    // The Autodesk-internal schema/field the Publish Settings checkboxes are stored in. We match on
    // either the schema name OR the field name because only one of the two is guaranteed stable.
    private const string PublishSchemaName = "ExportViewSheetSetListSchema";
    private const string PublishFieldName = "ExportViewViewSheetSetIdList";

    /// <summary>
    /// The per-model result: whether the checkbox storage was found, how many print sets exist, a
    /// one-line note for the missing/unreadable case, and the sheet-id → set-name membership split.
    /// When storage is NOT found the two maps stay empty (checkbox state is unknown, so a set is
    /// classified as neither True nor False — exactly what the Dynamo script does).
    /// </summary>
    internal sealed class PublishSetData
    {
        public bool Found { get; init; }
        public int ViewSheetSetCount { get; init; }
        public string Note { get; init; } = "";

        // Keyed by ViewSheet ElementId value; each value is the de-duplicated set names for that sheet.
        public Dictionary<long, List<string>> TrueSetsBySheet { get; } = new();
        public Dictionary<long, List<string>> FalseSetsBySheet { get; } = new();

        public IReadOnlyList<string> SortedTrueSetsFor(long sheetId) => Sorted(TrueSetsBySheet, sheetId);
        public IReadOnlyList<string> SortedFalseSetsFor(long sheetId) => Sorted(FalseSetsBySheet, sheetId);

        /// <summary>Set names for a sheet, sorted case-insensitively (matches the Dynamo output order).</summary>
        private static IReadOnlyList<string> Sorted(Dictionary<long, List<string>> map, long sheetId)
        {
            if (!map.TryGetValue(sheetId, out List<string>? list)) return Array.Empty<string>();
            var copy = new List<string>(list);
            copy.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
            return copy;
        }
    }

    /// <summary>
    /// Search the model for the Publish Settings entity, read the ticked-set tokens, then walk every
    /// <see cref="ViewSheetSet"/> to split its sheets into the True/False maps. Never throws — a
    /// missing schema or an unreadable field just yields <see cref="PublishSetData.Found"/> = false
    /// and empty maps, with the reason in <see cref="PublishSetData.Note"/>.
    /// </summary>
    public static PublishSetData Build(Document doc)
    {
        IReadOnlyList<ViewSheetSet> sets;
        try
        {
            sets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheetSet))
                .Cast<ViewSheetSet>()
                .ToList();
        }
        catch
        {
            sets = Array.Empty<ViewSheetSet>();
        }

        // Three token sets, one per identity kind the stored value might use — the ticked sets are
        // matched by numeric ElementId, by lowercase name, or by lowercase UniqueId.
        var numericIds = new HashSet<long>();
        var names = new HashSet<string>();
        var uniqueIds = new HashSet<string>();

        (Entity? entity, Field? field) = FindPublishEntity(doc, sets);

        bool found;
        string note;
        if (entity is null || field is null)
        {
            found = false;
            note = "Publish Settings storage not found — publish-set True/False left blank";
        }
        else if (TryReadTokens(entity, field, numericIds, names, uniqueIds))
        {
            found = true;
            note = "";
        }
        else
        {
            found = false;
            note = "Publish Settings storage found but unreadable — publish-set True/False left blank";
        }

        var data = new PublishSetData
        {
            Found = found,
            ViewSheetSetCount = sets.Count,
            Note = note,
        };

        BuildMembershipMaps(data, sets, found, numericIds, names, uniqueIds);
        return data;
    }

    // ---- entity search -------------------------------------------------------------------------

    /// <summary>
    /// Look for the Publish Settings entity on the likely owners only — <see cref="ProjectInfo"/>,
    /// every <see cref="DataStorage"/>, and every <see cref="ViewSheetSet"/> — rather than collecting
    /// the whole model. A schema qualifies when its name equals <see cref="PublishSchemaName"/> OR it
    /// carries a field named <see cref="PublishFieldName"/>.
    /// </summary>
    private static (Entity? entity, Field? field) FindPublishEntity(Document doc, IReadOnlyList<ViewSheetSet> viewSheetSets)
    {
        foreach (Element owner in EnumerateCandidates(doc, viewSheetSets))
        {
            IList<Guid> schemaGuids;
            try { schemaGuids = owner.GetEntitySchemaGuids(); }
            catch { continue; }

            foreach (Guid schemaGuid in schemaGuids)
            {
                try
                {
                    Schema? schema = Schema.Lookup(schemaGuid);
                    if (schema is null) continue;

                    string schemaName = CleanText(schema.SchemaName);
                    Field? field = schema.GetField(PublishFieldName);

                    bool schemaMatches = schemaName == PublishSchemaName;
                    bool fieldMatches = field is not null;
                    if (!schemaMatches && !fieldMatches) continue;

                    Entity entity = owner.GetEntity(schema);
                    if (entity is null || !entity.IsValid()) continue;

                    // If we matched on the schema name but the field lookup was deferred, resolve it now.
                    field ??= schema.GetField(PublishFieldName);
                    if (field is null) continue;

                    return (entity, field);
                }
                catch
                {
                    continue;
                }
            }
        }

        return (null, null);
    }

    /// <summary>ProjectInformation, then all DataStorage, then all ViewSheetSet — de-duplicated by id.</summary>
    private static IEnumerable<Element> EnumerateCandidates(Document doc, IReadOnlyList<ViewSheetSet> viewSheetSets)
    {
        var seen = new HashSet<long>();

        Element? projectInfo = doc.ProjectInformation;
        if (projectInfo is not null && seen.Add(projectInfo.Id.Value)) yield return projectInfo;

        List<Element>? dataStorages = null;
        try { dataStorages = new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).ToElements().ToList(); }
        catch { dataStorages = null; }

        if (dataStorages is not null)
            foreach (Element dataStorage in dataStorages)
                if (seen.Add(dataStorage.Id.Value)) yield return dataStorage;

        foreach (ViewSheetSet viewSheetSet in viewSheetSets)
            if (seen.Add(viewSheetSet.Id.Value)) yield return viewSheetSet;
    }

    // ---- field reading -------------------------------------------------------------------------

    /// <summary>
    /// Read the stored value(s) using the field's real <see cref="ContainerType"/> (Array or Simple)
    /// and <see cref="Field.ValueType"/>, converting each into the three match-token sets. Returns
    /// false (and leaves the sets as-is) on any unsupported type or read failure.
    /// </summary>
    private static bool TryReadTokens(Entity entity, Field field,
        HashSet<long> numericIds, HashSet<string> names, HashSet<string> uniqueIds)
    {
        Type? valueType = field.ValueType;
        if (valueType is null) return false;
        string valueTypeName = valueType.FullName ?? "";

        try
        {
            switch (field.ContainerType)
            {
                case ContainerType.Array:
                    switch (valueTypeName)
                    {
                        case "System.Int64":
                            foreach (long value in entity.Get<IList<long>>(field)) numericIds.Add(value);
                            return true;
                        case "System.Int32":
                            foreach (int value in entity.Get<IList<int>>(field)) numericIds.Add(value);
                            return true;
                        case "System.String":
                            foreach (string value in entity.Get<IList<string>>(field)) AddStringToken(value, numericIds, names, uniqueIds);
                            return true;
                        case "System.Guid":
                            foreach (Guid value in entity.Get<IList<Guid>>(field)) AddGuidToken(value, uniqueIds);
                            return true;
                        case "Autodesk.Revit.DB.ElementId":
                            foreach (ElementId value in entity.Get<IList<ElementId>>(field)) AddElementIdToken(value, numericIds);
                            return true;
                        default:
                            return false;
                    }

                case ContainerType.Simple:
                    switch (valueTypeName)
                    {
                        case "System.Int64": numericIds.Add(entity.Get<long>(field)); return true;
                        case "System.Int32": numericIds.Add(entity.Get<int>(field)); return true;
                        case "System.String": AddStringToken(entity.Get<string>(field), numericIds, names, uniqueIds); return true;
                        case "System.Guid": AddGuidToken(entity.Get<Guid>(field), uniqueIds); return true;
                        case "Autodesk.Revit.DB.ElementId": AddElementIdToken(entity.Get<ElementId>(field), numericIds); return true;
                        default: return false;
                    }

                default:
                    return false; // Map (or anything new) — unsupported, same as the Dynamo script.
            }
        }
        catch
        {
            return false;
        }
    }

    // A string token might actually be a numeric id; if it parses, treat it as one, otherwise use it
    // as both a name and a unique-id candidate (lowercased) — mirrors the script's converter.
    private static void AddStringToken(string? value, HashSet<long> numericIds, HashSet<string> names, HashSet<string> uniqueIds)
    {
        string text = CleanText(value);
        if (text.Length == 0) return;

        if (long.TryParse(text, out long parsed)) numericIds.Add(parsed);
        else
        {
            string lowered = text.ToLowerInvariant();
            names.Add(lowered);
            uniqueIds.Add(lowered);
        }
    }

    private static void AddGuidToken(Guid value, HashSet<string> uniqueIds) =>
        uniqueIds.Add(value.ToString().ToLowerInvariant());

    private static void AddElementIdToken(ElementId? value, HashSet<long> numericIds)
    {
        if (value is null) return;
        numericIds.Add(value.Value);
    }

    // ---- membership split ----------------------------------------------------------------------

    /// <summary>
    /// For each print set, record its (cleaned) name against every <see cref="ViewSheet"/> it holds,
    /// into the True map when the set is selected and the False map otherwise. When settings weren't
    /// found the selection state is unknown, so the set is skipped entirely (neither map).
    /// </summary>
    private static void BuildMembershipMaps(PublishSetData data, IReadOnlyList<ViewSheetSet> sets,
        bool found, HashSet<long> numericIds, HashSet<string> names, HashSet<string> uniqueIds)
    {
        foreach (ViewSheetSet set in sets)
        {
            string setName;
            ViewSet views;
            try
            {
                setName = CleanText(set.Name);
                if (setName.Length == 0) setName = "Unnamed Set";
                views = set.Views;
            }
            catch
            {
                continue;
            }

            if (views is null) continue;

            // null = checkbox state unknown; do not guess True or False.
            bool? selected = found ? IsSelected(set, numericIds, names, uniqueIds) : (bool?)null;
            if (selected is null) continue;

            Dictionary<long, List<string>> target = selected.Value ? data.TrueSetsBySheet : data.FalseSetsBySheet;

            // Iterate as Element (not View) — the WinForms global using makes an unqualified `View`
            // ambiguous; every item is a ViewSheet or another View, so an Element cast always holds.
            foreach (Element view in views)
            {
                if (view is not ViewSheet sheet) continue;
                long sheetId;
                try { sheetId = sheet.Id.Value; }
                catch { continue; }
                AppendUnique(target, sheetId, setName);
            }
        }
    }

    /// <summary>A set is selected when its ElementId value, lowercase Name, or lowercase UniqueId is a ticked token.</summary>
    private static bool IsSelected(ViewSheetSet set, HashSet<long> numericIds, HashSet<string> names, HashSet<string> uniqueIds)
    {
        if (numericIds.Contains(set.Id.Value)) return true;

        string name = CleanText(set.Name).ToLowerInvariant();
        if (name.Length > 0 && names.Contains(name)) return true;

        string uniqueId = CleanText(set.UniqueId).ToLowerInvariant();
        if (uniqueId.Length > 0 && uniqueIds.Contains(uniqueId)) return true;

        return false;
    }

    private static void AppendUnique(Dictionary<long, List<string>> map, long key, string value)
    {
        if (!map.TryGetValue(key, out List<string>? list))
        {
            list = new List<string>();
            map[key] = list;
        }
        if (!list.Contains(value)) list.Add(value);
    }

    /// <summary>Strip CR/LF to spaces and trim — the same normalisation the Dynamo script applies.</summary>
    private static string CleanText(string? value) =>
        value is null ? "" : value.Replace("\r", " ").Replace("\n", " ").Trim();
}
