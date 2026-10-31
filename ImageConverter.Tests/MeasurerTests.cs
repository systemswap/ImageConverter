using System.Numerics;
using ImageConverter.Viewer3D.Geometry;
using static ImageConverter.Tests.StlTestModels;

namespace ImageConverter.Tests;

/// <summary>Measuring between faces and points gives the values the models were built with.</summary>
public class MeasurerTests
{
    // Box 20 × 10 × 5, a shaft of radius 3 along z at (40, 5), a bore-like second shaft of radius 2 at (55, 5)
    private static readonly List<Tri> Parts =
    [
        .. Box(Vector3.Zero, new Vector3(20, 10, 5)),
        .. Extrude(Circle(new Vector2(40, 5), 3, 48), 5),
        .. Extrude(Circle(new Vector2(55, 5), 2, 48), 5),
    ];

    private static readonly (Mesh Mesh, MeshTopology Topology, FaceSet Faces) Model = Analyse(Parts);

    private static Pick FacePick(Vector3 p) => new(PickKind.Face, FaceAt(Model.Mesh, Model.Faces, p), D3.From(p));

    private static Measurement Measure(Pick a, Pick b) => new Measurer(Model.Mesh, Model.Faces).Measure(a, b);

    private static double Value(Measurement m, Quantity q, int of = 0) => m.Values.First(v => v.Quantity == q && v.Of == of).Value;

    [Fact]
    public void ParallelPlanes_GiveThePerpendicularDistance()
    {
        var m = Measure(FacePick(new Vector3(4, 3, 5)), FacePick(new Vector3(15, 8, 0)));
        Assert.Equal(Quantity.Distance, m.Values[0].Quantity);
        Assert.Equal(5, m.Values[0].Value, 9);
        // The line starts at the click and ends straight below it on the other face
        Assert.Equal(new D3(4, 3, 5), m.From);
        Assert.Equal(0, m.To.Z, 9);
        Assert.Equal(4, m.To.X, 9);

        Assert.Equal(20, Measure(FacePick(new Vector3(0, 5, 2)), FacePick(new Vector3(20, 2, 1))).Values[0].Value, 9);
    }

    [Fact]
    public void PerpendicularPlanes_GiveTheAngle()
    {
        var m = Measure(FacePick(new Vector3(5, 5, 5)), FacePick(new Vector3(20, 5, 2)));
        Assert.Equal(90, Value(m, Quantity.Angle), 9);
        Assert.Equal(0, Value(m, Quantity.MinDistance), 9);   // they meet at an edge
        Assert.False(m.HasLine);
    }

    /// <summary>Slightly tilted planes (0.1° … 5°) also give the distance at the click, not only angle and shortest distance.</summary>
    [Fact]
    public void SlightlyTiltedPlanes_GiveTheDistanceAtTheClick()
    {
        var rise = MathF.Tan(1 * MathF.PI / 180) * 40;   // top tilted by 1°
        var plate = Analyse(Extrude([new Vector2(0, 0), new Vector2(40, 0), new Vector2(40, 10 + rise), new Vector2(0, 10)], 20));
        var measurer = new Measurer(plate.Mesh, plate.Faces);
        Pick P(Vector3 p) => new(PickKind.Face, FaceAt(plate.Mesh, plate.Faces, p), D3.From(p));
        var top = new Vector3(30, 10 + rise * 0.75f, 10);
        var m = measurer.Measure(P(top), P(new Vector3(20, 0, 10)));
        Assert.Equal(Quantity.DistanceAtClick, m.Values[0].Quantity);
        Assert.Equal(10 + rise * 0.75, m.Values[0].Value, 4);
        Assert.Equal(1, Value(m, Quantity.Angle), 4);
        Assert.Equal(10, Value(m, Quantity.MinDistance), 4);
    }

    [Fact]
    public void ParallelCylinders_GiveAxisDistanceAndGaps()
    {
        var m = Measure(FacePick(new Vector3(43, 5, 2)), FacePick(new Vector3(57, 5, 2)));
        Assert.Equal(15, Value(m, Quantity.AxisDistance), 3);
        Assert.Equal(10, Value(m, Quantity.MinDistance), 3);
        Assert.Equal(20, Value(m, Quantity.MaxDistance), 3);
        Assert.Equal(6, Value(m, Quantity.Diameter, 1), 3);
        Assert.Equal(4, Value(m, Quantity.Diameter, 2), 3);
        // The line runs between the axes at the height of the first click
        Assert.Equal(2, m.From.Z, 3);
        Assert.Equal(40, m.From.X, 3);
        Assert.Equal(55, m.To.X, 3);
    }

    [Fact]
    public void CylinderAndParallelPlane_GiveAxisToPlane()
    {
        // Side face of the box at x = 20, shaft axis at x = 40
        var m = Measure(FacePick(new Vector3(37, 5, 2)), FacePick(new Vector3(20, 5, 2)));
        Assert.Equal(20, Value(m, Quantity.AxisDistance), 3);
        Assert.Equal(17, Value(m, Quantity.MinDistance), 3);
        Assert.Equal(23, Value(m, Quantity.MaxDistance), 3);

        // Order of the clicks does not matter for the values
        var back = Measure(FacePick(new Vector3(20, 5, 2)), FacePick(new Vector3(37, 5, 2)));
        Assert.Equal(20, Value(back, Quantity.AxisDistance), 3);
        Assert.Equal(20, back.From.X, 3);
    }

    [Fact]
    public void CylinderAndPerpendicularPlane_GiveTheAngle()
    {
        var m = Measure(FacePick(new Vector3(43, 5, 2)), FacePick(new Vector3(5, 5, 5)));
        Assert.Equal(90, Value(m, Quantity.Angle), 3);
    }

    [Fact]
    public void SingleCylinder_ShowsItsDiameter()
    {
        var values = new Measurer(Model.Mesh, Model.Faces).Single(FacePick(new Vector3(43, 5, 2)));
        Assert.Equal(Quantity.Diameter, Assert.Single(values).Quantity);
        Assert.Equal(6, values[0].Value, 3);
        Assert.Empty(new Measurer(Model.Mesh, Model.Faces).Single(FacePick(new Vector3(5, 5, 5))));
    }

    [Fact]
    public void Tube_GivesItsWallThickness()
    {
        var tube = Analyse(Tube(3, 5, 8, 40));
        var measurer = new Measurer(tube.Mesh, tube.Faces);
        Pick P(Vector3 p) => new(PickKind.Face, FaceAt(tube.Mesh, tube.Faces, p), D3.From(p));
        var m = measurer.Measure(P(new Vector3(5, 0, 4)), P(new Vector3(-3, 0, 4)));
        Assert.Equal(0, Value(m, Quantity.AxisDistance), 3);
        Assert.Equal(2, Value(m, Quantity.MinDistance), 3);
    }

    [Fact]
    public void Points_GiveDistanceAndDeltas()
    {
        var a = new Pick(PickKind.Point, 0, new D3(1, 2, 3));
        var b = new Pick(PickKind.Point, 0, new D3(4, 6, 3));
        var m = Measure(a, b);
        Assert.Equal(5, m.Values[0].Value, 12);
        Assert.Equal(3, Value(m, Quantity.DeltaX), 12);
        Assert.Equal(4, Value(m, Quantity.DeltaY), 12);
        Assert.Equal(0, Value(m, Quantity.DeltaZ), 12);
    }

    [Fact]
    public void PointAndPlane_GiveThePerpendicularDistance()
    {
        var point = new Pick(PickKind.Point, FaceAt(Model.Mesh, Model.Faces, new Vector3(40, 5, 5)), new D3(40, 5, 5));
        var m = Measure(point, FacePick(new Vector3(15, 8, 0)));
        Assert.Equal(5, m.Values[0].Value, 9);
    }

    [Fact]
    public void Freeform_GivesTheShortestDistance()
    {
        var model = Analyse([.. Sphere(Vector3.Zero, 5, 64, 32), .. Box(new Vector3(10, -2, -2), new Vector3(14, 2, 2))]);
        var measurer = new Measurer(model.Mesh, model.Faces);
        var sphere = new Pick(PickKind.Face, FaceAt(model.Mesh, model.Faces, new Vector3(0, 0, 5)), new D3(0, 0, 5));
        var face = new Pick(PickKind.Face, FaceAt(model.Mesh, model.Faces, new Vector3(10, 0, 0)), new D3(10, 0, 0));
        var m = measurer.Measure(sphere, face);
        Assert.Equal(Quantity.MinDistance, m.Values[0].Quantity);
        Assert.InRange(m.Values[0].Value, 5, 5.05);   // sphere surface between its corners lies slightly inside radius 5
        Assert.True(m.HasLine);
    }
}
