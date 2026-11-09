using System.Linq;
using System.Text.Json.Serialization;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Schedule;

public enum ScheduleRecurrence { Once, Daily, Weekly }

/// <summary>Whether a <see cref="RunItem"/> is a built-in action (incl. the synthetic publish
/// passes Initiate/Cut model) or a Dynamo script set.</summary>
public enum RunItemKind { Action, ScriptSet }

/// <summary>
/// One entry in a job's ordered run list (<see cref="ScheduledJob.RunItems"/>) — a single action
/// or script set. Storing an ordered list (rather than the old separate ActionNames/ScriptSetNames
/// lists) is what lets an action → script → action arrangement survive save/load and run in that
/// exact order.
/// </summary>
public sealed class RunItem
{
    private string _name = "";

    /// <summary>The action or script-set name. A name an older version saved is mapped to its
    /// current spelling as it is read (see <see cref="Actions.ActionNames.Canonical"/>).</summary>
    public string Name { get => _name; set => _name = Actions.ActionNames.Canonical(value ?? ""); }
    public RunItemKind Kind { get; set; } = RunItemKind.Action;

    /// <summary>Stable id of the referenced <see cref="Scripts.ScriptSet"/> (only when
    /// <see cref="Kind"/> is <see cref="RunItemKind.ScriptSet"/>) so renaming the set doesn't orphan
    /// this run item — the runner and the editor resolve by id first, name second. Null for actions
    /// and for jobs saved before ids existed (name fallback keeps them working until re-saved).</summary>
    public string? RefId { get; set; }

    public RunItem() { }
    public RunItem(string name, RunItemKind kind) { Name = name; Kind = kind; }
    public RunItem(string name, RunItemKind kind, string? refId) { Name = name; Kind = kind; RefId = refId; }
}

/// <summary>
/// A saved scheduled batch: run one or more <see cref="ActionNames"/>/<see cref="ScriptSetNames"/>
/// over one or more model sets (<see cref="ModelSetNames"/>) plus any ad-hoc models
/// (<see cref="AdHocModels"/>), at <see cref="Time"/> on the given recurrence. A Windows Task
/// Scheduler task launches Revit, which detects the job id (env var) and runs it headless.
///
/// <see cref="SchemaVersion"/> 2 replaced the original single ModelSetName/ActionName/
/// ScriptSetName/OpenMode/LogPath fields with lists; <see cref="SchemaVersion"/> 3 replaced the
/// separate ActionNames/ScriptSetNames lists with one ordered <see cref="RunItems"/> list so an
/// interleaved action → script → action order survives save/load/run (the old shape always ran
/// all actions first, then all script sets). <see cref="ScheduleStore"/> migrates v1/v2 files on
/// load. Open mode is no longer stored: it's fixed by each action's category (Check =
/// detached/read-only, Action = attached + sync, all worksets closed). Only the post-run
/// <see cref="Completion"/> disposition is a stored choice.
/// </summary>
public class ScheduledJob
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Product edition that owns this scheduled job (<c>Full</c> or <c>DRW</c>). Revit can load
    /// both editions in one process; without this discriminator both applications see the same
    /// <c>KHBATCH_JOB</c> value and can execute one scheduled run twice. Blank is retained for
    /// pre-existing jobs and is protected by the process-wide claim in <see cref="ScheduledRunner"/>.
    /// </summary>
    public string ScheduleOwner { get; set; } = "";

    /// <summary>
    /// The owner a saved job belongs to TODAY. Since 2.0.0 there are two schedule owners — "Full"
    /// and "DRW". A 1.x job saved by the retired lite edition carries a third value; its actions
    /// (Cut model, Internal Relink, Correct Workset) are all in Full now, so every owner that is not
    /// DRW is read as Full rather than left as a job nobody can run.
    /// </summary>
    public static string NormalizeOwner(string? owner) =>
        string.IsNullOrWhiteSpace(owner) ? ""
        : string.Equals(owner.Trim(), "DRW", StringComparison.OrdinalIgnoreCase) ? "DRW"
        : "Full";

    /// <summary>User-supplied label shown in the schedule table (distinct from the action/model names).</summary>
    public string Name { get; set; } = "";

    public List<string> ModelSetNames { get; set; } = new();

    /// <summary>Stable ids of the referenced model sets, parallel to <see cref="ModelSetNames"/>
    /// (kept for display + fallback). Resolving by id means a renamed set still runs. Empty on jobs
    /// saved before ids existed — the resolver then falls back to name until the job is re-saved.
    /// Nullable/new member, so old JSON loads unchanged (no schema bump).</summary>
    public List<string> ModelSetIds { get; set; } = new();

    /// <summary>Models added directly to this job, not via a saved model set.</summary>
    public List<CloudModel> AdHocModels { get; set; } = new();

    /// <summary>
    /// The actions and script sets to run, IN ORDER — the ordered source of truth (schema v3).
    /// Actions and script sets stay interleaved exactly as arranged in the window, and the runner
    /// executes them in this order. The synthetic publish passes (Initiate to Forma / Cut model) and
    /// Batch Interop appear here as <see cref="RunItemKind.Action"/> items whose config is carried
    /// by <see cref="InitiatePublish"/> / <see cref="CutModelPublish"/> / <see cref="InteropChecksetPath"/>.
    /// </summary>
    public List<RunItem> RunItems { get; set; } = new();

    /// <summary>Built-in action names, derived from <see cref="RunItems"/> (convenience for readers;
    /// not serialized — <see cref="RunItems"/> is the stored, ordered form).</summary>
    [JsonIgnore]
    public List<string> ActionNames =>
        RunItems.Where(r => r.Kind == RunItemKind.Action).Select(r => r.Name).ToList();

    /// <summary>Script-set names, derived from <see cref="RunItems"/> (see <see cref="ActionNames"/>).</summary>
    [JsonIgnore]
    public List<string> ScriptSetNames =>
        RunItems.Where(r => r.Kind == RunItemKind.ScriptSet).Select(r => r.Name).ToList();

    /// <summary>
    /// Publish config for the "Initiate to Forma" pass, snapshotted from a saved Initiate input
    /// set when that item is checked in Batch Run / Schedule Run. Null when the job doesn't
    /// publish. The token <see cref="Actions.InitiateToFormaAction.DisplayName"/> also appears in
    /// <see cref="ActionNames"/> to mark run order; the runner builds the action from this snapshot
    /// so a headless run is self-contained (it does not re-read the input-set store later).
    /// Nullable + a new member, so old job JSON loads unchanged (no schema bump).
    /// </summary>
    public Batch.InitiateInputSet? InitiatePublish { get; set; }

    /// <summary>
    /// Publish + site config for the "Cut model" pass (Forma Batch Action), snapshotted from a saved
    /// Cut model input set when that item is checked in Batch Run / Schedule Run. Null when the job
    /// doesn't cut. The token <see cref="Actions.CutModelAction.DisplayName"/> also appears in
    /// <see cref="RunItems"/> to mark run order; the runner builds the action from this snapshot so
    /// a headless run is self-contained. Mirrors <see cref="InitiatePublish"/>.
    /// </summary>
    public Batch.CutModelInputSet? CutModelPublish { get; set; }

    /// <summary>
    /// Everything the "Get File Log" step reads — the Forma folders AND the options — snapshotted
    /// from a saved File Log set when that item is checked. Null when the job pulls no file log.
    /// Mirrors <see cref="ExportNwcSpec"/>.
    ///
    /// <para><b>The folders live here since 1.4.5</b>, where they used to be inferred from whichever
    /// of the job's model sets happened to be folder-backed. That inference meant a job could
    /// request a file log and silently produce nothing, and a job whose model sets were plain model
    /// lists could not pull one at all. A job that names no set is now reported as misconfigured
    /// rather than doing nothing quietly — see <c>FileLogStep.Run</c>.</para>
    /// </summary>
    public Batch.FileLogInputSet? FileLogInputs { get; set; }

    /// <summary>
    /// Everything the "Get Data Extract" step needs — which Forma account and extract request, and
    /// how to save what it downloads — snapshotted from a saved Data Extract set when that item is
    /// checked. Null when the job downloads no extract. Nullable and new, so old job JSON loads
    /// unchanged (no schema bump), exactly like <see cref="FileLogInputs"/>.
    /// </summary>
    public Batch.DataExtractInputSet? DataExtractInputs { get; set; }

    /// <summary>
    /// The 1.4.4 file-log options, kept only so a job saved by that build still deserializes rather
    /// than tripping the store's malformed-file path. Nothing reads it: 1.4.4 never chose folders,
    /// so such a job has to be pointed at a set once regardless.
    /// </summary>
    public Forma.FileLogSpec? FileLogSpec { get; set; }

    /// <summary>
    /// Options for the "Export NWC" pass, snapshotted from a saved Export NWC option set when that
    /// item is checked in Batch Run / Schedule Run. Null when the job exports nothing. The token
    /// <see cref="Actions.ExportNwcAction.DisplayName"/> also appears in <see cref="RunItems"/> to
    /// mark run order; the runner builds the action from this snapshot so a headless run is
    /// self-contained. Mirrors <see cref="InitiatePublish"/>, including being nullable and new so
    /// old job JSON loads unchanged (no schema bump).
    /// </summary>
    public Batch.ExportNwcInputSet? ExportNwcSpec { get; set; }

    /// <summary>
    /// Options for the "Export IFC + Data" pass, snapshotted from a saved option set when that item
    /// is checked. Null when the job exports no IFC. Mirrors <see cref="ExportNwcSpec"/> in every
    /// respect, including being nullable and new so old job JSON loads unchanged.
    /// </summary>
    public Batch.ExportIfcInputSet? ExportIfcSpec { get; set; }

    /// <summary>
    /// Inputs for the "Internal Relink" pass (Forma Batch Action), snapshotted when that item is checked.
    /// Null when the job doesn't relink. The token <see cref="Actions.RelinkLinksAction.DisplayName"/>
    /// also appears in <see cref="RunItems"/>; the runner scans the spec's target folders and builds
    /// the action at pass-build time. The spec's own host fields are ignored — a scheduled run's
    /// hosts are the job's model sets (like Initiate's ModelSetName). Mirrors <see cref="InitiatePublish"/>.
    /// </summary>
    public Batch.RelinkInputSet? RelinkSpec { get; set; }

    /// <summary>
    /// Inputs for the "Correct Workset" pass (Model Batch Action), snapshotted when that item is checked.
    /// Null when the job doesn't correct worksets. A scheduled (unattended) Fix run AUTO-APPROVES all
    /// keyword renames (no dialog); interactive "Run now" still prompts. Mirrors <see cref="InitiatePublish"/>.
    /// </summary>
    public Batch.CorrectWorksetInputSet? CorrectWorksetSpec { get; set; }

    /// <summary>
    /// Model Checker checkset (.xml path or hosted URL) for the "Batch Interop" pass,
    /// snapshotted when that item is checked in Batch Run / Schedule Run (or set directly by
    /// <see cref="UI.ScheduleInteropWindow"/>). Null when the job doesn't run Batch Interop. The
    /// token <see cref="Actions.BatchInteropAction.DisplayName"/> also appears in
    /// <see cref="ActionNames"/> to mark run order. Nullable + a new member, so old job JSON
    /// loads unchanged (no schema bump).
    /// </summary>
    public string? InteropChecksetPath { get; set; }

    /// <summary>
    /// Whether that checkset asks about the link instances in the host model, snapshotted from the
    /// saved <see cref="Interop.ChecksetSet"/> alongside the path.
    ///
    /// <para><b>Default false, and that is the new behaviour, not the old one.</b> False lets the
    /// run leave worksets proven to hold nothing but links shut — see
    /// <see cref="Actions.BatchInteropAction.Worksets"/>. Jobs saved before 1.4.3 have no such
    /// property in their JSON and so deserialise to false, which is deliberate: the skip is safe for
    /// every checkset that does not examine links, and a checkset that does is the rarer case worth
    /// making someone tick a box for. The cost of being wrong is a check that passes over links it
    /// could not see; the cost of defaulting the other way is that nobody ever gets the saving.</para>
    /// </summary>
    public bool InteropChecksetExaminesLinks { get; set; }

    /// <summary>
    /// What to do when the run finishes. Honoured on interactive "Run now"; a headless scheduled
    /// run always closes Revit (it was launched only for the job), so the default is
    /// <see cref="BatchCompletion.CloseRevit"/>.
    /// </summary>
    public BatchCompletion Completion { get; set; } = BatchCompletion.CloseRevit;

    /// <summary>
    /// Whether write actions/scripts persist their changes (<see cref="SyncMode.Sync"/>, default)
    /// or run dry — execute and log, discard changes, relinquish everything. Missing in pre-existing
    /// job JSON, so the default keeps old jobs syncing as they always did.
    /// </summary>
    public SyncMode Sync { get; set; } = SyncMode.Sync;

    /// <summary>Folder that receives the `Log\` tracking file and the detail CSV.</summary>
    public string DestinationFolder { get; set; } = "";

    /// <summary>Used to name both log files: &lt;ProjectLog&gt;_&lt;Action&gt;.csv.</summary>
    public string ProjectLogName { get; set; } = "";

    /// <summary>Local start time, "HH:mm" (24h).</summary>
    public string Time { get; set; } = "18:00";
    public ScheduleRecurrence Recurrence { get; set; } = ScheduleRecurrence.Once;

    /// <summary>The calendar date a <see cref="ScheduleRecurrence.Once"/> job fires on (date part only;
    /// the time comes from <see cref="Time"/>). Null means "the day it was saved" (legacy behaviour) —
    /// the Windows task then omits <c>/SD</c> and schtasks defaults to today. Set explicitly so
    /// re-saving a Once job on a later day no longer re-anchors it to that day. Ignored for
    /// Daily/Weekly. Nullable/new member, so old JSON loads unchanged (no schema bump).</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>Days of the week the job repeats on, when <see cref="Recurrence"/> is Weekly.</summary>
    public List<DayOfWeek> WeeklyDays { get; set; } = new();

    /// <summary>Set after every run (headless or "Run now") — shown in the schedule table.</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>
    /// Whether this schedule is armed. False pauses it: the Windows task stays registered with
    /// <c>Settings/Enabled</c> false, so the history, the definition and the next-run arithmetic all
    /// survive, and ticking the box again re-arms it.
    ///
    /// <para><b>Deliberately defaults to true, and is a plain auto-property.</b> A missing member in
    /// old job JSON takes the initializer, so every schedule saved before 1.4.7 loads armed — the
    /// same "nullable/new member, no schema bump" mechanism <see cref="Sync"/> and
    /// <see cref="Completion"/> already rely on. It is not expression-bodied: a get-only property
    /// (see <see cref="TaskName"/>) serializes but never reads back, which for a flag would mean a
    /// paused schedule quietly arming itself on the next load.</para>
    ///
    /// <para>It pauses the SCHEDULE, not the job. "Run now" goes through
    /// <see cref="ScheduledRunner.TryRunInteractive"/> and is unaffected — a paused schedule can
    /// still be run by hand.</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Auto-split group size: when &gt; 0 and the resolved model count exceeds it, the run is split
    /// into groups of this many models, each launched in its OWN fresh Revit instance running
    /// simultaneously (the orchestrator supervises them and quits when the run is accounted for).
    ///
    /// <list type="bullet">
    /// <item><b>0</b> (default) — no split; run every model in one process. Missing in old JSON, so
    /// the default keeps existing jobs single-process. Chunk jobs always carry 0 so a child never
    /// re-splits.</item>
    /// <item><b><see cref="GroupSizeAuto"/></b> — split, with the size chosen at RUN time from the
    /// resolved model count and the machine. This is what the dialogs now write: the size cannot be
    /// decided when the job is saved, because a folder-backed set's model count is not known until
    /// it is re-scanned, and the machine that runs it may not be the machine that saved it.</item>
    /// <item><b>&gt; 0</b> — an explicit size, honoured as-is. Jobs saved before the group-size box
    /// was removed still carry one.</item>
    /// </list>
    /// </summary>
    public int GroupSize { get; set; }

    /// <summary>Sentinel for <see cref="GroupSize"/>: split this run, but let
    /// <see cref="InstanceBudget.AutoGroupSize"/> decide the shape when it actually runs.</summary>
    public const int GroupSizeAuto = -1;

    /// <summary>
    /// 1-based position of this chunk among its siblings ("group 3 of 8"), or <b>0</b> for a job
    /// that is not an auto-split chunk. Set by <see cref="ChunkLauncher"/> when it builds the chunk
    /// and serialized into the chunk file, so the child process can name itself the same way the
    /// parent's launch notes already do.
    ///
    /// Exists because a chunk's <see cref="Id"/> is a fresh GUID: unique, but useless for telling a
    /// person which group they are looking at, and useless for naming an output folder. Actions that
    /// write per-run FILES need it — see <see cref="Interop.InteropReportPaths"/>, where siblings
    /// launched in the same second would otherwise share one report folder and overwrite each other.
    /// </summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// Seconds this instance waits before its FIRST cloud model open. Set per group by
    /// <see cref="ChunkLauncher"/> so simultaneously-launched instances do not all make their cold
    /// first open at the same moment — the point at which Revit is 22.7% likely to die (see
    /// ChunkLauncher.FirstOpenStaggerSeconds). 0 for an ordinary job, and for group 1.
    /// </summary>
    public int FirstOpenDelaySeconds { get; set; }

    /// <summary>
    /// When set, every <see cref="BatchEngine"/> pass of this run stamps its per-run tracking file
    /// with THIS timestamp instead of "now". The auto-split orchestrator gives all sibling chunk
    /// jobs one shared value so their tracking rows land in a SINGLE unified
    /// <c>Log\&lt;stamp&gt;_&lt;project&gt;_&lt;run&gt;.csv</c> (the loggers serialize the concurrent appends —
    /// see <see cref="Logging.LogFileLock"/>). Null for an ordinary run (each pass uses "now").
    /// </summary>
    public DateTime? LogRunStamp { get; set; }

    /// <summary>
    /// Runtime-only flag (never serialized): true when this job was loaded from
    /// <see cref="ChunkJobStore"/> as an ephemeral auto-split chunk. Its run must NOT stamp/save
    /// back to the user's schedule store — it deletes its own chunk file when done instead.
    /// </summary>
    [JsonIgnore]
    public bool IsEphemeralChunk { get; set; }

    /// <summary>Windows Task Scheduler task name for this job.</summary>
    public string TaskName => "KhBatchRunner_" + Id;

    /// <summary>Returns a copy with a fresh Id/TaskName so it can be saved as an independent job.</summary>
    public ScheduledJob Duplicate() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ScheduleOwner = ScheduleOwner,
        Name = Name + " (copy)",
        ModelSetNames = new List<string>(ModelSetNames),
        ModelSetIds = new List<string>(ModelSetIds),
        AdHocModels = new List<CloudModel>(AdHocModels),
        RunItems = RunItems.Select(r => new RunItem(r.Name, r.Kind, r.RefId)).ToList(),
        InitiatePublish = InitiatePublish?.Clone(),
        CutModelPublish = CutModelPublish?.Clone(),
        // Not optional: a split run's children are built with Duplicate(), and a child that arrives
        // without its options snapshot cannot export anything and fails the whole group.
        ExportNwcSpec = ExportNwcSpec?.Clone(),
        ExportIfcSpec = ExportIfcSpec?.Clone(),
        // Cloned for the same reason, though a chunk child never runs the step (ChunkLauncher
        // strips it): the parent's own list must not be aliased into a job it hands to a child.
        FileLogInputs = FileLogInputs?.Clone(),
        DataExtractInputs = DataExtractInputs?.Clone(),
        RelinkSpec = RelinkSpec?.Clone(),
        CorrectWorksetSpec = CorrectWorksetSpec?.Clone(),
        InteropChecksetPath = InteropChecksetPath,
        InteropChecksetExaminesLinks = InteropChecksetExaminesLinks,
        Completion = Completion,
        Sync = Sync,
        DestinationFolder = DestinationFolder,
        ProjectLogName = ProjectLogName,
        Time = Time,
        Recurrence = Recurrence,
        StartDate = StartDate,
        WeeklyDays = new List<DayOfWeek>(WeeklyDays),
        GroupSize = GroupSize,
        // A copy of a paused schedule is paused. Anything omitted here is dropped on Duplicate AND
        // on every auto-split chunk child, which is why the harness asserts this one round-trips
        // false rather than trusting the initializer.
        Enabled = Enabled,
        LastRunAt = null
    };
}
