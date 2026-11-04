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
        "Survey E/W (m)", "Survey N/S (m)", "Survey Elev (m)"
    };

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        Facts f = Read(doc);
        return new Dictionary<string, string>
        {
            ["Latitude"] = f.Latitude,
            ["Longitude"] = f.Longitude,
            ["Place Name"] = f.PlaceName,
            ["Active Site"] = f.ActiveSite,
            ["Site Count"] = f.Sites.Count.ToString(CultureInfo.InvariantCulture),
            ["All Sites"] = string.Join(", ", f.Sites.Select(s => s.Name)),
            ["True North (deg)"] = f.ActiveTrueNorth,
            ["PBP E/W (m)"] = f.PbpEw,
            ["PBP N/S (m)"] = f.PbpNs,
            ["PBP Elev (m)"] = f.PbpElev,
            ["Survey E/W (m)"] = f.SurveyEw,
            ["Survey N/S (m)"] = f.SurveyNs,
            ["Survey Elev (m)"] = f.SurveyElev,
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
                ["PBP E/W (m)"] = f.PbpEw,
                ["PBP N/S (m)"] = f.PbpNs,
                ["PBP Elev (m)"] = f.PbpElev,
                ["Survey E/W (m)"] = f.SurveyEw,
                ["Survey N/S (m)"] = f.SurveyNs,
                ["Survey Elev (m)"] = f.SurveyElev,
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
    }

    private readonly struct Facts
    {
        public string Latitude { get; init; }
        public string Longitude { get; init; }
        public string PlaceName { get; init; }
        public string ActiveSite { get; init; }
        public string ActiveTrueNorth { get; init; }
        public string PbpEw { get; init; }
        public string PbpNs { get; init; }
        public string PbpElev { get; init; }
        public string SurveyEw { get; init; }
        public string SurveyNs { get; init; }
        public string SurveyElev { get; init; }
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
            try
            {
                ProjectPosition p = location.GetProjectPosition(XYZ.Zero);
                northDeg = Deg(p.Angle).ToString("F4", CultureInfo.InvariantCulture);
                ew = Metres(p.EastWest);
                ns = Metres(p.NorthSouth);
                elev = Metres(p.Elevation);
            }
            catch { /* leave the position blank; the name and primary flag still stand */ }

            sites.Add(new SiteRow
            {
                Name = location.Name,
                IsPrimary = location.Id == active.Id,
                TrueNorth = northDeg,
                EastWest = ew,
                NorthSouth = ns,
                Elevation = elev,
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
            PbpEw = Metres(pbp.X),
            PbpNs = Metres(pbp.Y),
            PbpElev = Metres(pbp.Z),
            SurveyEw = Metres(survey.X),
            SurveyNs = Metres(survey.Y),
            SurveyElev = Metres(survey.Z),
            Sites = sites,
        };
    }

    private static double Deg(double radians) => radians * 180.0 / Math.PI;

    private static string Metres(double internalFeet) =>
        UnitUtils.ConvertFromInternalUnits(internalFeet, UnitTypeId.Meters)
            .ToString("F3", CultureInfo.InvariantCulture);
}
