using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Viewer3D.Editing;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;

namespace ImageConverter.Viewer3D;

/// <summary>Editing: faces drawn on a flat face of the model (a list of them), built up or cut into the material.</summary>
public sealed partial class ModelViewModel
{
    /// <summary>Drawing a face: a press on a plane starts it, dragging spans it.</summary>
    [ObservableProperty] public partial bool SketchTool { get; set; }

    /// <summary>0 rectangle, 1 circle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SketchIsRectangle), nameof(SketchIsCircle))]
    public partial int SketchShapeIndex { get; set; }

    public bool SketchIsRectangle => SketchShapeIndex == 0;

    public bool SketchIsCircle => SketchShapeIndex == 1;

    /// <summary>The drawn faces; the selected one is moved, dimensioned and used.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SketchItem> Sketches { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSketches))]
    public partial SketchItem? SelectedSketch { get; set; }

    public bool HasSketches => Sketches.Count > 0;

    private int _sketchNumber;

    /// <summary>The selected drawn face (while drawing: the one being spanned).</summary>
    private DrawnFace? _sketch
    {
        get => SelectedSketch?.Data;
        set
        {
            if (SelectedSketch is { } item && value is { } v) item.Data = v;
        }
    }

    private bool _loadingSketch;

    partial void OnSelectedSketchChanged(SketchItem? value)
    {
        foreach (var item in Sketches) item.IsSelected = item == value;
        StopDimension();
        if (value is null)
        {
            HasSketch = false;
            UpdateSketchPreview();
            return;
        }
        _loadingSketch = true;
        SketchDepth = value.Depth;
        _loadingSketch = false;
        HasSketch = !_drawing;
        OnPropertyChanged(nameof(SketchAxesText));
        OnPropertyChanged(nameof(SketchIsRectangle));
        OnPropertyChanged(nameof(SketchIsCircle));
        WriteSketchSize();
        UpdateSketchPreview();
    }

    /// <summary>A drawn face is being spanned with the mouse.</summary>
    private bool _drawing;

    /// <summary>Removes a drawn face from the list (its ✕, "Verwerfen").</summary>
    internal void RemoveSketch(SketchItem item)
    {
        var wasSelected = item == SelectedSketch;
        Sketches.Remove(item);
        OnPropertyChanged(nameof(HasSketches));
        if (wasSelected) SelectedSketch = null;
        UpdateSketchPreview();
    }

    [RelayCommand]
    private void ClearSketches()
    {
        Sketches.Clear();
        SelectedSketch = null;
        OnPropertyChanged(nameof(HasSketches));
        UpdateSketchPreview();
    }

    /// <summary>The drawn face under the ray, if any (a press there selects it and grabs it).</summary>
    internal SketchItem? SketchAt(Vector3 origin, Vector3 direction)
    {
        foreach (var item in Sketches.Reverse())
        {
            var k = item.Data;
            if (MathF.Abs(direction[k.Axis]) < 1e-9f) continue;
            var t = (k.Plane - origin[k.Axis]) / direction[k.Axis];
            if (t < 0) continue;
            var p = InPlane(origin + direction * t, k.Axis);
            bool inside;
            if (k.Circle) inside = Vector2.Distance(p, k.A) <= Vector2.Distance(k.A, k.B);
            else
            {
                var min = Vector2.Min(k.A, k.B);
                var max = Vector2.Max(k.A, k.B);
                inside = p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;
            }
            if (inside) return item;
        }
        return null;
    }
    private IReadOnlyList<(float[] Triangles, Vector4 Colour)> _sketchPreview = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartSketch))]
    public partial bool HasSketch { get; set; }

    public bool CanStartSketch => !HasSketch;

    /// <summary>Depth or height of the build-up or cut (shown unit).</summary>
    [ObservableProperty] public partial decimal SketchDepth { get; set; } = 2;

    /// <summary>Size of the drawn face (shown unit): width and length of a rectangle, or the diameter of a circle in W.</summary>
    [ObservableProperty] public partial decimal SketchW { get; set; }

    [ObservableProperty] public partial decimal SketchH { get; set; }

    public string SketchAxesText => _sketch is { } k ? Tr.T("Fläche quer zu {0}, Breite in {1}, Länge in {2}", "XYZ"[k.Axis], "XYZ"[Others(k.Axis).A], "XYZ"[Others(k.Axis).B]) : "";

    partial void OnSketchToolChanged(bool value)
    {
        if (value) EditMessage = Tr.T("Auf eine ebene Fläche drücken und ziehen. Die Ecken rasten an Wänden ein, mit Umschalt frei.");
    }

    partial void OnSketchDepthChanged(decimal value)
    {
        if (!_loadingSketch && SelectedSketch is { } item) item.Depth = value;
        UpdateSketchPreview();
    }

    partial void OnSketchWChanged(decimal value) => ResizeSketch();

    partial void OnSketchHChanged(decimal value) => ResizeSketch();

    private static (int A, int B) Others(int axis) => axis switch { 0 => (1, 2), 1 => (0, 2), _ => (0, 1) };

    private static Vector2 InPlane(Vector3 p, int axis)
    {
        var (a, b) = Others(axis);
        return new Vector2(p[a], p[b]);
    }

    /// <summary>
    /// Starts a drawn face on the plane under the mouse; false (with a reason) if it is no plane across an axis. The
    /// first corner snaps onto walls within <paramref name="range"/> unless <paramref name="snap"/> is false (Shift).
    /// </summary>
    internal bool BeginSketch(Vector3 point, int face, Vector3 outward, bool snap = false, float range = 0)
    {
        if (!CanEdit) return false;
        if (Faces is not { } faces || face < 0 || faces.Faces[face].Kind != FaceKind.Plane)
        {
            EditMessage = Tr.T("Zeichnen geht auf ebenen Flächen.");
            return false;
        }
        var n = faces.Faces[face].Normal.ToVector3();
        var k = DominantAxis(n);
        if (MathF.Abs(n[k]) < (float)Math.Cos(Measurer.ParallelPlanes * Math.PI / 180))
        {
            EditMessage = Tr.T("Zeichnen geht bisher nur auf Flächen, die quer zu einer Achse liegen (nicht schräg).");
            return false;
        }
        var sign = MathF.Sign(outward[k]);
        var plane = (float)faces.Faces[face].Centre.ToVector3()[k];
        var at = snap ? SnapPoint(k, InPlane(point, k), range) : Round(InPlane(point, k));
        var item = new SketchItem(this, Tr.T("Fläche {0}", ++_sketchNumber), new DrawnFace(k, (int)sign, plane, at, at, SketchIsCircle));
        _drawing = true;
        Sketches.Add(item);
        OnPropertyChanged(nameof(HasSketches));
        SelectedSketch = item;
        return true;
    }

    /// <summary>Spans the drawn face to where the ray meets its plane; the corner snaps onto walls unless Shift is held.</summary>
    internal void DragSketch(Vector3 origin, Vector3 direction, bool snap = false, float range = 0)
    {
        if (_sketch is not { } k || MathF.Abs(direction[k.Axis]) < 1e-9f) return;
        var t = (k.Plane - origin[k.Axis]) / direction[k.Axis];
        if (t < 0) return;
        var p = InPlane(origin + direction * t, k.Axis);
        _sketch = k with { B = snap ? SnapPoint(k.Axis, p, range) : Round(p) };
        WriteSketchSize();
        UpdateSketchPreview();
    }

    /// <summary>The face is drawn: it stays for building up or cutting; a click without dragging draws nothing.</summary>
    internal void EndSketch()
    {
        if (_sketch is not { } k) return;
        var size = k.Circle ? Vector2.Distance(k.A, k.B) : MathF.Min(MathF.Abs(k.B.X - k.A.X), MathF.Abs(k.B.Y - k.A.Y));
        _drawing = false;
        if (size < 1e-3f)
        {
            if (SelectedSketch is { } empty) RemoveSketch(empty);
            return;
        }
        SketchTool = false;
        HasSketch = true;
        EditMessage = null;
        OnPropertyChanged(nameof(SketchAxesText));
        WriteSketchSize();
        UpdateSketchPreview();
    }

    private bool _writingSketch;

    private void WriteSketchSize()
    {
        if (_sketch is not { } k) return;
        _writingSketch = true;
        if (k.Circle) SketchW = Math.Round((decimal)(2 * Vector2.Distance(k.A, k.B) / UnitFactor), 3);
        else
        {
            SketchW = Math.Round((decimal)(MathF.Abs(k.B.X - k.A.X) / UnitFactor), 3);
            SketchH = Math.Round((decimal)(MathF.Abs(k.B.Y - k.A.Y) / UnitFactor), 3);
        }
        _writingSketch = false;
    }

    /// <summary>Typed sizes: the drawn face keeps its centre.</summary>
    private void ResizeSketch()
    {
        if (_writingSketch || _sketch is not { } k) return;
        var w = (float)SketchW * UnitFactor;
        if (k.Circle)
        {
            _sketch = k with { B = k.A + new Vector2(w / 2, 0) };
        }
        else
        {
            var c = (k.A + k.B) / 2;
            var half = new Vector2(w, (float)SketchH * UnitFactor) / 2;
            _sketch = k with { A = c - half, B = c + half };
        }
        UpdateSketchPreview();
    }

    /// <summary>The drawn face drawn out between two positions along its axis.</summary>
    private Body SketchBody(DrawnFace k, float from, float to)
    {
        var (a, b) = Others(k.Axis);
        Vector3 min = default, max = default;
        min[k.Axis] = MathF.Min(from, to);
        max[k.Axis] = MathF.Max(from, to);
        if (k.Circle)
        {
            var r = Vector2.Distance(k.A, k.B);
            (min[a], max[a], min[b], max[b]) = (k.A.X - r, k.A.X + r, k.A.Y - r, k.A.Y + r);
            return new Body(BodyShape.Cylinder, min, max, k.Axis);
        }
        (min[a], max[a]) = (MathF.Min(k.A.X, k.B.X), MathF.Max(k.A.X, k.B.X));
        (min[b], max[b]) = (MathF.Min(k.A.Y, k.B.Y), MathF.Max(k.A.Y, k.B.Y));
        return new Body(BodyShape.Box, min, max, k.Axis);
    }

    /// <summary>Overlap of 1 µm with the material, so that the build-up grows on and the cut leaves no skin.</summary>
    private const float Overlap = 0.001f;

    /// <summary>What would be built (outside the face) and what would be cut (into the material).</summary>
    private (Body Up, Body Down) SketchBodies(DrawnFace k)
    {
        var d = (float)SketchDepth * UnitFactor;
        var s = k.Sign;
        return (SketchBody(k, k.Plane - s * Overlap, k.Plane + s * d), SketchBody(k, k.Plane + s * Overlap, k.Plane - s * d));
    }

    private void UpdateSketchPreview()
    {
        var list = new List<(float[], Vector4)>();
        // the other drawn faces, pale
        foreach (var other in Sketches)
        {
            if (other == SelectedSketch) continue;
            var o = other.Data;
            try { list.Add((ToTriangles(SolidEditor.BodyMesh(SketchBody(o, o.Plane + o.Sign * 0.002f, o.Plane + o.Sign * 0.004f))), new Vector4(0.98f, 0.80f, 0.55f, 1))); }
            catch (EditException) { }
        }
        if (_sketch is not { } k)
        {
            _sketchPreview = list;
            Redraw();
            return;
        }
        try
        {
            // the drawn face, raised a hair above the surface, and – once there is a depth – both outcomes
            list.Add((ToTriangles(SolidEditor.BodyMesh(SketchBody(k, k.Plane + k.Sign * 0.002f, k.Plane + k.Sign * 0.004f))), new Vector4(0.95f, 0.55f, 0.15f, 1)));
            list.AddRange(DimensionMarks());
            if (HasSketch && SketchDepth > 0)
            {
                var (up, down) = SketchBodies(k);
                list.Add((ToTriangles(SolidEditor.BodyMesh(up)), new Vector4(0.18f, 0.66f, 0.38f, 1)));
                list.Add((ToTriangles(SolidEditor.BodyMesh(down)), new Vector4(0.85f, 0.25f, 0.28f, 1)));
            }
        }
        catch (EditException)
        {
            // a face of no size yet while drawing
        }
        _sketchPreview = list;
        Redraw();
    }

    private static float[] ToTriangles(Mesh mesh)
    {
        var tris = new float[mesh.TriangleCount * 9];
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Corners(t);
            a.CopyTo(tris, 9 * t);
            b.CopyTo(tris, 9 * t + 3);
            c.CopyTo(tris, 9 * t + 6);
        }
        return tris;
    }

    [RelayCommand]
    private Task BuildUp() => ApplySketch(up: true);

    [RelayCommand]
    private Task CutDown() => ApplySketch(up: false);

    private async Task ApplySketch(bool up)
    {
        if (_sketch is not { } k || SketchDepth <= 0) return;
        var (upBody, downBody) = SketchBodies(k);
        var body = up ? upBody : downBody;
        var depth = MeasureText.Short(new MeasureValue(Quantity.Distance, (double)SketchDepth * UnitFactor), Inches);
        var what = up ? Tr.T("Aufgebaut um {0}", depth) : Tr.T("Weggenommen um {0}", depth);
        // the drawn face stays in the list: it can be moved and used again
        await RunEdit(what, mesh => SolidEditor.Combine(mesh, body, add: up));
        UpdateSketchPreview();
    }

    /// <summary>"Verwerfen": the selected drawn face leaves the list.</summary>
    [RelayCommand]
    private void CancelSketch()
    {
        StopDimension();
        if (SelectedSketch is { } item) RemoveSketch(item);
        HasSketch = false;
        SketchTool = false;
        _sketchPreview = [];
        Redraw();
    }
}
