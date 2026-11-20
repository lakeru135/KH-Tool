using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

/// <summary>
/// Multi Model Check: read the model's geo-positioning — site location (lat/long/place), the named
/// SITES the model carries, angle to true north, and the Project Base Point / Survey Point
/// coordinates. Read-only. Coordinates are reported in metres, angles/lat-long in degrees.
///
/// NAMED SITES, NOT JUST THE PLACE NAME. Until 1.3.5 the only name here was
/// <see cref="SiteLocation.PlaceName"/>, which is the geo/weather place — on real models it reads
/// "&lt;default&gt;" and answers nothing. The names people actually manage live in
/// <see cref="Document.ProjectLocations"/> (Manage → Location → Site), with
/// <see cref="Document.ActiveProjectLocation"/> as the primary one. Both are reported now:
/// PlaceName is kept because it is a different fact, not a worse version of the same one.
///
/// ONE DETAIL ROW PER SITE. Each <see cref="ProjectLocation"/> carries its OWN
/// <see cref="ProjectPosition"/> — true north, east/west, north/south, elevation — so a model with
/// several sites has several different answers to "where is it", and a single row would have to
/// pick one and hide the rest. Document-level facts (lat/long, PBP, Survey) repeat on every row so
/// one CSV still answers everything and filters cleanly, the same shape
/// <see cref="GetRvtLinksWorksetAction"/> uses: <see cref="Columns"/> describes the detail rows,
/// <see cref="Run"/>'s dictionary is the per-model tracking summary.
///
/// PBP / SURVEY ARE SHARED COORDINATES (since 2.1.0.3). <see cref="BasePoint.Position"/> is the
/// point's place relative to the model's INTERNAL origin, so models that share one coordinate
/// system but were modelled with different internal origins reported different PBP numbers for
/// the same physical point. The PBP / Survey columns are now each point run through that row's
/// site (<see cref="ProjectLocation.GetProjectPosition"/>) — the E/W, N/S and Elev Revit shows on
/// the point itself — so correctly coordinated models agree. Because the answer depends on the
/// site, each site row carries its own values; the summary uses the active site. The old internal
/// reading survives as "PBP Internal X/Y/Z": it is how a moved internal origin is spotted.
/// </summary>
public sealed class GetSiteLocationPbpAction : BatchActionBase
{
    public override string Name => "Get Site Location & PBP";

    public override WorksetPosture Worksets => WorksetPosture.PartialByDesign;

    public override string WorksetPostureReason =>
        "The site and ProjectPosition come from document-level objects a closed open keeps, but " +
        "BasePoint.GetProjectBasePoint / GetSurveyPoint return ELEMENTS that normally live in a " +
        "user workset (Shared Levels and Grids) — and what that static returns when the workset is " +
        "shut is not something reading the code can answer. UNPROVEN as of 2026-08-16, declared " +
        "partial as the safe reading. One live run on a workshared model settles it: correct " +
        "coordinates mean this is really ReadsNoInstances.";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Site Name", "Primary", "Site True North (deg)",
        "Site E/W (m)", "Site N/S (m)", "Site Elev (m)",
        "Latitude", "Longitude", "Place Name", "Site Count",
        "PBP E/W (m)", "PBP N/S (m)", "PBP Elev (m)",
        "Survey E/W (m)", "Survey N/S (m)", "Survey Elev (m)",
        "PBP Internal X (m)", "PBP Internal Y (m)", "PBP Internal Z (m)"
    };

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        Facts f = Read(doc);
        SiteRow a = f.Sites.FirstOrDefault(s => s.IsPrimary);
        return new Dictionary<string, string>
        {
            ["Latitude"] = f.Latitude,
            ["Longitude"] = f.Longitude,
            ["Place Name"] = f.PlaceName,
            ["Active Site"] = f.ActiveSite,
            ["Site Count"] = f.Sites.Count.ToString(CultureInfo.InvariantCulture),
            ["All Sites"] = string.Join(", ", f.Sites.Select(s => s.Name)),
            ["True North (deg)"] = f.ActiveTrueNorth,
            ["PBP E/W (m)"] = a.PbpEw ?? "",
            ["PBP N/S (m)"] = a.PbpNs ?? "",
            ["PBP Elev (m)"] = a.PbpElev ?? "",
            ["Survey E/W (m)"] = a.SurveyEw ?? "",
            ["Survey N/S (m)"] = a.SurveyNs ?? "",
            ["Survey Elev (m)"] = a.SurveyElev ?? "",
            ["PBP Internal X (m)"] = f.PbpInternalX,
            ["PBP Internal Y (m)"] = f.PbpInternalY,
            ["PBP Internal Z (m)"] = f.PbpInternalZ,
        };
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(
        UIApplication uiApp, Document doc)
    {
        Facts f = Read(doc);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (SiteRow s in f.Sites)
        {
            rows.Add(new Dictionary<string, string>
            {
                ["Site Name"] = s.Name,
                ["Primary"] = s.IsPrimary ? "Yes" : "No",
                ["Site True North (deg)"] = s.TrueNorth,
                ["Site E/W (m)"] = s.EastWest,
                ["Site N/S (m)"] = s.NorthSouth,
                ["Site Elev (m)"] = s.Elevation,
                ["Latitude"] = f.Latitude,
                ["Longitude"] = f.Longitude,
                ["Place Name"] = f.PlaceName,
                ["Site Count"] = f.Sites.Count.ToString(CultureInfo.InvariantCulture),
                ["PBP E/W (m)"] = s.PbpEw ?? "",
                ["PBP N/S (m)"] = s.PbpNs ?? "",
                ["PBP Elev (m)"] = s.PbpElev ?? "",
                ["Survey E/W (m)"] = s.SurveyEw ?? "",
                ["Survey N/S (m)"] = s.SurveyNs ?? "",
                ["Survey Elev (m)"] = s.SurveyElev ?? "",
                ["PBP Internal X (m)"] = f.PbpInternalX,
                ["PBP Internal Y (m)"] = f.PbpInternalY,
                ["PBP Internal Z (m)"] = f.PbpInternalZ,
            });
        }
        return rows;
    }

    private readonly struct SiteRow
    {
        public string Name { get; init; }
        public bool IsPrimary { get; init; }
        public string TrueNorth { get; init; }
        public string EastWest { get; init; }
        public string NorthSouth { get; init; }
        public string Elevation { get; init; }
        public string PbpEw { get; init; }
        public string PbpNs { get; init; }
        public string PbpElev { get; init; }
        public string SurveyEw { get; init; }
        public string SurveyNs { get; init; }
        public string SurveyElev { get; init; }
    }

    private readonly struct Facts
    {
        public string Latitude { get; init; }
        public string Longitude { get; init; }
        public string PlaceName { get; init; }
        public string ActiveSite { get; init; }
        public string ActiveTrueNorth { get; init; }
        public string PbpInternalX { get; init; }
        public string PbpInternalY { get; init; }
        public string PbpInternalZ { get; init; }
        public IReadOnlyList<SiteRow> Sites { get; init; }
    }

    private static Facts Read(Document doc)
    {
        SiteLocation site = doc.SiteLocation;
        ProjectLocation active = doc.ActiveProjectLocation;
        XYZ pbp = BasePoint.GetProjectBasePoint(doc).Position;
        XYZ survey = BasePoint.GetSurveyPoint(doc).Position;

        var sites = new List<SiteRow>();
        foreach (ProjectLocation location in doc.ProjectLocations.Cast<ProjectLocation>()
                     .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Each site's position is read independently: a location that cannot be read must not
            // cost the model its whole row, and a model with one broken site still reports the rest.
            string northDeg = "", ew = "", ns = "", elev = "";
            string pbpEw = "", pbpNs = "", pbpElev = "", svEw = "", svNs = "", svElev = "";
            try
            {
                ProjectPosition p = location.GetProjectPosition(XYZ.Zero);
                northDeg = Deg(p.Angle).ToString("F4", CultureInfo.InvariantCulture);
                ew = Metres(p.EastWest);
                ns = Metres(p.NorthSouth);
                elev = Metres(p.Elevation);
            }
            catch { /* leave the position blank; the name and primary flag still stand */ }

            // The PBP / Survey in THIS site's shared coordinates — what Revit shows on the points.
            try
            {
                ProjectPosition p = location.GetProjectPosition(pbp);
                pbpEw = Metres(p.EastWest);
                pbpNs = Metres(p.NorthSouth);
                pbpElev = Metres(p.Elevation);
            }
            catch { }
            try
            {
                ProjectPosition p = location.GetProjectPosition(survey);
                svEw = Metres(p.EastWest);
                svNs = Metres(p.NorthSouth);
                svElev = Metres(p.Elevation);
            }
            catch { }

            sites.Add(new SiteRow
            {
                Name = location.Name,
                IsPrimary = location.Id == active.Id,
                TrueNorth = northDeg,
                EastWest = ew,
                NorthSouth = ns,
                Elevation = elev,
                PbpEw = pbpEw,
                PbpNs = pbpNs,
                PbpElev = pbpElev,
                SurveyEw = svEw,
                SurveyNs = svNs,
                SurveyElev = svElev,
            });
        }

        // A model always has at least one project location, but the collection has been seen empty
        // on damaged files — and an action that returns no rows reads as "the check did not run".
        if (sites.Count == 0)
            sites.Add(new SiteRow { Name = active.Name, IsPrimary = true });

        SiteRow primary = sites.FirstOrDefault(s => s.IsPrimary);

        return new Facts
        {
            Latitude = Deg(site.Latitude).ToString("F6", CultureInfo.InvariantCulture),
            Longitude = Deg(site.Longitude).ToString("F6", CultureInfo.InvariantCulture),
            PlaceName = site.PlaceName ?? "",
            ActiveSite = active.Name,
            ActiveTrueNorth = primary.TrueNorth ?? "",
            PbpInternalX = Metres(pbp.X),
            PbpInternalY = Metres(pbp.Y),
            PbpInternalZ = Metres(pbp.Z),
            Sites = sites,
        };
    }

    private static double Deg(double radians) => radians * 180.0 / Math.PI;

    private static string Metres(double internalFeet) =>
        UnitUtils.ConvertFromInternalUnits(internalFeet, UnitTypeId.Meters)
            .ToString("F3", CultureInfo.InvariantCulture);
}
