using System.Collections.ObjectModel;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;

namespace ImageConverter.Viewer3D;

/// <summary>A measurement in the list and on the model.</summary>
public sealed partial class MeasurementItem : ObservableObject
{
    public required Pick A { get; init; }
    public required Pick B { get; init; }

    /// <summary>Colour of its faces, line and label.</summary>
    public required Color Colour { get; init; }

    public IBrush Brush => new SolidColorBrush(Colour);

    /// <summary>For example "Ebene ↔ Zylinder".</summary>
    public required string Title { get; init; }

    /// <summary>Null while it is being computed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Lines), nameof(Label), nameof(SubLabel), nameof(IsComputing))]
    public partial Measurement? Result { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Lines), nameof(Label), nameof(SubLabel))]
    public partial bool Inches { get; set; }

    public bool IsComputing => Result is null;

    /// <summary>All values, one per line, for the list.</summary>
    public string Lines => Result is { } r ? string.Join("\n", r.Values.Select(v => MeasureText.Value(v, Inches))) : "…";

    /// <summary>Main value on the model.</summary>
    public string Label => Result is { } r ? MeasureText.Short(r.Values[0], Inches) : "…";

    /// <summary>Second line on the model: the next value or two (angle beside a distance, diameters …).</summary>
    public string SubLabel => Result is { } r ? string.Join("  ·  ", r.Values.Skip(1).Take(2).Select(v => MeasureText.Short(v, Inches))) : "";

    public required IRelayCommand DeleteCommand { get; init; }

    /// <summary>Texts again (other language).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(SubLabel));
    }
}

/// <summary>
/// State of one open STL model: the mesh and what is worked out about it in the background, the display options, the
/// selection and the measurements. The views (3D view, overlay, side column) bind to it.
/// </summary>
/// <remarks>
/// <para>Background steps, each published on the UI thread when done: neighbourhood (closed? parts, area, volume),
/// edges and ground shadow; then faces and the ray hierarchy in parallel (measuring and hover need them). The model is
/// on screen before any of them is finished.</para>
/// <para>Clicks while measuring: the first face (or, with Shift, point) is picked, a click on it again lets it go,
/// the second one completes a measurement, which stays; the next click starts the next pair.</para>
/// </remarks>
public sealed partial class ModelViewModel : ObservableObject, IDisposable
{
    private static readonly Color[] Palette =
    [
        Color.Parse("#3D7BEA"), Color.Parse("#E07B2E"), Color.Parse("#2FA36B"), Color.Parse("#9B59D0"),
        Color.Parse("#D04A5A"), Color.Parse("#1F9DB0"),
    ];

    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _measureCts = new();
    private int _nextColour;

    private ModelViewModel(string path, StlFile file)
    {
        Path = path;
        File = file;
        Mesh = file.Mesh;
        Scene = new ModelScene { Positions = Mesh.Positions, FileIndices = Mesh.Indices, TriangleCount = Mesh.TriangleCount };

        var settings = ViewerSettings.Load();
        _modeIndex = settings.Mode;
        _showEdges = settings.Edges;
        _showParts = settings.Parts;
        _orthographic = settings.Orthographic;
        _pivotAtCentre = settings.PivotAtCentre;
        _inches = settings.Inches;
        Tr.Instance.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>Reads the file in the background and starts working out the rest.</summary>
    /// <exception cref="StlFormatException">Not readable as STL; the message is for the user.</exception>
    public static async Task<ModelViewModel> OpenAsync(string path, CancellationToken ct)
    {
        var file = await Task.Run(() => StlReader.Read(path, ct), ct);
        var vm = new ModelViewModel(path, file);
        vm.StartAnalysis();
        return vm;
    }

    public string Path { get; }

    public StlFile File { get; }

    public Mesh Mesh { get; }

    internal ModelScene Scene { get; }

    /// <summary>Closed? parts, area, volume – null until the neighbourhood is worked out.</summary>
    public MeshSummary? Summary { get; private set; }

    public FaceSet? Faces { get; private set; }

    public Bvh? Hierarchy { get; private set; }

    private Measurer? _measurer;
    private int[]? _faceStart;

    /// <summary>Something that is drawn changed: the view draws a new frame.</summary>
    public event Action? Changed;

    private void Redraw() => Changed?.Invoke();

    // ------------------------------------------------------------------ background analysis

    [ObservableProperty] public partial string? Progress { get; set; } = Tr.T("Flächen werden erkannt …");

    private void StartAnalysis()
    {
        EnsureWireframe();
        var ct = _cts.Token;
        var mesh = Mesh;
        _ = Task.Run(async () =>
        {
            try
            {
                var topology = MeshTopology.Build(mesh, ct);
                var edges = SceneData.SharpEdges(mesh, topology, ct);
                var parts = SceneData.PartOfVertex(mesh, topology);
                var shadow = SceneData.Shadow(mesh, ct: ct);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    Summary = topology.Summary;
                    Scene.SharpEdges = edges;
                    Scene.SharpEdgesVersion++;
                    Scene.Parts = parts;
                    Scene.ExtrasVersion++;
                    Scene.Shadow = shadow;
                    Scene.ShadowVersion++;
                    RefreshStatistics();
                    Redraw();
                });

                var resolution = File.Resolution;
                var facesTask = Task.Run(() => FaceFinder.Find(mesh, topology, ct, resolution), ct);
                var bvh = Bvh.Build(mesh, null, ct);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ct.IsCancellationRequested) Hierarchy = bvh;
                });
                var faces = await facesTask;
                topology = null!;   // neighbours and parts (16 bytes per triangle) are not needed any more
                var (sorted, start) = SceneData.SortedByFace(mesh, faces);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    Faces = faces;
                    _faceStart = start;
                    _measurer = new Measurer(mesh, faces);
                    int[]? first = sorted;
                    // The sorted copy goes to the GPU once and is not kept; a new context sorts again
                    Scene.SortedIndices = () =>
                    {
                        var a = first ?? SceneData.SortedByFace(mesh, faces).Indices;
                        first = null;
                        return a;
                    };
                    Scene.IndicesVersion++;
                    Progress = null;
                    OnPropertyChanged(nameof(CanMeasure));
                    OnPropertyChanged(nameof(MeasureHint));
                    RefreshStatistics();
                    Redraw();
                });
                sorted = null!;
            }
            catch (OperationCanceledException)
            {
                // closed meanwhile
            }
            catch (Exception e)
            {
                AppLog.Write("3D: analysis failed", e.ToString());
                await Dispatcher.UIThread.InvokeAsync(() => Progress = Tr.T("Flächen ließen sich nicht erkennen: {0}", e.Message));
            }
        }, ct);
    }

    private bool _wireRequested;

    /// <summary>All edges for the wireframe, computed once in the background when it is first shown.</summary>
    private void EnsureWireframe()
    {
        if (Mode != RenderMode.Wireframe || _wireRequested) return;
        _wireRequested = true;
        var ct = _cts.Token;
        var mesh = Mesh;
        _ = Task.Run(() => SceneData.AllEdges(mesh, ct), ct).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !ct.IsCancellationRequested)
            {
                Scene.AllEdges = t.Result;
                Redraw();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ------------------------------------------------------------------ figures for the side column

    public string Format => File.Encoding == StlEncoding.Binary ? Tr.T("STL (binär)") : Tr.T("STL (Text)");

    public string Size
    {
        get
        {
            var s = Mesh.Bounds.Size;
            return $"{Num(s.X)} × {Num(s.Y)} × {Num(s.Z)} {(Inches ? "in" : "mm")}";

            string Num(float v) => Inches
                ? (v / MeasureText.MillimetresPerInch).ToString("N3", System.Globalization.CultureInfo.CurrentCulture)
                : v.ToString("N2", System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    public string Triangles => Mesh.TriangleCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public string Vertices => Mesh.VertexCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public string SurfaceArea => Summary is { } t ? MeasureText.Area(t.Area, Inches) : "…";

    public string Volume => Summary is { } t
        ? t.IsClosed ? MeasureText.Volume(t.Volume, Inches) : "≈ " + MeasureText.Volume(t.Volume, Inches)
        : "…";

    public string Closed => Summary is not { } t ? "…"
        : t.IsClosed ? Tr.T("ja")
        : t.NonManifoldEdges > 0 ? Tr.T("nein – {0:N0} offene, {1:N0} mehrfach genutzte Kanten", t.OpenEdges, t.NonManifoldEdges)
        : Tr.T("nein – {0:N0} offene Kanten", t.OpenEdges);

    public string Parts => Summary is { } t ? t.ShellCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "…";

    public string FaceCount => Faces is { } f
        ? Tr.T("{0:N0} ({1:N0} Ebenen, {2:N0} Zylinder)", f.Faces.Length, f.Faces.Count(x => x.Kind == FaceKind.Plane), f.Faces.Count(x => x.Kind == FaceKind.Cylinder))
        : "…";

    public string Header => File.Header;

    public bool HasHeader => File.Header.Length > 0;

    /// <summary>What was wrong with the file, or null.</summary>
    public string? Warning
    {
        get
        {
            var parts = new List<string>();
            if (File.Truncated) parts.Add(Tr.T("Die Datei ist unvollständig: {0:N0} von {1:N0} Dreiecken gelesen.", Mesh.TriangleCount, File.FileTriangles));
            if (File.Invalid > 0) parts.Add(Tr.T("{0:N0} Dreiecke mit ungültigen Zahlen übersprungen.", File.Invalid));
            if (File.Degenerate > 0) parts.Add(Tr.T("{0:N0} Dreiecke ohne Fläche übersprungen.", File.Degenerate));
            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }
    }

    /// <summary>Line under the file name in the header bar.</summary>
    public string Details
    {
        get
        {
            var s = Mesh.Bounds.Size;
            var bytes = new FileInfo(Path) is { Exists: true } fi ? fi.Length : 0;
            var size = bytes >= 1 << 20 ? $"{bytes / 1048576.0:F1} MB" : $"{bytes / 1024.0:F0} KB";
            return Tr.T("{0} × {1} × {2} mm  ·  {3:N0} Dreiecke  ·  {4}  ·  STL",
                s.X.ToString("0.##"), s.Y.ToString("0.##"), s.Z.ToString("0.##"), Mesh.TriangleCount, size);
        }
    }

    private void RefreshStatistics()
    {
        foreach (var name in new[] { nameof(Size), nameof(SurfaceArea), nameof(Volume), nameof(Closed), nameof(Parts), nameof(FaceCount), nameof(Format), nameof(Warning) })
            OnPropertyChanged(name);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RefreshStatistics();
        OnPropertyChanged(nameof(MeasureHint));
        OnPropertyChanged(nameof(PendingText));
        foreach (var m in Measurements) m.Refresh();
    }

    // ------------------------------------------------------------------ display options

    /// <summary>0 = solid, 1 = wireframe, 2 = blueprint.</summary>
    public int ModeIndex
    {
        get => _modeIndex;
        set
        {
            if (!SetProperty(ref _modeIndex, Math.Clamp(value, 0, 2))) return;
            ViewerSettings.Update(s => s with { Mode = _modeIndex });
            EnsureWireframe();
            Redraw();
        }
    }

    private int _modeIndex;

    public RenderMode Mode => (RenderMode)_modeIndex;

    public bool ShowEdges
    {
        get => _showEdges;
        set
        {
            if (!SetProperty(ref _showEdges, value)) return;
            ViewerSettings.Update(s => s with { Edges = value });
            Redraw();
        }
    }

    private bool _showEdges;

    public bool ShowParts
    {
        get => _showParts;
        set
        {
            if (!SetProperty(ref _showParts, value)) return;
            ViewerSettings.Update(s => s with { Parts = value });
            Redraw();
        }
    }

    private bool _showParts;

    public bool Orthographic
    {
        get => _orthographic;
        set
        {
            if (!SetProperty(ref _orthographic, value)) return;
            OnPropertyChanged(nameof(ProjectionIndex));
            ViewerSettings.Update(s => s with { Orthographic = value });
            Redraw();
        }
    }

    private bool _orthographic;

    /// <summary>0 = orthographic, 1 = perspective (for the list in the side column).</summary>
    public int ProjectionIndex
    {
        get => Orthographic ? 0 : 1;
        set => Orthographic = value == 0;
    }

    /// <summary>Turn around the vertical centre axis of the model instead of the point under the mouse.</summary>
    public bool PivotAtCentre
    {
        get => _pivotAtCentre;
        set
        {
            if (!SetProperty(ref _pivotAtCentre, value)) return;
            OnPropertyChanged(nameof(PivotIndex));
            ViewerSettings.Update(s => s with { PivotAtCentre = value });
        }
    }

    private bool _pivotAtCentre;

    /// <summary>0 = point under the mouse, 1 = centre axis of the model.</summary>
    public int PivotIndex
    {
        get => PivotAtCentre ? 1 : 0;
        set => PivotAtCentre = value == 1;
    }

    public bool Inches
    {
        get => _inches;
        set
        {
            if (!SetProperty(ref _inches, value)) return;
            OnPropertyChanged(nameof(UnitIndex));
            ViewerSettings.Update(s => s with { Inches = value });
            foreach (var m in Measurements) m.Inches = value;
            RefreshStatistics();
            OnPropertyChanged(nameof(PendingText));
            OnPropertyChanged(nameof(SectionText));
            OnPropertyChanged(nameof(SectionDepthInUnit));
            OnPropertyChanged(nameof(UnitLabel));
            Redraw();
        }
    }

    private bool _inches;

    /// <summary>0 = millimetres, 1 = inches.</summary>
    public int UnitIndex
    {
        get => Inches ? 1 : 0;
        set => Inches = value == 1;
    }

    // ------------------------------------------------------------------ view requests (handled by the 3D view)

    /// <summary>The view should turn to a standard view; null = fit only.</summary>
    public event Action<StandardView?>? ViewRequested;

    [RelayCommand]
    private void ShowView(StandardView view) => ViewRequested?.Invoke(view);

    [RelayCommand]
    private void Fit() => ViewRequested?.Invoke(null);

    /// <summary>The view should bring the model into the middle, keeping zoom and direction.</summary>
    public event Action? CentreRequested;

    [RelayCommand]
    private void Centre() => CentreRequested?.Invoke();

    // ------------------------------------------------------------------ measuring

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureHint))]
    public partial bool MeasureTool { get; set; }

    partial void OnMeasureToolChanged(bool value)
    {
        if (value)
        {
            PlaceSectionTool = false;
            MoveSectionTool = false;
        }
        else
        {
            Pending = null;
            HoverFace = -1;
        }
        Redraw();
    }

    /// <summary>Faces are known: measuring between faces works (points work as soon as the hierarchy is there).</summary>
    public bool CanMeasure => Faces is not null;

    public string MeasureHint => !MeasureTool
        ? Tr.T("Messen einschalten, dann zwei Flächen anklicken. Umschalt + Klick misst zwischen Punkten.")
        : Faces is null ? Tr.T("Flächen werden erkannt …")
        : Pending is null ? Tr.T("Erste Fläche anklicken (Umschalt: Punkt).")
        : Tr.T("Zweite Fläche anklicken – oder die erste noch einmal, um sie abzuwählen.");

    public ObservableCollection<MeasurementItem> Measurements { get; } = [];

    public bool HasMeasurements => Measurements.Count > 0;

    /// <summary>The first pick of the pair being made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureHint), nameof(PendingText), nameof(HasPending))]
    public partial Pick? Pending { get; set; }

    public bool HasPending => Pending is not null;

    /// <summary>What the first pick is, with its diameter for cylinders.</summary>
    public string PendingText
    {
        get
        {
            if (Pending is not { } p) return "";
            var single = PendingValues();
            var kind = MeasureText.Kind(p, Faces);
            return single.Count > 0 ? kind + "  " + MeasureText.Short(single[0], Inches) : kind;
        }
    }

    /// <summary>Values shown at the first pick itself (diameter of a cylinder).</summary>
    public IReadOnlyList<MeasureValue> PendingValues() =>
        Pending is { } p && _measurer is { } m ? m.Single(p) : [];

    /// <summary>Colour the next measurement gets (also of the first pick while it is open).</summary>
    public Color NextColour => Palette[_nextColour % Palette.Length];

    /// <summary>Face under the mouse while measuring, −1 for none.</summary>
    [ObservableProperty] public partial int HoverFace { get; set; } = -1;

    partial void OnHoverFaceChanged(int value)
    {
        OnPropertyChanged(nameof(HoverText));
        Redraw();
    }

    /// <summary>What lies under the mouse while measuring (for the status line).</summary>
    public string HoverText
    {
        get
        {
            if (HoverFace < 0 || Faces is not { } faces) return "";
            var f = faces.Faces[HoverFace];
            var area = MeasureText.Area(f.Area, Inches);
            return f.Kind switch
            {
                FaceKind.Plane => Tr.T("Ebene · {0}", area),
                FaceKind.Cylinder => f.Coverage >= 180
                    ? Tr.T("Zylinder · Ø {0}", MeasureText.Length(2 * f.Radius, Inches))
                    : Tr.T("Rundung · R {0}", MeasureText.Length(f.Radius, Inches)),
                _ => Tr.T("Freiform · {0}", area),
            };
        }
    }

    // ------------------------------------------------------------------ section

    /// <summary>
    /// Section: a plane that cuts away everything in front of it, as (normal, distance) – points with
    /// <c>dot(p, normal) &lt; distance</c> are hidden. It is set parallel to the screen (by zooming through a wall or with
    /// Shift + wheel) and then stays fixed in the model, so that what lies behind can be looked at from any side; null =
    /// no section.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSection), nameof(SectionText), nameof(SectionDepth), nameof(SectionDepthInUnit))]
    public partial Vector4? Section { get; set; }

    partial void OnSectionChanged(Vector4? value)
    {
        if (value is null) MoveSectionTool = false;
        Redraw();
    }

    /// <summary>„Schnittfläche anlegen“: the next click on a face puts a section plane onto it.</summary>
    [ObservableProperty] public partial bool PlaceSectionTool { get; set; }

    partial void OnPlaceSectionToolChanged(bool value)
    {
        if (value)
        {
            MeasureTool = false;
            MoveSectionTool = false;
        }
        OnPropertyChanged(nameof(SectionHint));
        Redraw();
    }

    /// <summary>
    /// „Schnittfläche verschieben“: dragging the plane (or the wheel over it) moves it. Only switched on by its button; a
    /// click beside the plane switches it off again, so that clicks go to faces otherwise.
    /// </summary>
    [ObservableProperty] public partial bool MoveSectionTool { get; set; }

    partial void OnMoveSectionToolChanged(bool value)
    {
        if (value && Section is null)
        {
            MoveSectionTool = false;
            return;
        }
        if (value)
        {
            MeasureTool = false;
            PlaceSectionTool = false;
        }
        OnPropertyChanged(nameof(SectionHint));
        Redraw();
    }

    /// <summary>What to do next with the section tools, for the status line; empty when none is active.</summary>
    public string SectionHint =>
        PlaceSectionTool ? Tr.T("Fläche anklicken, auf die die Schnittfläche gelegt wird (Esc bricht ab).")
        : MoveSectionTool ? Tr.T("Schnittfläche ziehen oder Mausrad über ihr; Klick daneben beendet das Verschieben.")
        : "";

    /// <summary>Unit of the number fields: mm or in.</summary>
    public string UnitLabel => Inches ? "in" : "mm";

    /// <summary>Depth of the section in the chosen unit, for the number field (0.1 steps).</summary>
    public double SectionDepthInUnit
    {
        get => Math.Round(SectionDepth / (Inches ? MeasureText.MillimetresPerInch : 1), 4);
        set => SetSectionDepth((float)(value * (Inches ? MeasureText.MillimetresPerInch : 1)));
    }

    /// <summary>Puts the section at a depth behind the front of the model (along its normal).</summary>
    public void SetSectionDepth(float depth)
    {
        if (Section is not { } c) return;
        var n = Normal(c);
        var (front, back) = Mesh.Bounds.Extent(n);
        Section = new Vector4(n, Math.Clamp(front + depth, front, back));
    }

    /// <summary>Moves the section to a plane distance (dragging it), within the model.</summary>
    public void SetSectionDistance(float w)
    {
        if (Section is not { } c) return;
        var n = Normal(c);
        var (front, back) = Mesh.Bounds.Extent(n);
        Section = new Vector4(n, Math.Clamp(w, front, back));
    }

    /// <summary>Puts a new section plane onto a clicked face; its normal points into the material.</summary>
    public void PlaceSection(D3 point, int face, Vector3 triangleNormal)
    {
        var outward = Faces is { } f && face >= 0 && f.Faces[face].Kind == FaceKind.Plane ? f.Faces[face].Normal.ToVector3() : triangleNormal;
        Section = SectionGeometry.OnFace(point.ToVector3(), outward);
        PlaceSectionTool = false;
    }

    public bool HasSection => Section is not null;

    /// <summary>How far behind the front of the model (along the section's normal) it cuts, in model units.</summary>
    public float SectionDepth => Section is { } c ? c.W - Mesh.Bounds.Extent(Normal(c)).Front : 0;

    public string SectionText => Tr.T("Schnitt {0} tief", MeasureText.Length(SectionDepth, Inches));

    private static Vector3 Normal(Vector4 c) => new(c.X, c.Y, c.Z);

    /// <summary>Moves the section by <paramref name="delta"/> (Shift + wheel; positive = deeper); without one, starts one facing <paramref name="viewDirection"/>.</summary>
    public void MoveSection(float delta, Vector3 viewDirection)
    {
        var n = Section is { } c ? Normal(c) : viewDirection;
        var (front, back) = Mesh.Bounds.Extent(n);
        var w = Math.Clamp((Section?.W ?? front) + delta, front, back);
        // A new one only once it cuts something; an existing one stays, also at the front (placed planes start there)
        Section = Section is null && w <= front ? null : new Vector4(n, w);
    }

    /// <summary>Zooming through a wall: the section goes just behind <paramref name="exit"/> (only deeper, never back).</summary>
    public void PeelTo(Vector3 exit, Vector3 viewDirection)
    {
        var n = Section is { } c ? Normal(c) : viewDirection;
        var w = Vector3.Dot(exit, n) + 1e-4f * Mesh.Bounds.Diagonal;
        if (Section is { } old && w <= old.W) return;
        Section = new Vector4(n, w);
    }

    /// <summary>Where the ray under the mouse leaves the next wall (behind the section), or null.</summary>
    public Vector3? ExitOfNextWall(Vector3 origin, Vector3 direction, Vector4? clip)
    {
        if (Hierarchy is not { } bvh) return null;
        if (clip is { } c)
        {
            var n = new Vector3(c.X, c.Y, c.Z);
            var along = Vector3.Dot(direction, n);
            if (along > 1e-9f)
            {
                var t = (c.W - Vector3.Dot(origin, n)) / along;
                if (t > 0) origin += direction * t;
            }
        }
        return Walls.ExitOfNextWall(Mesh, bvh, origin, direction);
    }

    [RelayCommand]
    private void ClearSection()
    {
        PlaceSectionTool = false;
        Section = null;
    }

    /// <summary>The surface point along a ray, with its face (−1 while faces are not known).</summary>
    /// <param name="origin">Start of the ray.</param>
    /// <param name="direction">Direction of the ray.</param>
    /// <param name="clip">Section plane (normal, distance): the ray starts where it crosses it, so that cut-away parts are not hit.</param>
    public (D3 Point, int Face)? HitTest(Vector3 origin, Vector3 direction, Vector4? clip = null)
    {
        if (clip is { } c)
        {
            var n = new Vector3(c.X, c.Y, c.Z);
            var along = Vector3.Dot(direction, n);
            if (along > 1e-9f)
            {
                var t = (c.W - Vector3.Dot(origin, n)) / along;
                if (t > 0) origin += direction * t;
            }
        }
        if (Hierarchy is not { } bvh || !bvh.Raycast(origin, direction, float.MaxValue, out var hit)) return null;
        var p = origin + direction * hit.Distance;
        return (D3.From(p), Faces is { } f ? f.FaceOf[hit.Triangle] : -1);
    }

    /// <summary>Like <see cref="HitTest"/>, with the outward normal of the triangle hit.</summary>
    public (D3 Point, int Face, Vector3 Normal)? HitTestWithNormal(Vector3 origin, Vector3 direction, Vector4? clip = null)
    {
        if (clip is { } c)
        {
            var n = new Vector3(c.X, c.Y, c.Z);
            var along = Vector3.Dot(direction, n);
            if (along > 1e-9f)
            {
                var t = (c.W - Vector3.Dot(origin, n)) / along;
                if (t > 0) origin += direction * t;
            }
        }
        if (Hierarchy is not { } bvh || !bvh.Raycast(origin, direction, float.MaxValue, out var hit)) return null;
        var normal = Vector3.Normalize(Mesh.AreaNormal(hit.Triangle).ToVector3());
        return (D3.From(origin + direction * hit.Distance), Faces is { } f ? f.FaceOf[hit.Triangle] : -1, normal);
    }

    /// <summary>The mouse moved over the model while measuring.</summary>
    public void Hover(Vector3 origin, Vector3 direction, Vector4? clip = null)
    {
        if (!MeasureTool) return;
        HoverFace = HitTest(origin, direction, clip) is { Face: >= 0 } h ? h.Face : -1;
    }

    /// <summary>A click while measuring: picks, lets go of the first pick or completes a measurement.</summary>
    /// <param name="origin">Start of the ray under the mouse.</param>
    /// <param name="direction">Its direction.</param>
    /// <param name="point">Shift held: a point instead of a face.</param>
    /// <param name="sameSpot">Whether a world point lies under the mouse within a few pixels of another (for letting go of a point).</param>
    /// <param name="clip">Section plane, see <see cref="HitTest"/>.</param>
    public void Click(Vector3 origin, Vector3 direction, bool point, Func<D3, D3, bool> sameSpot, Vector4? clip = null)
    {
        if (!MeasureTool || HitTest(origin, direction, clip) is not { } hit) return;
        if (!point && hit.Face < 0) return;   // faces not known yet
        var pick = new Pick(point ? PickKind.Point : PickKind.Face, hit.Face, hit.Point);

        if (Pending is { } a)
        {
            var same = a.Kind == PickKind.Face && pick.Kind == PickKind.Face && a.Face == pick.Face
                       || a.Kind == PickKind.Point && pick.Kind == PickKind.Point && sameSpot(a.Point, pick.Point);
            Pending = null;
            if (!same) AddMeasurement(a, pick);
        }
        else
        {
            Pending = pick;
        }
        Redraw();
    }

    /// <summary>Esc: lets go of the first pick; true if there was one.</summary>
    public bool CancelPending()
    {
        if (Pending is null) return false;
        Pending = null;
        Redraw();
        return true;
    }

    private void AddMeasurement(Pick a, Pick b)
    {
        if (_measurer is null && (a.Kind == PickKind.Face || b.Kind == PickKind.Face)) return;
        var colour = Palette[_nextColour++ % Palette.Length];
        MeasurementItem? item = null;
        item = new MeasurementItem
        {
            A = a,
            B = b,
            Colour = colour,
            Title = MeasureText.Kind(a, Faces) + " ↔ " + MeasureText.Kind(b, Faces),
            Inches = Inches,
            DeleteCommand = new RelayCommand(() => Remove(item!)),
        };
        Measurements.Add(item);
        OnPropertyChanged(nameof(HasMeasurements));
        OnPropertyChanged(nameof(NextColour));

        var m = _measurer ?? new Measurer(Mesh, Faces ?? new FaceSet([], [], []));
        var ct = _measureCts.Token;
        _ = Task.Run(() => m.Measure(a, b, ct), ct).ContinueWith(t =>
        {
            if (t.IsCanceled || ct.IsCancellationRequested) return;
            if (t.Exception is { } e)
            {
                AppLog.Write("3D: measuring failed", e.ToString());
                Remove(item);
                return;
            }
            item.Result = t.Result;
            Redraw();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Removes a measurement (✕ in the list, click on its label).</summary>
    public void Remove(MeasurementItem item)
    {
        if (!Measurements.Remove(item)) return;
        OnPropertyChanged(nameof(HasMeasurements));
        Redraw();
    }

    [RelayCommand]
    private void ClearMeasurements()
    {
        Measurements.Clear();
        Pending = null;
        _nextColour = 0;
        OnPropertyChanged(nameof(HasMeasurements));
        OnPropertyChanged(nameof(NextColour));
        Redraw();
    }

    /// <summary>Faces drawn in colour: faces of the measurements lightly, the open pick and the face under the mouse stronger.</summary>
    internal IReadOnlyList<Highlight> Highlights()
    {
        if (_faceStart is not { } start) return [];
        var list = new List<Highlight>();
        foreach (var m in Measurements)
        {
            Add(m.A, m.Colour, 0.30f);
            Add(m.B, m.Colour, 0.30f);
        }
        if (Pending is { } p) Add(p, NextColour, 0.55f);
        if (MeasureTool && HoverFace >= 0 && (Pending is not { Kind: PickKind.Face } q || q.Face != HoverFace))
            list.Add(Range(HoverFace, NextColour, 0.22f));
        return list;

        void Add(Pick pick, Color c, float strength)
        {
            if (pick.Kind == PickKind.Face && pick.Face >= 0) list.Add(Range(pick.Face, c, strength));
        }

        Highlight Range(int face, Color c, float strength) =>
            new(start[face], start[face + 1] - start[face], new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, strength));
    }

    public void Dispose()
    {
        Tr.Instance.PropertyChanged -= OnLanguageChanged;
        _cts.Cancel();
        _measureCts.Cancel();
        Changed = null;
        ViewRequested = null;
        CentreRequested = null;
    }
}
