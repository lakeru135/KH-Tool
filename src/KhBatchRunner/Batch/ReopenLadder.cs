namespace KhBatchRunner.Batch;

/// <summary>
/// The two decisions behind <c>BatchEngine.ReopenWithRequiredWorksets</c>'s retry ladder: whether an
/// open was REFUSED, and what the next attempt should ask for.
///
/// <para><b>Why they live away from Revit.</b> Both decide how much of a model gets loaded, and
/// getting either wrong fails SILENTLY — an under-opened model looks empty rather than broken, which
/// is the failure this whole release sequence has been closing out since 1.3.8.3. The same argument
/// that put <see cref="WorksetVisibilityRule"/> and <c>InstanceBudget.CapFor</c> in reach of the
/// offline harness applies here: the Revit-typed half (open the document, count its worksets) stays
/// in the engine; only the judgement is here.</para>
///
/// <para><b>What forced the ladder.</b> MEASURED 2026-08-15: <c>SITE2A-BLDA-STR</c> asked for the same
/// 13 of its 58 worksets twice, 45 minutes apart, and got <c>13 of 58</c> once and <c>0 of 58</c> the
/// other time. No error in the journal either way; the refused open was simply faster.
/// <c>WorksetConfiguration.Open(ids)</c> is intermittently ignored on a background cloud open, just
/// as <c>WorksetConfigurationOption.OpenAllWorksets</c> always is. A single attempt therefore loses a
/// model to a blip.</para>
/// </summary>
internal static class ReopenLadder
{
    /// <summary>Opens a reopen may cost before the model is failed: the derived set, the same set
    /// again, then every workset. Three because the second is cheap and usually enough, and the
    /// third is correct but re-loads the links a view-scoped open was chosen to skip.</summary>
    public const int Attempts = 3;

    /// <summary>Does <paramref name="attempt"/> abandon the derived set and ask for every user
    /// workset? Only the last rung — the expensive, known-good 1.3.8.7 path.</summary>
    public static bool WidensToAll(int attempt) => attempt >= Attempts;

    /// <summary>Is there another rung after <paramref name="attempt"/>?</summary>
    public static bool CanRetry(int attempt) => attempt < Attempts;

    /// <summary>
    /// Did Revit REFUSE the request? True when the reopened document has no user workset open at
    /// all, or none of the ones that were asked for.
    ///
    /// <para>A PARTIAL result is deliberately not a refusal: a workset can be deleted or renamed
    /// between two opens of a live cloud model, so "some of what I asked for" is a legitimate answer
    /// and is reported in the row rather than retried. Only "none of it" means the configuration was
    /// ignored.</para>
    /// </summary>
    /// <param name="openCount">User worksets open in the reopened document.</param>
    /// <param name="askedNowOpen">How many of the requested worksets are among them.</param>
    public static bool Refused(int openCount, int askedNowOpen) => openCount == 0 || askedNowOpen == 0;
}
