using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using KhBatchRunner.NwdFamily;

namespace KhBatchRunner.Commands;

/// <summary>
/// KH-NWD Tool panel: "Create Family from Navisworks". Reads a <c>.khnwd.json</c> written by the
/// KH-NWD Tool Navisworks add-in (selection + chosen Revit category), builds the .rfa next to it
/// (named after the family), then optionally loads it and places it — by clicking a point (the
/// family's insertion point is the object's plan centre at its lowest point) or at the original
/// Navisworks position in shared or internal coordinates. Active-model only; not a batch action.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CreateFamilyFromNavisworksCommand : IExternalCommand
{
    private const string Title = "KH-NWD Tool";

    private enum Placement { None, Manual, NavisworksShared, NavisworksInternal }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a KH-NWD Tool export",
            Filter = NwdExchangeFile.FileFilter,
        };
        if (open.ShowDialog() != true)
            return Result.Cancelled;

        try
        {
            NwdExchangeFile export = NwdExchangeFile.Read(open.FileName);
            string folder = Path.GetDirectoryName(open.FileName) ?? "";
            string rfaPath = Path.Combine(folder, SafeFileName(export.FamilyName) + ".rfa");

            NwdBuildReport report = NwdFamilyBuilder.Build(commandData.Application.Application, export, rfaPath);
            string summary = $"Category: {NwdCategories.Find(export.CategoryKey).DisplayName}\n{report}\n\nSaved: {rfaPath}";

            UIDocument? uidoc = commandData.Application.ActiveUIDocument;
            Document? doc = uidoc?.Document;
            if (uidoc == null || doc == null || doc.IsFamilyDocument)
            {
                Autodesk.Revit.UI.TaskDialog.Show(Title, "Family created.\n\n" + summary);
                return Result.Succeeded;
            }

            // Fully qualified: net8's WinForms also defines TaskDialog, and this add-in means Revit's.
            var dialog = new Autodesk.Revit.UI.TaskDialog(Title)
            {
                MainInstruction = $"Created {Path.GetFileName(rfaPath)}",
                MainContent = summary,
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Load into this project");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Load and place manually",
                "Click where it goes. The click point is the object's centre in plan, at its lowest point.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Load and place at the Navisworks location (shared coordinates)",
                "Use when the NWC was exported from Revit with Shared coordinates (the exporter's default).");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Load and place at the Navisworks location (internal coordinates)",
                "Use when the NWC was exported with Internal coordinates.");

            Placement placement;
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1: placement = Placement.None; break;
                case TaskDialogResult.CommandLink2: placement = Placement.Manual; break;
                case TaskDialogResult.CommandLink3: placement = Placement.NavisworksShared; break;
                case TaskDialogResult.CommandLink4: placement = Placement.NavisworksInternal; break;
                default: return Result.Succeeded;
            }

            FamilySymbol symbol = Load(doc, rfaPath, export, placement);
            if (placement == Placement.Manual)
                PlaceManually(uidoc, symbol);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            Autodesk.Revit.UI.TaskDialog.Show(Title, "Could not create the family:\n\n" + ex);
            return Result.Failed;
        }
    }

    /// <summary>Loads the family (overwriting an older copy) and, for Navisworks placement, places it.</summary>
    private static FamilySymbol Load(Document doc, string rfaPath, NwdExchangeFile export, Placement placement)
    {
        using var t = new Transaction(doc, "KH-NWD Tool: load family");
        t.Start();
        string familyName = Path.GetFileNameWithoutExtension(rfaPath);
        if (!doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out Family? family) || family == null)
            family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => f.Name == familyName);
        if (family == null)
            throw new InvalidOperationException("Revit did not load " + rfaPath);

        var symbol = (FamilySymbol)doc.GetElement(family.GetFamilySymbolIds().First());
        if (!symbol.IsActive)
        {
            symbol.Activate();
            doc.Regenerate();
        }

        if (placement is Placement.NavisworksShared or Placement.NavisworksInternal)
            PlaceAtNavisworks(doc, symbol, export, placement == Placement.NavisworksShared);
        t.Commit();
        return symbol;
    }

    /// <summary>Revit's own placement tool: the user clicks points until Esc (must run outside a transaction).</summary>
    private static void PlaceManually(UIDocument uidoc, FamilySymbol symbol)
    {
        try
        {
            uidoc.PromptForFamilyInstancePlacement(symbol);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // Esc ends placement; instances already placed are kept.
        }
    }

    private static void PlaceAtNavisworks(Document doc, FamilySymbol symbol, NwdExchangeFile export, bool shared)
    {
        var point = new XYZ(export.OriginX, export.OriginY, export.OriginZ);
        // The family was built square to its own axes; the Navisworks object was turned by this much.
        double angle = export.Rotation;
        if (shared)
        {
            // Navisworks position is in shared coordinates; convert to the project's internal coordinates.
            Transform toInternal = doc.ActiveProjectLocation.GetTotalTransform();
            point = toInternal.OfPoint(point);
            angle += Math.Atan2(toInternal.BasisX.Y, toInternal.BasisX.X);
        }

        List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation).ToList();
        Level? level = levels.LastOrDefault(l => l.Elevation <= point.Z + 1e-6) ?? levels.FirstOrDefault();

        StructuralType structural = NwdCategories.Find(export.CategoryKey).Category == BuiltInCategory.OST_StructuralColumns
            ? StructuralType.Column
            : StructuralType.NonStructural;
        FamilyInstance instance = level != null
            ? doc.Create.NewFamilyInstance(point, symbol, level, structural)
            : doc.Create.NewFamilyInstance(point, symbol, structural);
        doc.Regenerate();

        // Level-based placement can land on the level; move it to the exact Navisworks position.
        if (instance.Location is LocationPoint location)
        {
            XYZ delta = point - location.Point;
            if (!delta.IsZeroLength())
                ElementTransformUtils.MoveElement(doc, instance.Id, delta);
        }
        if (Math.Abs(angle) > 1e-9)
            ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(point, point + XYZ.BasisZ), angle);
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "Navisworks Family" : s;
    }

    private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
