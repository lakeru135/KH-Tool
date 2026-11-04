using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// Keeps a model's Parameter Helper rules INSIDE the model, so everyone working on a central model
/// runs the same rules. Revit Extensible Storage: one schema (fixed GUID, vendor KANGHUN, readable
/// by anyone, writable only by this add-in) with the rule set as JSON, on ONE DataStorage element of
/// its own.
///
/// <para>Not on Project Information: everyone edits that, so saving rules there would borrow it
/// from the whole team. Changing the rules borrows only this element; other people receive them
/// when they Sync or Reload Latest. Someone without the add-in never sees it and gets no warning.</para>
///
/// <para>The JSON carries its own version, so new rule features do not need a new schema GUID —
/// only a change that breaks the two fields would.</para>
/// </summary>
internal static class RuleStore
{
    public static readonly Guid SchemaGuid = new("6b1f4c2e-9a7d-4e35-b0c8-2d7e5f9a3c41");
    private const string SchemaName = "KhParameterHelperRules";
    private const string Vendor = "KANGHUN";
    private const string VersionField = "Version";
    private const string JsonField = "RulesJson";
    public const string StorageName = "KH Parameter Helper rules";

    private static Schema GetSchema()
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s != null) return s;
        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName(SchemaName);
        b.SetVendorId(Vendor);
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Vendor);
        b.SetDocumentation("KH-Tool Parameter Helper: formula rules, as JSON.");
        b.AddSimpleField(VersionField, typeof(int));
        b.AddSimpleField(JsonField, typeof(string));
        return b.Finish();
    }

    /// <summary>
    /// Every element holding rules, oldest first. Normally one. Two appear when two people each
    /// created the FIRST rules in their own local before either synced — neither could see the
    /// other's — and both synced. <see cref="Load"/> merges them; <see cref="Save"/> folds them back
    /// into the oldest.
    /// </summary>
    public static List<DataStorage> FindAll(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(DataStorage))
            .WherePasses(new ExtensibleStorageFilter(SchemaGuid))
            .Cast<DataStorage>()
            .OrderBy(d => d.Id.Value)
            .ToList();

    /// <summary>The element the rules are saved in (the oldest), or null when this model has none yet.</summary>
    public static DataStorage? Find(Document doc) => FindAll(doc).FirstOrDefault();

    private static FormulaRuleSet? Read(DataStorage ds)
    {
        Entity e = ds.GetEntity(GetSchema());
        if (!e.IsValid()) return null;
        string json = e.Get<string>(JsonField) ?? "";
        if (json.Length == 0) return null;
        try { return FormulaRuleSet.FromJson(json); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The Parameter Helper rules saved in this model could not be read: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// The model's rules. When two storage elements exist (see <see cref="FindAll"/>), the oldest's
    /// rules come first and every rule of the others that it lacks (by rule id) is added after them,
    /// so nobody's rules silently disappear; the oldest's Live setting stands.
    /// </summary>
    public static FormulaRuleSet Load(Document doc)
    {
        FormulaRuleSet? merged = null;
        foreach (DataStorage ds in FindAll(doc))
        {
            FormulaRuleSet? set = Read(ds);
            if (set == null) continue;
            if (merged == null) { merged = set; continue; }
            var ids = new HashSet<string>(merged.Rules.Select(r => r.Id), StringComparer.Ordinal);
            merged.Rules.AddRange(set.Rules.Where(r => ids.Add(r.Id)));
        }
        return merged ?? new FormulaRuleSet();
    }

    /// <summary>
    /// Why this user cannot change the rules right now, or null if they can. Workshared only: the
    /// element is someone else's, or it changed in central since this local was last updated.
    /// Asks central (CheckoutElements) rather than trusting the cached status — the remarks on
    /// WorksharingUtils say the cache is for display, not for deciding whether an edit will work.
    /// Call OUTSIDE a transaction.
    /// </summary>
    public static string? WhyNotEditable(Document doc)
    {
        if (!doc.IsWorkshared) return null;
        List<DataStorage> all = FindAll(doc);
        if (all.Count == 0) return null;   // a new element is ours by definition
        try
        {
            // All of them: saving folds the extra ones into the oldest and deletes them.
            var ids = new HashSet<ElementId>(all.Select(d => d.Id));
            ISet<ElementId> owned = WorksharingUtils.CheckoutElements(doc, ids, null);
            foreach (DataStorage ds in all)
            {
                if (!owned.Contains(ds.Id))
                {
                    WorksharingUtils.GetCheckoutStatus(doc, ds.Id, out string owner);
                    return $"The rules are being edited by {(string.IsNullOrEmpty(owner) ? "another user" : owner)}. " +
                           "They can be changed once that person syncs and relinquishes them.";
                }
                ModelUpdatesStatus st = WorksharingUtils.GetModelUpdatesStatus(doc, ds.Id);
                if (st == ModelUpdatesStatus.UpdatedInCentral || st == ModelUpdatesStatus.DeletedInCentral)
                    return "The rules were changed in central since this model was last updated. Reload Latest first.";
            }
            return null;
        }
        catch (Exception ex)
        {
            return "Central could not be asked whether the rules are free: " + ex.Message;
        }
    }

    /// <summary>
    /// Whether the rules in the model differ from <paramref name="openedJson"/>, the rules as they
    /// were when someone opened the editor — i.e. someone else's change arrived (Reload Latest,
    /// Sync) or was made while the window was open.
    /// </summary>
    public static bool ChangedSince(Document doc, string openedJson) =>
        !string.Equals(Load(doc).ToJson(), openedJson, StringComparison.Ordinal);

    /// <summary>Writes the rule set into the model. Call INSIDE a transaction.</summary>
    public static void Save(Document doc, FormulaRuleSet set)
    {
        Schema schema = GetSchema();
        List<DataStorage> all = FindAll(doc);
        DataStorage ds = all.FirstOrDefault() ?? DataStorage.Create(doc);
        // The set being saved was loaded MERGED (Load), so the extra elements' rules are in it already.
        foreach (DataStorage extra in all.Skip(1)) doc.Delete(extra.Id);
        try { if (ds.Name != StorageName) ds.Name = StorageName; } catch { /* a name is a courtesy */ }
        var e = new Entity(schema);
        e.Set(VersionField, FormulaRuleSet.CurrentVersion);
        e.Set(JsonField, set.ToJson());
        ds.SetEntity(e);
    }
}
