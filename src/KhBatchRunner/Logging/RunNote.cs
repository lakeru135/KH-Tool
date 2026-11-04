namespace KhBatchRunner.Logging;

/// <summary>
/// Writes a single non-model row into a tracking CSV — a run-level note rather than a model's
/// outcome. Used for the things a reader of the log otherwise has no way to know:
/// <list type="bullet">
/// <item>which groups an auto-split run launched, and whether each one reported back;</item>
/// <item>a run's succeeded/failed summary when the disposition is "Close Revit when complete" and
/// there is no dialog left to show it in;</item>
/// <item>a stall the watchdog spotted (see <see cref="Diagnostics.RunTrace"/>).</item>
/// </list>
/// Rows go through <see cref="TrackingCsvLogger"/>, so they share the same columns and the same
/// cross-process lock as the model rows around them — which is what lets the parent instance and
/// its child group instances all write one unified file.
/// </summary>
internal static class RunNote
{
    /// <summary>Appends one row. Fail-soft in every direction: a note is never worth failing a run over.</summary>
    public static void Write(string trackingLogPath, string task, string model, string status, string message)
    {
        if (string.IsNullOrWhiteSpace(trackingLogPath)) return;
        try
        {
            var logger = new TrackingCsvLogger(trackingLogPath, task) { CurrentModel = model };
            logger.Log(new Dictionary<string, string> { ["Status"] = status, ["Message"] = message });
        }
        catch { /* the run matters more than the note */ }
    }
}
