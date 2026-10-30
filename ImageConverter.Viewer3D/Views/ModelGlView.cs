using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
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
/// model when chosen; right or middle drag (or Ctrl + left) moves; the wheel zooms towards the mouse; a double
/// click fits the model.</item>
/// <item>A click without dragging (less than <see cref="DragThreshold"/> pixels) is a measuring click.</item>
/// <item>Frames are drawn only when something changed.</item>
/// </list>
/// </remarks>
internal sealed class ModelGlView : OpenGlControlBase
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
        ActualThemeVariantChanged += (_, _) => Invalidate();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelViewModel.MeasureTool))
                Cursor = model.MeasureTool ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
        };
    }

    public OrbitCamera Camera { get; } = new();

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
            _model.Mode, _model.ShowEdges, _model.ShowParts, dark, _model.Highlights());
        _renderer.Render(_model.Scene, frame, fb, size.Width, size.Height);

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
        Cursor = _model.MeasureTool ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
        Invalidate();
        if (wasDrag || e.InitialPressMouseButton != MouseButton.Left || !_model.MeasureTool) return;

        var pos = e.GetPosition(this);
        if (LabelAt?.Invoke(pos) is { } label)
        {
            _model.Remove(label);
            return;
        }
        var (o, d) = Camera.Ray((float)pos.X, (float)pos.Y);
        _model.Click(o, d, e.KeyModifiers.HasFlag(KeyModifiers.Shift), SameSpot);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _animation?.Stop();
        var pos = e.GetPosition(this);
        var factor = MathF.Pow(0.87f, (float)e.Delta.Y);
        Camera.Zoom(factor, AnchorAt(pos));
        Invalidate();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_model.MeasureTool) _model.HoverFace = -1;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _model.CancelPending()) e.Handled = true;
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None)
        {
            OnViewRequested(null);
            e.Handled = true;
        }
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
