using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Viewer3D.Editing;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;

namespace ImageConverter.Viewer3D;

/// <summary>Editing: a box or cylinder to add or remove, placed by numbers or dragged and docked with the mouse.</summary>
public sealed partial class ModelViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenBody))]
    public partial bool IsBodyOpen { get; set; }

    public bool CanOpenBody => !IsBodyOpen;

    /// <summary>0 box, 1 cylinder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBox), nameof(IsCylinder))]
    public partial int BodyShapeIndex { get; set; }

    public bool IsBox => BodyShapeIndex == 0;

    public bool IsCylinder => BodyShapeIndex == 1;

    // Box from … to (shown unit); cylinder centre, diameter, length, axis
    [ObservableProperty] public partial decimal BoxFromX { get; set; }
    [ObservableProperty] public partial decimal BoxToX { get; set; }
    [ObservableProperty] public partial decimal BoxFromY { get; set; }
    [ObservableProperty] public partial decimal BoxToY { get; set; }
    [ObservableProperty] public partial decimal BoxFromZ { get; set; }
    [ObservableProperty] public partial decimal BoxToZ { get; set; }
    [ObservableProperty] public partial decimal CylX { get; set; }
    [ObservableProperty] public partial decimal CylY { get; set; }
    [ObservableProperty] public partial decimal CylZ { get; set; }
    [ObservableProperty] public partial decimal CylDiameter { get; set; }
    [ObservableProperty] public partial decimal CylLength { get; set; }

    /// <summary>0 X, 1 Y, 2 Z.</summary>
    [ObservableProperty] public partial int CylAxisIndex { get; set; }

    /// <summary>Also the mirror image at the middle of the model in X (both sides of a symmetric part).</summary>
    [ObservableProperty] public partial bool BodyMirror { get; set; }

    /// <summary>Why the body cannot be shown or used, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBodyMessage))]
    public partial string? BodyMessage { get; set; }

    public bool HasBodyMessage => BodyMessage is not null;

    /// <summary>The body as triangles (9 floats each) for the preview; null when there is none.</summary>
    internal float[]? BodyPreview { get; private set; }

    /// <summary>Everything to draw see-through over the model: the body, a drawn face and what it would build or cut.</summary>
    internal IReadOnlyList<(float[] Triangles, Vector4 Colour)> Previews
    {
        get
        {
            var list = new List<(float[], Vector4)>();
            if (BodyPreview is { } body) list.Add((body, new Vector4(0.12f, 0.62f, 0.85f, 1)));
            list.AddRange(_sketchPreview);
            if (_maskPreview is { } mask) list.Add(mask);
            if (_pushPreview is { } push) list.Add(push);
            return list;
        }
    }

    /// <summary>0: a docked body sits on the face (to add), 1: it sinks into the material (to remove).</summary>
    [ObservableProperty] public partial int DockModeIndex { get; set; }

    /// <summary>Extent of the model, so that positions can be read off: "x −24.75 … 24.75 · y … · z …".</summary>
    public string ModelExtentText
    {
        get
        {
            var b = Mesh.Bounds;
            // + 0.0 turns a rounded −0 into 0
            string R(float a, float c) => $"{Math.Round(a / UnitFactor, 3) + 0.0:0.###} … {Math.Round(c / UnitFactor, 3) + 0.0:0.###}";
            return Tr.T("Modell: x {0} · y {1} · z {2} {3}", R(b.Min.X, b.Max.X), R(b.Min.Y, b.Max.Y), R(b.Min.Z, b.Max.Z), UnitLabel);
        }
    }

    public string MirrorText => Tr.T("Auch gespiegelt (an x = {0:0.###} {1})", Mesh.Bounds.Center.X / UnitFactor, UnitLabel);

    /// <summary>Hooks the preview to every value of the body editor.</summary>
    private void InitEditing()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } n && IsBodyOpen && (n.StartsWith("Box", StringComparison.Ordinal) || n.StartsWith("Cyl", StringComparison.Ordinal)
                    || n is nameof(BodyShapeIndex) or nameof(BodyMirror)))
                UpdateBodyPreview();
        };
    }

    [RelayCommand]
    private void OpenBody()
    {
        // A box and a cylinder in the middle of the model, 10 mm or a third of its size, whichever is smaller
        var b = Mesh.Bounds;
        var c = b.Center / UnitFactor;
        var size = b.Size / UnitFactor;
        decimal D(float v) => Math.Round((decimal)v, 3);
        var half = new Vector3(Math.Min(5, size.X / 6), Math.Min(5, size.Y / 6), Math.Min(5, size.Z / 6));
        BoxFromX = D(c.X - half.X); BoxToX = D(c.X + half.X);
        BoxFromY = D(c.Y - half.Y); BoxToY = D(c.Y + half.Y);
        BoxFromZ = D(c.Z - half.Z); BoxToZ = D(c.Z + half.Z);
        CylX = D(c.X); CylY = D(c.Y); CylZ = D(c.Z);
        CylDiameter = D(Math.Min(5, Math.Min(size.X, size.Y) / 4));
        CylLength = D(size.Z + 2);
        CylAxisIndex = 2;
        IsBodyOpen = true;
        EditMessage = null;
        OnPropertyChanged(nameof(ModelExtentText));
        OnPropertyChanged(nameof(MirrorText));
        UpdateBodyPreview();
    }

    [RelayCommand]
    private void CloseBody()
    {
        IsBodyOpen = false;
        BodyPreview = null;
        BodyMessage = null;
        Redraw();
    }

    /// <summary>The body from the values in the editor (model units).</summary>
    private Body CurrentBody()
    {
        var f = UnitFactor;
        if (IsBox)
        {
            var a = new Vector3((float)BoxFromX, (float)BoxFromY, (float)BoxFromZ) * f;
            var b = new Vector3((float)BoxToX, (float)BoxToY, (float)BoxToZ) * f;
            return new Body(BodyShape.Box, Vector3.Min(a, b), Vector3.Max(a, b));
        }
        var c = new Vector3((float)CylX, (float)CylY, (float)CylZ) * f;
        var r = (float)CylDiameter * f / 2;
        var h = (float)CylLength * f / 2;
        var half = CylAxisIndex switch { 0 => new Vector3(h, r, r), 1 => new Vector3(r, h, r), _ => new Vector3(r, r, h) };
        return new Body(BodyShape.Cylinder, c - half, c + half, CylAxisIndex);
    }

    private float? MirrorAt => BodyMirror ? Mesh.Bounds.Center.X : null;

    /// <summary>Middle of the drawn face, else of the body (depth for the snapping reach).</summary>
    internal Vector3 BodyCentreOrSketch
    {
        get
        {
            if (_sketch is not { } k) return BodyCentre;
            var (a, b) = Others(k.Axis);
            Vector3 v = default;
            v[k.Axis] = k.Plane;
            v[a] = (k.A.X + k.B.X) / 2;
            v[b] = (k.A.Y + k.B.Y) / 2;
            return v;
        }
    }

    /// <summary>Middle of the body (for moving it at its own depth).</summary>
    internal Vector3 BodyCentre
    {
        get
        {
            try { var b = CurrentBody(); return (b.Min + b.Max) / 2; }
            catch (EditException) { return Mesh.Bounds.Center; }
        }
    }

    private void UpdateBodyPreview()
    {
        try
        {
            BodyPreview = ToTriangles(SolidEditor.BodyMesh(CurrentBody(), MirrorAt));
            BodyMessage = null;
        }
        catch (EditException e)
        {
            BodyPreview = null;
            BodyMessage = e.Message;
        }
        Redraw();
    }

    [RelayCommand]
    private Task AddBody() => CombineBody(add: true);

    [RelayCommand]
    private Task RemoveBody() => CombineBody(add: false);

    private async Task CombineBody(bool add)
    {
        if (!CanEdit) return;
        Body body;
        try
        {
            body = CurrentBody();
        }
        catch (EditException e)
        {
            BodyMessage = e.Message;
            return;
        }
        var mirror = MirrorAt;
        var size = body.Max - body.Min;
        var shape = body.Shape == BodyShape.Box
            ? Tr.T("Quader {0} × {1} × {2}", Len(size.X), Len(size.Y), Len(size.Z))
            : Tr.T("Zylinder ⌀ {0}", Len(body.Axis switch { 0 => MathF.Min(size.Y, size.Z), 1 => MathF.Min(size.X, size.Z), _ => MathF.Min(size.X, size.Y) }));
        var what = (add ? Tr.T("Hinzugefügt: {0}", shape) : Tr.T("Weggenommen: {0}", shape)) + (mirror is null ? "" : Tr.T(" (beidseitig)"));
        await RunEdit(what, mesh => SolidEditor.Combine(mesh, body, add, mirror));
        if (IsBodyOpen) UpdateBodyPreview();

        string Len(float v) => MeasureText.Short(new MeasureValue(Quantity.Distance, v), Inches);
    }

    // ------------------------------------------------------------------ move the body with the mouse

    /// <summary>The ray passes through the body (or its mirror image): a press there grabs it.</summary>
    internal bool HitsBody(Vector3 origin, Vector3 direction)
    {
        if (!IsBodyOpen || BodyPreview is null) return false;
        Body body;
        try { body = CurrentBody(); } catch (EditException) { return false; }
        if (RayHitsBox(origin, direction, body.Min, body.Max)) return true;
        if (MirrorAt is not { } x) return false;
        var min = new Vector3(2 * x - body.Max.X, body.Min.Y, body.Min.Z);
        var max = new Vector3(2 * x - body.Min.X, body.Max.Y, body.Max.Z);
        return RayHitsBox(origin, direction, min, max);
    }

    private static bool RayHitsBox(Vector3 o, Vector3 d, Vector3 min, Vector3 max)
    {
        float near = float.NegativeInfinity, far = float.PositiveInfinity;
        for (var i = 0; i < 3; i++)
        {
            float oi = o[i], di = d[i], lo = min[i], hi = max[i];
            if (MathF.Abs(di) < 1e-12f)
            {
                if (oi < lo || oi > hi) return false;
                continue;
            }
            var t1 = (lo - oi) / di;
            var t2 = (hi - oi) / di;
            if (t1 > t2) (t1, t2) = (t2, t1);
            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far) return false;
        }
        return far >= 0;
    }

    /// <summary>
    /// Docks the body onto a face: centred on the point under the mouse, its side against the face – sitting on it or
    /// sunk into the material (<see cref="DockModeIndex"/>). A cylinder turns to stand upright on the face. Faces are
    /// matched to the nearest axis (the body stays aligned with the axes).
    /// </summary>
    /// <param name="point">Point on the face.</param>
    /// <param name="outward">Normal of the face, pointing out of the material (towards the viewer).</param>
    internal void DockBody(Vector3 point, Vector3 outward, float range = 0)
    {
        if (!IsBodyOpen) return;
        Body body;
        try { body = CurrentBody(); } catch (EditException) { return; }
        var k = DominantAxis(outward);
        var sign = MathF.Sign(outward[k]);
        if (IsCylinder && CylAxisIndex != k)
        {
            CylAxisIndex = k;
            body = CurrentBody();
        }
        var size = body.Max - body.Min;
        var min = point - size / 2;
        var max = point + size / 2;
        var outside = DockModeIndex == 0;
        var (lo, hi) = (sign > 0) == outside ? (point[k], point[k] + size[k]) : (point[k] - size[k], point[k]);
        min[k] = lo;
        max[k] = hi;
        // sideways the body snaps with a side (a cylinder: its axis) onto a wall nearby
        if (range > 0)
            foreach (var axis in new[] { (k + 1) % 3, (k + 2) % 3 })
                if (SnapShift(axis, min[axis], max[axis], range, IsCylinder) is { } shift)
                {
                    min[axis] += shift;
                    max[axis] += shift;
                }
        SetBody(min, max);
    }

    /// <summary>Moves the body freely (Shift + drag).</summary>
    internal void MoveBody(Vector3 delta)
    {
        if (!IsBodyOpen) return;
        Body body;
        try { body = CurrentBody(); } catch (EditException) { return; }
        SetBody(body.Min + delta, body.Max + delta);
    }

    /// <summary>Writes a new position into the fields of the editor (the preview follows).</summary>
    private void SetBody(Vector3 min, Vector3 max)
    {
        var f = UnitFactor;
        decimal D(float v) => Math.Round((decimal)(v / f), 3);
        if (IsBox)
        {
            (BoxFromX, BoxToX, BoxFromY, BoxToY, BoxFromZ, BoxToZ) = (D(min.X), D(max.X), D(min.Y), D(max.Y), D(min.Z), D(max.Z));
            return;
        }
        var c = (min + max) / 2;
        (CylX, CylY, CylZ) = (D(c.X), D(c.Y), D(c.Z));
    }

    private static int DominantAxis(Vector3 v)
    {
        var a = Vector3.Abs(v);
        return a.X >= a.Y && a.X >= a.Z ? 0 : a.Y >= a.Z ? 1 : 2;
    }
}
