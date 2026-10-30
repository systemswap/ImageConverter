namespace ImageConverter.Viewer3D.Geometry;

/// <summary>What a measured value means.</summary>
public enum Quantity
{
    /// <summary>Perpendicular or straight distance.</summary>
    Distance,

    /// <summary>Angle in degrees.</summary>
    Angle,

    /// <summary>Distance between the axes (cylinders) or from an axis to a plane or point.</summary>
    AxisDistance,

    /// <summary>Shortest distance between the surfaces.</summary>
    MinDistance,

    /// <summary>Largest distance across both cylinders (outer edge to outer edge).</summary>
    MaxDistance,

    Diameter,

    Radius,

    DeltaX,

    DeltaY,

    DeltaZ,
}

/// <summary>One measured value.</summary>
/// <param name="Quantity">What it means.</param>
/// <param name="Value">In file units, or degrees for <see cref="Quantity.Angle"/>.</param>
/// <param name="Of">0 = the pair; 1 or 2 = belongs to the first or second pick (diameters).</param>
public readonly record struct MeasureValue(Quantity Quantity, double Value, int Of = 0);

/// <summary>What a click selected.</summary>
public enum PickKind
{
    /// <summary>A whole face.</summary>
    Face,

    /// <summary>One point on the surface (Shift + click).</summary>
    Point,
}

/// <summary>A click on the model.</summary>
/// <param name="Kind">Face or point.</param>
/// <param name="Face">The face under the click (also for points).</param>
/// <param name="Point">Where the click met the surface.</param>
public readonly record struct Pick(PickKind Kind, int Face, D3 Point);

/// <summary>Result of measuring between two picks.</summary>
/// <param name="Values">The values; the first is the main one.</param>
/// <param name="From">Start of the dimension line.</param>
/// <param name="To">End of the dimension line; equal to <paramref name="From"/> when there is no line (angle only).</param>
public sealed record Measurement(IReadOnlyList<MeasureValue> Values, D3 From, D3 To)
{
    public bool HasLine => From != To;
}

/// <summary>
/// Measures between faces and points of one mesh: perpendicular distances of parallel planes, axis distances of
/// cylinders, angles, and shortest distances where nothing simpler applies.
/// </summary>
/// <remarks>
/// Everything is computed from the read triangles in double precision, not from the display. Shortest distances use a
/// hierarchy per face, built on first use and kept for the faces measured last.
/// </remarks>
public sealed class Measurer(Mesh mesh, FaceSet faces)
{
    /// <summary>Planes closer to parallel than this (degrees) count as parallel.</summary>
    public const double ParallelPlanes = 0.1;

    /// <summary>Axes from fitted cylinders closer to parallel than this (degrees) count as parallel.</summary>
    public const double ParallelAxes = 0.5;

    private const int CachedHierarchies = 6;
    private readonly List<(int Face, Bvh Bvh)> _cache = [];
    private readonly Lock _lock = new();

    /// <summary>Values shown for a single pick: diameter (closed cylinder) or radius (partial cylinder); empty otherwise.</summary>
    public IReadOnlyList<MeasureValue> Single(Pick a)
    {
        if (a.Kind != PickKind.Face) return [];
        var f = faces.Faces[a.Face];
        if (f.Kind != FaceKind.Cylinder) return [];
        return [f.Coverage >= 180 ? new MeasureValue(Quantity.Diameter, 2 * f.Radius, 1) : new MeasureValue(Quantity.Radius, f.Radius, 1)];
    }

    /// <summary>Measures between two picks.</summary>
    /// <param name="a">First pick.</param>
    /// <param name="b">Second pick.</param>
    /// <param name="ct">Cancels a shortest-distance search.</param>
    public Measurement Measure(Pick a, Pick b, CancellationToken ct = default)
    {
        if (a.Kind == PickKind.Point && b.Kind == PickKind.Point) return PointPoint(a.Point, b.Point);
        if (a.Kind == PickKind.Point) return PointFace(a.Point, faces.Faces[b.Face], swapped: false, ct);
        if (b.Kind == PickKind.Point) return PointFace(b.Point, faces.Faces[a.Face], swapped: true, ct);

        var fa = faces.Faces[a.Face];
        var fb = faces.Faces[b.Face];
        return (fa.Kind, fb.Kind) switch
        {
            (FaceKind.Plane, FaceKind.Plane) => PlanePlane(fa, a.Point, fb, b.Point, ct),
            (FaceKind.Cylinder, FaceKind.Cylinder) => CylinderCylinder(fa, a.Point, fb),
            (FaceKind.Cylinder, FaceKind.Plane) => CylinderPlane(fa, a.Point, fb, 1),
            (FaceKind.Plane, FaceKind.Cylinder) => CylinderPlane(fb, b.Point, fa, 2),
            _ => Shortest(fa, fb, a.Point, b.Point, ct),
        };
    }

    private static Measurement PointPoint(D3 p, D3 q)
    {
        var d = q - p;
        return new Measurement(
        [
            new MeasureValue(Quantity.Distance, d.Length),
            new MeasureValue(Quantity.DeltaX, Math.Abs(d.X)),
            new MeasureValue(Quantity.DeltaY, Math.Abs(d.Y)),
            new MeasureValue(Quantity.DeltaZ, Math.Abs(d.Z)),
        ], p, q);
    }

    private Measurement PointFace(D3 p, Face f, bool swapped, CancellationToken ct)
    {
        Measurement m;
        switch (f.Kind)
        {
            case FaceKind.Plane:
            {
                var off = D3.Dot(p - f.Centre, f.Normal);
                m = new Measurement([new MeasureValue(Quantity.Distance, Math.Abs(off))], p, p - f.Normal * off);
                break;
            }
            case FaceKind.Cylinder:
            {
                var foot = f.AxisPoint + f.Axis * D3.Dot(p - f.AxisPoint, f.Axis);
                var d = (p - foot).Length;
                m = new Measurement(
                [
                    new MeasureValue(Quantity.Distance, Math.Abs(d - f.Radius)),
                    new MeasureValue(Quantity.AxisDistance, d),
                    Single(new Pick(PickKind.Face, f.Id, p))[0] with { Of = swapped ? 1 : 2 },
                ], p, foot);
                break;
            }
            default:
            {
                var (q, _, d) = Hierarchy(f.Id).Nearest(p);
                m = new Measurement([new MeasureValue(Quantity.Distance, d)], p, q);
                break;
            }
        }
        return swapped ? m with { From = m.To, To = m.From } : m;
    }

    private Measurement PlanePlane(Face a, D3 hitA, Face b, D3 hitB, CancellationToken ct)
    {
        var angle = PlaneAngle(D3.AngleDegrees(a.Normal, b.Normal));
        if (angle < ParallelPlanes)
        {
            // Perpendicular distance, drawn from the click on A straight onto B
            var n = a.Normal;
            var d = D3.Dot(b.Centre - hitA, n);
            var distance = Math.Abs(D3.Dot(b.Centre - a.Centre, n));
            return new Measurement([new MeasureValue(Quantity.Distance, distance), new MeasureValue(Quantity.Angle, angle)], hitA, hitA + n * d);
        }
        var shortest = Shortest(a, b, hitA, hitB, ct);
        return shortest with { Values = [new MeasureValue(Quantity.Angle, angle), .. shortest.Values] };
    }

    private static Measurement CylinderCylinder(Face a, D3 hitA, Face b)
    {
        var angle = PlaneAngle(D3.AngleDegrees(a.Axis, b.Axis));
        var da = new MeasureValue(Quantity.Diameter, 2 * a.Radius, 1);
        var db = new MeasureValue(Quantity.Diameter, 2 * b.Radius, 2);
        if (angle < ParallelAxes)
        {
            var axis = a.Axis;
            var pa = a.AxisPoint + axis * D3.Dot(hitA - a.AxisPoint, axis);   // level with the click
            var pb = b.AxisPoint + axis * D3.Dot(pa - b.AxisPoint, axis);
            var d = (pb - pa).Length;
            double rMin = Math.Min(a.Radius, b.Radius), rMax = Math.Max(a.Radius, b.Radius);
            // Apart: gap between the surfaces; one inside the other (tube): thinnest wall
            var min = d >= a.Radius + b.Radius ? d - a.Radius - b.Radius
                : d + rMin <= rMax ? rMax - rMin - d
                : 0;
            return new Measurement(
            [
                new MeasureValue(Quantity.AxisDistance, d),
                new MeasureValue(Quantity.MinDistance, min),
                new MeasureValue(Quantity.MaxDistance, d + a.Radius + b.Radius),
                da, db,
            ], pa, pb);
        }

        // Skew axes: shortest connection of the two lines
        var (p, q) = LineLine(a.AxisPoint, a.Axis, b.AxisPoint, b.Axis);
        return new Measurement([new MeasureValue(Quantity.Angle, angle), new MeasureValue(Quantity.AxisDistance, (q - p).Length), da, db], p, q);
    }

    private static Measurement CylinderPlane(Face c, D3 hitC, Face plane, int cylinderIs)
    {
        // Angle between axis and plane: 0° = axis lies parallel to the plane
        var angle = Math.Abs(90 - D3.AngleDegrees(c.Axis, plane.Normal));
        var diameter = new MeasureValue(c.Coverage >= 180 ? Quantity.Diameter : Quantity.Radius, c.Coverage >= 180 ? 2 * c.Radius : c.Radius, cylinderIs);
        Measurement m;
        if (angle < ParallelAxes)
        {
            var pa = c.AxisPoint + c.Axis * D3.Dot(hitC - c.AxisPoint, c.Axis);
            var off = D3.Dot(pa - plane.Centre, plane.Normal);
            var d = Math.Abs(off);
            m = new Measurement(
            [
                new MeasureValue(Quantity.AxisDistance, d),
                new MeasureValue(Quantity.MinDistance, Math.Max(0, d - c.Radius)),
                new MeasureValue(Quantity.MaxDistance, d + c.Radius),
                diameter,
            ], pa, pa - plane.Normal * off);
        }
        else
        {
            m = new Measurement([new MeasureValue(Quantity.Angle, angle), diameter], hitC, hitC);
        }
        return cylinderIs == 1 ? m : m with { From = m.To, To = m.From };
    }

    private Measurement Shortest(Face a, Face b, D3 hitA, D3 hitB, CancellationToken ct)
    {
        var (pa, pb, d) = Bvh.Nearest(Hierarchy(a.Id), Hierarchy(b.Id), ct);
        if (d <= 0 || !double.IsFinite(d))
        {
            // Touching: no line, the value sits between the two clicks
            var mid = (hitA + hitB) * 0.5;
            return new Measurement([new MeasureValue(Quantity.MinDistance, 0)], mid, mid);
        }
        return new Measurement([new MeasureValue(Quantity.MinDistance, d)], pa, pb);
    }

    /// <summary>Angle between planes or lines (0 … 90°) from the angle between their directions (0 … 180°).</summary>
    private static double PlaneAngle(double between) => Math.Min(between, 180 - between);

    /// <summary>Closest points of two infinite lines.</summary>
    private static (D3 P, D3 Q) LineLine(D3 p1, D3 d1, D3 p2, D3 d2)
    {
        var r = p1 - p2;
        var a = D3.Dot(d1, d1);
        var b = D3.Dot(d1, d2);
        var c = D3.Dot(d1, r);
        var e = D3.Dot(d2, d2);
        var f = D3.Dot(d2, r);
        var denom = a * e - b * b;
        if (Math.Abs(denom) < 1e-18) return (p1, p2 + d2 * (f / e));
        var s = (b * f - c * e) / denom;
        var t = (a * f - b * c) / denom;
        return (p1 + d1 * s, p2 + d2 * t);
    }

    /// <summary>Hierarchy over the triangles of one face, from the cache or built now.</summary>
    private Bvh Hierarchy(int face)
    {
        lock (_lock)
        {
            var i = _cache.FindIndex(e => e.Face == face);
            if (i >= 0)
            {
                var hit = _cache[i];
                _cache.RemoveAt(i);
                _cache.Insert(0, hit);
                return hit.Bvh;
            }
        }
        var bvh = Bvh.Build(mesh, faces.TrianglesOf(face).ToArray());
        lock (_lock)
        {
            _cache.Insert(0, (face, bvh));
            if (_cache.Count > CachedHierarchies) _cache.RemoveAt(_cache.Count - 1);
        }
        return bvh;
    }
}
