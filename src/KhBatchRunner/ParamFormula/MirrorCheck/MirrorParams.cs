using System.IO;
using Application = Autodesk.Revit.ApplicationServices.Application;
using Autodesk.Revit.DB;

// Same namespace as the rest of Parameter Helper; the folder only groups the files.
namespace KhBatchRunner.ParamFormula;

/// <summary>
/// The one text instance parameter Mirror Check writes, <c>KH_Mirror State</c>: blank when the
/// element is neither mirrored nor flipped, else what it is, e.g. "Mirrored, Hand Flipped". A SHARED
/// parameter with a fixed GUID, so a tag, schedule or view filter made in one model works in every
/// model set up by KH-Tool, and a model set up twice still has one of it.
///
/// <para>TEXT, not Yes/No (user's choice, 2026-10-15): Revit 2026.5's API refuses
/// SetAllowVaryBetweenGroups on every Yes/No parameter, in all 125 model categories, while a text
/// parameter may vary in all of them (self-test probe). Without that, a door in a mirrored copy of a
/// group could not say so while the same door in the original group says it is not.</para>
///
/// <para>Blank means "not mirrored or flipped". Elements that are neither are never written, so a
/// first check in a central model borrows only the elements that are mirrored or flipped.</para>
/// </summary>
internal static class MirrorParams
{
    public const string Name = "KH_Mirror State";
    public static readonly Guid Guid = new("5c1e7a52-8d3b-4f0e-a6c9-2b7d4e81f3b0");
    private const string Description =
        "Blank, or what the element is: Mirrored, Hand Flipped, Facing Flipped. Kept by KH-Tool Mirror Check.";
    private const string GroupName = "KH-Tool Mirror Check";

    // What Revit reports (measured, 2026.5): FamilyInstance.Mirrored is true for any mirror image of
    // the family — the Mirror tool, OR a single hand or facing flip (a flip is a reflection too).
    // Flipping both ways, or mirroring twice, cancels out. A door mirrored with the Mirror tool across
    // a plane parallel to its wall reports FacingFlipped.
    public const string MirroredWord = "Mirrored", HandWord = "Hand Flipped", FacingWord = "Facing Flipped";

    /// <summary>The value for this state: blank, or the words that apply, in a fixed order.</summary>
    public static string Label(bool mirrored, bool hand, bool facing) =>
        string.Join(", ", new[] { mirrored ? MirroredWord : null, hand ? HandWord : null, facing ? FacingWord : null }
            .Where(w => w != null));

    /// <summary>The model categories the parameter is bound to: every model category that takes project parameters.</summary>
    public static List<Category> Categories(Document doc)
    {
        var list = new List<Category>();
        foreach (Category c in doc.Settings.Categories)
        {
            try
            {
                if (c.CategoryType == CategoryType.Model && c.AllowsBoundParameters && !c.IsTagCategory) list.Add(c);
            }
            catch { /* a category Revit cannot describe is simply not bound */ }
        }
        return list;
    }

    public sealed class State
    {
        /// <summary>The parameter exists in the model with the right GUID.</summary>
        public bool Present;
        /// <summary>Model categories that should carry it and do not yet.</summary>
        public int MissingCategories;
        public int BoundCategories;
        /// <summary>A parameter of the same name with another GUID: setting up would make two of them.</summary>
        public string? Clash;
        /// <summary>Not set to vary between group instances: grouped elements cannot be flagged.</summary>
        public bool NotVarying;
        public bool Ready => Present && MissingCategories == 0 && Clash == null && !NotVarying;
    }

    /// <summary>Mirror Check's own parameter, KH_Mirror State.</summary>
    public static State Read(Document doc) => Read(doc, Guid);

    /// <summary>Any shared parameter, by GUID: present, varying, and the model categories it lacks.</summary>
    public static State Read(Document doc, Guid target)
    {
        var s = new State();
        SharedParameterElement? sp = SharedParameterElement.Lookup(doc, target);
        if (sp == null)
        {
            if (target == Guid) s.Clash = SameNameOtherGuid(doc);
            s.MissingCategories = Categories(doc).Count;
            return s;
        }
        s.Present = true;
        try { s.NotVarying = !sp.GetDefinition().VariesAcrossGroups; } catch { s.NotVarying = true; }
        HashSet<ElementId> bound = BoundCategories(doc, sp);
        s.BoundCategories = bound.Count;
        s.MissingCategories = Categories(doc).Count(c => !bound.Contains(c.Id));
        return s;
    }

    private static string? SameNameOtherGuid(Document doc)
    {
        foreach (ParameterElement pe in new FilteredElementCollector(doc).OfClass(typeof(ParameterElement)).Cast<ParameterElement>())
        {
            if (!string.Equals(pe.GetDefinition()?.Name, Name, StringComparison.OrdinalIgnoreCase)) continue;
            return $"This model already has a parameter called '{Name}' that Mirror Check did not make. " +
                   "Rename or delete it (Manage → Project Parameters), then set up again.";
        }
        return null;
    }

    private static HashSet<ElementId> BoundCategories(Document doc, SharedParameterElement sp)
    {
        var set = new HashSet<ElementId>();
        DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
        {
            if (it.Key is not InternalDefinition def || def.Id != sp.Id) continue;
            if (it.Current is InstanceBinding b)
                foreach (Category c in b.Categories) set.Add(c.Id);
        }
        return set;
    }

    /// <summary>
    /// Adds the parameter, or the categories it is missing. Never removes a category. Uses a
    /// temporary shared parameter file and puts the user's own back afterwards. Call OUTSIDE a
    /// transaction; returns why it could not, or null. <paramref name="onlyFor"/> is for the
    /// self-test: bind to these categories alone, to make a model that is only partly set up.
    /// </summary>
    public static string? Ensure(Application app, Document doc, IList<Category>? onlyFor = null)
    {
        State before = Read(doc);
        if (before.Clash != null) return before.Clash;
        if (before.Ready && onlyFor == null) return null;
        if (doc.IsReadOnly) return "The model is read-only.";

        string spf = Path.Combine(Path.GetTempPath(), $"KH-MirrorCheck-{Guid.NewGuid():N}.txt");
        string oldSpf = app.SharedParametersFilename;
        try
        {
            File.WriteAllText(spf, "");
            app.SharedParametersFilename = spf;
            DefinitionFile file = app.OpenSharedParameterFile()
                ?? throw new InvalidOperationException("Revit could not open a temporary shared parameter file.");
            Definition def = file.Groups.Create(GroupName).Definitions.Create(
                new ExternalDefinitionCreationOptions(Name, SpecTypeId.String.Text)
                {
                    GUID = Guid,
                    Description = Description
                    // Left user-modifiable: Revit refuses SetAllowVaryBetweenGroups on a parameter made
                    // with UserModifiable = false. A value typed by hand is put right by the next check.
                });

            using var t = new Transaction(doc, "Mirror Check: set up parameter");
            t.Start();
            var cats = new CategorySet();
            foreach (Category c in onlyFor ?? Categories(doc)) cats.Insert(c);
            InstanceBinding binding = app.Create.NewInstanceBinding(cats);
            bool ok = before.Present
                ? doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.Data)
                : doc.ParameterBindings.Insert(def, binding, GroupTypeId.Data);
            if (!ok) throw new InvalidOperationException($"Revit refused to bind '{Name}'.");
            // A mirrored group's members must be able to say so on their own.
            SharedParameterElement sp = SharedParameterElement.Lookup(doc, Guid)
                ?? throw new InvalidOperationException($"'{Name}' was bound but is not in the model.");
            try { sp.GetDefinition().SetAllowVaryBetweenGroups(doc, true); }
            catch (Exception ex)
            {
                Diagnostics.RunTrace.Mark($"Mirror Check: {Name} cannot vary between groups: {ex.Message}");
                return $"Revit would not let '{Name}' vary between group instances: {ex.Message}";
            }
            if (t.Commit() != TransactionStatus.Committed) return "Revit did not accept the parameter.";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally
        {
            try { app.SharedParametersFilename = oldSpf; } catch { }
            try { File.Delete(spf); } catch { }
        }
    }

    /// <summary>The element's target parameter, or null when it does not carry it.</summary>
    public static Parameter? On(Element e, Guid target) => e.get_Parameter(target);

    // ------------------------------------------------------------------ a parameter of the user's own

    /// <summary>
    /// The parameter to write in this model: the one the model's setting names, if it is still in
    /// the model; otherwise Mirror Check's own.
    /// </summary>
    public static Guid Target(Document doc, FormulaRuleSet set) =>
        System.Guid.TryParse(set.MirrorTargetGuid, out Guid chosen) && chosen != Guid &&
        SharedParameterElement.Lookup(doc, chosen) != null ? chosen : Guid;

    /// <summary>The model's name for a shared parameter, or null when it is not in the model.</summary>
    public static string? NameOf(Document doc, Guid target) =>
        SharedParameterElement.Lookup(doc, target)?.GetDefinition()?.Name;

    /// <summary>
    /// The shared parameters Mirror Check may write instead of its own: bound to instances, shared
    /// (a GUID, so it is the same parameter in every model), and text. Alphabetical; ours left out.
    /// </summary>
    public static List<MirrorTarget> Candidates(Document doc)
    {
        var list = new List<MirrorTarget>();
        DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
        {
            if (it.Current is not InstanceBinding b || it.Key is not InternalDefinition def) continue;
            if (doc.GetElement(def.Id) is not SharedParameterElement sp || sp.GuidValue == Guid) continue;
            try { if (def.GetDataType() != SpecTypeId.String.Text) continue; } catch { continue; }
            bool varies;
            try { varies = def.VariesAcrossGroups; } catch { varies = false; }
            list.Add(new MirrorTarget { Name = def.Name, Guid = sp.GuidValue.ToString(), Categories = b.Categories.Size, Varies = varies });
        }
        return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Makes a parameter of the user's own cover what KH_Mirror State covers: adds every model
    /// category it lacks (keeping the ones it has, its parameter group and its values) and lets it
    /// vary between group instances. Needs no shared parameter file: the model's own definition is
    /// re-bound. Call OUTSIDE a transaction; returns why it could not, or null. Does nothing when
    /// the parameter already covers everything.
    /// </summary>
    public static string? Extend(Document doc, Guid target)
    {
        State before = Read(doc, target);
        if (!before.Present) return "That parameter is no longer in this model.";
        if (before.MissingCategories == 0 && !before.NotVarying) return null;
        if (doc.IsReadOnly) return "The model is read-only.";
        try
        {
            SharedParameterElement sp = SharedParameterElement.Lookup(doc, target)!;
            InternalDefinition def = sp.GetDefinition();
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            InstanceBinding? current = null;
            while (it.MoveNext())
                if (it.Key is InternalDefinition d && d.Id == def.Id && it.Current is InstanceBinding b) { current = b; break; }
            if (current == null) return $"'{def.Name}' is not bound to instances in this model.";

            using var t = new Transaction(doc, $"Mirror Check: extend '{def.Name}'");
            t.Start();
            if (before.MissingCategories > 0)
            {
                var cats = new CategorySet();
                foreach (Category c in current.Categories) cats.Insert(c);
                foreach (Category c in Categories(doc)) if (!cats.Contains(c)) cats.Insert(c);
                ForgeTypeId group = def.GetGroupTypeId();
                if (!doc.ParameterBindings.ReInsert(def, doc.Application.Create.NewInstanceBinding(cats), group))
                    return $"Revit refused to add categories to '{def.Name}'.";
            }
            if (before.NotVarying)
            {
                try { sp.GetDefinition().SetAllowVaryBetweenGroups(doc, true); }
                catch (Exception ex) { return $"Revit would not let '{def.Name}' vary between group instances: {ex.Message}"; }
            }
            if (t.Commit() != TransactionStatus.Committed) return "Revit did not accept the change.";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
