using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Interop;
using KhBatchRunner.Schedule;
using KhBatchRunner.UI;

// WinForms and Revit both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// Schedule panel button: open the Schedule Interop dialog. "Run now" runs immediately in this
/// Revit; "Save schedule" registers a Windows Task Scheduler task that launches Revit at the
/// chosen time to run the Batch Interop job headless. Mirrors <see cref="ScheduleRunCommand"/>
/// (same reused <see cref="ScheduledRunner"/>/<see cref="ScheduleSaver"/> plumbing) but opens the
/// dedicated <see cref="ScheduleInteropWindow"/> and is gated on AIT being usable here first —
/// editing/testing an Interop schedule from a Revit version Batch Interop hasn't been verified
/// against isn't useful even though the eventual scheduled run may execute on another machine.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ScheduleInteropCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!Update.UpdateCheck.EnsureNotExpired()) return Result.Cancelled;

        UIApplication uiApp = commandData.Application;
        string year = uiApp.Application.VersionNumber;

        switch (AitLocator.Check(year))
        {
            case AitStatus.NotInstalled:
                TaskDialog.Show("Schedule Interop",
                    $"Autodesk Interoperability Tools (Model Checker) is not installed for Revit {year}.\n\n" +
                    "Install \"BIM Interoperability Tools\" for this Revit version " +
                    "(interoperability.autodesk.com), then try again.");
                return Result.Cancelled;
            case AitStatus.InstalledUnverified:
                TaskDialog.Show("Schedule Interop",
                    $"Autodesk Interoperability Tools is installed for Revit {year}, but Batch Interop " +
                    $"has not been verified against that version yet (verified: Revit 2026).\n\n" +
                    "Run it from Revit 2026, or ask for this version to be verified.");
                return Result.Cancelled;
        }

        var client = new Forma.DataManagementClient(new Auth.SessionTokenProvider());
        using var window = new ScheduleInteropWindow(client, year);
        if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK || window.Job is null)
            return Result.Cancelled;

        ScheduledJob job = window.Job;

        if (window.Outcome == ScheduleInteropWindow.ScheduleOutcome.RunNow)
        {
            // Same modal message-loop runner every interactive batch button uses (see
            // ScheduleRunCommand) — cloud opens from an Idling event hung/crashed.
            try
            {
                if (!ScheduledRunner.TryRunInteractive(uiApp, job, $"Schedule Interop — {job.Name}", out string? error))
                {
                    TaskDialog.Show("Schedule Interop — Run now",
                        error ?? "A batch is already running. Wait for it to finish before starting another.");
                    return Result.Cancelled;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Schedule Interop — Run now", $"Could not start run: {ex.Message}");
                return Result.Failed;
            }
            return Result.Succeeded;
        }

        // "Save schedule" now saves inside the window (which stays open); the dialog only
        // returns OK for Run now, handled above.
        return Result.Succeeded;
    }
}
