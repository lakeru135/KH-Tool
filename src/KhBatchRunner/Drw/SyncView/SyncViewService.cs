using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.SyncView;

/// <summary>
/// DRW Helper's Setting → Sync View: while on, the view you switch TO opens at the same zoom and
/// position as the view you switched FROM — pyRevit's "Sync Views" behaviour.
///
/// <para><b>Which pairs are synced.</b> Only views whose model coordinates mean the same thing on
/// screen: plan to plan (floor, ceiling, structural, area), and section/elevation/detail to
/// section/elevation/detail looking the SAME way. A plan's zoom rectangle applied to a section
/// would frame an arbitrary patch of the section, so those switches are left alone. The same rules
/// hold in the Family Editor: Ref. Level plan to ceiling plan, Front to another view facing front.</para>
///
/// <para><b>Why it cannot run in batch.</b> It is event-driven UI behaviour: it reacts to a person
/// changing views in an interactive session, and a background batch has no views on screen.</para>
///
/// <para>The handler is attached once at startup and returns immediately while the toggle is off,
/// so an off Sync View costs one bool read per view switch.</para>
/// </summary>
internal static class SyncViewService
{
    /// <summary>Whether Sync View is on. Off at every Revit start — it is a per-session choice.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>The ribbon button, so the toggle can say whether it is on. Set by <see cref="App"/>.</summary>
    public static PushButton? Button { get; set; }

    private const string OnText = "Sync View\nON";
    private const string OffText = "Sync View\nOFF";

    public static string CaptionFor(bool enabled) => enabled ? OnText : OffText;

    public static bool Toggle()
    {
        Enabled = !Enabled;
        if (Button != null) Button.ItemText = CaptionFor(Enabled);
        return Enabled;
    }

    public static void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        if (!Enabled) return;
        try
        {
            View? from = e.PreviousActiveView;
            View to = e.CurrentActiveView;
            if (from == null || to == null || from.Id == to.Id) return;
            // Cross-document switches carry coordinates from another model's space.
            if (!from.Document.Equals(to.Document)) return;
            if (!Compatible(from, to)) return;

            // From the document itself rather than the event's sender, which Revit does not
            // promise to be a UIApplication for this event.
            IList<UIView> open = new UIDocument(to.Document).GetOpenUIViews();
            UIView? fromUi = open.FirstOrDefault(v => v.ViewId == from.Id);
            UIView? toUi = open.FirstOrDefault(v => v.ViewId == to.Id);
            // The previous view is gone when the switch happened because it was closed.
            if (fromUi == null || toUi == null) return;

            IList<XYZ> corners = fromUi.GetZoomCorners();
            if (corners.Count < 2) return;
            toUi.ZoomAndCenterRectangle(corners[0], corners[1]);
        }
        catch
        {
            // A zoom that cannot be applied must never interrupt the person switching views.
        }
    }

    private static bool Compatible(View a, View b)
    {
        if (IsPlan(a) && IsPlan(b)) return true;
        if (IsVertical(a) && IsVertical(b))
            return a.ViewDirection.IsAlmostEqualTo(b.ViewDirection);
        return false;
    }

    private static bool IsPlan(View v) => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan
        or ViewType.EngineeringPlan or ViewType.AreaPlan;

    private static bool IsVertical(View v) => v.ViewType is ViewType.Section or ViewType.Elevation
        or ViewType.Detail;
}
