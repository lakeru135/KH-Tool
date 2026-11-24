using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

using RunTrace = KhBatchRunner.Diagnostics.RunTrace;

namespace KhBatchRunner.Schedule;

/// <summary>
/// Answers the two questions Revit asks on its way out, so an unattended child that has finished
/// its models and asked to close actually closes.
///
/// <para><b>Why.</b> MEASURED 2026-11-23 20:18: three children wrote <c>batch end: 8 ok</c>, posted
/// their close — and Revit put up <c>TaskDialog_Changes_Not_Saved</c> (Synchronize / Save locally /
/// Do not save / Cancel). The batch's <c>DialogSuppressor</c> had already been disposed; the
/// <c>ForeignDialogBreaker</c> found no OK/Yes button and closed the box, which is Cancel, which
/// cancelled the quit. All three sat there until the user closed them by hand.</para>
///
/// <para><b>Armed at startup, acts only after the close.</b> It is attached in <c>App.OnStartup</c>
/// for an unattended launch — not when the batch ends — for the reason the dialog breaker learned on
/// 2026-08-18: a watchdog must not depend on the code it protects. Until this process has posted its
/// own close (<see cref="RevitShutdown.ClosePostedByThisProcess"/>) it answers nothing, so no dialog
/// raised during the run itself is ever touched here.</para>
///
/// <para><b>Why these answers are safe.</b> By the time the close is posted every model's result is
/// in the log and any sync the job asked for has already happened. A batch opens models detached or
/// with no-sync, so what is left unsaved is exactly what the job meant to throw away. Ids and buttons
/// are from the journal of the user's own close of the same dialog (journal.1149, 2026-11-23).</para>
/// </summary>
internal static class ShutdownDialogAnswers
{
    public const string ChangesNotSaved = "TaskDialog_Changes_Not_Saved";
    public const string CloseWithoutSaving = "TaskDialog_Close_Project_Without_Saving";

    /// <summary>"Do not save the project".</summary>
    public const int DoNotSave = 1003;

    /// <summary>"Relinquish all elements and worksets".</summary>
    public const int Relinquish = 1001;

    private static bool _armed;

    /// <summary>Attaches the handler once. Unattended launches only — see the class remarks.</summary>
    public static void Arm(UIControlledApplication app)
    {
        if (_armed) return;
        app.DialogBoxShowing += OnDialogBoxShowing;
        _armed = true;
    }

    /// <summary>The button to press for <paramref name="dialogId"/>, or null to leave it alone. Pure,
    /// so the harness pins the rule that nothing is answered before the close.</summary>
    internal static int? AnswerFor(string? dialogId, bool closePosted)
    {
        if (!closePosted || string.IsNullOrEmpty(dialogId)) return null;
        return dialogId switch
        {
            ChangesNotSaved => DoNotSave,
            CloseWithoutSaving => Relinquish,
            _ => null,
        };
    }

    private static void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
    {
        try
        {
            int? answer = AnswerFor(e.DialogId, RevitShutdown.ClosePostedByThisProcess);
            if (answer == null) return;
            bool taken = e.OverrideResult(answer.Value);
            RunTrace.Mark($"shutdown dialog answered: {e.DialogId} → {answer.Value}" +
                          (taken ? "" : " (Revit did not accept the answer)"));
        }
        catch (Exception ex)
        {
            // An event handler that throws during Revit's shutdown is the last thing to risk here.
            try { RunTrace.Mark("shutdown dialog answer failed: " + ex.Message); } catch { }
        }
    }
}
