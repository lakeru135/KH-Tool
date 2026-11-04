using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Drw.Annotation;
using KhBatchRunner.Drw.Common;
using KhBatchRunner.Drw.Dialogs;
using KhBatchRunner.Drw.DoorWindow;
using KhBatchRunner.Drw.Enlarge;
using KhBatchRunner.Drw.SyncView;
using KhBatchRunner.UI;

// WinForms and Revit both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Commands;

/// <summary>DRW Helper, Setting → Sync View: turns view zoom syncing on or off for this session.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class DrwSyncViewCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        SyncViewService.Toggle();
        return Result.Succeeded;
    }
}

/// <summary>Shared body of Enlarge Room / Enlarge Stair: list targets, ask, create, report.</summary>
public abstract class DrwEnlargeCommandBase : IExternalCommand
{
    protected abstract bool IsStair { get; }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDoc = commandData.Application.ActiveUIDocument;
        Document doc = uiDoc.Document;
        DrwPresetStore<EnlargePreset> store = IsStair ? EnlargeStores.Stair : EnlargeStores.Room;
        EnlargePreset initial = store.TryLoadLastUsed() ?? new EnlargePreset();
        string title = IsStair ? "Enlarge Stair" : "Enlarge Room";

        // Targets are listed with the naming options the dialog opens with; the run re-reads them
        // with the options the user finally chose (labels and marks can change in the dialog).
        List<EnlargeTarget> listed = ListTargets(doc, initial);
        if (listed.Count == 0)
        {
            TaskDialog.Show(title, IsStair ? "This model has no stairs." : "This model has no placed rooms.");
            return Result.Cancelled;
        }

        // What the user had selected before clicking: rooms or room tags; stairs, their runs and
        // landings. Each is mapped to the target it belongs to and arrives ticked.
        var fromModel = new HashSet<ElementId>(TargetIdsFor(doc, uiDoc.Selection.GetElementIds()));
        var ticked = new HashSet<ElementId>(fromModel);
        List<ParamChoice> viewParams = DrwParams.ViewParameters(doc)
            .Select(p => new ParamChoice(p.Label, p.Name, p.Guid)).ToList();
        // Templates that impose a scale, so the dialog can say when the Scale field will not apply.
        var templateScales = new Dictionary<string, int>();
        foreach ((string kind, ViewType type) in new[] { ("Plan", ViewType.FloorPlan), ("Section", ViewType.Section), ("Elevation", ViewType.Elevation) })
            foreach (View template in DrwLookup.Templates(doc, type))
                if (DrwLookup.ControlledScale(template) is int s) templateScales[$"{kind}|{template.Name}"] = s;

        EnlargePreset working = initial;
        EnlargePreset preset;
        List<int> chosen;
        while (true)
        {
            var data = new EnlargeDialogData
            {
                IsStair = IsStair,
                PlanTypes = DrwLookup.Labels(DrwLookup.ViewTypes(doc, ViewFamily.FloorPlan)),
                SectionTypes = DrwLookup.Labels(DrwLookup.ViewTypes(doc, ViewFamily.Section)),
                ElevationTypes = DrwLookup.Labels(DrwLookup.ViewTypes(doc, ViewFamily.Elevation)),
                PlanTemplates = DrwLookup.Names(DrwLookup.Templates(doc, ViewType.FloorPlan)),
                SectionTemplates = DrwLookup.Names(DrwLookup.Templates(doc, ViewType.Section)),
                ElevationTemplates = DrwLookup.Names(DrwLookup.Templates(doc, ViewType.Elevation)),
                Targets = listed.Select(t => t.Display).ToList(),
                Preselected = new HashSet<int>(Enumerable.Range(0, listed.Count).Where(i => ticked.Contains(listed[i].SourceId))),
                FromSelection = listed.Count(t => fromModel.Contains(t.SourceId)),
                ViewParameters = viewParams,
                TemplateScales = templateScales
            };

            using var window = new EnlargeWindow(data, working, store);
            System.Windows.Forms.DialogResult result = window.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.Retry && window.PickRequested)
            {
                // Keep everything the dialog held, pick in the model, then reopen with the picks added.
                working = window.Snapshot;
                ticked = new HashSet<ElementId>(window.SelectedTargets.Select(i => listed[i].SourceId));
                foreach (ElementId id in PickTargets(uiDoc))
                {
                    ticked.Add(id);
                    fromModel.Add(id);
                }
                continue;
            }
            if (result != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
            preset = window.Result;
            chosen = window.SelectedTargets.ToList();
            break;
        }
        store.SaveLastUsed(preset);

        var chosenIds = new HashSet<ElementId>(chosen.Select(i => listed[i].SourceId));
        List<EnlargeTarget> targets = ListTargets(doc, preset).Where(t => chosenIds.Contains(t.SourceId)).ToList();

        bool writeMarks = false;
        if (IsStair && preset.OverrideMultistoryMarks)
        {
            int changes = targets.Sum(t => t.MarkWrites.Count(w => (DrwParams.Mark(doc.GetElement(w.Id)) ?? "") != w.Mark));
            if (changes > 0)
            {
                var confirm = new TaskDialog(title)
                {
                    MainInstruction = $"Change the Mark of {changes} stair element(s)?",
                    MainContent = "Multistory stair members will be renumbered " +
                                  $"<mark>{preset.MarkDelimiter}1, <mark>{preset.MarkDelimiter}2… from the bottom up. " +
                                  "Choose No to create the views and leave every Mark as it is.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Yes
                };
                TaskDialogResult answer = confirm.Show();
                if (answer == TaskDialogResult.Cancel) return Result.Cancelled;
                writeMarks = answer == TaskDialogResult.Yes;
            }
        }

        List<IReadOnlyDictionary<string, string>> rows;
        try
        {
            rows = new EnlargeCreator(doc, preset, IsStair).Create(targets, writeMarks);
        }
        catch (Exception ex)
        {
            TaskDialog.Show(title, "Could not create the views:\n\n" + ex.Message);
            return Result.Failed;
        }
        DrwResults.Show(title, uiDoc, EnlargeCreator.Columns, rows);
        return Result.Succeeded;
    }

    /// <summary>
    /// The target ids (room, standalone stair or multistory stair) behind whatever is selected or
    /// picked: a room tag stands for its room; a stair run or landing for its stair; a member of a
    /// multistory stair for the whole group, which is what Enlarge Stair lists.
    /// </summary>
    private IEnumerable<ElementId> TargetIdsFor(Document doc, IEnumerable<ElementId> ids)
    {
        foreach (ElementId id in ids)
        {
            Element? e = doc.GetElement(id);
            if (!IsStair)
            {
                if (e is Autodesk.Revit.DB.Architecture.Room) yield return id;
                else if (e is Autodesk.Revit.DB.Architecture.RoomTag tag && tag.TaggedLocalRoomId != ElementId.InvalidElementId)
                    yield return tag.TaggedLocalRoomId;
                continue;
            }
            Autodesk.Revit.DB.Architecture.Stairs? st = e switch
            {
                Autodesk.Revit.DB.Architecture.Stairs s => s,
                Autodesk.Revit.DB.Architecture.StairsRun r => r.GetStairs(),
                Autodesk.Revit.DB.Architecture.StairsLanding l => l.GetStairs(),
                _ => null
            };
            if (e is Autodesk.Revit.DB.Architecture.MultistoryStairs) yield return id;
            else if (st != null)
                yield return st.MultistoryStairsId != ElementId.InvalidElementId ? st.MultistoryStairsId : st.Id;
        }
    }

    /// <summary>Lets the user pick rooms (or tags) / stairs in the model; Esc just returns nothing.</summary>
    private List<ElementId> PickTargets(UIDocument uiDoc)
    {
        try
        {
            IList<Reference> refs = uiDoc.Selection.PickObjects(
                Autodesk.Revit.UI.Selection.ObjectType.Element, new TargetFilter(IsStair),
                IsStair ? "Pick stairs (or their runs/landings), then click Finish"
                        : "Pick rooms or room tags, then click Finish");
            return TargetIdsFor(uiDoc.Document, refs.Select(r => r.ElementId)).Distinct().ToList();
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return new List<ElementId>();
        }
    }

    private sealed class TargetFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
    {
        private readonly bool _stairs;
        public TargetFilter(bool stairs) => _stairs = stairs;

        public bool AllowElement(Element e) => _stairs
            ? e is Autodesk.Revit.DB.Architecture.Stairs or Autodesk.Revit.DB.Architecture.StairsRun
                or Autodesk.Revit.DB.Architecture.StairsLanding or Autodesk.Revit.DB.Architecture.MultistoryStairs
            : e is Autodesk.Revit.DB.Architecture.Room or Autodesk.Revit.DB.Architecture.RoomTag;

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    private List<EnlargeTarget> ListTargets(Document doc, EnlargePreset p) => IsStair
        ? EnlargeTargets.Stairs(doc, p.MarkDelimiter, p.OverrideMultistoryMarks, p.StairBeforeFirstStepM, p.StairAfterLastStepM)
        : EnlargeTargets.Rooms(doc, p.RoomLabel);
}

[Transaction(TransactionMode.Manual)]
public sealed class DrwEnlargeRoomCommand : DrwEnlargeCommandBase
{
    protected override bool IsStair => false;
}

[Transaction(TransactionMode.Manual)]
public sealed class DrwEnlargeStairCommand : DrwEnlargeCommandBase
{
    protected override bool IsStair => true;
}

/// <summary>DRW Helper, Annotation → Datum Dimension (Grid Dimension before 0.2.6): grids in plans,
/// grids and levels in sections and elevations.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class DrwDatumDimensionCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDoc = commandData.Application.ActiveUIDocument;
        Document doc = uiDoc.Document;
        DatumDimensionPreset initial = DatumDimensionStore.Store.TryLoadLastUsed() ?? new DatumDimensionPreset();
        List<DimensionType> linear = DatumDimensioner.LinearTypes(doc);
        List<string> dimTypes = linear.Select(t => DrwLookup.TypeLabel(t))
            .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var snaps = new Dictionary<string, double?>();
        foreach (DimensionType t in linear) snaps[DrwLookup.TypeLabel(t)] = DatumDimensioner.SnapDistanceMm(t);

        using var window = new DatumDimensionWindow(dimTypes, snaps, doc.ActiveView?.Name ?? "",
            DrwResults.SelectedDatumViewCount(doc, uiDoc), initial);
        if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
        DatumDimensionPreset p = window.Result;
        DatumDimensionStore.Store.SaveLastUsed(p);

        List<View> views = DrwScope.ResolveDatumViews(doc, uiDoc, p.Scope, p.ViewNameContains, p.Plans, p.Sections, p.Elevations);
        if (views.Count == 0)
        {
            TaskDialog.Show("Datum Dimension", "No view to dimension: the active view is not a plan, section or " +
                                               "elevation, or no view matched.");
            return Result.Cancelled;
        }
        DrwResults.Show("Datum Dimension", uiDoc, DatumDimensioner.Columns, DatumDimensioner.Run(doc, views, p));
        return Result.Succeeded;
    }
}

/// <summary>DRW Helper, Annotation → Single Font: one font for the model's text types, dimension
/// types, schedule cell fonts and the text inside loaded families. In the Family Editor, the open
/// family's own text/label types and the families nested in it; loading it back into a project is
/// left to the user.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class DrwSingleFontCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDoc = commandData.Application.ActiveUIDocument;
        Document doc = uiDoc.Document;
        SingleFontPreset initial = SingleFontStore.Store.TryLoadLastUsed() ?? new SingleFontPreset();
        using var window = new SingleFontWindow(SingleFontEngine.InstalledFonts(), SingleFontEngine.Counts(doc), initial,
            doc.IsFamilyDocument);
        if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
        SingleFontPreset p = window.Result;
        SingleFontStore.Store.SaveLastUsed(p);
        List<IReadOnlyDictionary<string, string>> rows;
        try { rows = SingleFontEngine.Run(doc, p); }
        catch (Exception ex)
        {
            TaskDialog.Show("Single Font", "Could not change the fonts:\n\n" + ex.Message);
            return Result.Failed;
        }
        DrwResults.Show("Single Font", uiDoc, SingleFontEngine.Columns, rows);
        return Result.Succeeded;
    }
}

/// <summary>DRW Helper, Annotation → Match Datums: the active view's grid/level ends, bubbles and
/// elbows onto the parallel views the user ticks. Interactive only — the user picks the views.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class DrwMatchDatumsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDoc = commandData.Application.ActiveUIDocument;
        Document doc = uiDoc.Document;
        View source = doc.ActiveView;
        if (source == null || !DatumMatcher.IsMatchView(source))
        {
            TaskDialog.Show("Match Datums", "Open the plan, ceiling plan, section or elevation whose grids and levels are arranged " +
                                            "the way you want, then click Match Datums.");
            return Result.Cancelled;
        }
        List<View> candidates = DatumMatcher.Candidates(doc, source);
        if (candidates.Count == 0)
        {
            TaskDialog.Show("Match Datums", $"No other view at 1:{source.Scale} is parallel to \"{source.Name}\". " +
                                            "Match Datums lists only views at the active view's scale.");
            return Result.Cancelled;
        }

        ElementId sourceBox = DatumMatcher.ScopeBox(source);
        var choices = candidates.Select(v => new MatchDatumsWindow.Choice(
            DrwIds.Of(v.Id), v.Name, KindOf(v), DatumMatcher.ScopeBox(v) is { } b && b != ElementId.InvalidElementId ? DrwIds.Of(b) : -1)).ToList();
        var selected = uiDoc.Selection.GetElementIds().Select(DrwIds.Of);
        using var window = new MatchDatumsWindow(source.Name, KindOf(source),
            sourceBox != ElementId.InvalidElementId ? DrwIds.Of(sourceBox) : -1,
            sourceBox != ElementId.InvalidElementId ? doc.GetElement(sourceBox)?.Name ?? "" : "",
            choices, selected, source.Scale);
        if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;

        var wanted = new HashSet<long>(window.SelectedIds);
        List<View> targets = candidates.Where(v => wanted.Contains(DrwIds.Of(v.Id))).ToList();
        DrwResults.Show("Match Datums", uiDoc, DatumMatcher.Columns, DatumMatcher.Run(doc, source, targets));
        return Result.Succeeded;
    }

    private static string KindOf(View v) => v is ViewPlan ? "Plan" : v.ViewType == ViewType.Section ? "Section" : "Elevation";
}

[Transaction(TransactionMode.Manual)]
public sealed class DrwStairAnnotationCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDoc = commandData.Application.ActiveUIDocument;
        Document doc = uiDoc.Document;
        // A first run ticks dimensions; a last-used preset from before 0.2.9.1 keeps them off.
        StairAnnotationPreset initial = StairAnnotationStore.Store.TryLoadLastUsed() ?? new StairAnnotationPreset { PlaceDimensions = true };
        List<DimensionType> linear = DatumDimensioner.LinearTypes(doc);
        List<string> dimTypes = linear.Select(t => DrwLookup.TypeLabel(t))
            .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var snaps = new Dictionary<string, double?>();
        foreach (DimensionType t in linear) snaps[DrwLookup.TypeLabel(t)] = DatumDimensioner.SnapDistanceMm(t);

        using var window = new StairAnnotationWindow(
            DrwLookup.Labels(StairAnnotator.PathTypes(doc)), dimTypes, snaps,
            doc.ActiveView?.Name ?? "", DrwResults.SelectedStairViewCount(doc, uiDoc), initial);
        if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
        StairAnnotationPreset p = window.Result;
        StairAnnotationStore.Store.SaveLastUsed(p);

        List<View> views = DrwScope.ResolveStairViews(doc, uiDoc, p.Scope, p.ViewNameContains, p.Sections);
        if (views.Count == 0)
        {
            TaskDialog.Show("Stair Annotation", "No floor plan or section to annotate: the active view is neither, or no view matched.");
            return Result.Cancelled;
        }
        DrwResults.Show("Stair Annotation", uiDoc, StairAnnotator.Columns, StairAnnotator.Run(doc, views, p));
        return Result.Succeeded;
    }
}

/// <summary>DRW Helper, Annotation → Preset Tagger: each rule's category tagged with its own tag type
/// in the chosen views; spots that overlap other annotation avoided, elements already tagged skipped.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class DrwPresetTaggerCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDoc = commandData.Application.ActiveUIDocument;
        Document doc = uiDoc.Document;
        PresetTaggerPreset initial = PresetTaggerStore.Store.TryLoadLastUsed() ?? new PresetTaggerPreset();
        List<(string Key, string Label)> categories = PresetTagger.Categories(doc);
        var data = new PresetTaggerDialogData
        {
            Categories = categories,
            TagTypes = PresetTagger.TagTypeLabels(doc, categories.Select(c => c.Key)
                .Concat(PresetTaggerStore.Store.LoadAll().Append(initial).SelectMany(p => p.Rules).Select(r => r.Category))
                .Distinct()),
            ActiveView = doc.ActiveView?.Name ?? "",
            SelectedViews = uiDoc.Selection.GetElementIds().Select(doc.GetElement).OfType<View>().Count(PresetTagger.IsTagView)
        };

        using var window = new PresetTaggerWindow(data, initial);
        if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
        PresetTaggerPreset p = window.Result;
        PresetTaggerStore.Store.SaveLastUsed(p);

        List<View> views = DrwScope.ResolveTagViews(doc, uiDoc, p.Scope, p.ViewNameContains,
            p.Plans, p.CeilingPlans, p.Sections, p.Elevations);
        if (views.Count == 0)
        {
            TaskDialog.Show("Preset Tagger", "No view to tag: the active view is not a plan, ceiling plan, section or " +
                                             "elevation, or no view matched.");
            return Result.Cancelled;
        }
        List<IReadOnlyDictionary<string, string>> rows;
        try { rows = PresetTagger.Run(doc, views, p); }
        catch (Exception ex)
        {
            TaskDialog.Show("Preset Tagger", "Could not tag:\n\n" + ex.Message);
            return Result.Failed;
        }
        DrwResults.Show("Preset Tagger", uiDoc, PresetTagger.Columns, rows);
        return Result.Succeeded;
    }
}

/// <summary>DRW Helper, Annotation → Wall Mat Tag: a material tag on each finish face of the walls in
/// the views, of the type whose triangle sits on the side the face is seen from; optional "?" repair.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class DrwWallMatTagCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDoc = commandData.Application.ActiveUIDocument;
        Document doc = uiDoc.Document;
        WallMatTagPreset initial = WallMatTagStore.Store.TryLoadLastUsed() ?? new WallMatTagPreset();
        var data = new WallMatTagDialogData
        {
            // The family that comes with DRW Helper is offered too: it is loaded when first used.
            MaterialTagTypes = DrwLookup.Labels(PresetTagger.MaterialTagTypes(doc)).Union(BundledFamilies.WallMatTypes())
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList(),
            ActiveView = doc.ActiveView?.Name ?? "",
            SelectedViews = uiDoc.Selection.GetElementIds().Select(doc.GetElement).OfType<View>()
                .Count(v => DrwScope.IsPlan(v) || v.ViewType is ViewType.Section or ViewType.Elevation)
        };

        using var window = new WallMatTagWindow(data, initial);
        if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
        WallMatTagPreset p = window.Result;
        WallMatTagStore.Store.SaveLastUsed(p);

        List<View> views = WallMatTag.Views(doc, uiDoc, p);
        if (views.Count == 0)
        {
            TaskDialog.Show("Wall Mat Tag", "No view to tag: the active view is not a plan, section or elevation, or no view matched.");
            return Result.Cancelled;
        }
        List<IReadOnlyDictionary<string, string>> rows;
        var materialTags = new List<ElementId>();
        try { rows = WallMatTag.Run(doc, views, p, materialTags); }
        catch (Exception ex)
        {
            TaskDialog.Show("Wall Mat Tag", "Could not tag:\n\n" + ex.Message);
            return Result.Failed;
        }
        // New material tags read "?" until touched in Revit: leave them selected, ready to nudge.
        if (materialTags.Count > 0)
            try { uiDoc.Selection.SetElementIds(materialTags); } catch { /* tags in a view not open */ }
        DrwResults.Show("Wall Mat Tag", uiDoc, WallMatTag.Columns, rows);
        if (materialTags.Count > 0)
            TaskDialog.Show("Wall Mat Tag",
                $"{materialTags.Count} new material tag{(materialTags.Count == 1 ? "" : "s")} will show '?' until touched in Revit: " +
                "Revit's API cannot link a new material tag to its material.\n\n" +
                "They are left selected. Touch them as you would fix a '?' tag by hand (for example nudge them with an " +
                "arrow key and back), then check they show their material.");
        return Result.Succeeded;
    }
}

/// <summary>DRW Helper, Create → Enlarge Door &amp; Window: the door/window sheet as legends, one cell
/// per Type Mark. Opens the set's own schedule (made from the starter if missing) for the user to
/// edit, with the Enlarge window beside it, kept open (the user, 2026-10-21); the window builds.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class DrwEnlargeDoorWindowCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication app = commandData.Application;
        UIDocument uiDoc = app.ActiveUIDocument;
        Document doc = uiDoc.Document;
        DoorWindowPreset initial = DoorWindowStores.Store.TryLoadLastUsed() ?? new DoorWindowPreset();

        // Only what the window shows first is read here: items, the set's schedule, and the NAMES of
        // title blocks. A title block's lines and the plan widths are read when the Sheets tab first
        // needs them — measuring every plan up front took 21 s of the first press on Snowdon.
        var model = new DoorWindowModel(app, doc);
        var made = new List<string>();
        DwScheduleData? schedule;
        try { schedule = model.EnsureAndOpen(initial, made); }
        catch (Exception ex)
        {
            TaskDialog.Show("Enlarge Door & Window", "Could not make or open the set's schedule:\n\n" + ex.Message);
            return Result.Failed;
        }
        var data = new DoorWindowDialogData
        {
            Items = model.Items(initial),
            Schedule = schedule,
            TitleBlocks = DoorWindowSheets.TitleBlocks(doc).Select(s => new DwTitleBlockData { Name = DoorWindowSheets.Label(s) }).ToList(),
            Warnings = made.Select(m => "Schedule: " + m + ".").ToList(),
            ProjectUnits = DoorWindowItems.ProjectUnitsLabel(doc),
            SampleSize = DoorWindowItems.Size(doc, 900 / 304.8, 2100 / 304.8, DwSizeFormat.ProjectUnits),
            DimensionTypes = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(d => d.StyleType == DimensionStyleType.Linear).Select(d => d.Name).Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
        };
        DoorWindowSession.Open(app, doc, model, data, initial);
        return Result.Succeeded;
    }
}

/// <summary>Result reporting shared by the DRW commands — the same window an active-model batch action shows.</summary>
internal static class DrwResults
{
    public static void Show(string title, UIDocument uiDoc, IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        if (rows.Count == 0)
        {
            TaskDialog.Show(title, "Nothing to do — no matching elements were found.");
            return;
        }
        Document doc = uiDoc.Document;
        int created = rows.Count(r => r.TryGetValue("Status", out string? s) && s is "Created" or "Changed");
        int would = rows.Count(r => r.TryGetValue("Status", out string? s) && s is "Would change" or "Would create");
        int skipped = rows.Count(r => r.TryGetValue("Status", out string? s) && s == "Skipped");
        int repaired = rows.Count(r => r.TryGetValue("Status", out string? s) && s is "Repaired" or "Would repair");
        int matched = rows.Count(r => r.TryGetValue("Status", out string? s) && s == "Matched");
        int partly = rows.Count(r => r.TryGetValue("Status", out string? s) && s is "Partly matched" or "Not applied");
        int failed = rows.Count(r => r.TryGetValue("Status", out string? s) && s == "Failed");
        int warned = rows.Count(r => r.TryGetValue("Status", out string? s) && s == "Warning");
        string done = title == "Match Datums" ? $"{matched} matched"
            : title == "Single Font" ? (would > 0 ? $"{would} would change (nothing was changed)" : $"{created} changed")
            : title is "Preset Tagger" or "Wall Mat Tag" ? (would > 0 ? $"{would} would be tagged (nothing was changed)" : $"{created} tagged") +
                                         $", {skipped} skipped" +
                                         (repaired > 0 ? $", {repaired} '?' material tag{(repaired == 1 ? "" : "s")} {(would > 0 ? "would be " : "")}repaired" : "")
            : $"{created} created";
        string note = done +
                      (partly > 0 ? $", {partly} not fully kept by Revit (see Note)" : "") +
                      $", {failed} failed" +
                      (warned > 0 ? ", a warning in the first row" : "") +
                      (would > 0 ? "." : ". One Undo reverses the whole run.");
        using var results = new ActionResultsWindow(title, doc.Title, columns, rows, note, true,
            row => OpenView(uiDoc, row));
        results.ShowDialog();
    }

    /// <summary>
    /// Opens the view a result row names, behind the (still open) results window. By name — the
    /// rows are plain text, shared with the batch CSV — preferring the view type the row's Kind
    /// says, since a plan and a section may share a name. Returns why it could not, or null.
    /// The run's transactions have all closed by now, which is what lets the active view change.
    /// </summary>
    private static string? OpenView(UIDocument uiDoc, IReadOnlyDictionary<string, string> row)
    {
        string name = row.TryGetValue("View", out string? v) ? v.Trim() : "";
        if (name.Length == 0) return "This row has no view to open.";
        row.TryGetValue("Kind", out string? kind);
        ViewType? wanted = kind switch
        {
            "Plan" => ViewType.FloorPlan,
            "Section" => ViewType.Section,
            "Elevation" => ViewType.Elevation,
            _ => null
        };
        List<View> named = new FilteredElementCollector(uiDoc.Document).OfClass(typeof(View)).Cast<View>()
            .Where(x => !x.IsTemplate && x.Name == name).ToList();
        View? view = named.FirstOrDefault(x => wanted == null || x.ViewType == wanted) ?? named.FirstOrDefault();
        if (view == null) return $"The view \"{name}\" no longer exists.";
        try
        {
            uiDoc.ActiveView = view;
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not open \"{name}\": {ex.Message}";
        }
    }

    public static int SelectedStairViewCount(Document doc, UIDocument uiDoc) =>
        uiDoc.Selection.GetElementIds().Select(doc.GetElement).OfType<View>().Count(StairAnnotator.IsStairView);

    public static int SelectedDatumViewCount(Document doc, UIDocument uiDoc) =>
        uiDoc.Selection.GetElementIds().Select(doc.GetElement).OfType<View>().Count(DrwScope.IsDatumView);
}

/// <summary>An element id as a plain number for the WinForms side, which never references Revit types.</summary>
internal static class DrwIds
{
    public static long Of(ElementId id) => id.Value;
}
