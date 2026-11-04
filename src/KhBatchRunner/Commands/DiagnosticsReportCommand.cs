using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
// WinForms is referenced project-wide (UseWindowsForms), and it has its own TaskDialog.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.Commands;

/// <summary>
/// Setting panel: "Report Issue" — runs the diagnostic collector and leaves one ZIP on the Desktop.
///
/// The collector is a PowerShell script rather than C# on purpose: it has to be runnable on a PC
/// where Revit will not start at all, which is the case it exists for. This button is the same
/// thing the Start-menu shortcut runs — it exists because a shortcut buried in a Start-menu folder
/// is not somewhere anyone looks while Revit is open in front of them.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class DiagnosticsReportCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        string? script = FindCollector();
        if (script == null)
        {
            TaskDialog.Show(Product.Name,
                "The diagnostic collector was not found on this PC.\n\n" +
                "It ships with the installer, so this usually means the add-in was dev-deployed " +
                "rather than installed. Run the setup.exe, or run install\\Collect-Diagnostics.cmd " +
                "from the source checkout.");
            return Result.Cancelled;
        }

        try
        {
            // Through cmd.exe explicitly: a .cmd is not an executable image, so it can only be
            // started directly with UseShellExecute=true — and that mode ignores the environment
            // below. The console window it opens is wanted; it is the run's only progress display.
            string comSpec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var psi = new ProcessStartInfo(comSpec, $"/c \"\"{script}\"\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };

            // Revit runs with a PRIVATE per-session temp directory (%TEMP%\<guid>\), which a child
            // process inherits. The collector reads %TEMP%\KhBatchRunner\broker.log, so without
            // this it would look inside a directory the broker never wrote to and report the log as
            // missing — the same trap that sent an earlier investigation down a wrong root cause.
            string? userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(userTemp))
            {
                string expanded = Environment.ExpandEnvironmentVariables(userTemp!);
                psi.EnvironmentVariables["TEMP"] = expanded;
                psi.EnvironmentVariables["TMP"] = expanded;
            }

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            message = $"Could not start the diagnostic collector ({script}): {ex.Message}";
            return Result.Failed;
        }

        return Result.Succeeded;
    }

    /// <summary>
    /// Locates <c>Collect-Diagnostics.cmd</c>: beside the loaded assembly first (how the installers
    /// and Install-Dev.ps1 both deploy it, and the same assembly-relative rule the scheduled-run
    /// launcher uses), then the machine-wide Support folders of either edition, so a PC carrying
    /// only the other edition's install still finds one.
    /// </summary>
    private static string? FindCollector()
    {
        const string fileName = "Collect-Diagnostics.cmd";

        string? beside = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (beside != null)
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(beside, "Support", fileName),
                         Path.Combine(beside, fileName)
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (string product in new[] { "KH Batch Script Runner" })
        {
            string candidate = Path.Combine(programData, "Kanghun BIM", product, "Support", fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
