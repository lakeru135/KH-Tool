namespace KhBatchRunner.Batch;

/// <summary>
/// Which model the engine is working on right now, as a stable
/// <see cref="Presets.ModelKey"/> string.
///
/// <para>Ambient for the same reason <see cref="Schedule.ChunkContext"/> and
/// <see cref="Diagnostics.RunTrace"/> are: an action is handed a
/// <see cref="Autodesk.Revit.DB.Document"/> and nothing else, and a document opened DETACHED from a
/// cloud central no longer knows which cloud model it came from — the identity lives in the
/// <see cref="Presets.CloudModel"/> the engine holds. Threading it through
/// <see cref="Actions.IBatchAction"/> would change a signature every action implements, to serve the
/// one action that needs it.</para>
///
/// <para>Safe as process-wide state because the engine works one model at a time: cloud opens are
/// serialised through the Idling driver, and since 1.3.8.5 each model is closed before the next is
/// opened (<c>ModelDocumentCursor</c>). The value is set before the open and stays valid for that
/// model's whole open/run/close cycle.</para>
/// </summary>
internal static class ModelContext
{
    /// <summary>The current model's key, or "" outside a model (before the first open, after the
    /// last close, or on a path that never set it). Consumers must treat "" as "don't know" and fall
    /// back to whatever they would do without a record.</summary>
    public static string CurrentKey { get; private set; } = "";

    public static void Set(Presets.CloudModel model)
    {
        try { CurrentKey = Presets.ModelKey.For(model); }
        catch { CurrentKey = ""; }
    }

    public static void Clear() => CurrentKey = "";
}
