using System.Globalization;
using Autodesk.Revit.DB;

namespace KhBatchRunner.Export;

/// <summary>
/// Where the Project Base Point is, read once per model: its internal position, and its position in
/// the active site's shared coordinates. Lengths are Revit internal feet; <see cref="Describe"/> is
/// the metre text the log rows use.
/// </summary>
internal sealed class PbpFacts
{
    public XYZ Internal { get; init; } = XYZ.Zero;
    public double SharedEastWest { get; init; }
    public double SharedNorthSouth { get; init; }
    public double SharedElevation { get; init; }
    /// <summary>The active site's angle to true north, radians.</summary>
    public double SharedAngle { get; init; }
    public string SiteName { get; init; } = "";

    /// <summary>The PBP sits on Revit's internal origin, so internal coordinates already ARE the
    /// PBP frame and nothing needs building.</summary>
    public bool AtInternalOrigin => Internal.GetLength() < ProjectBasePointFrame.OriginToleranceFt;

    public string Describe() =>
        string.Format(CultureInfo.InvariantCulture,
            "PBP internal {0:0.000}, {1:0.000}, {2:0.000} m; in site '{3}' E {4:0.000} N {5:0.000} Z {6:0.000} m, angle {7:0.####}°",
            M(Internal.X), M(Internal.Y), M(Internal.Z), SiteName,
            M(SharedEastWest), M(SharedNorthSouth), M(SharedElevation), SharedAngle * 180.0 / Math.PI);

    /// <summary>
    /// How far this model's PBP is from <paramref name="other"/>'s in shared coordinates: the
    /// plan/height distance in metres and the angle difference in degrees. Two models whose PBPs
    /// differ will not line up once each is exported with its own PBP at 0,0,0.
    /// </summary>
    public (double DistanceM, double AngleDeg) DifferenceFrom(PbpFacts other)
    {
        double de = SharedEastWest - other.SharedEastWest;
        double dn = SharedNorthSouth - other.SharedNorthSouth;
        double dz = SharedElevation - other.SharedElevation;
        double da = (SharedAngle - other.SharedAngle) * 180.0 / Math.PI;
        return (M(Math.Sqrt(de * de + dn * dn + dz * dz)), Math.Abs(da));
    }

    private static double M(double ft) => ft * 0.3048;
}

/// <summary>
/// The "Project Base Point at 0,0,0" export frame.
///
/// <para><b>What the frame is.</b> The PBP becomes 0,0,0, project north points up (angle 0), and Z 0
/// is the PBP's elevation. It is the frame of a federation drawn on a local grid whose origin is the
/// PBP — which is neither of the two frames Revit's exporters know (shared/survey, or internal).</para>
///
/// <para><b>NWC — <see cref="ExportThroughTemporarySite"/>.</b> The Navisworks exporter writes either
/// internal or SHARED coordinates, and shared means "the active site". So for the length of one
/// export, the active site is made to BE the PBP frame:</para>
/// <list type="number">
/// <item>Transaction 1 (committed): copy the active site as <see cref="TempSiteName"/>, place the
/// internal origin at shared (−PBP.X, −PBP.Y, −PBP.Z) with angle 0 — which puts the PBP on shared
/// 0,0,0 — and make the copy active.</item>
/// <item>Check that the PBP now reads 0,0,0 in shared coordinates; stop if not.</item>
/// <item>Export with shared coordinates. This has to happen OUTSIDE a transaction, because the NWC
/// exporter silently writes nothing while one is open — which is why step 1 commits.</item>
/// <item>Transaction 2 (committed): make the original site active again and delete the copy.</item>
/// </list>
/// <para>The model ends as it was. On a workshared model the two transactions borrow a few
/// elements (project information, the sites, links placed by shared site); see
/// <see cref="OwnershipSnapshot"/> for how they are given back.</para>
///
/// <para><b>IFC</b> needs none of this: its exporter has a Project-Base-Point coordinate base of its
/// own, and runs inside a rolled-back transaction. See <see cref="IfcWcsPatcher"/> for the one fix
/// its output needs.</para>
///
/// <para>Ported from the team's Dynamo export script, where the method was tested on a cloud model
/// first.</para>
/// </summary>
internal static class ProjectBasePointFrame
{
    /// <summary>Name of the temporary site. Fixed, so a copy left by an interrupted run is
    /// recognisable and removed by the next one.</summary>
    public const string TempSiteName = "KH_TEMP_PBP_ORIGIN";

    /// <summary>Within this of internal 0 (feet, ≈0.03 mm) the PBP is treated as being on it.</summary>
    public const double OriginToleranceFt = 1e-4;

    /// <summary>After the temporary site is active, the PBP must read within this of shared 0
    /// (feet, ≈0.3 mm) or the export is refused.</summary>
    private const double CheckToleranceFt = 1e-3;

    public static PbpFacts Read(Document doc)
    {
        XYZ pbp = BasePoint.GetProjectBasePoint(doc).Position;
        ProjectLocation site = doc.ActiveProjectLocation;
        ProjectPosition atPbp = site.GetProjectPosition(pbp);
        return new PbpFacts
        {
            Internal = pbp,
            SharedEastWest = atPbp.EastWest,
            SharedNorthSouth = atPbp.NorthSouth,
            SharedElevation = atPbp.Elevation,
            SharedAngle = atPbp.Angle,
            SiteName = site.Name,
        };
    }

    /// <summary>
    /// Runs <paramref name="export"/> with a temporary site active whose shared 0,0,0 is the PBP —
    /// steps 1–4 in the class comment. The original site is made active again however the export
    /// ends. Returns "" when everything was put back, or a sentence saying what could not be (the
    /// caller puts it in the row). Throws if the temporary site cannot be made or checked — before
    /// anything is exported — or if the export itself throws.
    /// </summary>
    public static string ExportThroughTemporarySite(Document doc, PbpFacts pbp, Action export)
    {
        ElementId originalId = doc.ActiveProjectLocation.Id;
        if (doc.ActiveProjectLocation.Name == TempSiteName)
            throw new PbpFrameException(
                $"This model's active site is '{TempSiteName}', left by an interrupted export. Make your " +
                "own site current (Manage › Location › Site) and delete that one, then run again.");

        ElementId tempId = ElementId.InvalidElementId;
        using (Transaction t = QuietFailures.Start(doc, "KH-Tool: temporary PBP site"))
        {
            try
            {
                // A copy left behind by an interrupted run would block the name.
                foreach (ProjectLocation old in doc.ProjectLocations.Cast<ProjectLocation>()
                             .Where(l => l.Name == TempSiteName).ToList())
                    doc.Delete(old.Id);

                ProjectLocation temp = doc.ActiveProjectLocation.Duplicate(TempSiteName);
                temp.SetProjectPosition(XYZ.Zero,
                    new ProjectPosition(-pbp.Internal.X, -pbp.Internal.Y, -pbp.Internal.Z, 0.0));
                doc.ActiveProjectLocation = temp;
                tempId = temp.Id;
                if (t.Commit() != TransactionStatus.Committed)
                    throw new PbpFrameException("Revit did not commit the temporary site, so nothing was exported.");
            }
            catch (PbpFrameException)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                throw;
            }
            catch (Exception ex)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                throw new PbpFrameException(
                    $"The temporary site could not be made ({ex.Message}), so nothing was exported.", ex);
            }
        }

        string restoreNote;
        try
        {
            XYZ shared = BasePoint.GetProjectBasePoint(doc).SharedPosition;
            if (shared.GetLength() > CheckToleranceFt)
                throw new PbpFrameException(string.Format(CultureInfo.InvariantCulture,
                    "The temporary site did not put the PBP on 0,0,0 (it reads {0:0.000}, {1:0.000}, {2:0.000} m), " +
                    "so nothing was exported.", shared.X * 0.3048, shared.Y * 0.3048, shared.Z * 0.3048));
            export();
        }
        catch (Exception ex)
        {
            restoreNote = Restore(doc, originalId, tempId);
            if (restoreNote.Length == 0) throw;
            throw new PbpFrameException(ex.Message + " " + restoreNote, ex);
        }
        return Restore(doc, originalId, tempId);
    }

    /// <summary>
    /// Remembers the first model's PBP in a run and returns a warning for any later model whose PBP
    /// is somewhere else in shared coordinates. Each model is exported with ITS OWN PBP at 0,0,0, so
    /// a model whose PBP was placed differently comes out shifted — silently, unless something says
    /// so. Compares shared coordinates, so it is only meaningful where the models share coordinates.
    /// </summary>
    internal sealed class Agreement
    {
        private const double ToleranceM = 0.005;
        private const double ToleranceDeg = 0.01;
        private string _firstModel = "";
        private PbpFacts? _first;

        public string Check(string model, PbpFacts facts)
        {
            if (_first is null)
            {
                _first = facts;
                _firstModel = model;
                return "";
            }
            (double d, double a) = facts.DifferenceFrom(_first);
            if (d <= ToleranceM && a <= ToleranceDeg) return "";
            return string.Format(CultureInfo.InvariantCulture,
                "WARNING: this PBP is {0:0.000} m and {1:0.####}° away from the PBP of '{2}' (the run's first " +
                "model), so the two files will not line up. Check both models' PBP before using them together.",
                d, a, _firstModel);
        }
    }

    /// <summary>Step 4: the original site active again, the temporary one deleted. Never throws —
    /// a failure becomes the sentence the row carries.</summary>
    private static string Restore(Document doc, ElementId originalId, ElementId tempId)
    {
        using Transaction t = QuietFailures.Start(doc, "KH-Tool: restore site after PBP export");
        try
        {
            if (doc.GetElement(originalId) is ProjectLocation original)
                doc.ActiveProjectLocation = original;
            if (tempId != ElementId.InvalidElementId && doc.GetElement(tempId) != null)
                doc.Delete(tempId);
            if (t.Commit() == TransactionStatus.Committed) return "";
            return RestoreFailed("Revit did not commit the change");
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            return RestoreFailed(ex.Message);
        }
    }

    private static string RestoreFailed(string why) =>
        $"WARNING: the original site could not be made active again ({why}). A batch run closes the " +
        "model without saving, so nothing reaches the central model; on a model you have open, make " +
        $"your own site current (Manage › Location › Site), delete '{TempSiteName}', and do not sync " +
        "before you have.";
}

/// <summary>
/// The PBP frame could not be set up or put back. Deliberately NOT an
/// <see cref="InvalidOperationException"/>: Export NWC retries a view that produced no file as a
/// whole-model export on that type, and a site problem is not a view problem — retrying would fail
/// the same way and hide the real reason.
/// </summary>
internal sealed class PbpFrameException : Exception
{
    public PbpFrameException(string message) : base(message) { }
    public PbpFrameException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The failure handling for the temporary-site transactions: warnings are deleted, and errors take
/// Revit's own default resolution and the transaction commits. That keeps batch and scheduled runs
/// free of dialogs. The error expected here is "Constraints are not satisfied" on a link placed by
/// shared site, raised when the active site changes; the site is put back straight after.
/// </summary>
internal sealed class QuietFailures : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        bool resolved = false;
        foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
        {
            if (f.GetSeverity() == FailureSeverity.Warning)
            {
                accessor.DeleteWarning(f);
            }
            else if (f.HasResolutions())
            {
                accessor.ResolveFailure(f);
                resolved = true;
            }
        }
        return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
    }

    public static Transaction Start(Document doc, string name)
    {
        var t = new Transaction(doc, name);
        FailureHandlingOptions o = t.GetFailureHandlingOptions();
        o.SetFailuresPreprocessor(new QuietFailures());
        o.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(o);
        t.Start();
        return t;
    }
}

/// <summary>
/// What the current user owns in a workshared model that is connected to its central (attached),
/// so that what an export BORROWED can be given back while what the user already owned is kept.
///
/// <para>Matters on "Run on active model": that path never relinquishes, so without this the
/// temporary site's borrowed elements would stay checked out to the user until their next sync. In
/// a batch run the engine relinquishes before it closes the model anyway; doing it here too is
/// harmless and lets the row say what happened.</para>
/// </summary>
internal sealed class OwnershipSnapshot
{
    private readonly HashSet<WorksetId> _worksets;
    private readonly HashSet<ElementId> _elements;

    private OwnershipSnapshot(HashSet<WorksetId> worksets, HashSet<ElementId> elements)
    {
        _worksets = worksets;
        _elements = elements;
    }

    /// <summary>Null when there is nothing to track: not workshared, or detached (no central).</summary>
    public static OwnershipSnapshot? Take(Document doc)
    {
        if (!doc.IsWorkshared || doc.IsDetached) return null;
        (HashSet<WorksetId> ws, HashSet<ElementId> els) = Owned(doc);
        return new OwnershipSnapshot(ws, els);
    }

    /// <summary>
    /// Gives back what was borrowed since <see cref="Take"/>: relinquish everything, then check the
    /// user's own worksets and elements out again. Never throws; returns the row text.
    /// </summary>
    public string ReleaseBorrowed(Document doc)
    {
        try
        {
            (_, HashSet<ElementId> now) = Owned(doc);
            int borrowed = now.Count(id => !_elements.Contains(id));
            if (borrowed == 0) return "Ownership: nothing borrowed.";

            WorksharingUtils.RelinquishOwnership(doc, new RelinquishOptions(true), new TransactWithCentralOptions());
            if (_worksets.Count > 0)
                WorksharingUtils.CheckoutWorksets(doc, _worksets.ToList());
            if (_elements.Count > 0)
                WorksharingUtils.CheckoutElements(doc, _elements.ToList());

            (_, HashSet<ElementId> after) = Owned(doc);
            int left = after.Count(id => !_elements.Contains(id));
            return left == 0
                ? $"Ownership: borrowed {borrowed}, all released; your own {_elements.Count} kept."
                : $"Ownership: borrowed {borrowed}, {left} still owned — they are released at your next sync.";
        }
        catch (Exception ex)
        {
            return $"Ownership could not be released ({ex.Message}); it is released at your next sync.";
        }
    }

    private static (HashSet<WorksetId>, HashSet<ElementId>) Owned(Document doc)
    {
        string user = doc.Application.Username;
        var ws = new HashSet<WorksetId>(new FilteredWorksetCollector(doc)
            .Where(w => string.Equals(w.Owner, user, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Id));

        var els = new HashSet<ElementId>();
        var all = new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true));
        foreach (ElementId id in new FilteredElementCollector(doc).WherePasses(all).ToElementIds())
            if (WorksharingUtils.GetCheckoutStatus(doc, id) == CheckoutStatus.OwnedByCurrentUser)
                els.Add(id);
        return (ws, els);
    }
}
