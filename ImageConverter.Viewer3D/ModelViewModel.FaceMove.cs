using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Viewer3D.Editing;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;

namespace ImageConverter.Viewer3D;

/// <summary>Editing: a flat face (or a masked part of it) moved along its normal; the body grows or shrinks with it.</summary>
public sealed partial class ModelViewModel
{
    /// <summary>Moving a face: hovering shows the face, a press picks it, dragging moves it along its normal.</summary>
    [ObservableProperty] public partial bool PushTool { get; set; }

    /// <summary>The picked face: its outline, the triangles (for the highlight) and the distances to parallel faces.</summary>
    private (int Face, SolidEditor.FaceOutline Outline, double[] Snaps)? _push;

    private (float[] Triangles, Vector4 Colour)? _pushPreview;
    private (float[] Triangles, Vector4 Colour)? _maskPreview;

    // The mask on the picked face: from … to in the two directions of the face (shown unit)
    [ObservableProperty] public partial decimal MaskFromU { get; set; }
    [ObservableProperty] public partial decimal MaskToU { get; set; }
    [ObservableProperty] public partial decimal MaskFromV { get; set; }
    [ObservableProperty] public partial decimal MaskToV { get; set; }

    /// <summary>Names of the two directions of the face ("Y", "Z" for a face across X; otherwise "U", "V").</summary>
    public string MaskNameU => _push is { } p ? AxisName(p.Outline.Normal, 0) : "";

    public string MaskNameV => _push is { } p ? AxisName(p.Outline.Normal, 1) : "";

    private static string AxisName(Vector3 n, int which)
    {
        var k = DominantAxis(n);
        if (MathF.Abs(n[k]) < 0.9999f) return which == 0 ? "U" : "V";
        // frame of the editor: across X → (Y, Z), across Y → (Z, X), across Z → (X, Y)
        return k switch { 0 => which == 0 ? "Y" : "Z", 1 => which == 0 ? "Z" : "X", _ => which == 0 ? "X" : "Y" };
    }

    private bool _settingMask;

    /// <summary>
    /// How the two directions of the face frame run against the file axes: −1 where the frame runs backwards (faces
    /// facing −X, −Y: first direction; −Z: second), so that the fields always show coordinates of the file.
    /// </summary>
    private static (int U, int V) FrameSigns(Vector3 n)
    {
        var k = DominantAxis(n);
        if (MathF.Abs(n[k]) < 0.9999f || n[k] > 0) return (1, 1);
        return k == 2 ? (1, -1) : (-1, 1);
    }

    /// <summary>The mask as a rectangle in the frame of the face (model units).</summary>
    private (double MinU, double MaxU, double MinV, double MaxV) MaskRect(Vector3 n)
    {
        var (su, sv) = FrameSigns(n);
        var f = UnitFactor;
        double a = (double)MaskFromU * f * su, b = (double)MaskToU * f * su, c = (double)MaskFromV * f * sv, d = (double)MaskToV * f * sv;
        return (Math.Min(a, b), Math.Max(a, b), Math.Min(c, d), Math.Max(c, d));
    }

    partial void OnMaskFromUChanged(decimal value) => MaskChanged();
    partial void OnMaskToUChanged(decimal value) => MaskChanged();
    partial void OnMaskFromVChanged(decimal value) => MaskChanged();
    partial void OnMaskToVChanged(decimal value) => MaskChanged();

    private void MaskChanged()
    {
        if (_settingMask) return;
        UpdatePushPreview();
    }

    /// <summary>The picked face cut to the mask; null if the mask covers nothing.</summary>
    private SolidEditor.FaceOutline? MaskedOutline()
    {
        if (_push is not { } p) return null;
        try
        {
            var (minU, maxU, minV, maxV) = MaskRect(p.Outline.Normal);
            return SolidEditor.Mask(p.Outline, minU, maxU, minV, maxV);
        }
        catch (EditException)
        {
            return null;
        }
    }

    /// <summary>The mask on the whole face again.</summary>
    [RelayCommand]
    private void ResetMask()
    {
        if (_push is not { } p) return;
        var (minU, maxU, minV, maxV) = SolidEditor.Extent(p.Outline);
        var (su, sv) = FrameSigns(p.Outline.Normal);
        var f = UnitFactor;
        decimal D(double v) => Math.Round((decimal)(v / f), 3);
        _settingMask = true;
        (MaskFromU, MaskToU) = (D(Math.Min(minU * su, maxU * su)), D(Math.Max(minU * su, maxU * su)));
        (MaskFromV, MaskToV) = (D(Math.Min(minV * sv, maxV * sv)), D(Math.Max(minV * sv, maxV * sv)));
        _settingMask = false;
        UpdatePushPreview();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PushText))]
    public partial decimal PushDistance { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PushText))]
    public partial bool PushSnapped { get; set; }

    [ObservableProperty] public partial bool HasPushFace { get; set; }

    public string PushText => !HasPushFace ? "" : PushDistance == 0
        ? Tr.T("Fläche gewählt: ziehen (rastet an parallelen Flächen ein) oder Abstand eintippen.")
        : (PushDistance > 0 ? Tr.T("Nach außen um {0} – der Körper wird größer.", Len((double)PushDistance)) : Tr.T("Nach innen um {0} – der Körper wird kleiner.", Len(-(double)PushDistance)))
          + (PushSnapped ? " " + Tr.T("(liegt genau an einer parallelen Fläche)") : "");

    private string Len(double shown) => MeasureText.Short(new MeasureValue(Quantity.Distance, shown * UnitFactor), Inches);

    partial void OnPushToolChanged(bool value)
    {
        if (value) EditMessage = Tr.T("Eine ebene Fläche anklicken und ziehen. Eine verdeckte Fläche (z. B. zur Wand hin) mit einer Schnittfläche freilegen.");
        else
        {
            HoverFace = -1;
            Redraw();
        }
    }

    partial void OnPushDistanceChanged(decimal value) => UpdatePushPreview();

    /// <summary>Picks a flat face for moving; false (with a reason) if it is not flat.</summary>
    internal bool BeginPush(int face, Vector3 outward)
    {
        if (!CanEdit || Faces is not { } faces || face < 0) return false;
        var f = faces.Faces[face];
        if (f.Kind != FaceKind.Plane)
        {
            EditMessage = Tr.T("Verschieben geht mit ebenen Flächen.");
            return false;
        }
        var n = f.Normal.ToVector3();
        if (Vector3.Dot(n, outward) < 0) n = -n;
        try
        {
            var outline = SolidEditor.Outline(Mesh, faces.TrianglesOf(face), n);
            // Where parallel faces lie, measured along the normal from this one: moving snaps onto them
            var snaps = new List<double>();
            var cos = Math.Cos(Measurer.ParallelPlanes * Math.PI / 180);
            foreach (var other in faces.Faces)
            {
                if (other.Id == face || other.Kind != FaceKind.Plane) continue;
                var on = other.Normal.ToVector3();
                if (MathF.Abs(Vector3.Dot(on, n)) < cos) continue;
                snaps.Add(Vector3.Dot(n, other.Centre.ToVector3()) - outline.Plane);
            }
            _push = (face, outline, [.. snaps.Where(d => Math.Abs(d) > 1e-6).Distinct()]);
        }
        catch (EditException e)
        {
            EditMessage = e.Message;
            return false;
        }
        HasPushFace = true;
        PushSnapped = false;
        PushDistance = 0;
        EditMessage = null;
        OnPropertyChanged(nameof(PushText));
        OnPropertyChanged(nameof(MaskNameU));
        OnPropertyChanged(nameof(MaskNameV));
        ResetMask();
        return true;
    }

    /// <summary>
    /// Sets the distance while dragging; within <paramref name="snapRange"/> (model units, a few pixels) of a parallel
    /// face it lies exactly on that face.
    /// </summary>
    internal void DragPush(double distance, double snapRange)
    {
        if (_push is not { } p) return;
        var best = p.Snaps.Select(d => (double?)d).OrderBy(d => Math.Abs(d!.Value - distance)).FirstOrDefault();
        var snapped = best is { } s && Math.Abs(s - distance) <= snapRange;
        PushSnapped = snapped;
        PushDistance = Math.Round((decimal)((snapped ? best!.Value : distance) / UnitFactor), snapped ? 6 : 2);
    }

    /// <summary>Released after dragging: the face is moved (one step for undo).</summary>
    internal async Task EndPush()
    {
        if (_push is null || PushDistance == 0) return;
        await ApplyPush();
    }

    /// <summary>Point and normal of the picked face (for dragging along the normal).</summary>
    internal (Vector3 Normal, double Plane)? PushAxis => _push is { } p ? (p.Outline.Normal, p.Outline.Plane) : null;

    [RelayCommand]
    private async Task ApplyPush()
    {
        if (_push is not { } p || PushDistance == 0 || !CanEdit) return;
        var distance = (double)PushDistance * UnitFactor;
        if (MaskedOutline() is not { } outline)
        {
            EditMessage = Tr.T("Die Maske deckt nichts von der Fläche ab.");
            return;
        }
        var what = distance > 0 ? Tr.T("Fläche um {0} nach außen verschoben", Len((double)PushDistance)) : Tr.T("Fläche um {0} nach innen verschoben", Len(-(double)PushDistance));
        if (await RunEdit(what, mesh => SolidEditor.MoveFace(mesh, outline, distance))) CancelPush();
    }

    [RelayCommand]
    private void CancelPush()
    {
        _push = null;
        _pushPreview = null;
        _maskPreview = null;
        HasPushFace = false;
        PushDistance = 0;
        PushSnapped = false;
        Redraw();
    }

    private void UpdatePushPreview()
    {
        _pushPreview = null;
        _maskPreview = null;
        if (_push is { } p)
        {
            var f = UnitFactor;
            try
            {
                // the mask frame: a thin orange plate over the chosen part of the face
                var (minU, maxU, minV, maxV) = MaskRect(p.Outline.Normal);
                _maskPreview = (ToTriangles(SolidEditor.MaskFrameMesh(p.Outline, minU, maxU, minV, maxV, 0.05 * Math.Max(1, Mesh.Bounds.Diagonal / 100))),
                    new Vector4(0.95f, 0.55f, 0.15f, 1));
            }
            catch (EditException)
            {
            }
            if (PushDistance != 0 && MaskedOutline() is { } outline)
            {
                try
                {
                    var d = (double)PushDistance * f;
                    var colour = d > 0 ? new Vector4(0.18f, 0.66f, 0.38f, 1) : new Vector4(0.85f, 0.25f, 0.28f, 1);
                    _pushPreview = (ToTriangles(SolidEditor.FaceToolMesh(outline, d)), colour);
                }
                catch (EditException)
                {
                }
            }
        }
        Redraw();
    }

    /// <summary>The picked face (orange) for the highlights.</summary>
    internal int PushFace => _push?.Face ?? -1;
}
