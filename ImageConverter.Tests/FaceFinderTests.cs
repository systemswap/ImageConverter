using System.Numerics;
using ImageConverter.Viewer3D.Geometry;
using static ImageConverter.Tests.StlTestModels;

namespace ImageConverter.Tests;

/// <summary>Neighbourhood and faces: planes, cylinders and freeform patches are found as one face each.</summary>
public class FaceFinderTests
{
    [Fact]
    public void Box_HasSixPlanes()
    {
        var (_, topo, faces) = Analyse(Box(Vector3.Zero, new Vector3(20, 10, 5)));
        Assert.True(topo.IsClosed);
        Assert.Equal(6, faces.Faces.Length);
        Assert.All(faces.Faces, f => Assert.Equal(FaceKind.Plane, f.Kind));
        Assert.Contains(faces.Faces, f => Vector3.Distance(f.Normal.ToVector3(), Vector3.UnitZ) < 1e-6);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(48)]
    [InlineData(360)]
    public void Cylinder_IsOneFace_WithItsRadius(int segments)
    {
        var (_, _, faces) = Analyse(Extrude(Circle(new Vector2(5, -2), 4, segments), 10));
        Assert.Equal(3, faces.Faces.Length);
        var cyl = Assert.Single(faces.Faces, f => f.Kind == FaceKind.Cylinder);
        Assert.Equal(4, cyl.Radius, 4);
        Assert.Equal(360, cyl.Coverage);
        Assert.True(Math.Abs(Math.Abs(cyl.Axis.Z) - 1) < 1e-6);
        Assert.Equal(5, cyl.AxisPoint.X, 4);
        Assert.Equal(-2, cyl.AxisPoint.Y, 4);
        Assert.Equal(2, faces.Faces.Count(f => f.Kind == FaceKind.Plane));
    }

    [Fact]
    public void TiltedCylinder_FindsItsAxis()
    {
        var turn = Matrix4x4.CreateRotationX(0.6f) * Matrix4x4.CreateRotationY(-0.35f) * Matrix4x4.CreateTranslation(30, 40, 50);
        var (_, _, faces) = Analyse(Extrude(Circle(Vector2.Zero, 6.5f, 64), 25, turn));
        var cyl = Assert.Single(faces.Faces, f => f.Kind == FaceKind.Cylinder);
        var axis = Vector3.TransformNormal(Vector3.UnitZ, turn);
        Assert.True(D3.AngleDegrees(cyl.Axis, D3.From(axis)) is < 0.01 or > 179.99);
        Assert.Equal(6.5, cyl.Radius, 3);
    }

    [Fact]
    public void Tube_HasTwoCylindersAndTwoRings()
    {
        var (_, topo, faces) = Analyse(Tube(3, 5, 8, 40));
        Assert.True(topo.IsClosed);
        Assert.Equal(4, faces.Faces.Length);
        var radii = faces.Faces.Where(f => f.Kind == FaceKind.Cylinder).Select(f => Math.Round(f.Radius, 3)).Order().ToArray();
        Assert.Equal([3.0, 5.0], radii);
    }

    /// <summary>A rounded corner (quarter cylinder) is one face with its radius; the flat sides next to it stay planes.</summary>
    [Fact]
    public void RoundedCorner_IsAPartialCylinder_AndTheSidesStayPlanes()
    {
        var (_, _, faces) = Analyse(Extrude(RoundedCorner(30, 20, 6, 8), 10));
        var cyl = Assert.Single(faces.Faces, f => f.Kind == FaceKind.Cylinder);
        Assert.Equal(6, cyl.Radius, 3);
        Assert.InRange(cyl.Coverage, 85, 95);
        Assert.Equal(6, faces.Faces.Count(f => f.Kind == FaceKind.Plane));   // 2 caps + 4 sides
    }

    /// <summary>
    /// A sphere away from the origin, turned, stays one freeform face: the float tolerance for the circle test is one step
    /// of the coordinates, and fans of triangles (poles) are no cylinder because their shared edges do not run along an axis.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1000f)]
    public void Sphere_AwayFromTheOrigin_IsOneFreeformFace(float offset)
    {
        var pose = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(0.3f, -0.2f, 0.5f, 0.7f)))
                   * Matrix4x4.CreateTranslation(Vector3.Normalize(new Vector3(1, 0.4f, -0.7f)) * offset);
        var tris = Sphere(Vector3.Zero, 5, 96, 48).Select(t => new Tri(Vector3.Transform(t.A, pose), Vector3.Transform(t.B, pose), Vector3.Transform(t.C, pose))).ToList();
        var (_, _, faces) = Analyse(tris);
        var face = Assert.Single(faces.Faces);
        Assert.Equal(FaceKind.Freeform, face.Kind);
    }

    /// <summary>
    /// A finely divided sphere is one freeform face: the facets around one corner form a ring of neighbours, but their
    /// corners do not go round an axis – they used to become hundreds of tiny "closed cylinders".
    /// </summary>
    [Fact]
    public void FineSphere_HasNoTinyCylinders()
    {
        var (_, _, faces) = Analyse(Sphere(Vector3.Zero, 1, 600, 300));
        Assert.Equal(FaceKind.Freeform, Assert.Single(faces.Faces).Kind);
    }

    /// <summary>
    /// Text files often keep only six significant digits; a cylinder from such a file, turned and away from the origin,
    /// is still found – the tolerance follows the digits of the file.
    /// </summary>
    [Fact]
    public void Cylinder_FromATextFileWithSixDigits_IsFound()
    {
        var pose = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(0.2f, 0.5f, -0.1f, 0.8f))) * Matrix4x4.CreateTranslation(120, -80, 45);
        var tris = Extrude(Circle(Vector2.Zero, 7.5f, 72), 20, pose);
        var sb = new System.Text.StringBuilder("solid six\n");
        foreach (var t in tris)
        {
            sb.Append("facet normal 0 0 0\nouter loop\n");
            foreach (var v in new[] { t.A, t.B, t.C })
                sb.Append(System.FormattableString.Invariant($"vertex {v.X:G6} {v.Y:G6} {v.Z:G6}\n"));
            sb.Append("endloop\nendfacet\n");
        }
        var file = Read(System.Text.Encoding.ASCII.GetBytes(sb.Append("endsolid\n").ToString()));
        Assert.True(file.Resolution > 1e-6);
        var faces = FaceFinder.Find(file.Mesh, MeshTopology.Build(file.Mesh), default, file.Resolution);
        var cyl = Assert.Single(faces.Faces, f => f.Kind == FaceKind.Cylinder);
        Assert.Equal(7.5, cyl.Radius, 3);
    }

    /// <summary>An arc that ends at sharp edges (a pie slice) is still found: its end strips have one gentle long side only.</summary>
    [Theory]
    [InlineData(90, 3)]
    [InlineData(30, 6)]
    public void ArcEndingAtSharpEdges_IsAPartialCylinder(int degrees, int segments)
    {
        var profile = new List<Vector2> { Vector2.Zero };
        for (var i = 0; i <= segments; i++)
        {
            var a = degrees * MathF.PI / 180 * i / segments;
            profile.Add(new Vector2(9 * MathF.Cos(a), 9 * MathF.Sin(a)));
        }
        var (_, _, faces) = Analyse(Extrude(profile, 10));
        var cyl = Assert.Single(faces.Faces, f => f.Kind == FaceKind.Cylinder);
        Assert.Equal(9, cyl.Radius, 4);
        Assert.Equal(degrees, cyl.Coverage, 2);
    }

    [Fact]
    public void Sphere_IsFreeform()
    {
        var (_, _, faces) = Analyse(Sphere(Vector3.Zero, 10, 48, 24));
        Assert.Contains(faces.Faces, f => f.Kind == FaceKind.Freeform && f.Area > 0.5 * 4 * Math.PI * 100);
        Assert.DoesNotContain(faces.Faces, f => f.Kind == FaceKind.Plane && f.Area > 5);
    }

    [Fact]
    public void Topology_CountsOpenAndNonManifoldEdges_AndParts()
    {
        var open = Box(Vector3.Zero, Vector3.One);
        open.RemoveAt(0);
        var topo = MeshTopology.Build(MeshOf(open));
        Assert.Equal(3, topo.OpenEdges);
        Assert.False(topo.IsClosed);

        var two = Box(Vector3.Zero, Vector3.One).Concat(Box(new Vector3(3, 0, 0), new Vector3(4, 1, 1))).ToList();
        Assert.Equal(2, MeshTopology.Build(MeshOf(two)).ShellCount);

        // Three triangles on one edge
        var fin = new List<Tri>
        {
            new(Vector3.Zero, Vector3.UnitX, Vector3.UnitY),
            new(Vector3.Zero, Vector3.UnitX, -Vector3.UnitY),
            new(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ),
        };
        Assert.Equal(1, MeshTopology.Build(MeshOf(fin)).NonManifoldEdges);
    }

    /// <summary>
    /// Many triangles on one edge, all around one corner (a crafted "book"): the neighbourhood still takes linear-
    /// logarithmic time instead of quadratic.
    /// </summary>
    [Fact]
    public void CraftedFan_StaysFast()
    {
        var list = new List<Tri>();
        for (var i = 0; i < 200_000; i++)
        {
            var a = i * 0.001f;
            list.Add(new Tri(Vector3.Zero, Vector3.UnitX, new Vector3(0, MathF.Cos(a), MathF.Sin(a)) * (1 + i * 1e-4f)));
        }
        var mesh = MeshOf(list);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var topo = MeshTopology.Build(mesh);
        FaceFinder.Find(mesh, topo);
        Bvh.Build(mesh).Raycast(new Vector3(0.5f, 0.5f, 5), -Vector3.UnitZ, 100, out _);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Bvh_FindsTheSameNearestHitAsTryingEveryTriangle()
    {
        var mesh = MeshOf(Sphere(new Vector3(1, 2, 3), 10, 40, 20).Concat(Box(new Vector3(-30, -5, -5), new Vector3(-20, 5, 5))).ToList());
        var bvh = Bvh.Build(mesh);
        var rng = new Random(3);
        for (var i = 0; i < 300; i++)
        {
            var o = new Vector3(rng.NextSingle() * 80 - 40, rng.NextSingle() * 80 - 40, rng.NextSingle() * 80 - 40);
            var d = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
            var hit = bvh.Raycast(o, d, 1000, out var h);
            var bestT = float.MaxValue;
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                var (a, b, c) = mesh.Corners(t);
                if (Moller(o, d, a, b, c, out var tt) && tt < bestT) bestT = tt;
            }
            Assert.Equal(bestT < float.MaxValue, hit);
            if (hit) Assert.Equal(bestT, h.Distance, 3);
        }
    }

    [Fact]
    public void Bvh_ShortestDistance_MatchesTryingEveryPair()
    {
        var mesh = MeshOf(Sphere(Vector3.Zero, 5, 16, 8).Concat(Box(new Vector3(8, 1, -2), new Vector3(12, 3, 2))).ToList());
        var topo = MeshTopology.Build(mesh);
        var a = Enumerable.Range(0, mesh.TriangleCount).Where(t => topo.ShellOf[t] == 0).ToArray();
        var b = Enumerable.Range(0, mesh.TriangleCount).Where(t => topo.ShellOf[t] == 1).ToArray();
        var (_, _, d) = Bvh.Nearest(Bvh.Build(mesh, a.ToArray()), Bvh.Build(mesh, b.ToArray()));
        var brute = double.MaxValue;
        foreach (var i in a)
        foreach (var j in b)
        {
            var (a0, a1, a2) = mesh.CornersD(i);
            var (b0, b1, b2) = mesh.CornersD(j);
            brute = Math.Min(brute, TriangleDistance.Between(a0, a1, a2, b0, b1, b2).Distance);
        }
        Assert.Equal(brute, d, 9);
        Assert.InRange(d, 3.0, 3.6);   // box face at x = 8, sphere radius 5 (a little less between its corners)
    }

    private static bool Moller(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-20f) return false;
        var s = o - a;
        var u = Vector3.Dot(s, p) / det;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(d, q) / det;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) / det;
        return t > 0;
    }
}
