namespace KhBatchRunner.Schedule;

/// <summary>
/// How expensive one Revit instance of a run is, which is what <see cref="InstanceBudget"/> divides
/// the machine by.
///
/// Not a general "priority" or "size" knob: it is a small closed set of measured-or-argued
/// per-instance costs, and a new member should only appear when some action genuinely changes what
/// a Revit process weighs. Derive it from a job with <see cref="InstanceBudget.WeightFor"/> rather
/// than deciding per call site — the caps must agree between the parent that splits a run and the
/// supervisor that relaunches its groups.
/// </summary>
internal enum RunWeight
{
    /// <summary>Revit opening and processing models. The budget the original measurements were
    /// taken against.</summary>
    Normal,

    /// <summary>Revit plus the AIT Model Checker loaded in-process, holding a checkset and a full
    /// report until it is exported. See <see cref="InstanceBudget"/>'s Interop constants.</summary>
    Interop,

    /// <summary>
    /// A run whose action opens EVERY workset (<see cref="Actions.IBatchAction.OpensAllWorksets"/>
    /// — today, Export NWC). The whole model is resident rather than the fraction an ordinary open
    /// loads, and every link comes with it. OBSERVED 2026-08-15: eight such instances took this
    /// 64 GB machine from 64 GB free to about 10.
    /// </summary>
    AllWorksets,
}
