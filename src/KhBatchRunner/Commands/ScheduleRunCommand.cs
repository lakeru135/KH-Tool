using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

// WinForms and Revit both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// Setting panel button: open the Schedule Run dialog. "Run now" runs immediately in this
/// Revit; "Create schedule" registers a Windows Task Scheduler task that launches Revit
/// at the chosen time to run the job headless.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ScheduleRunCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;

        var client = new Forma.DataManagementClient(new Auth.SessionTokenProvider());
        using var window = new ScheduleRunWindow(client);
        if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK || window.Job is null)
            return Result.Cancelled;

        ScheduledJob job = window.Job;

        if (window.Outcome == ScheduleRunWindow.ScheduleOutcome.RunNow)
        {
            // Run interactively through the modal message-loop runner (BatchRunnerWindow), the same
            // path the Multi Model Check/Action buttons use — opening Forma cloud models from an
            // Idling event hung/crashed. TryRunInteractive blocks until the run
            // finishes, honours the job's completion (close/leave-open/close-Revit), and shows the
            // completion summary itself.
            try
            {
                if (!ScheduledRunner.TryRunInteractive(uiApp, job, $"Schedule Run — {job.Name}", out string? error))
                {
                    TaskDialog.Show("Schedule Run — Run now",
                        error ?? "A batch is already running. Wait for it to finish before starting another.");
                    return Result.Cancelled;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Schedule Run — Run now", $"Could not start run: {ex.Message}");
                return Result.Failed;
            }
            return Result.Succeeded;
        }

        // "Save schedule" now saves inside the window (which stays open); the dialog only
        // returns OK for Run now, handled above.
        return Result.Succeeded;
    }
}
