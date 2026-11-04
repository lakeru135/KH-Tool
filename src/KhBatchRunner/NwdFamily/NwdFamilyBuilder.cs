using System.IO;
using Autodesk.Revit.DB;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace KhBatchRunner.NwdFamily;

/// <summary>What one build produced, for the summary dialog.</summary>
internal sealed class NwdBuildReport
{
    public int Meshes;
    public int Solids;
    public int MeshShapes;
    public int Skipped;
    public int SkippedFaces;
    public int ParametricBoxes;
    public int ParametricCylinders;
    public int ParametricFellBack;
    public int ParametersCreated;
    public int ParametersFilled;
    public int ParametersSkipped;
    public string Template = "";
    public readonly List<string> Notes = new();

    private const int NotesShown = 6;

    public void Note(string line) => Notes.Add(line);

    public override string ToString() =>
        $"Elements: {Meshes:N0}" +
        (ParametricBoxes + ParametricCylinders > 0
            ? $"\nResizable shapes: {ParametricBoxes:N0} box(es), {ParametricCylinders:N0} cylinder(s)" : "") +
        $"\nSolids (FreeForm): {Solids:N0}\nMesh shapes (DirectShape): {MeshShapes:N0}" +
        (Skipped > 0 ? $"\nSkipped elements: {Skipped:N0}" : "") +
        (SkippedFaces > 0 ? $"\nDegenerate triangles dropped: {SkippedFaces:N0}" : "") +
        (ParametersCreated + ParametersFilled + ParametersSkipped > 0
            ? $"\nParameters: {ParametersCreated:N0} added, {ParametersFilled:N0} filled in" +
              (ParametersSkipped > 0 ? $", {ParametersSkipped:N0} skipped" : "") : "") +
        $"\nTemplate: {Path.GetFileName(Template)}" +
        (Notes.Count > 0 ? "\n\n" + string.Join("\n", Notes.Take(NotesShown)) +
            (Notes.Count > NotesShown ? $"\n(+{Notes.Count - NotesShown} more)" : "") : "");
}

/// <summary>
/// Builds an .rfa from a KH-NWD Tool export: a new family from the category's template, one
/// material per Navisworks colour, and each Navisworks element as a FreeForm solid (closed meshes)
/// or a DirectShape (open meshes, which Revit cannot turn into a solid). When the export asks for it,
/// boxes and upright cylinders become resizable extrusions instead (<see cref="NwdParametricShapes"/>);
/// the export's Navisworks properties become type parameters (<see cref="NwdFamilyParameters"/>).
/// </summary>
internal static class NwdFamilyBuilder
{
    public static NwdBuildReport Build(RevitApplication app, NwdExchangeFile export, string rfaPath)
    {
        NwdCategory category = NwdCategories.Find(export.CategoryKey);
        var report = new NwdBuildReport { Template = FindTemplate(app, category) };

        Document fam = app.NewFamilyDocument(report.Template);
        try
        {
            using (var t = new Transaction(fam, "KH-NWD Tool: build family"))
            {
                // Constraint warnings never become a dialog; a shape that cannot hold its
                // constraints is caught by its own check and kept as fixed geometry.
                FailureHandlingOptions failures = t.GetFailureHandlingOptions();
                failures.SetFailuresPreprocessor(new DropWarnings());
                t.SetFailureHandlingOptions(failures);
                t.Start();
                Category? target = Category.GetCategory(fam, category.Category);
                if (target != null && fam.OwnerFamily.FamilyCategory?.Id != target.Id)
                    fam.OwnerFamily.FamilyCategory = target;

                FamilyManager fm = fam.FamilyManager;
                if (fm.CurrentType == null)
                    fm.NewType(string.IsNullOrWhiteSpace(export.FamilyName) ? "Type 1" : export.FamilyName);

                var fits = export.Meshes.Select(m => export.RebuildShapes ? NwdShapeFitter.Fit(m) : null).ToList();
                int sized = fits.Count(f => f != null);
                NwdParametricShapes? shapes = sized > 0 ? NwdParametricShapes.TryCreate(fam, report) : null;
                var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var materials = new Dictionary<int, ElementId>();
                for (int i = 0; i < export.Meshes.Count; i++)
                {
                    NwdMesh mesh = export.Meshes[i];
                    report.Meshes++;
                    ElementId material = MaterialFor(fam, materials, mesh);
                    if (fits[i] is { } shape && shapes != null &&
                        shapes.TryBuild(shape, sized == 1 ? "" : Prefix(mesh, prefixes), material, reserved, report))
                        continue;
                    AddMesh(fam, category.Category, mesh, material, report);
                }

                NwdFamilyParameters.Apply(fam, export.Parameters, reserved, report);
                t.Commit();
            }

            fam.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
        }
        finally
        {
            fam.Close(false);
        }
        return report;
    }

    /// <summary>Category template, falling back to the English templates and finally to Generic Model.</summary>
    internal static string FindTemplate(RevitApplication app, NwdCategory category)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(app.FamilyTemplatePath))
            dirs.Add(app.FamilyTemplatePath);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "RVT " + app.VersionNumber, "Family Templates");
        dirs.Add(Path.Combine(root, "English"));
        dirs.Add(Path.Combine(root, "English-Imperial"));

        string[] names =
        {
            category.Template,
            category.Template.Replace("Metric ", ""),
            "Metric Generic Model.rft",
            "Generic Model.rft",
        };
        foreach (string name in names)
            foreach (string dir in dirs)
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                    return path;
            }
        throw new FileNotFoundException(
            $"No family template found for {category.DisplayName}. Looked in:\n" + string.Join("\n", dirs));
    }

    private static ElementId MaterialFor(Document fam, Dictionary<int, ElementId> cache, NwdMesh mesh)
    {
        int key = (mesh.R << 16) | (mesh.G << 8) | mesh.B;
        if (cache.TryGetValue(key, out ElementId? id))
            return id;

        string name = $"KH-NWD {mesh.R:D3}-{mesh.G:D3}-{mesh.B:D3}";
        Material? existing = new FilteredElementCollector(fam).OfClass(typeof(Material)).Cast<Material>()
            .FirstOrDefault(m => m.Name == name);
        id = existing?.Id ?? Material.Create(fam, name);
        if (fam.GetElement(id) is Material material)
            material.Color = new Autodesk.Revit.DB.Color(mesh.R, mesh.G, mesh.B); // not System.Drawing's
        cache[key] = id;
        return id;
    }

    private static void AddMesh(Document fam, BuiltInCategory bic, NwdMesh mesh, ElementId material, NwdBuildReport report)
    {
        var builder = new TessellatedShapeBuilder
        {
            Target = TessellatedShapeBuilderTarget.AnyGeometry,
            Fallback = TessellatedShapeBuilderFallback.Mesh,
            GraphicsStyleId = ElementId.InvalidElementId,
        };
        builder.OpenConnectedFaceSet(true);
        int faces = 0;
        double[] v = mesh.Vertices;
        for (int i = 0; i + 2 < mesh.Triangles.Length; i += 3)
        {
            XYZ a = Point(v, mesh.Triangles[i]), b = Point(v, mesh.Triangles[i + 1]), c = Point(v, mesh.Triangles[i + 2]);
            if ((b - a).CrossProduct(c - a).GetLength() < 1e-12)
            {
                report.SkippedFaces++;
                continue;
            }
            builder.AddFace(new TessellatedFace(new List<XYZ> { a, b, c }, material));
            faces++;
        }
        builder.CloseConnectedFaceSet();
        if (faces == 0)
        {
            report.Skipped++;
            return;
        }

        builder.Build();
        IList<GeometryObject> geometry = builder.GetBuildResult().GetGeometricalObjects();

        var meshes = new List<GeometryObject>();
        foreach (GeometryObject g in geometry)
        {
            if (g is Solid solid && solid.Volume > 1e-9 && TryFreeForm(fam, solid, material))
                report.Solids++;
            else
                meshes.Add(g);
        }

        if (meshes.Count > 0)
        {
            if (TryDirectShape(fam, bic, meshes, mesh.Name))
                report.MeshShapes++;
            else
                report.Skipped++;
        }
    }

    private static bool TryFreeForm(Document fam, Solid solid, ElementId material)
    {
        try
        {
            FreeFormElement ffe = FreeFormElement.Create(fam, solid);
            ffe.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(material);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Open (non-watertight) geometry cannot be a FreeFormElement; keep it as a DirectShape.
    private static bool TryDirectShape(Document fam, BuiltInCategory bic, IList<GeometryObject> shape, string name)
    {
        foreach (BuiltInCategory candidate in new[] { bic, BuiltInCategory.OST_GenericModel }.Distinct())
        {
            try
            {
                DirectShape ds = DirectShape.CreateElement(fam, new ElementId(candidate));
                ds.SetShape(shape);
                if (!string.IsNullOrEmpty(name))
                    ds.SetName(name);
                return true;
            }
            catch (Exception)
            {
                // try the next category
            }
        }
        return false;
    }

    /// <summary>"&lt;mesh name&gt; " for a family with several sized shapes; "Shape N " when the name is empty or taken.</summary>
    private static string Prefix(NwdMesh mesh, HashSet<string> used)
    {
        string name = NwdFamilyParameters.Clean(mesh.Name);
        if (name.Length == 0 || !used.Add(name))
            for (int n = used.Count + 1; !used.Add(name = $"Shape {n}"); n++) { }
        return name + " ";
    }

    private static XYZ Point(double[] v, int index) => new(v[index * 3], v[index * 3 + 1], v[index * 3 + 2]);

    private sealed class DropWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (FailureMessageAccessor f in a.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning)
                    a.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}
