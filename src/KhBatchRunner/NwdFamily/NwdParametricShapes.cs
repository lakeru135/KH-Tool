using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.NwdFamily;

/// <summary>
/// Rebuilds a recognised box or upright cylinder (<see cref="NwdShapeFitter"/>) as an extrusion that
/// type parameters resize: Width / Depth / Height, or Diameter / Height. Each size is a labelled
/// dimension between reference planes the extrusion's faces are locked to. Where the shape stays
/// put when it is resized follows Revit's own habit: centred on the family centre → grows both ways
/// (EQ); otherwise its side nearest the centre / its bottom stays where Navisworks had it (a locked
/// dimension to the centre plane / Ref. Level).
///
/// <para>Each shape is built in its own sub-transaction and checked against the mesh it replaces; any
/// failure rolls the shape back and the caller keeps it as fixed geometry.</para>
/// </summary>
internal sealed class NwdParametricShapes
{
    private const double Tol = NwdShapeFitter.Tolerance;
    // Where dimension lines sit, outside the shape (feet).
    private const double DimOffset = 1.0;

    private readonly Document _fam;
    private readonly FamilyManager _fm;
    private readonly ViewPlan _plan;
    private readonly View _front;
    private readonly Reference _centreX, _centreY, _level;

    private NwdParametricShapes(Document fam, ViewPlan plan, View front, Reference centreX, Reference centreY, Reference level)
    {
        _fam = fam;
        _fm = fam.FamilyManager;
        _plan = plan;
        _front = front;
        _centreX = centreX;
        _centreY = centreY;
        _level = level;
    }

    /// <summary>Null (with a note) when the template lacks the plan, the front view or the centre planes.</summary>
    public static NwdParametricShapes? TryCreate(Document fam, NwdBuildReport report)
    {
        Level? level = new FilteredElementCollector(fam).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => Math.Abs(l.Elevation)).FirstOrDefault();
        ViewPlan? plan = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && level != null && v.GenLevel?.Id == level.Id);
        View? front = new FilteredElementCollector(fam).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation && Math.Abs(v.ViewDirection.Y) > 0.99)
            .OrderBy(v => v.Name == "Front" ? 0 : 1).FirstOrDefault();
        var planes = new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
        ReferencePlane? centreX = planes.FirstOrDefault(p => Math.Abs(p.Normal.X) > 0.999 && Math.Abs(p.BubbleEnd.X) < Tol);
        ReferencePlane? centreY = planes.FirstOrDefault(p => Math.Abs(p.Normal.Y) > 0.999 && Math.Abs(p.BubbleEnd.Y) < Tol);

        if (level == null || plan == null || front == null || centreX == null || centreY == null)
        {
            report.Note("Shapes not made resizable: the template has no " +
                (level == null ? "reference level" : plan == null ? "plan view" : front == null ? "front elevation" : "centre reference planes") + ".");
            return null;
        }
        return new NwdParametricShapes(fam, plan, front, centreX.GetReference(), centreY.GetReference(), level.GetPlaneReference());
    }

    /// <param name="prefix">"" for the family's only sized shape (Width…), else "&lt;name&gt; ".</param>
    /// <param name="reserved">Receives the parameter names used, so Navisworks properties never overwrite them.</param>
    public bool TryBuild(NwdShape shape, string prefix, ElementId material, ISet<string> reserved, NwdBuildReport report)
    {
        using var st = new SubTransaction(_fam);
        st.Start();
        var used = new List<string>();
        var flexes = new List<(FamilyParameter Size, Func<Extrusion, double> Extent)>();
        try
        {
            Extrusion extrusion = shape.Kind == NwdShapeKind.Box
                ? Box(shape, prefix, used, flexes)
                : Cylinder(shape, prefix, used, flexes);
            extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(material);
            _fam.Regenerate();

            if (!WhereTheMeshWas(extrusion, shape))
                throw new InvalidOperationException("the rebuilt extrusion is not where the mesh was");
            // A label Revit accepts but cannot drive is worse than no label: prove each one resizes.
            foreach ((FamilyParameter size, Func<Extrusion, double> extent) in flexes)
                Flex(extrusion, size, extent);

            st.Commit();
            foreach (string name in used)
                reserved.Add(name);
            if (shape.Kind == NwdShapeKind.Box) report.ParametricBoxes++;
            else report.ParametricCylinders++;
            return true;
        }
        catch (Exception ex)
        {
            if (st.HasStarted() && !st.HasEnded())
                st.RollBack();
            report.ParametricFellBack++;
            report.Note($"{(prefix.Length > 0 ? prefix.Trim() : "Shape")}: kept as fixed geometry ({ex.Message}).");
            return false;
        }
    }

    // ------------------------------------------------------------------ shapes

    private Extrusion Box(NwdShape s, string prefix, List<string> used, List<(FamilyParameter, Func<Extrusion, double>)> flexes)
    {
        FamilyParameter width = Size(prefix + "Width", s.MaxX - s.MinX, used);
        FamilyParameter depth = Size(prefix + "Depth", s.MaxY - s.MinY, used);
        FamilyParameter height = Size(prefix + "Height", s.MaxZ - s.MinZ, used);

        // X sides are planes normal to X, drawn along Y in plan; Y sides the other way.
        (Reference xLo, Reference xHi) = PlanAxis(s.MinX, s.MaxX, _centreX,
            c => new[] { new XYZ(c, s.MinY - DimOffset, 0), new XYZ(c, s.MaxY + DimOffset, 0) });
        (Reference yLo, Reference yHi) = PlanAxis(s.MinY, s.MaxY, _centreY,
            c => new[] { new XYZ(s.MinX - DimOffset, c, 0), new XYZ(s.MaxX + DimOffset, c, 0) });
        (Reference zLo, Reference zHi) = HeightAxis(s);

        var profile = new CurveArray();
        XYZ p0 = new(s.MinX, s.MinY, s.MinZ), p1 = new(s.MaxX, s.MinY, s.MinZ), p2 = new(s.MaxX, s.MaxY, s.MinZ), p3 = new(s.MinX, s.MaxY, s.MinZ);
        profile.Append(Line.CreateBound(p0, p1));
        profile.Append(Line.CreateBound(p1, p2));
        profile.Append(Line.CreateBound(p2, p3));
        profile.Append(Line.CreateBound(p3, p0));
        Extrusion extrusion = NewExtrusion(profile, s);

        Align(_plan, xLo, Face(extrusion, -XYZ.BasisX));
        Align(_plan, xHi, Face(extrusion, XYZ.BasisX));
        Align(_plan, yLo, Face(extrusion, -XYZ.BasisY));
        Align(_plan, yHi, Face(extrusion, XYZ.BasisY));
        Align(_front, zLo, Face(extrusion, -XYZ.BasisZ));
        Align(_front, zHi, Face(extrusion, XYZ.BasisZ));

        Label(_plan, xLo, xHi, new XYZ(s.MinX, s.MaxY + DimOffset, s.MinZ), XYZ.BasisX, width);
        Label(_plan, yLo, yHi, new XYZ(s.MaxX + DimOffset, s.MinY, s.MinZ), XYZ.BasisY, depth);
        Label(_front, zLo, zHi, new XYZ(s.MaxX + DimOffset, s.MinY, s.MinZ), XYZ.BasisZ, height);
        flexes.Add((width, e => Box(e).Max.X - Box(e).Min.X));
        flexes.Add((depth, e => Box(e).Max.Y - Box(e).Min.Y));
        flexes.Add((height, e => Box(e).Max.Z - Box(e).Min.Z));
        return extrusion;
    }

    private Extrusion Cylinder(NwdShape s, string prefix, List<string> used, List<(FamilyParameter, Func<Extrusion, double>)> flexes)
    {
        FamilyParameter diameter = Size(prefix + "Diameter", 2 * s.Radius, used);
        FamilyParameter height = Size(prefix + "Height", s.MaxZ - s.MinZ, used);
        (Reference zLo, Reference zHi) = HeightAxis(s);
        // Planes through the axis, held where Navisworks had it.
        Reference axisX = AxisPlane(s.CentreX, _centreX, c => new[] { new XYZ(c, s.MinY - DimOffset, 0), new XYZ(c, s.MaxY + DimOffset, 0) });
        Reference axisY = AxisPlane(s.CentreY, _centreY, c => new[] { new XYZ(s.MinX - DimOffset, c, 0), new XYZ(s.MaxX + DimOffset, c, 0) });

        var centre = new XYZ(s.CentreX, s.CentreY, s.MinZ);
        var profile = new CurveArray();
        profile.Append(Arc.Create(centre, s.Radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
        profile.Append(Arc.Create(centre, s.Radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
        Extrusion extrusion = NewExtrusion(profile, s);

        Align(_front, zLo, Face(extrusion, -XYZ.BasisZ));
        Align(_front, zHi, Face(extrusion, XYZ.BasisZ));
        Label(_front, zLo, zHi, new XYZ(s.MaxX + DimOffset, s.MinY, s.MinZ), XYZ.BasisZ, height);

        // Probed on Revit 2026.5 (NWD circle probe, 2026-10-21): the diameter must be ONE dimension
        // on ONE sketch arc. On the solid's rim edge it is accepted but never drives (Regenerate spun
        // 9 minutes); on both halves the two fight and neither drives; SketchEditScope is refused in
        // families. One arc drives, but the circle then grows from one side, so both arcs' centres
        // are aligned to the axis planes.
        Curve first = extrusion.Sketch.Profile.get_Item(0).get_Item(0);
        if (first is not Arc || first.Reference == null)
            throw new InvalidOperationException("the circle's sketch has no arc reference");
        XYZ at = new(s.CentreX + s.Radius + DimOffset, s.CentreY + s.Radius + DimOffset, s.MinZ);
        _fam.FamilyCreate.NewDiameterDimension(_plan, first.Reference, at).FamilyLabel = diameter;
        foreach (CurveElement arc in extrusion.Sketch.GetAllElements().Select(_fam.GetElement).OfType<CurveElement>())
        {
            Reference axis = arc.CenterPointReference ?? throw new InvalidOperationException("a sketch arc has no centre reference");
            Align(_plan, axisX, axis);
            Align(_plan, axisY, axis);
        }
        flexes.Add((diameter, e => 2 * (Rim(e)?.Radius ?? 0)));
        flexes.Add((height, e => Box(e).Max.Z - Box(e).Min.Z));
        return extrusion;
    }

    private Extrusion NewExtrusion(CurveArray loop, NwdShape s)
    {
        var profile = new CurveArrArray();
        profile.Append(loop);
        SketchPlane sketch = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, s.MinZ)));
        Extrusion e = _fam.FamilyCreate.NewExtrusion(true, profile, sketch, s.MaxZ - s.MinZ);
        _fam.Regenerate();
        return e;
    }

    // ------------------------------------------------------------------ constraints

    private FamilyParameter Size(string name, double value, List<string> used)
    {
        string unique = NwdFamilyParameters.Unique(_fm, NwdFamilyParameters.Clean(name));
        FamilyParameter fp = _fm.AddParameter(unique, GroupTypeId.Geometry, SpecTypeId.Length, false);
        _fm.Set(fp, value);
        used.Add(unique);
        return fp;
    }

    /// <summary>
    /// The two side references of one plan axis. A side on the centre plane IS the centre plane;
    /// a shape centred on it gets EQ; otherwise the side nearer the centre is locked to it.
    /// </summary>
    private (Reference Lo, Reference Hi) PlanAxis(double lo, double hi, Reference centre, Func<double, XYZ[]> ends)
    {
        Reference Side(double at)
        {
            if (Math.Abs(at) < Tol)
                return centre;
            XYZ[] e = ends(at);
            return _fam.FamilyCreate.NewReferencePlane(e[0], e[1], XYZ.BasisZ, _plan).GetReference();
        }
        Reference rLo = Side(lo), rHi = Side(hi);
        _fam.Regenerate();

        if (rLo != centre && rHi != centre)
        {
            bool centred = Math.Abs(lo + hi) < Tol;
            XYZ[] e = ends(lo);
            XYZ along = (e[1] - e[0]).Normalize().CrossProduct(XYZ.BasisZ);
            XYZ at = e[0] - (e[1] - e[0]).Normalize() * DimOffset;
            var refs = new ReferenceArray();
            if (centred)
            {
                refs.Append(rLo);
                refs.Append(centre);
                refs.Append(rHi);
                Dimension eq = _fam.FamilyCreate.NewDimension(_plan, Line.CreateBound(at, at + along), refs);
                eq.AreSegmentsEqual = true;
            }
            else
            {
                refs.Append(centre);
                refs.Append(Math.Abs(lo) < Math.Abs(hi) ? rLo : rHi);
                _fam.FamilyCreate.NewDimension(_plan, Line.CreateBound(at, at + along), refs).IsLocked = true;
            }
        }
        return (rLo, rHi);
    }

    /// <summary>A plane through a cylinder's axis: the centre plane itself, or a new one locked to it.</summary>
    private Reference AxisPlane(double at, Reference centre, Func<double, XYZ[]> ends)
    {
        if (Math.Abs(at) < Tol)
            return centre;
        XYZ[] e = ends(at);
        Reference plane = _fam.FamilyCreate.NewReferencePlane(e[0], e[1], XYZ.BasisZ, _plan).GetReference();
        _fam.Regenerate();
        XYZ along = (e[1] - e[0]).Normalize().CrossProduct(XYZ.BasisZ);
        XYZ from = e[0] - (e[1] - e[0]).Normalize() * DimOffset;
        var refs = new ReferenceArray();
        refs.Append(centre);
        refs.Append(plane);
        _fam.FamilyCreate.NewDimension(_plan, Line.CreateBound(from, from + along), refs).IsLocked = true;
        return plane;
    }

    /// <summary>Bottom and top references in the front view; the bottom is the level or locked to it.</summary>
    private (Reference Lo, Reference Hi) HeightAxis(NwdShape s)
    {
        Reference Plane(double z) => _fam.FamilyCreate.NewReferencePlane(
            new XYZ(s.MinX - DimOffset, 0, z), new XYZ(s.MaxX + DimOffset, 0, z), XYZ.BasisY, _front).GetReference();

        Reference bottom = Math.Abs(s.MinZ) < Tol ? _level : Plane(s.MinZ);
        Reference top = Plane(s.MaxZ);
        _fam.Regenerate();
        if (bottom != _level)
        {
            var refs = new ReferenceArray();
            refs.Append(_level);
            refs.Append(bottom);
            var at = new XYZ(s.MinX - DimOffset, s.MinY, 0);
            _fam.FamilyCreate.NewDimension(_front, Line.CreateBound(at, at + XYZ.BasisZ), refs).IsLocked = true;
        }
        return (bottom, top);
    }

    private void Label(View view, Reference a, Reference b, XYZ at, XYZ along, FamilyParameter fp)
    {
        var refs = new ReferenceArray();
        refs.Append(a);
        refs.Append(b);
        _fam.FamilyCreate.NewDimension(view, Line.CreateBound(at, at + along), refs).FamilyLabel = fp;
    }

    /// <summary>Sets the size to 1.5×, checks the extrusion's extent follows, puts it back.</summary>
    private void Flex(Extrusion extrusion, FamilyParameter size, Func<Extrusion, double> extent)
    {
        double value = _fm.CurrentType.AsDouble(size) ?? 0;
        using var flex = new SubTransaction(_fam);
        flex.Start();
        try
        {
            _fm.Set(size, 1.5 * value);
            _fam.Regenerate();
            double measured = extent(extrusion);
            if (Math.Abs(measured - 1.5 * value) > Tol)
                throw new InvalidOperationException($"'{size.Definition.Name}' does not resize it ({value:0.###} ft x 1.5 measured {measured:0.###} ft)");
        }
        finally
        {
            flex.RollBack();
        }
    }

    private void Align(View view, Reference plane, Reference face)
    {
        _fam.FamilyCreate.NewAlignment(view, plane, face);
    }

    private static Reference Face(Extrusion e, XYZ normal)
    {
        foreach (Solid solid in Solids(e))
            foreach (Face f in solid.Faces)
                if (f is PlanarFace pf && pf.FaceNormal.IsAlmostEqualTo(normal) && pf.Reference != null)
                    return pf.Reference;
        throw new InvalidOperationException($"no face facing {normal}");
    }

    /// <summary>
    /// The extrusion matches the shape. Boxes by bounding box; cylinders by their circle, as Revit's
    /// bounding box of a curved solid is approximate (2026.5: 2.24 ft for a 2.25 ft circle).
    /// </summary>
    private static bool WhereTheMeshWas(Extrusion e, NwdShape s)
    {
        BoundingBoxXYZ b = Box(e);
        if (Math.Abs(b.Min.Z - s.MinZ) > Tol || Math.Abs(b.Max.Z - s.MaxZ) > Tol)
            return false;
        if (s.Kind == NwdShapeKind.Box)
            return Near(b.Min, s.MinX, s.MinY, s.MinZ) && Near(b.Max, s.MaxX, s.MaxY, s.MaxZ);
        return Rim(e) is { } rim && Math.Abs(rim.Radius - s.Radius) < Tol &&
               Math.Abs(rim.Centre.X - s.CentreX) < Tol && Math.Abs(rim.Centre.Y - s.CentreY) < Tol;
    }

    private static BoundingBoxXYZ Box(Extrusion e) =>
        e.get_BoundingBox(null) ?? throw new InvalidOperationException("the extrusion has no extent");

    /// <summary>The circle of an extruded cylinder (from its arc edges), or null.</summary>
    internal static (XYZ Centre, double Radius)? Rim(Extrusion e)
    {
        foreach (Solid solid in Solids(e))
            foreach (Edge edge in solid.Edges)
                if (edge.AsCurve() is Arc arc)
                    return (arc.Center, arc.Radius);
        return null;
    }

    private static IEnumerable<Solid> Solids(Extrusion e)
    {
        GeometryElement? g = e.get_Geometry(new Options { ComputeReferences = true });
        if (g == null)
            yield break;
        foreach (GeometryObject o in g)
            if (o is Solid s && s.Faces.Size > 0)
                yield return s;
    }

    private static bool Near(XYZ p, double x, double y, double z) =>
        Math.Abs(p.X - x) < Tol && Math.Abs(p.Y - y) < Tol && Math.Abs(p.Z - z) < Tol;
}
