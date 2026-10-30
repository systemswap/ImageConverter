using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Controls;

/// <summary>
/// Image preview with a selection frame.
///
/// The frame is initially invisible and appears only when the left mouse button is
/// dragged over the image. Then: dragging corners and edge midpoints resizes, dragging
/// the interior moves, a click outside the selection or Esc clears it, arrow keys move
/// it by 1 px (Shift: 10 px).
///
/// The selection is held in image pixels, not in screen coordinates; otherwise it
/// would shift whenever the window size or zoom changes.
/// </summary>
public sealed class CropView : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<CropView, Bitmap?>(nameof(Source));

    /// <summary>Redraws when the value changes; for bitmaps whose pixels change in place
    /// (upscaling writes finished tiles into the same bitmap).</summary>
    public static readonly StyledProperty<object?> RedrawTokenProperty =
        AvaloniaProperty.Register<CropView, object?>(nameof(RedrawToken));

    public object? RedrawToken
    {
        get => GetValue(RedrawTokenProperty);
        set => SetValue(RedrawTokenProperty, value);
    }

    /// <summary>Selection in image pixels; null = no selection.</summary>
    public static readonly StyledProperty<Rect?> SelectionProperty =
        AvaloniaProperty.Register<CropView, Rect?>(nameof(Selection),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Selection tool on (default): dragging with the left button draws a selection. Off: dragging with the left
    /// button pans the image (hand tool). Ctrl + drag pans in both cases.
    /// </summary>
    public static readonly StyledProperty<bool> SelectionEnabledProperty =
        AvaloniaProperty.Register<CropView, bool>(nameof(SelectionEnabled), defaultValue: true);

    public bool SelectionEnabled
    {
        get => GetValue(SelectionEnabledProperty);
        set => SetValue(SelectionEnabledProperty, value);
    }

    /// <summary>
    /// Free rotation in degrees that has not been applied yet (positive = clockwise); 0 = none.
    /// The preview shows the image rotated. Every change sets the frame to the largest rectangle with the
    /// image's aspect ratio that lies entirely inside the rotated image; afterwards it can be moved and
    /// resized but stops at the slanted edges, so empty corners never appear. The frame is expressed in
    /// the unrotated coordinate system around the image centre (<see cref="RotationGeometry"/>).
    /// </summary>
    public static readonly StyledProperty<double> RotationAngleProperty =
        AvaloniaProperty.Register<CropView, double>(nameof(RotationAngle), defaultBindingMode: BindingMode.TwoWay);

    public double RotationAngle
    {
        get => GetValue(RotationAngleProperty);
        set => SetValue(RotationAngleProperty, value);
    }

    private bool IsRotated => Math.Abs(RotationAngle) > 1e-9;

    /// <summary>
    /// Perspective correction: frame with four freely movable corners (top left, top right, bottom right,
    /// bottom left) in image pixels; null = off. The outside is darkened as when cropping, so the edges of
    /// the result are visible immediately. Handles at corners and edge midpoints, the interior moves the
    /// whole frame, a magnifier appears while a corner is dragged. Arrow keys move the most recently
    /// dragged corner by 1 px (Shift: 10 px).
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<Point>?> QuadProperty =
        AvaloniaProperty.Register<CropView, IReadOnlyList<Point>?>(nameof(Quad), defaultBindingMode: BindingMode.TwoWay);

    public IReadOnlyList<Point>? Quad
    {
        get => GetValue(QuadProperty);
        set => SetValue(QuadProperty, value);
    }

    private bool QuadMode => Quad is { Count: 4 };

    /// <summary>
    /// Curved page: traced lines in image pixels; null = off. In drawing mode (<see cref="DrawLines"/>) a click adds a
    /// point to the active line (without an active line a new one starts), Enter or double-click ends it. Points can be
    /// dragged (with magnifier), a click on a line inserts a point there (drawing mode) or continues drawing on it, the
    /// × next to a line removes it, right-click cycles the direction (horizontal → vertical → fold). Delete or Backspace
    /// removes the selected point, with Shift the whole line; arrow keys move the point by 1 px (Shift: 10 px). Esc ends
    /// drawing or clears the selection, but never leaves the tool.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<WarpLineShape>?> WarpLinesProperty =
        AvaloniaProperty.Register<CropView, IReadOnlyList<WarpLineShape>?>(nameof(WarpLines), defaultBindingMode: BindingMode.TwoWay);

    public IReadOnlyList<WarpLineShape>? WarpLines
    {
        get => GetValue(WarpLinesProperty);
        set => SetValue(WarpLinesProperty, value);
    }

    /// <summary>
    /// Curved page, drawing mode: every click adds a point, Enter or double-click starts the next line, Esc leaves the
    /// mode. Off (default): dragging pans the image, dragging a point moves the point.
    /// </summary>
    public static readonly StyledProperty<bool> DrawLinesProperty =
        AvaloniaProperty.Register<CropView, bool>(nameof(DrawLines), defaultBindingMode: BindingMode.TwoWay);

    public bool DrawLines
    {
        get => GetValue(DrawLinesProperty);
        set => SetValue(DrawLinesProperty, value);
    }

    private bool LinesMode => WarpLines is not null;

    /// <summary>Eyedropper active (white balance): a click into the image samples instead of drawing a frame; while hovering, a magnifier shows the sampling area. Esc ends it.</summary>
    public static readonly StyledProperty<bool> PickActiveProperty =
        AvaloniaProperty.Register<CropView, bool>(nameof(PickActive), defaultBindingMode: BindingMode.TwoWay);

    public bool PickActive
    {
        get => GetValue(PickActiveProperty);
        set => SetValue(PickActiveProperty, value);
    }

    /// <summary>Executed with the position in image pixels (<see cref="Point"/>) when clicking with the eyedropper active.</summary>
    public static readonly StyledProperty<ICommand?> PickCommandProperty =
        AvaloniaProperty.Register<CropView, ICommand?>(nameof(PickCommand));

    public ICommand? PickCommand
    {
        get => GetValue(PickCommandProperty);
        set => SetValue(PickCommandProperty, value);
    }

    /// <summary>Sampling points shown as small markers in the image.</summary>
    public static readonly StyledProperty<IReadOnlyList<PickMarker>?> PickMarkersProperty =
        AvaloniaProperty.Register<CropView, IReadOnlyList<PickMarker>?>(nameof(PickMarkers));

    public IReadOnlyList<PickMarker>? PickMarkers
    {
        get => GetValue(PickMarkersProperty);
        set => SetValue(PickMarkersProperty, value);
    }

    private Point? _pickHover;   // image point under the pointer while the eyedropper is active

    /// <summary>Brush active (removal): dragging paints a mark instead of drawing a frame.</summary>
    public static readonly StyledProperty<bool> BrushActiveProperty =
        AvaloniaProperty.Register<CropView, bool>(nameof(BrushActive));

    public bool BrushActive
    {
        get => GetValue(BrushActiveProperty);
        set => SetValue(BrushActiveProperty, value);
    }

    /// <summary>Brush diameter in screen pixels.</summary>
    public static readonly StyledProperty<double> BrushSizeProperty =
        AvaloniaProperty.Register<CropView, double>(nameof(BrushSize), 40);

    public double BrushSize
    {
        get => GetValue(BrushSizeProperty);
        set => SetValue(BrushSizeProperty, value);
    }

    /// <summary>The strokes painted so far (image pixels), drawn in red over the image (green for <see cref="BrushStroke.Keep"/>).</summary>
    public static readonly StyledProperty<IReadOnlyList<BrushStroke>?> BrushStrokesProperty =
        AvaloniaProperty.Register<CropView, IReadOnlyList<BrushStroke>?>(nameof(BrushStrokes));

    public IReadOnlyList<BrushStroke>? BrushStrokes
    {
        get => GetValue(BrushStrokesProperty);
        set => SetValue(BrushStrokesProperty, value);
    }

    /// <summary>New strokes keep instead of remove ("Hintergrund" tab, green brush).</summary>
    public static readonly StyledProperty<bool> BrushKeepProperty =
        AvaloniaProperty.Register<CropView, bool>(nameof(BrushKeep));

    public bool BrushKeep
    {
        get => GetValue(BrushKeepProperty);
        set => SetValue(BrushKeepProperty, value);
    }

    /// <summary>Hardness of the stroke being painted, 0…1 (1 = hard edge); finished strokes carry their own.</summary>
    public static readonly StyledProperty<double> BrushHardnessProperty =
        AvaloniaProperty.Register<CropView, double>(nameof(BrushHardness), 1);

    public double BrushHardness
    {
        get => GetValue(BrushHardnessProperty);
        set => SetValue(BrushHardnessProperty, value);
    }

    /// <summary>Strength of the green stroke being painted, 0…1; finished strokes carry their own.</summary>
    public static readonly StyledProperty<double> BrushStrengthProperty =
        AvaloniaProperty.Register<CropView, double>(nameof(BrushStrength), 1);

    public double BrushStrength
    {
        get => GetValue(BrushStrengthProperty);
        set => SetValue(BrushStrengthProperty, value);
    }

    /// <summary>The strokes have already been removed: hide them until a new stroke is added (then all are shown again).</summary>
    public static readonly StyledProperty<bool> BrushDimProperty =
        AvaloniaProperty.Register<CropView, bool>(nameof(BrushDim));

    public bool BrushDim
    {
        get => GetValue(BrushDimProperty);
        set => SetValue(BrushDimProperty, value);
    }

    /// <summary>Executed with the finished stroke (<see cref="BrushStroke"/>) when the mouse button is released.</summary>
    public static readonly StyledProperty<ICommand?> BrushCommandProperty =
        AvaloniaProperty.Register<CropView, ICommand?>(nameof(BrushCommand));

    public ICommand? BrushCommand
    {
        get => GetValue(BrushCommandProperty);
        set => SetValue(BrushCommandProperty, value);
    }

    private List<Point>? _brushPoints;   // stroke currently being painted (image pixels)
    private Point? _brushHover;          // pointer position on screen while the brush is active

    /// <summary>Aspect ratio width/height; 0 = free.</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<CropView, double>(nameof(AspectRatio));

    /// <summary>
    /// Corner radius of the selection in image pixels. Ranges up to half the longest side:
    /// beyond half the shorter side the corners become elliptical, at the maximum the
    /// selection becomes an ellipse (a circle at 1:1).
    /// </summary>
    public static readonly StyledProperty<double> SelectionRadiusProperty =
        AvaloniaProperty.Register<CropView, double>(nameof(SelectionRadius));

    public double SelectionRadius
    {
        get => GetValue(SelectionRadiusProperty);
        set => SetValue(SelectionRadiusProperty, value);
    }

    /// <summary>Executed on Enter or a double-click inside the selection.</summary>
    public static readonly StyledProperty<ICommand?> CropCommandProperty =
        AvaloniaProperty.Register<CropView, ICommand?>(nameof(CropCommand));

    public ICommand? CropCommand
    {
        get => GetValue(CropCommandProperty);
        set => SetValue(CropCommandProperty, value);
    }

    /// <summary>Executed when a gesture on the frame ends (mouse button released); this forms one undo step.</summary>
    public static readonly StyledProperty<ICommand?> GestureCompletedCommandProperty =
        AvaloniaProperty.Register<CropView, ICommand?>(nameof(GestureCompletedCommand));

    public ICommand? GestureCompletedCommand
    {
        get => GetValue(GestureCompletedCommandProperty);
        set => SetValue(GestureCompletedCommandProperty, value);
    }

    private void RequestCrop()
    {
        if (CropCommand is { } cmd && cmd.CanExecute(null))
            cmd.Execute(null);
    }

    /// <summary>Actual image size in pixels; the selection is computed in it even when
    /// <see cref="Source"/> is only a downscaled preview.</summary>
    public static readonly StyledProperty<PixelSize> DocumentSizeProperty =
        AvaloniaProperty.Register<CropView, PixelSize>(nameof(DocumentSize));

    public PixelSize DocumentSize
    {
        get => GetValue(DocumentSizeProperty);
        set => SetValue(DocumentSizeProperty, value);
    }

    /// <summary>
    /// Size of the result (target size). The zoom display, "100 %" and fit-to-view refer to it.
    /// Empty = same as <see cref="DocumentSize"/>.
    /// </summary>
    public static readonly StyledProperty<PixelSize> OutputSizeProperty =
        AvaloniaProperty.Register<CropView, PixelSize>(nameof(OutputSize));

    public PixelSize OutputSize
    {
        get => GetValue(OutputSizeProperty);
        set => SetValue(OutputSizeProperty, value);
    }

    /// <summary>Result pixels per image pixel (width): 800 from 4000 → 0.2.</summary>
    private double OutputRatio =>
        OutputSize.Width > 0 && ImageSize.Width > 0 ? (double)OutputSize.Width / ImageSize.Width : 1;

    /// <summary>Every change fits the view anew (new image, crop …).</summary>
    public static readonly StyledProperty<object?> ViewResetTokenProperty =
        AvaloniaProperty.Register<CropView, object?>(nameof(ViewResetToken));

    public object? ViewResetToken
    {
        get => GetValue(ViewResetTokenProperty);
        set => SetValue(ViewResetTokenProperty, value);
    }

    /// <summary>Image size of the placeholder while no image is loaded.</summary>
    public static readonly StyledProperty<PixelSize> PlaceholderSizeProperty =
        AvaloniaProperty.Register<CropView, PixelSize>(nameof(PlaceholderSize), new PixelSize(4000, 3000));

    public static readonly DirectProperty<CropView, int> DisplayPercentProperty =
        AvaloniaProperty.RegisterDirect<CropView, int>(nameof(DisplayPercent), o => o.DisplayPercent);

    /// <summary>
    /// "Before" image for the comparison; null = no comparison. When set, it lies left of the
    /// divider over <see cref="Source"/> ("after"); the divider can be dragged across the image
    /// with the mouse. Stretched to the image area like <see cref="Source"/>.
    /// </summary>
    public static readonly StyledProperty<Bitmap?> CompareSourceProperty =
        AvaloniaProperty.Register<CropView, Bitmap?>(nameof(CompareSource));

    public Bitmap? CompareSource
    {
        get => GetValue(CompareSourceProperty);
        set => SetValue(CompareSourceProperty, value);
    }

    /// <summary>Label of the left side in the comparison (translated, supplied by the UI).</summary>
    public static readonly StyledProperty<string?> BeforeLabelProperty =
        AvaloniaProperty.Register<CropView, string?>(nameof(BeforeLabel));

    public string? BeforeLabel
    {
        get => GetValue(BeforeLabelProperty);
        set => SetValue(BeforeLabelProperty, value);
    }

    /// <summary>Label of the right side in the comparison.</summary>
    public static readonly StyledProperty<string?> AfterLabelProperty =
        AvaloniaProperty.Register<CropView, string?>(nameof(AfterLabel));

    public string? AfterLabel
    {
        get => GetValue(AfterLabelProperty);
        set => SetValue(AfterLabelProperty, value);
    }

    /// <summary>Divider as a fraction of the image width (0 = all "after", 1 = all "before"); kept when the image changes.</summary>
    private double _split = 0.5;

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Rect? Selection
    {
        get => GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    public PixelSize PlaceholderSize
    {
        get => GetValue(PlaceholderSizeProperty);
        set => SetValue(PlaceholderSizeProperty, value);
    }

    private int _displayPercent = 100;

    /// <summary>Current display scale in percent (for the zoom display).</summary>
    public int DisplayPercent
    {
        get => _displayPercent;
        private set => SetAndRaise(DisplayPercentProperty, ref _displayPercent, value);
    }

    // ------------------------------------------------------------------ State

    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private enum DragMode { None, Create, Move, Resize, Pan, Split, QuadCorner, QuadEdge, QuadMove, Brush, WarpPoint }

    // More room at the top: the usage hint floats above the image there.
    private static readonly Thickness ViewPadding = new(28, 50, 28, 28);
    private const double HandleSize = 9;
    private const double HandleHitRadius = 9;
    private const double CreateThreshold = 4;

    private DragMode _mode;
    private Edge _edges;
    private Point _pressScreen;
    private Point _anchorImage;
    private Rect _startSelection;
    private Rect? _selectionBeforeCreate;
    private Point _dragImage;   // image point under the pointer while drawing or dragging the frame (for the magnifier)
    private Vector _moveOffset;

    private Rect _imageRect;   // image rectangle on screen
    private Rect _panStartRect;
    private double _scale = 1; // screen px per image pixel

    // ------------------------------------------------------------------ Appearance

    private static readonly IBrush ShadeBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x96, 0, 0, 0));
    private static readonly IBrush HandleFill = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush LabelBack = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0));
    private static readonly IPen FramePen = new ImmutablePen(Brushes.White, 1.5);
    private static readonly IPen BoundsPen = new ImmutablePen(
        new ImmutableSolidColorBrush(Color.FromArgb(0x90, 255, 255, 255)), 1, new ImmutableDashStyle([4, 3], 0));
    private static readonly IPen ThirdsPen =new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), 1);
    private static readonly IPen HandlePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 1);
    private static readonly IPen ImageEdgePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1);

    private static readonly IBrush PlaceholderBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#5B7BA8"), 0),
            new GradientStop(Color.Parse("#9DB0C4"), 0.5),
            new GradientStop(Color.Parse("#D9CDB8"), 1),
        }
    };

    private static readonly IBrush CheckerBrush = CreateCheckerBrush();

    private static IBrush CreateCheckerBrush()
    {
        const double cell = 8;
        var drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing
                {
                    Brush = new SolidColorBrush(Color.Parse("#FFFFFF")),
                    Geometry = new RectangleGeometry(new Rect(0, 0, cell * 2, cell * 2)),
                },
                new GeometryDrawing
                {
                    Brush = new SolidColorBrush(Color.Parse("#D9DCE1")),
                    Geometry = new GeometryGroup
                    {
                        Children =
                        {
                            new RectangleGeometry(new Rect(0, 0, cell, cell)),
                            new RectangleGeometry(new Rect(cell, cell, cell, cell)),
                        }
                    },
                },
            }
        };

        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, cell * 2, cell * 2, RelativeUnit.Absolute),
        };
    }

    private static readonly Cursor CursorCross = new(StandardCursorType.Cross);
    private static readonly Cursor CursorHand = new(StandardCursorType.Hand);
    private static readonly Cursor CursorGrab = new(StandardCursorType.DragMove);
    private static readonly Cursor CursorMove = new(StandardCursorType.SizeAll);
    private static readonly Cursor CursorTopLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor CursorTopRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor CursorBottomLeft = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor CursorBottomRight = new(StandardCursorType.BottomRightCorner);
    private static readonly Cursor CursorWestEast = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor CursorNorthSouth = new(StandardCursorType.SizeNorthSouth);

    static CropView()
    {
        AffectsRender<CropView>(SourceProperty, SelectionProperty, PlaceholderSizeProperty, SelectionRadiusProperty,
            CompareSourceProperty, BeforeLabelProperty, AfterLabelProperty, RotationAngleProperty, QuadProperty,
            PickActiveProperty, PickMarkersProperty, BrushActiveProperty, BrushStrokesProperty, BrushDimProperty, BrushSizeProperty,
            BrushKeepProperty, BrushHardnessProperty, BrushStrengthProperty, WarpLinesProperty, RedrawTokenProperty);
        FocusableProperty.OverrideDefaultValue<CropView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CropView>(true);
    }

    /// <summary>
    /// Image size for selection and zoom. The displayed bitmap may be smaller
    /// (preview); it is then stretched to this area.
    /// </summary>
    private PixelSize ImageSize =>
        DocumentSize is { Width: > 0, Height: > 0 } doc ? doc : Source?.PixelSize ?? PlaceholderSize;

    // ------------------------------------------------------------ Geometry

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ViewResetTokenProperty)
        {
            // New image state (load, crop, rotation, undo): fit the view again
            _fitMode = true;
            UpdateGeometry();
            InvalidateVisual();
        }
        else if (change.Property == DrawLinesProperty)
        {
            // Drawing on (button, N): always a new line; continuing an existing line works by clicking on it
            // (BeginDrawingOn). Off (Esc, button): finish the active line and deselect everything; otherwise drawing
            // would continue on that line the next time the mode is switched on.
            if (!DrawLines)
            {
                EndActiveLine();
                _warpSel = (-1, -1);
            }
            else _activeLine = -1;
            _drawHover = null;
            InvalidateVisual();
        }
        else if (change.Property == WarpLinesProperty)
        {
            // changed from outside (reset, tool restarted): keep the active line and selection only if they still exist
            var count = WarpLines?.Count ?? 0;
            if (_activeLine >= count) _activeLine = -1;
            if (_hoverLine >= count) _hoverLine = -1;
            if (_warpSel.Line >= count || (_warpSel.Line >= 0 && _warpSel.Point >= WarpLines![_warpSel.Line].Points.Count)) _warpSel = (-1, -1);
        }
        else if (change.Property == BoundsProperty
                 || change.Property == SourceProperty
                 || change.Property == DocumentSizeProperty
                 || change.Property == OutputSizeProperty
                 || change.Property == PlaceholderSizeProperty)
        {
            // Only the preview bitmap changed (e.g. sharpness slider): zoom and
            // selection stay where they are.
            UpdateGeometry();
        }

        if (change.Property == AspectRatioProperty && Selection is { } sel && AspectRatio > 0)
            Selection = IsRotated ? ShrinkIntoRotated(ReshapeAroundCenter(sel, AspectRatio)) : Round(ReshapeAroundCenter(sel, AspectRatio));

        if (change.Property == PickActiveProperty && !PickActive) _pickHover = null;
        if (change.Property == BrushActiveProperty && !BrushActive)
        {
            _brushHover = null;
            _brushPoints = null;
            if (_mode == DragMode.Brush) _mode = DragMode.None;
        }

        if (change.Property == RotationAngleProperty && ImageSize.Width > 0)
        {
            if (IsRotated)
            {
                // Recomputed for every angle: smaller at first, larger again from 45°, the whole image at 180°
                Selection = AutoFrame();
                ShowGridBriefly();
            }
            else if (Math.Abs(change.GetOldValue<double>()) > 1e-9 && Selection is not null)
            {
                // Moved back to 0°: the frame covers the whole image again (discarding then sets it to null)
                Selection = new Rect(0, 0, ImageSize.Width, ImageSize.Height);
            }
        }
    }

    // ------------------------------------------------------------ Free rotation

    /// <summary>Rotation around the centre of the on-screen image area (as in ImageMagick: positive = clockwise).</summary>
    private Matrix RotationMatrix()
    {
        var c = _imageRect.Center;
        return Matrix.CreateTranslation(-c.X, -c.Y)
               * Matrix.CreateRotation(RotationAngle * Math.PI / 180)
               * Matrix.CreateTranslation(c.X, c.Y);
    }

    /// <summary>
    /// Largest rectangle with the image's aspect ratio that lies entirely inside the rotated image, with a 1 px
    /// margin, because rotation blends the edge pixels with the empty background.
    /// </summary>
    private Rect AutoFrame()
    {
        var size = ImageSize;
        var (x, y, w, h) = RotationGeometry.LargestInside(size.Width, size.Height, RotationAngle,
            (double)size.Width / size.Height);
        return RoundInside(new Rect(x + 1, y + 1, Math.Max(1, w - 2), Math.Max(1, h - 2)));
    }

    /// <summary>Whether the rectangle lies entirely inside the (rotated) image.</summary>
    private bool InsideRotated(Rect r) =>
        !IsRotated || RotationGeometry.ContainsRect(ImageSize.Width, ImageSize.Height, RotationAngle,
            r.X, r.Y, r.Width, r.Height);

    /// <summary>Whether the screen point lies on the (rotated) image.</summary>
    private bool OnImage(Point screen)
    {
        if (!IsRotated) return _imageRect.Contains(screen);
        var p = ToImage(screen);
        return RotationGeometry.Contains(ImageSize.Width, ImageSize.Height, RotationAngle, p.X, p.Y);
    }

    /// <summary>
    /// The requested frame, but not beyond the slanted edges: starting from a valid frame, it goes as far
    /// towards the requested one as possible, so the frame stops at the edge.
    /// </summary>
    private Rect KeepInRotated(Rect requested, Rect valid)
    {
        var r = RoundInside(requested);
        if (InsideRotated(r)) return r;

        var lo = FurthestInside(valid, requested);
        var best = RoundInside(Lerp(valid, requested, lo));
        return InsideRotated(best) ? best : RoundInside(valid);
    }

    /// <summary>
    /// Moving: like <see cref="KeepInRotated"/>, but the size is kept. Only the position is rounded, and towards
    /// the valid position; everything in between lies inside the (convex) rotated image. Rounding inwards would
    /// make the frame one pixel narrower at the edge on every pointer move.
    /// </summary>
    private Rect MoveInRotated(Rect requested, Rect valid)
    {
        var r = new Rect(Math.Round(requested.X), Math.Round(requested.Y), valid.Width, valid.Height);
        if (InsideRotated(r)) return r;

        var lo = FurthestInside(valid, requested);
        var dx = Math.Truncate((requested.X - valid.X) * lo);
        var dy = Math.Truncate((requested.Y - valid.Y) * lo);
        var best = new Rect(valid.X + dx, valid.Y + dy, valid.Width, valid.Height);
        return InsideRotated(best) ? best : valid;
    }

    /// <summary>Largest fraction 0 … 1 of the way from <paramref name="valid"/> to <paramref name="requested"/> that stays inside the image (bisection).</summary>
    private double FurthestInside(Rect valid, Rect requested)
    {

        double lo = 0, hi = 1;
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2;
            if (InsideRotated(Lerp(valid, requested, mid))) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Shrinks around the centre until the frame lies inside the rotated image (after an aspect ratio change).</summary>
    private Rect ShrinkIntoRotated(Rect r)
    {
        var (x, y, w, h) = RotationGeometry.ShrinkInside(ImageSize.Width, ImageSize.Height, RotationAngle,
            r.X, r.Y, r.Width, r.Height);
        return RoundInside(new Rect(x, y, w, h));
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        new Point(a.Left + (b.Left - a.Left) * t, a.Top + (b.Top - a.Top) * t),
        new Point(a.Right + (b.Right - a.Right) * t, a.Bottom + (b.Bottom - a.Bottom) * t));

    /// <summary>Rounds inwards to whole pixels so that the frame stays inside the rotated image.</summary>
    private static Rect RoundInside(Rect r)
    {
        var x0 = Math.Ceiling(r.X - 1e-6);
        var y0 = Math.Ceiling(r.Y - 1e-6);
        var x1 = Math.Floor(r.Right + 1e-6);
        var y1 = Math.Floor(r.Bottom + 1e-6);
        return new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    // Guide grid while rotating: stays visible briefly after the last angle change
    private static readonly TimeSpan GridTime = TimeSpan.FromSeconds(1.2);
    private DateTime _gridUntil;
    private DispatcherTimer? _gridTimer;

    private void ShowGridBriefly()
    {
        _gridUntil = DateTime.UtcNow + GridTime;
        _gridTimer ??= new DispatcherTimer(GridTime, DispatcherPriority.Background, (_, _) =>
        {
            _gridTimer!.Stop();
            InvalidateVisual();
        });
        _gridTimer.Stop();
        _gridTimer.Start();
    }

    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), 1);
    private static readonly IPen GridShadowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 1);
    private const double GridSpacing = 32;   // screen pixels

    /// <summary>Draws a fine horizontal/vertical grid over the rotated image for aligning the horizon and edges.</summary>
    private void RenderGrid(DrawingContext context, Geometry imageArea)
    {
        var area = imageArea.Bounds.Intersect(new Rect(Bounds.Size));
        if (area.Width <= 0 || area.Height <= 0) return;
        var c = _imageRect.Center;
        using (context.PushGeometryClip(imageArea))
        {
            var startX = c.X - Math.Ceiling((c.X - area.Left) / GridSpacing) * GridSpacing;
            for (var x = startX; x <= area.Right; x += GridSpacing)
            {
                var px = Math.Round(x) + 0.5;
                context.DrawLine(GridShadowPen, new Point(px + 1, area.Top), new Point(px + 1, area.Bottom));
                context.DrawLine(GridPen, new Point(px, area.Top), new Point(px, area.Bottom));
            }
            var startY = c.Y - Math.Ceiling((c.Y - area.Top) / GridSpacing) * GridSpacing;
            for (var y = startY; y <= area.Bottom; y += GridSpacing)
            {
                var py = Math.Round(y) + 0.5;
                context.DrawLine(GridShadowPen, new Point(area.Left, py + 1), new Point(area.Right, py + 1));
                context.DrawLine(GridPen, new Point(area.Left, py), new Point(area.Right, py));
            }
        }
    }

    // ------------------------------------------------------------ Zoom

    private const double MaxScale = 32;       // 3200 %, for inspecting individual pixels
    private const double WheelZoomStep = 1.2; // per wheel notch

    /// <summary>true = the image is fitted into the window; false = free zoom.</summary>
    private bool _fitMode = true;

    private Rect ViewArea => new Rect(Bounds.Size).Deflate(ViewPadding);

    /// <summary>Scale at which the image fits exactly (small images at most 100 % of the result size).</summary>
    private double FitScale
    {
        get
        {
            var size = ImageSize;
            var area = ViewArea;
            if (size.Width <= 0 || size.Height <= 0 || area.Width <= 0 || area.Height <= 0) return 1;
            // fit, but do not enlarge the result beyond 100 %
            return Math.Min(OutputRatio, Math.Min(area.Width / size.Width, area.Height / size.Height));
        }
    }

    private void UpdateGeometry()
    {
        var size = ImageSize;
        var area = ViewArea;
        if (size.Width <= 0 || size.Height <= 0 || area.Width <= 0 || area.Height <= 0)
        {
            _imageRect = default;
            return;
        }

        if (_fitMode || _scale < FitScale - 1e-9)   // fitted scale but panned (_fitMode off): the position is kept (else branch)
        {
            _fitMode = true;
            _scale = FitScale;
            var w = size.Width * _scale;
            var h = size.Height * _scale;
            _imageRect = new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }
        else
        {
            // window size changed while zoomed: keep the scale, only clamp the position
            _imageRect = ClampPan(_imageRect);
        }

        DisplayPercent = (int)Math.Round(_scale / OutputRatio * 100);
    }

    /// <summary>Zooms to <paramref name="newScale"/>; the image point under <paramref name="anchor"/> stays in place.</summary>
    private void ZoomAt(Point anchor, double newScale)
    {
        if (_imageRect.Width <= 0) return;

        var fit = FitScale;
        newScale = Math.Clamp(newScale, fit, MaxScale * OutputRatio);
        if (Math.Abs(newScale - fit) < 1e-6)
        {
            FitToView();
            return;
        }

        var imagePoint = ToImage(anchor);
        _fitMode = false;
        _scale = newScale;
        var size = ImageSize;
        _imageRect = ClampPan(new Rect(
            anchor.X - imagePoint.X * _scale,
            anchor.Y - imagePoint.Y * _scale,
            size.Width * _scale,
            size.Height * _scale));

        DisplayPercent = (int)Math.Round(_scale / OutputRatio * 100);
        InvalidateVisual();
    }

    /// <summary>
    /// How far the image can be panned: far enough for every point to reach the centre of the preview, so that edges
    /// covered by a panel (tool, edits) can be reached as well. No further, otherwise the image could be lost off screen.
    /// </summary>
    private Rect ClampPan(Rect r)
    {
        var center = ViewArea.Center;
        var x = Math.Clamp(r.X, center.X - r.Width, center.X);
        var y = Math.Clamp(r.Y, center.Y - r.Height, center.Y);
        return new Rect(x, y, r.Width, r.Height);
    }

    /// <summary>Fits the whole image into the window again.</summary>
    public void FitToView()
    {
        _fitMode = true;
        UpdateGeometry();
        InvalidateVisual();
    }

    /// <summary>Returns the visible part of the image in image pixels.</summary>
    /// <returns>The visible area (the whole image when fitted); an empty rectangle when no image is laid out.</returns>
    public Rect VisibleImageRect()
    {
        if (_imageRect.Width <= 0 || _scale <= 0) return default;
        var a = ToImage(ViewArea.TopLeft);
        var b = ToImage(ViewArea.BottomRight);
        return new Rect(a, b).Intersect(new Rect(0, 0, ImageSize.Width, ImageSize.Height));
    }

    /// <summary>Zooms to actual size (100 % of the result size) around the centre of the view.</summary>
    public void ZoomToActualSize() => ZoomAt(ViewArea.Center, OutputRatio);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_imageRect.Width <= 0) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl + mouse wheel: zoom around the pointer
            ZoomAt(e.GetPosition(this), _scale * Math.Pow(WheelZoomStep, e.Delta.Y));
            e.Handled = true;
        }
    }

    private Point ToImage(Point screen) =>
        new((screen.X - _imageRect.X) / _scale, (screen.Y - _imageRect.Y) / _scale);

    private Rect ToScreen(Rect image) =>
        new(_imageRect.X + image.X * _scale, _imageRect.Y + image.Y * _scale,
            image.Width * _scale, image.Height * _scale);

    private Point ClampToImage(Point p) =>
        new(Math.Clamp(p.X, 0, ImageSize.Width), Math.Clamp(p.Y, 0, ImageSize.Height));

    /// <summary>
    /// The brush centre may go up to one brush radius beyond the image: a soft brush acts fully only in its middle, so with its
    /// centre held at the edge a wide band along the edge stayed half covered and could not be painted (No. 107).
    /// </summary>
    private Point ClampToBrushArea(Point p)
    {
        var r = BrushSize / 2 / _scale;
        return new Point(Math.Clamp(p.X, -r, ImageSize.Width + r), Math.Clamp(p.Y, -r, ImageSize.Height + r));
    }

    /// <summary>Whether the brush at this screen position touches the image (a stroke may start just outside it).</summary>
    private bool TouchesImage(Point screen)
    {
        var p = ToImage(screen);
        var r = BrushSize / 2 / _scale;
        return p.X >= -r && p.Y >= -r && p.X <= ImageSize.Width + r && p.Y <= ImageSize.Height + r;
    }

    private static Rect Round(Rect r)
    {
        var x = Math.Round(r.X);
        var y = Math.Round(r.Y);
        return new Rect(x, y,
            Math.Max(1, Math.Round(r.Right) - x),
            Math.Max(1, Math.Round(r.Bottom) - y));
    }

    // ------------------------------------------------------------ Hit testing

    private Edge HitTestHandles(Point p, Rect s)
    {
        var edges = Edge.None;
        var inX = p.X >= s.Left - HandleHitRadius && p.X <= s.Right + HandleHitRadius;
        var inY = p.Y >= s.Top - HandleHitRadius && p.Y <= s.Bottom + HandleHitRadius;
        if (!inX || !inY) return Edge.None;

        if (Math.Abs(p.X - s.Left) <= HandleHitRadius) edges |= Edge.Left;
        else if (Math.Abs(p.X - s.Right) <= HandleHitRadius) edges |= Edge.Right;
        if (Math.Abs(p.Y - s.Top) <= HandleHitRadius) edges |= Edge.Top;
        else if (Math.Abs(p.Y - s.Bottom) <= HandleHitRadius) edges |= Edge.Bottom;
        return edges;
    }

    private static Cursor CursorFor(Edge e) => e switch
    {
        Edge.Left | Edge.Top => CursorTopLeft,
        Edge.Right | Edge.Bottom => CursorBottomRight,
        Edge.Right | Edge.Top => CursorTopRight,
        Edge.Left | Edge.Bottom => CursorBottomLeft,
        Edge.Left or Edge.Right => CursorWestEast,
        _ => CursorNorthSouth,
    };

    // ------------------------------------------------------------ Mouse

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        var p = e.GetPosition(this);

        // Panning: Ctrl + left button; without the selection tool the left button alone
        var panKey = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (props.IsLeftButtonPressed && panKey)   // also when fitted: the image can be pushed beyond the edge
        {
            Focus();
            _mode = DragMode.Pan;
            _pressScreen = p;
            _panStartRect = _imageRect;
            Cursor = CursorGrab;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Curved page: right-clicking a line cycles its direction
        if (LinesMode && props.IsRightButtonPressed)
        {
            if (HitWarpPoint(p) is { } hp) ChangeWarpLine(hp.Line, l => l.NextKind());
            else if (HitWarpSegment(p) is { } hs) ChangeWarpLine(hs.Line, l => l.NextKind());
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        Focus();
        _pressScreen = p;

        // Comparison: the divider takes precedence, also over brush and eyedropper (otherwise an attempt to drag it would
        // paint a stroke in the removal tab, and the already removed mark would reappear)
        if (OnSplitLine(p))
        {
            _mode = DragMode.Split;
            Cursor = CursorWestEast;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Brush: paint instead of drawing a frame
        if (BrushActive && !IsRotated)
        {
            if (TouchesImage(p))
            {
                _mode = DragMode.Brush;
                _brushPoints = [ClampToBrushArea(ToImage(p))];
                e.Pointer.Capture(this);
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        // Eyedropper: sample instead of drawing a frame
        if (PickActive)
        {
            if (OnImage(p) && PickCommand is { } pick)
            {
                var at = ToImage(p);
                if (pick.CanExecute(at)) pick.Execute(at);
            }
            e.Handled = true;
            return;
        }

        // Curved page: add and drag points
        if (LinesMode)
        {
            PressWarp(e, p);
            return;
        }

        // Perspective correction: drag corners, edge midpoints or the whole quadrilateral
        if (QuadMode)
        {
            if (e.ClickCount >= 2 && InsideQuad(p))
            {
                RequestCrop();
                e.Handled = true;
                return;
            }

            var (kind, index) = HitQuad(p);
            if (kind != DragMode.None)
            {
                _mode = kind;
                _quadIndex = index;
                if (kind == DragMode.QuadCorner) _activeCorner = index;
                _quadStart = Quad!.ToArray();
                _anchorImage = ToImage(p);
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
                return;
            }

            _mode = DragMode.Pan;   // outside: pan
            _panStartRect = _imageRect;
            Cursor = CursorGrab;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Selection tool off: dragging pans the image (also when fitted, beyond the edge)
        if (!SelectionEnabled)
        {
            _mode = DragMode.Pan;
            _panStartRect = _imageRect;
            Cursor = CursorGrab;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (Selection is { } sel)
        {
            var screenSel = ToScreen(sel);
            var edges = HitTestHandles(p, screenSel);
            if (edges != Edge.None)
            {
                _mode = DragMode.Resize;
                _edges = edges;
                _startSelection = sel;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (screenSel.Contains(p) && e.ClickCount >= 2)
            {
                RequestCrop();
                e.Handled = true;
                return;
            }

            if (screenSel.Contains(p))
            {
                _mode = DragMode.Move;
                var ip = ToImage(p);
                _moveOffset = new Vector(ip.X - sel.X, ip.Y - sel.Y);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (OnImage(p))
        {
            // A new selection starts only once the pointer has moved a little; a plain
            // click only clears the existing selection (while rotating, it is kept).
            var anchor = ClampToImage(ToImage(p));
            if (IsRotated && !InsideRotated(new Rect(anchor, new Size(0, 0)))) return;
            _mode = DragMode.Create;
            _anchorImage = anchor;
            _selectionBeforeCreate = Selection;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        switch (_mode)
        {
            case DragMode.None:
                if ((BrushActive && !IsRotated || PickActive) && OnSplitLine(p))
                {
                    // over the divider: drag instead of paint/sample, no brush circle
                    _brushHover = null;
                    _pickHover = null;
                    Cursor = CursorWestEast;
                    InvalidateVisual();
                    return;
                }
                if (BrushActive && !IsRotated)
                {
                    _brushHover = OnImage(p) ? p : null;
                    Cursor = _brushHover is null ? Cursor.Default : CursorCross;
                    InvalidateVisual();
                    return;
                }
                if (PickActive)
                {
                    _pickHover = OnImage(p) ? ToImage(p) : null;
                    Cursor = _pickHover is null ? Cursor.Default : CursorCross;
                    InvalidateVisual();
                    return;
                }
                UpdateHoverCursor(p, e.KeyModifiers);
                return;

            case DragMode.Create:
            {
                var d = p - _pressScreen;
                if (Selection is null && Math.Abs(d.X) < CreateThreshold && Math.Abs(d.Y) < CreateThreshold)
                    return;
                var current = _dragImage = ClampToImage(ToImage(p));
                var wanted = AspectRatio > 0
                    ? ConstrainFromAnchor(_anchorImage, current, AspectRatio)
                    : new Rect(_anchorImage, current).Normalize();
                Selection = IsRotated ? KeepInRotated(wanted, new Rect(_anchorImage, new Size(0, 0))) : Round(wanted);
                Cursor = CursorCross;
                break;
            }

            case DragMode.Move when Selection is { } sel:
            {
                var ip = ToImage(p);
                var x = Math.Clamp(ip.X - _moveOffset.X, 0, ImageSize.Width - sel.Width);
                var y = Math.Clamp(ip.Y - _moveOffset.Y, 0, ImageSize.Height - sel.Height);
                var wanted = new Rect(x, y, sel.Width, sel.Height);
                Selection = IsRotated ? MoveInRotated(wanted, sel) : Round(wanted);
                break;
            }

            case DragMode.Resize:
            {
                var wanted = Resize(_dragImage = ClampToImage(ToImage(p)));
                Selection = IsRotated ? KeepInRotated(wanted, _startSelection) : Round(wanted);
                break;
            }

            case DragMode.Pan:
                _fitMode = false;   // panned: do not fit again on the next layout update
                _imageRect = ClampPan(_panStartRect.Translate(p - _pressScreen));
                InvalidateVisual();
                break;

            case DragMode.Split:
                _split = Math.Clamp((p.X - _imageRect.X) / _imageRect.Width, 0, 1);
                InvalidateVisual();
                break;

            case DragMode.QuadCorner:
            {
                var q = _quadStart.ToArray();
                q[_quadIndex] = ClampToImage(ToImage(p));
                Quad = q;
                break;
            }

            case DragMode.WarpPoint when WarpLines is { } wl && _warpSel.Line >= 0 && _warpSel.Line < wl.Count:
            {
                if (!_warpMoved)
                {
                    // Start dragging only after a few pixels of movement: a click on a point does not move it (and does
                    // not turn a suggestion into a user line); instead drawing continues on that line.
                    if (_warpSel.Point < 0 || Point.Distance(p, _pressScreen) < WarpDragThreshold) break;
                    _warpMoved = true;
                }
                var line = wl[_warpSel.Line];
                if (_warpSel.Point < 0 || _warpSel.Point >= line.Points.Count) break;
                var pts = line.Points.ToArray();
                pts[_warpSel.Point] = ClampToImage(ToImage(p));
                ChangeWarpLine(_warpSel.Line, _ => (line with { Points = pts }).WithGuessedKind());
                break;
            }

            case DragMode.QuadEdge:
                Quad = ShiftQuad([_quadIndex, (_quadIndex + 1) % 4], ToImage(p) - _anchorImage);
                break;

            case DragMode.QuadMove:
                Quad = ShiftQuad([0, 1, 2, 3], ToImage(p) - _anchorImage);
                break;

            case DragMode.Brush when _brushPoints is { } points:
            {
                // Add a point only at a distance of about 1.5 screen pixels; otherwise the stroke grows without benefit.
                var ip = ClampToBrushArea(ToImage(p));
                if (Point.Distance(ip, points[^1]) * _scale >= 1.5) points.Add(ip);
                _brushHover = p;
                InvalidateVisual();
                break;
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mode == DragMode.None) return;

        if (_mode == DragMode.Brush)
        {
            var points = _brushPoints;
            _brushPoints = null;
            _mode = DragMode.None;
            e.Pointer.Capture(null);
            if (points is { Count: > 0 } && BrushCommand is { } paint)
            {
                var stroke = new BrushStroke(points.Select(q => (q.X, q.Y)).ToArray(), BrushSize / 2 / _scale, BrushKeep);
                if (paint.CanExecute(stroke)) paint.Execute(stroke);
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_mode == DragMode.Create)
        {
            var d = e.GetPosition(this) - _pressScreen;
            if (Math.Abs(d.X) < CreateThreshold && Math.Abs(d.Y) < CreateThreshold)
                Selection = IsRotated ? _selectionBeforeCreate : null;   // while rotating, the frame belongs to the rotation and is kept
        }

        if (_mode == DragMode.Pan && LinesMode) ReleaseWarpClick(e.GetPosition(this));

        // Click on a point without dragging (not in drawing mode): continue drawing on that line
        if (_mode == DragMode.WarpPoint && !_warpMoved && !DrawLines && _warpSel.Line >= 0) BeginDrawingOn(_warpSel.Line);

        var wasEdit = _mode is not (DragMode.Pan or DragMode.Split);
        _mode = DragMode.None;
        InvalidateVisual();   // hide the magnifier, show the active corner
        e.Pointer.Capture(null);
        UpdateHoverCursor(e.GetPosition(this), e.KeyModifiers);
        if (wasEdit && GestureCompletedCommand is { } done && done.CanExecute(null))
            done.Execute(null);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverLine >= 0 || _drawHover is not null)
        {
            _hoverLine = -1;   // hide the delete button when the pointer leaves the preview
            _drawHover = null;
            InvalidateVisual();
        }
        if (_pickHover is null && _brushHover is null) return;
        _pickHover = null;   // hide the magnifier when the pointer leaves the preview
        _brushHover = null;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _mode = DragMode.None;
    }

    private void UpdateHoverCursor(Point p, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            Cursor = CursorHand;
            return;
        }

        if (OnSplitLine(p))
        {
            Cursor = CursorWestEast;
            return;
        }

        if (LinesMode)
        {
            // Line under the pointer: its delete button (×) appears; over the button that line stays the shown one.
            // Drawing mode: the magnifier follows the pointer even before the click.
            var draw = DrawLines && OnImage(p) && HitDeleteHandle(p) is null ? ToImage(p) : (Point?)null;
            if (draw != _drawHover)
            {
                _drawHover = draw;
                InvalidateVisual();
            }
            var hover = HitDeleteHandle(p) ?? HitWarpPoint(p)?.Line ?? HitWarpSegment(p)?.Line ?? -1;
            // on the way from the line to its ×, the × stays visible (otherwise it would disappear before it is reached)
            if (hover < 0 && _hoverLine >= 0 && DeleteHandle(_hoverLine) is { } handle && Point.Distance(handle, p) <= DeleteHandleRadius + 22)
                hover = _hoverLine;
            if (hover != _hoverLine)
            {
                _hoverLine = hover;
                InvalidateVisual();
            }
            Cursor = HitDeleteHandle(p) is not null ? CursorHand
                : HitWarpPoint(p) is not null ? CursorMove
                : DrawLines ? (OnImage(p) ? CursorCross : CursorHand)
                : CursorHand;   // viewing: dragging pans the image
            return;
        }

        if (QuadMode)
        {
            var (kind, _) = HitQuad(p);
            Cursor = kind switch
            {
                DragMode.QuadCorner => CursorCross,
                DragMode.QuadEdge or DragMode.QuadMove => CursorMove,
                _ => CursorHand,
            };
            return;
        }

        if (!SelectionEnabled)
        {
            Cursor = CursorHand;
            return;
        }

        if (Selection is { } sel)
        {
            var s = ToScreen(sel);
            var edges = HitTestHandles(p, s);
            if (edges != Edge.None) { Cursor = CursorFor(edges); return; }
            if (s.Contains(p)) { Cursor = CursorMove; return; }
        }
        Cursor = OnImage(p) ? CursorCross : Cursor.Default;
    }

    // ------------------------------------------------------------ Comparison

    private const double SplitHitRadius = 8;
    private const double SplitKnobRadius = 13;

    /// <summary>Screen x position of the divider.</summary>
    private double SplitX => _imageRect.X + _imageRect.Width * _split;

    /// <summary>Visible part of the image (when zoomed, the image extends beyond the view).</summary>
    private Rect VisibleImage => _imageRect.Intersect(new Rect(Bounds.Size));

    /// <summary>Handle in the middle of the divider, within the visible part.</summary>
    private Point SplitKnob => new(SplitX, VisibleImage.Center.Y);

    private bool OnSplitLine(Point p) =>
        CompareSource is not null && VisibleImage.Width > 0
        && ((VisibleImage.Contains(p) && Math.Abs(p.X - SplitX) <= SplitHitRadius)
            || Point.Distance(p, SplitKnob) <= SplitKnobRadius + 2);

    private static readonly IPen SplitPen = new ImmutablePen(Brushes.White, 2);
    private static readonly IPen SplitShadowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 4);
    private static readonly IBrush KnobFill = new ImmutableSolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255));
    private static readonly IBrush KnobArrow = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0x30, 0x30, 0x30));

    /// <summary>Draws "before" left of the divider over "after", plus the divider, handle and labels.</summary>
    private void RenderCompare(DrawingContext context, Bitmap before)
    {
        var visible = VisibleImage;
        if (visible.Width <= 0) return;
        var x = SplitX;

        var left = new Rect(_imageRect.X, _imageRect.Y, Math.Max(0, x - _imageRect.X), _imageRect.Height);
        using (context.PushClip(left))
            DrawBitmap(context, before);

        var top = new Point(x, visible.Top);
        var bottom = new Point(x, visible.Bottom);
        context.DrawLine(SplitShadowPen, top, bottom);
        context.DrawLine(SplitPen, top, bottom);

        // Handle: circle with two arrowheads ◂ ▸
        var knob = SplitKnob;
        context.DrawEllipse(KnobFill, HandlePen, knob, SplitKnobRadius, SplitKnobRadius);
        var arrows = new StreamGeometry();
        using (var g = arrows.Open())
        {
            foreach (var dir in new[] { -1, 1 })
            {
                g.BeginFigure(new Point(knob.X + dir * 9, knob.Y), true);
                g.LineTo(new Point(knob.X + dir * 3.5, knob.Y - 5));
                g.LineTo(new Point(knob.X + dir * 3.5, knob.Y + 5));
                g.EndFigure(true);
            }
        }
        context.DrawGeometry(KnobArrow, null, arrows);

        // Labels in the top corners of each side, only if there is room
        DrawTag(context, BeforeLabel, new Point(Math.Max(visible.Left, _imageRect.Left) + 8, visible.Top + 8), x, alignRight: false);
        DrawTag(context, AfterLabel, new Point(Math.Min(visible.Right, _imageRect.Right) - 8, visible.Top + 8), x, alignRight: true);
    }

    private static void DrawTag(DrawingContext context, string? label, Point anchor, double splitX, bool alignRight)
    {
        if (string.IsNullOrEmpty(label)) return;
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Brushes.White);
        var pad = new Thickness(7, 3);
        var box = new Size(text.Width + pad.Left + pad.Right, text.Height + pad.Top + pad.Bottom);
        var bx = alignRight ? anchor.X - box.Width : anchor.X;
        // only on its own side of the divider
        if (alignRight ? bx < splitX + 4 : bx + box.Width > splitX - 4) return;
        context.DrawRectangle(LabelBack, null, new RoundedRect(new Rect(new Point(bx, anchor.Y), box), 4));
        context.DrawText(text, new Point(bx + pad.Left, anchor.Y + pad.Top));
    }

    /// <summary>Draws the bitmap stretched to the image area; from 2 screen pixels per bitmap pixel without interpolation (for inspection), below that smoothly.</summary>
    private void DrawBitmap(DrawingContext context, Bitmap bmp)
    {
        using (context.PushRenderOptions(new RenderOptions
               {
                   BitmapInterpolationMode = _imageRect.Width / bmp.PixelSize.Width >= 2
                       ? BitmapInterpolationMode.None
                       : BitmapInterpolationMode.HighQuality,
               }))
        {
            context.DrawImage(bmp, new Rect(bmp.Size), _imageRect);
        }
    }

    // ------------------------------------------------------------ Perspective correction (quadrilateral)

    private const double QuadCornerHit = 14;
    private const double QuadEdgeHit = 11;
    private const double QuadCornerRadius = 7;
    private const double LoupeRadius = 62;
    private const double LoupeZoom = 4;

    private int _quadIndex;
    private int _activeCorner;
    private Point[] _quadStart = [];

    private static readonly IPen QuadShadowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 3.5);
    private static readonly IPen QuadInvalidPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4F)), 2);
    private static readonly IBrush AccentFill = new ImmutableSolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xF6));
    private static readonly IPen LoupeRingPen = new ImmutablePen(Brushes.White, 2.5);
    private static readonly IPen LoupeEdgePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xD2, 0x3F)), 1.5);

    private Point ToScreenPoint(Point image) => new(_imageRect.X + image.X * _scale, _imageRect.Y + image.Y * _scale);

    private static IReadOnlyList<(double X, double Y)> Tuples(IReadOnlyList<Point> q) => q.Select(p => (p.X, p.Y)).ToArray();

    /// <summary>Determines what lies under the pointer: corner, edge midpoint, interior, or nothing.</summary>
    private (DragMode Kind, int Index) HitQuad(Point screen)
    {
        if (Quad is not { Count: 4 } q) return (DragMode.None, -1);
        var pts = q.Select(ToScreenPoint).ToArray();
        for (var i = 0; i < 4; i++)
            if (Point.Distance(screen, pts[i]) <= QuadCornerHit) return (DragMode.QuadCorner, i);
        for (var i = 0; i < 4; i++)
        {
            var mid = (pts[i] + pts[(i + 1) % 4]) / 2;
            if (Point.Distance(screen, mid) <= QuadEdgeHit) return (DragMode.QuadEdge, i);
        }
        return InsideQuad(screen) ? (DragMode.QuadMove, -1) : (DragMode.None, -1);
    }

    private bool InsideQuad(Point screen) =>
        Quad is { Count: 4 } q && QuadGeometry(q.Select(ToScreenPoint).ToArray()).FillContains(screen);

    /// <summary>Moves several corners together, but not beyond the image edge.</summary>
    private Point[] ShiftQuad(int[] indices, Vector delta)
    {
        var q = _quadStart.ToArray();
        var img = ImageSize;
        var dx = delta.X;
        var dy = delta.Y;
        foreach (var i in indices)
        {
            dx = Math.Clamp(dx, -q[i].X, img.Width - q[i].X);
            dy = Math.Clamp(dy, -q[i].Y, img.Height - q[i].Y);
        }
        foreach (var i in indices) q[i] = new Point(q[i].X + dx, q[i].Y + dy);
        return q;
    }

    private void OnQuadKey(KeyEventArgs e)
    {
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        Vector delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => default,
        };

        if (e.Key == Key.Escape)
        {
            Quad = null;   // tool off, nothing applied
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            RequestCrop();
            e.Handled = true;
        }
        else if (delta != default && Quad is { Count: 4 } q)
        {
            _quadStart = q.ToArray();
            Quad = ShiftQuad([_activeCorner], delta);
            e.Handled = true;
        }
    }

    private static StreamGeometry QuadGeometry(Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true);
            for (var i = 1; i < pts.Length; i++) c.LineTo(pts[i]);
            c.EndFigure(true);
        }
        return g;
    }

    /// <summary>Draws the quadrilateral: darkened outside, perspective-correct thirds grid, handles, target size, and the magnifier while dragging.</summary>
    private void RenderQuad(DrawingContext context)
    {
        var q = Quad!;
        var pts = q.Select(ToScreenPoint).ToArray();
        var outline = QuadGeometry(pts);
        var tuples = Tuples(q);
        var valid = PerspectiveGeometry.IsValid(tuples);

        context.DrawGeometry(ShadeBrush, null, new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(_imageRect), outline },
        });

        if (valid)
        {
            // Where the thirds lie in the result, so it is visible how straight it will become
            foreach (var t in new[] { 1 / 3.0, 2 / 3.0 })
            {
                Line(PerspectiveGeometry.PointAt(tuples, t, 0), PerspectiveGeometry.PointAt(tuples, t, 1));
                Line(PerspectiveGeometry.PointAt(tuples, 0, t), PerspectiveGeometry.PointAt(tuples, 1, t));
            }
        }

        context.DrawGeometry(null, QuadShadowPen, outline);
        context.DrawGeometry(null, valid ? FramePen : QuadInvalidPen, outline);

        // Edge midpoints (small, square) and corners (round, the active one blue)
        for (var i = 0; i < 4; i++)
        {
            var mid = (pts[i] + pts[(i + 1) % 4]) / 2;
            context.DrawRectangle(HandleFill, HandlePen, new Rect(mid.X - 4, mid.Y - 4, 8, 8));
        }
        for (var i = 0; i < 4; i++)
        {
            var dragging = _mode == DragMode.QuadCorner && _quadIndex == i;
            context.DrawEllipse(dragging || (_mode == DragMode.None && _activeCorner == i) ? AccentFill : HandleFill,
                HandlePen, pts[i], QuadCornerRadius, QuadCornerRadius);
        }

        if (valid)
        {
            var (w, h) = PerspectiveGeometry.TargetSize(tuples);
            var text = new FormattedText($"{w} × {h} px", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, Brushes.White);
            var pad = new Thickness(7, 3);
            var box = new Size(text.Width + pad.Left + pad.Right, text.Height + pad.Top + pad.Bottom);
            var bottom = pts.Max(p => p.Y);
            var cx = pts.Average(p => p.X);
            var bx = Math.Clamp(cx - box.Width / 2, 2, Math.Max(2, Bounds.Width - box.Width - 2));
            var by = bottom + 12 + box.Height <= Bounds.Height ? bottom + 12 : bottom - box.Height - 12;
            context.DrawRectangle(LabelBack, null, new RoundedRect(new Rect(new Point(bx, by), box), 4));
            context.DrawText(text, new Point(bx + pad.Left, by + pad.Top));
        }

        if (_mode == DragMode.QuadCorner && _quadIndex is >= 0 and < 4)
        {
            var i = _quadIndex;
            DrawLoupe(context, q[i], toLoupe =>
            {
                context.DrawLine(LoupeEdgePen, toLoupe(q[i]), toLoupe(q[(i + 1) % 4]));
                context.DrawLine(LoupeEdgePen, toLoupe(q[i]), toLoupe(q[(i + 3) % 4]));
            });
        }

        // Light with a dark shadow: visible on light paper as well as on a dark background
        void Line((double X, double Y) a, (double X, double Y) b)
        {
            var (pa, pb) = (ToScreenPoint(new Point(a.X, a.Y)), ToScreenPoint(new Point(b.X, b.Y)));
            context.DrawLine(GridShadowPen, pa + new Vector(1, 1), pb + new Vector(1, 1));
            context.DrawLine(ThirdsPen, pa, pb);
        }
    }

    /// <summary>
    /// Magnifier next to an image point: enlarged image and crosshair; <paramref name="overlay"/> draws on top (given the
    /// mapping image point → magnifier), e.g. the edges at a corner or the sampling area of the eyedropper.
    /// </summary>
    private void DrawLoupe(DrawingContext context, Point corner, Action<Func<Point, Point>>? overlay)
    {
        if (Source is not { } bmp || ImageSize.Width <= 0) return;
        var sp = ToScreenPoint(corner);
        var off = LoupeRadius + 26;
        var cx = sp.X - off - LoupeRadius < 0 ? sp.X + off : sp.X - off;
        var cy = sp.Y - off - LoupeRadius < 0 ? sp.Y + off : sp.Y - off;
        var center = new Point(cx, cy);
        var dest = new Rect(cx - LoupeRadius, cy - LoupeRadius, 2 * LoupeRadius, 2 * LoupeRadius);

        // image point → magnifier
        var zoom = _scale * LoupeZoom;
        Point ToLoupe(Point image) => new(cx + (image.X - corner.X) * zoom, cy + (image.Y - corner.Y) * zoom);

        // Section of the bitmap, clipped to the bitmap (points at the image edge)
        var bmpPerImage = bmp.PixelSize.Width / (double)ImageSize.Width;
        var half = LoupeRadius / zoom;
        var srcImage = new Rect(corner.X - half, corner.Y - half, 2 * half, 2 * half)
            .Intersect(new Rect(0, 0, ImageSize.Width, ImageSize.Height));

        using (context.PushGeometryClip(new EllipseGeometry(dest)))
        {
            context.FillRectangle(CheckerBrush, dest);
            if (srcImage.Width > 0 && srcImage.Height > 0)
            {
                var src = new Rect(srcImage.X * bmpPerImage, srcImage.Y * bmpPerImage,
                    srcImage.Width * bmpPerImage, srcImage.Height * bmpPerImage);
                var target = new Rect(ToLoupe(srcImage.TopLeft), ToLoupe(srcImage.BottomRight));
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                    context.DrawImage(bmp, src, target);
            }

            overlay?.Invoke(ToLoupe);
            // Crosshair in the inverse colour of the image below (dark on white paper, light on a dark background), with
            // a thin shadow so that it is visible on medium grey as well.
            var cross = new ImmutablePen(new ImmutableSolidColorBrush(CrossColor(bmp, new Point(corner.X * bmpPerImage, corner.Y * bmpPerImage))), 1.5);
            foreach (var pen in new[] { LoupeCrossShadowPen, cross })
            {
                context.DrawLine(pen, new Point(cx - 10, cy), new Point(cx - 3, cy));
                context.DrawLine(pen, new Point(cx + 3, cy), new Point(cx + 10, cy));
                context.DrawLine(pen, new Point(cx, cy - 10), new Point(cx, cy - 3));
                context.DrawLine(pen, new Point(cx, cy + 3), new Point(cx, cy + 10));
            }
        }
        context.DrawEllipse(null, QuadShadowPen, center, LoupeRadius, LoupeRadius);
        context.DrawEllipse(null, LoupeRingPen, center, LoupeRadius, LoupeRadius);
    }

    private static readonly IPen LoupeCrossShadowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 3.5);

    /// <summary>
    /// Inverse colour of the image around a point (mean over 5 × 5 bitmap pixels). If the point is almost medium grey, the
    /// inverse would be just as grey; then black or white, whichever contrasts more.
    /// </summary>
    private static Color CrossColor(Bitmap bmp, Point at)
    {
        try
        {
            var size = bmp.PixelSize;
            int x0 = Math.Clamp((int)at.X - 2, 0, Math.Max(0, size.Width - 5)), y0 = Math.Clamp((int)at.Y - 2, 0, Math.Max(0, size.Height - 5));
            var rect = new PixelRect(x0, y0, Math.Min(5, size.Width), Math.Min(5, size.Height));
            var buffer = new byte[rect.Width * rect.Height * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bmp.CopyPixels(rect, handle.AddrOfPinnedObject(), buffer.Length, rect.Width * 4); }
            finally { handle.Free(); }
            double c0 = 0, c1 = 0, c2 = 0;
            for (var i = 0; i < buffer.Length; i += 4) { c0 += buffer[i]; c1 += buffer[i + 1]; c2 += buffer[i + 2]; }
            var n = buffer.Length / 4.0;
            var rgba = bmp.Format == Avalonia.Platform.PixelFormats.Rgba8888;
            double r = (rgba ? c0 : c2) / n, g = c1 / n, b = (rgba ? c2 : c0) / n;
            var lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
            if (lum is > 0.38 and < 0.62) return lum >= 0.5 ? Colors.Black : Colors.White;
            return Color.FromRgb((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
        }
        catch (Exception)
        {
            return Colors.Magenta;   // bitmap not readable: a conspicuous colour rather than none
        }
    }

    // ------------------------------------------------------------ Curved page (lines)

    private const double WarpPointHit = 9;
    private const double WarpSegmentHit = 6;

    private int _activeLine = -1;                    // line currently being extended
    private (int Line, int Point) _warpSel = (-1, -1); // most recently touched point (keyboard, magnifier)

    // Colours per direction: horizontal blue, vertical orange, fold pink; light, with a dark shadow below
    private static readonly Color[] WarpColors = [Color.FromRgb(0x3F, 0xA9, 0xF5), Color.FromRgb(0xFF, 0xA6, 0x30), Color.FromRgb(0xFF, 0x4F, 0xA3)];
    private static readonly IPen[] WarpPens = WarpColors.Select(c => (IPen)new ImmutablePen(new ImmutableSolidColorBrush(c), 1.8)).ToArray();
    private static readonly IPen[] WarpActivePens = WarpColors.Select(c => (IPen)new ImmutablePen(new ImmutableSolidColorBrush(c), 2.6)).ToArray();
    private static readonly IBrush[] WarpFills = WarpColors.Select(c => (IBrush)new ImmutableSolidColorBrush(c)).ToArray();
    private static readonly IPen[] WarpSuggestedPens = WarpColors
        .Select(c => (IPen)new ImmutablePen(new ImmutableSolidColorBrush(c), 2, new ImmutableDashStyle([4, 3], 0))).ToArray();

    private (int Line, int Point)? HitWarpPoint(Point screen)
    {
        if (WarpLines is not { } lines) return null;
        (int, int)? best = null;
        var bestD = WarpPointHit;
        for (var i = 0; i < lines.Count; i++)
        for (var j = 0; j < lines[i].Points.Count; j++)
        {
            var d = Point.Distance(screen, ToScreenPoint(lines[i].Points[j]));
            if (d <= bestD) (bestD, best) = (d, (i, j));
        }
        return best;
    }

    private (int Line, int Segment)? HitWarpSegment(Point screen)
    {
        if (WarpLines is not { } lines) return null;
        (int, int)? best = null;
        var bestD = WarpSegmentHit;
        for (var i = 0; i < lines.Count; i++)
        for (var j = 0; j + 1 < lines[i].Points.Count; j++)
        {
            var d = DistanceToSegment(screen, ToScreenPoint(lines[i].Points[j]), ToScreenPoint(lines[i].Points[j + 1]));
            if (d <= bestD) (bestD, best) = (d, (i, j));
        }
        return best;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var len2 = ab.X * ab.X + ab.Y * ab.Y;
        var t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        return Point.Distance(p, a + ab * t);
    }

    private void ChangeWarpLine(int index, Func<WarpLineShape, WarpLineShape> change)
    {
        if (WarpLines is not { } lines || index < 0 || index >= lines.Count) return;
        var next = lines.ToArray();
        var old = next[index];
        var changed = change(old);
        // any change to a suggestion (point, direction) turns it into a user line, which "Suggest" then leaves alone
        if (old.Suggested && changed.Suggested && (changed.Kind != old.Kind || !changed.Points.SequenceEqual(old.Points)))
            changed = changed.Adopted();
        next[index] = changed;
        WarpLines = next;
    }

    // ------------------------------------------------------------ Delete button at the line

    private const double DeleteHandleRadius = 8;
    private int _hoverLine = -1;   // line under the pointer
    private Point? _drawHover;     // drawing mode: image point under the pointer (magnifier even before the click)

    /// <summary>Lines currently showing a delete button: the one under the pointer, the active one and the most recently touched one.</summary>
    private IEnumerable<int> DeleteCandidates()
    {
        var count = WarpLines?.Count ?? 0;
        return new[] { _hoverLine, _activeLine, _warpSel.Line }.Where(i => i >= 0 && i < count).Distinct();
    }

    /// <summary>
    /// Screen position of a line's delete button: next to the midpoint of its path, slightly offset (above a horizontal
    /// line, left of a vertical one), so that it covers neither the line nor its points.
    /// </summary>
    private Point? DeleteHandle(int line)
    {
        if (WarpLines is not { } lines || line < 0 || line >= lines.Count || lines[line].Points.Count < 2) return null;
        var pts = lines[line].Points.Select(ToScreenPoint).ToArray();
        var lengths = new double[pts.Length];
        for (var i = 1; i < pts.Length; i++) lengths[i] = lengths[i - 1] + Point.Distance(pts[i - 1], pts[i]);
        var half = lengths[^1] / 2;
        var k = Math.Clamp(Array.FindIndex(lengths, l => l >= half), 1, pts.Length - 1);
        var seg = lengths[k] - lengths[k - 1];
        var t = seg > 1e-9 ? (half - lengths[k - 1]) / seg : 0;
        var mid = pts[k - 1] + (pts[k] - pts[k - 1]) * t;
        var dir = pts[k] - pts[k - 1];
        var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 1e-9) return mid;
        var normal = new Vector(-dir.Y / len, dir.X / len);
        if (normal.Y > 0.01 || (Math.Abs(normal.Y) <= 0.01 && normal.X > 0)) normal = -normal;
        return mid + normal * (DeleteHandleRadius + 10);
    }

    private int? HitDeleteHandle(Point screen)
    {
        foreach (var i in DeleteCandidates())
            if (DeleteHandle(i) is { } c && Point.Distance(c, screen) <= DeleteHandleRadius + 2) return i;
        return null;
    }

    private void RemoveWarpLine(int index)
    {
        if (WarpLines is not { } lines || index < 0 || index >= lines.Count) return;
        _activeLine = _activeLine == index ? -1 : _activeLine > index ? _activeLine - 1 : _activeLine;
        _warpSel = _warpSel.Line == index ? (-1, -1) : _warpSel.Line > index ? (_warpSel.Line - 1, _warpSel.Point) : _warpSel;
        _hoverLine = -1;
        WarpLines = lines.Where((_, i) => i != index).ToArray();
    }

    private static readonly IBrush DeleteFill = new ImmutableSolidColorBrush(Color.FromArgb(0xD8, 0x30, 0x30, 0x30));
    private static readonly IPen DeleteCrossPen = new ImmutablePen(Brushes.White, 1.8, lineCap: PenLineCap.Round);

    private void RenderDeleteHandles(DrawingContext context)
    {
        foreach (var i in DeleteCandidates())
        {
            if (DeleteHandle(i) is not { } c) continue;
            context.DrawEllipse(DeleteFill, LoupeRingPen, c, DeleteHandleRadius, DeleteHandleRadius);
            const double a = 3.4;
            context.DrawLine(DeleteCrossPen, new Point(c.X - a, c.Y - a), new Point(c.X + a, c.Y + a));
            context.DrawLine(DeleteCrossPen, new Point(c.X - a, c.Y + a), new Point(c.X + a, c.Y - a));
        }
    }

    /// <summary>
    /// Left click in the tool. Viewing (default): dragging a point moves the point, otherwise dragging pans the image; a
    /// click without dragging on a line (or one of its points) switches drawing on, and the line grows at its nearer end
    /// (the × next to it removes it). Drawing (<see cref="DrawLines"/>): every click adds a point to the active line (or
    /// starts one), a click on a line inserts a point there and makes it the active line, double-click or Enter ends the
    /// line.
    /// </summary>
    private void PressWarp(PointerPressedEventArgs e, Point p)
    {
        e.Handled = true;
        if (HitDeleteHandle(p) is { } doomed)
        {
            RemoveWarpLine(doomed);   // × next to the line: remove the whole line (also a suggestion)
            return;
        }
        if (DrawLines && e.ClickCount >= 2)
        {
            EndActiveLine();   // double-click: the first click already added the last point; the next click starts a new line
            return;
        }

        if (HitWarpPoint(p) is { } hp)
        {
            StartWarpDrag(e, hp.Line, hp.Point);
            return;
        }

        if (!DrawLines)
        {
            // Viewing: pan; whether it was a click on a line is decided on release
            _lineClick = HitWarpSegment(p)?.Line ?? -1;
            StartWarpPan(e, p);
            return;
        }

        if (HitWarpSegment(p) is { } hs)
        {
            // Insert a point (a suggestion becomes a user line); the line becomes the active one
            var line = WarpLines![hs.Line];
            var pts = line.Points.ToList();
            pts.Insert(hs.Segment + 1, ClampToImage(ToImage(p)));
            ChangeWarpLine(hs.Line, _ => line with { Points = pts });
            StartWarpDrag(e, hs.Line, hs.Segment + 1);
            return;
        }
        if (!OnImage(p))
        {
            StartWarpPan(e, p);   // outside: pan
            return;
        }

        var at = ClampToImage(ToImage(p));
        var lines = WarpLines!.ToList();
        int index;
        if (_activeLine < 0 || _activeLine >= lines.Count)
        {
            lines.Add(new WarpLineShape(WarpLineKind.Horizontal, [at]));
            _activeLine = lines.Count - 1;
            index = 0;
        }
        else
        {
            // at the nearer end, so that a line can be extended at either end (a suggestion becomes a normal
            // line)
            var line = lines[_activeLine].Suggested ? lines[_activeLine].Adopted() : lines[_activeLine];
            var atStart = line.Points.Count >= 2 && Point.Distance(at, line.Points[0]) < Point.Distance(at, line.Points[^1]);
            lines[_activeLine] = (line with { Points = atStart ? [at, .. line.Points] : [.. line.Points, at] }).WithGuessedKind();
            index = atStart ? 0 : lines[_activeLine].Points.Count - 1;
        }
        WarpLines = lines;
        StartWarpDrag(e, _activeLine, index);
    }

    // Line under the click while panning: released without movement = click on the line
    private int _lineClick = -1;

    private void StartWarpPan(PointerPressedEventArgs e, Point p)
    {
        _mode = DragMode.Pan;
        _pressScreen = p;
        _panStartRect = _imageRect;
        Cursor = CursorGrab;
        e.Pointer.Capture(this);
    }

    /// <summary>Released without panning: a click on a line (continue drawing there) or outside (clear the selection).</summary>
    private void ReleaseWarpClick(Point p)
    {
        var line = _lineClick;
        _lineClick = -1;
        if (Point.Distance(p, _pressScreen) >= CreateThreshold) return;
        if (WarpLines is not { } lines || line < 0 || line >= lines.Count)
        {
            _warpSel = (-1, -1);
            InvalidateVisual();
            return;
        }
        BeginDrawingOn(line);
    }

    /// <summary>Switches drawing on and continues on this line (each click goes to its nearer end).</summary>
    private void BeginDrawingOn(int line)
    {
        _warpSel = (line, -1);   // selected: the × stays at it
        DrawLines = true;
        _activeLine = line;
        InvalidateVisual();
    }

    // A point is dragged only after this many screen pixels of movement; below that it is a click.
    private const double WarpDragThreshold = 3;
    private bool _warpMoved;

    private void StartWarpDrag(PointerPressedEventArgs e, int line, int point)
    {
        _mode = DragMode.WarpPoint;
        _warpMoved = false;
        _warpSel = (line, point);
        if (DrawLines) _activeLine = line;   // in drawing mode, the touched line keeps growing
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    /// <summary>Finishes the active line; a line with only one point is removed (it indicates no direction).</summary>
    private void EndActiveLine()
    {
        if (WarpLines is { } lines && _activeLine >= 0 && _activeLine < lines.Count && lines[_activeLine].Points.Count < 2)
        {
            var removed = _activeLine;
            _activeLine = -1;
            _warpSel = (-1, -1);
            WarpLines = lines.Where((_, i) => i != removed).ToArray();
        }
        _activeLine = -1;
        InvalidateVisual();
    }

    private void OnWarpKey(KeyEventArgs e)
    {
        var lines = WarpLines!;
        switch (e.Key)
        {
            case Key.Enter or Key.Return:
                if (_activeLine >= 0) EndActiveLine();   // while drawing: the next line starts with the next click
                else RequestCrop();
                e.Handled = true;
                return;
            case Key.Escape:
                // End drawing (line finished, selection cleared), otherwise only clear the selection. Esc never leaves
                // the tool; otherwise pressing it several times would lose all lines. The tool is left only via the
                // icon at the top.
                if (DrawLines) DrawLines = false;
                else if (_activeLine >= 0) EndActiveLine();
                else if (_warpSel.Line >= 0) { _warpSel = (-1, -1); InvalidateVisual(); }
                e.Handled = true;
                return;
            case Key.N:
                EndActiveLine();
                DrawLines = true;   // draw a new line
                e.Handled = true;
                return;
            case Key.Delete or Key.Back when _warpSel.Line >= 0 && _warpSel.Line < lines.Count:
            {
                var (li, pi) = _warpSel;
                var keep = lines.ToList();
                var pts = keep[li].Points.ToList();
                if (!keep[li].Suggested && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && pi >= 0 && pi < pts.Count) pts.RemoveAt(pi);   // suggestion: remove entirely
                else pts.Clear();
                if (pts.Count == 0)
                {
                    keep.RemoveAt(li);
                    _activeLine = _activeLine == li ? -1 : _activeLine > li ? _activeLine - 1 : _activeLine;
                    _warpSel = (-1, -1);
                }
                else
                {
                    keep[li] = (keep[li] with { Points = pts }).WithGuessedKind();
                    _warpSel = (li, Math.Min(pi, pts.Count - 1));
                }
                WarpLines = keep;
                e.Handled = true;
                return;
            }
        }

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        Vector delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => default,
        };
        if (delta == default || _warpSel.Line < 0 || _warpSel.Line >= lines.Count) return;
        var target = lines[_warpSel.Line];
        if (_warpSel.Point < 0 || _warpSel.Point >= target.Points.Count) return;   // whole line selected: nothing to move
        var moved = target.Points.ToArray();
        moved[_warpSel.Point] = ClampToImage(moved[_warpSel.Point] + delta);
        ChangeWarpLine(_warpSel.Line, _ => (target with { Points = moved }).WithGuessedKind());
        e.Handled = true;
    }

    /// <summary>Draws the lines in the colour of their direction (the active one bolder), their points, and the magnifier while dragging.</summary>
    private void RenderWarpLines(DrawingContext context)
    {
        var lines = WarpLines!;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var kind = (int)line.Kind;
            var active = i == _activeLine;
            var pts = line.Points.Select(ToScreenPoint).ToArray();
            if (pts.Length >= 2)
            {
                // smooth through the points (Catmull-Rom as Bézier); otherwise a curved text line looks angular and off
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(pts[0], false);
                    for (var k = 0; k + 1 < pts.Length; k++)
                    {
                        var (p0, p1, p2, p3) = (pts[Math.Max(0, k - 1)], pts[k], pts[k + 1], pts[Math.Min(pts.Length - 1, k + 2)]);
                        c.CubicBezierTo(p1 + (p2 - p0) / 6, p2 - (p3 - p1) / 6, p2);
                    }
                    c.EndFigure(false);
                }
                context.DrawGeometry(null, QuadShadowPen, g);
                context.DrawGeometry(null, line.Suggested ? WarpSuggestedPens[kind] : active ? WarpActivePens[kind] : WarpPens[kind], g);
            }
            for (var j = 0; j < pts.Length; j++)
            {
                var selected = _warpSel == (i, j);
                // suggestions: small points
                var r = selected ? 6 : line.Suggested ? 3 : 4.5;
                var fill = active ? HandleFill : WarpFills[kind];
                context.DrawEllipse(fill, HandlePen, pts[j], r, r);
                if (selected) context.DrawEllipse(null, WarpPens[kind], pts[j], r + 2, r + 2);
            }
        }

        if (_mode != DragMode.WarpPoint) RenderDeleteHandles(context);   // not while dragging (the magnifier matters more)

        // Drawing: magnifier even before the click, plus the next piece of the active line dashed (from the nearer end)
        if (_mode == DragMode.None && DrawLines && _drawHover is { } hv)
        {
            Point? from = null;
            if (_activeLine >= 0 && _activeLine < lines.Count && lines[_activeLine].Points is { Count: > 0 } ap)
                from = ap.Count >= 2 && Point.Distance(hv, ap[0]) < Point.Distance(hv, ap[^1]) ? ap[0] : ap[^1];
            if (from is { } f0)
            {
                context.DrawLine(QuadShadowPen, ToScreenPoint(f0), ToScreenPoint(hv));
                context.DrawLine(WarpSuggestedPens[(int)lines[_activeLine].Kind], ToScreenPoint(f0), ToScreenPoint(hv));
            }
            DrawLoupe(context, hv, toLoupe =>
            {
                if (from is { } f1) context.DrawLine(LoupeEdgePen, toLoupe(f1), toLoupe(hv));
            });
        }

        if (_mode == DragMode.WarpPoint && _warpSel.Line >= 0 && _warpSel.Line < lines.Count
            && _warpSel.Point >= 0 && _warpSel.Point < lines[_warpSel.Line].Points.Count)   // −1: line clicked, no point
        {
            var line = lines[_warpSel.Line];
            var j = _warpSel.Point;
            DrawLoupe(context, line.Points[j], toLoupe =>
            {
                if (j > 0) context.DrawLine(LoupeEdgePen, toLoupe(line.Points[j - 1]), toLoupe(line.Points[j]));
                if (j + 1 < line.Points.Count) context.DrawLine(LoupeEdgePen, toLoupe(line.Points[j]), toLoupe(line.Points[j + 1]));
            });
        }
    }

    private static readonly IBrush MarkerNeutral = new ImmutableSolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));
    private static readonly IBrush MarkerBlack = new ImmutableSolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly IBrush MarkerWhite = new ImmutableSolidColorBrush(Colors.White);

    /// <summary>Draws the eyedropper sampling points: a small circle with N, S or W.</summary>
    private void RenderPickMarkers(DrawingContext context)
    {
        if (PickMarkers is not { Count: > 0 } markers) return;
        foreach (var m in markers)
        {
            var c = ToScreenPoint(m.At);
            var (fill, ink) = m.Kind switch
            {
                WbPoint.Black => (MarkerBlack, Brushes.White),
                WbPoint.White => (MarkerWhite, Brushes.Black),
                _ => (MarkerNeutral, Brushes.White),
            };
            context.DrawEllipse(null, QuadShadowPen, c, 9, 9);
            context.DrawEllipse(fill, LoupeRingPen, c, 9, 9);
            var text = new FormattedText(m.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Typeface.Default.FontFamily, weight: FontWeight.Bold), 10, ink);
            context.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
        }
    }

    private static readonly IBrush BrushFill = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x30, 0x30));
    private static readonly IBrush BrushKeepFill = new ImmutableSolidColorBrush(Color.FromRgb(0x30, 0xD0, 0x50));
    private static readonly IPen BrushRingPen = new ImmutablePen(Brushes.White, 1.5);

    /// <summary>
    /// Draws the mark: strokes in semi-transparent red, green for "keep" (overlaps not darker; weaker green paler), soft strokes
    /// with the same falloff from the middle of the brush to its edge as the computation, plus the brush circle (a soft brush
    /// filled with its radial falloff).
    /// </summary>
    private void RenderBrush(DrawingContext context)
    {
        var strokes = BrushDim ? [] : BrushStrokes?.Select(s => (Points: s.Points.Select(q => new Point(q.X, q.Y)).ToList(), s.Radius, s.Keep,
            s.Strength, s.Hardness)).ToList() ?? [];
        if (_brushPoints is { } live) strokes.Add((live, BrushSize / 2 / _scale, BrushKeep, BrushKeep ? BrushStrength : 1, BrushHardness));
        if (strokes.Count > 0)
        {
            // Only over the image: the brush centre may go beyond the edge, but what lies outside is not part of the picture
            var corner = ToScreenPoint(new Point(ImageSize.Width, ImageSize.Height));
            using (context.PushClip(new Rect(ToScreenPoint(new Point(0, 0)), corner)))
            using (context.PushOpacity(0.5))
            {
                foreach (var (points, radius, keep, strength, hardness) in strokes)
                {
                    var r = radius * _scale;
                    var color = keep ? KeepColor : RemoveColor;
                    var top = keep ? 0.3 + 0.7 * Math.Clamp(strength, 0, 1) : 1;
                    if (hardness >= 1)
                    {
                        DrawStroke(context, points, r, top >= 1 ? keep ? BrushKeepFill : BrushFill : new ImmutableSolidColorBrush(color, top));
                        continue;
                    }
                    // Soft: nested strokes from the edge inwards; each layer adds so much that the coverage follows the falloff of
                    // the computation (full up to hardness × radius, smoothstep down to 0 at the radius)
                    const int layers = 12;
                    var inner = r * Math.Clamp(hardness, 0, 1);
                    var before = 0.0;
                    for (var j = 0; j < layers; j++)
                    {
                        var outer = r * (layers - j) / layers;
                        var d = outer - r / layers / 2;
                        var v = d <= inner ? 1 : Math.Clamp(1 - (d - inner) / Math.Max(1e-6, r - inner), 0, 1);
                        var target = top * v * v * (3 - 2 * v);
                        if (target > before)
                        {
                            DrawStroke(context, points, outer, new ImmutableSolidColorBrush(color, 1 - (1 - target) / (1 - before)));
                            before = target;
                        }
                        if (outer <= inner) break;   // everything inside is covered fully
                    }
                }
            }
        }

        if (BrushActive && _brushHover is { } hover)
        {
            var r = BrushSize / 2;
            // A soft brush shows its falloff: filled in its colour from the middle, fading out radially to the edge
            if (BrushHardness < 1)
            {
                var color = BrushKeep ? KeepColor : RemoveColor;
                var inner = Math.Clamp(BrushHardness, 0, 1);
                var fill = new RadialGradientBrush();
                for (var k = 0; k <= 8; k++)
                {
                    var t = inner + (1 - inner) * k / 8.0;   // radius fraction
                    var v = 1 - (t - inner) / Math.Max(1e-6, 1 - inner);
                    fill.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(110 * v * v * (3 - 2 * v)), color.R, color.G, color.B), t));
                }
                fill.GradientStops.Insert(0, new GradientStop(Color.FromArgb(110, color.R, color.G, color.B), 0));
                context.DrawEllipse(fill, null, hover, r, r);
            }
            context.DrawEllipse(null, QuadShadowPen, hover, r, r);
            context.DrawEllipse(null, BrushRingPen, hover, r, r);
        }
    }

    private static readonly Color RemoveColor = Color.FromRgb(0xFF, 0x30, 0x30);
    private static readonly Color KeepColor = Color.FromRgb(0x30, 0xD0, 0x50);

    /// <summary>One stroke with round ends (a single point as a disc).</summary>
    private void DrawStroke(DrawingContext context, IReadOnlyList<Point> points, double r, IBrush fill)
    {
        if (points.Count == 1)
        {
            context.DrawEllipse(fill, null, ToScreenPoint(points[0]), r, r);
            return;
        }
        var pen = new Pen(fill, 2 * r, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(ToScreenPoint(points[0]), false);
            foreach (var q in points.Skip(1)) g.LineTo(ToScreenPoint(q));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, line);
    }

    // ------------------------------------------------------------ Keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var center = ViewArea.Center;
            switch (e.Key)
            {
                case Key.D0 or Key.NumPad0: FitToView(); e.Handled = true; return;
                case Key.D1 or Key.NumPad1: ZoomToActualSize(); e.Handled = true; return;
                case Key.OemPlus or Key.Add: ZoomAt(center, _scale * WheelZoomStep); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: ZoomAt(center, _scale / WheelZoomStep); e.Handled = true; return;
            }
        }

        if (PickActive && e.Key == Key.Escape)
        {
            PickActive = false;
            _pickHover = null;
            e.Handled = true;
            return;
        }

        if (LinesMode)
        {
            OnWarpKey(e);
            return;
        }

        if (QuadMode)
        {
            OnQuadKey(e);
            return;
        }

        if (Selection is not { } sel) return;

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        Vector delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => default,
        };

        if (e.Key == Key.Escape)
        {
            // While rotating: discard the rotation, the frame belongs to it
            if (IsRotated) RotationAngle = 0;
            Selection = null;
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            RequestCrop();
            e.Handled = true;
        }
        else if (delta != default)
        {
            var x = Math.Clamp(sel.X + delta.X, 0, ImageSize.Width - sel.Width);
            var y = Math.Clamp(sel.Y + delta.Y, 0, ImageSize.Height - sel.Height);
            var wanted = new Rect(x, y, sel.Width, sel.Height);
            Selection = IsRotated ? MoveInRotated(wanted, sel) : wanted;
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ Calculations

    private Rect Resize(Point pt)
    {
        var s = _startSelection;
        var left = s.Left; var top = s.Top; var right = s.Right; var bottom = s.Bottom;

        if (_edges.HasFlag(Edge.Left)) left = pt.X;
        if (_edges.HasFlag(Edge.Right)) right = pt.X;
        if (_edges.HasFlag(Edge.Top)) top = pt.Y;
        if (_edges.HasFlag(Edge.Bottom)) bottom = pt.Y;

        if (AspectRatio <= 0)
            return new Rect(new Point(left, top), new Point(right, bottom)).Normalize();

        var horizontal = _edges.HasFlag(Edge.Left) || _edges.HasFlag(Edge.Right);
        var vertical = _edges.HasFlag(Edge.Top) || _edges.HasFlag(Edge.Bottom);

        if (horizontal && vertical)
        {
            // corner: the opposite corner stays fixed
            var anchor = new Point(
                _edges.HasFlag(Edge.Left) ? s.Right : s.Left,
                _edges.HasFlag(Edge.Top) ? s.Bottom : s.Top);
            return ConstrainFromAnchor(anchor, pt, AspectRatio);
        }

        // edge: the other dimension grows symmetrically around the centre
        var img = ImageSize;
        if (horizontal)
        {
            var fixedX = _edges.HasFlag(Edge.Left) ? s.Right : s.Left;
            var w = Math.Max(1, Math.Abs(pt.X - fixedX));
            var cy = s.Center.Y;
            var maxH = 2 * Math.Min(cy, img.Height - cy);
            var h = Math.Min(w / AspectRatio, maxH);
            w = h * AspectRatio;
            var x = pt.X < fixedX ? fixedX - w : fixedX;
            return new Rect(x, cy - h / 2, w, h);
        }
        else
        {
            var fixedY = _edges.HasFlag(Edge.Top) ? s.Bottom : s.Top;
            var h = Math.Max(1, Math.Abs(pt.Y - fixedY));
            var cx = s.Center.X;
            var maxW = 2 * Math.Min(cx, img.Width - cx);
            var w = Math.Min(h * AspectRatio, maxW);
            h = w / AspectRatio;
            var y = pt.Y < fixedY ? fixedY - h : fixedY;
            return new Rect(cx - w / 2, y, w, h);
        }
    }

    /// <summary>Rectangle from the anchor to the pointer, forced to the aspect ratio and kept inside the image.</summary>
    private Rect ConstrainFromAnchor(Point anchor, Point free, double ratio)
    {
        var img = ImageSize;
        var dirX = free.X >= anchor.X ? 1 : -1;
        var dirY = free.Y >= anchor.Y ? 1 : -1;
        var w = Math.Abs(free.X - anchor.X);
        var h = Math.Abs(free.Y - anchor.Y);

        if (h <= 0 || w / h > ratio) h = w / ratio;
        else w = h * ratio;

        var maxW = dirX > 0 ? img.Width - anchor.X : anchor.X;
        var maxH = dirY > 0 ? img.Height - anchor.Y : anchor.Y;
        if (w > maxW) { w = maxW; h = w / ratio; }
        if (h > maxH) { h = maxH; w = h * ratio; }

        var x = dirX > 0 ? anchor.X : anchor.X - w;
        var y = dirY > 0 ? anchor.Y : anchor.Y - h;
        return new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>Reshapes the existing selection to a new aspect ratio; the centre stays.</summary>
    private Rect ReshapeAroundCenter(Rect sel, double ratio)
    {
        var img = ImageSize;
        var c = sel.Center;
        var w = sel.Width;
        var h = w / ratio;
        var maxW = 2 * Math.Min(c.X, img.Width - c.X);
        var maxH = 2 * Math.Min(c.Y, img.Height - c.Y);
        if (h > maxH) { h = maxH; w = h * ratio; }
        if (w > maxW) { w = maxW; h = w / ratio; }
        return new Rect(c.X - w / 2, c.Y - h / 2, w, h);
    }

    // ------------------------------------------------------------ Rendering

    public override void Render(DrawingContext context)
    {
        if (_imageRect.Width <= 0) return;

        // Free rotation: image, checkerboard and image edge rotated around the centre; frame and grid stay upright
        var rotated = IsRotated && Source is not null;
        var turn = rotated ? RotationMatrix() : Matrix.Identity;
        Geometry imageArea = rotated
            ? new RectangleGeometry(_imageRect) { Transform = new MatrixTransform(turn) }
            : new RectangleGeometry(_imageRect);

        using (rotated ? context.PushTransform(turn) : (IDisposable?)null)
        {
            if (Source is { } bmp)
            {
                // Checkerboard below the image: visible only where the image is transparent
                // (e.g. the corners after a rounded crop).
                context.FillRectangle(CheckerBrush, _imageRect);
                DrawBitmap(context, bmp);
            }
            else
                context.FillRectangle(PlaceholderBrush, _imageRect);

            context.DrawRectangle(null, ImageEdgePen, _imageRect);
        }

        // Before/after only when not rotated; the divider belongs to the upright image
        if (!rotated && !QuadMode && !LinesMode && Source is not null && CompareSource is { } before)
            RenderCompare(context, before);

        if (rotated && DateTime.UtcNow < _gridUntil)
            RenderGrid(context, imageArea);

        if (!rotated) RenderPickMarkers(context);
        if (!rotated) RenderBrush(context);
        if (PickActive && _pickHover is { } hover && !rotated)
        {
            var r = WhiteBalance.SampleRadius(ImageSize.Width, ImageSize.Height);
            DrawLoupe(context, hover, toLoupe =>
            {
                // sampling area: the region that is averaged
                var box = new Rect(toLoupe(new Point(Math.Floor(hover.X) - r, Math.Floor(hover.Y) - r)),
                    toLoupe(new Point(Math.Floor(hover.X) + r + 1, Math.Floor(hover.Y) + r + 1)));
                context.DrawRectangle(null, QuadShadowPen, box);
                context.DrawRectangle(null, LoupeEdgePen, box);
            });
            return;
        }

        if (LinesMode)
        {
            RenderWarpLines(context);
            return;
        }

        if (QuadMode)
        {
            RenderQuad(context);
            return;
        }

        if (Selection is not { } sel) return;

        var s = ToScreen(sel);

        // Radius per axis: circular corners until the shorter side is used up;
        // after that they grow elliptically up to the full ellipse.
        var radius = Math.Max(0, SelectionRadius) * _scale;
        var rx = Math.Min(radius, s.Width / 2);
        var ry = Math.Min(radius, s.Height / 2);
        var rounded = rx > 0.5 && ry > 0.5;

        // Darken the outside: one area with a hole instead of four strips, because
        // strips leave light seams at fractional coordinates. With rounded
        // corners, the gussets outside the rounding are darkened too, because
        // exactly those become transparent later.
        var hole = rounded
            ? new RectangleGeometry(s) { RadiusX = rx, RadiusY = ry }
            : new RectangleGeometry(s);
        var shade = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { imageArea, hole },
        };
        context.DrawGeometry(ShadeBrush, null, shade);

        // rule-of-thirds grid
        for (var i = 1; i <= 2; i++)
        {
            var x = s.Left + s.Width * i / 3;
            var y = s.Top + s.Height * i / 3;
            context.DrawLine(ThirdsPen, new Point(x, s.Top), new Point(x, s.Bottom));
            context.DrawLine(ThirdsPen, new Point(s.Left, y), new Point(s.Right, y));
        }

        if (rounded)
        {
            // bounding frame thin (the handles stay on it), shape bold
            context.DrawRectangle(null, BoundsPen, s);
            context.DrawGeometry(null, FramePen, new RectangleGeometry(s) { RadiusX = rx, RadiusY = ry });
        }
        else
        {
            context.DrawRectangle(null, FramePen, s);
        }

        // 8 handles
        foreach (var hp in new[]
                 {
                     s.TopLeft, new Point(s.Center.X, s.Top), s.TopRight,
                     new Point(s.Left, s.Center.Y), new Point(s.Right, s.Center.Y),
                     s.BottomLeft, new Point(s.Center.X, s.Bottom), s.BottomRight,
                 })
        {
            var h = new Rect(hp.X - HandleSize / 2, hp.Y - HandleSize / 2, HandleSize, HandleSize);
            context.DrawRectangle(HandleFill, HandlePen, h);
        }

        // size label
        var text = new FormattedText(
            $"{(int)sel.Width} × {(int)sel.Height} px",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Brushes.White);
        var pad = new Thickness(7, 3);
        var box = new Size(text.Width + pad.Left + pad.Right, text.Height + pad.Top + pad.Bottom);
        var below = s.Bottom + 8 + box.Height <= Bounds.Height;
        var bx = Math.Clamp(s.Center.X - box.Width / 2, 2, Math.Max(2, Bounds.Width - box.Width - 2));
        var by = below ? s.Bottom + 8 : s.Bottom - box.Height - 8;
        context.DrawRectangle(LabelBack, null, new RoundedRect(new Rect(new Point(bx, by), box), 4));
        context.DrawText(text, new Point(bx + pad.Left, by + pad.Top));

        // Magnifier while drawing and while dragging corners and edges (not during free rotation, where the image is rotated)
        if (!rotated && _mode is DragMode.Create or DragMode.Resize)
        {
            // the dragged corner, or the point of the dragged edge under the pointer
            var at = _mode == DragMode.Create
                ? _dragImage
                : new Point(
                    _edges.HasFlag(Edge.Left) ? sel.Left : _edges.HasFlag(Edge.Right) ? sel.Right : Math.Clamp(_dragImage.X, sel.Left, sel.Right),
                    _edges.HasFlag(Edge.Top) ? sel.Top : _edges.HasFlag(Edge.Bottom) ? sel.Bottom : Math.Clamp(_dragImage.Y, sel.Top, sel.Bottom));
            DrawLoupe(context, at, toLoupe =>
            {
                // the frame edges in the magnifier
                var (tl, br) = (toLoupe(sel.TopLeft), toLoupe(sel.BottomRight));
                context.DrawRectangle(null, LoupeEdgePen, new Rect(tl, br));
            });
        }
    }
}
