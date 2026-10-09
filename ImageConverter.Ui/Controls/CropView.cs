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

    /// <summary>
    /// Auswahlwerkzeug an (Vorgabe): Ziehen mit links zieht einen Ausschnitt auf. Aus: Ziehen mit links
    /// verschiebt das Bild (Hand). Strg + Ziehen verschiebt in beiden Fällen.
    /// </summary>
    public static readonly StyledProperty<bool> SelectionEnabledProperty =
        AvaloniaProperty.Register<CropView, bool>(nameof(SelectionEnabled), defaultValue: true);

    public bool SelectionEnabled
    {
        get => GetValue(SelectionEnabledProperty);
        set => SetValue(SelectionEnabledProperty, value);
    }

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

    /// <summary>Wird ausgelöst, wenn eine Geste am Rahmen endet (Maus losgelassen) — ein Rückgängig-Schritt.</summary>
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

    /// <summary>Echte Bildgröße in Pixeln; die Auswahl rechnet damit, auch wenn
    /// <see cref="Source"/> nur eine verkleinerte Vorschau ist.</summary>
    public static readonly StyledProperty<PixelSize> DocumentSizeProperty =
        AvaloniaProperty.Register<CropView, PixelSize>(nameof(DocumentSize));

    public PixelSize DocumentSize
    {
        get => GetValue(DocumentSizeProperty);
        set => SetValue(DocumentSizeProperty, value);
    }

    /// <summary>Jede Änderung passt die Ansicht neu ein (neues Bild, Zuschnitt …).</summary>
    /// <summary>
    /// Größe des Ergebnisses (Zielgröße). Darauf beziehen sich Zoom-Anzeige, „100 %"
    /// und Einpassen. Leer = wie <see cref="DocumentSize"/>.
    /// </summary>
    public static readonly StyledProperty<PixelSize> OutputSizeProperty =
        AvaloniaProperty.Register<CropView, PixelSize>(nameof(OutputSize));

    public PixelSize OutputSize
    {
        get => GetValue(OutputSizeProperty);
        set => SetValue(OutputSizeProperty, value);
    }

    /// <summary>Ergebnis-Pixel je Bild-Pixel (Breite): 800 aus 4000 → 0,2.</summary>
    private double OutputRatio =>
        OutputSize.Width > 0 && ImageSize.Width > 0 ? (double)OutputSize.Width / ImageSize.Width : 1;

    public static readonly StyledProperty<object?> ViewResetTokenProperty =
        AvaloniaProperty.Register<CropView, object?>(nameof(ViewResetToken));

    public object? ViewResetToken
    {
        get => GetValue(ViewResetTokenProperty);
        set => SetValue(ViewResetTokenProperty, value);
    }

    /// <summary>Bildgröße des Platzhalters, solange kein Bild geladen ist.</summary>
    public static readonly StyledProperty<PixelSize> PlaceholderSizeProperty =
        AvaloniaProperty.Register<CropView, PixelSize>(nameof(PlaceholderSize), new PixelSize(4000, 3000));

    public static readonly DirectProperty<CropView, int> DisplayPercentProperty =
        AvaloniaProperty.RegisterDirect<CropView, int>(nameof(DisplayPercent), o => o.DisplayPercent);

    /// <summary>
    /// Vorher-Bild für den Vergleich; null = kein Vergleich. Gesetzt, liegt es links der
    /// Trennlinie über <see cref="Source"/> (Nachher) — die Linie lässt sich mit der Maus
    /// über das Bild ziehen. Wie <see cref="Source"/> auf die Bildfläche gestreckt.
    /// </summary>
    public static readonly StyledProperty<Bitmap?> CompareSourceProperty =
        AvaloniaProperty.Register<CropView, Bitmap?>(nameof(CompareSource));

    public Bitmap? CompareSource
    {
        get => GetValue(CompareSourceProperty);
        set => SetValue(CompareSourceProperty, value);
    }

    /// <summary>Beschriftung der linken Seite beim Vergleich (übersetzt, aus der Oberfläche).</summary>
    public static readonly StyledProperty<string?> BeforeLabelProperty =
        AvaloniaProperty.Register<CropView, string?>(nameof(BeforeLabel));

    public string? BeforeLabel
    {
        get => GetValue(BeforeLabelProperty);
        set => SetValue(BeforeLabelProperty, value);
    }

    /// <summary>Beschriftung der rechten Seite beim Vergleich.</summary>
    public static readonly StyledProperty<string?> AfterLabelProperty =
        AvaloniaProperty.Register<CropView, string?>(nameof(AfterLabel));

    public string? AfterLabel
    {
        get => GetValue(AfterLabelProperty);
        set => SetValue(AfterLabelProperty, value);
    }

    /// <summary>Trennlinie als Anteil der Bildbreite (0 = alles Nachher, 1 = alles Vorher); bleibt beim Bildwechsel.</summary>
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

    /// <summary>Aktueller Anzeigemaßstab in Prozent (für die Zoom-Anzeige).</summary>
    public int DisplayPercent
    {
        get => _displayPercent;
        private set => SetAndRaise(DisplayPercentProperty, ref _displayPercent, value);
    }

    // ------------------------------------------------------------------ Zustand

    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private enum DragMode { None, Create, Move, Resize, Pan, Split }

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
        AffectsRender<CropView>(SourceProperty, SelectionProperty, PlaceholderSizeProperty, SelectionRadiusProperty,
            CompareSourceProperty, BeforeLabelProperty, AfterLabelProperty);
        FocusableProperty.OverrideDefaultValue<CropView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CropView>(true);
    }

    /// <summary>
    /// Bildgröße für Auswahl und Zoom. Die angezeigte Bitmap kann kleiner sein
    /// (Vorschau) — sie wird dann auf diese Fläche gestreckt.
    /// </summary>
    private PixelSize ImageSize =>
        DocumentSize is { Width: > 0, Height: > 0 } doc ? doc : Source?.PixelSize ?? PlaceholderSize;

    // ------------------------------------------------------------ Geometrie

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ViewResetTokenProperty)
        {
            // Neuer Bildstand (Laden, Zuschnitt, Drehen, Rückgängig): neu einpassen
            _fitMode = true;
            UpdateGeometry();
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty
                 || change.Property == SourceProperty
                 || change.Property == DocumentSizeProperty
                 || change.Property == OutputSizeProperty
                 || change.Property == PlaceholderSizeProperty)
        {
            // Nur die Vorschau-Bitmap gewechselt (z. B. Schärfe-Regler): Zoom und
            // Auswahl bleiben, wo sie sind.
            UpdateGeometry();
        }

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
            // Einpassen, aber das Ergebnis nicht über 100 % aufblasen
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

        DisplayPercent = (int)Math.Round(_scale / OutputRatio * 100);
    }

    /// <summary>Zoomt auf <paramref name="newScale"/>; der Bildpunkt unter <paramref name="anchor"/> bleibt stehen.</summary>
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
    public void ZoomToActualSize() => ZoomAt(ViewArea.Center, OutputRatio);

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

        // Verschieben im Zoom: mittlere Maustaste, Strg oder Leertaste + links, ohne Auswahlwerkzeug links allein
        var panKey = _spaceHeld || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!_fitMode && (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && panKey)))
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

        // Vergleich: die Trennlinie geht vor — sie liegt mitten im Bild
        if (OnSplitLine(p))
        {
            _mode = DragMode.Split;
            Cursor = CursorWestEast;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Auswahlwerkzeug aus: Ziehen verschiebt das Bild (nur im Zoom — eingepasst gibt es nichts zu schieben)
        if (!SelectionEnabled)
        {
            if (_fitMode) return;
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
                UpdateHoverCursor(p, e.KeyModifiers);
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

            case DragMode.Split:
                _split = Math.Clamp((p.X - _imageRect.X) / _imageRect.Width, 0, 1);
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

        var wasEdit = _mode is not (DragMode.Pan or DragMode.Split);
        _mode = DragMode.None;
        e.Pointer.Capture(null);
        UpdateHoverCursor(e.GetPosition(this), e.KeyModifiers);
        if (wasEdit && GestureCompletedCommand is { } done && done.CanExecute(null))
            done.Execute(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _mode = DragMode.None;
    }

    private void UpdateHoverCursor(Point p, KeyModifiers modifiers)
    {
        if ((_spaceHeld || modifiers.HasFlag(KeyModifiers.Control)) && !_fitMode)
        {
            Cursor = CursorHand;
            return;
        }

        if (OnSplitLine(p))
        {
            Cursor = CursorWestEast;
            return;
        }

        if (!SelectionEnabled)
        {
            Cursor = _fitMode ? Cursor.Default : CursorHand;
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

    // ------------------------------------------------------------ Vergleich

    private const double SplitHitRadius = 8;
    private const double SplitKnobRadius = 13;

    /// <summary>Trennlinie auf dem Bildschirm — innerhalb des sichtbaren Teils des Bildes.</summary>
    private double SplitX => _imageRect.X + _imageRect.Width * _split;

    /// <summary>Sichtbarer Teil des Bildes (im Zoom ragt es über die Ansicht hinaus).</summary>
    private Rect VisibleImage => _imageRect.Intersect(new Rect(Bounds.Size));

    /// <summary>Griff mittig auf der Linie, im sichtbaren Teil.</summary>
    private Point SplitKnob => new(SplitX, VisibleImage.Center.Y);

    private bool OnSplitLine(Point p) =>
        CompareSource is not null && VisibleImage.Width > 0
        && ((VisibleImage.Contains(p) && Math.Abs(p.X - SplitX) <= SplitHitRadius)
            || Point.Distance(p, SplitKnob) <= SplitKnobRadius + 2);

    private static readonly IPen SplitPen = new ImmutablePen(Brushes.White, 2);
    private static readonly IPen SplitShadowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 4);
    private static readonly IBrush KnobFill = new ImmutableSolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255));
    private static readonly IBrush KnobArrow = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0x30, 0x30, 0x30));

    /// <summary>Vorher links der Linie über das Nachher, dazu Linie, Griff und Beschriftung.</summary>
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

        // Griff: Kreis mit zwei Pfeilspitzen ◂ ▸
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

        // Beschriftung oben in den Ecken der jeweiligen Seite, nur wenn Platz ist
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
        // Nur auf der eigenen Seite der Linie
        if (alignRight ? bx < splitX + 4 : bx + box.Width > splitX - 4) return;
        context.DrawRectangle(LabelBack, null, new RoundedRect(new Rect(new Point(bx, anchor.Y), box), 4));
        context.DrawText(text, new Point(bx + pad.Left, anchor.Y + pad.Top));
    }

    /// <summary>Bitmap auf die Bildfläche gestreckt; ab 200 % Pixel hart (zum Prüfen), darunter weich.</summary>
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
        if (_mode != DragMode.Pan) Cursor = SelectionEnabled ? CursorCross : _fitMode ? Cursor.Default : CursorHand;
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
            DrawBitmap(context, bmp);
        }
        else
            context.FillRectangle(PlaceholderBrush, _imageRect);

        context.DrawRectangle(null, ImageEdgePen, _imageRect);

        if (Source is not null && CompareSource is { } before)
            RenderCompare(context, before);

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
