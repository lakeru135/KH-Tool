using Autodesk.Revit.DB;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Enlarge;

/// <summary>
/// Creates the enlarged plan(s) and the sections or elevations for each target, as the preset
/// says. One transaction per target inside one group, so a target that fails rolls back alone and
/// the rest still land — and the whole run is still one Undo.
///
/// <para>Every view is cropped directly to the target plus its margin. (Revit's API can neither
/// create nor resize a scope box, so none is involved.)</para>
/// </summary>
internal sealed class EnlargeCreator
{
    public static readonly IReadOnlyList<string> Columns = new[] { "Target", "View", "Kind", "Status", "Note" };

    private readonly Document _doc;
    private readonly EnlargePreset _p;
    private readonly HashSet<string> _names;
    // Names that existed before this run. "Skip existing" means views an EARLIER run made; a clash
    // inside this run is two targets that need telling apart, and gets " (2)" instead.
    private readonly HashSet<string> _preexisting;
    private readonly ElementId _planType, _sectionType, _elevationType;
    private readonly ElementId _planTemplate, _sectionTemplate, _elevationTemplate;

    public EnlargeCreator(Document doc, EnlargePreset preset, bool stairs = false)
    {
        _doc = doc;
        _p = preset;
        _names = DrwNaming.ExistingViewNames(doc);
        _preexisting = new HashSet<string>(_names, StringComparer.OrdinalIgnoreCase);
        // Only what this preset will use is checked, so a missing elevation type does not warn a
        // section-only run. A missing one falls back (first type / no template) and says so.
        bool plans = preset.CreatePlan;
        bool sections = preset.CreateVerticalViews && (stairs || preset.ViewKind == EnlargeViewKind.Section);
        bool elevations = preset.CreateVerticalViews && !stairs && preset.ViewKind == EnlargeViewKind.Elevation;   // stairs: sections only
        var ignore = new List<string>();
        _planType = DrwLookup.ResolveType(DrwLookup.ViewTypes(doc, ViewFamily.FloorPlan), preset.PlanTypeName,
            "Plan type", plans ? _warnings : ignore);
        _sectionType = DrwLookup.ResolveType(DrwLookup.ViewTypes(doc, ViewFamily.Section), preset.SectionTypeName,
            "Section type", sections ? _warnings : ignore);
        _elevationType = DrwLookup.ResolveType(DrwLookup.ViewTypes(doc, ViewFamily.Elevation), preset.ElevationTypeName,
            "Elevation type", elevations ? _warnings : ignore);
        _planTemplate = DrwLookup.ResolveTemplate(doc, ViewType.FloorPlan, preset.PlanTemplate,
            "Plan template", plans ? _warnings : ignore);
        _sectionTemplate = DrwLookup.ResolveTemplate(doc, ViewType.Section, preset.SectionTemplate,
            "Section template", sections ? _warnings : ignore);
        _elevationTemplate = DrwLookup.ResolveTemplate(doc, ViewType.Elevation, preset.ElevationTemplate,
            "Elevation template", elevations ? _warnings : ignore);
    }

    /// <summary>Types or templates the preset names that this model lacks (see the constructor).</summary>
    private readonly List<string> _warnings = new();

    public List<IReadOnlyDictionary<string, string>> Create(IEnumerable<EnlargeTarget> targets, bool writeMarks)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (_warnings.Count > 0)
            rows.Add(DrwRows.Row(Columns, "", "", "Setup", "Warning", string.Join("; ", _warnings)));
        using var group = new TransactionGroup(_doc, "DRW Enlarge");
        group.Start();
        foreach (EnlargeTarget target in targets)
        {
            var targetRows = new List<IReadOnlyDictionary<string, string>>();
            using Transaction t = DrwWarningSwallower.Start(_doc, "Enlarge " + target.BaseName);
            try
            {
                if (writeMarks) WriteMarks(target, targetRows);
                CreateFor(target, targetRows);
                t.Commit();
                rows.AddRange(targetRows);
            }
            catch (Exception ex)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                rows.Add(Row(target, "", target.Kind, "Failed", ex.Message));
            }
        }
        group.Assimilate();
        return rows;
    }

    private void WriteMarks(EnlargeTarget target, List<IReadOnlyDictionary<string, string>> rows)
    {
        foreach ((ElementId id, string mark) in target.MarkWrites)
        {
            Element? e = _doc.GetElement(id);
            if (e == null) continue;
            string before = DrwParams.Mark(e) ?? "";
            if (before == mark) continue;
            bool ok = DrwParams.SetMark(e, mark);
            rows.Add(Row(target, "", "Mark", ok ? "Mark set" : "Mark not set",
                ok ? $"{e.Name}: '{before}' → '{mark}'" : $"{e.Name}: Mark is read-only or owned by someone else"));
        }
    }

    private void CreateFor(EnlargeTarget target, List<IReadOnlyDictionary<string, string>> rows)
    {
        List<XYZ> points = target.OutlinePoints().ToList();
        (XYZ ax, XYZ ay) = Frame(target);

        // --- plans ---
        ViewPlan? hostPlan = null;
        foreach (Level level in target.PlanLevels)
        {
            string name = DrwNaming.Compose(_p.Prefix, _p.PlanPrefix, target.BaseName,
                target.LevelSuffix ? level.Name : "");
            ViewPlan? existing = _preexisting.Contains(name) ? FindPlan(name) : null;
            if (!_p.CreatePlan || (existing != null && _p.SkipExisting))
            {
                if (existing != null) rows.Add(Row(target, name, "Plan", "Skipped", "A view with this name exists"));
                hostPlan ??= existing;
                continue;
            }
            ViewPlan plan = ViewPlan.Create(_doc, _planType, level.Id);
            plan.Name = DrwNaming.Unique(name, _names);
            string finished = Finish(plan, _planTemplate);
            _doc.Regenerate();
            DrwCrop.Fit(plan, points, DrwUnits.FromMeters(_p.PlanMarginM));
            rows.Add(Row(target, plan.Name, "Plan", "Created", Join(finished, WriteParams(plan))));
            hostPlan ??= plan;
        }

        if (!_p.CreateVerticalViews) return;

        // Stairs only ever get sections — including a stair preset saved with Elevation before.
        if (_p.ViewKind == EnlargeViewKind.Section || target.IsStair)
        {
            foreach ((string side, XYZ look) in Sides(ax, ay))
                CreateSection(target, points, side, look, rows);
            return;
        }

        // Elevation markers need a plan on the target's base level to hang from.
        hostPlan ??= HostPlanFor(target.PlanLevels[0]);
        if (hostPlan == null)
        {
            rows.Add(Row(target, "", "Elevation", "Failed", "No floor plan on this level to place elevation markers in"));
            return;
        }
        if (_p.ElevationMode == ElevationMode.ByCenter4)
            CreateCenterElevations(target, points, ax, ay, hostPlan, rows);
        else
            CreateBoundaryElevations(target, hostPlan, rows);
    }

    // ------------------------------------------------------------------ sections

    private void CreateSection(EnlargeTarget target, List<XYZ> points, string side, XYZ look,
        List<IReadOnlyDictionary<string, string>> rows)
    {
        string name = DrwNaming.Compose(_p.Prefix, _p.SectionPrefix, target.BaseName, side);
        if (_p.SkipExisting && _preexisting.Contains(name))
        {
            rows.Add(Row(target, name, "Section", "Skipped", "A view with this name exists"));
            return;
        }

        // Where the line stands, measured along `look` from the centre of the target's box in the
        // N/E/S/W frame: `near` is the edge behind the viewer, `far` the edge it looks at.
        XYZ center = Center(points);
        double near = points.Min(p => Flat(p - center).DotProduct(look));
        double far = points.Max(p => Flat(p - center).DotProduct(look));
        double cut, shift = 0;
        if (_p.SectionPlacement == SectionPlacement.FromCentre)
        {
            shift = DrwUnits.FromMeters(_p.SectionCentreOffsetM);
            cut = Math.Max(near, Math.Min(far, (near + far) / 2 - shift));
        }
        else
        {
            // A room or stair shallower than twice the inset gets its line in the middle instead.
            cut = near + Math.Min(DrwUnits.FromMeters(_p.SectionOffsetM), (far - near) / 2);
        }

        // Height: its own pad below the floor / first step, and above the chosen top reference.
        double below = DrwUnits.FromMeters(_p.SectionBelowM);
        double above = DrwUnits.FromMeters(_p.SectionAboveM);
        (double topRef, string topName) = TopReference(target);
        double top = Math.Max(topRef - target.BottomZ + above, -below + DrwUnits.FromMeters(0.1));

        XYZ origin = new XYZ(center.X, center.Y, target.BottomZ) + look * cut;
        ViewSection section = MakeSection(origin, look, points, far - cut, below, top,
            DrwUnits.FromMeters(_p.SectionCropOffsetM));
        section.Name = DrwNaming.Unique(name, _names);
        string finished = Finish(section, _sectionTemplate);

        string back = side switch { "N" => "S", "S" => "N", "E" => "W", _ => "E" };
        string where = _p.SectionPlacement == SectionPlacement.FromCentre
            ? (Math.Abs(shift) < 1e-6 ? "Line through the centre"
                : $"Line {DrwUnitsDisplay.Meters(Math.Abs(shift))} {(shift > 0 ? "back" : "forward")} from the centre")
            : $"Line {DrwUnitsDisplay.Meters(cut - near)} inside the {back} edge";
        string note = string.Join("; ", new[] { $"{where}, looking {side}", HeightNote(target, below, above, topName), finished, WriteParams(section) }
            .Where(s => !string.IsNullOrEmpty(s)));
        rows.Add(Row(target, section.Name, "Section", "Created", note));
    }

    /// <summary>
    /// What a section's top is measured from, and its name for the row note: the next level above
    /// when the preset asks and there is one, otherwise the top of the room / the last step.
    /// </summary>
    private (double Z, string Name) TopReference(EnlargeTarget target)
    {
        string own = target.IsStair ? "the last step" : "the top of the room";
        if (_p.SectionTopFrom != SectionTopFrom.NextLevel) return (target.LastStepZ, own);
        return target.NextLevel != null
            ? (target.NextLevel.Elevation, target.NextLevel.Name)
            : (target.LastStepZ, own + " (no level above)");
    }

    private static string HeightNote(EnlargeTarget target, double below, double above, string topName) =>
        $"height {DrwUnitsDisplay.Meters(below)} below {target.PlanLevels[0].Name} to {DrwUnitsDisplay.Meters(above)} above {topName}";

    /// <summary>
    /// An elevation's crop, from the same values a section uses (since 0.2.7): the side crop past
    /// the outline, and the height from <see cref="EnlargePreset.SectionBelowM"/> under the floor to
    /// <see cref="EnlargePreset.SectionAboveM"/> over the chosen top reference. Fits the view and
    /// returns the side crop (for the far clip) and the row's height note.
    /// </summary>
    private (double Side, string Height) FitElevation(ViewSection view, EnlargeTarget target, IEnumerable<XYZ> plan)
    {
        double side = DrwUnits.FromMeters(_p.SectionCropOffsetM);
        double below = DrwUnits.FromMeters(_p.SectionBelowM);
        double above = DrwUnits.FromMeters(_p.SectionAboveM);
        (double topRef, string topName) = TopReference(target);
        double topZ = Math.Max(topRef, target.BottomZ + DrwUnits.FromMeters(0.1));
        var points = plan.SelectMany(p => new[] { new XYZ(p.X, p.Y, target.BottomZ), new XYZ(p.X, p.Y, topZ) }).ToList();
        DrwCrop.Fit(view, points, side, below, above);
        return (side, HeightNote(target, below, above, topName));
    }

    /// <summary>
    /// Which way round Revit reads a section box, learned from the first section of the run:
    /// null = not yet known; false = the documented way (looks along −BasisZ, line on the Max.Z
    /// face); true = the mirror (looks along +BasisZ, line on the Min.Z face).
    ///
    /// <para><b>Why it is learned, not assumed.</b> The API docs say the first. 0.2.1 and 0.2.2
    /// were built that way, and the user's plans showed every line sitting just OUTSIDE the far
    /// wall, looking back in — the second. Both boxes below hold the same volume, so whichever
    /// reading Revit uses, one of them puts the line where it was asked and faces it the right way.</para>
    /// </summary>
    private bool? _sectionAlongZ;

    /// <summary>
    /// A section whose line passes through <paramref name="origin"/> and which looks along
    /// <paramref name="look"/>, seeing <paramref name="reach"/> forward plus the crop, from
    /// <paramref name="below"/> under the origin's height to <paramref name="top"/> above it —
    /// checked against the view Revit actually made (<see cref="View.ViewDirection"/> points back
    /// at the viewer).
    /// </summary>
    private ViewSection MakeSection(XYZ origin, XYZ look, List<XYZ> points, double reach,
        double below, double top, double crop)
    {
        ViewSection Build(bool alongZ)
        {
            XYZ bz = alongZ ? look : look.Negate();
            XYZ by = XYZ.BasisZ;
            XYZ bx = by.CrossProduct(bz);
            double minR = points.Min(p => Flat(p - origin).DotProduct(bx));
            double maxR = points.Max(p => Flat(p - origin).DotProduct(bx));
            Transform tf = Transform.Identity;
            tf.Origin = origin;
            tf.BasisX = bx;
            tf.BasisY = by;
            tf.BasisZ = bz;
            double depth = reach + crop;
            var box = new BoundingBoxXYZ
            {
                Transform = tf,
                Min = new XYZ(minR - crop, -below, alongZ ? 0 : -depth),
                Max = new XYZ(maxR + crop, top, alongZ ? depth : 0)
            };
            ViewSection s = ViewSection.CreateSection(_doc, _sectionType, box);
            _doc.Regenerate();
            return s;
        }

        bool FacesLook(ViewSection s) => s.ViewDirection.Negate().DotProduct(look) > 0.99;

        bool first = _sectionAlongZ ?? false;
        ViewSection section = Build(first);
        if (FacesLook(section))
        {
            _sectionAlongZ = first;
            return section;
        }
        _doc.Delete(section.Id);
        section = Build(!first);
        if (FacesLook(section))
        {
            _sectionAlongZ = !first;
            return section;
        }
        _doc.Delete(section.Id);
        throw new InvalidOperationException("Revit did not face the section the way it was asked.");
    }

    private static XYZ Flat(XYZ v) => new(v.X, v.Y, 0);

    // ------------------------------------------------------------------ elevations

    private void CreateCenterElevations(EnlargeTarget target, List<XYZ> points, XYZ ax, XYZ ay,
        ViewPlan host, List<IReadOnlyDictionary<string, string>> rows)
    {
        List<(string Side, XYZ Look)> wanted = Sides(ax, ay).ToList();
        if (wanted.Count == 0) return;

        XYZ c = Center(points);
        XYZ origin = new XYZ(c.X, c.Y, host.GenLevel?.Elevation ?? target.BottomZ);
        ElevationMarker marker = ElevationMarker.CreateElevationMarker(_doc, _elevationType, origin, _p.Scale);
        double turn = XYZ.BasisX.AngleOnPlaneTo(ax, XYZ.BasisZ);
        if (Math.Abs(turn) > 1e-6)
            ElementTransformUtils.RotateElement(_doc, marker.Id, Line.CreateBound(origin, origin + XYZ.BasisZ), turn);

        var all = new[] { ("N", ay), ("E", ax), ("S", ay.Negate()), ("W", ax.Negate()) };
        for (int i = 0; i < 4; i++)
        {
            if (!marker.IsAvailableIndex(i)) continue;
            ViewSection v = marker.CreateElevation(_doc, host.Id, i);
            XYZ look = v.ViewDirection.Negate();
            string side = all.OrderByDescending(s => s.Item2.DotProduct(look)).First().Item1;
            if (!wanted.Any(w => w.Side == side))
            {
                _doc.Delete(v.Id);
                continue;
            }
            string name = DrwNaming.Compose(_p.Prefix, _p.ElevationPrefix, target.BaseName, side);
            if (_p.SkipExisting && _preexisting.Contains(name))
            {
                _doc.Delete(v.Id);
                rows.Add(Row(target, name, "Elevation", "Skipped", "A view with this name exists"));
                continue;
            }
            v.Name = DrwNaming.Unique(name, _names);
            _doc.Regenerate();
            (double side0, string height) = FitElevation(v, target, points);
            double depth = points.Max(p => new XYZ(p.X - c.X, p.Y - c.Y, 0).DotProduct(look));
            DrwCrop.FarClip(v, depth + side0);
            string finished = Finish(v, _elevationTemplate);
            rows.Add(Row(target, v.Name, "Elevation", "Created", Join($"Looking {side}", height, finished, WriteParams(v))));
        }
        if (!marker.HasElevations()) _doc.Delete(marker.Id);
    }

    private void CreateBoundaryElevations(EnlargeTarget target, ViewPlan host,
        List<IReadOnlyDictionary<string, string>> rows)
    {
        double min = DrwUnits.FromMeters(_p.MinBoundaryLengthM);
        double inset = DrwUnits.FromMeters(_p.ElevationInsetM);
        double z = host.GenLevel?.Elevation ?? target.BottomZ;
        IEnumerable<List<Line>> loops = _p.IncludeInnerLoops ? target.Loops : target.Loops.Take(1);

        int n = 0, tooShort = 0;
        foreach (Line seg in loops.SelectMany(l => l))
        {
            if (seg.Length < min) { tooShort++; continue; }
            XYZ a = seg.GetEndPoint(0), b = seg.GetEndPoint(1);
            XYZ dir = new XYZ(b.X - a.X, b.Y - a.Y, 0).Normalize();
            XYZ mid = (a + b) / 2;
            XYZ normal = XYZ.BasisZ.CrossProduct(dir);
            double probe = Math.Min(0.5, inset);
            XYZ inward;
            if (target.Inside(mid + normal * probe)) inward = normal;
            else if (target.Inside(mid - normal * probe)) inward = normal.Negate();
            else continue;   // a sliver edge with no inside to stand in
            XYZ look = inward.Negate();

            n++;
            string name = DrwNaming.Compose(_p.Prefix, _p.ElevationPrefix, target.BaseName, n.ToString("00"));
            if (_p.SkipExisting && _preexisting.Contains(name))
            {
                rows.Add(Row(target, name, "Elevation", "Skipped", "A view with this name exists"));
                continue;
            }

            XYZ origin = new XYZ(mid.X, mid.Y, z) + inward * inset;
            ElevationMarker marker = ElevationMarker.CreateElevationMarker(_doc, _elevationType, origin, _p.Scale);

            // A marker's four indices look in fixed directions; make the one closest to the wall,
            // delete the rest, then turn the marker so that view squares up to the wall exactly.
            ViewSection? keep = null;
            for (int i = 0; i < 4; i++)
            {
                if (!marker.IsAvailableIndex(i)) continue;
                ViewSection v = marker.CreateElevation(_doc, host.Id, i);
                if (keep == null || v.ViewDirection.Negate().DotProduct(look) > keep.ViewDirection.Negate().DotProduct(look))
                {
                    if (keep != null) _doc.Delete(keep.Id);
                    keep = v;
                }
                else _doc.Delete(v.Id);
            }
            if (keep == null) { _doc.Delete(marker.Id); continue; }

            double turn = keep.ViewDirection.Negate().AngleOnPlaneTo(look, XYZ.BasisZ);
            if (Math.Abs(turn) > 1e-6)
                ElementTransformUtils.RotateElement(_doc, marker.Id, Line.CreateBound(origin, origin + XYZ.BasisZ), turn);

            keep.Name = DrwNaming.Unique(name, _names);
            _doc.Regenerate();
            (double side, string height) = FitElevation(keep, target, new[] { a, b });
            DrwCrop.FarClip(keep, inset + side);
            string finished = Finish(keep, _elevationTemplate);
            rows.Add(Row(target, keep.Name, "Elevation", "Created",
                Join($"Edge {DrwUnitsDisplay.Meters(seg.Length)}", height, finished, WriteParams(keep))));
        }
        if (n == 0)
            rows.Add(Row(target, "", "Elevation", "None",
                $"No boundary edge reaches {_p.MinBoundaryLengthM:0.00} m ({tooShort} shorter)"));
    }

    // ------------------------------------------------------------------ helpers

    private (XYZ X, XYZ Y) Frame(EnlargeTarget target)
    {
        XYZ ax = XYZ.BasisX;
        if (_p.OrientToLongestEdge)
        {
            Line? longest = target.Loops.FirstOrDefault()?.OrderByDescending(l => l.Length).FirstOrDefault();
            if (longest != null)
            {
                XYZ d = longest.GetEndPoint(1) - longest.GetEndPoint(0);
                d = new XYZ(d.X, d.Y, 0);
                if (d.GetLength() > 1e-6)
                {
                    d = d.Normalize();
                    // Keep the frame's "east" within 45° of the project's so N stays roughly up.
                    for (int i = 0; i < 4 && d.DotProduct(XYZ.BasisX) < Math.Cos(Math.PI / 4) - 1e-9; i++)
                        d = new XYZ(-d.Y, d.X, 0);
                    ax = d;
                }
            }
        }
        return (ax, XYZ.BasisZ.CrossProduct(ax));
    }

    private IEnumerable<(string Side, XYZ Look)> Sides(XYZ ax, XYZ ay)
    {
        if (_p.North) yield return ("N", ay);
        if (_p.East) yield return ("E", ax);
        if (_p.South) yield return ("S", ay.Negate());
        if (_p.West) yield return ("W", ax.Negate());
    }

    private static XYZ Center(List<XYZ> points) => new(
        (points.Min(p => p.X) + points.Max(p => p.X)) / 2,
        (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2, 0);

    /// <summary>
    /// Scale, then the template. A template that controls View Scale (or Far Clipping) wins over
    /// the preset — that is what a template is for — but it used to win silently; the returned
    /// note says so, and is blank when the view came out as asked.
    /// </summary>
    private string Finish(View view, ElementId template)
    {
        try { view.Scale = _p.Scale; } catch { /* a template may lock scale */ }
        if (template == ElementId.InvalidElementId) return "";
        view.ViewTemplateId = template;
        string name = _doc.GetElement(template)?.Name ?? "?";
        var notes = new List<string>();
        if (view.Scale != _p.Scale)
            notes.Add($"Scale 1:{view.Scale} comes from template '{name}' (the preset says 1:{_p.Scale})");
        if (view.ViewType is ViewType.Section or ViewType.Elevation &&
            view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.AsInteger() == 0)
            notes.Add($"Template '{name}' turns the far clip off, so the view sees past the far wall");
        return string.Join("; ", notes);
    }

    private string WriteParams(View view)
    {
        var errors = _p.Parameters.Where(pv => !string.IsNullOrWhiteSpace(pv.Name))
            .Select(pv => DrwParams.Set(view, pv.Name.Trim(), pv.Guid ?? "", pv.Value ?? ""))
            .Where(e => e != null).ToList();
        return errors.Count == 0 ? "" : "Parameters: " + string.Join("; ", errors);
    }

    private ViewPlan? FindPlan(string name) =>
        new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    private ViewPlan? HostPlanFor(Level level) =>
        new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id)
            .OrderBy(v => v.CropBoxActive ? 1 : 0).FirstOrDefault();

    private static string Join(params string[] parts) =>
        string.Join("; ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static IReadOnlyDictionary<string, string> Row(EnlargeTarget t, string view, string kind, string status, string note) =>
        DrwRows.Row(Columns, t.BaseName, view, kind, status, note);
}

internal static class DrwUnitsDisplay
{
    public static string Meters(double feet) =>
        UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters).ToString("0.00") + " m";
}
