namespace KhBatchRunner.Actions;

/// <summary>
/// The workset postures of the actions the Schedule window offers OUTSIDE the action registry
/// (each needs a per-run target, so it has its own dialog). The window has to show their posture
/// before any of them is constructed, and it is also compiled into the non-Revit controller, where
/// the Revit-typed action classes cannot follow. So the posture is declared once here, and each
/// action's <c>DeclaredWorksets</c> reads it from here — still exactly one declaration per action.
/// </summary>
public static class SyntheticActionPostures
{
    /// <summary>Initiate to Forma moves the FILE; it never reads model content.</summary>
    public const WorksetPosture InitiateToForma = WorksetPosture.ReadsNoInstances;

    /// <summary>Cut Model works on document-level project locations, not instances.</summary>
    public const WorksetPosture CutModel = WorksetPosture.ReadsNoInstances;

    /// <summary>Export NWC opens the worksets its view needs itself.</summary>
    public const WorksetPosture ExportNwc = WorksetPosture.PlansOwn;

    /// <summary>Export IFC + Data opens the worksets its export view shows, like Export NWC.</summary>
    public const WorksetPosture ExportIfcData = WorksetPosture.PlansOwn;
}
