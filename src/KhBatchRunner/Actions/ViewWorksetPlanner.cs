using Autodesk.Revit.DB;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Actions;

/// <summary>
/// "Which worksets do these 3D views show?" — shared by every action that exports geometry from a
/// view (<see cref="ExportNwcAction"/>, <see cref="ExportIfcDataAction"/>). The reasoning lives on
/// <see cref="ExportNwcAction.SelectWorksetsToOpen"/>: read on the cheap worksets-closed open,
/// union across views and their templates, and every uncertain path answers "all worksets",
/// because under-opening fails silently (the model just looks empty) while over-opening only costs
/// time.
///
/// <para>Lives in Actions\, not Batch\, on purpose: Batch\ is compiled into the controller, which
/// has no Revit API.</para>
/// </summary>
internal static class ViewWorksetPlanner
{
    /// <summary>Every exportable 3D view whose title CONTAINS <paramref name="contains"/>,
    /// case-sensitively (ordinal); blank returns them all. Templates excluded; ordered by name.</summary>
    public static List<View3D> FindViews(Document doc, string contains)
    {
        IEnumerable<View3D> all = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate);

        if (!string.IsNullOrWhiteSpace(contains))
            all = all.Where(v => v.Name.IndexOf(contains, StringComparison.Ordinal) >= 0);

        return all.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
    }

    public static List<WorksetId> AllUserWorksets(Document doc)
    {
        try
        {
            return new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .Select(w => w.Id).ToList();
        }
        catch { return new List<WorksetId>(); }
    }

    /// <summary>
    /// Does <paramref name="view"/> show <paramref name="w"/>? The view's answer and its template's,
    /// handed to <see cref="WorksetVisibilityRule"/>. Unreadable means YES.
    /// </summary>
    public static bool ShowsWorkset(Document doc, View3D view, Workset w)
    {
        try
        {
            bool? fromView = Answer(view, w);

            ElementId templateId = view.ViewTemplateId;
            // Autodesk.Revit.DB.View spelled out: System.Windows.Forms.View is an ENUM of the same
            // name, and this assembly references both.
            bool? fromTemplate = templateId != ElementId.InvalidElementId &&
                                 doc.GetElement(templateId) is Autodesk.Revit.DB.View t
                ? Answer(t, w)
                : (bool?)false;   // no template: nothing extra to union in

            return WorksetVisibilityRule.Shows(w.IsVisibleByDefault, fromView, fromTemplate);
        }
        catch { return true; }

        // Visible / Hidden / UseGlobalSetting → true / false / null.
        static bool? Answer(Autodesk.Revit.DB.View v, Workset ws) => v.GetWorksetVisibility(ws.Id) switch
        {
            WorksetVisibility.Visible => true,
            WorksetVisibility.Hidden => false,
            _ => null,
        };
    }

    /// <summary>
    /// The user worksets <paramref name="views"/> show, minus <paramref name="skippable"/> (worksets
    /// proven to hold only links, when links are not exported). Returns null when the views show
    /// nothing, so the caller falls back to every workset rather than opening none.
    /// </summary>
    public static List<WorksetId>? VisibleWorksets(Document doc, IReadOnlyCollection<View3D> views,
                                                   ISet<string> skippable, out int skipped)
    {
        skipped = 0;
        var wanted = new List<WorksetId>();
        foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
        {
            if (!views.Any(v => ShowsWorkset(doc, v, w))) continue;
            if (skippable.Contains(w.Name)) { skipped++; continue; }
            wanted.Add(w.Id);
        }
        return wanted.Count == 0 ? null : wanted;
    }

    /// <summary>Worksets proven link-only for the model being processed (see
    /// <see cref="WorksetAudit"/>); empty when the engine has not published which model this is.</summary>
    public static HashSet<string> LinkOnlyWorksets()
    {
        string key = ModelContext.CurrentKey;
        return string.IsNullOrEmpty(key)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : WorksetAudit.LinkOnlyWorksets(key);
    }
}
