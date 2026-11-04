using System.Drawing;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

namespace KhBatchRunner;

/// <summary>
/// Add-in shell. Builds the ribbon on startup (Setting + Batch Run + Schedule + Multi Model
/// Check + Multi Model Action panels) and, when Revit was launched by a scheduled task, arms the
/// headless scheduled runner. The Revit-style ribbon icons follow Revit's light/dark theme
/// live — Revit's API has no theme-changed event, so we poll
/// <see cref="UIControlledApplication.Idling"/> and redraw the glyphs (cheap enum compare
/// per idle; a redraw only when the theme actually flips).
/// </summary>
public class App : IExternalApplication
{
    private const string TabName = Product.Tab;
    private const string SettingPanel = "Setting";
    private const string CheckPanel = "Multi Model Check";
    private const string ActionPanel = "Model Batch Action";
    // Everything Navisworks, on one panel since 1.5.1.4 (it was two: "NWC" and "KH-NWD Tool").
    // Export NWC left Multi Model Action in 1.5.1 — the only item there that never writes to a
    // model. Create Family from Navisworks is the Revit half of KH-NWD Tool (the Navisworks add-in
    // in the KH-NWD-Tool repo); it lives here, as a panel of this add-in, rather than as its own
    // add-in adding a panel to this tab: OnStartup creates the tab unconditionally, so a second
    // add-in creating it first would make this one fail to load.
    private const string NwdHelperPanel = "NWD Helper";
    private const string BatchRunPanel = "Batch Run";
    private const string SchedulePanel = "Schedule";
    private const string FormaPanel = "Forma Batch Action";
    private const string InteropPanel = "Interop";
    private const string ExcelPanel = "Excel Link";
    // Formula rules that write a calculated value into a real parameter (1.5.2). Full edition only.
    // Not "Schedule Helper": the Schedule panel next door is the Windows Task Scheduler runner.
    private const string ParameterPanel = "Parameter Helper";
    private const string DrwCreatePanel = "Create";
    private const string DrwAnnotationPanel = "Annotation";
    private static readonly string AlwaysAvailable = typeof(Commands.AlwaysAvailable).FullName!;
    private static readonly string DocumentAvailability = typeof(Commands.DocumentAvailable).FullName!;
    private static readonly string AnyDocumentAvailability = typeof(Commands.AnyDocumentAvailable).FullName!;

    /// <summary>
    /// One tooltip for the Import/Export button, because it is added to BOTH edition panels and a
    /// tooltip that says different things about the same button in two builds is how two editions
    /// end up disagreeing about what a feature does.
    ///
    /// <para>It states the exclusion up front. A backup that silently leaves schedules behind is
    /// worse than one that says so, and this is the last place to say it before the dialog does.</para>
    /// </summary>
    private const string SettingsTransferTip =
        "Copy saved settings to or from another PC as ONE zip file — model sets, script sets, and " +
        "every tool's saved input sets. Schedules are NOT included (a schedule is half-stored in " +
        "Windows Task Scheduler and must be re-created), and licence/update state is never carried. " +
        "Opens no model and changes no schedule.";

    // Ribbon buttons whose glyph must track the active theme, paired with the factory that
    // redraws that glyph in a given palette and size. RibbonButton rather than PushButton since
    // 1.5.1: a PulldownButton has a face glyph of its own, and RibbonButton is the base that declares
    // Image/LargeImage. SmallOnly marks stacked/dropdown entries, which show only the 16px Image.
    private readonly List<(RibbonButton Button, Func<IconPalette, int, BitmapSource> Icon, bool SmallOnly)> _themedButtons = new();
    private IconPalette _palette = IconPalette.Light;
    private bool _lastThemeDark;

#if NETFRAMEWORK
    // Revit 2024 (net48) shares one AppDomain across all add-ins and ships no
    // System.Text.Json — hook AssemblyResolve to our own folder before any code touches it.
    // A static ctor runs when Revit's add-in manager first instantiates App, ahead of
    // OnStartup and any member that could trigger an STJ bind.
    static App() => Compat.AddinDirectoryResolver.Register();
#endif

    public Result OnStartup(UIControlledApplication application)
    {
        // The working directory this Revit was started in, recorded before anything can change it.
        // A scheduled task can hand a per-job folder to the program it starts but cannot set an
        // environment variable, so for the Revit-direct task action (see TaskSchedulerGateway) this
        // is how the run says which job fired. Logged unconditionally because "which folder did
        // Revit actually inherit" is unanswerable after the fact, and it is one line — and it is
        // the line that would diagnose a future Revit that changes the directory on us.
        try { Diagnostics.RunTrace.Mark("launch cwd=" + Environment.CurrentDirectory); }
        catch { /* a breadcrumb must never stop the add-in loading */ }

        // FIRST, and before ScheduleEnvironment.IsUnattendedLaunch is read below: turn that folder
        // into the same KHBATCH_JOB/KHBATCH_OWNER pair every other launch shape arrives with, so
        // nothing downstream has to know there is more than one way to start a scheduled run.
        // 2.0.0: carry a 1.x install's settings and schedules into the renamed folders, once,
        // before LaunchIdentity (below) or anything else reads a store. The 1.x folders are kept.
        Settings.LegacyMigration.CopySettingsOnce();
        Schedule.LaunchIdentity.Resolve();

        application.CreateRibbonTab(TabName);

        string asm = Assembly.GetExecutingAssembly().Location;
        _lastThemeDark = Theme.IsDark();
        _palette = IconPalette.ForCurrentTheme();

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
            "model set, a Forma folder, or the active model.",
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

        // Install the Revit-backed implementations of the services shared dialogs need. Inside
        // Revit the action list comes from the registry and confirmations use Revit's own
        // TaskDialog, exactly as before; the standalone controller installs its own equivalents.
        UI.HostServices.ListActions = () => Actions.ActionRegistry.All()
            .Select(a => new UI.ActionInfo(a.Name, a.Category, a.ModifiesModel, a.Worksets))
            .ToList();
        // Which Revit is hosting, so the Forma model lists can grey out a model this one must not
        // open. Blank outside Revit, where it would be a guess rather than a fact.
        UI.HostServices.RunningRevitVersion = application.ControlledApplication.VersionNumber;
        UI.HostServices.Confirm = (title, message) =>
        {
            // Fully qualified: net8's WinForms also defines TaskDialog, and this add-in means Revit's.
            var confirm = new Autodesk.Revit.UI.TaskDialog(title)
            {
                MainInstruction = title,
                MainContent = message,
                CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Yes
                              | Autodesk.Revit.UI.TaskDialogCommonButtons.No,
                DefaultButton = Autodesk.Revit.UI.TaskDialogResult.No
            };
            return confirm.Show() == Autodesk.Revit.UI.TaskDialogResult.Yes;
        };

        // Create the ExternalEvent used to surface "leave files open" models after a batch
        // (activating documents is illegal from the Idling loop that drives the batch).
        Batch.LeftOpenDocsActivator.Initialize();

        // Repaint the glyphs whenever Revit's theme changes (no dedicated API event exists).
        application.Idling += OnThemeIdling;

        // Any supervised run whose watching Revit is gone (closed mid-recovery, or crashed) is
        // recorded as Abandoned rather than left claiming to be in progress — otherwise a run that
        // was cut short looks identical to one still going.
        try { Schedule.RunManifestStore.MarkAbandoned(); } catch { /* never block startup */ }

        // Clear notifications whose retention window passed while nothing was running. Each raised
        // toast tidies the older ones, so this only matters for the LAST notification of a run that
        // finished unattended — which is exactly the one still on screen the next morning.
        try { UI.RunNotification.SweepExpiredToasts(); } catch { /* never block startup */ }

        // UNATTENDED ONLY: start the dialog watchdog NOW, on its own thread, before anything that a
        // modal dialog could block. Not when the batch begins — a batch begins from Idling, and a
        // dialog raised during Revit's own startup stops Idling from ever running. See
        // ForeignDialogBreaker.StartShared for the 2026-08-18 run this cost.
        //
        // Keyed to the launch environment rather than to a job this edition claimed: when Full and
        // DRW are both loaded, only one of them accepts the job, but BOTH are in the process that
        // the dialog would freeze. StartShared is idempotent, so the loser arming it is harmless.
#if !EDITION_DRW
        // Parameter Helper's live updater (optional, so a model never asks for it). Interactive
        // sessions only: a scheduled batch must do exactly what its actions say and nothing else.
        if (!ScheduleEnvironment.IsUnattendedLaunch) ParamFormula.LiveFormulas.Startup(application);
        // Parameter Helper's headless self-test: PARAMHELPER_SELFTEST=<report> + PARAMHELPER_SELFTEST_DIR=<work folder>.
        if (ParamFormula.ParamHelperSelfTest.IsRequested(out string phReport, out string phDir))
            ParamFormula.ParamHelperSelfTest.ArmOnIdling(application, phReport, phDir);
#endif
        if (ScheduleEnvironment.IsUnattendedLaunch)
        {
            try { Batch.ForeignDialogBreaker.StartShared(); }
            catch (Exception ex) { Diagnostics.RunTrace.Mark("dialog watchdog not started: " + ex.Message); }
        }

        // Diagnostic: if launched with KHBATCH_SELFTEST=<logpath>, probe the Forma REST stack
        // headlessly against the signed-in session and self-terminate (dev/test only).
        if (Diagnostics.SelfTest.IsRequested(out string selfTestLog))
            Diagnostics.SelfTest.ArmOnIdling(application, selfTestLog);
#if EDITION_DRW
        // Enlarge Door & Window's live update (optional updater). Interactive sessions only, as
        // Parameter Helper's: a scheduled batch must do exactly what its actions say.
        if (!ScheduleEnvironment.IsUnattendedLaunch) Drw.DoorWindow.LiveDoorWindow.Startup(application);
        // DRW's own headless self-test: DRW_SELFTEST=<report> + DRW_SELFTEST_MODEL=<.rvt>.
        if (Drw.SelfTest.DrwSelfTest.IsRequested(out string drwReport, out string drwModel))
            Drw.SelfTest.DrwSelfTest.ArmOnIdling(application, drwReport, drwModel);
#endif
        // If launched with KHBATCH_TOKEN_BROKER=<pipeName>, serve the signed-in session's Forma
        // token to the standalone controller over that pipe and stay alive (hidden) until the
        // owning process exits. Opens no document; must sit above the UpdateCheck branch so a
        // broker — which has no visible window — can never be blocked behind a dialog.
        else if (Auth.TokenBrokerServer.IsRequested(out string brokerPipe))
            Auth.TokenBrokerServer.ArmOnIdling(application, brokerPipe);
        // If launched by a scheduled task — or as one group of an auto-split run — run the job
        // headless once Revit is idle. The breadcrumb is the child half of the launch record: an
        // auto-split parent logs a line per group it started, and a group that never gets this far
        // (Revit failed to start, sat on a sign-in prompt, ran out of memory) leaves the gap visible.
        else if (ScheduledRunner.TryGetPendingJob(out ScheduledJob? job) && job != null)
        {
            Diagnostics.RunTrace.Mark(
                $"armed {(job.IsEphemeralChunk ? "group " + job.Id : "schedule '" + job.Name + "'")}");
            ScheduledRunner.ArmOnIdling(application, job);
        }
        // Interactive session only (neither headless path armed): daily build-validity check + a
        // one-shot "update available" notice. Headless self-test/scheduled runs must never pop a
        // dialog, so they deliberately skip this — they refresh from the gate instead.
#if EDITION_DRW
        // DRW Helper has no update manifest of its own yet. The batch runner's latest.json would
        // report its 1.5.x as "newer" than DRW 0.x every day, and the cache it keeps is shared, so
        // DRW stays off that channel entirely. It also has no legacy schedule registrations.
#else
        else
        {
            // Upgrade existing schedule registrations once after installing this build. Older
            // launchers used locale-sensitive /SD text and returned immediately after START;
            // recreating them from XML fixes local time and overlapping execution without asking
            // the user to delete/re-save every schedule manually.
            try
            {
                string revitExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                TaskSchedulerGateway.MigrateLegacyRegistrations(revitExe);
                // ...then retire each 1.x task whose 2.0.0 task is now registered.
                Settings.LegacyMigration.RemoveLegacyTasks();
            }
            catch { /* schedule repair must never block Revit startup */ }
            Update.UpdateCheck.ArmOnIdling(application);
        }
#endif

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.Idling -= OnThemeIdling;
#if !EDITION_DRW
        ParamFormula.LiveFormulas.Shutdown(application);
        Mcp.McpBridge.Stop();
#endif
#if EDITION_DRW
        application.ViewActivated -= Drw.SyncView.SyncViewService.OnViewActivated;
        Drw.DoorWindow.LiveDoorWindow.Shutdown(application);
#endif
        Batch.LeftOpenDocsActivator.Shutdown();
        Batch.ForeignDialogBreaker.StopShared();
        Update.UpdateCheck.Detach(application);
        return Result.Succeeded;
    }

    private void OnThemeIdling(object? sender, IdlingEventArgs e)
    {
        CaptureSignedInUser(sender);

        bool dark = Theme.IsDark();
        if (dark == _lastThemeDark) return;   // no change → nothing to redraw.
        _lastThemeDark = dark;
        _palette = IconPalette.ForCurrentTheme();
        foreach ((RibbonButton button, Func<IconPalette, int, BitmapSource> icon, bool smallOnly) in _themedButtons)
            Paint(button, icon, smallOnly);
    }

    /// <summary>
    /// Keeps <see cref="UI.HostServices.SignedInUserId"/> current with the Autodesk account Revit
    /// is signed in as.
    ///
    /// <para>Read here rather than in <c>OnStartup</c> because <c>LoginUserId</c> lives on
    /// <c>Application</c>, which <c>UIControlledApplication</c> does not expose — the same reason
    /// <see cref="Auth.TokenBrokerServer"/> captures it from Idling. Read on EVERY tick, not once:
    /// a user can sign out and back in as someone else without restarting Revit, and that is the
    /// exact case the account-aware model lists exist for. The read is a property access; the
    /// snapshot behind it is only recomputed when the value actually changes.</para>
    /// </summary>
    private static void CaptureSignedInUser(object? sender)
    {
        if (sender is not UIApplication ui) return;
        string user;
        try { user = ui.Application.LoginUserId ?? ""; }
        catch { return; /* non-fatal: an unknown account is Unknown, never "no access" */ }

        if (string.Equals(user, UI.HostServices.SignedInUserId, StringComparison.Ordinal)) return;
        UI.HostServices.SignedInUserId = user;
        Presets.AccountAccess.Invalidate();
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
