using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Batch;
using KhBatchRunner.Interop;

namespace KhBatchRunner.Actions;

/// <summary>
/// Writes Navisworks <c>.nwc</c> files — either one per model (whole model), or one per 3D view
/// whose title contains a given string, which is how the team's existing tool works and therefore
/// how the exports they already have are named.
///
/// <para><b>One model can produce several files</b>, so this reports one log row per FILE rather
/// than per model: "did I actually get the views I asked for?" is a per-view question, and a
/// per-model row cannot answer it.</para>
///
/// <para><b>The fallback is the feature.</b> A model set is rarely uniform enough that every model
/// carries matching views, and a batch that failed the odd model over a missing view would be more
/// trouble than exporting by hand. A model matching nothing falls back to a whole-model export and
/// says so in its row; the run finishes either way.</para>
///
/// <para><b>Category is Check, like Cut model and Initiate.</b> Nothing is written back: the engine
/// opens the source detached, never syncs it, and this action only reads geometry out to a file.
/// Marking it an Action would make the engine try to save a detached document per model.</para>
///
/// <para><b>Availability is a runtime question.</b> <see cref="NavisworksExportOptions"/> is part of
/// <c>RevitAPI.dll</c> and always compiles; the exporter that services it arrives with Navisworks.
/// <see cref="NavisworksLocator"/> is checked once per run in <see cref="ConfigureOutput"/> so an
/// unequipped PC gets one clear failure instead of the same COM error once per model.</para>
/// </summary>
public sealed class ExportNwcAction : BatchActionBase
{
    public const string DisplayName = ActionNames.ExportNwc;

    private readonly ExportNwcInputSet _spec;
    private readonly string _revitVersion;
    private string _outputFolder = "";
    private string _note = "";
    // File names used so far this RUN, so no export can quietly overwrite an earlier one.
    private readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);

    // What SelectWorksetsToOpen decided for the CURRENT model, in the words the row will use. Set on
    // the first, cheap open and read back after the reopen. Re-set per model: the engine calls
    // SelectWorksetsToOpen before RunDetailRows for every model in a group. The COUNTS are not kept
    // here — the reopened document is the authority on what actually opened, and asking it (see
    // WorksetCounts) cannot go stale the way a remembered number can.
    private string _worksetPlan = "";

    /// <param name="revitVersion">
    /// This Revit's <c>Application.VersionNumber</c>. Passed in rather than read from the
    /// <c>UIApplication</c> at use time because the availability check belongs in
    /// <see cref="ConfigureOutput"/> — which runs once, before the first model, and is handed no
    /// application to ask.
    /// </param>
    public ExportNwcAction(ExportNwcInputSet spec, string revitVersion)
    {
        _spec = spec;
        _revitVersion = revitVersion;
    }

    public override string Name => DisplayName;

    public override IReadOnlyList<string> Columns { get; } = new[]
        { "Exported File", "Scope", "View Used", "Output Folder", "Size (KB)", "Contents" };

    public override string PerModelNote => _note;

    /// <summary>
    /// FALSE since 1.3.8.5, and the change that lets a run finish.
    ///
    /// <para>It was true out of caution borrowed from the Dynamo runner, which binds a graph to
    /// <c>UIApplication.ActiveUIDocument</c> and so would run against the wrong model in the
    /// background. <b>That reasoning does not transfer.</b> <c>doc.Export(...)</c> is a
    /// <see cref="Document"/> method whose receiver IS the document being exported — there is no
    /// ambient "current document" to get wrong. (Revit Design Automation exports NWC from documents
    /// opened exactly this way, with no UI at all.)</para>
    ///
    /// <para>The caution had a real cost: the activated path cannot close the model it just
    /// finished, because Revit forbids closing the ACTIVE document, so the runner opens the next
    /// model first and closes the previous one after. Two heavy cloud models are then open at once.
    /// The background path hands each model to <c>ModelDocumentCursor</c>, which closes it before
    /// the next one opens and never holds two — which is what the user asked for after an 8-model
    /// export died on its third model, twice.</para>
    ///
    /// <para>If a background export is ever refused, it fails loudly rather than quietly: the
    /// per-file check in <see cref="ExportOne"/> reports that no file appeared.</para>
    /// </summary>
    public override bool NeedsActiveDocument => false;

    /// <summary>
    /// FALSE since 1.3.8.8, replaced by <see cref="PlansWorksetOpen"/> and
    /// <see cref="SelectWorksetsToOpen"/> — which open the same worksets when a view needs them all,
    /// and far fewer when it does not.
    ///
    /// <para>It was true from 1.3.8.4 to 1.3.8.7 and it was right at the time: the engine opens every
    /// model with all worksets CLOSED, an element in a closed workset is not loaded at all, and the
    /// exporter walked an empty model and wrote a near-empty <c>.nwc</c> while reporting success.
    /// 1.3.8.7 finally made the open work — by NAMING the ids, because
    /// <c>WorksetConfigurationOption.OpenAllWorksets</c> is silently ignored on a background cloud
    /// open — and that is when the price arrived in full.</para>
    ///
    /// <para><b>MEASURED 2026-08-15, the run that proved 1.3.8.7:</b> a single instance peaked at
    /// 48.6 GB and another at 42.9 GB, four at once left a 64 GB machine with 12 MB free, one Revit
    /// died of an access violation inside the model loader with 569 MB free, and the reopens took
    /// 18–23 minutes each against 5.5 minutes for the same model run alone. The journals say why:
    /// <c>SITE2A-BLDD-SIGN</c> loaded SEVENTEEN other models, because every link sits in a workset too
    /// and opening them all makes Revit download and open the entire federated set.</para>
    ///
    /// <para>Which is the argument for asking the views instead. If a view hides a link's workset,
    /// a view-scoped NWC would not have contained that link anyway — so loading it was never work
    /// the export needed.</para>
    /// </summary>
    /// <summary><see cref="SelectWorksetsToOpen"/> derives the set from the views about to be
    /// exported. It always names at least one workset, so the engine always reopens.</summary>
    /// <summary>
    /// The same posture, reachable WITHOUT constructing the action. Its real constructor needs an
    /// input set and a Revit version, and constructing any <see cref="IBatchAction"/> outside Revit
    /// can fail to resolve <c>UIApplication</c> at all — which is why <c>ActionRegistry.All()</c>
    /// catches type-load failures. The schedule editor offers this action as a synthetic checklist
    /// item and has to know how it opens models before any of that exists, so the declaration is
    /// made static and the instance property returns it. ONE declaration, two ways to read it.
    /// </summary>
    public const WorksetPosture DeclaredWorksets = SyntheticActionPostures.ExportNwc;

    public override WorksetPosture Worksets => DeclaredWorksets;

    public override string WorksetPostureReason =>
        "Exports model geometry, so a closed open writes an empty .nwc and reports success — the " +
        "failure that cost 1.3.8.4 to 1.3.8.8. Derives its set from the export views' workset " +
        "visibility rather than opening everything: 13 of 58 and 16 of 59 on the live STR set, " +
        "against first opens of 13–30 minutes when every link loaded too.";

    /// <summary>
    /// The worksets the views about to be exported actually SHOW — read from the cheap, first,
    /// worksets-closed open, which is exactly where the engine calls this.
    ///
    /// <para><b>Why this is readable with the worksets shut:</b> views live in view worksets, and
    /// <c>CloseAllWorksets</c> closes only USER worksets. Proven by our own 1.3.8.3 logs, where the
    /// run found its matching views and built file names from them while no model element was
    /// loaded.</para>
    ///
    /// <para><b>The rule.</b> For each matching 3D view and each user workset:
    /// <see cref="View.GetWorksetVisibility"/> — <c>Visible</c> keep, <c>Hidden</c> drop,
    /// <c>UseGlobalSetting</c> defers to <see cref="Workset.IsVisibleByDefault"/> — then union across
    /// the views. A view TEMPLATE controlling the Worksets tab is handled by unioning the template's
    /// answer as well rather than by trusting that the view reports the applied value: the union is a
    /// superset either way, and a superset is the safe direction to be wrong in.</para>
    ///
    /// <para><b>Every uncertain path returns every workset.</b> No matching view (a whole-model
    /// export has nothing to ask), an empty derived set, or any exception at all. Under-opening is
    /// the failure mode this entire release arc has been about, and it fails SILENTLY — the model
    /// merely looks empty. Over-opening only costs time.</para>
    /// </summary>
    public override ICollection<WorksetId>? SelectWorksetsToOpen(Document doc)
    {
        List<WorksetId> all = AllUserWorksets(doc);
        try
        {
            if (_spec.ExportEntireModel) return Planned(all, all.Count, 0, "whole-model export");

            List<View3D> views = FindExportViews(doc, _spec.ViewTitleContains);
            if (views.Count == 0) return Planned(all, all.Count, 0, "no matching view");

            // Worksets this machine has PROVED hold nothing but links, from a run that had them
            // open. Empty unless links are unwanted and a record exists — see WorksetAudit.
            HashSet<string> skippable = SkippableLinkWorksets();
            int skipped = 0;

            var wanted = new HashSet<WorksetId>();
            foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                if (!views.Any(v => ShowsWorkset(doc, v, w))) continue;
                if (skippable.Contains(w.Name)) { skipped++; continue; }
                wanted.Add(w.Id);
            }

            if (wanted.Count == 0) return Planned(all, all.Count, views.Count, "no workset is visible");
            return Planned(wanted.ToList(), all.Count, views.Count, null, skipped);
        }
        catch (Exception ex)
        {
            return Planned(all, all.Count, 0, "the views could not be read: " + ex.Message);
        }
    }

    /// <summary>Records what was chosen so the row and the breadcrumb can say it, and returns the
    /// set unchanged. A plan nobody can see is how under-opening stayed invisible for three
    /// releases.</summary>
    private List<WorksetId> Planned(List<WorksetId> chosen, int total, int viewCount, string? fellBack,
                                    int skippedLinkOnly = 0)
    {
        _worksetPlan = fellBack != null
            ? $"all {total} workset(s) — {fellBack}"
            : $"{chosen.Count} of {total} workset(s), from {viewCount} view(s)";
        if (skippedLinkOnly > 0)
            _worksetPlan += $", skipping {skippedLinkOnly} proven link-only (links are not exported)";
        Diagnostics.RunTrace.Mark($"Export NWC | workset plan: {_worksetPlan}");
        return chosen;
    }

    /// <summary>
    /// Worksets that may be left SHUT because this model is known to hold nothing but links in them
    /// and this run is not exporting links. Empty — meaning "skip nothing" — unless every condition
    /// holds, because each one on its own would make the skip a guess:
    ///
    /// <list type="bullet">
    /// <item>the options set exports neither Revit links nor linked CAD, so their geometry cannot
    /// reach the file even if loaded;</item>
    /// <item>the engine published which model this is (<see cref="Batch.ModelContext"/>) — a detached
    /// document cannot say;</item>
    /// <item>and a previous run had those worksets OPEN and recorded what was in them. Without that
    /// record nothing is skipped, so a model's first run is exactly what it was before this existed.
    /// See <see cref="Batch.WorksetAudit"/> for why it cannot be decided on the closed open.</item>
    /// </list>
    /// </summary>
    private HashSet<string> SkippableLinkWorksets()
    {
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_spec.ExportLinks || _spec.ConvertLinkedCADFormats) return none;

        string key = Batch.ModelContext.CurrentKey;
        return string.IsNullOrEmpty(key) ? none : Batch.WorksetAudit.LinkOnlyWorksets(key);
    }

    /// <summary>
    /// Does <paramref name="view"/> show <paramref name="w"/>? Reads the Revit side — the view's
    /// answer and its template's, if it has one — and hands both to
    /// <see cref="WorksetVisibilityRule"/>, which owns the decision and is checkable without a Revit.
    /// Unreadable means YES; see the note on <see cref="SelectWorksetsToOpen"/>.
    /// </summary>
    private static bool ShowsWorkset(Document doc, View3D view, Workset w)
    {
        try
        {
            bool? fromView = Answer(view, w);

            ElementId templateId = view.ViewTemplateId;
            // Autodesk.Revit.DB.View spelled out: System.Windows.Forms.View is an ENUM of the same
            // name, and this assembly references both. Same trap as Form in the UI files.
            bool? fromTemplate = templateId != ElementId.InvalidElementId &&
                                 doc.GetElement(templateId) is Autodesk.Revit.DB.View t
                ? Answer(t, w)
                : (bool?)false;   // no template: nothing extra to union in

            return WorksetVisibilityRule.Shows(w.IsVisibleByDefault, fromView, fromTemplate);
        }
        catch { return true; }

        // Visible / Hidden / UseGlobalSetting → true / false / null.
        static bool? Answer(Autodesk.Revit.DB.View v, Workset ws) => v.GetWorksetVisibility(ws.Id) switch
        {
            WorksetVisibility.Visible => true,
            WorksetVisibility.Hidden => false,
            _ => null,
        };
    }

    private static List<WorksetId> AllUserWorksets(Document doc)
    {
        try
        {
            return new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .Select(w => w.Id).ToList();
        }
        catch { return new List<WorksetId>(); }
    }

    public override void ConfigureOutput(string destinationFolder, string projectLogName)
    {
        _outputFolder = _spec.ResolveOutputFolder(destinationFolder);
        if (string.IsNullOrWhiteSpace(_outputFolder))
            throw new InvalidOperationException(
                "Export NWC has nowhere to write: set an output folder in the options set, or give " +
                "the run a destination folder.");

        // Fail the RUN, not each model: a PC without the exporter will fail identically for every
        // model, and one clear sentence beats fifty copies of a COM error.
        if (NavisworksLocator.Check(_revitVersion) != NavisworksStatus.Installed)
            throw new InvalidOperationException(
                "Export NWC needs the Revit exporter for Navisworks, which is installed with " +
                "Navisworks itself and was not found for this Revit. Install Navisworks (or its " +
                "Revit exporter add-in) on this PC and run again.");

        Directory.CreateDirectory(_outputFolder);
    }

    /// <summary>
    /// One row per file written. Overridden instead of <see cref="Run"/> because a single model can
    /// export several views.
    /// </summary>
    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(
        UIApplication uiApp, Document doc)
    {
        _note = "";
        string modelName = ModelFileName(doc.Title);

        // Recording which worksets held nothing but links used to happen here, before the export,
        // while this run's worksets were still loaded. Since 1.4.3 the ENGINE does it for every
        // action that opens worksets (BatchEngine.ReopenWithRequiredWorksets → WorksetContentsProbe),
        // so Batch Interop's opens feed this action's skip and vice versa. See WorksetContentsProbe
        // for why one action must not be the sole producer of evidence two actions consume.

        List<View3D> views = _spec.ExportEntireModel
            ? new List<View3D>()
            : FindExportViews(doc, _spec.ViewTitleContains);

        // Nothing matched: fall back rather than fail, and say so.
        if (!_spec.ExportEntireModel && views.Count == 0)
        {
            _note = string.IsNullOrWhiteSpace(_spec.ViewTitleContains)
                ? "no 3D views in this model — exported the whole model instead"
                : $"no 3D view title contains '{_spec.ViewTitleContains}' — exported the whole model instead";
            return new[] { ExportOne(doc, modelName, view: null) };
        }

        if (views.Count == 0)
            return new[] { ExportOne(doc, modelName, view: null) };

        // Several matching views mean several files, so the view name goes into every one of them
        // whatever the options set says — otherwise they all build the same name and only the last
        // export survives (2026-08-15: three views matched, three exports ran, one file remained).
        // A single match keeps the short name the team's existing files use.
        bool forceViewInName = views.Count > 1;

        var rows = new List<IReadOnlyDictionary<string, string>>(views.Count);
        foreach (View3D v in views)
            rows.Add(ExportOne(doc, modelName, v, forceViewInName));

        _note = views.Count == 1
            ? $"exported view '{views[0].Name}'"
            : $"exported {views.Count} views";
        return rows;
    }

    /// <summary>Never called — <see cref="RunDetailRows"/> is overridden. Present because
    /// <see cref="IBatchAction"/> requires it (the interface has no default members: Revit 2024
    /// targets net48).</summary>
    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc) =>
        throw new NotSupportedException(
            "Export NWC writes one row per exported file; see RunDetailRows.");

    /// <summary>
    /// Exports one file and returns its row. <paramref name="view"/> null = whole model.
    ///
    /// <para><b>A view export that writes nothing is retried as a WHOLE-MODEL export</b> (1.3.8.6).
    /// On 2026-08-15 four models each produced no file, and the exporter's own message box said "No
    /// suitable geometry found" — which has exactly two explanations: the model's elements are not
    /// loaded, or the exporter did not use the view it was handed. The retry separates them without
    /// costing another live round: if a whole-model export writes a real file from the same open
    /// document, the elements are plainly there and the view was the problem.</para>
    ///
    /// <para><b>Unless the open was partial</b> (1.3.8.8). Since the worksets are now derived from
    /// the views, a whole-model export from a view-scoped open would write a file that LOOKS like the
    /// whole model and silently is not — which is the exact failure this release arc exists to close.
    /// Refused with the numbers, so the row says which model to re-run and how.</para>
    ///
    /// <para>This is not hypothetical: on 2026-08-15 <c>SITE2A-BLDC-SIGN</c> took the retry path on a
    /// view named <c>Forma PUBLISH_SITE1A-BLDC_SIGNAGE</c> — a SITE1A view left inside a SITE2A model.</para>
    /// </summary>
    private IReadOnlyDictionary<string, string> ExportOne(
        Document doc, string modelName, View3D? view, bool forceViewInName = false)
    {
        try
        {
            return ExportOnce(doc, modelName, view, forceViewInName);
        }
        catch (InvalidOperationException ex) when (view != null)
        {
            (int open, int total) = WorksetCounts(doc);
            if (total > 0 && open < total)
                throw new InvalidOperationException(
                    $"View '{view.Name}' produced no file, and the whole-model retry was refused " +
                    $"because only {open} of {total} worksets are open — it would have written a " +
                    $"partial file that looks complete. This model's worksets were opened from its " +
                    $"export views ({_worksetPlan}); re-run this model with 'Export entire model' " +
                    $"to get every workset. Original failure: {ex.Message}");

            Diagnostics.RunTrace.Mark(
                $"Export NWC | {doc.Title} | view '{view.Name}' produced no file — retrying as whole model");
            IReadOnlyDictionary<string, string> row = ExportOnce(doc, modelName, view: null);
            var retried = new Dictionary<string, string>(row.ToDictionary(p => p.Key, p => p.Value))
            {
                ["Scope"] = "3D view → whole model (retry)",
                ["View Used"] = view.Name,
            };
            _note = $"view '{view.Name}' exported nothing — wrote the whole model instead";
            return retried;
        }
    }

    /// <summary>Open and total USER worksets, or (0, 0) when the document is not workshared or the
    /// count cannot be read — either way, not grounds to refuse a retry.</summary>
    private static (int Open, int Total) WorksetCounts(Document doc)
    {
        try
        {
            if (!doc.IsWorkshared) return (0, 0);
            int open = 0, total = 0;
            foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                total++;
                if (w.IsOpen) open++;
            }
            return (open, total);
        }
        catch { return (0, 0); }
    }

    /// <summary>One export attempt. Throws when Revit writes no file, which is how a view that
    /// yields nothing reaches the retry above.</summary>
    private IReadOnlyDictionary<string, string> ExportOnce(
        Document doc, string modelName, View3D? view, bool forceViewInName = false)
    {
        bool wholeModel = view is null;
        string baseName = UniqueName(
            SafeFileName(_spec.BuildFileName(modelName, wholeModel ? "" : view!.Name, forceViewInName)));
        string target = Path.Combine(_outputFolder, baseName + ".nwc");

        // Nothing to export is something this action can see for itself, and handing it to the
        // exporter costs a modal "No suitable geometry found" that stops the instance until the
        // dialog breaker answers it 45 seconds later. Said plainly here instead.
        if (!HasExportableGeometry(doc, view))
        {
            string what = wholeModel ? "this model" : $"view '{view!.Name}'";
            throw new InvalidOperationException(
                $"Nothing to export: {what} holds no model geometry. " + Contents(doc, view));
        }

        var options = new NavisworksExportOptions
        {
            ExportScope = wholeModel ? NavisworksExportScope.Model : NavisworksExportScope.View,
            ConvertElementProperties = _spec.ConvertElementProperties,
            ExportLinks = _spec.ExportLinks,
            ExportRoomAsAttribute = _spec.ExportRoomAsAttribute,
            FindMissingMaterials = _spec.FindMissingMaterials,
            DivideFileIntoLevels = _spec.DivideFileIntoLevels,
            ExportParts = _spec.ExportParts,
            ExportRoomGeometry = _spec.ExportRoomGeometry,
            ExportElementIds = _spec.ExportElementIds,
            ExportUrls = _spec.ExportUrls,
            ConvertLinkedCADFormats = _spec.ConvertLinkedCADFormats,
            ConvertLights = _spec.ConvertLights,
            FacetingFactor = _spec.FacetingFactor,
            Coordinates = _spec.UseInternalCoordinates
                ? NavisworksCoordinates.Internal
                : NavisworksCoordinates.Shared,
            Parameters = _spec.Parameters switch
            {
                NwcParameterMode.All => NavisworksParameters.All,
                NwcParameterMode.Elements => NavisworksParameters.Elements,
                _ => NavisworksParameters.None,
            },
        };
        if (!wholeModel) options.ViewId = view!.Id;

        // Export takes the folder and a BARE name, not a full path.
        doc.Export(_outputFolder, baseName, options);

        var written = new FileInfo(target);
        if (!written.Exists)
            throw new InvalidOperationException(
                $"Revit reported no error but no file appeared at {target}. " + Contents(doc, view));

        _usedNames.Add(baseName); // written — now the name is taken for the rest of this run

        return new Dictionary<string, string>
        {
            ["Exported File"] = written.Name,
            ["Scope"] = wholeModel ? "Whole model" : "3D view",
            ["View Used"] = wholeModel ? "" : view!.Name,
            ["Output Folder"] = _outputFolder,
            ["Size (KB)"] = (written.Length / 1024).ToString(),
            ["Contents"] = Contents(doc, view),
        };
    }

    /// <summary>
    /// Does the chosen scope — the view's, or the document's for a whole-model export — hold anything
    /// the exporter could turn into geometry?
    ///
    /// <para><b>MODEL-category elements, not every element</b> (1.3.8.8). Counting every element was
    /// the 1.3.8.7 test, and it was not enough: on 2026-08-15 the view
    /// <c>Forma PUBLISH_SITE1A-BLDC_SIGNAGE</c> passed it and still produced the exporter's "No suitable
    /// geometry found" box, costing 45 seconds of the dialog breaker's grace. A view holding only
    /// annotation has elements and no geometry. Model category is the conservative line: being
    /// over-inclusive just hands the export to the exporter as before, while being under-inclusive
    /// would skip a real one.</para>
    ///
    /// <para>Short-circuits on the first hit — the answer is only ever used as a yes/no — and
    /// fail-soft: unreadable means YES, so a diagnostic can never be the reason an export is
    /// skipped.</para>
    /// </summary>
    private static bool HasExportableGeometry(Document doc, View3D? view)
    {
        try
        {
            FilteredElementCollector scope = view is null
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, view.Id);

            return scope.WhereElementIsNotElementType()
                .Any(e => !e.ViewSpecific && e.Category is { CategoryType: CategoryType.Model });
        }
        catch { return true; }
    }

    /// <summary>
    /// A base name no earlier file in this RUN has used — "name", then "name (2)", "name (3)"…
    ///
    /// <para>The naming rule already puts the view name in when several views match, so this is the
    /// backstop for what a rule cannot cover: two views whose names differ only in characters
    /// Windows will not accept, or a set whose prefix/suffix happen to collapse two models onto one
    /// name. <see cref="BatchInteropAction"/> keeps report names unique the same way and for the
    /// same reason — silently overwriting a finished file is the one outcome with no trace.</para>
    /// </summary>
    private string UniqueName(string baseName)
    {
        // Chosen but NOT reserved: a name is taken only once a file has actually been written under
        // it (see ExportOnce). An export that fails must leave its name free, or the whole-model
        // retry of a failed view export would be pushed to "name (2)" for no reason.
        if (!_usedNames.Contains(baseName)) return baseName;
        for (int n = 2; n < 1000; n++)
        {
            string candidate = $"{baseName} ({n})";
            if (!_usedNames.Contains(candidate)) return candidate;
        }
        return baseName;
    }

    /// <summary>
    /// What this Revit could actually see: elements in the exported view, elements loaded in the
    /// document, open worksets, and which view the document has active.
    ///
    /// <para>It exists to settle one question with one line. "No suitable geometry found" is
    /// produced both by a model whose worksets never opened (so nothing is loaded) and by an
    /// exporter that ignored the view it was given (so it looked at the active view, which in a
    /// background-opened model is whatever was saved — often a sheet). Those need opposite fixes,
    /// and a count of elements tells them apart immediately: thousands in the view means the view
    /// was not the exporter's problem; almost none in the DOCUMENT means the worksets are.</para>
    ///
    /// <para>Two collector counts and a workset count per file — cheap next to an export, and
    /// fail-soft: a diagnostic must never be the reason a model fails.</para>
    ///
    /// <para><b>Since 1.3.8.8 it also carries the workset PLAN and the machine's free RAM.</b> The
    /// plan is how under-opening becomes visible in the CSV instead of in a journal: "6 of 45
    /// workset(s), from 3 view(s)" is the sentence that says a view-derived open did its job, and
    /// "all 45 workset(s) — no matching view" is the sentence that says it fell back. Free RAM is
    /// there because on 2026-08-15 an instance died of an access violation with 569 MB left and
    /// nothing recorded how close the machine had been to empty.</para>
    /// </summary>
    private string Contents(Document doc, View3D? view)
    {
        try
        {
            int inDoc = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType().GetElementCount();
            int inView = view is null
                ? -1
                : new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType().GetElementCount();

            (int open, int total) = WorksetCounts(doc);

            string active = "";
            try { active = doc.ActiveView is { } v ? $"{v.Name} ({v.ViewType})" : "none"; }
            catch { active = "unreadable"; }

            string viewPart = inView < 0 ? "" : $"view {inView:N0} elements, ";
            string worksetPart = doc.IsWorkshared ? $"worksets {open}/{total} open, " : "not workshared, ";
            string planPart = string.IsNullOrEmpty(_worksetPlan) ? "" : $"plan: {_worksetPlan}, ";
            string free = Schedule.InstanceBudget.FreeMemoryNote();
            string freePart = free.Length == 0 ? "" : free + ", ";
            return $"{viewPart}document {inDoc:N0} elements, {worksetPart}{planPart}{freePart}" +
                   $"active view: {active}.";
        }
        catch (Exception ex)
        {
            return "(model contents could not be read: " + ex.Message + ")";
        }
    }

    /// <summary>
    /// Every exportable 3D view whose title CONTAINS <paramref name="contains"/>, case-sensitively;
    /// blank returns them all. Ordered by name so a re-run writes the same files in the same order.
    ///
    /// <para>View TEMPLATES are excluded: they share the name space with real views, cannot be
    /// exported, and a template called "NAVISWORKS" sitting alongside the view called "NAVISWORKS"
    /// would turn a working export into an error nobody could read.</para>
    /// </summary>
    private static List<View3D> FindExportViews(Document doc, string contains)
    {
        IEnumerable<View3D> all = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate);

        // Ordinal, not the culture comparer: Bird's panel says "(Case Sensitive)" and a view list
        // is not prose. A culture-sensitive compare would also make the result depend on the PC.
        if (!string.IsNullOrWhiteSpace(contains))
            all = all.Where(v => v.Name.IndexOf(contains, StringComparison.Ordinal) >= 0);

        return all.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The model's name for file-naming purposes. The batch opens cloud models detached, which
    /// appends "_detached" to the title — stripped here so the NWC is named after the model rather
    /// than after how the batch happened to open it.
    /// </summary>
    private static string ModelFileName(string title)
    {
        string name = title;
        const string detached = "_detached";
        if (name.EndsWith(detached, StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - detached.Length);
        if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 4);
        return name;
    }

    /// <summary>A name Windows will accept — view names in particular may hold <c>/</c> or <c>:</c>.</summary>
    private static string SafeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}
