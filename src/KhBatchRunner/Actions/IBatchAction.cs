using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

// ActionCategory moved to Actions\ActionCategory.cs — dialogs and job configuration need it
// without referencing this file, which is Revit-typed by definition.

/// <summary>
/// A unit of work the batch engine runs against each opened model.
/// Built-in C# actions plus the Dynamo runner (<see cref="RunScriptSetAction"/>).
/// Implementations must NOT open/close/sync/save documents — the engine owns that
/// lifecycle (including persistence, driven by <see cref="Category"/>/<see cref="ModifiesModel"/>),
/// and the engine (not the action) writes the log row, stamping Status/Message so every
/// model yields exactly one aligned row. Actions own their own transaction(s) only.
/// Derive from <see cref="BatchActionBase"/> rather than implementing this directly — it
/// carries the shared defaults. (Kept as a plain interface with no default members: the
/// Revit 2024 build targets .NET Framework 4.8, whose runtime can't load default interface
/// implementations.)
/// </summary>
public interface IBatchAction
{
    /// <summary>Display name, also used to name the CSV log (e.g. "Get Phase &amp; Workset").</summary>
    string Name { get; }

    /// <summary>Read (Multi Model Check) or write (Multi Model Action).</summary>
    ActionCategory Category { get; }

    /// <summary>Convenience the engine reads instead of comparing <see cref="Category"/> everywhere.</summary>
    bool ModifiesModel { get; }

    /// <summary>
    /// The data columns this action contributes to each log row (besides the engine's
    /// Status/Message). Declared up front so the CSV columns stay stable even when the
    /// first model fails before the action runs.
    /// </summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>
    /// Run against one already-open document and return this model's data columns.
    /// Throw to have the engine record an ERROR row with the message.
    /// </summary>
    IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc);

    /// <summary>
    /// Detail-log rows for this model — one row per data item (e.g. one row per phase)
    /// instead of a single joined-cell summary. Override when an action has list-shaped
    /// data that should expand into multiple rows.
    /// </summary>
    IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc);

    /// <summary>
    /// Called once by <see cref="Batch.BatchEngine.BeginRun"/>, before any model opens, so an
    /// action that needs a sidecar file (e.g. a master-index CSV) knows where to put it.
    /// </summary>
    void ConfigureOutput(string destinationFolder, string projectLogName);

    /// <summary>
    /// The run's shared timestamp, set by <see cref="Batch.BatchEngine.BeginRun"/> before
    /// <see cref="ConfigureOutput"/>. Null outside a batch (the active-model runner).
    ///
    /// <para>Only the matrix actions read it, and only to name their workbook. Without it
    /// <c>MatrixPaths.OutputPath</c> falls back to the second the WRITE happened, so the same run
    /// produced one filename from the in-process writer and a different one from the run-end merge
    /// — two names for one run, kept apart only by which of them happened to be reachable.</para>
    /// </summary>
    DateTime? RunStamp { get; set; }

    /// <summary>
    /// Called once by <see cref="Batch.BatchEngine.FinalizeRun"/> after the LAST model of a run.
    /// An action that produces a run-level artifact across all the run's models — e.g. the
    /// Phase/Workset/Design-Option matrix workbook — writes it here. Default is a no-op.
    /// </summary>
    void OnRunComplete();

    /// <summary>
    /// True when the action can only run against the ACTIVE document (a real window), not a
    /// background-opened one. The Dynamo script runner needs this — DynamoRevit binds a graph
    /// to <c>UIApplication.ActiveUIDocument</c>, so a background document would silently run
    /// the graph against the wrong model (spike-verified). The modal runner opens such a
    /// pass's models activated even when the disposition closes files as it goes.
    /// </summary>
    bool NeedsActiveDocument { get; }

    /// <summary>
    /// True when this action's models must not be opened while a SIBLING instance of the same run
    /// is opening one. Default false; see <see cref="Schedule.CloudOpenGate"/> for the measurement
    /// and for why it is not simply switched on everywhere.
    ///
    /// This says nothing about the action itself — it is about how long its instances live. Batch
    /// Interop holds each model open for a whole checkset run, which is what makes two Revits
    /// likely to enter <c>DocumentOpening</c> together, where Revit's own Steel Connections
    /// extension contends with itself over a machine-wide database and an abandoned mutex.
    /// </summary>
    bool SerialisesCloudOpens { get; }

    /// <summary>
    /// A one-line summary of the <em>most recently processed</em> model, appended to that
    /// model's Message cell in the per-model tracking log (the "general log"). Read by the
    /// engine immediately after <see cref="RunDetailRows"/>, while processing is strictly one
    /// model per Idling tick, so a plain field set during the run is safe. Empty for actions
    /// whose per-element detail rows already say everything (the default); an action with a
    /// roll-up worth seeing at a glance — e.g. Isolate Warning Elements' new/updated/removed
    /// view counts — overrides it. Not read on the ERROR path (the exception message wins there).
    /// </summary>
    string PerModelNote { get; }

    /// <summary>
    /// What the engine's closed-worksets open means for THIS action — see
    /// <see cref="WorksetPosture"/> for the whole argument. <b>There is no default</b>: it is
    /// abstract on <see cref="BatchActionBase"/>, so a new action does not compile until its author
    /// has decided. <see cref="OpensAllWorksets"/> and <see cref="PlansWorksetOpen"/> are derived
    /// from it and sealed, so the flags and the declaration cannot drift apart.
    /// </summary>
    WorksetPosture Worksets { get; }

    /// <summary>
    /// One line saying WHAT this action collects and why <see cref="Worksets"/> is the right answer
    /// for it — the sentence a reader needs to check the claim without re-deriving it.
    ///
    /// <para>For <see cref="WorksetPosture.PartialByDesign"/> it must also record <b>who accepted
    /// the partial answer and when</b>. That is the difference between a decision and an oversight,
    /// and the four actions audited on 2026-08-16 were indistinguishable from the second until
    /// someone read their collectors.</para>
    /// </summary>
    string WorksetPostureReason { get; }

    /// <summary>
    /// True when the action reads MODEL ELEMENTS and therefore needs every user workset open — the
    /// engine's default open closes them all, and an element in a closed workset is not loaded at
    /// all (a <c>FilteredElementCollector</c> never sees it, and neither does an exporter). For such
    /// an action a closed-worksets open is not a cheap open, it is a <b>wrong answer with no
    /// error</b>: Export NWC wrote empty files and Batch Interop checked a partial model, both
    /// reporting success.
    ///
    /// <para>Distinct from <see cref="PlansWorksetOpen"/>, and cheaper: this is decided before any
    /// model opens, so the engine simply opens with
    /// <see cref="WorksetConfigurationOption.OpenAllWorksets"/> the first time and costs no extra
    /// open. <see cref="PlansWorksetOpen"/> has to inspect the document to learn WHICH worksets it
    /// needs, so it always pays for a second open (and a probe open on the activated path).</para>
    ///
    /// <para>Not free: every workset has to come down from the cloud, so opens are slower. Only for
    /// actions whose answer is wrong without it.</para>
    ///
    /// <para>DERIVED from <see cref="Worksets"/> and sealed on <see cref="BatchActionBase"/> — set
    /// <see cref="WorksetPosture.OpensAll"/>, not this.</para>
    /// </summary>
    bool OpensAllWorksets { get; }

    // THERE IS NO WAY TO SKIP LINK LOADING ON A NORMAL OPEN. Recorded here, next to the flag that
    // causes the cost, so the search for a lever is not repeated: with OpensAllWorksets on, every
    // link sits in an open workset, so Revit downloads and opens each linked cloud model too —
    // MEASURED 2026-08-14 on an 8-model Export NWC run, first opens of 30 min and 13 min against
    // 20–40 s for the same models with the worksets closed.
    //
    // RevitAPI.xml documents OpenOptions.DoNotLoadLinks ("since 2013"). It is NOT IN THE ASSEMBLY:
    // the 2024 and 2026 compilers both reject it (CS1061) — stale documentation, verified against
    // both installed APIs. The only managed API taking a doNotLoadLinks flag is
    // Application.OpenCloudCentralModel(ModelPath, bool), a different open path with no OpenOptions
    // at all, and so no way to ask for open worksets — the one thing the export must have.
    //
    // The cost is therefore paid, and mitigated instead: each model is closed before the next opens
    // (ExportNwcAction.NeedsActiveDocument), and a run can be split across Revit instances.

    /// <summary>
    /// True when the action may need specific worksets OPEN to do its work (the engine opens
    /// every model with all worksets closed for speed). When true, the engine calls
    /// <see cref="SelectWorksetsToOpen"/> after the first (fast, closed-worksets) open and, if any
    /// returned workset is closed, CLOSES the document and REOPENS it with exactly those worksets
    /// open — the only way to change open worksets, since the Revit API can only configure them at
    /// open time, never on an already-open document. False costs nothing.
    ///
    /// <para>DERIVED from <see cref="Worksets"/> and sealed on <see cref="BatchActionBase"/> — set
    /// <see cref="WorksetPosture.PlansOwn"/>, not this. An action whose posture varies with its own
    /// mode returns the posture conditionally (Correct Workset is <see cref="WorksetPosture.PlansOwn"/>
    /// only when fixing), which is why it is a computed property rather than a constant.</para>
    /// </summary>
    bool PlansWorksetOpen { get; }

    /// <summary>
    /// The worksets that must be OPEN in this document for the action to work — e.g. Internal
    /// Relink returns the worksets holding the link types/instances it will re-point, because
    /// <c>RevitLinkType.LoadFrom</c> throws "The element ... is in a closed workset" otherwise.
    /// Called only when <see cref="PlansWorksetOpen"/> is true, on a document opened with all
    /// worksets closed (elements are still collectible then — closed only blocks EDITS). Return
    /// null/empty when this particular model needs nothing (no reopen happens).
    /// </summary>
    ICollection<WorksetId>? SelectWorksetsToOpen(Document doc);
}
