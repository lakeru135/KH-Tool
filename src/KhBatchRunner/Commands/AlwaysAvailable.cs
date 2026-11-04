using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Commands;

/// <summary>
/// Ribbon-command availability that is always true — so the button is clickable even
/// with no document open (the Revit home/start screen). The Forma crawler needs no
/// active document: it talks to Forma over REST
/// using the signed-in session token, independent of whatever model (if any) is open.
/// </summary>
public class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}
