using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Styling;
using Avalonia.Threading;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;

namespace ImageConverter.Viewer3D.Views;

/// <summary>
/// The 3D view: draws the model with OpenGL and turns mouse input into camera moves and clicks.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Left drag turns (turntable around +Z): around the point under the mouse, or around the centre axis of the
/// model when chosen; right or middle drag (or Ctrl + left) moves; the wheel zooms towards the mouse – in the
/// perspective view through surfaces, see <see cref="OrbitCamera.Zoom"/>; Shift + wheel moves a section parallel to
/// the screen through the model; a double click fits the model.</item>
/// <item>A click without dragging (less than <see cref="DragThreshold"/> pixels) is a measuring click.</item>
/// <item>Frames are drawn only when something changed.</item>
/// <item>The whole area takes the mouse (<see cref="ICustomHitTest"/>): Avalonia counts a control as hit only where it
/// draws itself, and the OpenGL picture is drawn past it – without this, every click and drag went to the frame behind
/// (Fehlergedächtnis #115).</item>
/// </list>
/// </remarks>
internal sealed partial class ModelGlView : OpenGlControlBase, ICustomHitTest
{
    private const double DragThreshold = 4;
    private const float TurnPerPixel = 0.0065f;

    private readonly ModelViewModel _model;
    private readonly ModelRenderer _renderer = new();
    private Gl? _gl;
    private bool _fitted;

    // Pointer gesture
    private Point _pressAt;
    private Point _last;
    private bool _dragging;
    private bool _panning;
    private bool _pressed;
    private Vector3 _pivot;

    // View animation
    private DispatcherTimer? _animation;

    public ModelGlView(ModelViewModel model)
    {
        _model = model;
        Focusable = true;
        ClipToBounds = true;
        Camera.Orthographic = model.Orthographic;
        var box = model.Mesh.Bounds;
        Camera.SceneCentre = box.Center;
        Camera.SceneRadius = Math.Max(box.Diagonal * 0.5f * 1.8f, 1e-3f);   // model and the ground around it
        model.Changed += OnModelChanged;
        model.ViewRequested += OnViewRequested;
        model.CentreRequested += OnCentreRequested;
        model.MeshReplaced += OnMeshReplaced;
        ActualThemeVariantChanged += (_, _) => Invalidate();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ModelViewModel.MeasureTool) or nameof(ModelViewModel.PlaceSectionTool) or nameof(ModelViewModel.MoveSectionTool) or nameof(ModelViewModel.SketchTool) or nameof(ModelViewModel.PushTool))
                Cursor = model.MeasureTool || model.PlaceSectionTool || model.SketchTool || model.PushTool ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
        };
    }

    public OrbitCamera Camera { get; } = new();

    /// <summary>An edit replaced the model: the scene bounds follow it, the drawing uploads it anew; the view stays.</summary>
    private void OnMeshReplaced()
    {
        var box = _model.Mesh.Bounds;
        Camera.SceneCentre = box.Center;
        Camera.SceneRadius = Math.Max(box.Diagonal * 0.5f * 1.8f, 1e-3f);
        _renderer.ForgetScene();
        Invalidate();
    }

    /// <summary>Ambient occlusion on (for comparisons of the shading).</summary>
    internal bool OcclusionEnabled
    {
        get => _renderer.OcclusionEnabled;
        set
        {
            _renderer.OcclusionEnabled = value;
            Invalidate();
        }
    }

    /// <summary>Every point inside the control counts as a hit.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Why the model cannot be drawn, or null.</summary>
    public string? Failure { get; private set; }

    /// <summary>Raised after the camera moved or the model changed: the overlay draws again.</summary>
    public event Action? ViewChanged;

    /// <summary>Raised when drawing turns out impossible (no suitable OpenGL).</summary>
    public event Action? Failed;

    /// <summary>Pivot of the turn in progress, for the marker in the overlay; null when not turning.</summary>
    public Vector3? TurningAround { get; private set; }

    /// <summary>Hit test of the overlay's labels; set by the canvas.</summary>
    public Func<Point, MeasurementItem?>? LabelAt { get; set; }

    /// <summary>The grip of the drawn face's frame under a point (from the overlay), or null.</summary>
    public Func<Point, int?>? GripAt { get; set; }

    private void OnModelChanged()
    {
        if (Camera.Orthographic != _model.Orthographic)
        {
            Camera.Orthographic = _model.Orthographic;
        }
        Invalidate();
    }

    private void Invalidate()
    {
        RequestNextFrameRendering();
        ViewChanged?.Invoke();
    }

    // ------------------------------------------------------------------ OpenGL

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            if (GlVersion.Major < 3)
                throw new NotSupportedException($"OpenGL {GlVersion.Major}.{GlVersion.Minor}");
            _gl = new Gl(gl, GlVersion);
            _renderer.Init(_gl);
            Failure = _renderer.Failure;
            AppLog.Write($"3D: {GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}, {gl.Renderer}");
        }
        catch (NotSupportedException e)
        {
            Failure = e.Message;
            AppLog.Write("3D: OpenGL not usable", e.Message);
        }
        if (Failure is not null) Dispatcher.UIThread.Post(() => Failed?.Invoke());
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer.Deinit();
        _gl = null;
    }

    protected override void OnOpenGlLost()
    {
        _gl = null;
        base.OnOpenGlLost();
    }

    private Action<byte[], int, int>? _capture;

    /// <summary>Hands the next drawn frame (RGBA, top row first) to <paramref name="done"/>; for checks of the drawing.</summary>
    internal void CaptureNextFrame(Action<byte[], int, int> done)
    {
        _capture = done;
        RequestNextFrameRendering();
    }

    /// <summary>Frames drawn so far (for measuring the frame rate).</summary>
    internal int FramesDrawn { get; private set; }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null || Failure is not null) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var size = PixelSize.FromSize(Bounds.Size, scaling);
        if (size.Width <= 0 || size.Height <= 0) return;
        EnsureFitted();

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var frame = new FrameState(Camera.ViewProjection, Camera.Projection, 0.06f * _model.Mesh.Bounds.Diagonal,
            Camera.Eye, Camera.Forward, Camera.Right, Camera.Up, Camera.Orthographic,
            _model.Mode, _model.ShowEdges, _model.ShowParts, dark, _model.Highlights(), _model.SectionPlanes,
            _model.Sections.Select(x => (SectionGeometry.Quad(_model.Mesh.Bounds, x.Plane), x.IsSelected)).ToArray(), _model.MoveSectionTool,
            _model.Brightness / 100f, _model.Quality, _model.ResizeGuide, _model.Previews);
        _renderer.Render(_model.Scene, frame, fb, size.Width, size.Height);
        FramesDrawn++;
        if (_renderer.Failure is { } failure && Failure is null)
        {
            Failure = failure;
            Dispatcher.UIThread.Post(() => Failed?.Invoke());
            return;
        }

        if (_capture is { } capture)
        {
            _capture = null;
            var pixels = _renderer.ReadBack(fb, size.Width, size.Height);
            Dispatcher.UIThread.Post(() => capture(pixels, size.Width, size.Height));
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Camera.Width = Math.Max(1, (int)Math.Ceiling(e.NewSize.Width));
        Camera.Height = Math.Max(1, (int)Math.Ceiling(e.NewSize.Height));
        Invalidate();
    }

    /// <summary>The first time the size is known: looking from the front-right above, fitted.</summary>
    private void EnsureFitted()
    {
        if (_fitted || Bounds.Width < 2) return;
        _fitted = true;
        (Camera.Yaw, Camera.Pitch) = OrbitCamera.Angles(StandardView.Iso);
        FitNow();
    }

    private void FitNow()
    {
        var box = _model.Mesh.Bounds;
        Camera.Fit(box.Center, Math.Max(box.Diagonal * 0.5f, 1e-3f));
    }

    // ------------------------------------------------------------------ standard views, with a short animation

    private void OnViewRequested(StandardView? view)
    {
        var box = _model.Mesh.Bounds;
        var probe = new OrbitCamera { Width = Camera.Width, Height = Camera.Height, Orthographic = Camera.Orthographic, Yaw = Camera.Yaw, Pitch = Camera.Pitch };
        if (view is { } v) (probe.Yaw, probe.Pitch) = OrbitCamera.Angles(v);
        probe.Fit(box.Center, Math.Max(box.Diagonal * 0.5f, 1e-3f));
        AnimateTo(probe.State);
    }

    /// <summary>Moves the centre of the model into the middle of the view; zoom and direction stay.</summary>
    private void OnCentreRequested()
    {
        var end = Camera.State;
        end.Target = _model.Mesh.Bounds.Center;
        AnimateTo(end);
    }

    private void AnimateTo((Vector3 Target, float Yaw, float Pitch, float Distance) end)
    {
        _animation?.Stop();
        var start = Camera.State;
        var began = DateTime.UtcNow;
        const double seconds = 0.28;
        _animation = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
        {
            var t = (float)Math.Min(1, (DateTime.UtcNow - began).TotalSeconds / seconds);
            Camera.State = OrbitCamera.Blend(start, end, t);
            Invalidate();
            if (t >= 1) _animation?.Stop();
        });
        _animation.Start();
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        _animation?.Stop();
        var p = e.GetCurrentPoint(this);
        _pressAt = _last = p.Position;
        _pressed = true;
        _dragging = false;
        _panning = !p.Properties.IsLeftButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        e.Pointer.Capture(this);

        // A press on a measurement label only removes it on release, whatever tool is on
        _gesture = EditGesture.None;
        if (p.Properties.IsLeftButtonPressed && LabelAt?.Invoke(_pressAt) is not null)
        {
            e.Handled = true;
            return;
        }

        // Editing tools: grips, drawn faces, the body, moving a face (ModelGlView.Editing)
        if (p.Properties.IsLeftButtonPressed && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && EditPress(e))
        {
            e.Handled = true;
            return;
        }

        // Moving the section: a press on the plane grabs it, a press beside it ends the moving
        _planeDrag = null;
        if (_model.MoveSectionTool && p.Properties.IsLeftButtonPressed && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (SectionPlaneAt(_pressAt) is { } grab)
            {
                _model.SelectedSection = grab.Section;
                _planeDrag = (grab.Point, grab.Section.Plane.W);
            }
            else _model.MoveSectionTool = false;
        }

        if (p.Properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            OnViewRequested(null);
            _pressed = false;
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (!_pressed)
        {
            if (_model.MeasureTool)
            {
                var (o, d) = Camera.Ray((float)pos.X, (float)pos.Y);
                _model.Hover(o, d);
            }
            else if (_model.MoveSectionTool)
            {
                Cursor = SectionPlaneAt(pos) is not null ? new Cursor(StandardCursorType.SizeNorthSouth) : Cursor.Default;
            }
            else EditHover(pos);
            return;
        }

        if (_planeDrag is { } drag)
        {
            DragSection(drag.Point, drag.Distance, pos);
            e.Handled = true;
            return;
        }
        if (_gesture != EditGesture.None)
        {
            EditMove(pos, e.KeyModifiers);
            e.Handled = true;
            return;
        }

        if (!_dragging)
        {
            if (Distance(pos, _pressAt) < DragThreshold) return;
            _dragging = true;
            _pivot = _panning ? Camera.Target : PivotFor(_pressAt);
            TurningAround = _panning ? null : _pivot;
            Cursor = new Cursor(_panning ? StandardCursorType.SizeAll : StandardCursorType.Hand);
        }

        var dx = (float)(pos.X - _last.X);
        var dy = (float)(pos.Y - _last.Y);
        _last = pos;
        if (_panning) Camera.Pan(dx, dy, _pivot);
        else Camera.Orbit(_pivot, dx * TurnPerPixel, dy * TurnPerPixel);
        Invalidate();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        var wasDrag = _dragging;
        _dragging = false;
        TurningAround = null;
        Cursor = _model.MeasureTool || _model.PlaceSectionTool || _model.SketchTool || _model.PushTool ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
        Invalidate();
        if (_planeDrag is not null)
        {
            _planeDrag = null;
            return;
        }
        if (_gesture != EditGesture.None)
        {
            EditRelease(wasDrag || Distance(e.GetPosition(this), _pressAt) >= DragThreshold);
            return;
        }
        var pos = e.GetPosition(this);
        if (!wasDrag && e.InitialPressMouseButton == MouseButton.Left && LabelAt?.Invoke(pos) is { } clicked)
        {
            // the label of a measurement (with its ✕): removes it, also when measuring is switched off
            _model.Remove(clicked);
            e.Handled = true;
            return;
        }
        if (!wasDrag && e.InitialPressMouseButton == MouseButton.Left && _model.PlaceSectionTool)
        {
            // Put the section onto the face under the mouse
            var (ro, rd) = Camera.Ray((float)pos.X, (float)pos.Y);
            if (_model.HitTestWithNormal(ro, rd) is { } hit) _model.PlaceSection(hit.Point, hit.Face, hit.Normal);
            e.Handled = true;
            return;
        }
        if (wasDrag || e.InitialPressMouseButton != MouseButton.Left || !_model.MeasureTool) return;

        var (o, d) = Camera.Ray((float)pos.X, (float)pos.Y);
        _model.Click(o, d, e.KeyModifiers.HasFlag(KeyModifiers.Shift), SameSpot);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _animation?.Stop();
        var pos = e.GetPosition(this);
        e.Handled = true;
        var box = _model.Mesh.Bounds;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift + wheel: the section goes deeper (wheel towards the screen) or back, by 2 % of what is visible – fine
            // when zoomed in; some systems send Shift + wheel sideways
            var steps = (float)(Math.Abs(e.Delta.Y) >= Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X);
            _model.MoveSection(steps * 0.02f * Camera.UnitsPerPixel(Camera.Target) * Camera.Height, Camera.Forward);
            return;
        }

        // Moving the section: the wheel over the plane moves it finely (1 % of what is visible per step)
        if (_model.MoveSectionTool && SectionPlaneAt(pos) is { } over)
        {
            _model.SelectedSection = over.Section;
            _model.MoveSection((float)e.Delta.Y * 0.01f * Camera.UnitsPerPixel(Camera.Target) * Camera.Height, Camera.Forward);
            return;
        }

        var zoomIn = e.Delta.Y > 0;
        if (Camera.Orthographic)
        {
            // Zooming back out keeps the walls away, so that what lies behind them can be seen as a whole;
            // Esc, ✕ or Shift + wheel bring them back.
            // Fully zoomed in: each further step goes through the next wall under the mouse (the section jumps behind it)
            if (zoomIn && Camera.ViewHeight <= MinViewHeight * 1.001f)
            {
                if ((_peelSteps += (float)e.Delta.Y) >= 1)
                {
                    _peelSteps = 0;
                    PeelAt(pos);
                }
                return;
            }
        }
        _peelSteps = 0;
        var factor = MathF.Pow(0.87f, (float)e.Delta.Y);
        // Perspective: at least half a percent of the model per step, so that the wheel goes through surfaces;
        // orthographic: down to a view of 2 % of the model, then through walls (above)
        Camera.Zoom(factor, AnchorAt(pos), 0.005f * box.Diagonal, MinViewHeight);
        Invalidate();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_model.MeasureTool) _model.HoverFace = -1;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && (_model.PlaceSectionTool || _model.MoveSectionTool))
        {
            _model.PlaceSectionTool = false;
            _model.MoveSectionTool = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _model.CancelPending()) e.Handled = true;
        else if (e.Key == Key.Escape && _model.HasSection)
        {
            _model.ClearSectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None)
        {
            OnViewRequested(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.None)
        {
            OnCentreRequested();
            e.Handled = true;
        }
    }

    // Section being dragged: the point grabbed on the plane and the plane's distance at the press
    private (Vector3 Point, float Distance)? _planeDrag;


    /// <summary>
    /// The nearest drawn section plane under the mouse and the point on it, or null: off all rectangles, or hidden behind
    /// visible model parts (looking at a plane from behind).
    /// </summary>
    private (SectionItem Section, Vector3 Point)? SectionPlaneAt(Point at)
    {
        var (o, d) = Camera.Ray((float)at.X, (float)at.Y);
        var model = _model.HitTest(o, d) is { } hit ? Vector3.Dot(hit.Point.ToVector3() - o, d) : float.MaxValue;
        (SectionItem Section, Vector3 Point)? best = null;
        var bestT = float.MaxValue;
        foreach (var s in _model.Sections)
        {
            if (SectionGeometry.Hit(_model.Mesh.Bounds, s.Plane, o, d) is not { } t || t >= bestT || t > model + 1e-6f) continue;
            bestT = t;
            best = (s, o + d * t);
        }
        return best;
    }

    /// <summary>
    /// Moves the section with the mouse: the plane follows the mouse along its normal as seen on screen; looking straight
    /// at the plane, dragging up moves it deeper.
    /// </summary>
    private void DragSection(Vector3 grabbed, float startDistance, Point now)
    {
        if (_model.SelectedSection is not { } section) return;
        var n = section.Normal;
        var k = Camera.UnitsPerPixel(grabbed) * 50;
        var s0 = Camera.Project(grabbed);
        var s1 = Camera.Project(grabbed + n * k);
        var screen = new Vector2(s1.X - s0.X, s1.Y - s0.Y) / k;   // pixels per unit along the normal
        var mouse = new Vector2((float)(now.X - _pressAt.X), (float)(now.Y - _pressAt.Y));
        float delta;
        if (screen.Length() * k < 10) delta = -mouse.Y * Camera.UnitsPerPixel(grabbed);
        else delta = Vector2.Dot(mouse, screen) / screen.LengthSquared();
        _model.SetSectionDistance(startDistance + delta);
    }

    /// <summary>Smallest view height of the orthographic view: 2 % of the model.</summary>
    private float MinViewHeight => 0.02f * _model.Mesh.Bounds.Diagonal;

    // Wheel steps gathered while fully zoomed in (touchpads send fractions of a step)
    private float _peelSteps;

    /// <summary>Takes away the next wall under the mouse: the section goes just behind where the ray leaves it.</summary>
    private void PeelAt(Point at)
    {
        var (o, d) = Camera.Ray((float)at.X, (float)at.Y);
        if (_model.ExitOfNextWall(o, d) is { } exit) _model.PeelTo(exit, Camera.Forward);
    }

    /// <summary>Turning centre: the centre axis of the model, or the surface under the mouse (else the current centre).</summary>
    private Vector3 PivotFor(Point at)
    {
        if (_model.PivotAtCentre) return _model.Mesh.Bounds.Center;
        var (o, d) = Camera.Ray((float)at.X, (float)at.Y);
        return _model.HitTest(o, d) is { } hit ? hit.Point.ToVector3() : Camera.Target;
    }

    /// <summary>Point that stays under the mouse while zooming: the surface, else the plane through the current centre.</summary>
    private Vector3 AnchorAt(Point at)
    {
        var (o, d) = Camera.Ray((float)at.X, (float)at.Y);
        if (_model.HitTest(o, d) is { } hit) return hit.Point.ToVector3();
        var f = Camera.Forward;
        var t = Vector3.Dot(Camera.Target - o, f) / Math.Max(Vector3.Dot(d, f), 1e-6f);
        return o + d * t;
    }

    /// <summary>Two surface points are the same spot: within 6 pixels on screen.</summary>
    private bool SameSpot(D3 a, D3 b)
    {
        var pa = Camera.Project(a.ToVector3());
        var pb = Camera.Project(b.ToVector3());
        return Math.Abs(pa.X - pb.X) < 6 && Math.Abs(pa.Y - pb.Y) < 6;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animation?.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
