using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Commands;

/// <summary>
/// Ribbon-command availability for the active-model tools — DRW Helper's Create and Annotation
/// tools, Excel Link and Parameter Helper: clickable only while a PROJECT document is active. They
/// work on the active model, so on the Revit home screen or in a family there is nothing to act on.
/// </summary>
public class DocumentAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
    {
        UIDocument? uiDoc = applicationData.ActiveUIDocument;
        return uiDoc != null && !uiDoc.Document.IsFamilyDocument;
    }
}

/// <summary>
/// Ribbon-command availability for the DRW tools that make sense in the Family Editor too — Sync
/// View and Single Font: clickable while ANY document, project or family, is active.
/// </summary>
public class AnyDocumentAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
        applicationData.ActiveUIDocument != null;
}
