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

    // ------------------------------------------------------------------ DRW tools

    private static JsonObject EnlargeRooms(Document doc, string? presetName, List<string> rooms)
    {
        if (rooms.Count == 0) throw new McpError("Name the rooms to enlarge (or ['*'] for the preset's batch filter).");
        EnlargePreset preset = LoadPreset(EnlargeStores.Room, presetName, "enlarge_room", out string used);
        List<EnlargeTarget> targets = MatchTargets(EnlargeTargets.Rooms(doc, preset.RoomLabel), rooms, preset.BatchFilter,
            "room", out JsonArray notFound);

        List<IReadOnlyDictionary<string, string>> rows = new EnlargeCreator(doc, preset, stairs: false).Create(targets, writeMarks: false);
        return Rows(rows, new JsonObject
        {
            ["preset"] = used,
            ["rooms"] = new JsonArray(targets.Select(t => (JsonNode)t.Display).ToArray()),
            ["not_found"] = notFound
        });
    }

    /// <summary>The targets the AI named — by id, number, name, "number name" or the listed name,
    /// then by a part of the listed name — or, for ['*'], those the preset's batch filter takes.</summary>
    private static List<EnlargeTarget> MatchTargets(List<EnlargeTarget> all, List<string> keys, string? batchFilter,
        string noun, out JsonArray notFound)
    {
        var targets = new List<EnlargeTarget>();
        notFound = new JsonArray();
        if (IsAll(keys))
        {
            string filter = batchFilter?.Trim() ?? "";
            targets = all.Where(t => filter.Length == 0 || t.BaseName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
        else
        {
            foreach (string key in keys)
            {
                bool eq(string? s) => s != null && s.Equals(key, StringComparison.OrdinalIgnoreCase);
                List<EnlargeTarget> hits = all.Where(t => eq(t.SourceId.Value.ToString()) || eq(t.Number) || eq(t.BaseName)
                                                         || eq(t.Number + " " + t.BaseName) || eq(t.Display)).ToList();
                if (hits.Count == 0)
                    hits = all.Where(t => t.Display.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 0) notFound.Add(key);
                foreach (EnlargeTarget t in hits)
                    if (!targets.Any(x => x.SourceId == t.SourceId)) targets.Add(t);
            }
        }
        if (targets.Count == 0)
            throw new McpError(all.Count == 0
                ? $"This model has no {noun}s to enlarge."
                : $"No {noun} matched. {char.ToUpperInvariant(noun[0])}{noun.Substring(1)}s here: " +
                  string.Join(" | ", all.Select(t => t.Display).Take(40)) + ".");
        return targets;
    }

    private static List<EnlargeTarget> StairTargets(Document doc, EnlargePreset p) =>
        EnlargeTargets.Stairs(doc, p.MarkDelimiter, p.OverrideMultistoryMarks, p.StairBeforeFirstStepM, p.StairAfterLastStepM);

    private static JsonNode ListStairs(Document doc, string? presetName)
    {
        EnlargePreset preset = LoadPreset(EnlargeStores.Stair, presetName, "enlarge_stair", out _);
        return new JsonArray(StairTargets(doc, preset).Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.SourceId.Value,
            ["name"] = t.Display,
            ["kind"] = t.Kind,
            ["levels"] = new JsonArray(t.PlanLevels.Select(l => (JsonNode)l.Name).ToArray())
        }).ToArray());
    }

    private static JsonObject EnlargeStairs(Document doc, string? presetName, List<string> stairs, bool writeMarks)
    {
        if (stairs.Count == 0) throw new McpError("Name the stairs to enlarge (or ['*'] for the preset's batch filter).");
        EnlargePreset preset = LoadPreset(EnlargeStores.Stair, presetName, "enlarge_stair", out string used);
        List<EnlargeTarget> targets = MatchTargets(StairTargets(doc, preset), stairs, preset.BatchFilter, "stair", out JsonArray notFound);

        // The ribbon asks before renumbering; here the AI must have asked, so marks change only when told to.
        bool marks = writeMarks && preset.OverrideMultistoryMarks;
        List<IReadOnlyDictionary<string, string>> rows = new EnlargeCreator(doc, preset, stairs: true).Create(targets, marks);
        return Rows(rows, new JsonObject
        {
            ["preset"] = used,
            ["stairs"] = new JsonArray(targets.Select(t => (JsonNode)t.Display).ToArray()),
            ["not_found"] = notFound,
            ["marks_written"] = marks
        });
    }

    private static string CategoryKey(string s)
    {
        if (s.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) return "OST_" + s.Substring(4);
        string k = s.Trim().Replace(" ", "");
        // "doors" → "OST_Doors": the BuiltInCategory names are plural PascalCase.
        return "OST_" + char.ToUpperInvariant(k[0]) + k.Substring(1);
    }

    private static JsonObject TagViews(Document doc, string? presetName, List<string> viewKeys, List<string> categories,
        bool autoTypes, bool dryRun)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the views to tag (or ['*'] for the preset's own name filter).");
        PresetTaggerPreset preset = LoadPreset(PresetTaggerStore.Store, presetName, "preset_tagger", out string used);
        preset.DryRun = dryRun;

        if (categories.Count > 0)
        {
            var keys = categories.Select(CategoryKey).ToList();
            var rules = new List<TagRule>();
            foreach (string key in keys)
            {
                List<TagRule> existing = preset.Rules.Where(r => r.Category.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
                rules.AddRange(existing.Count > 0 ? existing : new List<TagRule> { new() { Category = key } });
            }
            preset.Rules = rules;
        }

        var chosen = new JsonObject();
        if (autoTypes)
        {
            foreach (TagRule rule in preset.Rules.Where(r => r.Enabled && !r.TagsMaterial() && string.IsNullOrWhiteSpace(r.TagType)))
            {
                if (!Enum.TryParse(rule.Category, true, out BuiltInCategory cat)) continue;
                // The category's own tag (M_Window Tag) before a multi-category one, which sorts
                // first by name but shows the wrong label.
                long own = (long?)PresetTagger.TagCategory(cat) ?? -1;
                FamilySymbol? first = PresetTagger.TagTypes(doc, cat)
                    .OrderBy(s => s.Category?.Id.Value == own ? 0 : 1).FirstOrDefault();
                if (first == null) continue;
                rule.TagType = DrwLookup.TypeLabel(first);
                chosen[rule.Category] = rule.TagType;
            }
        }

        List<View> views = IsAll(viewKeys)
            ? DrwScope.ResolveTagViews(doc, null, DrwViewScope.AllPlans, preset.ViewNameContains,
                preset.Plans, preset.CeilingPlans, preset.Sections, preset.Elevations)
            : FindViews(doc, viewKeys);
        List<string> skipped = views.Where(v => !PresetTagger.IsTagView(v)).Select(v => v.Name).ToList();
        views = views.Where(PresetTagger.IsTagView).ToList();
        if (views.Count == 0) throw new McpError("None of those views can take tags (plans, ceiling plans, sections, elevations).");

        return Rows(PresetTagger.Run(doc, views, preset), new JsonObject
        {
            ["preset"] = used,
            ["views"] = new JsonArray(views.Select(v => (JsonNode)v.Name).ToArray()),
            ["views_skipped"] = new JsonArray(skipped.Select(n => (JsonNode)n).ToArray()),
            ["auto_tag_types"] = chosen,
            ["dry_run"] = dryRun
        });
    }

    private static JsonObject DimensionDatums(Document doc, string? presetName, List<string> viewKeys)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the views to dimension (or ['*'] for the preset's own name filter).");
        DatumDimensionPreset preset = LoadPreset(DatumDimensionStore.Store, presetName, "datum_dimension", out string used);
        List<View> views = IsAll(viewKeys)
            ? DrwScope.ResolveDatumViews(doc, null, DrwViewScope.AllPlans, preset.ViewNameContains,
                preset.Plans, preset.Sections, preset.Elevations)
            : FindViews(doc, viewKeys);
        List<string> skipped = views.Where(v => !DrwScope.IsDatumView(v)).Select(v => v.Name).ToList();
        views = views.Where(DrwScope.IsDatumView).ToList();
        if (views.Count == 0) throw new McpError("None of those views has datums to dimension (plans, sections, elevations).");

        return Rows(DatumDimensioner.Run(doc, views, preset), new JsonObject
        {
            ["preset"] = used,
            ["views"] = new JsonArray(views.Select(v => (JsonNode)v.Name).ToArray()),
            ["views_skipped"] = new JsonArray(skipped.Select(n => (JsonNode)n).ToArray())
        });
    }

    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode)n).ToArray());

    private static JsonObject MatchDatums(Document doc, string sourceKey, List<string> viewKeys)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the target views (or ['*'] for every parallel view at the source's scale).");
        View source = FindView(doc, sourceKey);
        if (!DatumMatcher.IsMatchView(source))
            throw new McpError($"'{source.Name}' is not a plan, ceiling plan, section or elevation.");
        List<View> candidates = DatumMatcher.Candidates(doc, source);

        // The ribbon only offers candidates; a named view that is not one is reported, not forced.
        var skipped = new JsonArray();
        List<View> targets;
        if (IsAll(viewKeys)) targets = candidates;
        else
        {
            targets = new List<View>();
            foreach (View v in FindViews(doc, viewKeys))
            {
                if (candidates.Any(c => c.Id == v.Id)) { if (!targets.Contains(v)) targets.Add(v); }
                else skipped.Add($"{v.Name}: " + (v.Id == source.Id ? "it is the source"
                    : !DatumMatcher.IsMatchView(v) ? "not a plan, ceiling plan, section or elevation"
                    : v.Scale != source.Scale ? $"1:{v.Scale}, the source is 1:{source.Scale}"
                    : "not parallel to the source"));
            }
        }
        if (targets.Count == 0)
            throw new McpError($"No target view can take '{source.Name}''s datums." +
                               (skipped.Count > 0 ? " " + string.Join("; ", skipped.Select(n => (string?)n)) : "") +
                               (candidates.Count > 0 ? " Parallel views at 1:" + source.Scale + ": " +
                                   string.Join(" | ", candidates.Select(c => c.Name).Take(30)) : ""));

        return Rows(DatumMatcher.Run(doc, source, targets), new JsonObject
        {
            ["source"] = source.Name,
            ["views"] = Names(targets.Select(v => v.Name)),
            ["views_skipped"] = skipped
        });
    }

    private static JsonObject AnnotateStairs(Document doc, string? presetName, List<string> viewKeys)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the views to annotate (or ['*'] for the preset's own name filter).");
        StairAnnotationPreset preset = LoadPreset(StairAnnotationStore.Store, presetName, "stair_annotation", out string used);
        List<View> views = IsAll(viewKeys)
            ? DrwScope.ResolveStairViews(doc, null, DrwViewScope.AllPlans, preset.ViewNameContains, preset.Sections)
            : FindViews(doc, viewKeys);
        List<string> skipped = views.Where(v => !StairAnnotator.IsStairView(v)).Select(v => v.Name).ToList();
        views = views.Where(StairAnnotator.IsStairView).ToList();
        if (views.Count == 0) throw new McpError("None of those views is a floor plan or section.");

        return Rows(StairAnnotator.Run(doc, views, preset), new JsonObject
        {
            ["preset"] = used,
            ["views"] = Names(views.Select(v => v.Name)),
            ["views_skipped"] = Names(skipped)
        });
    }

    private static JsonObject TagWallMaterials(Document doc, string? presetName, List<string> viewKeys, bool dryRun)
    {
        if (viewKeys.Count == 0) throw new McpError("Name the views to tag (or ['*'] for the preset's own name filter).");
        WallMatTagPreset preset = DrwPresetStore<WallMatTagPreset>.Clone(
            LoadPreset(WallMatTagStore.Store, presetName, "wall_mat_tag", out string used));
        preset.DryRun = dryRun;
        preset.Scope = DrwViewScope.AllPlans;

        bool wallView(View v) => DrwScope.IsPlan(v) || (!v.IsTemplate && v.ViewType is ViewType.Section or ViewType.Elevation);
        List<View> views = IsAll(viewKeys) ? WallMatTag.Views(doc, null, preset) : FindViews(doc, viewKeys);
        List<string> skipped = views.Where(v => !wallView(v)).Select(v => v.Name).ToList();
        views = views.Where(wallView).ToList();
        if (views.Count == 0) throw new McpError("None of those views is a plan, section or elevation.");

        var made = new List<ElementId>();
        JsonObject result = Rows(WallMatTag.Run(doc, views, preset, made), new JsonObject
        {
            ["preset"] = used,
            ["views"] = Names(views.Select(v => v.Name)),
            ["views_skipped"] = Names(skipped),
            ["dry_run"] = dryRun
        });
        if (made.Count > 0)
            result["material_tags_to_touch"] =
                $"{made.Count} new material tag(s) will read '?' until touched in Revit (Revit's API cannot link a new material tag " +
                "to its material). Ask the user to select them and nudge them with an arrow key and back.";
        return result;
    }

    private static JsonObject SingleFont(Document doc, string? presetName, string? font, bool dryRun)
    {
        SingleFontPreset preset = DrwPresetStore<SingleFontPreset>.Clone(
            LoadPreset(SingleFontStore.Store, presetName, "single_font", out string used));
        if (font != null) preset.FontName = font;
        preset.DryRun = dryRun;
        if (!SingleFontEngine.FontInstalled(preset.FontName))
            throw new McpError($"The font '{preset.FontName}' is not installed on this PC.");

        return Rows(SingleFontEngine.Run(doc, preset), new JsonObject
        {
            ["preset"] = used,
            ["font"] = preset.FontName,
            ["dry_run"] = dryRun
        });
    }
}
#endif
