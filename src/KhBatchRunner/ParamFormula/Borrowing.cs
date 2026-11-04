using Autodesk.Revit.DB;

namespace KhBatchRunner.ParamFormula;

/// <summary>
/// Borrows, through central, exactly the elements a write is about to change, and drops the ones
/// that cannot be edited now. Shared by Parameter Helper's Apply and Mirror Check's Check All Now.
/// </summary>
internal static class Borrowing
{
    /// <summary>
    /// Asks central for the elements of <paramref name="plan"/> — only those, in one call, OUTSIDE any
    /// transaction — and returns the items that can be written. Each one left out is passed to
    /// <paramref name="skip"/> with the reason: owned by someone else, changed in central since this
    /// local was updated, or central could not be reached. Revit may borrow a whole group to borrow
    /// one member.
    /// </summary>
    public static List<T> Keep<T>(Document doc, List<T> plan, Func<T, ElementId> id, Action<T, string> skip)
    {
        if (!doc.IsWorkshared) return plan;
        var want = new HashSet<ElementId>(plan.Select(id));
        ISet<ElementId> owned;
        try
        {
            owned = WorksharingUtils.CheckoutElements(doc, want, null);
        }
        catch (Exception ex)
        {
            foreach (T p in plan) skip(p, "Central could not be reached to borrow it: " + ex.Message);
            return new List<T>();
        }

        var ok = new List<T>();
        foreach (T p in plan)
        {
            ElementId e = id(p);
            if (!owned.Contains(e))
            {
                WorksharingUtils.GetCheckoutStatus(doc, e, out string owner);
                skip(p, $"Owned by {(string.IsNullOrEmpty(owner) ? "another user" : owner)} — runs again once it is free");
                continue;
            }
            ModelUpdatesStatus st = WorksharingUtils.GetModelUpdatesStatus(doc, e);
            if (st == ModelUpdatesStatus.UpdatedInCentral || st == ModelUpdatesStatus.DeletedInCentral)
            {
                skip(p, "Changed in central since this model was updated — Reload Latest, then apply again");
                continue;
            }
            ok.Add(p);
        }
        return ok;
    }
}
