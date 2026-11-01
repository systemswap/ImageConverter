using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Viewer3D.Editing;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;

namespace ImageConverter.Viewer3D;

/// <summary>Editing: moving a drawn face (snapping onto walls) and placing it by a dimension to a wall, edge or face.</summary>
public sealed partial class ModelViewModel
{
    /// <summary>Where the ray meets the plane of the drawn face, in its two directions; null if it runs along the plane.</summary>
    internal Vector2? SketchPlanePoint(Vector3 origin, Vector3 direction)
    {
        if (_sketch is not { } k || MathF.Abs(direction[k.Axis]) < 1e-9f) return null;
        var t = (k.Plane - origin[k.Axis]) / direction[k.Axis];
        return t < 0 ? null : InPlane(origin + direction * t, k.Axis);
    }

    /// <summary>The ray passes through the drawn face (a press there grabs it for moving).</summary>
    internal bool SketchContains(Vector3 origin, Vector3 direction)
    {
        if (!HasSketch || _sketch is not { } k || SketchPlanePoint(origin, direction) is not { } p) return false;
        if (k.Circle) return Vector2.Distance(p, k.A) <= Vector2.Distance(k.A, k.B);
        var min = Vector2.Min(k.A, k.B);
        var max = Vector2.Max(k.A, k.B);
        return p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;
    }

    private Vector2 _sketchMoveStart;

    /// <summary>Positions along each file axis where a flat face stands across that axis (walls, sides of ribs).</summary>
    private float[][]? _snapLines;

    private float[] SnapLines(int axis)
    {
        if (_snapLines is null && Faces is { } faces)
        {
            var cos = (float)Math.Cos(Measurer.ParallelPlanes * Math.PI / 180);
            _snapLines = new float[3][];
            for (var a = 0; a < 3; a++)
                _snapLines[a] = [.. faces.Faces.Where(f => f.Kind == FaceKind.Plane && MathF.Abs(f.Normal.ToVector3()[a]) > cos)
                    .Select(f => (float)f.Centre.ToVector3()[a]).Distinct().Order()];
        }
        return _snapLines?[axis] ?? [];
    }

    /// <summary>The line nearest <paramref name="value"/> within <paramref name="range"/>, or null.</summary>
    private float? NearestLine(int axis, float value, float range)
    {
        float? best = null;
        var bestDistance = range;
        foreach (var line in SnapLines(axis))
        {
            var d = MathF.Abs(line - value);
            if (d <= bestDistance) (best, bestDistance) = (line, d);
        }
        return best;
    }

    /// <summary>A point of the drawn face: onto the nearest wall in each direction (within the range), else on 0.01.</summary>
    private Vector2 SnapPoint(int planeAxis, Vector2 p, float range)
    {
        var (a, b) = Others(planeAxis);
        var r = Round(p);
        return new Vector2(NearestLine(a, p.X, range) ?? r.X, NearestLine(b, p.Y, range) ?? r.Y);
    }

    /// <summary>
    /// How far to shift a span (low … high) along an axis so that one of its ends lies on a wall within the range; for a
    /// circle (<paramref name="centre"/>) its middle.
    /// </summary>
    private float? SnapShift(int axis, float low, float high, float range, bool centre)
    {
        float? best = null;
        var bestDistance = range;
        foreach (var at in centre ? [(low + high) / 2] : new[] { low, high })
            if (NearestLine(axis, at, bestDistance) is { } line && MathF.Abs(line - at) <= bestDistance)
                (best, bestDistance) = (line - at, MathF.Abs(line - at));
        return best;
    }

    internal void BeginSketchMove()
    {
        if (_sketch is { } k) _sketchMoveStart = k.A;
    }

    /// <summary>
    /// Dragging the drawn face by the whole way since the press: its edges snap onto walls within
    /// <paramref name="range"/>; free (on 0.01 of the shown unit) with <paramref name="snap"/> false (Shift).
    /// </summary>
    internal void DragSketchMove(Vector2 total, bool snap, float range)
    {
        if (_sketch is not { } k) return;
        var a = Round(_sketchMoveStart + total);
        var shift = a - k.A;
        if (snap)
        {
            var (ia, ib) = Others(k.Axis);
            var moved = k with { A = k.A + shift, B = k.B + shift };
            float lowA, highA, lowB, highB;
            if (moved.Circle) (lowA, highA, lowB, highB) = (moved.A.X, moved.A.X, moved.A.Y, moved.A.Y);
            else (lowA, highA, lowB, highB) = (MathF.Min(moved.A.X, moved.B.X), MathF.Max(moved.A.X, moved.B.X), MathF.Min(moved.A.Y, moved.B.Y), MathF.Max(moved.A.Y, moved.B.Y));
            shift.X += SnapShift(ia, lowA, highA, range, moved.Circle) ?? 0;
            shift.Y += SnapShift(ib, lowB, highB, range, moved.Circle) ?? 0;
        }
        MoveSketch(shift);
    }

    /// <summary>A position on 0.01 of the shown unit, so that drawn and dragged faces get clean values.</summary>
    private Vector2 Round(Vector2 p)
    {
        var step = 0.01f * UnitFactor;
        return new Vector2(MathF.Round(p.X / step) * step, MathF.Round(p.Y / step) * step);
    }

    /// <summary>Moves the drawn face within its plane.</summary>
    internal void MoveSketch(Vector2 delta)
    {
        if (_sketch is not { } k) return;
        _sketch = k with { A = k.A + delta, B = k.B + delta };
        UpdateSketchPreview();
        RefreshDimension();
    }

    /// <summary>0 off, 1: pick an edge of the drawn face, 2: pick a wall, edge or face of the model, 3: type the distance.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDimensioning), nameof(HasDimension))]
    public partial int DimensionStep { get; set; }

    public bool IsDimensioning => DimensionStep is 1 or 2;

    public bool HasDimension => DimensionStep == 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDimensionText))]
    public partial string? DimensionText { get; set; }

    public bool HasDimensionText => DimensionText is not null;

    /// <summary>The distance between the picked edge and the reference (shown unit).</summary>
    [ObservableProperty] public partial decimal DimensionValue { get; set; }

    /// <summary>
    /// Edge of the drawn face (0/1: low/high in its first direction, 2/3: in its second, 4: centre of a circle), the file
    /// axis the distance runs along, the reference position on that axis, which side the edge lies on, and whether the
    /// reference is exact.
    /// </summary>
    private (int Edge, int Axis, float Reference, int Side, bool Exact, string What)? _dimension;

    private (int Edge, Vector3 A, Vector3 B)? _dimensionReferenceEdge;
    private int _dimensionFace = -1;
    private bool _settingDimension;

    [RelayCommand]
    private void StartDimension()
    {
        if (!HasSketch) return;
        _dimension = null;
        _dimensionReferenceEdge = null;
        _dimensionFace = -1;
        DimensionStep = 1;
        DimensionText = _sketch is { Circle: true }
            ? Tr.T("1. Die Kreismitte anklicken – oder in den Kreis, um die Höhe zu bemaßen.")
            : Tr.T("1. Eine Kante der gezeichneten Fläche anklicken – oder in die Fläche, um die Höhe zu bemaßen (wie tief weggenommen oder wie hoch aufgebaut wird).");
        UpdateSketchPreview();
    }

    [RelayCommand]
    private void StopDimension()
    {
        DimensionStep = 0;
        DimensionText = null;
        _dimension = null;
        _dimensionReferenceEdge = null;
        _dimensionFace = -1;
        UpdateSketchPreview();
    }

    /// <summary>The edges of the drawn face as segments in the model (a circle: its centre twice).</summary>
    internal IReadOnlyList<(int Edge, Vector3 A, Vector3 B)> SketchEdges
    {
        get
        {
            if (_sketch is not { } k) return [];
            Vector3 P(float a, float b) { var (ia, ib) = Others(k.Axis); Vector3 v = default; v[k.Axis] = k.Plane; v[ia] = a; v[ib] = b; return v; }
            if (k.Circle) return [(4, P(k.A.X, k.A.Y), P(k.A.X, k.A.Y))];
            var min = Vector2.Min(k.A, k.B);
            var max = Vector2.Max(k.A, k.B);
            return
            [
                (0, P(min.X, min.Y), P(min.X, max.Y)),
                (1, P(max.X, min.Y), P(max.X, max.Y)),
                (2, P(min.X, min.Y), P(max.X, min.Y)),
                (3, P(min.X, max.Y), P(max.X, max.Y)),
            ];
        }
    }

    private int _dimensionEdge = -1;

    /// <summary>Height dimension: the reference lies on the material side (the cut's bottom is measured).</summary>
    private bool _heightTowardsMaterial = true;

    /// <summary>The face taken as reference (highlighted).</summary>
    internal int DimensionFace => _dimensionFace;

    internal void PickSketchEdge(int edge)
    {
        if (DimensionStep != 1) return;
        _dimensionEdge = edge;
        DimensionStep = 2;
        DimensionText = Tr.T("2. Jetzt eine Wand, eine Kante oder eine Fläche des Modells anklicken.");
        UpdateSketchPreview();
    }

    /// <summary>
    /// Position of the picked edge of the drawn face along a file axis; for the height (edge 5) the end of the build-up
    /// or cut on the side of the reference: the bottom of the cut towards the material, the top of the build-up away from it.
    /// </summary>
    private float EdgeCoordinate(DrawnFace k, int edge, int axis)
    {
        if (edge == 5)
        {
            var d = (float)SketchDepth * UnitFactor;
            return _heightTowardsMaterial ? k.Plane - k.Sign * d : k.Plane + k.Sign * d;
        }
        var (ia, _) = Others(k.Axis);
        var inA = axis == ia;
        if (edge == 4) return inA ? k.A.X : k.A.Y;
        var min = Vector2.Min(k.A, k.B);
        var max = Vector2.Max(k.A, k.B);
        return edge switch { 0 => min.X, 1 => max.X, 2 => min.Y, _ => max.Y };
    }

    /// <summary>
    /// The reference: a sharp edge of the model near the click (exact if it runs parallel to the picked edge), else a flat
    /// face across the measuring direction (exact), else the clicked point (approximate).
    /// </summary>
    /// <param name="hit">Point and face under the mouse, if any.</param>
    /// <param name="sharpEdge">Sharp edge of the model within a few pixels of the click, if any.</param>
    internal void PickReference((Vector3 Point, int Face, Vector3 Normal)? hit, (Vector3 A, Vector3 B)? sharpEdge)
    {
        if (DimensionStep != 2 || _sketch is not { } k || (hit is null && sharpEdge is null)) return;
        var (ia, ib) = Others(k.Axis);
        // measuring direction: across the picked edge; for a circle across the reference; for the height along the normal
        int axis;
        if (_dimensionEdge == 5) axis = k.Axis;
        else if (_dimensionEdge < 4) axis = _dimensionEdge < 2 ? ia : ib;
        else if (sharpEdge is { } se) axis = MathF.Abs((se.B - se.A)[ia]) >= MathF.Abs((se.B - se.A)[ib]) ? ib : ia;
        else if (hit is { } h0 && Faces is { } f0 && h0.Face >= 0 && f0.Faces[h0.Face].Kind == FaceKind.Plane)
        {
            var n = f0.Faces[h0.Face].Normal.ToVector3();
            axis = MathF.Abs(n[ia]) >= MathF.Abs(n[ib]) ? ia : ib;
        }
        else axis = ia;

        float reference;
        bool exact;
        string what;
        _dimensionReferenceEdge = null;
        _dimensionFace = -1;
        if (sharpEdge is { } e && MathF.Abs(e.A[axis] - e.B[axis]) < 1e-4f)
        {
            reference = (e.A[axis] + e.B[axis]) / 2;
            exact = true;
            what = Tr.T("Kante");
            _dimensionReferenceEdge = (0, e.A, e.B);
        }
        else if (hit is { } h && Faces is { } faces && h.Face >= 0 && faces.Faces[h.Face] is { Kind: FaceKind.Plane } face
                 && MathF.Abs(face.Normal.ToVector3()[axis]) > (float)Math.Cos(Measurer.ParallelPlanes * Math.PI / 180))
        {
            reference = (float)face.Centre.ToVector3()[axis];
            exact = true;
            what = Tr.T("Fläche");
            _dimensionFace = h.Face;
        }
        else if (hit is { } hp)
        {
            reference = hp.Point[axis];
            exact = false;
            what = Tr.T("Punkt (ungefähr)");
        }
        else return;

        if (_dimensionEdge == 5) _heightTowardsMaterial = (reference - k.Plane) * k.Sign < 0;
        var at = EdgeCoordinate(k, _dimensionEdge, axis);
        var side = at >= reference ? 1 : -1;
        _dimension = (_dimensionEdge, axis, reference, side, exact, what);
        DimensionStep = 3;
        RefreshDimension();
        UpdateSketchPreview();
    }

    /// <summary>Shows the present distance and the reference.</summary>
    private void RefreshDimension()
    {
        if (_dimension is not { } d || _sketch is not { } k) return;
        var at = EdgeCoordinate(k, d.Edge, d.Axis);
        _settingDimension = true;
        DimensionValue = Math.Round((decimal)(MathF.Abs(at - d.Reference) / UnitFactor), 3);
        _settingDimension = false;
        var edge = d.Edge switch
        {
            4 => Tr.T("Kreismitte"),
            5 => _heightTowardsMaterial ? Tr.T("Boden des Wegnehmens") : Tr.T("Oberseite des Aufbaus"),
            _ => Tr.T("Kante"),
        };
        DimensionText = Tr.T("{0} ↔ {1} bei {2} = {3}: Abstand entlang {4}. Maß eintippen, die gezeichnete Fläche rückt nach.",
            edge, d.What, "XYZ"[d.Axis], Len(d.Reference / UnitFactor), "XYZ"[d.Axis]);
    }

    partial void OnDimensionValueChanged(decimal value)
    {
        if (_settingDimension || _dimension is not { } d || _sketch is not { } k) return;
        var target = d.Reference + d.Side * (float)value * UnitFactor;
        if (d.Edge == 5)
        {
            // the height: the cut or build-up reaches exactly to the measured position
            var depth = MathF.Abs(target - k.Plane) / UnitFactor;
            _settingDimension = true;
            SketchDepth = Math.Round((decimal)depth, 4);
            _settingDimension = false;
            return;
        }
        var delta = target - EdgeCoordinate(k, d.Edge, d.Axis);
        var (ia, _) = Others(k.Axis);
        MoveSketch(d.Axis == ia ? new Vector2(delta, 0) : new Vector2(0, delta));
    }

    /// <summary>Thin bars over the picked edge and the reference edge (magenta) while dimensioning.</summary>
    private IEnumerable<(float[], Vector4)> DimensionMarks()
    {
        if (DimensionStep == 0) yield break;
        var colour = new Vector4(0.80f, 0.20f, 0.75f, 1);
        var t = 0.04f * MathF.Max(1, Mesh.Bounds.Diagonal / 100);
        foreach (var (edge, a, b) in SketchEdges)
            if (edge == _dimensionEdge && DimensionStep >= 2) yield return (Bar(a, b, t), colour);
        if (_dimensionReferenceEdge is { } r) yield return (Bar(r.A, r.B, t), colour);
    }

    private static float[] Bar(Vector3 a, Vector3 b, float t)
    {
        var min = Vector3.Min(a, b) - new Vector3(t);
        var max = Vector3.Max(a, b) + new Vector3(t);
        try { return ToTriangles(SolidEditor.BodyMesh(new Body(BodyShape.Box, min, max))); }
        catch (EditException) { return []; }
    }
}
