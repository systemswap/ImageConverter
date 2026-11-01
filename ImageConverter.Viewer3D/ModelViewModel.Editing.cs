using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Viewer3D.Editing;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;

namespace ImageConverter.Viewer3D;

/// <summary>
/// Editing the model: changing a length between two parallel planes, undo and redo, saving as STL. Every edit produces a
/// new mesh that replaces the old one as a whole; faces, measuring and the drawing are then worked out again.
/// </summary>
public sealed partial class ModelViewModel
{
    /// <summary>Most steps kept for undo (each holds a whole model).</summary>
    private const int MaxUndo = 30;

    private readonly List<(Mesh Mesh, string What)> _undo = [];
    private readonly List<(Mesh Mesh, string What)> _redo = [];

    /// <summary>The model was replaced (camera and drawing take the new one).</summary>
    public event Action? MeshReplaced;

    /// <summary>Changed since opening or the last "Save as".</summary>
    [ObservableProperty] public partial bool IsModified { get; set; }

    /// <summary>An edit is being computed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsEditBusy { get; set; }

    /// <summary>Result or reason of the last edit, for the column.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditMessage))]
    public partial string? EditMessage { get; set; }

    public bool HasEditMessage => EditMessage is not null;

    /// <summary>Only closed models can be edited (the geometry kernel needs a solid).</summary>
    public bool CanEdit => Summary is { IsClosed: true } && !IsEditBusy;

    public string EditHint => Summary is null
        ? Tr.T("Das Modell wird geprüft …")
        : !Summary.IsClosed
            ? Tr.T("Bearbeiten lässt sich nur ein geschlossenes Modell (Kennzahl „Geschlossen: ja“).")
            : Tr.T("Länge ändern: mit „Messen“ zwei parallele Flächen anklicken, dann in der Liste der Maße auf „Länge ändern“.");

    // ------------------------------------------------------------------ change a length

    /// <summary>The measurement whose length is being changed; null when the editor is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResizing), nameof(ResizeCurrentText))]
    public partial MeasurementItem? ResizeTarget { get; set; }

    public bool IsResizing => ResizeTarget is not null;

    /// <summary>Direction from the first to the second plane, and both positions along it (model units).</summary>
    private (Vector3 Normal, double From, double To)? _resize;

    /// <summary>The new length in the shown unit.</summary>
    [ObservableProperty] public partial decimal ResizeNew { get; set; }

    /// <summary>0: the first clicked face stays, 1: the second.</summary>
    [ObservableProperty] public partial int ResizeFixedIndex { get; set; }

    /// <summary>Dividing plane: distance from the face that stays, in the shown unit.</summary>
    [ObservableProperty] public partial decimal ResizeCut { get; set; }

    partial void OnResizeFixedIndexChanged(int value)
    {
        UseUniformStretch();
        Redraw();
    }

    partial void OnResizeCutChanged(decimal value) => Redraw();

    /// <summary>Stretches with the same cross-section between the faces (positions along the normal), widest first.</summary>
    private IReadOnlyList<(double From, double To)> _uniform = [];

    /// <summary>Where the model is uniform, for the column; null while searching or if there is no such stretch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUniformText))]
    public partial string? UniformText { get; set; }

    public bool HasUniformText => UniformText is not null;

    public string ResizeCurrentText => _resize is { } r
        ? Tr.T("Jetzt: {0}", MeasureText.Short(new MeasureValue(Quantity.Distance, r.To - r.From), Inches))
        : "";

    /// <summary>The two picks are parallel planes: their common direction (first → second) and positions.</summary>
    private (Vector3 Normal, double From, double To)? ParallelPlanes(Pick a, Pick b)
    {
        if (Faces is not { } faces || a.Kind != PickKind.Face || b.Kind != PickKind.Face || a.Face < 0 || b.Face < 0) return null;
        var fa = faces.Faces[a.Face];
        var fb = faces.Faces[b.Face];
        if (fa.Kind != FaceKind.Plane || fb.Kind != FaceKind.Plane) return null;
        var n = fa.Normal;
        var cos = Math.Abs(D3.Dot(n, fb.Normal)) / Math.Max(n.Length * fb.Normal.Length, 1e-30);
        if (cos < Math.Cos(Measurer.ParallelPlanes * Math.PI / 180)) return null;
        n /= n.Length;
        var from = D3.Dot(n, fa.Centre);
        var to = D3.Dot(n, fb.Centre);
        if (to < from) (n, from, to) = (-n, -from, -to);
        return to - from > 1e-9 ? (n.ToVector3(), from, to) : null;
    }

    private async void StartResize(MeasurementItem item)
    {
        if (!CanEdit || ParallelPlanes(item.A, item.B) is not { } r) return;
        _resize = r;
        _uniform = [];
        ResizeTarget = item;
        ResizeFixedIndex = 0;
        var length = (r.To - r.From) / UnitFactor;
        ResizeNew = Math.Round((decimal)length, 3);
        ResizeCut = Math.Round((decimal)(length / 2), 3);
        EditMessage = null;
        UniformText = Tr.T("Suche eine Stelle mit gleichem Querschnitt …");
        Redraw();

        // Where the model is the same throughout, a length change leaves every feature as it is: the plane goes there
        var mesh = Mesh;
        var ct = _cts.Token;
        try
        {
            var found = await Task.Run(() => SolidEditor.UniformStretches(mesh, r.Normal, r.From, r.To, ct), ct);
            if (ResizeTarget != item || ct.IsCancellationRequested) return;
            _uniform = found;
            if (found.Count == 0)
            {
                UniformText = Tr.T("Kein Bereich mit gleichem Querschnitt: was die Trennebene schneidet, wird mit verlängert.");
                return;
            }
            UseUniformStretch();
        }
        catch (OperationCanceledException)
        {
        }
        catch (EditException e)
        {
            UniformText = e.Message;
        }
    }

    /// <summary>Puts the dividing plane into the middle of the widest uniform stretch and says where that is.</summary>
    [RelayCommand]
    private void UseUniformStretch()
    {
        if (_resize is not { } r || _uniform.Count == 0) return;
        var (a, b) = _uniform[0];
        // measured from the face that stays
        var (near, far) = ResizeFixedIndex == 0 ? (a - r.From, b - r.From) : (r.To - b, r.To - a);
        ResizeCut = Math.Round((decimal)((near + far) / 2 / UnitFactor), 3);
        UniformText = Tr.T("Gleicher Querschnitt von {0} bis {1} ab der festen Fläche: dort ändert die Trennebene nichts an Schlitzen, Bohrungen oder Stegen. Kürzen geht dort um bis zu {2}.",
            MeasureText.Short(new MeasureValue(Quantity.Distance, near), Inches),
            MeasureText.Short(new MeasureValue(Quantity.Distance, far), Inches),
            MeasureText.Short(new MeasureValue(Quantity.Distance, (far - near) / 2), Inches));
    }

    [RelayCommand]
    private void CancelResize()
    {
        ResizeTarget = null;
        _resize = null;
        Redraw();
    }

    /// <summary>Normal (towards the moving side) and offset of the dividing plane, both along that normal.</summary>
    private (Vector3 Normal, double Offset)? ResizePlane()
    {
        if (_resize is not { } r) return null;
        var cut = (double)ResizeCut * UnitFactor;
        return ResizeFixedIndex == 0 ? (r.Normal, r.From + cut) : (-r.Normal, -(r.To - cut));
    }

    /// <summary>The dividing plane as a rectangle across the model (preview while the editor is open).</summary>
    internal Vector3[]? ResizeGuide => ResizePlane() is { } p
        ? SectionGeometry.Quad(Mesh.Bounds, new Vector4(p.Normal, (float)p.Offset))
        : null;

    [RelayCommand]
    private async Task ApplyResize()
    {
        if (_resize is not { } r || ResizePlane() is not { } plane || !CanEdit) return;
        var length = r.To - r.From;
        var delta = (double)ResizeNew * UnitFactor - length;
        var cut = (double)ResizeCut * UnitFactor;
        if (Math.Abs(delta) < 1e-9) { CancelResize(); return; }
        if (cut <= 0 || cut >= length)
        {
            EditMessage = Tr.T("Die Trennebene muss zwischen den beiden Flächen liegen.");
            return;
        }
        if (delta < 0 && cut - delta >= length)
        {
            EditMessage = Tr.T("So viel lässt sich hinter der Trennebene nicht wegnehmen: die Trennebene näher an die feste Fläche legen.");
            return;
        }
        var before = MeasureText.Short(new MeasureValue(Quantity.Distance, length), Inches);
        var after = MeasureText.Short(new MeasureValue(Quantity.Distance, length + delta), Inches);
        await RunEdit(Tr.T("Länge {0} → {1}", before, after), mesh => SolidEditor.ChangeLength(mesh, plane.Normal, plane.Offset, delta));
    }

    /// <summary>Computes an edit in the background; on success the old model goes onto the undo list.</summary>
    /// <returns>True if the model was changed.</returns>
    private async Task<bool> RunEdit(string what, Func<Mesh, Mesh> edit)
    {
        var mesh = Mesh;
        IsEditBusy = true;
        EditMessage = Tr.T("Wird berechnet …");
        try
        {
            var result = await Task.Run(() => edit(mesh));
            _undo.Add((mesh, what));
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear();
            ResizeTarget = null;
            _resize = null;
            ReplaceMesh(result);
            EditMessage = what;
            return true;
        }
        catch (EditException e)
        {
            EditMessage = e.Message;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Write("3D: edit failed", e.ToString());
            EditMessage = Tr.T("Die Änderung ließ sich nicht berechnen: {0}", e.Message);
        }
        finally
        {
            IsEditBusy = false;
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
        return false;
    }

    // ------------------------------------------------------------------ undo, redo, save

    private bool CanUndo() => _undo.Count > 0 && !IsEditBusy;

    private bool CanRedo() => _redo.Count > 0 && !IsEditBusy;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        var (mesh, what) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((Mesh, what));
        ReplaceMesh(mesh);
        EditMessage = Tr.T("Rückgängig: {0}", what);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        var (mesh, what) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((Mesh, what));
        ReplaceMesh(mesh);
        EditMessage = Tr.T("Wiederholt: {0}", what);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Suggested name for "Save as": the file name with "-bearbeitet".</summary>
    public string SuggestedSaveName => System.IO.Path.GetFileNameWithoutExtension(Path) + "-bearbeitet.stl";

    /// <summary>Writes the model as binary STL; the view then shows that file.</summary>
    public async Task SaveAsAsync(string path)
    {
        var mesh = Mesh;
        try
        {
            await Task.Run(() => StlWriter.Write(path, mesh, "ImageConverter STL"));
            Path = path;
            IsModified = false;
            EditMessage = Tr.T("Gespeichert: {0}", System.IO.Path.GetFileName(path));
            OnPropertyChanged(nameof(Path));
            OnPropertyChanged(nameof(SuggestedSaveName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            EditMessage = Tr.T("Speichern ging nicht: {0}", e.Message);
        }
    }

    /// <summary>
    /// Puts a new model in place of the old one: background analysis restarts, measurements (they refer to the old
    /// faces) are cleared, sections stay where they are in the model.
    /// </summary>
    private void ReplaceMesh(Mesh mesh)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _measureCts.Cancel();
        _measureCts = new CancellationTokenSource();
        Mesh = mesh;
        File = File with { Mesh = mesh, FileTriangles = mesh.TriangleCount, Invalid = 0, Degenerate = 0, Truncated = false };
        Scene = new ModelScene { Positions = mesh.Positions, FileIndices = mesh.Indices, TriangleCount = mesh.TriangleCount };
        Summary = null;
        Faces = null;
        Hierarchy = null;
        _measurer = null;
        _faceStart = null;
        _smoothRequested = false;
        _wireRequested = false;
        Measurements.Clear();
        _snapLines = null;
        Pending = null;
        _nextColour = 0;
        HoverFace = -1;
        IsModified = true;
        Progress = Tr.T("Flächen werden erkannt …");
        MeshReplaced?.Invoke();
        StartAnalysis();
        RefreshStatistics();
        foreach (var name in new[] { nameof(Mesh), nameof(HasMeasurements), nameof(NextColour), nameof(CanMeasure), nameof(MeasureHint), nameof(CanEdit), nameof(EditHint) })
            OnPropertyChanged(name);
        Redraw();
    }
}
