using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ImageConverter.Viewer3D.Geometry;

namespace ImageConverter.Viewer3D.Views;

/// <summary>
/// Dimension lines and labels over the 3D view, drawn in 2D from the projected 3D points, so that they follow every
/// turn and zoom. Lets mouse input through to the view; the view asks <see cref="LabelAt"/> whether a click hit a
/// label (which removes that measurement).
/// </summary>
internal sealed class MeasureOverlay : Control
{
    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Regular = new(FontFamily.Default);

    private readonly ModelViewModel _model;
    private readonly ModelGlView _view;
    private readonly List<(Rect Box, MeasurementItem Item)> _labels = [];

    public MeasureOverlay(ModelViewModel model, ModelGlView view)
    {
        _model = model;
        _view = view;
        IsHitTestVisible = false;
        ClipToBounds = true;
        view.ViewChanged += InvalidateVisual;
        model.Measurements.CollectionChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The measurement whose label lies under a point of the view, or null.</summary>
    public MeasurementItem? LabelAt(Point p)
    {
        for (var i = _labels.Count - 1; i >= 0; i--)
            if (_labels[i].Box.Inflate(2).Contains(p)) return _labels[i].Item;
        return null;
    }

    public override void Render(DrawingContext dc)
    {
        _labels.Clear();
        var cam = _view.Camera;
        if (_view.Failure is not null) return;

        foreach (var m in _model.Measurements)
        {
            var colour = m.Colour;
            var pen = new Pen(new SolidColorBrush(colour), 2);
            Point labelAt;
            if (m.Result is { HasLine: true } r)
            {
                if (!TryProject(cam, r.From, out var a) || !TryProject(cam, r.To, out var b)) continue;
                DrawDimension(dc, pen, a, b);
                labelAt = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            }
            else
            {
                // Angle or touching faces: a thin dashed link between the two clicks, the value in the middle
                if (!TryProject(cam, m.A.Point, out var a) || !TryProject(cam, m.B.Point, out var b)) continue;
                var dash = new Pen(new SolidColorBrush(colour, 0.8), 1.5, new DashStyle([4, 3], 0));
                dc.DrawLine(dash, a, b);
                Dot(dc, colour, a);
                Dot(dc, colour, b);
                labelAt = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            }
            var box = Label(dc, colour, labelAt, m.Label, m.SubLabel, closable: true);
            _labels.Add((box, m));
        }

        // The open first pick: a dot, and the diameter of a cylinder
        if (_model.Pending is { } p && TryProject(cam, p.Point, out var at))
        {
            Dot(dc, _model.NextColour, at, 4.5);
            var values = _model.PendingValues();
            if (values.Count > 0)
                Label(dc, _model.NextColour, new Point(at.X, at.Y - 26), MeasureText.Short(values[0], _model.Inches), "", closable: false);
        }

        // Pivot of the turn in progress
        if (_view.TurningAround is { } pivot && TryProject(cam, D3.From(pivot), out var c))
        {
            var ring = new Pen(new SolidColorBrush(Colors.White, 0.9), 1.5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 40, 110, 230)), ring, c, 5, 5);
        }
    }

    private static bool TryProject(Rendering.OrbitCamera cam, D3 p, out Point screen)
    {
        var (x, y, visible) = cam.Project(p.ToVector3());
        screen = new Point(x, y);
        return visible && float.IsFinite(x) && float.IsFinite(y) && Math.Abs(x) < 1e6 && Math.Abs(y) < 1e6;
    }

    /// <summary>Line with arrow heads pointing outwards to both ends, and small dots at the ends.</summary>
    private static void DrawDimension(DrawingContext dc, Pen pen, Point a, Point b)
    {
        dc.DrawLine(pen, a, b);
        var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        var colour = ((SolidColorBrush)pen.Brush!).Color;
        if (len > 24)
        {
            var ux = (b.X - a.X) / len;
            var uy = (b.Y - a.Y) / len;
            Arrow(dc, colour, b, ux, uy);
            Arrow(dc, colour, a, -ux, -uy);
        }
        Dot(dc, colour, a, 2.5);
        Dot(dc, colour, b, 2.5);
    }

    private static void Arrow(DrawingContext dc, Color colour, Point tip, double ux, double uy)
    {
        const double length = 9, half = 4;
        var bx = tip.X - ux * length;
        var by = tip.Y - uy * length;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(new Point(bx - uy * half, by + ux * half));
            g.LineTo(new Point(bx + uy * half, by - ux * half));
            g.EndFigure(true);
        }
        dc.DrawGeometry(new SolidColorBrush(colour), null, geometry);
    }

    private static void Dot(DrawingContext dc, Color colour, Point p, double r = 3.5) =>
        dc.DrawEllipse(new SolidColorBrush(colour), new Pen(Brushes.White, 1.2), p, r, r);

    /// <summary>Rounded label in the measurement's colour with white text; returns its box.</summary>
    private static Rect Label(DrawingContext dc, Color colour, Point centre, string main, string sub, bool closable)
    {
        var culture = CultureInfo.CurrentCulture;
        var mainText = new FormattedText(main, culture, FlowDirection.LeftToRight, Bold, 13, Brushes.White);
        var subText = sub.Length > 0 ? new FormattedText(sub, culture, FlowDirection.LeftToRight, Regular, 11, Brushes.White) : null;
        var close = closable ? new FormattedText("✕", culture, FlowDirection.LeftToRight, Regular, 10, new SolidColorBrush(Colors.White, 0.8)) : null;
        var width = Math.Max(mainText.Width, subText?.Width ?? 0) + (close is not null ? close.Width + 8 : 0);
        var height = mainText.Height + (subText?.Height ?? 0);
        var box = new Rect(centre.X - width / 2 - 8, centre.Y - height / 2 - 4, width + 16, height + 8);
        dc.DrawRectangle(new SolidColorBrush(colour, 0.92), new Pen(new SolidColorBrush(Colors.White, 0.6), 1), box, 7, 7);
        dc.DrawText(mainText, new Point(box.X + 8, box.Y + 4));
        if (subText is not null) dc.DrawText(subText, new Point(box.X + 8, box.Y + 4 + mainText.Height));
        if (close is not null) dc.DrawText(close, new Point(box.Right - 8 - close.Width, box.Y + 5));
        return box;
    }
}
