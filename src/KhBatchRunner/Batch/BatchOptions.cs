namespace KhBatchRunner.Batch;

// The two per-run options a job CARRIES. They live here rather than in BatchEngine.cs because they
// are pure configuration — chosen in a dialog, serialized into a ScheduledJob, read back by
// whatever executes it — while BatchEngine.cs is the Revit-document machinery that consumes them.
// Splitting them out lets a non-Revit host (the standalone controller) compose and store a job
// without dragging in the engine, which cannot exist outside Revit. Same namespace as before, so
// nothing that already uses these types changes.

/// <summary>
/// Whether a write Action's changes are persisted at the end of each model. The per-run
/// choice on Batch Run / Schedule Run / the single Action dialog; read-only Checks never
/// persist regardless. <see cref="NoSync"/> is a dry run: the action executes and logs
/// everything, but the model closes with its changes discarded and all worksharing
/// ownership relinquished — centrals and cloud models are untouched.
/// </summary>
public enum SyncMode
{
    /// <summary>Persist a write Action's changes: sync+relinquish when workshared, else save (default).</summary>
    Sync,

    /// <summary>Dry run — execute and log, but discard changes and relinquish everything.</summary>
    NoSync
}

/// <summary>
/// What to do with the opened models once a whole batch run finishes. The per-model open
/// mode is no longer a user choice — it's fixed by the action's category (a read-only Check
/// opens detached; a write Action stays attached and syncs, unless the run's
/// <see cref="SyncMode"/> says dry-run) and every model opens with all worksets closed.
/// This enum only controls the end-of-run disposition.
/// </summary>
public enum BatchCompletion
{
    /// <summary>Close each model as it's processed (default) — keeps memory bounded.</summary>
    CloseDocuments,

    /// <summary>Leave the processed models open (loaded in the background) to inspect afterward.</summary>
    LeaveOpen,

    /// <summary>Close Revit once the run finishes.</summary>
    CloseRevit
}
