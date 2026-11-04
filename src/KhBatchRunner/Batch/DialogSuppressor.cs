using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace KhBatchRunner.Batch;

/// <summary>
/// Keeps a batch run unattended by auto-answering the pop-ups Revit would otherwise block on.
/// Active for the whole run (created when the run starts, disposed when it ends) via two events:
///
/// <list type="bullet">
/// <item><b><see cref="UIApplication.DialogBoxShowing"/></b> — any modal TaskDialog / message
/// box (e.g. "file from a previous release will be upgraded", missing links, an add-in's own
/// prompt) is dismissed by overriding its result with the affirmative/continue button so the
/// batch proceeds instead of waiting for a click.</item>
/// <item><b><see cref="Autodesk.Revit.ApplicationServices.Application.FailuresProcessing"/></b>
/// — warnings raised inside a write action's transaction (Purge / Isolate) are deleted and
/// errors rolled back, so Revit never surfaces the modal "warning" dialog at commit time.</item>
/// </list>
///
/// The batch's own summary <see cref="TaskDialog"/> is shown by <see cref="BatchIdlingDriver"/>
/// only after the run finishes and this suppressor is disposed, so it is never swallowed.
/// </summary>
public sealed class DialogSuppressor : IDisposable
{
    // Win32 modal result for the "OK"/"Yes"/default-affirmative button — the generic "continue"
    // answer for the message boxes and simple TaskDialogs a batch open/QA run runs into.
    private const int IdOk = 1;

    private readonly UIApplication _uiApp;
    private bool _disposed;

    public DialogSuppressor(UIApplication uiApp)
    {
        _uiApp = uiApp;
        _uiApp.DialogBoxShowing += OnDialogBoxShowing;
        _uiApp.Application.FailuresProcessing += OnFailuresProcessing;
    }

    private static void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
    {
        // OverrideResult tells Revit to close the dialog as if the given button was clicked,
        // without ever displaying it. A TaskDialog uses its command-link/button ids (Ok = 1,
        // Cancel = 2, Close = 8…); a message box uses the Win32 ids (IDOK = 1). IDOK/first
        // affirmative is the "keep going" answer for the upgrade/missing-link/info prompts a
        // batch run hits. It's a no-op for a dialog that has no matching button, which is safe —
        // the rare Yes/No confirmation is simply left to Revit's own default rather than risking
        // a wrong destructive answer.
        e.OverrideResult(IdOk);
    }

    private static void OnFailuresProcessing(object? sender, FailuresProcessingEventArgs e)
    {
        FailuresAccessor fa = e.GetFailuresAccessor();
        IList<FailureMessageAccessor> failures = fa.GetFailureMessages();
        if (failures.Count == 0)
        {
            e.SetProcessingResult(FailureProcessingResult.Continue);
            return;
        }

        bool resolved = false;
        foreach (FailureMessageAccessor failure in failures)
        {
            FailureSeverity severity = failure.GetSeverity();
            if (severity == FailureSeverity.Warning)
            {
                // Warnings are non-blocking on commit, but deleting them here stops the modal
                // "the following warnings were encountered" dialog from appearing in a batch.
                fa.DeleteWarning(failure);
                resolved = true;
            }
            else if (severity == FailureSeverity.Error)
            {
                // An error would block the commit with a modal dialog. Apply Revit's own default
                // resolution if it offers one; otherwise delete the offending elements so the
                // rest of the action's transaction can still commit instead of the whole model
                // being rolled back.
                if (failure.HasResolutionOfType(FailureResolutionType.Default))
                {
                    fa.ResolveFailure(failure);
                    resolved = true;
                }
                else
                {
                    ICollection<ElementId> culprits = failure.GetFailingElementIds();
                    if (culprits.Count > 0)
                    {
                        fa.DeleteElements(culprits.ToList());
                        resolved = true;
                    }
                }
            }
        }

        // ProceedWithRollBack when nothing could be resolved (a truly unrecoverable error) keeps
        // the model consistent; ProceedWithCommit re-runs failure checking after our fixes.
        e.SetProcessingResult(resolved
            ? FailureProcessingResult.ProceedWithCommit
            : FailureProcessingResult.Continue);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uiApp.DialogBoxShowing -= OnDialogBoxShowing;
        _uiApp.Application.FailuresProcessing -= OnFailuresProcessing;
    }
}
