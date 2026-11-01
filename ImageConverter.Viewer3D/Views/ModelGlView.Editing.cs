using System.Numerics;
using Avalonia;
using Avalonia.Input;

namespace ImageConverter.Viewer3D.Views;

/// <summary>
/// Mouse input of the editing tools: moving a face, drawing a face, its grips and dimensions, moving a drawn face and
/// dragging a body. The handlers of the view ask these methods first; whatever they do not take, turns and pans the view.
/// </summary>
internal sealed partial class ModelGlView
{
    /// <summary>What a press with the left button started.</summary>
    private enum EditGesture
    {
        None,
        FaceMove,
        Drawing,
        Resize,
        SketchMove,
        Body,
    }

    private EditGesture _gesture;
    private Vector3 _faceGrab;
    private Vector2 _sketchGrab;
    private bool _overGrip;

    /// <summary>A left press (without Ctrl): true if an editing tool took it.</summary>
    private bool EditPress(PointerPressedEventArgs e)
    {
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var (o, d) = Camera.Ray((float)_pressAt.X, (float)_pressAt.Y);
        if (_model.PushTool)
        {
            if (_model.HitTestWithNormal(o, d) is { } hit && _model.BeginPush(hit.Face, Outward(hit.Normal, d)))
            {
                _gesture = EditGesture.FaceMove;
                _faceGrab = hit.Point.ToVector3();
            }
            return true;
        }
        if (_model.SketchTool)
        {
            if (_model.HitTestWithNormal(o, d) is { } hit
                && _model.BeginSketch(hit.Point.ToVector3(), hit.Face, Outward(hit.Normal, d), !shift, SnapRange(hit.Point.ToVector3())))
                _gesture = EditGesture.Drawing;
            return true;
        }
        if (_model.DimensionStep == 1)
        {
            // Inside the drawn face counts as its height unless the click is right on an edge (narrow faces are only a few
            // pixels wide); outside, the nearest edge within 12 pixels (a circle: its centre)
            var edges = _model.SketchEdges;
            var inside = _model.SketchContains(o, d);
            if (NearestSegment(_pressAt, [.. edges.Select(x => (x.A, x.B))], inside ? 3 : 12) is { } i) _model.PickSketchEdge(edges[i].Edge);
            else if (inside) _model.PickSketchEdge(5);
            return true;
        }
        if (_model.DimensionStep == 2)
        {
            var hit = _model.HitTestWithNormal(o, d);
            _model.PickReference(hit is { } h ? (h.Point.ToVector3(), h.Face, Outward(h.Normal, d)) : null, SharpEdgeAt(_pressAt, hit?.Point.ToVector3()));
            return true;
        }
        if (GripAt?.Invoke(_pressAt) is { } grip && _model.SketchPlanePoint(o, d) is not null)
        {
            // Shift at the start keeps a corner in proportion for the whole drag
            _model.BeginResize(grip, shift);
            _gesture = EditGesture.Resize;
            return true;
        }
        if (_model.SketchAt(o, d) is { } sketch) _model.SelectedSketch = sketch;
        if (_model.SketchContains(o, d) && _model.SketchPlanePoint(o, d) is { } grab)
        {
            _sketchGrab = grab;
            _model.BeginSketchMove();
            _gesture = EditGesture.SketchMove;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            return true;
        }
        if (_model.HitsBody(o, d))
        {
            _gesture = EditGesture.Body;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            return true;
        }
        return false;
    }

    /// <summary>The mouse moved with the button down during an editing gesture.</summary>
    private void EditMove(Point pos, KeyModifiers keys)
    {
        var shift = keys.HasFlag(KeyModifiers.Shift);
        var (o, d) = Camera.Ray((float)pos.X, (float)pos.Y);
        switch (_gesture)
        {
            case EditGesture.FaceMove:
                if (Distance(pos, _pressAt) >= DragThreshold && _model.PushAxis is { } axis)
                {
                    // Along the normal through the grabbed point: where the mouse ray passes closest to that line
                    var n = axis.Normal;
                    var w = _faceGrab - o;
                    var b = Vector3.Dot(n, d);
                    var denominator = 1 - b * b;
                    double along = denominator > 1e-4f
                        ? (b * Vector3.Dot(d, w) - Vector3.Dot(n, w)) / denominator
                        : -(pos.Y - _pressAt.Y) * Camera.UnitsPerPixel(_faceGrab);   // looking along the normal: up = out
                    _model.DragPush(along, SnapRange(_faceGrab));
                }
                break;
            case EditGesture.Drawing:
                _model.DragSketch(o, d, !shift, SnapRange(_model.BodyCentreOrSketch));
                break;
            case EditGesture.Resize:
                if (_model.SketchPlanePoint(o, d) is { } at) _model.DragResize(at, SnapRange(_model.BodyCentreOrSketch));
                break;
            case EditGesture.SketchMove:
                if (_model.SketchPlanePoint(o, d) is { } to) _model.DragSketchMove(to - _sketchGrab, !shift, SnapRange(_model.BodyCentreOrSketch));
                break;
            case EditGesture.Body:
                if (shift)
                {
                    // free: parallel to the screen at the depth of the body
                    var k = Camera.UnitsPerPixel(_model.BodyCentre);
                    _model.MoveBody(Camera.Right * ((float)(pos.X - _last.X) * k) - Camera.Up * ((float)(pos.Y - _last.Y) * k));
                }
                else if (_model.HitTestWithNormal(o, d) is { } hit)
                    _model.DockBody(hit.Point.ToVector3(), Outward(hit.Normal, d), SnapRange(hit.Point.ToVector3()));
                break;
        }
        _last = pos;
    }

    /// <summary>The button went up: ends the gesture; a face that was dragged is moved now.</summary>
    private void EditRelease(bool moved)
    {
        switch (_gesture)
        {
            case EditGesture.FaceMove when moved:
                _ = _model.EndPush();
                break;
            case EditGesture.Drawing:
                _model.EndSketch();
                break;
            case EditGesture.Resize:
                _model.EndResize();
                break;
        }
        _gesture = EditGesture.None;
    }

    /// <summary>The mouse moves without a button: highlights the face to move, or shows the resize cursor over a grip.</summary>
    private void EditHover(Point pos)
    {
        if (_model.PushTool)
        {
            var (o, d) = Camera.Ray((float)pos.X, (float)pos.Y);
            _model.Hover(o, d);
            return;
        }
        if (_model.SketchTool) return;
        var overGrip = GripAt?.Invoke(pos) is not null;
        if (overGrip == _overGrip) return;
        _overGrip = overGrip;
        Cursor = overGrip ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
    }

    /// <summary>
    /// A sharp edge of the model within 8 pixels of a click, if the visible surface reaches it there (an edge hidden
    /// behind a wall does not count); with nothing under the mouse, an edge on the outline.
    /// </summary>
    private (Vector3 A, Vector3 B)? SharpEdgeAt(Point at, Vector3? surface)
    {
        if (_model.Scene.SharpEdges is not { } sharp) return null;
        var pos = _model.Scene.Positions;
        Vector3 V(int i) => new(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);
        var segments = new List<(Vector3, Vector3)>(sharp.Length / 2);
        for (var i = 0; i + 1 < sharp.Length; i += 2) segments.Add((V(sharp[i]), V(sharp[i + 1])));
        if (NearestSegment(at, segments, 8) is not { } j) return null;
        var (a, b) = segments[j];
        if (surface is not { } p) return (a, b);
        var ab = b - a;
        var t = ab.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
        return Vector3.Distance(p, a + ab * t) <= 10 * Camera.UnitsPerPixel(p) ? (a, b) : null;
    }

    /// <summary>Snapping reach: 8 pixels at the depth of <paramref name="at"/>, in model units.</summary>
    private float SnapRange(Vector3 at) => 8 * Camera.UnitsPerPixel(at);

    /// <summary>Index of the segment nearest a point on screen, if it is within <paramref name="pixels"/>.</summary>
    private int? NearestSegment(Point at, IReadOnlyList<(Vector3 A, Vector3 B)> segments, double pixels)
    {
        int? best = null;
        var bestDistance = pixels;
        for (var i = 0; i < segments.Count; i++)
        {
            var (ax, ay, av) = Camera.Project(segments[i].A);
            var (bx, by, bv) = Camera.Project(segments[i].B);
            if (!av || !bv) continue;
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            var t = len2 > 0 ? Math.Clamp(((at.X - ax) * dx + (at.Y - ay) * dy) / len2, 0, 1) : 0;
            var dist = Math.Sqrt(Math.Pow(at.X - (ax + t * dx), 2) + Math.Pow(at.Y - (ay + t * dy), 2));
            if (dist < bestDistance) (best, bestDistance) = (i, dist);
        }
        return best;
    }

    /// <summary>A face normal turned towards the viewer: out of the material on the side that is seen.</summary>
    private static Vector3 Outward(Vector3 normal, Vector3 ray) => Vector3.Dot(normal, ray) > 0 ? -normal : normal;
}
