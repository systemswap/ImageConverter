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

    /// <summary>Between two parallel planes: the length can be changed ("Länge ändern").</summary>
    [ObservableProperty] public partial bool CanResize { get; set; }

    public IRelayCommand? ResizeCommand { get; init; }

    /// <summary>Texts again (other language).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(SubLabel));
    }
}

/// <summary>A section plane in the list.</summary>
public sealed partial class SectionItem : ObservableObject
{
    private readonly ModelViewModel _owner;

    internal SectionItem(ModelViewModel owner, Vector4 plane, int number)
    {
        _owner = owner;
        _plane = plane;
        Number = number;
        SelectCommand = new RelayCommand(() => _owner.SelectedSection = this);
        DeleteCommand = new RelayCommand(() => _owner.RemoveSection(this));
    }

    public int Number { get; }

    public string Name => Tr.T("Schnitt {0}", Number);

    /// <summary>(Unit normal into the material, distance): points with <c>dot(p, normal) &lt; distance</c> are cut away.</summary>
    public Vector4 Plane => _plane;

    private Vector4 _plane;

    public Vector3 Normal => new(_plane.X, _plane.Y, _plane.Z);

    /// <summary>Distance of the plane, kept within the model's extent along its normal.</summary>
    public float Distance
    {
        get => _plane.W;
        set
        {
            var (front, back) = _owner.Mesh.Bounds.Extent(Normal);
            var w = Math.Clamp(value, front, back);
            if (w == _plane.W) return;
            _plane = new Vector4(Normal, w);
            OnPropertyChanged(nameof(Plane));
            OnPropertyChanged(nameof(Depth));
            OnPropertyChanged(nameof(DepthInUnit));
            _owner.SectionMoved(this);
        }
    }

    /// <summary>How far behind the front of the model (along the normal) it cuts, in model units.</summary>
    public float Depth => _plane.W - _owner.Mesh.Bounds.Extent(Normal).Front;

    /// <summary>Depth in the chosen unit (mm or inches), for the number fields.</summary>
    public double DepthInUnit
    {
        get => Math.Round(Depth / _owner.UnitFactor, 4);
        set => Distance = _owner.Mesh.Bounds.Extent(Normal).Front + (float)(value * _owner.UnitFactor);
    }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Which way it faces: „⊥ X“, „⊥ Y“, „⊥ Z“ when it lies perpendicular to an axis (within 1°), otherwise „schräg“.</summary>
    public string Direction
    {
        get
        {
            var n = Normal;
            const float aligned = 0.99985f;   // cos 1°
            return Math.Abs(n.X) >= aligned ? "⊥ X" : Math.Abs(n.Y) >= aligned ? "⊥ Y" : Math.Abs(n.Z) >= aligned ? "⊥ Z" : Tr.T("schräg");
        }
    }

    /// <summary>Unit of the depth: mm or in.</summary>
    public string UnitLabel => _owner.UnitLabel;

    public IRelayCommand SelectCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    internal void UnitChanged()
    {
        OnPropertyChanged(nameof(DepthInUnit));
        OnPropertyChanged(nameof(UnitLabel));
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

    private CancellationTokenSource _cts = new();
    private CancellationTokenSource _measureCts = new();
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
        _brightness = settings.Brightness;
        _qualityIndex = settings.Quality;
        Tr.Instance.PropertyChanged += OnLanguageChanged;
        InitEditing();
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

    /// <summary>The file shown; after "Save as" the new one.</summary>
    public string Path { get; private set; }

    public StlFile File { get; private set; }

    /// <summary>The model; replaced as a whole by an edit (see <see cref="ReplaceMesh"/>).</summary>
    public Mesh Mesh { get; private set; }

    internal ModelScene Scene { get; private set; }

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
                    OnPropertyChanged(nameof(CanEdit));
                    OnPropertyChanged(nameof(EditHint));
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
                    EnsureSmooth();
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

    /// <summary>Brightness of the lighting in percent; the background stays as it is.</summary>
    public int Brightness
    {
        get => _brightness;
        set
        {
            value = ViewerSettings.ClampBrightness(value);
            if (!SetProperty(ref _brightness, value)) return;
            OnPropertyChanged(nameof(BrightnessLabel));
            ViewerSettings.Update(s => s with { Brightness = value });
            Redraw();
        }
    }

    private int _brightness = 100;

    public string BrightnessLabel => $"{_brightness} %";

    /// <summary>Drawing quality: 0 fast (as plain as possible), 1 medium (all refinements), 2 high (and 8× smoothing).</summary>
    public int QualityIndex
    {
        get => _qualityIndex;
        set
        {
            value = Math.Clamp(value, 0, 2);
            if (!SetProperty(ref _qualityIndex, value)) return;
            ViewerSettings.Update(s => s with { Quality = value });
            EnsureSmooth();
            Redraw();
        }
    }

    private int _qualityIndex = 2;

    internal RenderQuality Quality => RenderQuality.ForLevel(_qualityIndex);

    private bool _smoothRequested;

    /// <summary>
    /// Smooth normals of the curved faces, worked out once in the background as soon as the faces are known and a
    /// quality above "fast" is chosen (fast costs nothing extra, also when loading large models).
    /// </summary>
    private void EnsureSmooth()
    {
        if (_qualityIndex == 0 || _smoothRequested || Faces is not { } faces) return;
        _smoothRequested = true;
        var ct = _cts.Token;
        var mesh = Mesh;
        _ = Task.Run(() => SmoothShading.Build(mesh, faces, ct), ct).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || ct.IsCancellationRequested) return;
            var smooth = t.Result;
            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                Scene.Smooth = smooth;
                Scene.SmoothVersion++;
                // The triangles now point at the added vertices of the curved faces
                Scene.SortedIndices = () => smooth.SortedByFace(faces);
                Scene.IndicesVersion++;
                Redraw();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Puts the brightness back to 100 %.</summary>
    [RelayCommand]
    private void ResetBrightness() => Brightness = 100;

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
            OnPropertyChanged(nameof(UnitLabel));
            foreach (var x in Sections) x.UnitChanged();
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

    // ------------------------------------------------------------------ sections

    /// <summary>Most section planes at once.</summary>
    public const int MaxSections = 6;

    /// <summary>
    /// Section planes: each cuts away everything in front of it, so the model shows only what lies behind all of them
    /// (two planes free a corner, for example). They are laid onto a face or set parallel to the screen and then stay
    /// fixed in the model. The selected one is moved by dragging, the wheel and the number field.
    /// </summary>
    public ObservableCollection<SectionItem> Sections { get; } = [];

    /// <summary>The section moved by the tools and shown in the number field; null if there is none.</summary>
    [ObservableProperty]
    public partial SectionItem? SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(SectionItem? oldValue, SectionItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (newValue is null) MoveSectionTool = false;
        Redraw();
    }

    public bool HasSection => Sections.Count > 0;

    public bool CanAddSection => Sections.Count < MaxSections;

    /// <summary>The planes as (normal, distance), for drawing and hit tests.</summary>
    public IReadOnlyList<Vector4> SectionPlanes => Sections.Select(x => x.Plane).ToArray();

    /// <summary>„Schnittfläche anlegen“: the next click on a face puts a further section plane onto it.</summary>
    [ObservableProperty] public partial bool PlaceSectionTool { get; set; }

    partial void OnPlaceSectionToolChanged(bool value)
    {
        if (value && !CanAddSection)
        {
            PlaceSectionTool = false;
            return;
        }
        if (value)
        {
            MeasureTool = false;
            MoveSectionTool = false;
        }
        OnPropertyChanged(nameof(SectionHint));
        Redraw();
    }

    /// <summary>
    /// „Schnittfläche verschieben“: dragging a plane (or the wheel over it) moves it, and selects it. Only switched on by its
    /// button; a click beside all planes switches it off again, so that clicks go to faces otherwise.
    /// </summary>
    [ObservableProperty] public partial bool MoveSectionTool { get; set; }

    partial void OnMoveSectionToolChanged(bool value)
    {
        if (value && Sections.Count == 0)
        {
            MoveSectionTool = false;
            return;
        }
        if (value)
        {
            MeasureTool = false;
            PlaceSectionTool = false;
            SelectedSection ??= Sections[^1];
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

    internal float UnitFactor => Inches ? (float)MeasureText.MillimetresPerInch : 1;

    /// <summary>Puts a new section plane onto a clicked face (normal into the material) and selects it.</summary>
    public void PlaceSection(D3 point, int face, Vector3 triangleNormal)
    {
        var outward = Faces is { } f && face >= 0 && f.Faces[face].Kind == FaceKind.Plane ? f.Faces[face].Normal.ToVector3() : triangleNormal;
        AddSection(SectionGeometry.OnFace(point.ToVector3(), outward));
        PlaceSectionTool = false;
    }

    /// <summary>Moves the selected section by <paramref name="delta"/> (Shift + wheel; positive = deeper); without one, starts one facing <paramref name="viewDirection"/>.</summary>
    public void MoveSection(float delta, Vector3 viewDirection)
    {
        if (SelectedSection is { } s)
        {
            s.Distance = s.Plane.W + delta;
            return;
        }
        if (delta <= 0 || !CanAddSection) return;
        var (front, _) = Mesh.Bounds.Extent(viewDirection);
        AddSection(new Vector4(viewDirection, front + delta));
    }

    /// <summary>Zooming through a wall: the selected section goes just behind <paramref name="exit"/> (only deeper), or a new one starts there.</summary>
    public void PeelTo(Vector3 exit, Vector3 viewDirection)
    {
        if (SelectedSection is { } s)
        {
            var n = s.Normal;
            var w = Vector3.Dot(exit, n) + 1e-4f * Mesh.Bounds.Diagonal;
            if (w > s.Plane.W) s.Distance = w;
            return;
        }
        if (!CanAddSection) return;
        AddSection(new Vector4(viewDirection, Vector3.Dot(exit, viewDirection) + 1e-4f * Mesh.Bounds.Diagonal));
    }

    /// <summary>Moves the selected section to a plane distance (dragging it).</summary>
    public void SetSectionDistance(float w)
    {
        if (SelectedSection is { } s) s.Distance = w;
    }

    private int _sectionNumber;

    private void AddSection(Vector4 plane)
    {
        var item = new SectionItem(this, plane, ++_sectionNumber);
        Sections.Add(item);
        SelectedSection = item;
        SectionsChanged();
    }

    /// <summary>Removes a section (✕ in the list).</summary>
    public void RemoveSection(SectionItem item)
    {
        if (!Sections.Remove(item)) return;
        if (SelectedSection == item) SelectedSection = Sections.Count > 0 ? Sections[^1] : null;
        if (Sections.Count == 0) _sectionNumber = 0;
        SectionsChanged();
    }

    /// <summary>A section moved: the picture follows.</summary>
    internal void SectionMoved(SectionItem item) => Redraw();

    private void SectionsChanged()
    {
        OnPropertyChanged(nameof(HasSection));
        OnPropertyChanged(nameof(CanAddSection));
        if (Sections.Count == 0) MoveSectionTool = false;
        Redraw();
    }

    /// <summary>Where the ray under the mouse leaves the next wall (behind the sections), or null.</summary>
    public Vector3? ExitOfNextWall(Vector3 origin, Vector3 direction)
    {
        if (Hierarchy is not { } bvh || SectionGeometry.VisibleInterval(SectionPlanes, origin, direction) is not { } visible) return null;
        return Walls.ExitOfNextWall(Mesh, bvh, origin + direction * visible.Min, direction);
    }

    /// <summary>Removes all sections (Esc, „Alle Schnitte aufheben“).</summary>
    [RelayCommand]
    private void ClearSection()
    {
        PlaceSectionTool = false;
        MoveSectionTool = false;
        Sections.Clear();
        SelectedSection = null;
        _sectionNumber = 0;
        SectionsChanged();
    }

    /// <summary>The surface point along a ray, with its face (−1 while faces are not known); only what lies behind all sections.</summary>
    /// <param name="origin">Start of the ray.</param>
    /// <param name="direction">Direction of the ray.</param>
    public (D3 Point, int Face)? HitTest(Vector3 origin, Vector3 direction) =>
        HitTestWithNormal(origin, direction) is { } h ? (h.Point, h.Face) : null;

    /// <summary>Like <see cref="HitTest"/>, with the outward normal of the triangle hit.</summary>
    public (D3 Point, int Face, Vector3 Normal)? HitTestWithNormal(Vector3 origin, Vector3 direction)
    {
        if (Hierarchy is not { } bvh || SectionGeometry.VisibleInterval(SectionPlanes, origin, direction) is not { } visible) return null;
        var start = origin + direction * visible.Min;
        var reach = visible.Max >= float.MaxValue ? float.MaxValue : visible.Max - visible.Min;
        if (!bvh.Raycast(start, direction, reach, out var hit)) return null;
        var normal = Vector3.Normalize(Mesh.AreaNormal(hit.Triangle).ToVector3());
        return (D3.From(start + direction * hit.Distance), Faces is { } f ? f.FaceOf[hit.Triangle] : -1, normal);
    }

    /// <summary>The mouse moved over the model while measuring or moving a face.</summary>
    public void Hover(Vector3 origin, Vector3 direction)
    {
        if (!MeasureTool && !PushTool) return;
        HoverFace = HitTest(origin, direction) is { Face: >= 0 } h ? h.Face : -1;
    }

    /// <summary>A click while measuring: picks, lets go of the first pick or completes a measurement.</summary>
    /// <param name="origin">Start of the ray under the mouse.</param>
    /// <param name="direction">Its direction.</param>
    /// <param name="point">Shift held: a point instead of a face.</param>
    /// <param name="sameSpot">Whether a world point lies under the mouse within a few pixels of another (for letting go of a point).</param>
    public void Click(Vector3 origin, Vector3 direction, bool point, Func<D3, D3, bool> sameSpot)
    {
        if (!MeasureTool || HitTest(origin, direction) is not { } hit) return;
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
            ResizeCommand = new RelayCommand(() => StartResize(item!)),
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
            item.CanResize = ParallelPlanes(a, b) is not null;
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
        if (ResizeTarget is { } r)
        {
            // Length change: the face that stays in green, the one that moves in orange
            var (stays, moves) = ResizeFixedIndex == 0 ? (r.A, r.B) : (r.B, r.A);
            Add(stays, Color.Parse("#2FA36B"), 0.55f);
            Add(moves, Color.Parse("#F08A24"), 0.55f);
        }
        if (PushFace >= 0) list.Add(Range(PushFace, Color.Parse("#F08A24"), 0.6f));
        if (DimensionFace >= 0) list.Add(Range(DimensionFace, Color.Parse("#CC33BF"), 0.45f));
        if (PushTool && HoverFace >= 0 && HoverFace != PushFace) list.Add(Range(HoverFace, Color.Parse("#F08A24"), 0.3f));
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
