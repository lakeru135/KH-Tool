namespace KhBatchRunner.Scripts;

/// <summary>
/// A named set of Dynamo <c>.dyn</c> scripts to run in a batch. Stored as JSON; a checked
/// set executes via the Dynamo runner (<c>Actions.RunScriptSetAction</c> +
/// <c>DynamoInvoker</c>) in Batch Run / Schedule Run.
/// </summary>
public class ScriptSet
{
    /// <summary>Stable identity that survives a rename. Schedules reference this (not <see cref="Name"/>)
    /// so renaming a set doesn't orphan them. Empty by default (and on sets saved before ids existed);
    /// <see cref="ScriptSetStore.LoadAll"/> back-fills + persists an id on load so a saved set always
    /// has a stable, non-empty id. Do not initialize to a fresh Guid here — deserializing old JSON
    /// that lacks the field would then mint a NEW id every load and defeat the purpose.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Absolute paths to <c>.dyn</c> files, run in order.</summary>
    public List<string> ScriptPaths { get; set; } = new();
}
