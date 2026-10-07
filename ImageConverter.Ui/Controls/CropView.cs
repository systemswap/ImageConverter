using System;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace ImageConverter.Controls;

/// <summary>
/// Bildvorschau mit Referenzwerkzeug-artigem Auswahlrahmen.
///
/// Der Rahmen ist anfangs unsichtbar und entsteht erst, wenn man mit gedrückter
/// linker Maustaste über das Bild zieht. Danach: Ecken und Kantenmitten ziehen
/// = Größe ändern, Innenfläche ziehen = verschieben, Klick neben die Auswahl
/// oder Esc = Auswahl aufheben, Pfeiltasten = 1 px (Shift: 10 px) verschieben.
///
/// Die Auswahl wird in Bildpixeln gehalten, nicht in Bildschirmkoordinaten —
/// sonst verschiebt sie sich, sobald sich Fenstergröße oder Zoom ändern.
/// </summary>
public sealed class CropView : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<CropView, Bitmap?>(nameof(Source));

    /// <summary>Auswahl in Bildpixeln; null = keine Auswahl.</summary>
    public static readonly StyledProperty<Rect?> SelectionProperty =
        AvaloniaProperty.Register<CropView, Rect?>(nameof(Selection),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Seitenverhältnis Breite/Höhe; 0 = frei.</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<CropView, double>(nameof(AspectRatio));

    /// <summary>
    /// Eckenradius der Auswahl in Bildpixeln. Reicht bis zur halben längsten Seite:
    /// über die halbe kürzere Seite hinaus werden die Ecken elliptisch, am Maximum
    /// wird aus der Auswahl eine Ellipse — bei 1:1 ein Kreis.
    /// </summary>
    public static readonly StyledProperty<double> SelectionRadiusProperty =
        AvaloniaProperty.Register<CropView, double>(nameof(SelectionRadius));

    public double SelectionRadius
    {
        get => GetValue(SelectionRadiusProperty);
        set => SetValue(SelectionRadiusProperty, value);
    }

    /// <summary>Wird bei Enter oder Doppelklick in die Auswahl ausgelöst (wie in Referenzwerkzeug).</summary>
    public static readonly StyledProperty<ICommand?> CropCommandProperty =
        AvaloniaProperty.Register<CropView, ICommand?>(nameof(CropCommand));

    public ICommand? CropCommand
    {
        get => GetValue(CropCommandProperty);
        set => SetValue(CropCommandProperty, value);
    }

    private void RequestCrop()
    {
        if (CropCommand is { } cmd && cmd.CanExecute(null))
            cmd.Execute(null);
    }

    /// <summary>Bildgröße des Platzhalters, solange kein Bild geladen ist.</summary>
    public static readonly StyledProperty<PixelSize> PlaceholderSizeProperty =
        AvaloniaProperty.Register<CropView, PixelSize>(nameof(PlaceholderSize), new PixelSize(4000, 3000));

    public static readonly DirectProperty<CropView, int> DisplayPercentProperty =
        AvaloniaProperty.RegisterDirect<CropView, int>(nameof(DisplayPercent), o => o.DisplayPercent);

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

    /// <summary>Aktueller Anzeigemaßstab in Prozent (für die Zoom-Anzeige).</summary>
    public int DisplayPercent
    {
        get => _displayPercent;
        private set => SetAndRaise(DisplayPercentProperty, ref _displayPercent, value);
    }

    // ------------------------------------------------------------------ Zustand

    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private enum DragMode { None, Create, Move, Resize, Pan }

    // Oben mehr Luft: dort schwebt der Bedienhinweis über dem Bild.
    private static readonly Thickness ViewPadding = new(28, 50, 28, 28);
    private const double HandleSize = 9;
    private const double HandleHitRadius = 9;
    private const double CreateThreshold = 4;

    private DragMode _mode;
    private Edge _edges;
    private Point _pressScreen;
    private Point _anchorImage;
    private Rect _startSelection;
    private Vector _moveOffset;

    private Rect _imageRect;   // Bild auf dem Bildschirm
    private Rect _panStartRect;
    private bool _spaceHeld;   // Leertaste gedrückt = Hand-Werkzeug
    private double _scale = 1; // Bildschirm-px pro Bildpixel

    // ------------------------------------------------------------------ Optik

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
        AffectsRender<CropView>(SourceProperty, SelectionProperty, PlaceholderSizeProperty, SelectionRadiusProperty);
        FocusableProperty.OverrideDefaultValue<CropView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CropView>(true);
    }

    private PixelSize ImageSize => Source?.PixelSize ?? PlaceholderSize;

    // ------------------------------------------------------------ Geometrie

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty || change.Property == PlaceholderSizeProperty)
        {
            // Neues Bild (auch nach Zuschnitt/Rückgängig): wieder eingepasst anzeigen
            _fitMode = true;
            UpdateGeometry();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateGeometry();
        }

        if (change.Property == SourceProperty)
            Selection = null;

        if (change.Property == AspectRatioProperty && Selection is { } sel && AspectRatio > 0)
            Selection = Round(ReshapeAroundCenter(sel, AspectRatio));
    }

    // ------------------------------------------------------------ Zoom

    private const double MaxScale = 32;       // 3200 % — einzelne Pixel prüfen
    private const double WheelZoomStep = 1.2; // pro Rastung
    private const double WheelPanStep = 60;   // px pro Rastung beim Scrollen

    /// <summary>true = Bild wird ins Fenster eingepasst; false = freier Zoom.</summary>
    private bool _fitMode = true;

    private Rect ViewArea => new Rect(Bounds.Size).Deflate(ViewPadding);

    /// <summary>Maßstab, bei dem das Bild genau hineinpasst (kleine Bilder max. 100 %).</summary>
    private double FitScale
    {
        get
        {
            var size = ImageSize;
            var area = ViewArea;
            if (size.Width <= 0 || size.Height <= 0 || area.Width <= 0 || area.Height <= 0) return 1;
            return Math.Min(1.0, Math.Min(area.Width / size.Width, area.Height / size.Height));
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

        if (_fitMode || _scale <= FitScale)
        {
            _fitMode = true;
            _scale = FitScale;
            var w = size.Width * _scale;
            var h = size.Height * _scale;
            _imageRect = new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }
        else
        {
            // Fenstergröße geändert im Zoom: Maßstab halten, nur in den Rahmen zwingen
            _imageRect = ClampPan(_imageRect);
        }

        DisplayPercent = (int)Math.Round(_scale * 100);
    }

    /// <summary>Zoomt auf <paramref name="newScale"/>; der Bildpunkt unter <paramref name="anchor"/> bleibt stehen.</summary>
    private void ZoomAt(Point anchor, double newScale)
    {
        if (_imageRect.Width <= 0) return;

        var fit = FitScale;
        newScale = Math.Clamp(newScale, fit, MaxScale);
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

        DisplayPercent = (int)Math.Round(_scale * 100);
        InvalidateVisual();
    }

    /// <summary>Hält das Bild im Sichtbereich: kleiner als die Fläche = zentriert, größer = keine Lücken.</summary>
    private Rect ClampPan(Rect r)
    {
        var area = ViewArea;
        var x = r.Width <= area.Width
            ? area.X + (area.Width - r.Width) / 2
            : Math.Clamp(r.X, area.Right - r.Width, area.X);
        var y = r.Height <= area.Height
            ? area.Y + (area.Height - r.Height) / 2
            : Math.Clamp(r.Y, area.Bottom - r.Height, area.Y);
        return new Rect(x, y, r.Width, r.Height);
    }

    private void PanBy(Vector delta)
    {
        if (_fitMode) return;
        _imageRect = ClampPan(_imageRect.Translate(delta));
        InvalidateVisual();
    }

    /// <summary>Bild wieder ganz ins Fenster einpassen.</summary>
    public void FitToView()
    {
        _fitMode = true;
        UpdateGeometry();
        InvalidateVisual();
    }

    /// <summary>Originalgröße (100 %), um die Mitte der Ansicht.</summary>
    public void ZoomToActualSize() => ZoomAt(ViewArea.Center, 1.0);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_imageRect.Width <= 0) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Strg + Mausrad: zoomen um den Mauszeiger
            ZoomAt(e.GetPosition(this), _scale * Math.Pow(WheelZoomStep, e.Delta.Y));
            e.Handled = true;
        }
        else if (!_fitMode)
        {
            // Ohne Strg im Zoom: scrollen, mit Shift waagerecht
            var d = e.Delta * WheelPanStep;
            PanBy(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? new Vector(d.Y, d.X) : d);
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

    private static Rect Round(Rect r)
    {
        var x = Math.Round(r.X);
        var y = Math.Round(r.Y);
        return new Rect(x, y,
            Math.Max(1, Math.Round(r.Right) - x),
            Math.Max(1, Math.Round(r.Bottom) - y));
    }

    // ------------------------------------------------------------ Hit-Test

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

    // ------------------------------------------------------------ Maus

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        var p = e.GetPosition(this);

        // Verschieben im Zoom: mittlere Maustaste oder Leertaste + links
        if (!_fitMode && (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && _spaceHeld)))
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

        if (!props.IsLeftButtonPressed) return;

        Focus();
        _pressScreen = p;

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

        if (_imageRect.Contains(p))
        {
            // Neue Auswahl beginnt erst, wenn die Maus sich ein Stück bewegt —
            // ein reiner Klick hebt die bestehende Auswahl nur auf.
            _mode = DragMode.Create;
            _anchorImage = ClampToImage(ToImage(p));
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
                UpdateHoverCursor(p);
                return;

            case DragMode.Create:
            {
                var d = p - _pressScreen;
                if (Selection is null && Math.Abs(d.X) < CreateThreshold && Math.Abs(d.Y) < CreateThreshold)
                    return;
                var current = ClampToImage(ToImage(p));
                Selection = Round(AspectRatio > 0
                    ? ConstrainFromAnchor(_anchorImage, current, AspectRatio)
                    : new Rect(_anchorImage, current).Normalize());
                Cursor = CursorCross;
                break;
            }

            case DragMode.Move when Selection is { } sel:
            {
                var ip = ToImage(p);
                var x = Math.Clamp(ip.X - _moveOffset.X, 0, ImageSize.Width - sel.Width);
                var y = Math.Clamp(ip.Y - _moveOffset.Y, 0, ImageSize.Height - sel.Height);
                Selection = Round(new Rect(x, y, sel.Width, sel.Height));
                break;
            }

            case DragMode.Resize:
                Selection = Round(Resize(ClampToImage(ToImage(p))));
                break;

            case DragMode.Pan:
                _imageRect = ClampPan(_panStartRect.Translate(p - _pressScreen));
                InvalidateVisual();
                break;
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mode == DragMode.None) return;

        if (_mode == DragMode.Create)
        {
            var d = e.GetPosition(this) - _pressScreen;
            if (Math.Abs(d.X) < CreateThreshold && Math.Abs(d.Y) < CreateThreshold)
                Selection = null;
        }

        _mode = DragMode.None;
        e.Pointer.Capture(null);
        UpdateHoverCursor(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _mode = DragMode.None;
    }

    private void UpdateHoverCursor(Point p)
    {
        if (_spaceHeld && !_fitMode)
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
        Cursor = _imageRect.Contains(p) ? CursorCross : Cursor.Default;
    }

    // ------------------------------------------------------------ Tastatur

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Space)
        {
            _spaceHeld = true;
            if (!_fitMode) Cursor = CursorHand;
            e.Handled = true;
            return;
        }

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
            Selection = new Rect(x, y, sel.Width, sel.Height);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key != Key.Space) return;

        _spaceHeld = false;
        if (_mode != DragMode.Pan) Cursor = CursorCross;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _spaceHeld = false; // sonst "klebt" die Leertaste nach einem Fensterwechsel
    }

    // ------------------------------------------------------------ Rechnen

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
            // Ecke: gegenüberliegende Ecke bleibt fest
            var anchor = new Point(
                _edges.HasFlag(Edge.Left) ? s.Right : s.Left,
                _edges.HasFlag(Edge.Top) ? s.Bottom : s.Top);
            return ConstrainFromAnchor(anchor, pt, AspectRatio);
        }

        // Kante: andere Dimension wächst symmetrisch um die Mitte
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

    /// <summary>Rechteck vom Anker zur Maus, auf Seitenverhältnis gezwungen und im Bild gehalten.</summary>
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

    /// <summary>Bestehende Auswahl auf ein neues Verhältnis bringen, Mitte bleibt.</summary>
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

    // ------------------------------------------------------------ Zeichnen

    public override void Render(DrawingContext context)
    {
        if (_imageRect.Width <= 0) return;

        if (Source is { } bmp)
        {
            // Schachbrett unter dem Bild: sichtbar nur, wo das Bild transparent ist
            // (z. B. die Ecken nach einem gerundeten Zuschnitt).
            context.FillRectangle(CheckerBrush, _imageRect);

            // Ab 200 % Pixel hart zeigen (zum Prüfen), darunter weich interpolieren
            using (context.PushRenderOptions(new RenderOptions
                   {
                       BitmapInterpolationMode = _scale >= 2
                           ? BitmapInterpolationMode.None
                           : BitmapInterpolationMode.HighQuality,
                   }))
            {
                context.DrawImage(bmp, new Rect(bmp.Size), _imageRect);
            }
        }
        else
            context.FillRectangle(PlaceholderBrush, _imageRect);

        context.DrawRectangle(null, ImageEdgePen, _imageRect);

        if (Selection is not { } sel) return;

        var s = ToScreen(sel);
        var r = _imageRect;

        // Radius pro Achse: kreisrunde Ecken, bis die kürzere Seite aufgebraucht
        // ist; danach wachsen sie elliptisch weiter bis zur vollen Ellipse.
        var radius = Math.Max(0, SelectionRadius) * _scale;
        var rx = Math.Min(radius, s.Width / 2);
        var ry = Math.Min(radius, s.Height / 2);
        var rounded = rx > 0.5 && ry > 0.5;

        // Außenbereich abdunkeln: eine Fläche mit Loch statt vier Streifen —
        // Streifen hinterlassen bei krummen Koordinaten helle Nahtlinien.
        // Bei runden Ecken sind die Zwickel außerhalb der Rundung mit
        // abgedunkelt, denn genau die werden später transparent.
        var hole = rounded
            ? new RectangleGeometry(s) { RadiusX = rx, RadiusY = ry }
            : new RectangleGeometry(s);
        var shade = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(r), hole },
        };
        context.DrawGeometry(ShadeBrush, null, shade);

        // Drittel-Raster
        for (var i = 1; i <= 2; i++)
        {
            var x = s.Left + s.Width * i / 3;
            var y = s.Top + s.Height * i / 3;
            context.DrawLine(ThirdsPen, new Point(x, s.Top), new Point(x, s.Bottom));
            context.DrawLine(ThirdsPen, new Point(s.Left, y), new Point(s.Right, y));
        }

        if (rounded)
        {
            // Begrenzungsrahmen dünn (Griffe sitzen weiter darauf), Form kräftig
            context.DrawRectangle(null, BoundsPen, s);
            context.DrawGeometry(null, FramePen, new RectangleGeometry(s) { RadiusX = rx, RadiusY = ry });
        }
        else
        {
            context.DrawRectangle(null, FramePen, s);
        }

        // 8 Griffe
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

        // Maßanzeige
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
    }
}
