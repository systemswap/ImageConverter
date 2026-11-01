using System.Numerics;
using ImageConverter.Viewer3D.Editing;
using ImageConverter.Viewer3D.Geometry;
using static ImageConverter.Tests.StlTestModels;

namespace ImageConverter.Tests;

/// <summary>Lengthening and shortening at a dividing plane, and writing the result back as STL.</summary>
public class SolidEditorTests
{
    private static double Volume(Mesh m) => MeshTopology.Build(m).Volume;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Lengthen_Box_GrowsExactlyAlongTheAxis(int axis)
    {
        var mesh = MeshOf(Box(Vector3.Zero, new Vector3(10, 20, 5)));
        var n = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        var size = new Vector3(10, 20, 5);
        var cut = Vector3.Dot(size, n) / 2;
        var result = SolidEditor.ChangeLength(mesh, n, cut, 3.5);
        var topo = MeshTopology.Build(result);
        Assert.True(topo.IsClosed);
        Assert.Equal(size + n * 3.5f, result.Bounds.Max - result.Bounds.Min);
        Assert.Equal(1000 + 1000 / Vector3.Dot(size, n) * 3.5, topo.Volume, 3);
    }

    /// <summary>Features beyond the plane move along unchanged: a bore keeps its diameter and moves by the change.</summary>
    [Fact]
    public void Lengthen_Tube_KeepsTheBoreAndItsPlace()
    {
        var mesh = MeshOf(Tube(3, 5, 20, 64));
        var before = Volume(mesh);
        var result = SolidEditor.ChangeLength(mesh, Vector3.UnitZ, 10, 4);
        Assert.True(MeshTopology.Build(result).IsClosed);
        Assert.Equal(24, result.Bounds.Max.Z - result.Bounds.Min.Z, 4);
        Assert.Equal(before * 24 / 20, Volume(result), 2);
        // corners of the inner wall at the top moved exactly by 4, the radius unchanged
        var top = Enumerable.Range(0, result.VertexCount).Select(i => new Vector3(result.Positions[3 * i], result.Positions[3 * i + 1], result.Positions[3 * i + 2]))
            .Where(p => MathF.Abs(p.Z - 24) < 1e-4f).Select(p => new Vector2(p.X, p.Y).Length()).ToList();
        Assert.Contains(top, r => MathF.Abs(r - 3) < 1e-4f);
        Assert.Contains(top, r => MathF.Abs(r - 5) < 1e-4f);
    }

    [Fact]
    public void Shorten_UniformBand_ClosesUp()
    {
        var mesh = MeshOf(Tube(3, 5, 20, 64));
        var result = SolidEditor.ChangeLength(mesh, Vector3.UnitZ, 6, -5);
        Assert.True(MeshTopology.Build(result).IsClosed);
        Assert.Equal(15, result.Bounds.Max.Z - result.Bounds.Min.Z, 4);
        Assert.Equal(Volume(mesh) * 15 / 20, Volume(result), 2);
    }

    /// <summary>A rib in the band to remove: shortening would cut it through, so it is refused with a reason.</summary>
    [Fact]
    public void Shorten_AcrossARib_IsRefused()
    {
        var plate = Box(Vector3.Zero, new Vector3(40, 10, 2));
        var rib = Box(new Vector3(18, 0, 2), new Vector3(22, 10, 6));
        var mesh = MeshOf(plate.Concat(rib).ToList());
        var e = Assert.Throws<EditException>(() => SolidEditor.ChangeLength(mesh, Vector3.UnitX, 15, -10));
        Assert.Contains("nicht überall gleich", e.Message);
    }

    /// <summary>
    /// The uniform stretches stop at the edges of a rib; after a length change the seam does not split the stretch it was made in.
    /// </summary>
    [Fact]
    public void UniformStretches_LeaveOutTheRib_AndSurviveAnEdit()
    {
        var plate = Box(Vector3.Zero, new Vector3(40, 10, 2));
        var rib = Box(new Vector3(28, 0, 2), new Vector3(30, 10, 6));
        var mesh = MeshOf(plate.Concat(rib).ToList());
        var stretches = SolidEditor.UniformStretches(mesh, Vector3.UnitX, 0, 40);
        Assert.Equal((0d, 28d), (Math.Round(stretches[0].From, 4), Math.Round(stretches[0].To, 4)));
        // no stretch reaches across an edge of the rib (inside the rib alone the section is uniform too)
        Assert.DoesNotContain(stretches, s => (s.From < 27.99 && s.To > 28.01) || (s.From < 29.99 && s.To > 30.01));

        var longer = SolidEditor.ChangeLength(mesh, Vector3.UnitX, 14, 6);
        var after = SolidEditor.UniformStretches(longer, Vector3.UnitX, 0, 46);
        Assert.Equal((0d, 34d), (Math.Round(after[0].From, 4), Math.Round(after[0].To, 4)));
    }

    [Fact]
    public void OpenModel_IsRefused()
    {
        var open = Box(Vector3.Zero, new Vector3(1, 1, 1));
        open.RemoveAt(0);
        Assert.Throws<EditException>(() => SolidEditor.ChangeLength(MeshOf(open), Vector3.UnitX, 0.5, 1));
    }

    /// <summary>A slanted direction: the length along it grows by the change, the model stays closed.</summary>
    [Fact]
    public void Lengthen_AlongASlantedDirection()
    {
        var mesh = MeshOf(Box(Vector3.Zero, new Vector3(10, 10, 10)));
        var n = Vector3.Normalize(new Vector3(1, 1, 0));
        var before = Extent(mesh, n);
        var result = SolidEditor.ChangeLength(mesh, n, Vector3.Dot(new Vector3(5, 5, 5), n), 2);
        Assert.True(MeshTopology.Build(result).IsClosed);
        Assert.Equal(before + 2, Extent(result, n), 3);

        static double Extent(Mesh m, Vector3 n)
        {
            var d = Enumerable.Range(0, m.VertexCount).Select(i => (double)Vector3.Dot(new Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n)).ToList();
            return d.Max() - d.Min();
        }
    }

    [Fact]
    public void AddBox_FillsTheGap_VolumeGrowsByThePart()
    {
        // a plate with a rail that leaves a 0.75 mm gap to a wall – the box closes the gap
        var plate = Box(Vector3.Zero, new Vector3(20, 30, 2));
        var wall = Box(new Vector3(18, 0, 2), new Vector3(20, 30, 8));
        var rail = Box(new Vector3(16.55f, 5, 2), new Vector3(17.25f, 25, 5));
        var mesh = MeshOf([.. plate, .. wall, .. rail]);
        var before = Volume(mesh);
        var result = SolidEditor.Combine(mesh, new Body(BodyShape.Box, new Vector3(16.55f, 5, 1.9f), new Vector3(18, 25, 5)), add: true);
        Assert.True(MeshTopology.Build(result).IsClosed);
        Assert.Equal(1, MeshTopology.Build(result).ShellCount);
        Assert.Equal(before + 0.75 * 20 * 3, Volume(result), 3);
    }

    [Fact]
    public void RemoveBox_Mirrored_CutsBothSides()
    {
        var mesh = MeshOf(Box(new Vector3(-10, -10, 0), new Vector3(10, 10, 4)));
        var result = SolidEditor.Combine(mesh, new Body(BodyShape.Box, new Vector3(6, -2, 2), new Vector3(11, 2, 5)), add: false, mirrorAtX: 0);
        Assert.True(MeshTopology.Build(result).IsClosed);
        Assert.Equal(1600 - 2 * (4 * 4 * 2), Volume(result), 3);
    }

    /// <summary>A bore along each axis: volume removed is that of the polygon prism, the hole runs along the axis asked for.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RemoveCylinder_AlongEachAxis(int axis)
    {
        var mesh = MeshOf(Box(new Vector3(-10, -10, -10), new Vector3(10, 10, 10)));
        var min = new Vector3(-1.5f, -1.5f, -1.5f);
        var max = new Vector3(1.5f, 1.5f, 1.5f);
        if (axis == 0) (min.X, max.X) = (-12, 12); else if (axis == 1) (min.Y, max.Y) = (-12, 12); else (min.Z, max.Z) = (-12, 12);
        var tool = SolidEditor.BodyMesh(new Body(BodyShape.Cylinder, min, max, axis));
        var toolSize = tool.Bounds.Max - tool.Bounds.Min;
        Assert.Equal(24, axis == 0 ? toolSize.X : axis == 1 ? toolSize.Y : toolSize.Z, 4);
        var result = SolidEditor.Combine(mesh, new Body(BodyShape.Cylinder, min, max, axis), add: false);
        Assert.True(MeshTopology.Build(result).IsClosed);
        var removed = 8000 - Volume(result);
        Assert.InRange(removed, Math.PI * 1.5 * 1.5 * 20 * 0.999, Math.PI * 1.5 * 1.5 * 20);
    }

    [Fact]
    public void RemoveBody_Elsewhere_IsRefused()
    {
        var mesh = MeshOf(Box(Vector3.Zero, new Vector3(10, 10, 10)));
        var e = Assert.Throws<EditException>(() => SolidEditor.Combine(mesh, new Body(BodyShape.Box, new Vector3(20, 0, 0), new Vector3(25, 5, 5)), add: false));
        Assert.Contains("berührt das Material nicht", e.Message);
    }

    /// <summary>The long outer face of a rail moved out to the wall: the rail becomes wider and joins the wall.</summary>
    [Fact]
    public void MoveFace_RailToTheWall_GrowsAndJoins()
    {
        var plate = Box(Vector3.Zero, new Vector3(20, 30, 2));
        var wall = Box(new Vector3(18, 0, 2), new Vector3(20, 30, 8));
        var rail = Box(new Vector3(16.55f, 5, 2), new Vector3(17.25f, 25, 5));
        var (mesh, _, faces) = Analyse([.. plate, .. wall, .. rail]);
        var face = FaceAt(mesh, faces, new Vector3(17.25f, 15, 3.5f));
        var outline = SolidEditor.Outline(mesh, faces.TrianglesOf(face), Vector3.UnitX);
        Assert.Equal(17.25, outline.Plane, 5);
        var before = Volume(mesh);
        var result = SolidEditor.MoveFace(mesh, outline, 0.75);
        var topo = MeshTopology.Build(result);
        Assert.True(topo.IsClosed);
        Assert.Equal(1, topo.ShellCount);
        Assert.Equal(before + 0.75 * 20 * 3, Volume(result), 3);
        // moved inwards instead: the rail becomes thinner
        var thinner = SolidEditor.MoveFace(mesh, outline, -0.3);
        Assert.Equal(before - 0.3 * 20 * 3, Volume(thinner), 3);
    }

    /// <summary>Only the masked middle of the rail face moved inwards by the rail width: the rail is gone there.</summary>
    [Fact]
    public void MoveFace_Masked_RemovesTheMiddleOfTheRail()
    {
        var plate = Box(Vector3.Zero, new Vector3(20, 30, 2));
        var rail = Box(new Vector3(16.55f, 5, 2), new Vector3(17.25f, 25, 5));
        var (mesh, _, faces) = Analyse([.. plate, .. rail]);
        var face = FaceAt(mesh, faces, new Vector3(17.25f, 15, 3.5f));
        var outline = SolidEditor.Outline(mesh, faces.TrianglesOf(face), Vector3.UnitX);
        var (minU, maxU, minV, maxV) = SolidEditor.Extent(outline);
        Assert.Equal((5d, 25d, 2d, 5d), (Math.Round(minU, 4), Math.Round(maxU, 4), Math.Round(minV, 4), Math.Round(maxV, 4)));
        // frame of a face across +X: U = Y, V = Z
        var middle = SolidEditor.Mask(outline, 11, 19, minV, maxV);
        var result = SolidEditor.MoveFace(mesh, middle, -0.7);
        var topo = MeshTopology.Build(result);
        Assert.True(topo.IsClosed);
        Assert.Equal(Volume(mesh) - 0.7 * 8 * 3, Volume(result), 3);
    }

    [Fact]
    public void Writer_RoundTrip_GivesTheSameModel()
    {
        var mesh = SolidEditor.ChangeLength(MeshOf(Tube(3, 5, 20, 32)), Vector3.UnitZ, 10, 2.5);
        using var stream = new MemoryStream();
        StlWriter.Write(stream, mesh, "solid looking header");
        var bytes = stream.ToArray();
        Assert.Equal(84 + 50 * mesh.TriangleCount, bytes.Length);
        Assert.NotEqual("solid", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        var back = Read(bytes).Mesh;
        Assert.Equal(mesh.TriangleCount, back.TriangleCount);
        Assert.Equal(Volume(mesh), Volume(back), 6);
    }
}
