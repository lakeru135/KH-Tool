// WinForms and the Revit API both define TaskDialog; this add-in means Revit's.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace KhBatchRunner.UI;

/// <summary>
/// Builds and shows a modal dialog with a safety net: any exception thrown while constructing or
/// running it is surfaced as a Revit TaskDialog instead of letting the window silently close (the
/// reported "the dialog shows, then errors and closes itself" symptom). The <see cref="Func{Form}"/>
/// factory means a throw in the constructor is caught too, not just during ShowDialog.
/// </summary>
internal static class DialogHost
{
    public static void Show(Func<Form> factory)
    {
        try
        {
            using Form window = factory();
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            TaskDialog.Show("KH Batch Script Runner",
                "The dialog could not open or failed while running:\n\n" +
                ex.GetType().Name + ": " + ex.Message);
        }
    }
}
