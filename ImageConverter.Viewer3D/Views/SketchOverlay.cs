using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ImageConverter.Viewer3D.Views;

/// <summary>
/// The frame around the selected drawn face with its grips, drawn in 2D over the 3D view so that the grips keep their
/// size at every zoom. Lets the mouse through; the view asks <see cref="GripAt"/> whether a press hit a grip.
/// </summary>
internal sealed class SketchOverlay : Control
{
    /// <summary>Half the side of a grip square in pixels.</summary>
    private const double Half = 5;

    private static readonly IBrush Fill = Brushes.White;
    private static readonly IPen Frame = new Pen(new SolidColorBrush(Color.Parse("#3D7BEA")), 1.5, new DashStyle([4, 3], 0));
    private static readonly IPen GripPen = new Pen(new SolidColorBrush(Color.Parse("#3D7BEA")), 1.5);

    private readonly ModelViewModel _model;
    private readonly ModelGlView _view;

    public SketchOverlay(ModelViewModel model, ModelGlView view)
    {
        _model = model;
        _view = view;
        IsHitTestVisible = false;
        ClipToBounds = true;
        view.ViewChanged += InvalidateVisual;
    }

    /// <summary>The grip under a point of the view (within a few pixels), or null.</summary>
    public int? GripAt(Point p)
    {
        foreach (var (id, at) in _model.SketchGrips)
        {
            var (x, y, visible) = _view.Camera.Project(at);
            if (visible && Math.Abs(x - p.X) <= Half + 3 && Math.Abs(y - p.Y) <= Half + 3) return id;
        }
        return null;
    }

    public override void Render(DrawingContext dc)
    {
        var outline = _model.SketchOutline;
        if (outline.Count < 2 || _view.Failure is not null) return;
        var cam = _view.Camera;
        Point? P(Vector3 v) => cam.Project(v) is (var x, var y, true) ? new Point(x, y) : null;
        for (var i = 0; i < outline.Count; i++)
            if (P(outline[i]) is { } a && P(outline[(i + 1) % outline.Count]) is { } b) dc.DrawLine(Frame, a, b);
        foreach (var (_, at) in _model.SketchGrips)
            if (P(at) is { } g) dc.DrawRectangle(Fill, GripPen, new Rect(g.X - Half, g.Y - Half, 2 * Half, 2 * Half), 1.5, 1.5);
    }
}
