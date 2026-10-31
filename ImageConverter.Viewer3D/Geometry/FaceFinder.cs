namespace ImageConverter.Viewer3D.Geometry;

/// <summary>What a face is geometrically.</summary>
public enum FaceKind
{
    /// <summary>Flat: all triangles in one plane.</summary>
    Plane,

    /// <summary>Part of a circular cylinder (bore, shaft, fillet).</summary>
    Cylinder,

    /// <summary>Anything else (sphere, cone, organic shape); only distances to it are measured.</summary>
    Freeform,
}

/// <summary>A face of the model: connected triangles that belong to one plane, one cylinder or one freeform patch.</summary>
public sealed class Face
{
    public required int Id { get; init; }

    public required FaceKind Kind { get; init; }

    /// <summary>Area in square file units.</summary>
    public required double Area { get; init; }

    /// <summary>Area-weighted centre of the triangles.</summary>
    public required D3 Centre { get; init; }

    /// <summary>Plane: unit normal (pointing out of a consistently oriented model). Otherwise the mean normal.</summary>
    public D3 Normal { get; init; }

    /// <summary>Cylinder: unit direction of the axis.</summary>
    public D3 Axis { get; init; }

    /// <summary>Cylinder: a point on the axis, level with <see cref="Centre"/>.</summary>
    public D3 AxisPoint { get; init; }

    /// <summary>Cylinder: radius.</summary>
    public double Radius { get; init; }

    /// <summary>Cylinder: covered angle around the axis in degrees (360 for a full bore or shaft).</summary>
    public double Coverage { get; init; }

    /// <summary>Range of this face in <see cref="FaceSet.Triangles"/>.</summary>
    public required int First { get; init; }

    public required int Count { get; init; }
}

/// <summary>All faces of a mesh.</summary>
/// <param name="FaceOf">Face of each triangle.</param>
/// <param name="Faces">The faces, indexed by <see cref="Face.Id"/>.</param>
/// <param name="Triangles">Triangle indices grouped by face; face f owns <c>Triangles[First .. First + Count)</c>.</param>
public sealed record FaceSet(int[] FaceOf, Face[] Faces, int[] Triangles)
{
    public ReadOnlySpan<int> TrianglesOf(int face) => Triangles.AsSpan(Faces[face].First, Faces[face].Count);
}

/// <summary>
/// Splits a mesh into faces: planes, cylinders and freeform patches, so that one click selects a whole face.
/// </summary>
/// <remarks>
/// <para>An STL file has only loose triangles, and a curved surface arrives as a strip of flat facets. The steps:</para>
/// <list type="number">
/// <item>Planes: starting from the largest triangles (their normals are the most accurate), neighbours join while
/// all their corners lie within a tolerance of the start plane (a few millionths of the model size, at least the
/// float resolution of the coordinates).</item>
/// <item>Facets: a plane is taken as a facet of a curved surface when more than a quarter of its border meets
/// neighbours at a gentle angle (≤ <see cref="SmoothAngle"/>°) and it is not much broader than those neighbours –
/// so the top of a box with rounded edges stays a plane, while the strips of a bore become facets, also the last strip
/// of an arc that ends at a sharp edge (one gentle long side).</item>
/// <item>Cylinders: neighbouring facets whose normals are all perpendicular to one axis (the axis is the eigenvector
/// of the smallest eigenvalue of Σ A n nᵀ), which meet along edges parallel to that axis, and whose corners fit one
/// circle around it.</item>
/// <item>Freeform: the remaining facets, joined across gentle edges.</item>
/// </list>
/// <para>Each step visits every triangle a bounded number of times; a failed cylinder attempt removes its facets from
/// further attempts, so crafted meshes cannot make it quadratic.</para>
/// </remarks>
public static class FaceFinder
{
    /// <summary>Largest angle between neighbouring facets of one curved surface, in degrees.</summary>
    public const double SmoothAngle = 40;

    /// <summary>Normals of a cylinder's facets may deviate this much (degrees) from perpendicular to the axis.</summary>
    private const double CylinderAxisAngle = 2;

    /// <summary>Shared edges of neighbouring strips must run within 2° of the axis.</summary>
    private static readonly double CosEdgeAlongAxis = Math.Cos(2 * Math.PI / 180);

    /// <summary>Finds the faces.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="topology">Its neighbourhood (<see cref="MeshTopology.Build"/>).</param>
    /// <param name="ct">Cancels the search.</param>
    /// <param name="resolution">Relative rounding of the coordinates (<see cref="StlFile.Resolution"/>).</param>
    public static FaceSet Find(Mesh mesh, MeshTopology topology, CancellationToken ct = default, double resolution = StlReader.FloatResolution)
    {
        var t = mesh.TriangleCount;
        var nbr = topology.Neighbours;
        var idx = mesh.Indices;
        var bounds = mesh.Bounds;
        var maxCoord = Math.Max(Math.Max(Math.Abs(bounds.Min.X), Math.Abs(bounds.Max.X)),
            Math.Max(Math.Max(Math.Abs(bounds.Min.Y), Math.Abs(bounds.Max.Y)), Math.Max(Math.Abs(bounds.Min.Z), Math.Abs(bounds.Max.Z))));
        // Coplanar tolerance: relative to the model, but never below a few float steps of the coordinates
        // Rounding of the corners in the file (half a step at the largest coordinate)
        var noise = maxCoord * resolution;
        var tolerance = Math.Max(2e-5 * bounds.Diagonal, 8 * noise);

        // ---- 1. Planes, seeded from the largest triangles
        var areas = new float[t];
        for (var i = 0; i < t; i++) areas[i] = (float)(mesh.AreaNormal(i).Length * 0.5);
        var order = LargestFirst(areas);
        ct.ThrowIfCancellationRequested();

        var region = new int[t];
        Array.Fill(region, -1);
        var regions = 0;
        var queue = new Queue<int>();
        var tick = 0;
        foreach (var seed in order)
        {
            if (region[seed] >= 0) continue;
            if ((++tick & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var r = regions++;
            region[seed] = r;
            var n = mesh.AreaNormal(seed).Normalized();
            if (n == D3.Zero) continue;   // degenerate (zero area): a region of its own
            var p = D3.From(mesh.Vertex(idx[3 * seed]));
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                for (var k = 0; k < 3; k++)
                {
                    var q = nbr[3 * cur + k];
                    if (q < 0 || region[q] >= 0) continue;
                    var nq = mesh.AreaNormal(q);
                    if (D3.Dot(nq, n) <= 0.5 * nq.Length) continue;   // folded back or steep: not this plane
                    var (a, b, c) = mesh.CornersD(q);
                    if (Math.Abs(D3.Dot(a - p, n)) > tolerance || Math.Abs(D3.Dot(b - p, n)) > tolerance
                                                               || Math.Abs(D3.Dot(c - p, n)) > tolerance) continue;
                    region[q] = r;
                    queue.Enqueue(q);
                }
            }
        }
        order = null!;
        ct.ThrowIfCancellationRequested();

        // ---- 2. Region figures: area, normal, border length, gentle border length
        var rArea = new double[regions];
        var rNormal = new float[3 * regions];
        for (var i = 0; i < t; i++)
        {
            var r = region[i];
            var an = mesh.AreaNormal(i);
            rArea[r] += areas[i];
            rNormal[3 * r] += (float)an.X;
            rNormal[3 * r + 1] += (float)an.Y;
            rNormal[3 * r + 2] += (float)an.Z;
        }
        for (var r = 0; r < regions; r++)
        {
            var nr = new D3(rNormal[3 * r], rNormal[3 * r + 1], rNormal[3 * r + 2]).Normalized();
            rNormal[3 * r] = (float)nr.X;
            rNormal[3 * r + 1] = (float)nr.Y;
            rNormal[3 * r + 2] = (float)nr.Z;
        }
        ct.ThrowIfCancellationRequested();

        var cosSmooth = Math.Cos(SmoothAngle * Math.PI / 180);
        var border = new float[regions];
        var gentle = new float[regions];
        for (var h = 0; h < 3 * t; h++)
        {
            var tri = h / 3;
            var q = nbr[h];
            var r = region[tri];
            if (q >= 0 && region[q] == r) continue;
            var len = EdgeLength(mesh, h);
            border[r] += len;
            if (q >= 0 && NormalDot(rNormal, r, region[q]) >= cosSmooth) gentle[r] += len;
        }
        // Breadth of a region (area / border) against the broadest gently joined neighbour
        var breadth = new float[regions];
        for (var r = 0; r < regions; r++) breadth[r] = border[r] > 0 ? (float)(rArea[r] / border[r]) : float.MaxValue;
        var neighbourBreadth = new float[regions];
        for (var h = 0; h < 3 * t; h++)
        {
            var q = nbr[h];
            if (q < 0) continue;
            int r = region[h / 3], s = region[q];
            if (r == s || NormalDot(rNormal, r, s) < cosSmooth) continue;
            neighbourBreadth[r] = Math.Max(neighbourBreadth[r], breadth[s]);
        }
        var facet = new bool[regions];
        for (var r = 0; r < regions; r++)
            facet[r] = rArea[r] <= 0 || gentle[r] > 0.25f * border[r] && breadth[r] < 3 * neighbourBreadth[r];
        border = gentle = breadth = neighbourBreadth = null!;
        ct.ThrowIfCancellationRequested();

        // Triangles of each region (counting sort) for walking from region to region
        var (regionFirst, regionTris) = Group(region, regions);
        var around = new RegionNeighbours(regionFirst, regionTris, nbr, region, regions);

        // ---- 3. Cylinders from facets
        var group = new int[regions];   // −1 none yet, −2 tried and failed, ≥ 0 cylinder number
        Array.Fill(group, -1);
        var cylinders = new List<(D3 Axis, D3 AxisPoint, double Radius, double Coverage)>();
        var members = new List<int>();
        var regionQueue = new Queue<int>();
        var sinAxis = Math.Sin(CylinderAxisAngle * Math.PI / 180);
        for (var r0 = 0; r0 < regions; r0++)
        {
            if (!facet[r0] || group[r0] != -1 || rArea[r0] <= 0) continue;
            ct.ThrowIfCancellationRequested();

            // A gently joined second facet gives the first axis estimate
            var n0 = RNormal(rNormal, r0);
            var r1 = -1;
            foreach (var s in around.Of(r0))
            {
                if (!facet[s] || group[s] != -1 || rArea[s] <= 0) continue;
                var ang = D3.AngleDegrees(n0, RNormal(rNormal, s));
                if (ang > 0.5 && ang <= SmoothAngle)
                {
                    r1 = s;
                    break;
                }
            }
            if (r1 < 0) continue;

            var axis = D3.Cross(n0, RNormal(rNormal, r1)).Normalized();
            var m = new Sym3();
            members.Clear();
            var marker = -3 - r0;   // temporary mark: member of this attempt
            Add(r0);
            Add(r1);
            regionQueue.Clear();
            regionQueue.Enqueue(r0);
            regionQueue.Enqueue(r1);
            while (regionQueue.Count > 0)
            {
                var cur = regionQueue.Dequeue();
                foreach (var s in around.Of(cur))
                {
                    if (!facet[s] || group[s] != -1 || rArea[s] <= 0) continue;
                    var ns = RNormal(rNormal, s);
                    if (NormalDot(rNormal, cur, s) < cosSmooth) continue;
                    if (Math.Abs(D3.Dot(ns, axis)) > sinAxis) continue;
                    if (!EdgeAlongAxis(cur, s, axis)) continue;
                    Add(s);
                    regionQueue.Enqueue(s);
                    if (members.Count >= 3) axis = m.SmallestEigenvector(axis);
                }
            }

            // Closed ring: as many links between member facets as facets (an open arc has one fewer)
            var links = 0;
            foreach (var s in members)
                foreach (var q in around.Of(s))
                    if (group[q] == marker) links++;
            var closed = links / 2 >= members.Count;

            var fit = members.Count >= 3 && m.SmallestEigenvalueShare(axis) <= sinAxis * sinAxis
                ? FitCylinder(mesh, axis, members, regionFirst, regionTris, 3 * noise, closed)
                : null;
            if (fit is { } c)
            {
                var id = cylinders.Count;
                cylinders.Add(c);
                foreach (var s in members) group[s] = id;
            }
            else
            {
                foreach (var s in members) group[s] = -2;
            }

            // Strips of a cylinder meet along lines parallel to its axis; the fan of triangles at the pole of a sphere
            // meets along spokes in all directions
            bool EdgeAlongAxis(int a, int b, D3 dir)
            {
                var best = D3.Zero;
                for (var i = regionFirst[b]; i < regionFirst[b + 1]; i++)
                {
                    var tri = regionTris[i];
                    for (var k = 0; k < 3; k++)
                    {
                        var q = nbr[3 * tri + k];
                        if (q < 0 || region[q] != a) continue;
                        var e = D3.From(mesh.Vertex(idx[3 * tri + (k + 1) % 3])) - D3.From(mesh.Vertex(idx[3 * tri + k]));
                        if (e.LengthSquared > best.LengthSquared) best = e;
                    }
                }
                return best != D3.Zero && Math.Abs(D3.Dot(best.Normalized(), dir)) >= CosEdgeAlongAxis;
            }

            void Add(int s)
            {
                members.Add(s);
                group[s] = marker;
                var ns = RNormal(rNormal, s);
                m.AddOuter(ns, rArea[s]);
            }
        }
        ct.ThrowIfCancellationRequested();

        // ---- 4. Faces: planes, cylinders, freeform patches across gentle edges
        var faceOfRegion = new int[regions];
        Array.Fill(faceOfRegion, -1);
        var kinds = new List<FaceKind>();
        var cylOfFace = new List<int>();
        var cylFace = new int[cylinders.Count];
        Array.Fill(cylFace, -1);
        for (var r = 0; r < regions; r++)
        {
            if (faceOfRegion[r] >= 0) continue;
            if (!facet[r])
            {
                faceOfRegion[r] = kinds.Count;
                kinds.Add(FaceKind.Plane);
                cylOfFace.Add(-1);
                continue;
            }
            if (group[r] >= 0)
            {
                var g = group[r];
                if (cylFace[g] < 0)
                {
                    cylFace[g] = kinds.Count;
                    kinds.Add(FaceKind.Cylinder);
                    cylOfFace.Add(g);
                }
                faceOfRegion[r] = cylFace[g];
                continue;
            }

            // Freeform: flood over gentle edges to other leftover facets
            var f = kinds.Count;
            kinds.Add(FaceKind.Freeform);
            cylOfFace.Add(-1);
            faceOfRegion[r] = f;
            regionQueue.Clear();
            regionQueue.Enqueue(r);
            while (regionQueue.Count > 0)
            {
                var cur = regionQueue.Dequeue();
                foreach (var s in around.Of(cur))
                {
                    if (faceOfRegion[s] >= 0 || !facet[s] || group[s] >= 0) continue;
                    if (rArea[cur] > 0 && rArea[s] > 0 && NormalDot(rNormal, cur, s) < cosSmooth) continue;
                    faceOfRegion[s] = f;
                    regionQueue.Enqueue(s);
                }
            }
        }

        var faceOf = new int[t];
        for (var i = 0; i < t; i++) faceOf[i] = faceOfRegion[region[i]];
        region = null!;
        var (faceFirst, faceTris) = Group(faceOf, kinds.Count);

        var faces = new Face[kinds.Count];
        for (var f = 0; f < faces.Length; f++)
        {
            double area = 0;
            D3 centre = D3.Zero, normal = D3.Zero;
            for (var i = faceFirst[f]; i < faceFirst[f + 1]; i++)
            {
                var tri = faceTris[i];
                var an = mesh.AreaNormal(tri);
                var (a, b, c) = mesh.CornersD(tri);
                double w = areas[tri];
                area += w;
                centre += (a + b + c) * (w / 3);
                normal += an;
            }
            if (area > 0) centre /= area;
            var cyl = cylOfFace[f] >= 0 ? cylinders[cylOfFace[f]] : default;
            faces[f] = new Face
            {
                Id = f,
                Kind = kinds[f],
                Area = area,
                Centre = centre,
                Normal = normal.Normalized(),
                Axis = cyl.Axis,
                // Axis point level with the centre of the face
                AxisPoint = cylOfFace[f] >= 0 ? cyl.AxisPoint + cyl.Axis * D3.Dot(centre - cyl.AxisPoint, cyl.Axis) : D3.Zero,
                Radius = cyl.Radius,
                Coverage = cyl.Coverage,
                First = faceFirst[f],
                Count = faceFirst[f + 1] - faceFirst[f],
            };
        }
        return new FaceSet(faceOf, faces, faceTris);
    }

    /// <summary>
    /// Circle through the corners of the facets in the plane perpendicular to the axis (algebraic least squares after
    /// I. Kåsa: A circle fitting procedure and its error analysis, IEEE Trans. Instrum. Meas. 25(1), 1976).
    /// </summary>
    /// <remarks>
    /// Accepted only if every corner lies on the circle within the rounding of the file (plus a millionth of the radius):
    /// a tessellated cylinder from a CAD program has its corners exactly on the surface, while a strip of a sphere or
    /// another doubly curved surface also has normals perpendicular to one axis but corners off the circle. An open
    /// piece must also cover at least 15° around the axis and be at least 2 % of its radius long along it – a finely
    /// divided sphere has strips whose corners lie on a circle within any tolerance, but they are only one facet wide.
    /// </remarks>
    private static (D3 Axis, D3 AxisPoint, double Radius, double Coverage)? FitCylinder(
        Mesh mesh, D3 axis, List<int> members, int[] regionFirst, int[] regionTris, double rounding, bool closed)
    {
        var u = axis.AnyPerpendicular();
        var v = D3.Cross(axis, u);
        var idx = mesh.Indices;

        // Reference point: first corner, for numerically small coordinates
        var origin = D3.From(mesh.Vertex(idx[3 * regionTris[regionFirst[members[0]]]]));
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sn = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var (x, y) in Corners())
        {
            var z = x * x + y * y;
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            sx += x;
            sy += y;
            sn += 1;
            sxz += x * z;
            syz += y * z;
            sz += z;
        }
        // Solve [sxx sxy sx; sxy syy sy; sx sy n] [D E F]ᵀ = −[sxz syz sz]ᵀ
        if (!Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, sn, -sxz, -syz, -sz, out var d, out var e, out var f)) return null;
        double cx = -d / 2, cy = -e / 2;
        var r2 = cx * cx + cy * cy - f;
        if (!(r2 > 0)) return null;
        var radius = Math.Sqrt(r2);

        var angles = new List<double>();
        foreach (var (x, y) in Corners()) angles.Add(Math.Atan2(y - cy, x - cx));
        angles.Sort();
        var gap = 2 * Math.PI - (angles[^1] - angles[0]);
        for (var i = 1; i < angles.Count; i++) gap = Math.Max(gap, angles[i] - angles[i - 1]);
        // A ring of neighbours is a closed cylinder only if its corners go all round the axis (largest gap about one
        // facet); facets around a single corner of a finely divided surface are a ring of neighbours, too
        if (closed && gap > 2.5 * 2 * Math.PI / members.Count) closed = false;
        var coverage = closed ? 2 * Math.PI : 2 * Math.PI - gap;
        if (coverage < 15 * Math.PI / 180) return null;

        var limit = 1e-6 * radius + rounding;
        foreach (var (x, y) in Corners())
            if (Math.Abs(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius) > limit) return null;

        // An open piece must be at least 2 % of its radius long along the axis: a strip of a sphere one facet wide
        // also has its corners on a circle, but no length
        if (!closed)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var r in members)
                for (var i = regionFirst[r]; i < regionFirst[r + 1]; i++)
                    for (var k = 0; k < 3; k++)
                    {
                        var t = D3.Dot(D3.From(mesh.Vertex(idx[3 * regionTris[i] + k])) - origin, axis);
                        lo = Math.Min(lo, t);
                        hi = Math.Max(hi, t);
                    }
            if (hi - lo < 0.02 * radius) return null;
        }

        coverage *= 180 / Math.PI;
        var centre = origin + u * cx + v * cy;
        return (axis, centre, radius, coverage);

        IEnumerable<(double X, double Y)> Corners()
        {
            foreach (var r in members)
                for (var i = regionFirst[r]; i < regionFirst[r + 1]; i++)
                {
                    var tri = regionTris[i];
                    for (var k = 0; k < 3; k++)
                    {
                        var p = D3.From(mesh.Vertex(idx[3 * tri + k])) - origin;
                        yield return (D3.Dot(p, u), D3.Dot(p, v));
                    }
                }
        }
    }

    private static bool Solve3(double a11, double a12, double a13, double a21, double a22, double a23, double a31, double a32, double a33,
        double b1, double b2, double b3, out double x1, out double x2, out double x3)
    {
        var det = a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31);
        var scale = Math.Abs(a11 * a22 * a33) + Math.Abs(a12 * a23 * a31) + Math.Abs(a13 * a21 * a32) + 1e-300;
        if (Math.Abs(det) < 1e-14 * scale)
        {
            x1 = x2 = x3 = 0;
            return false;
        }
        x1 = (b1 * (a22 * a33 - a23 * a32) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a32 - a22 * b3)) / det;
        x2 = (a11 * (b2 * a33 - a23 * b3) - b1 * (a21 * a33 - a23 * a31) + a13 * (a21 * b3 - b2 * a31)) / det;
        x3 = (a11 * (a22 * b3 - b2 * a32) - a12 * (a21 * b3 - b2 * a31) + b1 * (a21 * a32 - a22 * a31)) / det;
        return true;
    }

    /// <summary>
    /// Distinct regions next to a region (the region itself excluded), without allocating: the result list is reused
    /// and a stamp per region removes duplicates. The list is valid until the next call, so calls must not be nested.
    /// </summary>
    private sealed class RegionNeighbours(int[] first, int[] tris, int[] nbr, int[] region, int regions)
    {
        private readonly int[] _stamp = new int[regions];
        private readonly List<int> _result = [];
        private int _now;

        public List<int> Of(int r)
        {
            _result.Clear();
            if (++_now == int.MaxValue)
            {
                Array.Clear(_stamp);
                _now = 1;
            }
            _stamp[r] = _now;
            for (var i = first[r]; i < first[r + 1]; i++)
            {
                var tri = tris[i];
                for (var k = 0; k < 3; k++)
                {
                    var q = nbr[3 * tri + k];
                    if (q < 0) continue;
                    var s = region[q];
                    if (_stamp[s] == _now) continue;
                    _stamp[s] = _now;
                    _result.Add(s);
                }
            }
            return _result;
        }
    }

    /// <summary>
    /// Triangles roughly by decreasing area, in linear time: a counting sort over the exponent and first mantissa bit
    /// of the area (steps of √2), which is all that choosing good starting triangles needs.
    /// </summary>
    private static int[] LargestFirst(float[] areas)
    {
        const int buckets = 512;
        static int Key(float a) => a > 0 ? Math.Clamp(BitConverter.SingleToInt32Bits(a) >> 22, 0, buckets - 1) : 0;
        var start = new int[buckets + 1];
        foreach (var a in areas) start[buckets - 1 - Key(a) + 1]++;
        for (var b = 0; b < buckets; b++) start[b + 1] += start[b];
        var order = new int[areas.Length];
        for (var i = 0; i < areas.Length; i++) order[start[buckets - 1 - Key(areas[i])]++] = i;
        return order;
    }

    /// <summary>Indices 0 … n − 1 grouped by their key (counting sort): group g owns <c>items[first[g] .. first[g + 1])</c>.</summary>
    private static (int[] First, int[] Items) Group(int[] keyOf, int groups)
    {
        var first = new int[groups + 1];
        foreach (var k in keyOf) first[k + 1]++;
        for (var g = 0; g < groups; g++) first[g + 1] += first[g];
        var fill = (int[])first.Clone();
        var items = new int[keyOf.Length];
        for (var i = 0; i < keyOf.Length; i++) items[fill[keyOf[i]]++] = i;
        return (first, items);
    }

    private static D3 RNormal(float[] n, int r) => new(n[3 * r], n[3 * r + 1], n[3 * r + 2]);

    private static double NormalDot(float[] n, int a, int b) =>
        (double)n[3 * a] * n[3 * b] + (double)n[3 * a + 1] * n[3 * b + 1] + (double)n[3 * a + 2] * n[3 * b + 2];

    private static float EdgeLength(Mesh mesh, int halfEdge)
    {
        var idx = mesh.Indices;
        var a = idx[halfEdge];
        var b = idx[halfEdge % 3 == 2 ? halfEdge - 2 : halfEdge + 1];
        return (mesh.Vertex(a) - mesh.Vertex(b)).Length();
    }
}

/// <summary>Symmetric 3 × 3 matrix built from outer products, with its eigenvector of the smallest eigenvalue.</summary>
internal struct Sym3
{
    private double _xx, _xy, _xz, _yy, _yz, _zz;

    public void AddOuter(D3 n, double w)
    {
        _xx += w * n.X * n.X;
        _xy += w * n.X * n.Y;
        _xz += w * n.X * n.Z;
        _yy += w * n.Y * n.Y;
        _yz += w * n.Y * n.Z;
        _zz += w * n.Z * n.Z;
    }

    private readonly double Trace => _xx + _yy + _zz;

    /// <summary>Share of the trace that the direction carries (vᵀ M v / trace); near 0 when all normals are perpendicular to it.</summary>
    public readonly double SmallestEigenvalueShare(D3 v)
    {
        var tr = Trace;
        if (!(tr > 0)) return 1;
        var mv = Multiply(v);
        return D3.Dot(v, mv) / tr;
    }

    private readonly D3 Multiply(D3 v) => new(
        _xx * v.X + _xy * v.Y + _xz * v.Z,
        _xy * v.X + _yy * v.Y + _yz * v.Z,
        _xz * v.X + _yz * v.Y + _zz * v.Z);

    /// <summary>
    /// Eigenvector of the smallest eigenvalue by cyclic Jacobi rotations (at most 12 sweeps, converges in about five);
    /// <paramref name="hint"/> chooses the sign and is returned if the matrix is empty.
    /// </summary>
    public readonly D3 SmallestEigenvector(D3 hint)
    {
        Span<double> a = [_xx, _xy, _xz, _xy, _yy, _yz, _xz, _yz, _zz];
        Span<double> v = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        if (!(Trace > 0)) return hint;
        for (var sweep = 0; sweep < 12; sweep++)
        {
            var off = Math.Abs(a[1]) + Math.Abs(a[2]) + Math.Abs(a[5]);
            if (off < 1e-15 * Trace) break;
            Rotate(a, v, 0, 1);
            Rotate(a, v, 0, 2);
            Rotate(a, v, 1, 2);
        }
        var k = a[0] <= a[4] && a[0] <= a[8] ? 0 : a[4] <= a[8] ? 1 : 2;
        var e = new D3(v[k], v[3 + k], v[6 + k]).Normalized();
        return D3.Dot(e, hint) < 0 ? -e : e;
    }

    private static void Rotate(Span<double> a, Span<double> v, int p, int q)
    {
        var apq = a[3 * p + q];
        if (Math.Abs(apq) < 1e-300) return;
        var theta = (a[3 * q + q] - a[3 * p + p]) / (2 * apq);
        var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
        if (theta == 0) t = 1;
        var c = 1 / Math.Sqrt(t * t + 1);
        var s = t * c;
        for (var k = 0; k < 3; k++)
        {
            var akp = a[3 * k + p];
            var akq = a[3 * k + q];
            a[3 * k + p] = c * akp - s * akq;
            a[3 * k + q] = s * akp + c * akq;
        }
        for (var k = 0; k < 3; k++)
        {
            var apk = a[3 * p + k];
            var aqk = a[3 * q + k];
            a[3 * p + k] = c * apk - s * aqk;
            a[3 * q + k] = s * apk + c * aqk;
        }
        for (var k = 0; k < 3; k++)
        {
            var vkp = v[3 * k + p];
            var vkq = v[3 * k + q];
            v[3 * k + p] = c * vkp - s * vkq;
            v[3 * k + q] = s * vkp + c * vkq;
        }
    }
}
