using System.Security.Principal;

namespace KhBatchRunner.Mcp;

/// <summary>
/// The contract between KhMcp.exe (the MCP server Claude Code starts) and the AI Bridge inside
/// KH-Tool: the pipe name, the wire format, and every tool's name, description and input schema.
/// Compiled into BOTH projects (KhMcp links this file), so the tool list the AI sees and the tools
/// the bridge serves can never drift apart. Plain strings only — no Revit types, no JSON library —
/// because KhMcp is a net48 exe with no dependencies.
///
/// <para>Wire format: the client opens the pipe, writes ONE line of JSON
/// <c>{"tool":"name","args":{...}}</c>, and reads ONE line back:
/// <c>{"ok":true,"result":...}</c> or <c>{"ok":false,"error":"..."}</c>. UTF-8, no BOM.</para>
/// </summary>
internal static class McpContract
{
    public const string ServerName = "kh-tool";

    /// <summary>One pipe per Windows user: the pipe is ACL'd to that user, and the name carries the
    /// SID so two people on one machine never reach each other's Revit.</summary>
    public static string PipeName() =>
        "KhMcp-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    public sealed class ToolDef
    {
        public string Name { get; }
        public string Description { get; }
        /// <summary>JSON Schema for the tool's arguments, as JSON text.</summary>
        public string InputSchema { get; }
        /// <summary>Changes the model: the bridge wraps the call in one undoable step.</summary>
        public bool Modifies { get; }

        public ToolDef(string name, bool modifies, string description, string inputSchema)
        {
            Name = name;
            Modifies = modifies;
            Description = description;
            InputSchema = inputSchema;
        }
    }

    /// <summary>Schemas are written with ` for " so they read as JSON without escaping.</summary>
    private static string J(string s) => s.Replace('`', '"');

    private static readonly string NoArgs = J("{`type`:`object`,`properties`:{}}");

    /// <summary>The DRW tools that keep saved presets, by the key the preset tools take.</summary>
    public static readonly string[] PresetTools =
    {
        "enlarge_room", "enlarge_stair", "datum_dimension", "preset_tagger",
        "stair_annotation", "single_font", "wall_mat_tag"
    };

    private const string PresetToolArg =
        "`tool`:{`type`:`string`,`enum`:[`enlarge_room`,`enlarge_stair`,`datum_dimension`,`preset_tagger`,`stair_annotation`,`single_font`,`wall_mat_tag`]}";

    private const string PresetArg =
        "`preset`:{`type`:`string`,`description`:`Saved preset name (see list_presets). Omit to use the preset last used in Revit, or the built-in default when there is none.`}";

    private const string ViewsArg =
        "`views`:{`type`:`array`,`items`:{`type`:`string`},`description`:`View names or ids. ['*'] = the views the saved preset's own name filter takes.`}";

    public static readonly ToolDef[] Tools =
    {
        new("kh_status", false,
            "The open model (title, path, active view) and the KH-Tool version. Call this first.",
            NoArgs),

        new("open_model", true,
            "Open a Revit model (.rvt) or family (.rfa) and make it the active document, or switch to it when it is already open. " +
            "Documents already open stay open. A workshared central model is opened detached, so nothing syncs back.",
            J("{`type`:`object`,`properties`:{`path`:{`type`:`string`,`description`:`Full path of the .rvt or .rfa file.`}},`required`:[`path`]}")),

        new("undo", true,
            "Undo the last AI call in the active model - the same as the user pressing Ctrl+Z once. Refused when the newest " +
            "step in Revit's undo list is not an AI call (the user changed something since), so it never undoes the user's own work. " +
            "Revit runs the undo just after this call returns: call kh_status to see it done (ai_steps).",
            NoArgs),

        new("list_presets", false,
            "The saved presets of one DRW Helper tool, and which one was used last.",
            J("{`type`:`object`,`properties`:{" + PresetToolArg + "},`required`:[`tool`]}")),

        new("get_preset", false,
            "Every setting of one saved preset, as JSON.",
            J("{`type`:`object`,`properties`:{" + PresetToolArg + "," + PresetArg + "},`required`:[`tool`]}")),

        new("list_rooms", false,
            "Rooms in the model: id, number, name, level, area (m2), placed or not.",
            J("{`type`:`object`,`properties`:{`name_contains`:{`type`:`string`},`level`:{`type`:`string`}}}")),

        new("list_stairs", false,
            "Stairs as Enlarge Stair lists them (a multistory stair is one entry): id, name, kind, levels.",
            J("{`type`:`object`,`properties`:{" + PresetArg + "}}")),

        new("list_views", false,
            "Views (not templates): id, name, kind, scale, and the sheet each sits on.",
            J("{`type`:`object`,`properties`:{`kind`:{`type`:`string`,`enum`:[`all`,`plan`,`ceiling`,`section`,`elevation`,`3d`,`drafting`,`legend`,`schedule`,`sheet`]},`name_contains`:{`type`:`string`}}}")),

        new("list_sheets", false,
            "Sheets: id, number, name, and the views placed on each.",
            NoArgs),

        new("list_levels_grids", false,
            "Levels (name, elevation mm) and grids (name).",
            NoArgs),

        new("export_view_png", false,
            "Export a view or sheet to PNG and return the image, so you can check the result of your work. " +
            "A sheet can be named by its number, its name, or 'number - name'.",
            J("{`type`:`object`,`properties`:{`view`:{`type`:`string`,`description`:`View or sheet name, or element id.`},`pixels`:{`type`:`integer`,`description`:`Image width in pixels (default 1600, max 4000).`}},`required`:[`view`]}")),

        new("enlarge_room", true,
            "DRW Helper Enlarge Room: makes the enlarged plan (and the interior elevations or sections the preset asks for) " +
            "for the named rooms, exactly as the ribbon tool does. One Ctrl+Z in Revit undoes the whole call.",
            J("{`type`:`object`,`properties`:{" + PresetArg + ",`rooms`:{`type`:`array`,`items`:{`type`:`string`},`description`:`Rooms by number, name, 'number name' or element id. ['*'] = every room the preset's batch filter takes.`}},`required`:[`rooms`]}")),

        new("preset_tagger", true,
            "DRW Helper Preset Tagger: tags elements in the named views by the preset's rules (door, window, room, wall tags). " +
            "One Ctrl+Z undoes the whole call.",
            J("{`type`:`object`,`properties`:{" + PresetArg + "," + ViewsArg + ",`categories`:{`type`:`array`,`items`:{`type`:`string`},`description`:`Only these rules, e.g. ['doors','windows','rooms'] or 'OST_Doors'. A category the preset has no rule for gets a default rule.`},`auto_tag_types`:{`type`:`boolean`,`description`:`Default true: a rule with no tag type uses the first tag type loaded for its category.`},`dry_run`:{`type`:`boolean`,`description`:`Report what would be tagged; change nothing.`}},`required`:[`views`]}")),

        new("datum_dimension", true,
            "DRW Helper Datum Dimension: dimension strings to grids (plans) and grids + levels (sections, elevations) " +
            "in the named views, by the preset. One Ctrl+Z undoes the whole call.",
            J("{`type`:`object`,`properties`:{" + PresetArg + "," + ViewsArg + "},`required`:[`views`]}")),

        new("enlarge_stair", true,
            "DRW Helper Enlarge Stair: makes the enlarged plans (one per level) and sections for the named stairs, " +
            "exactly as the ribbon tool does. A multistory stair is one target. One Ctrl+Z undoes the whole call.",
            J("{`type`:`object`,`properties`:{" + PresetArg + ",`stairs`:{`type`:`array`,`items`:{`type`:`string`},`description`:`Stairs by the name list_stairs shows, or element id. ['*'] = every stair the preset's batch filter takes.`},`write_marks`:{`type`:`boolean`,`description`:`Default false. True renumbers multistory stair members' Mark as the preset says (the ribbon asks the user first - ask the user before setting this).`}},`required`:[`stairs`]}")),

        new("match_datums", true,
            "DRW Helper Match Datums: copies the source view's grid/level ends, bubbles and elbows onto the target views. " +
            "Targets must be parallel to the source and at the same scale (plans for a plan; sections/elevations facing the same " +
            "or opposite way). One Ctrl+Z undoes the whole call.",
            J("{`type`:`object`,`properties`:{`source`:{`type`:`string`,`description`:`The view whose datums are arranged as wanted.`},`views`:{`type`:`array`,`items`:{`type`:`string`},`description`:`Target view names or ids. ['*'] = every parallel view at the source's scale.`}},`required`:[`source`,`views`]}")),

        new("stair_annotation", true,
            "DRW Helper Stair Annotation: stair path, tread/riser numbers and (plans) run dimensions in the named floor plans " +
            "and sections, by the preset. One Ctrl+Z undoes the whole call.",
            J("{`type`:`object`,`properties`:{" + PresetArg + "," + ViewsArg + "},`required`:[`views`]}")),

        new("wall_mat_tag", true,
            "DRW Helper Wall Mat Tag: a material tag on each finish face of the walls cut in plans/sections (and seen in elevations), " +
            "by the preset. New material tags read '?' until the user touches them in Revit - tell the user. One Ctrl+Z undoes the whole call.",
            J("{`type`:`object`,`properties`:{" + PresetArg + "," + ViewsArg + ",`dry_run`:{`type`:`boolean`,`description`:`Report what would be tagged; change nothing.`}},`required`:[`views`]}")),

        new("single_font", true,
            "DRW Helper Single Font: one font for the model's text, dimension and model-text types, schedule fonts and the text " +
            "in loaded families, as the preset ticks. Model-wide. One Ctrl+Z undoes the whole call.",
            J("{`type`:`object`,`properties`:{" + PresetArg + ",`font`:{`type`:`string`,`description`:`Font to use instead of the preset's, e.g. 'Arial'.`},`dry_run`:{`type`:`boolean`,`description`:`Report what would change; change nothing.`}}}")),

        new("create_sheet", true,
            "Create a new sheet with a number and name. Its title block is the one named, or the one a 'like' sheet uses, or else " +
            "the title block most sheets in the model use. Refused when the number is taken. One Ctrl+Z undoes it.",
            J("{`type`:`object`,`properties`:{`number`:{`type`:`string`},`name`:{`type`:`string`},`title_block`:{`type`:`string`,`description`:`Title block type, 'Family : Type' or the type name.`},`like`:{`type`:`string`,`description`:`An existing sheet whose title block to use.`}},`required`:[`number`]}")),

        new("place_viewports", true,
            "Place views on a sheet, laid out inside the title block's drawing frame (border and title strip found from its lines) or the area given. " +
            "An empty sheet gets a row, column or grid; a sheet with content gets each view in the first free spot, never covering " +
            "what is there (views, schedules, notes); so do views too many for one sheet. A view with no room left is reported " +
            "(Skipped), not placed: create the next sheet (create_sheet) and place it there. Views already on another sheet are " +
            "reported, not moved. Schedules are not placed. " +
            "One Ctrl+Z undoes the whole call.",
            J("{`type`:`object`,`properties`:{`sheet`:{`type`:`string`,`description`:`Sheet number, name, 'number - name' or id.`},`views`:{`type`:`array`,`items`:{`type`:`string`},`description`:`Views in placing order (left to right, top to bottom).`},`layout`:{`type`:`string`,`enum`:[`grid`,`row`,`column`],`description`:`Default grid: rows filled left to right, wrapping at the area's width.`},`gap_mm`:{`type`:`number`,`description`:`Paper gap between viewports, default 10.`},`margin_mm`:{`type`:`number`,`description`:`Paper margin inside the title block's drawing frame (its border, and its title strip where it has one), default 10.`},`area_mm`:{`type`:`object`,`description`:`Usable area on the sheet in paper mm from the sheet origin, instead of the title block less the margin (e.g. to keep off the title strip).`,`properties`:{`left`:{`type`:`number`},`bottom`:{`type`:`number`},`right`:{`type`:`number`},`top`:{`type`:`number`}},`required`:[`left`,`bottom`,`right`,`top`]},`viewport_type`:{`type`:`string`,`description`:`Viewport type name (title style); default leaves Revit's.`}},`required`:[`sheet`,`views`]}")),

        new("align_viewports", true,
            "Align viewports to a reference viewport. mode 'model' (default) moves each target so the same model point sits at the " +
            "same spot on its sheet as on the reference's (plans or parallel sections at the same scale, on the same or other sheets). " +
            "The edge modes line up the viewport boxes instead. One Ctrl+Z undoes the whole call.",
            J("{`type`:`object`,`properties`:{`reference`:{`type`:`string`,`description`:`The view whose viewport stays put.`},`views`:{`type`:`array`,`items`:{`type`:`string`},`description`:`Views whose viewports move. ['*'] = every other viewport on the reference's sheet.`},`mode`:{`type`:`string`,`enum`:[`model`,`left`,`right`,`top`,`bottom`,`center_x`,`center_y`]}},`required`:[`reference`,`views`]}")),

        new("param_rules", false,
            "KH-Tool Parameter Helper: the formula rules saved in the model (id, categories, target parameter, formula, " +
            "refused-and-why), whether live mode recalculates as people edit, and the Mirror Check setting.",
            NoArgs),

        new("param_rule_set", true,
            "KH-Tool Parameter Helper: add a formula rule, replace one (id + the fields to change), delete one (id + delete:true), " +
            "and/or turn live mode on or off (live). The rule is checked against real elements and refused with the reason when it " +
            "would not work. Formulas read parameters as [Name] or [type:Name], e.g. '[Width] * [Height] / 1000000' or " +
            "'if([Fire Rating] = \"\", \"-\", [Fire Rating])'. Saving does not write values: call param_apply. One Ctrl+Z undoes it.",
            J("{`type`:`object`,`properties`:{`id`:{`type`:`string`,`description`:`The rule to replace or delete (param_rules). Omit to add.`},`categories`:{`type`:`array`,`items`:{`type`:`string`},`description`:`e.g. ['Walls'] or ['OST_Doors','OST_Windows'].`},`target`:{`type`:`string`,`description`:`Parameter the result is written to (writable text, number, integer or yes/no).`},`target_on_type`:{`type`:`boolean`,`description`:`The target is a type parameter (one value per type). Found from the model when omitted.`},`formula`:{`type`:`string`},`enabled`:{`type`:`boolean`},`note`:{`type`:`string`},`delete`:{`type`:`boolean`},`live`:{`type`:`boolean`,`description`:`Recalculate as people edit the model.`}}}")),

        new("param_apply", true,
            "KH-Tool Parameter Helper Apply Now: works the model's rules out and writes ONLY values that differ (in a workshared " +
            "model it borrows only the elements that change). Use dry_run first to see what would change. One Ctrl+Z undoes it.",
            J("{`type`:`object`,`properties`:{`rules`:{`type`:`array`,`items`:{`type`:`string`},`description`:`Only these rules, by id or target name. Default: every enabled rule.`},`elements`:{`type`:`array`,`items`:{`type`:`string`},`description`:`Only these element ids. Default: every element the rules cover.`},`dry_run`:{`type`:`boolean`,`description`:`Report what would change; change nothing.`}}}")),

        new("mirror_check", true,
            "KH-Tool Mirror Check: writes each family instance's mirrored / hand-flipped / facing-flipped state into KH_Mirror State " +
            "(or the model's chosen shared text parameter), only where it differs, and returns the counts. dry_run counts without " +
            "writing. When the parameter is not set up yet, ask the user before set_up:true (it adds a shared parameter to every " +
            "model category). One Ctrl+Z undoes it.",
            J("{`type`:`object`,`properties`:{`dry_run`:{`type`:`boolean`},`set_up`:{`type`:`boolean`,`description`:`Add KH_Mirror State to the model first when it is missing. Ask the user first.`},`live`:{`type`:`boolean`,`description`:`Keep the state up to date as people edit (saved in the model).`}}}")),

        new("list_actions", false,
            "KH-Tool Multi Model Check / Action buttons that can run on the active model: name, whether it changes the model, " +
            "and the columns of its report.",
            NoArgs),

        new("run_code", true,
            "Run your own C# in Revit, for what no other tool here does (read anything, or change the model). Only " +
            "while the user has AI Code ON in Revit (KH-Tool tab > Setting); otherwise it is refused: ask them. `code` is the " +
            "BODY of `public static object Run(UIApplication app, Document doc, TextWriter log)`; doc is null when nothing is " +
            "open (it runs anyway, e.g. to open a file), compiled against this Revit's " +
            "own API (C# 12). Usings: System, System.Linq, System.Collections.Generic, System.IO, System.Text, Autodesk.Revit.DB, " +
            "Autodesk.Revit.UI (add more in `usings`). Return a value (a list comes back one item per line) and/or write to " +
            "`log`. To change the model, start your own Transaction(s); the whole call is still ONE Ctrl+Z (undo tool). " +
            "Compile errors come back with line numbers of your code; nothing runs then. Never loop without an end: it runs " +
            "on Revit's own thread. Every run is logged on the user's PC.",
            J("{`type`:`object`,`properties`:{`code`:{`type`:`string`,`description`:`Method body; may end with return.`},`usings`:{`type`:`array`,`items`:{`type`:`string`},`description`:`Extra namespaces, e.g. ['Autodesk.Revit.DB.Architecture'].`},`title`:{`type`:`string`,`description`:`A few words for the undo list and the log.`}},`required`:[`code`]}")),

        new("run_action", true,
            "Run one KH-Tool check or action (list_actions) on the active model, as the button's 'Run on active model' does: " +
            "returns the report rows; no log files, no save. Checks (Get Phase & Workset, Get Design Option, Get Link Workset, " +
            "Get Site Location & PBP, Drawing Progress, Type Mark by Text Note) change nothing; actions (Purge Import CAD, " +
            "Isolate Warning Elements) are one Ctrl+Z.",
            J("{`type`:`object`,`properties`:{`name`:{`type`:`string`,`description`:`Action name as list_actions gives it.`}},`required`:[`name`]}")),

        new("new_model", true,
            "Create a new model from a project template, save it at 'path' and make it the active model.",
            J("{`type`:`object`,`properties`:{`path`:{`type`:`string`,`description`:`Full path of the new .rvt.`},`template`:{`type`:`string`,`description`:`.rte path; default Revit's default project template.`},`overwrite`:{`type`:`boolean`}},`required`:[`path`]}")),

        new("save_model", true,
            "Save the active model (Ctrl+S), or save it as 'path'. Refused for workshared models: the user syncs those.",
            J("{`type`:`object`,`properties`:{`path`:{`type`:`string`,`description`:`Save As this .rvt path instead.`},`overwrite`:{`type`:`boolean`}}}")),

        new("create_view", true,
            "Create a view. kind: plan / ceiling / structural_plan (needs level), section (from/to [x,y] m, looks to the right " +
            "of from->to unless flip; depth_m, bottom_m, top_m absolute), elevation (plan + at [x,y] m + facing N/E/S/W = the " +
            "direction it looks), 3d (direction [x,y,z] it looks along; section_box {min,max} [x,y,z] m; display hidden/shaded/" +
            "realistic/shaded_edges), drafting. Also scale, detail_level, template, view_type. One Ctrl+Z undoes it.",
            J("{`type`:`object`,`properties`:{`kind`:{`type`:`string`,`enum`:[`plan`,`ceiling`,`structural_plan`,`section`,`elevation`,`3d`,`drafting`]},`name`:{`type`:`string`},`level`:{`type`:`string`},`scale`:{`type`:`integer`},`detail_level`:{`type`:`string`,`enum`:[`coarse`,`medium`,`fine`]},`template`:{`type`:`string`},`view_type`:{`type`:`string`,`description`:`View family type name.`},`from`:{`type`:`array`,`items`:{`type`:`number`}},`to`:{`type`:`array`,`items`:{`type`:`number`}},`flip`:{`type`:`boolean`},`depth_m`:{`type`:`number`},`bottom_m`:{`type`:`number`},`top_m`:{`type`:`number`},`plan`:{`type`:`string`},`at`:{`type`:`array`,`items`:{`type`:`number`}},`facing`:{`type`:`string`,`enum`:[`N`,`E`,`S`,`W`]},`direction`:{`type`:`array`,`items`:{`type`:`number`}},`section_box`:{`type`:`object`,`properties`:{`min`:{`type`:`array`,`items`:{`type`:`number`}},`max`:{`type`:`array`,`items`:{`type`:`number`}}}},`display`:{`type`:`string`}},`required`:[`kind`,`name`]}")),

        new("crop_view", true,
            "Crop a plan, section, elevation or drafting view to its model elements (to: model, default), rooms, grids, the given " +
            "element ids, or box_m {min,max} [x,y] m (plans), plus margin_mm on paper (default 10). annotation_mm turns the " +
            "annotation crop on at that paper offset (hides far markers and tags). One Ctrl+Z undoes it.",
            J("{`type`:`object`,`properties`:{`view`:{`type`:`string`},`to`:{`type`:`string`,`enum`:[`model`,`rooms`,`grids`,`elements`]},`include_grids`:{`type`:`boolean`},`elements`:{`type`:`array`,`items`:{`type`:`string`}},`box_m`:{`type`:`object`,`properties`:{`min`:{`type`:`array`,`items`:{`type`:`number`}},`max`:{`type`:`array`,`items`:{`type`:`number`}}}},`margin_mm`:{`type`:`number`},`annotation_mm`:{`type`:`number`},`show_crop`:{`type`:`boolean`}},`required`:[`view`]}")),

        new("place_text", true,
            "Place text notes (and simple table lines) in a view or on a sheet, in PAPER mm (a sheet from its origin; a view, " +
            "model mm / scale). items: {text, x, y, size_mm, width_mm, bold, align left/center/right, angle, font}. lines: " +
            "[x1,y1,x2,y2,width_mm]. Text types 'KH <font> <size>' are made as needed (default font TH Sarabun New). One Ctrl+Z undoes it.",
            J("{`type`:`object`,`properties`:{`view`:{`type`:`string`,`description`:`View or sheet.`},`font`:{`type`:`string`},`size_mm`:{`type`:`number`},`items`:{`type`:`array`,`items`:{`type`:`object`}},`lines`:{`type`:`array`,`items`:{`type`:`array`,`items`:{`type`:`number`}}}},`required`:[`view`]}")),

        new("import_vectors", true,
            "Draw a JSON file of 2D vectors (from the PDF extractor) in a drafting view, plan or sheet: lines [[x1,y1,x2,y2,w]], " +
            "polylines [{pts,w,closed}], fills [{pts}] (not on sheets), arcs [{c,r,a0,a1,w}], texts [{t,x,y,size,angle,align,bold}], " +
            "all paper mm, y up; w = pen width mm -> line style 'KH Pen 1..6'. origin_mm shifts everything. One Ctrl+Z undoes it.",
            J("{`type`:`object`,`properties`:{`view`:{`type`:`string`},`file`:{`type`:`string`},`origin_mm`:{`type`:`array`,`items`:{`type`:`number`}},`font`:{`type`:`string`}},`required`:[`view`,`file`]}")),

        new("pdf_underlay", true,
            "Place a PDF page as an image in a view at true size (scale = the PDF drawing's scale, e.g. 75), so pdf_mm[0] " +
            "(page mm from its top-left, y down) lands on model_m[0] ([x,y] metres); a second pair reports the error. For checking " +
            "a model against the PDF. One Ctrl+Z undoes it.",
            J("{`type`:`object`,`properties`:{`view`:{`type`:`string`},`pdf`:{`type`:`string`},`page`:{`type`:`integer`},`scale`:{`type`:`number`},`dpi`:{`type`:`integer`},`pdf_mm`:{`type`:`array`,`items`:{`type`:`array`,`items`:{`type`:`number`}}},`model_m`:{`type`:`array`,`items`:{`type`:`array`,`items`:{`type`:`number`}}}},`required`:[`view`,`pdf`,`scale`,`pdf_mm`,`model_m`]}")),
    };

    /// <summary>KhMcp.exe asks the bridge this (not a listed tool) before it compiles run_code: the
    /// assemblies to compile against, and whether AI Code is on.</summary>
    public const string CodeRefs = "code_refs";

    /// <summary>What the AI is told about this server when it connects.</summary>
    public const string Instructions =
        "KH-Tool AI Bridge: runs the user's own tested KH-Tool / DRW Helper tools in the open Revit model. " +
        "Start with kh_status, then the list_* tools to learn exact room and view names. Prefer the user's saved " +
        "presets (list_presets). Each model-changing call is ONE undo step in Revit. After a change, check it with " +
        "export_view_png. If a needed operation has no tool here, say so rather than working around it.";
}
