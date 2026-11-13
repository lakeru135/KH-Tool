using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

namespace KhBatchRunner;

public partial class App
{
    /// <summary>
    /// The ribbon panels and buttons of this edition, in creation (left-to-right) order. Called
    /// once from <see cref="OnStartup"/>, at the point where the ribbon used to be built inline.
    /// </summary>
    private void BuildRibbon(UIControlledApplication application, string asm)
    {
#if EDITION_DRW
        // DRW Helper ribbon (left-to-right): Setting, Create, Annotation. Used mostly in the ACTIVE
        // model: Create and Annotation need an open project (DocumentAvailable); Setting works from
        // the home screen like the batch runner's. Sync View and Single Font also work in the Family
        // Editor (AnyDocumentAvailable).
        RibbonPanel setting = application.CreateRibbonPanel(TabName, SettingPanel);
        RibbonPanel create = application.CreateRibbonPanel(TabName, DrwCreatePanel);
        RibbonPanel annotation = application.CreateRibbonPanel(TabName, DrwAnnotationPanel);

        AddButton(setting, asm, "About", "About",
            typeof(Commands.AboutCommand),
            $"About {Product.Name} — version, publisher and what's new.",
            Icons.About);
        AddButton(setting, asm, "EditModelSet", "Model\nSet",
            typeof(Commands.BrowseFormaCommand),
            "Browse Forma and/or add local .rvt files, and manage saved model sets (create, load, " +
            "edit, delete). The same sets as KH-Tool: a set saved in either ribbon shows in both.",
            Icons.EditModelSet);
        AddButton(setting, asm, "DynMultiPlayer", "Batch\nRun",
            typeof(Commands.BatchRunCommand),
            "Run DRW Helper's batch-capable tools over model sets and ad-hoc models (Forma or local), " +
            "now or on a schedule. Lists only this ribbon's tools; tools that need you to pick " +
            "something in the model run from their own buttons instead.",
            Icons.DynMultiPlayer);
        var syncView = (PushButton)setting.AddItem(MakeButtonData(asm, "SyncView",
            Drw.SyncView.SyncViewService.CaptionFor(false),
            typeof(Commands.DrwSyncViewCommand),
            "Toggle. While ON, switching between plans (or between sections/elevations facing the " +
            "same way) keeps the zoom and position of the view you left, like pyRevit's Sync Views. " +
            "Works in projects and in the Family Editor. Off at every Revit start. Interactive only; " +
            "it has no batch mode.",
            AnyDocumentAvailability));
        RegisterThemed(syncView, Icons.SyncView);
        Drw.SyncView.SyncViewService.Button = syncView;
        application.ViewActivated += Drw.SyncView.SyncViewService.OnViewActivated;

        AddButton(create, asm, "EnlargeRoom", "Enlarge\nRoom",
            typeof(Commands.DrwEnlargeRoomCommand),
            "Create an enlarged plan and sections or elevations for each chosen room (cropped to the room), " +
            "named from the room with configurable prefixes, with view templates and parameter " +
            "values from a saved preset.",
            Icons.EnlargeRoom, DocumentAvailability);
        AddButton(create, asm, "EnlargeStair", "Enlarge\nStair",
            typeof(Commands.DrwEnlargeStairCommand),
            "As Enlarge Room, for stairs: one plan per level the stair crosses (level as suffix), " +
            "named from the stair's Mark; multistory stairs number their members ST-1-1, ST-1-2….",
            Icons.EnlargeStair, DocumentAvailability);
        AddButton(create, asm, "EnlargeDoorWindow", "Enlarge\nDoor & Window",
            typeof(Commands.DrwEnlargeDoorWindowCommand),
            "The door & window sheet as legends: one cell per Type Mark — the type in elevation and plan, " +
            "dimensioned, with a table whose rows take their values from parameters you pick and label. " +
            "Building again rebuilds the same legends, so sheets keep their viewports.",
            Icons.EnlargeDoorWindow, DocumentAvailability);

        AddButton(annotation, asm, "DatumDimension", "Datum\nDimension",
            typeof(Commands.DrwDatumDimensionCommand),
            "Dimension grids in plans, and grids and levels in sections and elevations: an overall " +
            "string and one through every datum, for each set of parallel datums, at the end with the " +
            "most bubbles (or the end you choose). Was Grid Dimension; saved presets and schedules still run.",
            Icons.GridDimension, DocumentAvailability);
        AddButton(annotation, asm, "MatchDatums", "Match\nDatums",
            typeof(Commands.DrwMatchDatumsCommand),
            "Copy the active view's grid and level ends (2D/3D), bubbles and elbows to the parallel " +
            "views you tick, with a row per datum per view saying what happened. Replaces Propagate " +
            "Extents, which picks the views itself and fails silently. Section markers cannot be matched.",
            Icons.MatchDatums, DocumentAvailability);
        AddButton(annotation, asm, "SingleFont", "Single\nFont",
            typeof(Commands.DrwSingleFontCommand),
            "One font for the whole model: text, dimension, spot and model-text types, schedule cell " +
            "fonts, and the text inside tags, title blocks, heads and other annotation families (each " +
            "opened, changed and reloaded). Type names carrying the old font are renamed. In the Family " +
            "Editor it changes the open family's text and label types and its nested families. Can " +
            "report only, and runs in Batch Run from a saved preset.",
            Icons.SingleFont, AnyDocumentAvailability);
        AddButton(annotation, asm, "StairAnnotation", "Stair\nAnnotation",
            typeof(Commands.DrwStairAnnotationCommand),
            "Place the stair path and tread numbers on stairs in plan views, from a saved preset.",
            Icons.StairAnnotation, DocumentAvailability);
        AddButton(annotation, asm, "PresetTagger", "Preset\nTagger",
            typeof(Commands.DrwPresetTaggerCommand),
            "Tag doors, windows, rooms or any other category in plans, sections and elevations: each rule " +
            "names its own tag type and a spot in paper millimetres, so it suits every view scale. Spots " +
            "overlapping other annotation are avoided; elements already tagged are skipped, so a rerun adds " +
            "nothing. Runs in Batch Run from a saved preset.",
            Icons.PresetTagger, DocumentAvailability);
        AddButton(annotation, asm, "WallMatTag", "Wall Mat\nTag",
            typeof(Commands.DrwWallMatTagCommand),
            "Tag the finish material on each face of the walls in plans and sections — the triangle wall tag: " +
            "one tag type per direction, its tip on the face, the two faces' tags lined up; optionally one pair " +
            "per wall piece between doors and windows. Can repair material tags that read '?'. New tags show " +
            "'?' until touched in Revit (an API limit); they are left selected. Runs in Batch Run from a saved preset.",
            Icons.WallMatTag, DocumentAvailability);
#else
        // Panel order left-to-right follows creation order: Setting, Batch Run, Schedule,
        // Multi Model Check, Model Batch Action, NWD Helper, Forma Batch Action,
        // Excel Link, Parameter Helper.
        RibbonPanel setting = application.CreateRibbonPanel(TabName, SettingPanel);
        RibbonPanel batchRun = application.CreateRibbonPanel(TabName, BatchRunPanel);
        RibbonPanel schedule = application.CreateRibbonPanel(TabName, SchedulePanel);
        RibbonPanel check = application.CreateRibbonPanel(TabName, CheckPanel);
        RibbonPanel action = application.CreateRibbonPanel(TabName, ActionPanel);
        RibbonPanel nwdHelper = application.CreateRibbonPanel(TabName, NwdHelperPanel);
        RibbonPanel forma = application.CreateRibbonPanel(TabName, FormaPanel);
        RibbonPanel excel = application.CreateRibbonPanel(TabName, ExcelPanel);
        RibbonPanel parameter = application.CreateRibbonPanel(TabName, ParameterPanel);

        // --- Setting panel (About first) ---
        AddButton(setting, asm, "About", "About",
            typeof(Commands.AboutCommand),
            $"About {Product.Name} — version, publisher and what's new.",
            Icons.About);
        // Builds under test only. When it IS shown it sits second, deliberately: reporting a failed
        // run has to be findable while Revit is open and something has just gone wrong, not buried
        // in a Start-menu folder. A released build keeps the collector but not the button — the
        // Start-menu "Collect diagnostics report" shortcut and install\Collect-Diagnostics.cmd are
        // still installed, so a report can be produced by walking someone through it.
        if (Product.IsDevelopmentBuild)
            AddButton(setting, asm, "DiagnosticReport", "Diagnostic\nReport",
                typeof(Commands.DiagnosticsReportCommand),
                "Collect everything needed to explain what a run did — the schedule launch chain, task " +
                "results, run logs and Revit journals — into ONE zip file on your Desktop to send for " +
                "support. Nothing is changed: no schedule is started, edited or deleted.",
                Icons.DiagnosticReport);
        // The two "edit a saved thing" buttons share ONE full-height button with a dropdown arrow —
        // the shape Revit itself uses for Electrical / Model on the Systems tab.
        //
        // NOT AddStackedItems. Stacked items are three short rows of small buttons, so two of them
        // left a half-empty column of truncated captions ("...ostic Rep...") rather than the single
        // large button this is meant to be. A pulldown keeps the large glyph and the readable
        // two-line caption and still folds both commands into one button's width.
        //
        // Full edition only: the DRW ribbon has no Edit DYN Script Set command on it, so there is
        // nothing there to fold together and it keeps a plain Model Set button.
        var editSets = (PulldownButton)setting.AddItem(new PulldownButtonData("EditSets", "Edit\nSets")
        {
            ToolTip = "Manage the saved sets this add-in runs over — model sets, and Dynamo script sets."
            // No AvailabilityClassName: PulldownButtonData has none. A pulldown follows its
            // entries, and both are built by MakeButtonData, which sets AlwaysAvailable.
        });
        RegisterThemed(editSets, Icons.EditModelSet);
        RegisterThemed(
            editSets.AddPushButton(MakeButtonData(asm, "EditModelSet", "Edit Model Set",
                typeof(Commands.BrowseFormaCommand),
                "Browse Forma and/or add local .rvt files, and manage saved model sets " +
                "(create, load, edit, delete).")),
            Icons.EditModelSet, smallOnly: true);
        RegisterThemed(
            editSets.AddPushButton(MakeButtonData(asm, "EditScriptSet", "Edit DYN Script Set",
                typeof(Commands.EditScriptSetCommand),
                "Manage saved sets of Dynamo .dyn scripts (add/remove/save). " +
                "Sets run via Batch Run / Schedule Run (Dynamo runner).")),
            Icons.EditScriptSet, smallOnly: true);
        AddButton(setting, asm, "SettingsTransfer", "Import /\nExport Settings",
            typeof(Commands.SettingsTransferCommand),
            SettingsTransferTip,
            Icons.SettingsTransfer);
        var aiBridge = (PushButton)setting.AddItem(MakeButtonData(asm, "AiBridge",
            Mcp.McpBridge.CaptionFor(false),
            typeof(Commands.AiBridgeCommand),
            "Toggle. While ON, an AI assistant (Claude Code with the kh-tool MCP server) can run KH-Tool " +
            "and DRW Helper tools in the open model with your saved presets — each call is one undo step " +
            "(Ctrl+Z). Off at every Revit start.\n\nOne-time setup in Claude Code:\n" +
            Mcp.McpBridge.SetupLine()));
        RegisterThemed(aiBridge, Icons.AiBridge);
        Mcp.McpBridge.Button = aiBridge;
        Mcp.McpBridge.AutoStartIfAsked();
        var aiCode = (PushButton)setting.AddItem(MakeButtonData(asm, "AiCode",
            Mcp.McpCode.CaptionFor(false),
            typeof(Commands.AiCodeCommand),
            "Toggle. While ON (and AI Bridge is ON), the AI can run its own C# code in the open model WITHOUT asking you " +
            "first (the kh-tool run_code tool) — for what no KH-Tool button does. Each run is one undo step (Ctrl+Z), and " +
            "every run is logged to " + Mcp.McpCode.LogFolder + ". Off at every Revit start; turn it off again when done."));
        RegisterThemed(aiCode, Icons.AiCode);
        Mcp.McpCode.Button = aiCode;
        Mcp.McpCode.AutoStartIfAsked();

        // --- Batch Run panel ---
        AddButton(batchRun, asm, "DynMultiPlayer", "Batch\nRun",
            typeof(Commands.BatchRunCommand),
            "Mix model sets and ad-hoc models (Forma or local) with one or more actions/script " +
            "sets, run now, or save the selection as a schedule.",
            Icons.DynMultiPlayer);
        AddButton(batchRun, asm, "BatchInterop", "Batch\nInterop",
            typeof(Commands.BatchInteropCommand),
            "Batch-run an Interoperability Tools (BIM Interoperability Tools) Model Checker " +
            "checkset over a model set (Forma or local); per-model HTML/Excel report + CSV log. " +
            "Read-only — never syncs or saves. Requires AIT installed for this Revit version.",
            Icons.BatchInterop);

        // --- Schedule panel ---
        AddButton(schedule, asm, "ScheduleRun", "Schedule\nRun",
            typeof(Commands.ScheduleRunCommand),
            "View, create, edit or duplicate scheduled batches (actions + DYN script sets) via " +
            "Windows Task Scheduler, or Run now.",
            Icons.ScheduleRun);
        AddButton(schedule, asm, "ScheduleInterop", "Schedule\nInterop",
            typeof(Commands.ScheduleInteropCommand),
            "View, create, edit or duplicate scheduled Batch Interop (AIT Model Checker) runs " +
            "via Windows Task Scheduler, or Run now.",
            Icons.ScheduleInterop);
#if DEBUG
        // DEV-only Phase A spike for Batch Interop: exercises the AIT Model Checker reflection
        // bridge against the active + a background-opened doc and logs every step.
        AddButton(batchRun, asm, "SpikeInterop", "Spike\nInterop",
            typeof(Commands.SpikeInteropCommand),
            @"DEV: verify the AIT Model Checker bridge (logs to %USERPROFILE%\KhBatchTest\interopspike).",
            Icons.DynMultiPlayer);
#endif

        // --- Multi Model Check panel (read-only) ---
        AddButton(check, asm, "GetSiteLocationPbp", "Get Site\nLocation & PBP",
            typeof(Commands.GetSiteLocationPbpCommand),
            "Batch-read each model's site location, true-north angle, Project Base Point " +
            "and Survey Point; per-model CSV log.",
            Icons.SiteLocationPbp);
        AddButton(check, asm, "GetPhaseWorkset", "Get Phase\n& Workset",
            typeof(Commands.GetPhaseWorksetCommand),
            "Batch-read each model's phases and worksets; per-model CSV log.",
            Icons.PhaseWorkset);
        AddButton(check, asm, "GetDesignOption", "Get Design\nOption",
            typeof(Commands.GetDesignOptionCommand),
            "Batch-read each model's design option sets and options (and which is primary); per-model CSV log.",
            Icons.GetDesignOption);
        AddButton(check, asm, "GetRvtLinksWorkset", "Get Link\nWorkset",
            typeof(Commands.GetRvtLinksWorksetCommand),
            "Batch-read every external reference in each model — Revit links (RVT/IFC), linked and " +
            "imported CAD, images and point clouds — and which workset each sits in, with a Kind " +
            "(Link/Import) and Type column; per-model CSV log. Load status " +
            "reflects the batch open, which closes all worksets; system worksets shown as (system).",
            Icons.GetRvtLinksWorkset);
        AddButton(check, asm, "DrawingProgress", "Drawing\nProgress",
            typeof(Commands.DrawingProgressCommand),
            "Batch-read every sheet in each model — number, name, current revision, revision " +
            "date/description, sheet-list and placeholder status. Per-model CSV log.",
            Icons.DrawingProgress);
        AddButton(check, asm, "TypeMarkTextNote", "Type Mark\nText Note",
            typeof(Commands.TypeMarkTextNoteCommand),
            "Batch-compare every text note against the model's Type Mark values: one row per note " +
            "with its view, whether any line matches a Type Mark, and which type(s) carry that " +
            "mark; also counts duplicate Type Marks. Read-only per-model CSV log.",
            Icons.TypeMarkTextNote);
        // Rebuild Report (Rebuild Matrix until 1.5.5) stays on Multi Model Check, where it has
        // always been — it rebuilds a report from a report, so it belongs with the checks that
        // produced the CSV rather than with the actions that write to models. 1.5.1 gave it a
        // dropdown per matrix type; the window still auto-detects from the chosen CSV, so a wrong
        // pick cannot build a wrong matrix — the entry only decides what is preselected. 1.5.5
        // adds the Batch Interop summary and renames it, since that one is not a matrix. The
        // internal item names keep "RebuildMatrix" so Quick Access Toolbar entries survive.
        var matrixPulldown = (PulldownButton)check.AddItem(new PulldownButtonData(
            "RebuildMatrix", "Rebuild\nReport")
        {
            ToolTip = "Rebuild a report workbook from files a run already wrote — no model opened: " +
                      "a matrix from a detail CSV (choosing the CSV re-detects its type, and a CSV " +
                      "missing the required columns is rejected), or one Batch Interop summary from " +
                      "the per-model report workbooks."
            // No AvailabilityClassName here — PulldownButtonData does not have one. A pulldown
            // follows its entries, and both of those are built by MakeButtonData, which sets
            // AlwaysAvailable — so the dropdown still works on the Revit home screen.
        });
        RegisterThemed(matrixPulldown, Icons.RebuildMatrix);
        RegisterThemed(
            matrixPulldown.AddPushButton(MakeButtonData(asm, "RebuildMatrixPhaseWorkset",
                "Phase & Workset", typeof(Commands.RebuildMatrixPhaseWorksetCommand),
                "Rebuild the Phase & Workset matrix from a Get Phase & Workset detail CSV.")),
            Icons.RebuildMatrix, smallOnly: true);
        RegisterThemed(
            matrixPulldown.AddPushButton(MakeButtonData(asm, "RebuildMatrixDesignOption",
                "Design Option", typeof(Commands.RebuildMatrixDesignOptionCommand),
                "Rebuild the Design Option matrix from a Get Design Option detail CSV.")),
            Icons.RebuildMatrix, smallOnly: true);
        RegisterThemed(
            matrixPulldown.AddPushButton(MakeButtonData(asm, "RebuildInteropSummary",
                "Batch Interop Summary", typeof(Commands.RebuildInteropSummaryCommand),
                "Merge Batch Interop report workbooks (one <model>.xlsx per model) into one summary " +
                "workbook to send — select every .xlsx in a run's InteropReports folder. A run " +
                "already writes this summary itself; use this for older runs or a hand-picked set.")),
            Icons.RebuildMatrix, smallOnly: true);

        // --- Model Batch Action panel (writes to the model — syncs or saves afterwards) ---
        AddButton(action, asm, "PurgeImportCad", "Purge\nImport CAD",
            typeof(Commands.PurgeImportCadCommand),
            "Batch-delete every non-linked imported CAD instance (DWG/DXF/DGN/SAT import) " +
            "in each model. WRITES to the model — syncs (if kept attached to a workshared " +
            "central) or saves afterwards.",
            Icons.PurgeImportCad);
        // Caption only. IsolateWarningElementsAction.Name stays "Isolate Warning Elements",
        // because that string is the KEY a saved schedule stores for this action — RunItem carries
        // no id for built-in actions, and ActionRegistry.Create falls back to the FIRST registered
        // action on a miss without raising anything. Renaming it would make every existing
        // schedule quietly run Get Site Location & PBP and report success.
        AddButton(action, asm, "IsolateWarningElements", "Isolate\nWarnings",
            typeof(Commands.IsolateWarningElementsCommand),
            "Batch-create one 3D view per warning description, isolating that warning's " +
            "failing elements (numbered W-01, W-02… in a master index CSV). WRITES to the " +
            "model — syncs (if kept attached to a workshared central) or saves afterwards.",
            Icons.IsolateWarningElements);
        AddButton(action, asm, "BatchLinkRvt", "Batch\nLink RVT",
            typeof(Commands.BatchLinkRvtCommand),
            "Batch-link one or more source models into every model of a set — all on one named " +
            "workset, or one workset per link under the same name Correct Workset expects. " +
            "A source a host already links is skipped rather than linked twice. WRITES to the " +
            "model — syncs (if kept attached to a workshared central) or saves afterwards.",
            Icons.BatchLinkRvt);
        AddButton(action, asm, "CorrectWorkset", "Correct\nWorkset",
            typeof(Commands.CorrectWorksetCommand),
            "Enforce the workset convention per model: move chosen categories onto their dedicated " +
            "workset (an existing workset containing a keyword is renamed with your approval, else " +
            "created); a workset holding one RVT link is renamed to <keyword>_<model name>, extra " +
            "links move to their own worksets, and leftover elements in a link workset follow your " +
            "rules or move to WIP. Check mode reports only; Fix mode applies and syncs. Runs on a " +
            "model set (a fixed list or a Forma folder set) or the active model.",
            Icons.CorrectWorkset);
        // --- NWD Helper panel (Revit ⇄ Navisworks; full edition only) ---
        // Export NWC: batch, read-only — it writes a FILE and never the model.
        AddButton(nwdHelper, asm, "ExportNwc", "Export\nNWC",
            typeof(Commands.ExportNwcCommand),
            "Batch-export one Navisworks .nwc per model, from a named 3D view where the model has " +
            "one and from the whole model where it does not (the log says which each got). " +
            "Read-only — the source models are never modified. Needs the Revit exporter that " +
            "installs with Navisworks.",
            Icons.ExportNwc);
        // Export IFC + Data: batch, read-only — the IFC plus the CSVs the BIM report reads.
        AddButton(nwdHelper, asm, "ExportIfcData", "Export\nIFC + Data",
            typeof(Commands.ExportIfcCommand),
            "Batch-export one IFC per model (from a named 3D view, or the whole model), plus " +
            "elements.csv and rooms.csv, into a dated run folder. When the run ends the IFC is " +
            "converted for the BIM report and the 'latest' folder is refreshed. Read-only — the " +
            "source models are never modified.",
            Icons.ExportIfcData);

        // Create Family from Navisworks: Navisworks selection → Revit family, not a batch action.
        // AlwaysAvailable like the rest: building the .rfa needs no open model — only loading and
        // placing it does, and the command offers that only when a project is active.
        // Internal name unchanged (it is the button's identity); only the caption was shortened.
        AddButton(nwdHelper, asm, "CreateFamilyFromNavisworks", "Create rfa\nfrom NWD",
            typeof(Commands.CreateFamilyFromNavisworksCommand),
            "Build a Revit family (.rfa) from a KH-NWD Tool export (.khnwd.json) — a Navisworks " +
            "selection saved with its Revit category by Navisworks' KH-NWD Tool › Export for Revit " +
            "(RFA). The .rfa is saved next to the export; then load it, place it by clicking (plan " +
            "centre, lowest point) or place it at the Navisworks location. Not a batch action.",
            Icons.NwdFamily);

        // --- Forma Batch Action panel (cloud extract + publish) ---
        AddButton(forma, asm, "GetFileLog", "Get File\nLog",
            typeof(Commands.GetFileLogCommand),
            "Build a set of Forma folders and export each one's file log (version history) to its own " +
            "CSV — every file type, or Revit models only. Save the set to re-run it or schedule it. " +
            "Read-only — opens no model.",
            Icons.GetFileLog);
        AddButton(forma, asm, "GetDataExtract", "Get Data\nExtract",
            typeof(Commands.GetDataExtractCommand),
            "Download the newest finished run of a Forma Data Connector extract — issues, RFIs, " +
            "users, cost and the rest — as CSVs in a dated folder. Forma's own schedule produces the " +
            "extract; this collects it, by hand or on a schedule. Read-only — opens no model.",
            Icons.GetDataExtract);
        AddButton(forma, asm, "InitiateToForma", "Initiate\nto Forma",
            typeof(Commands.InitiateToFormaCommand),
            "Batch-publish each model in a set (Forma or local) as a NEW cloud model in a chosen " +
            "Forma folder (SaveAsCloudModel). Sources open detached; per-model CSV log.",
            Icons.InitiateToForma);
        AddButton(forma, asm, "CheckSheetPublish", "Check Sheet\nPublish",
            typeof(Commands.CheckSheetPublishCommand),
            "Pick/save a set of Forma folders and a CSV of sheet numbers, then report which sheets " +
            "are present among each model's published (translated) 2D sheet views. Read-only — " +
            "opens no model.",
            Icons.CheckSheetPublish);
        // Cut Model + Internal Relink: whole-set cloud workflows (temp copy → publish; relink → sync).
        AddButton(forma, asm, "CutModel", "Cut\nModel",
            typeof(Commands.CutModelCommand),
            "Cut each model in a set for one site: save a local temp copy (the source is never " +
            "modified), activate the site location matching the site code, optionally delete the " +
            "other locations, then publish it as a NEW cloud model named with the site code as " +
            "prefix. Per-model CSV log.",
            Icons.CutModel);
        AddButton(forma, asm, "InternalRelink", "Internal\nRelink",
            typeof(Commands.RelinkModelCommand),
            "Re-point each host model's Revit links from their old-prefix targets to the matching " +
            "new-prefix models in the target Forma folders (e.g. OLD-ST-BLDA → SITE1A-ST-BLDA). Pick a " +
            "host model set, the folders to scan, and an old → new site code; each host is relinked " +
            "and synced back to its cloud central. Per-model CSV log.",
            Icons.Relink);
        // Publish Cloud Model: ARCHIVED off the ribbon (0.8.3): the borrowed Revit session token
        // cannot call the publish command; it needs an APS 3-legged app.

        // --- Excel Link panel (SheetLink-style schedule export/import, active model only) ---
        AddButton(excel, asm, "ExcelExport", "Export\nExcel",
            typeof(Commands.ExcelExportCommand),
            "Export selected schedules of the ACTIVE model to one Excel workbook (one worksheet " +
            "per schedule). A legend row marks each column Instance (may edit), Type (cannot edit) " +
            "or Read-only (cannot edit); legend + header rows stay frozen and locked columns are " +
            "grey. Edit the Instance columns in Excel, then Import Excel to write them back.",
            Icons.ExcelExport, DocumentAvailability);
        AddButton(excel, asm, "ExcelImport", "Import\nExcel",
            typeof(Commands.ExcelImportCommand),
            "Import an Excel Link workbook back into the ACTIVE model: each worksheet is matched " +
            "to its schedule by name, only CHANGED cells in editable Instance columns are written " +
            "(one undo step), and edits to locked columns are ignored and reported per cell. " +
            "Nothing is saved or synced — review, then save/sync yourself.",
            Icons.ExcelImport, DocumentAvailability);

        // --- Parameter Helper panel (formula rules saved in the ACTIVE model) ---
        AddButton(parameter, asm, "FormulaRules", "Formula\nRules",
            typeof(Commands.ParameterRulesCommand),
            "Rules that write a formula's result into a parameter of every element in chosen " +
            "categories — like a schedule's calculated value, but stored on the element so it can " +
            "be tagged, filtered and exported (e.g. Panel Area = [Width] * [Height]). Units are " +
            "checked as in Revit. The rules are saved IN the model, so everyone on a central model " +
            "shares them; Live mode recalculates as elements change. Preview shows what would change.",
            Icons.FormulaRules, DocumentAvailability);
        AddButton(parameter, asm, "ApplyFormulas", "Apply\nNow",
            typeof(Commands.ParameterApplyCommand),
            "Run this model's formula rules now — on the selected elements if any are selected, " +
            "otherwise on every element the rules cover. Only values that differ are written, so in " +
            "a central model only those elements are borrowed; elements owned by someone else are " +
            "listed and left alone. One Undo reverses it. Nothing is saved or synced.",
            Icons.ApplyFormulas, DocumentAvailability);
        AddButton(parameter, asm, "MirrorCheck", "Mirror\nCheck",
            typeof(Commands.ParameterMirrorCheckCommand),
            "Label mirrored and flipped family instances in one text parameter, KH_Mirror State — blank, " +
            "or e.g. \"Mirrored, Hand Flipped\" — so they can be scheduled, tagged and coloured by a view " +
            "filter, including inside mirrored groups. Or pick a shared text parameter of your own; Mirror " +
            "Check can add it to every category for you. Live mode updates it as elements are mirrored, " +
            "flipped or placed (one Undo takes back both); Check All Now catches up the rest, borrowing " +
            "only what changes. Saved in the model, so a central model's users share it.",
            Icons.MirrorCheck, DocumentAvailability);
#endif
    }

    private void AddButton(
        RibbonPanel panel, string assemblyPath, string internalName, string text,
        Type commandType, string tooltip, Func<IconPalette, int, BitmapSource> iconFactory,
        string? availabilityClass = null)
    {
        var button = (PushButton)panel.AddItem(
            MakeButtonData(assemblyPath, internalName, text, commandType, tooltip, availabilityClass));
        RegisterThemed(button, iconFactory);
    }

    /// <summary>
    /// The button DEFINITION, without adding it anywhere.
    ///
    /// <para>Split out of <see cref="AddButton"/> because a stacked pair and a pulldown's entries
    /// have to be described before they are placed: <c>RibbonPanel.AddStackedItems</c> takes
    /// <see cref="PushButtonData"/> and hands back the created items, and
    /// <c>PulldownButton.AddPushButton</c> is the only way into a dropdown. Both then need the
    /// themed-glyph registration that <see cref="AddButton"/> used to do inline.</para>
    /// </summary>
    private PushButtonData MakeButtonData(string assemblyPath, string internalName, string text,
        Type commandType, string tooltip, string? availabilityClass = null) =>
        new(internalName, text, assemblyPath, commandType.FullName)
        {
            ToolTip = tooltip,
            // Clickable with no document open (Revit home screen) unless the caller says otherwise
            // — DRW's model tools pass DocumentAvailable.
            AvailabilityClassName = availabilityClass ?? AlwaysAvailable
        };

    /// <summary>
    /// Paints a ribbon item's glyph for the current theme and remembers how to repaint it.
    ///
    /// <para>Takes <see cref="RibbonButton"/>, not <see cref="PushButton"/>: a
    /// <c>PulldownButton</c> has its own face glyph to keep in step with the theme, and
    /// <c>RibbonButton</c> is the base that actually declares Image/LargeImage — <c>RibbonItem</c>
    /// does not.</para>
    ///
    /// <para><paramref name="smallOnly"/> is for stacked items and dropdown entries, which render
    /// the 16px <c>Image</c> and never the large one, so only that one is drawn.</para>
    /// </summary>
    private void RegisterThemed(RibbonButton button, Func<IconPalette, int, BitmapSource> iconFactory,
        bool smallOnly = false)
    {
        Paint(button, iconFactory, smallOnly);
        _themedButtons.Add((button, iconFactory, smallOnly));
    }

    /// <summary>
    /// Draws the 16px <c>Image</c> and the 32px <c>LargeImage</c> separately. Until 1.5.1.4 one 32px
    /// bitmap went into both and WPF shrank it for the small slot (Quick Access Toolbar, a collapsed
    /// panel, a dropdown entry), which is visibly softer than a glyph drawn at the size it is shown —
    /// and the 16px drawing also drops detail that would only blur at that size.
    /// </summary>
    private void Paint(RibbonButton button, Func<IconPalette, int, BitmapSource> icon, bool smallOnly)
    {
        button.Image = icon(_palette, 16);
        if (!smallOnly) button.LargeImage = icon(_palette, 32);
    }
}
