namespace KhBatchRunner.Logging;

/// <summary>
/// Append-only run logger. Actions call <see cref="Log"/> with per-model result rows;
/// the concrete logger stamps Datetime + Model Name and persists them.
/// Follows the team [[dynamo-batch-scripting]] CSV contract.
/// </summary>
public interface IRunLogger
{
    /// <summary>The model currently being processed — used to stamp the "Model Name" column.</summary>
    string CurrentModel { get; set; }

    /// <summary>Append one row. <paramref name="fields"/> are the action-specific columns.</summary>
    void Log(IReadOnlyDictionary<string, string> fields);
}
