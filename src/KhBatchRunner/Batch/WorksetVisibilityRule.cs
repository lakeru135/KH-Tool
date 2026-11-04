namespace KhBatchRunner.Batch;

/// <summary>
/// The rule that decides whether a view SHOWS a workset — and therefore whether that workset has to
/// be opened for an export scoped to that view.
///
/// <para><b>Why it lives here, away from Revit.</b> This is the arithmetic that decides how much of a
/// model gets loaded, and loading too little fails SILENTLY: the model merely looks empty, which is
/// how Export NWC wrote nothing for three releases while reporting success. A rule with that failure
/// mode has to be checkable without a Revit, a cloud model and a live run — the same argument that
/// put <see cref="Schedule.RunSupervisor.RoundGroupSize"/> and
/// <see cref="Schedule.InstanceBudget.CapFor"/> in reach of the harness. The Revit-typed half (read
/// <c>View.GetWorksetVisibility</c>, read <c>Workset.IsVisibleByDefault</c>) stays in
/// <see cref="Actions.ExportNwcAction"/>; only the decision is here.</para>
///
/// <para><b>The shape: three-valued answers, unioned.</b> Revit's <c>WorksetVisibility</c> is
/// Visible / Hidden / UseGlobalSetting, which maps to <c>true</c> / <c>false</c> / <c>null</c> —
/// null deferring to the workset's own default. Several sources can answer for one workset: the view
/// itself, and its view template when it has one. They are UNIONED rather than resolved in
/// precedence order, deliberately: whether <c>GetWorksetVisibility</c> returns the applied value when
/// a template controls the Worksets tab is not something this project has confirmed, and a union is a
/// superset whichever way that turns out. Over-opening costs time; under-opening costs the run.</para>
/// </summary>
internal static class WorksetVisibilityRule
{
    /// <summary>
    /// Does any <paramref name="answers"/> source show this workset? <c>null</c> means "use the
    /// global setting" and defers to <paramref name="visibleByDefault"/>. No answers at all means
    /// nothing was readable, which is treated as VISIBLE for the reason in the class summary.
    /// </summary>
    public static bool Shows(bool visibleByDefault, params bool?[] answers)
    {
        if (answers is null || answers.Length == 0) return true;
        foreach (bool? a in answers)
            if (a ?? visibleByDefault) return true;
        return false;
    }
}
