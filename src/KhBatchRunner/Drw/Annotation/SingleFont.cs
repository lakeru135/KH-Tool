using System.Drawing.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Common;

namespace KhBatchRunner.Drw.Annotation;

/// <summary>A Single Font preset: the font, which groups of elements it reaches, and how.</summary>
public sealed class SingleFontPreset : IDrwPreset
{
    public string Name { get; set; } = "Default";
    public string FontName { get; set; } = "Arial";
    public bool TextTypes { get; set; } = true;
    public bool DimensionTypes { get; set; } = true;
    public bool ModelTextTypes { get; set; } = true;
    public bool ScheduleFonts { get; set; } = true;
    public bool TagFamilies { get; set; } = true;
    public bool TitleBlocks { get; set; } = true;
    public bool HeadFamilies { get; set; } = true;
    public bool OtherAnnotationFamilies { get; set; } = true;
    /// <summary>Model and detail families that contain text (door symbols, detail items…). Off by
    /// default: a model has hundreds of them and opening each is slow.</summary>
    public bool ModelFamilies { get; set; }
    /// <summary>"2.5mm Arial" becomes "2.5mm Tahoma" when the font changes from Arial to Tahoma.</summary>
    public bool RenameTypes { get; set; } = true;
    /// <summary>Report what would change; change nothing.</summary>
    public bool DryRun { get; set; }
}

public static class SingleFontStore
{
    public static readonly DrwPresetStore<SingleFontPreset> Store = new("SingleFontPresets");
}

/// <summary>The groups of loaded families Single Font can open, as its tick boxes name them.</summary>
public enum FontFamilyGroup { Tags, TitleBlocks, Heads, OtherAnnotation, Model }

/// <summary>
/// Single Font: one font for every text-bearing type in the model — the project's text, dimension,
/// spot and model-text types, schedule cell fonts, and the text and labels inside loaded families
/// (tags, title blocks, grid/level/section heads, view titles, other annotation symbols; model
/// families on request). Type names that carry the old font are renamed to the new one.
///
/// <para>Families are opened with <c>EditFamily</c>, their text types changed, nested annotation
/// families handled the same way (up to <see cref="MaxDepth"/> deep), and loaded back — only when
/// something changed. <c>EditFamily</c> and <c>LoadFamily</c> refuse to run inside an open
/// transaction, so project edits and family reloads take turns inside one TransactionGroup.</para>
///
/// <para>Every change is read back before its row says Changed (a commit is not proof of the edit).</para>
/// </summary>
internal static class SingleFontEngine
{
    public static readonly IReadOnlyList<string> Columns = new[] { "Element", "Kind", "From", "To", "Status", "Note" };

    private const int MaxDepth = 3;

    // Category groups for families. Names are resolved at run time so a category missing in some
    // Revit year never breaks the build.
    private static readonly string[] HeadCategoryNames =
    {
        "OST_GridHeads", "OST_LevelHeads", "OST_SectionHeads", "OST_ElevationMarks", "OST_CalloutHeads",
        "OST_ViewportLabel", "OST_SpotElevSymbols", "OST_ReferenceViewerSymbol", "OST_CallOutHeadsSymbol"
    };

    public static bool FontInstalled(string font)
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            return fonts.Families.Any(f => string.Equals(f.Name, font, StringComparison.OrdinalIgnoreCase));
        }
        catch { return true; }   // cannot tell: do not warn
    }

    public static List<string> InstalledFonts()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            return fonts.Families.Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new List<string>(); }
    }

    // ------------------------------------------------------------------ what there is

    /// <summary>
    /// The active document's own types that carry a font, by group. In the Family Editor that is the
    /// open family's types, and any other font-bearing type there — a tag's or title block's label
    /// types among them — counts as a text type, as <see cref="EditFamilyDoc"/> already treats it.
    /// </summary>
    public static List<ElementType> ProjectTypes(Document doc, SingleFontPreset p) =>
        new FilteredElementCollector(doc).WhereElementIsElementType().Cast<Element>().OfType<ElementType>()
            .Where(t => FontParam(t) != null)
            .Where(t => t is TextNoteType ? p.TextTypes : t is DimensionType ? p.DimensionTypes : t is ModelTextType ? p.ModelTextTypes
                : doc.IsFamilyDocument && p.TextTypes)
            .OrderBy(t => t.GetType().Name).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Counts for the dialog's tick boxes.</summary>
    public static Dictionary<string, int> Counts(Document doc)
    {
        var all = new SingleFontPreset { ModelFamilies = true };
        var types = ProjectTypes(doc, all);
        var fams = Families(doc, all);
        return new Dictionary<string, int>
        {
            ["TextTypes"] = types.Count(t => t is not DimensionType and not ModelTextType),
            ["DimensionTypes"] = types.Count(t => t is DimensionType),
            ["ModelTextTypes"] = types.Count(t => t is ModelTextType),
            ["ScheduleFonts"] = Schedules(doc).Count,
            ["TagFamilies"] = fams.Count(f => f.Group == FontFamilyGroup.Tags),
            ["TitleBlocks"] = fams.Count(f => f.Group == FontFamilyGroup.TitleBlocks),
            ["HeadFamilies"] = fams.Count(f => f.Group == FontFamilyGroup.Heads),
            ["OtherAnnotationFamilies"] = fams.Count(f => f.Group == FontFamilyGroup.OtherAnnotation),
            ["ModelFamilies"] = fams.Count(f => f.Group == FontFamilyGroup.Model),
        };
    }

    public static List<ViewSchedule> Schedules(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule && !v.IsInternalKeynoteSchedule)
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Loaded, editable families in the ticked groups.</summary>
    public static List<(Family Family, FontFamilyGroup Group)> Families(Document doc, SingleFontPreset p)
    {
        var heads = new HashSet<long>(HeadCategoryNames
            .Select(n => Enum.TryParse(n, out BuiltInCategory b) ? (long?)(long)b : null)
            .Where(x => x != null).Select(x => x!.Value));
        var list = new List<(Family, FontFamilyGroup)>();
        foreach (Family f in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
        {
            if (IsOwnerFamily(doc, f)) continue;   // in the Family Editor: the family being edited
            Category? cat;
            try { cat = f.FamilyCategory; } catch { cat = null; }
            if (cat == null || f.IsInPlace || !f.IsEditable) continue;
            long id = cat.Id.Value;
            FontFamilyGroup g =
                id == (long)BuiltInCategory.OST_TitleBlocks ? FontFamilyGroup.TitleBlocks
                : heads.Contains(id) ? FontFamilyGroup.Heads
                : cat.CategoryType == CategoryType.Annotation && Enum.GetName(typeof(BuiltInCategory), (BuiltInCategory)id)?.EndsWith("Tags") == true
                    ? FontFamilyGroup.Tags
                : cat.CategoryType == CategoryType.Annotation ? FontFamilyGroup.OtherAnnotation
                : FontFamilyGroup.Model;
            bool wanted = g switch
            {
                FontFamilyGroup.Tags => p.TagFamilies,
                FontFamilyGroup.TitleBlocks => p.TitleBlocks,
                FontFamilyGroup.Heads => p.HeadFamilies,
                FontFamilyGroup.OtherAnnotation => p.OtherAnnotationFamilies,
                _ => p.ModelFamilies
            };
            if (wanted) list.Add((f, g));
        }
        return list.OrderBy(x => x.Item2).ThenBy(x => x.Item1.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Whether <paramref name="f"/> is the family a family document IS, rather than one nested in it.
    /// Its types are the document's own (<see cref="ProjectTypes"/>); EditFamily on it has nothing to open.
    /// </summary>
    private static bool IsOwnerFamily(Document doc, Family f)
    {
        if (!doc.IsFamilyDocument) return false;
        try { return doc.OwnerFamily?.Id == f.Id; }
        catch { return false; }
    }

    private static Parameter? FontParam(Element e)
    {
        Parameter? p = e.get_Parameter(BuiltInParameter.TEXT_FONT);
        return p != null && p.StorageType == StorageType.String ? p : null;
    }

    private static string Kind(ElementType t) =>
        t is TextNoteType ? "Text type" : t is DimensionType ? "Dimension type" : t is ModelTextType ? "Model text type" : "Text type";

    // ------------------------------------------------------------------ the run

    public static List<IReadOnlyDictionary<string, string>> Run(Document doc, SingleFontPreset p)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        string font = (p.FontName ?? "").Trim();
        if (font.Length == 0)
        {
            rows.Add(DrwRows.Row(Columns, "", "Setup", "", "", "Failed", "No font chosen"));
            return rows;
        }
        if (!FontInstalled(font))
            rows.Add(DrwRows.Row(Columns, "", "Setup", "", font, "Warning",
                $"'{font}' is not installed on this PC — Revit keeps the name, but text shows in a substitute font here"));

        using var group = new TransactionGroup(doc, "DRW Single Font");
        group.Start();
        try
        {
            ProjectTypesPass(doc, p, font, rows);
            if (p.ScheduleFonts) SchedulesPass(doc, p, font, rows);
            // Ids and names now, the Family objects later: reloading one family can replace another
            // (a shared nested family), and reading a stale object threw and ended the whole run.
            var families = Families(doc, p).Select(x => (x.Family.Id, x.Family.Name, x.Family.FamilyCategoryId, x.Group)).ToList();
            foreach ((ElementId id, string name, ElementId catId, FontFamilyGroup g) in families)
            {
                Family? family = doc.GetElement(id) as Family
                                 ?? new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                                     .FirstOrDefault(f => f.Name == name && f.FamilyCategoryId == catId);
                rows.Add(family == null
                    ? DrwRows.Row(Columns, name, GroupName(g), "", font, "Skipped", "Replaced while another family was reloaded — run again to check it")
                    : FamilyPass(doc, family, name, g, p, font));
            }
            group.Assimilate();
        }
        catch
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            throw;
        }
        return rows;
    }

    private static void ProjectTypesPass(Document doc, SingleFontPreset p, string font, List<IReadOnlyDictionary<string, string>> rows)
    {
        List<ElementType> types = ProjectTypes(doc, p);
        if (types.Count == 0) return;
        Transaction? t = p.DryRun ? null : DrwStrictFailures.Start(doc, "Single Font — types", out _);
        try
        {
            foreach (ElementType type in types)
                rows.Add(ChangeType(doc, type, font, p, Kind(type), type.Name));
            t?.Commit();
        }
        finally { t?.Dispose(); }
    }

    /// <summary>One type: its font, and its name when that carries the old font. Read back after.</summary>
    private static IReadOnlyDictionary<string, string> ChangeType(Document doc, ElementType type, string font,
        SingleFontPreset p, string kind, string label)
    {
        Parameter fp = FontParam(type)!;
        string old = fp.AsString() ?? "";
        string newName = p.RenameTypes ? RenamedFor(type.Name, old, font) : type.Name;
        bool fontChange = !string.Equals(old, font, StringComparison.Ordinal);
        bool nameChange = !string.Equals(newName, type.Name, StringComparison.Ordinal);
        if (!fontChange && !nameChange)
            return DrwRows.Row(Columns, label, kind, old, font, "Unchanged", "");
        string? borrowed = BorrowedBy(doc, type.Id);
        if (borrowed != null)
            return DrwRows.Row(Columns, label, kind, old, font, "Skipped", $"Borrowed by {borrowed} — ask them to relinquish it");
        if (p.DryRun)
            return DrwRows.Row(Columns, label, kind, old, font, "Would change", nameChange ? $"renamed to \"{newName}\"" : "");

        var notes = new List<string>();
        try
        {
            if (fontChange && fp.IsReadOnly) return DrwRows.Row(Columns, label, kind, old, font, "Failed", "Its font is read-only");
            if (fontChange) fp.Set(font);
            if (nameChange)
            {
                // Checked first, and read back after: Revit let a second text type take a name
                // already in use, and the row claimed a rename the clash made meaningless.
                if (NameTaken(type.Document, type, newName))
                    notes.Add($"not renamed: \"{newName}\" is already another type's name — kept its name");
                else
                {
                    try { type.Name = newName; } catch { /* reported below from the read-back */ }
                    notes.Add(type.Name == newName ? $"renamed to \"{newName}\"" : $"not renamed to \"{newName}\" — kept its name");
                }
            }
        }
        catch (Exception ex) { return DrwRows.Row(Columns, label, kind, old, font, "Failed", ex.Message); }
        string now = FontParam(type)?.AsString() ?? "";
        return string.Equals(now, font, StringComparison.Ordinal)
            ? DrwRows.Row(Columns, label, kind, old, font, "Changed", string.Join("; ", notes))
            : DrwRows.Row(Columns, label, kind, old, font, "Failed", $"Revit kept '{now}'");
    }

    /// <summary>"2.5mm Arial" → "2.5mm Tahoma" (case-insensitive), or the name unchanged.</summary>
    public static string RenamedFor(string name, string oldFont, string newFont)
    {
        if (string.IsNullOrWhiteSpace(oldFont) || string.Equals(oldFont, newFont, StringComparison.OrdinalIgnoreCase)) return name;
        // Bounded by non-letters, so "Arial" is not found inside "Arialx"; "Arial Narrow" in a name
        // whose font is Arial Narrow is matched as a whole, since the old font is the full name.
        return Regex.Replace(name, @"(?<![\p{L}\p{N}])" + Regex.Escape(oldFont) + @"(?![\p{L}\p{N}])", newFont, RegexOptions.IgnoreCase);
    }

    /// <summary>Another type of the same kind and family already has <paramref name="name"/>.</summary>
    private static bool NameTaken(Document doc, ElementType type, string name) =>
        new FilteredElementCollector(doc).WhereElementIsElementType().Cast<Element>().OfType<ElementType>()
            .Any(o => o.Id != type.Id && o.GetType() == type.GetType() && o.FamilyName == type.FamilyName &&
                      string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string? BorrowedBy(Document doc, ElementId id)
    {
        if (!doc.IsWorkshared) return null;
        try
        {
            return WorksharingUtils.GetCheckoutStatus(doc, id, out string owner) == CheckoutStatus.OwnedByOtherUser ? owner : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ schedules

    private static void SchedulesPass(Document doc, SingleFontPreset p, string font, List<IReadOnlyDictionary<string, string>> rows)
    {
        Transaction? t = p.DryRun ? null : DrwStrictFailures.Start(doc, "Single Font — schedules", out _);
        try
        {
            foreach (ViewSchedule vs in Schedules(doc))
            {
                int changed = 0, total = 0;
                var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    // Column (field) styles: the body's per-column font overrides.
                    ScheduleDefinition def = vs.Definition;
                    for (int i = 0; i < def.GetFieldCount(); i++)
                    {
                        ScheduleField field = def.GetField(i);
                        TableCellStyle style = field.GetStyle();
                        if (!OverridesFont(style)) continue;
                        total++;
                        if (string.Equals(style.FontName, font, StringComparison.Ordinal)) continue;
                        fonts.Add(style.FontName);
                        changed++;
                        if (!p.DryRun) { style.FontName = font; field.SetStyle(style); }
                    }
                    // Header cells (title and column headings) with their own font.
                    TableSectionData header = vs.GetTableData().GetSectionData(SectionType.Header);
                    for (int r = header.FirstRowNumber; r <= header.LastRowNumber; r++)
                        for (int c = header.FirstColumnNumber; c <= header.LastColumnNumber; c++)
                        {
                            TableCellStyle style = header.GetTableCellStyle(r, c);
                            if (!OverridesFont(style)) continue;
                            total++;
                            if (string.Equals(style.FontName, font, StringComparison.Ordinal)) continue;
                            fonts.Add(style.FontName);
                            changed++;
                            if (!p.DryRun) header.SetCellStyle(r, c, style.FontNameReplaced(font));
                        }
                }
                catch (Exception ex)
                {
                    rows.Add(DrwRows.Row(Columns, vs.Name, "Schedule", "", font, "Failed", ex.Message));
                    continue;
                }
                if (total == 0) continue;   // no cell overrides its font: its text types decide
                string status = changed == 0 ? "Unchanged" : p.DryRun ? "Would change" : "Changed";
                rows.Add(DrwRows.Row(Columns, vs.Name, "Schedule", string.Join(", ", fonts), font, status,
                    changed == 0 ? "" : $"{changed} of {total} cell/column font override(s)"));
            }
            t?.Commit();
        }
        finally { t?.Dispose(); }
    }

    private static bool OverridesFont(TableCellStyle style)
    {
        try { return style.GetCellStyleOverrideOptions().Font; }
        catch { return false; }
    }

    private static TableCellStyle FontNameReplaced(this TableCellStyle style, string font)
    {
        style.FontName = font;
        return style;
    }

    // ------------------------------------------------------------------ families

    private sealed class OverwriteOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;   // keep the project's parameter values
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return true;
        }
    }

    private static string GroupName(FontFamilyGroup g) => g switch
    {
        FontFamilyGroup.Tags => "Tag family",
        FontFamilyGroup.TitleBlocks => "Title block",
        FontFamilyGroup.Heads => "Head / view title family",
        FontFamilyGroup.OtherAnnotation => "Annotation family",
        _ => "Model / detail family"
    };

    private static IReadOnlyDictionary<string, string> FamilyPass(Document doc, Family family, string name, FontFamilyGroup g,
        SingleFontPreset p, string font)
    {
        string kind = GroupName(g);
        string? borrowed = BorrowedBy(doc, family.Id);
        if (borrowed != null)
            return DrwRows.Row(Columns, name, kind, "", font, "Skipped", $"Borrowed by {borrowed} — ask them to relinquish it");
        Document? famDoc = null;
        try
        {
            famDoc = doc.EditFamily(family);
            var olds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var notes = new List<string>();
            int changed = EditFamilyDoc(famDoc, p, font, olds, notes, 1);
            if (changed == 0)
                return DrwRows.Row(Columns, name, kind, "", font, "Unchanged", string.Join("; ", notes));
            if (p.DryRun)
                return DrwRows.Row(Columns, name, kind, string.Join(", ", olds), font, "Would change",
                    Join($"{changed} text type(s)", notes));
            famDoc.LoadFamily(doc, new OverwriteOptions());
            return DrwRows.Row(Columns, name, kind, string.Join(", ", olds), font, "Changed",
                Join($"{changed} text type(s), family reloaded", notes));
        }
        catch (Exception ex)
        {
            return DrwRows.Row(Columns, name, kind, "", font, "Failed", ex.Message);
        }
        finally
        {
            try { famDoc?.Close(false); } catch { /* the edit copy is discarded either way */ }
        }
    }

    /// <summary>
    /// Changes every font-bearing type in a family document, then its nested annotation families
    /// (edited and loaded back into this one). Returns how many types changed (or would), in total.
    /// </summary>
    private static int EditFamilyDoc(Document famDoc, SingleFontPreset p, string font, HashSet<string> olds,
        List<string> notes, int depth)
    {
        int changed = 0;
        var types = new FilteredElementCollector(famDoc).WhereElementIsElementType().Cast<Element>()
            .OfType<ElementType>().Where(t => FontParam(t) != null).ToList();
        var pending = types.Where(t => !string.Equals(FontParam(t)!.AsString(), font, StringComparison.Ordinal) ||
                                       (p.RenameTypes && RenamedFor(t.Name, FontParam(t)!.AsString() ?? "", font) != t.Name)).ToList();
        if (pending.Count > 0)
        {
            foreach (ElementType t in pending) olds.Add(FontParam(t)!.AsString() ?? "");
            changed += pending.Count;
            if (!p.DryRun)
            {
                using var tx = new Transaction(famDoc, "Single Font");
                tx.Start();
                foreach (ElementType t in pending)
                {
                    Parameter fp = FontParam(t)!;
                    string old = fp.AsString() ?? "";
                    if (!fp.IsReadOnly && old != font) fp.Set(font);
                    if (p.RenameTypes)
                    {
                        string newName = RenamedFor(t.Name, old, font);
                        if (newName != t.Name)
                        {
                            if (NameTaken(famDoc, t, newName)) notes.Add($"type \"{t.Name}\" kept its name (\"{newName}\" is taken)");
                            else
                            {
                                try { t.Name = newName; } catch { /* checked below */ }
                                if (t.Name != newName) notes.Add($"type \"{t.Name}\" kept its name");
                            }
                        }
                    }
                }
                tx.Commit();
                int stuck = pending.Count(t => !string.Equals(FontParam(t)?.AsString(), font, StringComparison.Ordinal));
                if (stuck > 0) notes.Add($"{stuck} type(s) kept their old font");
            }
        }

        if (depth >= MaxDepth) return changed;
        // Ids and names first, as at the top level: loading one nested family back into its parent
        // replaces the nested families it shares with its siblings, so Family objects collected
        // before it go stale ("The referenced object is not valid", 5 of Snowdon's 52 families).
        var nestedIds = new FilteredElementCollector(famDoc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => !IsOwnerFamily(famDoc, f)).Select(f => (f.Id, f.Name)).ToList();
        foreach ((ElementId id, string name) in nestedIds)
        {
            Document? nestedDoc = null;
            try
            {
                if (famDoc.GetElement(id) is not Family nested || !nested.IsValidObject)
                    nested = new FilteredElementCollector(famDoc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == name)!;
                if (nested == null) continue;   // folded into a sibling's reload
                Category? cat = nested.FamilyCategory;
                if (cat == null || nested.IsInPlace || !nested.IsEditable) continue;
                if (cat.CategoryType != CategoryType.Annotation && !p.ModelFamilies) continue;
                nestedDoc = famDoc.EditFamily(nested);
                int n = EditFamilyDoc(nestedDoc, p, font, olds, notes, depth + 1);
                if (n > 0)
                {
                    changed += n;
                    if (!p.DryRun) nestedDoc.LoadFamily(famDoc, new OverwriteOptions());
                    notes.Add($"nested \"{name}\"");
                }
            }
            catch (Exception ex) { notes.Add($"nested \"{name}\" not changed: {ex.Message}"); }
            finally { try { nestedDoc?.Close(false); } catch { /* discarded */ } }
        }
        return changed;
    }

    private static string Join(string first, List<string> more) =>
        string.Join("; ", new[] { first }.Concat(more).Where(s => !string.IsNullOrEmpty(s)));
}
