namespace KhBatchRunner.NwdFamily;

internal enum NwdShapeKind { Box, Cylinder }

/// <summary>
/// A Navisworks mesh recognised as a simple solid: an axis-aligned box (Min/Max) or a cylinder
/// standing upright (CentreX/Y, Radius, Min.Z/Max.Z). Feet, family coordinates.
/// </summary>
internal sealed class NwdShape
{
    public NwdShapeKind Kind { get; init; }
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MinZ { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }
    public double MaxZ { get; init; }
    public double CentreX { get; init; }
    public double CentreY { get; init; }
    public double Radius { get; init; }
}

/// <summary>
/// Recognises boxes and upright cylinders in a triangle mesh by rule, not by guessing: every
/// triangle must lie on the shape's surface AND the triangles' total area must equal the shape's,
/// so an open box, a box with a notch or a cylinder with a hole is refused (null) and stays a
/// fixed-geometry solid. Tolerance is 1 mm on positions, 0.5 % on areas.
/// </summary>
internal static class NwdShapeFitter
{
    public const double Tolerance = 1.0 / 304.8;
    private const double AreaTolerance = 0.005;
    private const int MinCylinderSides = 8;

    public static NwdShape? Fit(NwdMesh mesh)
    {
        if (mesh.Triangles.Length < 12 * 3 || mesh.Vertices.Length < 8 * 3)
            return null;
        return FitBox(mesh) ?? FitCylinder(mesh);
    }

    private static NwdShape? FitBox(NwdMesh mesh)
    {
        Bounds(mesh, out double[] min, out double[] max);
        double w = max[0] - min[0], d = max[1] - min[1], h = max[2] - min[2];
        if (w < Tolerance || d < Tolerance || h < Tolerance)
            return null;

        double area = 0;
        double[] v = mesh.Vertices;
        int[] t = mesh.Triangles;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            // All three corners on the same face of the box.
            bool onFace = false;
            for (int axis = 0; axis < 3 && !onFace; axis++)
                onFace = Same(v, t, i, axis, min[axis]) || Same(v, t, i, axis, max[axis]);
            if (!onFace)
                return null;
            area += TriangleArea(v, t[i], t[i + 1], t[i + 2]);
        }

        double want = 2 * (w * d + w * h + d * h);
        if (Math.Abs(area - want) > AreaTolerance * want)
            return null;
        return new NwdShape
        {
            Kind = NwdShapeKind.Box,
            MinX = min[0], MinY = min[1], MinZ = min[2],
            MaxX = max[0], MaxY = max[1], MaxZ = max[2],
        };
    }

    private static NwdShape? FitCylinder(NwdMesh mesh)
    {
        Bounds(mesh, out double[] min, out double[] max);
        double z0 = min[2], z1 = max[2], h = z1 - z0;
        if (h < Tolerance)
            return null;
        double[] v = mesh.Vertices;
        int[] t = mesh.Triangles;

        // Every vertex on a cap; the bottom ring (vertices not at the plan centre) gives the centre.
        // A cap's centre vertex (if the cap is a fan) sits near the axis; "near" is relative to the
        // size, as an odd-sided polygon's box centre is a few % of the radius off its true centre.
        var ring = new List<int>();
        double cx = (min[0] + max[0]) / 2, cy = (min[1] + max[1]) / 2;
        double nearAxis = 0.25 * Math.Min(max[0] - min[0], max[1] - min[1]);
        for (int k = 0; k < v.Length / 3; k++)
        {
            double z = v[k * 3 + 2];
            if (Math.Abs(z - z0) > Tolerance && Math.Abs(z - z1) > Tolerance)
                return null;
            if (Math.Abs(z - z0) <= Tolerance && Planar(v, k, cx, cy) > nearAxis)
                ring.Add(k);
        }
        int sides = ring.Count;
        if (sides < MinCylinderSides)
            return null;
        cx = ring.Average(k => v[k * 3]);
        cy = ring.Average(k => v[k * 3 + 1]);
        double r = ring.Average(k => Planar(v, k, cx, cy));
        if (r < Tolerance)
            return null;

        // Every vertex off the axis is on the circle, and the axis is upright (same rings top and bottom).
        for (int k = 0; k < v.Length / 3; k++)
        {
            double p = Planar(v, k, cx, cy);
            if (p > 0.5 * r && Math.Abs(p - r) > Tolerance || p <= 0.5 * r && p > Tolerance)
                return null;
        }

        double capBottom = 0, capTop = 0, side = 0;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            double a = TriangleArea(v, t[i], t[i + 1], t[i + 2]);
            if (Same(v, t, i, 2, z0)) capBottom += a;
            else if (Same(v, t, i, 2, z1)) capTop += a;
            else side += a;
        }

        // The regular polygon the mesh was cut from, not the true circle.
        double wantCap = sides / 2.0 * r * r * Math.Sin(2 * Math.PI / sides);
        double wantSide = sides * 2 * r * Math.Sin(Math.PI / sides) * h;
        if (Math.Abs(capBottom - wantCap) > AreaTolerance * wantCap ||
            Math.Abs(capTop - wantCap) > AreaTolerance * wantCap ||
            Math.Abs(side - wantSide) > AreaTolerance * wantSide)
            return null;

        return new NwdShape
        {
            Kind = NwdShapeKind.Cylinder,
            CentreX = cx, CentreY = cy, Radius = r,
            MinX = cx - r, MinY = cy - r, MinZ = z0,
            MaxX = cx + r, MaxY = cy + r, MaxZ = z1,
        };
    }

    private static void Bounds(NwdMesh mesh, out double[] min, out double[] max)
    {
        min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
        max = new[] { double.MinValue, double.MinValue, double.MinValue };
        double[] v = mesh.Vertices;
        for (int k = 0; k + 2 < v.Length; k += 3)
            for (int axis = 0; axis < 3; axis++)
            {
                min[axis] = Math.Min(min[axis], v[k + axis]);
                max[axis] = Math.Max(max[axis], v[k + axis]);
            }
    }

    private static bool Same(double[] v, int[] t, int i, int axis, double value) =>
        Math.Abs(v[t[i] * 3 + axis] - value) <= Tolerance &&
        Math.Abs(v[t[i + 1] * 3 + axis] - value) <= Tolerance &&
        Math.Abs(v[t[i + 2] * 3 + axis] - value) <= Tolerance;

    private static double Planar(double[] v, int k, double cx, double cy)
    {
        double dx = v[k * 3] - cx, dy = v[k * 3 + 1] - cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double TriangleArea(double[] v, int a, int b, int c)
    {
        double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
        double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
        double x = uy * wz - uz * wy, y = uz * wx - ux * wz, z = ux * wy - uy * wx;
        return Math.Sqrt(x * x + y * y + z * z) / 2;
    }
}
