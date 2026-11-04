namespace KhBatchRunner.Actions;

/// <summary>
/// Action display names that code OUTSIDE the Revit-typed action classes has to spell.
///
/// They live here so a dialog, a scheduler or a report writer can name an action without
/// referencing the action itself — several of these files are also compiled into the non-Revit
/// controller project, where the action classes cannot follow. Each action takes its own
/// <c>Name</c>/<c>DisplayName</c> from the constant here, so there is still exactly one spelling.
/// </summary>
public static class ActionNames
{
    // Actions that are NOT in ActionRegistry: each needs a per-run target (a publish folder, cut
    // parameters), so it is offered through its own dialog rather than the generic action list —
    // yet the schedule editor still has to list them.
    public const string InitiateToForma = "Initiate to Forma";
    public const string CutModel = "Cut model";
    public const string ExportNwc = "Export NWC";
    public const string InternalRelink = "Internal Relink";
    public const string CorrectWorkset = "Correct Workset";

    /// <summary>The Forma file-log pull. Not an action at all — it opens no model and never reaches
    /// the batch engine — but it IS a run item, so the scheduler, the chunk launcher and the
    /// controller all have to spell it. <c>Schedule.FileLogStep.DisplayName</c> takes its value
    /// from here.</summary>
    public const string GetFileLog = "Get File Log";

    /// <summary>The Forma Data Connector download — the newest successful run of an extract Forma
    /// itself schedules. Like <see cref="GetFileLog"/> it opens no model and never reaches the
    /// batch engine, and for the same reason it is named here: the scheduler, the chunk launcher
    /// and the controller all have to spell it. <c>Schedule.DataExtractStep.DisplayName</c> takes
    /// its value from here.</summary>
    public const string GetDataExtract = "Get Data Extract";

    // Registry checks that pivot the whole run into a matrix workbook at run end. Named here
    // because the run-end merge that builds that workbook for an auto-split run
    // (Reporting.RunEndMatrix) runs in the SUPERVISING process, which never constructs the action.
    public const string GetPhaseAndWorkset = "Get Phase & Workset";
    public const string GetDesignOption = "Get Design Option";

    /// <summary>
    /// The current name for a name a saved job or selection carries. An action's name IS its
    /// schedule key, so a name an older version saved has to map to today's spelling here —
    /// <see cref="Schedule.RunItem.Name"/> runs every loaded name through this.
    /// </summary>
    public static string Canonical(string name) =>
        name == Settings.LegacyNames.InitiateAction ? InitiateToForma
        : name.StartsWith(LegacyGridDimensionPrefix, StringComparison.Ordinal)
            ? DatumDimensionPrefix + name.Substring(LegacyGridDimensionPrefix.Length)
            : name;

    /// <summary>DRW Helper's Datum Dimension presets are listed as this prefix + the preset name
    /// (<c>Drw.DrwActions</c> takes its constant from here).</summary>
    public const string DatumDimensionPrefix = "Datum Dimension — ";

    /// <summary>What Datum Dimension was called before DRW Helper 0.2.6. Schedules and batch
    /// selections saved then still carry it; <see cref="Canonical"/> maps it.</summary>
    public const string LegacyGridDimensionPrefix = "Grid Dimension — ";
}
