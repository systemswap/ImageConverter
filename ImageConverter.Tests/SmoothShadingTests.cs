using System.Numerics;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;
using static ImageConverter.Tests.StlTestModels;

namespace ImageConverter.Tests;

/// <summary>
/// Smooth shading of curved faces: only finely divided cylinders and freeform patches are smoothed, with the true
/// normal of the surface; planes, edges between faces and coarse facets stay exactly as the file describes them.
/// </summary>
public class SmoothShadingTests
{
    private static Vector3 Normal(SmoothShading s, int vertex) => new(s.Normals[3 * vertex], s.Normals[3 * vertex + 1], s.Normals[3 * vertex + 2]);

    private static Vector3 Position(float[] p, int vertex) => new(p[3 * vertex], p[3 * vertex + 1], p[3 * vertex + 2]);

    [Fact]
    public void Box_StaysFlat()
    {
        var (mesh, _, faces) = Analyse(Box(Vector3.Zero, new Vector3(10, 20, 5)));
        var smooth = SmoothShading.Build(mesh, faces);
        Assert.Equal(mesh.Indices, smooth.Corners);
    }

    /// <summary>Two planes meeting at only 3° stay two flat faces with a crisp edge – nothing is softened across it.</summary>
    [Fact]
    public void ShallowEdgeBetweenPlanes_StaysCrisp()
    {
        var kink = MathF.Tan(3 * MathF.PI / 180) * 10;
        var (mesh, _, faces) = Analyse(Extrude([new(0, 0), new(20, 0), new(20, 5), new(10, 5 + kink), new(0, 5)], 8));
        var smooth = SmoothShading.Build(mesh, faces);
        Assert.Equal(mesh.Indices, smooth.Corners);
    }

    /// <summary>A finely divided tube: the walls get the exact normal of the cylinder (radial), the rings stay flat.</summary>
    [Fact]
    public void FineTube_GetsRadialNormals()
    {
        var (mesh, _, faces) = Analyse(Tube(4, 6, 10, 96));
        var smooth = SmoothShading.Build(mesh, faces);
        var positions = smooth.ExtendedPositions(mesh.Positions);
        int smoothed = 0, flat = 0;
        for (var c = 0; c < smooth.Corners.Length; c++)
        {
            var t = c / 3;
            var (a, b, d) = mesh.Corners(t);
            var facet = Vector3.Normalize(Vector3.Cross(b - a, d - a));
            var v = smooth.Corners[c];
            if (v < mesh.VertexCount)
            {
                flat++;
                Assert.True(MathF.Abs(facet.Z) > 0.999f, "only the flat rings stay flat");
                continue;
            }
            smoothed++;
            var n = Normal(smooth, v);
            var p = Position(positions, v);
            Assert.Equal(Position(mesh.Positions, smooth.ExtraSources[v - mesh.VertexCount]), p);
            var radial = Vector3.Normalize(new Vector3(p.X, p.Y, 0));
            Assert.True(MathF.Abs(MathF.Abs(Vector3.Dot(n, radial)) - 1) < 1e-4f, $"normal {n} not radial at {p}");
            Assert.True(Vector3.Dot(n, facet) > 0.99f, "normal on the side the facet faces");
        }
        Assert.Equal(96 * 4 * 3, smoothed);
        Assert.Equal(96 * 4 * 3, flat);
    }

    /// <summary>A coarse tube (12 sides, 30° per facet) is shown faceted as in the file, not rounded.</summary>
    [Fact]
    public void CoarseTube_StaysFaceted()
    {
        var (mesh, _, faces) = Analyse(Tube(4, 6, 10, 12));
        var smooth = SmoothShading.Build(mesh, faces);
        Assert.Equal(mesh.Indices, smooth.Corners);
    }

    /// <summary>A fine sphere (freeform): the averaged normals point away from the centre.</summary>
    [Fact]
    public void FineSphere_GetsNormalsFromTheCentre()
    {
        var centre = new Vector3(1, 2, 3);
        var (mesh, _, faces) = Analyse(Sphere(centre, 10, 96, 48));
        var smooth = SmoothShading.Build(mesh, faces);
        var positions = smooth.ExtendedPositions(mesh.Positions);
        var smoothed = 0;
        for (var c = 0; c < smooth.Corners.Length; c++)
        {
            var v = smooth.Corners[c];
            if (v < mesh.VertexCount) continue;
            smoothed++;
            var radial = Vector3.Normalize(Position(positions, v) - centre);
            Assert.True(MathF.Abs(Vector3.Dot(Normal(smooth, v), radial)) > MathF.Cos(2 * MathF.PI / 180));
        }
        Assert.True(smoothed > smooth.Corners.Length * 0.9, $"{smoothed} of {smooth.Corners.Length} corners smoothed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void QualityLevels_FastIsPlain_HighHasMostSamples(int level)
    {
        var q = RenderQuality.ForLevel(level);
        Assert.Equal(level > 0, q.Smooth && q.HardwareClip && q.SmoothLines && q.Dither && q.SharpOcclusion);
        Assert.Equal(level == 2 ? 8 : 4, q.Samples);
    }
}
