#if !EDITION_DRW
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.Drw.Enlarge;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Mcp;

/// <summary>
/// The AI Bridge's tools (<see cref="McpContract.Tools"/>), run on the Revit API thread. Each one
/// calls the SAME engine its ribbon button calls — EnlargeCreator, PresetTagger, DatumDimensioner —
/// with a saved preset, so the AI gets the user's tested behaviour, never improvised geometry.
///
/// <para>Every model-changing tool runs inside one <see cref="TransactionGroup"/> that is
/// assimilated, so one Ctrl+Z in Revit undoes one AI call, whatever the engine did inside it.</para>
/// <para>The sheet tools, which have no DRW engine behind them, are in McpSheetTools.cs.</para>
/// </summary>
internal static partial class McpTools
{
    /// <summary>A problem the AI can act on (bad name, nothing open). Reported as-is, no stack.</summary>
    internal sealed class McpError : Exception
    {
        public McpError(string message) : base(message) { }
    }

    private static readonly JsonSerializerOptions PresetJson = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Invoke(UIApplication app, string requestJson)
    {
        JsonObject request = JsonNode.Parse(requestJson) as JsonObject
            ?? throw new McpError("Request is not a JSON object.");
        string tool = (string?)request["tool"] ?? "";
        JsonObject args = request["args"] as JsonObject ?? new JsonObject();
        // KhMcp.exe's own question before it compiles run_code: not a tool the AI sees.
        if (tool == McpContract.CodeRefs)
            return new JsonObject { ["ok"] = true, ["result"] = McpCode.Refs(app) }.ToJsonString();

        McpContract.ToolDef def = McpContract.Tools.FirstOrDefault(t => t.Name == tool)
            ?? throw new McpError($"Unknown tool '{tool}'.");

        WatchUndo(app);
        // run_code works with nothing open too: its code may be what opens the model or family.
        JsonNode? result = tool is "kh_status" or "list_presets" or "get_preset" or "open_model" or "list_actions" or "new_model"
            ? Run(app, null, tool, args)
            : tool == "run_code" ? Run(app, app.ActiveUIDocument?.Document, tool, args)
            : Run(app, ActiveDoc(app), tool, args);
        return new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString();
    }

    public static string ErrorReply(Exception ex)
    {
        Exception e = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
        string message = e is McpError ? e.Message : $"{e.GetType().Name}: {e.Message}";
        return new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString();
    }

    private static JsonNode? Run(UIApplication app, Document? doc, string tool, JsonObject a) => tool switch
    {
        "kh_status" => Status(app),
        "run_code" => RunCode(app, doc, a),
        "open_model" => OpenModel(app, Str(a, "path")),
        "undo" => Undo(app, doc!),
        "list_presets" => ListPresets(Str(a, "tool")),
        "get_preset" => GetPreset(Str(a, "tool"), OptStr(a, "preset")),
        "list_rooms" => ListRooms(doc!, OptStr(a, "name_contains"), OptStr(a, "level")),
        "list_views" => ListViews(doc!, OptStr(a, "kind") ?? "all", OptStr(a, "name_contains")),
        "list_sheets" => ListSheets(doc!),
        "list_levels_grids" => ListLevelsGrids(doc!),
        "export_view_png" => ExportPng(doc!, Str(a, "view"), (int?)a["pixels"] ?? 1600),
        "enlarge_room" => Change(doc!, "Enlarge Room", () => EnlargeRooms(doc!, OptStr(a, "preset"), StrList(a, "rooms"))),
        "preset_tagger" => Change(doc!, "Preset Tagger", () => TagViews(doc!, OptStr(a, "preset"), StrList(a, "views"),
            StrList(a, "categories"), (bool?)a["auto_tag_types"] ?? true, (bool?)a["dry_run"] ?? false)),
        "datum_dimension" => Change(doc!, "Datum Dimension", () => DimensionDatums(doc!, OptStr(a, "preset"), StrList(a, "views"))),
        "list_stairs" => ListStairs(doc!, OptStr(a, "preset")),
        "enlarge_stair" => Change(doc!, "Enlarge Stair", () => EnlargeStairs(doc!, OptStr(a, "preset"), StrList(a, "stairs"),
            (bool?)a["write_marks"] ?? false)),
        "match_datums" => Change(doc!, "Match Datums", () => MatchDatums(doc!, Str(a, "source"), StrList(a, "views"))),
        "stair_annotation" => Change(doc!, "Stair Annotation", () => AnnotateStairs(doc!, OptStr(a, "preset"), StrList(a, "views"))),
        "wall_mat_tag" => Change(doc!, "Wall Mat Tag", () => TagWallMaterials(doc!, OptStr(a, "preset"), StrList(a, "views"),
            (bool?)a["dry_run"] ?? false)),
        "single_font" => Change(doc!, "Single Font", () => SingleFont(doc!, OptStr(a, "preset"), OptStr(a, "font"),
            (bool?)a["dry_run"] ?? false)),
        "create_sheet" => Change(doc!, "Create Sheet", () => CreateSheet(doc!, Str(a, "number"), OptStr(a, "name"),
            OptStr(a, "title_block"), OptStr(a, "like"))),
        "place_viewports" => Change(doc!, "Place Viewports", () => PlaceViewports(doc!, FindSheet(doc!, Str(a, "sheet")),
            FindViews(doc!, StrList(a, "views")), OptStr(a, "layout") ?? "grid", (double?)a["gap_mm"] ?? 10,
            (double?)a["margin_mm"] ?? 10, Area(a["area_mm"]), OptStr(a, "viewport_type"))),
        "align_viewports" => Change(doc!, "Align Viewports", () => AlignViewports(doc!, Str(a, "reference"), StrList(a, "views"),
            OptStr(a, "mode") ?? "model")),
        "param_rules" => ParamRules(doc!),
        "param_rule_set" => Change(doc!, "Parameter Rules", () => ParamRuleSet(doc!, a)),
        "param_apply" => Change(doc!, "Apply Formulas", () => ParamApply(doc!, StrList(a, "rules"), StrList(a, "elements"),
            (bool?)a["dry_run"] ?? false)),
        "mirror_check" => Change(doc!, "Mirror Check", () => MirrorCheck(app, doc!, (bool?)a["dry_run"] ?? false,
            (bool?)a["set_up"] ?? false, (bool?)a["live"])),
        "list_actions" => ListActions(),
        "run_action" => RunAction(app, doc!, Str(a, "name")),
        "new_model" => NewModel(app, Str(a, "path"), OptStr(a, "template"), (bool?)a["overwrite"] ?? false),
        "save_model" => SaveModel(doc!, OptStr(a, "path"), (bool?)a["overwrite"] ?? false),
        "create_view" => Change(doc!, "Create View", () => CreateView(doc!, a)),
        "crop_view" => Change(doc!, "Crop View", () => CropView(doc!, a)),
        "place_text" => Change(doc!, "Place Text", () => PlaceText(doc!, a)),
        "import_vectors" => Change(doc!, "Import Vectors", () => ImportVectors(doc!, a)),
        "pdf_underlay" => Change(doc!, "PDF Underlay", () => PdfUnderlay(doc!, a)),
        _ => throw new McpError($"Tool '{tool}' is listed but not implemented in this KH-Tool build.")
    };

    // ------------------------------------------------------------------ plumbing

    /// <summary>run_code: one undo step in the open model; with none open, doc is null and the run
    /// has nothing to group (a model it opens is not undone by Ctrl+Z either).</summary>
    private static JsonNode RunCode(UIApplication app, Document? doc, JsonObject a)
    {
        string title = OptStr(a, "title") ?? "run";
        JsonObject run() => McpCode.Run(app, doc, Convert.FromBase64String(Str(a, "assembly")), title, OptStr(a, "code") ?? "");
        if (doc != null) return Change(doc, "Code - " + title, run);
        JsonObject result = run();
        result["undo"] = "No model was open, so this call is not an undo step.";
        return result;
    }

    private static Document ActiveDoc(UIApplication app) =>
        app.ActiveUIDocument?.Document ?? throw new McpError("No model is open in Revit.");

    private static string Str(JsonObject a, string key) =>
        OptStr(a, key) ?? throw new McpError($"Argument '{key}' is required.");

    private static string? OptStr(JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static List<string> StrList(JsonObject a, string key) =>
        a[key] is JsonArray arr
            ? arr.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s.Trim() : n?.ToJsonString() ?? "")
                .Where(s => s.Length > 0).ToList()
            : new List<string>();

    private static bool IsAll(List<string> names) => names.Count == 1 && names[0] == "*";

    private static string Mm(double feet) =>
        Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters)).ToString("0");

    /// <summary>One AI call = one undo step. The engines open their own transactions (and Enlarge its
    /// own group) inside this one; assimilating folds them all into a single entry.</summary>
    private static JsonNode Change(Document doc, string title, Func<JsonObject> work)
    {
        if (doc.IsReadOnly) throw new McpError("The model is read-only.");
        using var group = new TransactionGroup(doc, "AI: " + title);
        group.Start();
        BeginAiCall();
        try
        {
            JsonObject result = work();
            group.Assimilate();
            // A dry run, or a call that committed nothing, leaves no step in Revit's undo list.
            bool step = EndAiCall() && result["dry_run"]?.GetValue<bool>() != true;
            if (step) PushAiStep(doc, "AI: " + title);
            result["undo"] = step
                ? $"One Ctrl+Z in Revit (or the undo tool) undoes this call (\"AI: {title}\")."
                : "Nothing was changed, so there is nothing to undo.";
            return result;
        }
        catch
        {
            EndAiCall();
            if (group.HasStarted()) group.RollBack();
            throw;
        }
    }

    /// <summary>Rows sent back at most: a model-wide tool (Single Font: 162 rows on a template)
    /// would otherwise flood the AI's context with lines it only needs counted.</summary>
    private const int MaxRows = 60;

    private static readonly HashSet<string> RoutineStatuses = new(StringComparer.Ordinal)
    {
        "Created", "Changed", "Matched", "Would change", "WouldChange", "Would create", "Loaded", "Would load"
    };

    /// <summary>The engines' result rows, plus a count by Status the AI can read at a glance.
    /// Past <see cref="MaxRows"/>, the rows needing attention (failed, warning, skipped…) are kept
    /// first and the routine ones fill the rest; the summary always counts every row.</summary>
    private static JsonObject Rows(IEnumerable<IReadOnlyDictionary<string, string>> rows, JsonObject? extra = null)
    {
        var all = rows.ToList();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, string> row in all)
        {
            string status = row.TryGetValue("Status", out string? s) && s.Length > 0 ? s : "(none)";
            counts[status] = counts.TryGetValue(status, out int n) ? n + 1 : 1;
        }
        bool routine(IReadOnlyDictionary<string, string> r) => r.TryGetValue("Status", out string? s) && RoutineStatuses.Contains(s);
        List<IReadOnlyDictionary<string, string>> shown = all.Count <= MaxRows
            ? all
            : all.Where(r => !routine(r)).Concat(all.Where(routine)).Take(MaxRows).ToList();

        var list = new JsonArray();
        foreach (IReadOnlyDictionary<string, string> row in shown)
        {
            var o = new JsonObject();
            foreach (KeyValuePair<string, string> kv in row) o[kv.Key] = kv.Value;
            list.Add(o);
        }
        var summary = new JsonObject();
        foreach (KeyValuePair<string, int> kv in counts) summary[kv.Key] = kv.Value;
        var result = extra ?? new JsonObject();
        result["summary"] = summary;
        if (shown.Count < all.Count)
            result["rows_shown"] = $"{shown.Count} of {all.Count}: every row needing attention first, then routine ones; summary counts them all.";
        result["rows"] = list;
        return result;
    }

    // ------------------------------------------------------------------ read-only

    private static JsonNode Status(UIApplication app)
    {
        Document? doc = app.ActiveUIDocument?.Document;
        return new JsonObject
        {
            ["product"] = Product.Name,
            ["version"] = typeof(McpTools).Assembly.GetName().Version?.ToString(),
            ["revit"] = app.Application.VersionNumber + " (" + app.Application.SubVersionNumber + ")",
            ["model"] = doc == null ? null : new JsonObject
            {
                ["title"] = doc.Title,
                ["path"] = doc.PathName,
                ["is_workshared"] = doc.IsWorkshared,
                ["active_view"] = doc.ActiveView?.Name,
                ["modified"] = doc.IsModified,
                ["ai_steps"] = AiSteps(doc),
                ["last_undo"] = _lastUndo
            },
            ["tools"] = new JsonArray(McpContract.Tools.Select(t => (JsonNode)t.Name).ToArray())
        };
    }

    private sealed class PresetKind
    {
        public Func<IEnumerable<string>> Names = () => Array.Empty<string>();
        public Func<string?> LastUsed = () => null;
        public Func<string?, object?> Load = _ => null;
    }

    private static PresetKind Kind<T>(DrwPresetStore<T> store) where T : class, IDrwPreset, new() => new()
    {
        Names = () => store.LoadAll().Select(p => p.Name),
        LastUsed = () => store.TryLoadLastUsed()?.Name,
        Load = name => name == null ? store.TryLoadLastUsed() ?? new T() : store.Load(name)
    };

    private static PresetKind KindFor(string tool) => tool switch
    {
        "enlarge_room" => Kind(EnlargeStores.Room),
        "enlarge_stair" => Kind(EnlargeStores.Stair),
        "datum_dimension" => Kind(DatumDimensionStore.Store),
        "preset_tagger" => Kind(PresetTaggerStore.Store),
        "stair_annotation" => Kind(StairAnnotationStore.Store),
        "single_font" => Kind(SingleFontStore.Store),
        "wall_mat_tag" => Kind(WallMatTagStore.Store),
        _ => throw new McpError($"No presets for '{tool}'. Tools with presets: {string.Join(", ", McpContract.PresetTools)}.")
    };

    private static JsonNode ListPresets(string tool)
    {
        PresetKind kind = KindFor(tool);
        return new JsonObject
        {
            ["tool"] = tool,
            ["saved"] = new JsonArray(kind.Names().Select(n => (JsonNode)n).ToArray()),
            ["last_used"] = kind.LastUsed()
        };
    }

    private static JsonNode GetPreset(string tool, string? name)
    {
        object preset = KindFor(tool).Load(name) ?? throw new McpError($"No {tool} preset named '{name}'.");
        return JsonNode.Parse(JsonSerializer.Serialize(preset, preset.GetType(), PresetJson))!;
    }

    /// <summary>A saved preset by name; without one, the last used, then the built-in default.</summary>
    private static T LoadPreset<T>(DrwPresetStore<T> store, string? name, string tool, out string used)
        where T : class, IDrwPreset, new()
    {
        if (name != null)
        {
            used = name;
            return store.Load(name) ?? throw new McpError(
                $"No {tool} preset named '{name}'. Saved: {string.Join(", ", store.LoadAll().Select(p => p.Name))}.");
        }
        if (store.TryLoadLastUsed() is T last)
        {
            used = last.Name + " (last used)";
            return last;
        }
        used = "built-in default";
        return new T();
    }

    private static JsonNode ListRooms(Document doc, string? nameContains, string? level)
    {
        var list = new JsonArray();
        foreach (Room r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType().OfType<Room>()
                     .OrderBy(r => r.Number, StringComparer.OrdinalIgnoreCase))
        {
            string name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
            string lvl = r.Level?.Name ?? "";
            if (nameContains != null && name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (level != null && !lvl.Equals(level, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new JsonObject
            {
                ["id"] = r.Id.Value,
                ["number"] = r.Number,
                ["name"] = name,
                ["level"] = lvl,
                ["area_m2"] = Math.Round(UnitUtils.ConvertFromInternalUnits(r.Area, UnitTypeId.SquareMeters), 2),
                ["placed"] = r.Location != null && r.Area > 0
            });
        }
        return list;
    }

    private static string KindOf(View v) => v.ViewType switch
    {
        ViewType.FloorPlan or ViewType.EngineeringPlan or ViewType.AreaPlan => "plan",
        ViewType.CeilingPlan => "ceiling",
        ViewType.Section => "section",
        ViewType.Elevation => "elevation",
        ViewType.ThreeD => "3d",
        ViewType.DraftingView => "drafting",
        ViewType.Legend => "legend",
        ViewType.Schedule or ViewType.PanelSchedule or ViewType.ColumnSchedule => "schedule",
        ViewType.DrawingSheet => "sheet",
        _ => v.ViewType.ToString().ToLowerInvariant()
    };

    private static JsonNode ListViews(Document doc, string kind, string? nameContains)
    {
        var list = new JsonArray();
        foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                     .Where(v => !v.IsTemplate && v.ViewType != ViewType.Internal && v.ViewType != ViewType.ProjectBrowser
                                 && v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.Undefined)
                     .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            string k = KindOf(v);
            if (kind != "all" && k != kind) continue;
            if (nameContains != null && v.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            string sheet = v.get_Parameter(BuiltInParameter.VIEWER_SHEET_NUMBER)?.AsString() ?? "";
            list.Add(new JsonObject
            {
                ["id"] = v.Id.Value,
                ["name"] = v.Name,
                ["kind"] = k,
                ["scale"] = v.ViewType is ViewType.ThreeD or ViewType.DrawingSheet or ViewType.Schedule ? null : (JsonNode)("1:" + v.Scale),
                ["sheet"] = sheet.Length == 0 || sheet == "---" ? null : sheet
            });
        }
        return list;
    }

    private static JsonNode ListSheets(Document doc)
    {
        var list = new JsonArray();
        foreach (ViewSheet s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                     .Where(s => !s.IsTemplate).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new JsonObject
            {
                ["id"] = s.Id.Value,
                ["number"] = s.SheetNumber,
                ["name"] = s.Name,
                ["views"] = new JsonArray(s.GetAllPlacedViews().Select(doc.GetElement).OfType<View>()
                    .Select(v => (JsonNode)v.Name).ToArray()),
                // Schedules on a sheet are not "placed views", but they take up the sheet all the same.
                ["schedules"] = new JsonArray(new FilteredElementCollector(doc, s.Id).OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>().Where(i => !i.IsTitleblockRevisionSchedule)
                    .Select(i => (JsonNode)(doc.GetElement(i.ScheduleId)?.Name ?? "?")).ToArray())
            });
        }
        return list;
    }

    private static JsonNode ListLevelsGrids(Document doc) => new JsonObject
    {
        ["levels"] = new JsonArray(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation)
            .Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["elevation_mm"] = double.Parse(Mm(l.Elevation)) })
            .ToArray()),
        ["grids"] = new JsonArray(new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
            .Select(g => g.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => (JsonNode)n).ToArray())
    };

    /// <summary>A view by id, exact name, or (sheets) number / "number - name".</summary>
    private static View FindView(Document doc, string key)
    {
        if (long.TryParse(key, out long id) && doc.GetElement(new ElementId(id)) is View byId) return byId;
        List<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        View? hit = views.FirstOrDefault(v => v.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? views.OfType<ViewSheet>().FirstOrDefault(s =>
                        s.SheetNumber.Equals(key, StringComparison.OrdinalIgnoreCase)
                        || (s.SheetNumber + " - " + s.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;
        List<string> near = views.Where(v => v.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(v => v.Name).Take(8).ToList();
        throw new McpError($"No view or sheet '{key}'." + (near.Count > 0 ? " Did you mean: " + string.Join(" | ", near) + "?" : " See list_views."));
    }

    private static List<View> FindViews(Document doc, List<string> keys) => keys.Select(k => FindView(doc, k)).ToList();

    private static JsonNode ExportPng(Document doc, string viewKey, int pixels)
    {
        View view = FindView(doc, viewKey);
        string dir = Path.Combine(Path.GetTempPath(), "KhMcp", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(dir);
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(dir, "view"),
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = Math.Max(400, Math.Min(4000, pixels)),
            ImageResolution = ImageResolution.DPI_150,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            FitDirection = FitDirectionType.Horizontal
        };
        options.SetViewsAndSheets(new List<ElementId> { view.Id });
        doc.ExportImage(options);
        string? file = Directory.GetFiles(dir, "*.png").FirstOrDefault()
            ?? throw new McpError($"Revit exported no image for '{view.Name}'.");
        return new JsonObject { ["view"] = view.Name, ["path"] = file };
    }
}
#endif
