namespace KhBatchRunner.Actions;

/// <summary>
/// Whether an action only reads a model (Multi Model Check) or mutates it
/// (Multi Model Action). Drives <see cref="Batch.BatchEngine"/>'s persistence rule — a
/// read never saves/syncs, a write does — instead of every action having to know that
/// rule itself.
///
/// Declared in its own file rather than beside <see cref="IBatchAction"/>: the dialogs that let a
/// user choose an action, and the job records that store one, need this enum but must not drag in
/// the Revit-typed interface. Same namespace as before.
/// </summary>
public enum ActionCategory { Check, Action }
