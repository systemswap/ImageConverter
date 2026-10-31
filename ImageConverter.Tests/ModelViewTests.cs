using System.Numerics;
using ImageConverter.Documents;
using ImageConverter.Viewer3D;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;
using static ImageConverter.Tests.StlTestModels;

namespace ImageConverter.Tests;

/// <summary>The parts of the 3D view that work without a screen: camera, prepared data, hit tests, texts, registration.</summary>
public class ModelViewTests
{
    private static OrbitCamera Camera(bool orthographic)
    {
        var cam = new OrbitCamera { Width = 1000, Height = 700, Orthographic = orthographic, SceneCentre = new Vector3(10, 5, 3), SceneRadius = 40 };
        (cam.Yaw, cam.Pitch) = OrbitCamera.Angles(StandardView.Iso);
        cam.Fit(new Vector3(10, 5, 3), 20);
        return cam;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProjectAndRay_AreInverse(bool orthographic)
    {
        var cam = Camera(orthographic);
        var p = new Vector3(14, -3, 9);
        var (x, y, visible) = cam.Project(p);
        Assert.True(visible);
        var (o, d) = cam.Ray(x, y);
        // The point lies on the ray
        var t = Vector3.Dot(p - o, d);
        Assert.True(Vector3.Distance(o + d * t, p) < 1e-3f);
    }

    /// <summary>Turning keeps the pivot where it is on screen, and the up axis stays vertical (turntable).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Orbit_KeepsThePivotInPlace(bool orthographic)
    {
        var cam = Camera(orthographic);
        var pivot = new Vector3(3, 8, 1);
        var before = cam.Project(pivot);
        cam.Orbit(pivot, 0.7f, -0.3f);
        var after = cam.Project(pivot);
        Assert.InRange(after.X - before.X, -0.05f, 0.05f);
        Assert.InRange(after.Y - before.Y, -0.05f, 0.05f);
        Assert.Equal(0, cam.Right.Z, 6);
    }

    [Fact]
    public void Orbit_StopsAtTheViewFromAbove()
    {
        var cam = Camera(true);
        cam.Orbit(cam.Target, 0, 10);
        Assert.Equal(MathF.PI / 2, cam.Pitch, 5);
        Assert.True(Vector3.Distance(cam.Forward, -Vector3.UnitZ) < 1e-5f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Zoom_KeepsThePointUnderTheMouse(bool orthographic)
    {
        var cam = Camera(orthographic);
        var anchor = new Vector3(18, 2, 6);
        var before = cam.Project(anchor);
        cam.Zoom(0.6f, anchor);
        var after = cam.Project(anchor);
        Assert.InRange(after.X - before.X, -0.05f, 0.05f);
        Assert.InRange(after.Y - before.Y, -0.05f, 0.05f);
    }

    /// <summary>In the perspective view the wheel goes through a surface: the eye passes the point under the mouse.</summary>
    [Fact]
    public void Zoom_InPerspective_GoesThroughTheSurface()
    {
        var cam = Camera(false);
        var anchor = cam.Target + cam.Forward * 0;   // a surface point straight ahead
        var before = Vector3.Dot(anchor - cam.Eye, cam.Forward);
        for (var i = 0; i < 400; i++) cam.Zoom(0.87f, anchor, 0.2f);
        Assert.True(before > 0);
        Assert.True(Vector3.Dot(anchor - cam.Eye, cam.Forward) < 0, "the eye should be past the surface");
    }

    [Fact]
    public void Zoom_InOrthographic_NeverPassesTheAnchor()
    {
        var cam = Camera(true);
        var anchor = cam.Target;
        for (var i = 0; i < 400; i++) cam.Zoom(0.87f, anchor, 0.2f);
        Assert.True(cam.Distance >= cam.SceneRadius * 1e-3f * 0.999f);
    }

    /// <summary>Extent of a box along a direction – the range in which a section through the model can lie.</summary>
    [Fact]
    public void BoxExtent_CoversAllCorners()
    {
        var box = new Box3(new Vector3(0, 0, 0), new Vector3(10, 20, 5));
        var n = Vector3.Normalize(new Vector3(1, -2, 0.5f));
        var (front, back) = box.Extent(n);
        for (var i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? 0 : 10, (i & 2) == 0 ? 0 : 20, (i & 4) == 0 ? 0 : 5);
            Assert.InRange(Vector3.Dot(c, n), front - 1e-4f, back + 1e-4f);
        }
        Assert.Equal((0f, 10f), box.Extent(Vector3.UnitX));
    }

    /// <summary>The orthographic zoom stops at its smallest view and says so (the wheel then goes through walls).</summary>
    [Fact]
    public void Zoom_InOrthographic_StopsAtTheSmallestView()
    {
        var cam = Camera(true);
        var zoomed = 0;
        while (cam.Zoom(0.87f, cam.Target, 0, 1.5f) && zoomed < 500) zoomed++;
        Assert.InRange(cam.ViewHeight, 1.5f * 0.999f, 1.5f * 1.2f);
        Assert.True(zoomed < 500);
    }

    /// <summary>
    /// Zooming through walls: from outside, the first step goes just past the outer wall of a tube, the next past the far
    /// wall – where the ray leaves the material each time.
    /// </summary>
    [Fact]
    public void Walls_ExitPoints_GoWallByWall()
    {
        var mesh = MeshOf(Tube(3, 5, 10, 64));
        var bvh = Bvh.Build(mesh);
        var dir = -Vector3.UnitX;
        var first = Walls.ExitOfNextWall(mesh, bvh, new Vector3(20, 0, 5), dir);
        Assert.NotNull(first);
        Assert.Equal(3, first.Value.X, 3);   // leaves the near wall at its inner side
        var second = Walls.ExitOfNextWall(mesh, bvh, first.Value + dir * 0.01f, dir);
        Assert.NotNull(second);
        Assert.Equal(-5, second.Value.X, 3);   // through the cavity and the far wall
        Assert.Null(Walls.ExitOfNextWall(mesh, bvh, second.Value + dir * 0.01f, dir));
    }

    [Fact]
    public void Fit_ShowsTheWholeSphere()
    {
        foreach (var ortho in new[] { true, false })
        {
            var cam = Camera(ortho);
            // Points on the sphere around the model in all directions land inside the view
            for (var i = 0; i < 50; i++)
            {
                var a = i * 2.39996f;
                var z = 1 - 2 * (i + 0.5f) / 50;
                var r = MathF.Sqrt(1 - z * z);
                var p = new Vector3(10, 5, 3) + 20 * new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
                var (x, y, _) = cam.Project(p);
                Assert.InRange(x, 0, 1000);
                Assert.InRange(y, 0, 700);
            }
        }
    }

    /// <summary>
    /// A ray exactly along the shared edge of two strips of a cylinder, from far away (as the orthographic camera casts it),
    /// hits the cylinder – it used to slip between both triangles and hit what lay behind.
    /// </summary>
    [Fact]
    public void RayAlongASharedEdge_FromFarAway_HitsTheSurface()
    {
        var mesh = MeshOf([.. Box(Vector3.Zero, new Vector3(60, 40, 8)), .. Extrude(Circle(new Vector2(15, 20), 8, 64), 22, Matrix4x4.CreateTranslation(0, 0, 8))]);
        var bvh = Bvh.Build(mesh);
        var d = Vector3.Normalize(new Vector3(-1, 1, -1));
        var target = new Vector3(15, 12, 15);   // on the vertical edge at 270°
        foreach (var far in new[] { 50f, 600f, 5000f })
        {
            Assert.True(bvh.Raycast(target - d * far, d, float.MaxValue, out var hit));
            var p = target - d * far + d * hit.Distance;
            Assert.True(Vector3.Distance(p, target) < 0.01f, $"from {far}: hit {p}");
        }
    }

    /// <summary>
    /// Triangles lying on top of each other cannot be separated by the hierarchy; a crafted file with millions of them
    /// made each ray take 39 ms (every mouse move while measuring). The test budget per ray bounds it.
    /// </summary>
    [Fact]
    public void StackedTriangles_KeepRaysFast()
    {
        var mesh = new Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0], Enumerable.Range(0, 3 * 1_000_000).Select(i => i % 3).ToArray());
        var bvh = Bvh.Build(mesh);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) Assert.True(bvh.Raycast(new Vector3(0.2f, 0.2f, 5), -Vector3.UnitZ, 100, out _));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms for 20 rays");   // before: 2.7 s (debug build)
    }

    [Fact]
    public void SortedByFace_KeepsEveryTriangle_AndGroupsThem()
    {
        var (mesh, _, faces) = Analyse(Tube(3, 5, 8, 24));
        var (sorted, start) = SceneData.SortedByFace(mesh, faces);
        Assert.Equal(mesh.Indices.Length, sorted.Length);
        Assert.Equal(mesh.TriangleCount, start[^1]);
        // The same triangles, only in another order
        static IEnumerable<(int, int, int)> Tris(int[] i) => Enumerable.Range(0, i.Length / 3).Select(t => (i[3 * t], i[3 * t + 1], i[3 * t + 2])).Order();
        Assert.Equal(Tris(mesh.Indices), Tris(sorted));
        for (var f = 0; f < faces.Faces.Length; f++) Assert.Equal(faces.Faces[f].Count, start[f + 1] - start[f]);
    }

    [Fact]
    public void SharpEdges_OfABox_AreItsTwelveEdges_AndCylinderStripsHaveNone()
    {
        var box = Analyse(Box(Vector3.Zero, new Vector3(2, 3, 4)));
        Assert.Equal(2 * 12, SceneData.SharpEdges(box.Mesh, box.Topology).Length);
        var cyl = Analyse(Extrude(Circle(Vector2.Zero, 5, 48), 10));
        Assert.Equal(2 * 2 * 48, SceneData.SharpEdges(cyl.Mesh, cyl.Topology).Length);   // only the two rims
        Assert.Equal(cyl.Mesh.TriangleCount * 3 / 2 * 2, SceneData.AllEdges(cyl.Mesh).Length);
    }

    [Fact]
    public void Shadow_IsDarkUnderTheModel_AndFreeAtTheBorder()
    {
        var mesh = MeshOf(Box(new Vector3(0, 0, 0), new Vector3(10, 10, 2)));
        var s = SceneData.Shadow(mesh, 64);
        int Cell(float x, float y) => s.Cells[(int)((y - s.Min.Y) / (s.Max.Y - s.Min.Y) * 64) * 64 + (int)((x - s.Min.X) / (s.Max.X - s.Min.X) * 64)];
        Assert.True(Cell(5, 5) > 200);
        Assert.Equal(0, s.Cells[0]);
        Assert.True(s.Z < 0);
    }

    /// <summary>
    /// Large triangles on top of each other: the shadow used to test hundreds of cells per triangle (over a minute for
    /// two million); with the work budget it stays fast and still shades the footprint.
    /// </summary>
    [Fact]
    public void Shadow_OfStackedLargeTriangles_StaysFast()
    {
        var mesh = new Mesh([0, 0, 0, 10, 0, 0, 0, 10, 0], Enumerable.Range(0, 3 * 500_000).Select(i => i % 3).ToArray());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var s = SceneData.Shadow(mesh);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"{watch.ElapsedMilliseconds} ms");
        var cx = (int)((2 - s.Min.X) / (s.Max.X - s.Min.X) * s.Size);
        var cy = (int)((2 - s.Min.Y) / (s.Max.Y - s.Min.Y) * s.Size);
        Assert.True(s.Cells[cy * s.Size + cx] > 200);
    }

    [Fact]
    public void Texts_UseTheUnitAndCulture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("12,50 mm", MeasureText.Length(12.5, inches: false));
            Assert.Equal("1,000 in", MeasureText.Length(25.4, inches: true));
            Assert.Equal("∠ 90,00°", MeasureText.Short(new MeasureValue(Quantity.Angle, 90), false));
            Assert.Equal("Ø 6,00 mm", MeasureText.Short(new MeasureValue(Quantity.Diameter, 6), false));
            Assert.Equal("1,50 cm²", MeasureText.Area(150, false));
            Assert.Equal("2,00 cm³", MeasureText.Volume(2000, false));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = old;
        }
    }

    /// <summary>
    /// The 3D view takes the mouse over its whole area: Avalonia counts a control as hit only where it draws, and the
    /// OpenGL picture is drawn past it – without its own hit test, turning and zooming did nothing (No. 115).
    /// </summary>
    [Fact]
    public void View_TakesTheMouseEverywhere() =>
        Assert.True(typeof(Avalonia.Rendering.ICustomHitTest).IsAssignableFrom(typeof(ImageConverter.Viewer3D.Views.ModelGlView)));

    [Fact]
    public void Registry_FindsTheViewerByExtension_AnyCase()
    {
        DocumentViewers.Register(new StlViewer());
        Assert.IsType<StlViewer>(DocumentViewers.For("/tmp/Teil.STL"));
        Assert.Null(DocumentViewers.For("/tmp/foto.jpg"));
        Assert.True(DocumentViewers.CanOpen("/tmp/x.stl"));
        Assert.False(DocumentViewers.CanOpen("/tmp/x.step"));
    }
}
