namespace KhBatchRunner.Actions;

/// <summary>
/// What a closed-worksets open means for one action — the question every action must answer, and
/// the reason <see cref="BatchActionBase.Worksets"/> has no default.
///
/// <para><b>Why this exists.</b> <c>BatchEngine</c> opens every model with
/// <see cref="Autodesk.Revit.DB.WorksetConfigurationOption.CloseAllWorksets"/> because it is far
/// cheaper on cloud models. An element in a closed workset is <b>not loaded at all</b>: a
/// <c>FilteredElementCollector</c> never returns it and no exporter can walk it. For an action that
/// reads element INSTANCES that is not a cheap open, it is a <b>wrong answer with no error</b> —
/// nothing throws, the log is green, and the output is empty.</para>
///
/// <para><b>What it cost before it was a declaration.</b> Export NWC wrote near-empty <c>.nwc</c>
/// files from 1.3.8.4 to 1.3.8.8 — five builds, because each fix uncovered the next silent Revit
/// behaviour underneath. Batch Interop checked partial models and called them clean from 1.3.7, so
/// every checkset report from before 1.3.8.4 is worthless. Neither failure was visible from inside:
/// only a human opening the NWC in Navisworks could see it.</para>
///
/// <para><b>The mechanism.</b> The old <c>BatchActionBase.OpensAllWorksets =&gt; false</c> default
/// let a new action be blind without anyone deciding anything, and its comment asserted that
/// everything other than Export NWC and Batch Interop "reads phases, worksets, sheets or link types,
/// none of which a closed workset hides". An audit on 2026-08-16 found four actions for which that
/// was simply untrue. A default cannot be reviewed; an abstract member has to be answered before the
/// code will compile. That, plus <c>install\Verify-ActionPosture.ps1</c>, is the whole design.</para>
///
/// <para><b>Types are the exception, and the reason several actions are safe.</b> Element TYPES stay
/// loaded whatever the worksets do — that is how <c>RelinkLinksAction.SelectWorksetsToOpen</c> reads
/// <c>RevitLinkType.WorksetId</c> on the closed document (live since 0.9.1). So do views, sheets,
/// phases, worksets themselves, and view-owned annotation:
/// <see cref="Autodesk.Revit.DB.WorksetConfigurationOption.CloseAllWorksets"/> closes USER worksets,
/// and a view's own workset is not one.</para>
/// </summary>
public enum WorksetPosture
{
    /// <summary>
    /// Reads only things a closed workset cannot hide — element types, views, sheets, phases,
    /// worksets, or annotation owned by a view. The closed open is a <b>complete</b> answer, not a
    /// cheap approximation of one.
    /// </summary>
    ReadsNoInstances,

    /// <summary>
    /// Reads element INSTANCES on the closed open, so its answer <b>is</b> partial — and that has
    /// been accepted rather than missed. The reason string must say who accepted it and when, so
    /// this is never mistaken for the state the four blind actions were in before anyone looked.
    /// </summary>
    PartialByDesign,

    /// <summary>
    /// Needs every workset open (<see cref="IBatchAction.OpensAllWorksets"/>). Correct and
    /// expensive: every link sits in a workset too, so Revit downloads and opens each linked cloud
    /// model as well — measured at 13–30 minute first opens against 20–40 seconds closed.
    /// </summary>
    OpensAll,

    /// <summary>
    /// Works out its own set from the cheap closed open, then reopens naming only those worksets
    /// (<see cref="IBatchAction.PlansWorksetOpen"/> + <see cref="IBatchAction.SelectWorksetsToOpen"/>).
    /// Costs a second open and buys back most of what <see cref="OpensAll"/> spends.
    /// </summary>
    PlansOwn,
}
