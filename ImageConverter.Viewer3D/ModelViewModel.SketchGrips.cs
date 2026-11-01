using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Viewer3D.Editing;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;

namespace ImageConverter.Viewer3D;

/// <summary>Editing: the frame around a drawn face with grips to resize it; corners in proportion with Shift.</summary>
public sealed partial class ModelViewModel
{
    /// <summary>A point of the drawn face in the model (from its two in-plane coordinates).</summary>
    private static Vector3 OnPlane(DrawnFace k, float a, float b)
    {
        var (ia, ib) = Others(k.Axis);
        Vector3 v = default;
        v[k.Axis] = k.Plane;
        v[ia] = a;
        v[ib] = b;
        return v;
    }

    /// <summary>
    /// Grips of the selected drawn face: rectangle corners 0–3 and edge middles 4–7 (low/high in the first direction,
    /// then the second), circle rim 8–11. Empty while drawing or without a selection.
    /// </summary>
    internal IReadOnlyList<(int Id, Vector3 At)> SketchGrips
    {
        get
        {
            if (!HasSketch || _sketch is not { } k) return [];
            if (k.Circle)
            {
                var r = Vector2.Distance(k.A, k.B);
                return [(8, OnPlane(k, k.A.X + r, k.A.Y)), (9, OnPlane(k, k.A.X - r, k.A.Y)), (10, OnPlane(k, k.A.X, k.A.Y + r)), (11, OnPlane(k, k.A.X, k.A.Y - r))];
            }
            var min = Vector2.Min(k.A, k.B);
            var max = Vector2.Max(k.A, k.B);
            var c = (min + max) / 2;
            return
            [
                (0, OnPlane(k, min.X, min.Y)), (1, OnPlane(k, max.X, min.Y)), (2, OnPlane(k, max.X, max.Y)), (3, OnPlane(k, min.X, max.Y)),
                (4, OnPlane(k, c.X, min.Y)), (5, OnPlane(k, max.X, c.Y)), (6, OnPlane(k, c.X, max.Y)), (7, OnPlane(k, min.X, c.Y)),
            ];
        }
    }

    /// <summary>The outline of the selected drawn face as a closed line in the model (for the frame).</summary>
    internal IReadOnlyList<Vector3> SketchOutline
    {
        get
        {
            if (!HasSketch || _sketch is not { } k) return [];
            if (!k.Circle)
            {
                var min = Vector2.Min(k.A, k.B);
                var max = Vector2.Max(k.A, k.B);
                return [OnPlane(k, min.X, min.Y), OnPlane(k, max.X, min.Y), OnPlane(k, max.X, max.Y), OnPlane(k, min.X, max.Y)];
            }
            var r = Vector2.Distance(k.A, k.B);
            return [.. Enumerable.Range(0, 64).Select(i => OnPlane(k, k.A.X + r * MathF.Cos(i * MathF.Tau / 64), k.A.Y + r * MathF.Sin(i * MathF.Tau / 64)))];
        }
    }

    /// <summary>The grip being dragged, the drawn face before, and whether the corner keeps its proportions.</summary>
    private (int Grip, DrawnFace Start, bool Proportional)? _gripDrag;

    /// <summary>
    /// Takes hold of a grip; <paramref name="proportional"/> (Shift at the start, corners only) stays for the whole drag.
    /// </summary>
    internal void BeginResize(int grip, bool proportional)
    {
        if (_sketch is not { } k) return;
        _gripDrag = (grip, k, proportional && grip < 4);
    }

    /// <summary>
    /// The grip follows the mouse (in-plane point <paramref name="p"/>); moved edges dock onto walls and the edges of
    /// faces within <paramref name="range"/>. A proportional corner keeps the width to height ratio; when one of its
    /// edges docks, the other follows in proportion.
    /// </summary>
    internal void DragResize(Vector2 p, float range)
    {
        if (_gripDrag is not { } r || _sketch is null) return;
        var k0 = r.Start;
        var (ia, ib) = Others(k0.Axis);
        var minSize = 0.01f * UnitFactor;
        if (k0.Circle)
        {
            // radius to the mouse; the rim docks onto a wall in either direction
            var free = MathF.Max(minSize, Vector2.Distance(p, k0.A));
            float? DockedRadius()
            {
                foreach (var (axis, centre) in new[] { (ia, k0.A.X), (ib, k0.A.Y) })
                    foreach (var sign in new[] { 1f, -1f })
                        if (NearestLine(axis, centre + sign * free, range) is { } line && MathF.Abs(line - centre) > minSize)
                            return MathF.Abs(line - centre);
                return null;
            }
            var rad = DockedRadius() ?? Round(new Vector2(free, 0)).X;
            _sketch = k0 with { B = k0.A + new Vector2(rad, 0) };
            WriteSketchSize();
            UpdateSketchPreview();
            return;
        }

        var min = Vector2.Min(k0.A, k0.B);
        var max = Vector2.Max(k0.A, k0.B);
        // which sides the grip moves: −1 low, +1 high, 0 none
        var (sx, sy) = r.Grip switch
        {
            0 => (-1, -1), 1 => (1, -1), 2 => (1, 1), 3 => (-1, 1),
            4 => (0, -1), 5 => (1, 0), 6 => (0, 1), _ => (-1, 0),
        };
        float Docked(int axis, float value)
        {
            var line = NearestLine(axis, value, range);
            return line ?? Round(new Vector2(value, 0)).X;
        }
        var newMin = min;
        var newMax = max;
        if (r.Proportional)
        {
            // keep the ratio: the larger relative pull decides, a docked edge wins
            var fixedCorner = new Vector2(sx < 0 ? max.X : min.X, sy < 0 ? max.Y : min.Y);
            var w0 = max.X - min.X;
            var h0 = max.Y - min.Y;
            var scale = MathF.Max(MathF.Abs(p.X - fixedCorner.X) / w0, MathF.Abs(p.Y - fixedCorner.Y) / h0);
            var x = fixedCorner.X + sx * w0 * scale;
            var y = fixedCorner.Y + sy * h0 * scale;
            if (NearestLine(ia, x, range) is { } lx) scale = MathF.Abs(lx - fixedCorner.X) / w0;
            else if (NearestLine(ib, y, range) is { } ly) scale = MathF.Abs(ly - fixedCorner.Y) / h0;
            scale = MathF.Max(scale, minSize / MathF.Min(w0, h0));
            x = fixedCorner.X + sx * w0 * scale;
            y = fixedCorner.Y + sy * h0 * scale;
            (newMin.X, newMax.X) = sx < 0 ? (x, max.X) : (min.X, x);
            (newMin.Y, newMax.Y) = sy < 0 ? (y, max.Y) : (min.Y, y);
        }
        else
        {
            if (sx < 0) newMin.X = MathF.Min(Docked(ia, p.X), max.X - minSize);
            if (sx > 0) newMax.X = MathF.Max(Docked(ia, p.X), min.X + minSize);
            if (sy < 0) newMin.Y = MathF.Min(Docked(ib, p.Y), max.Y - minSize);
            if (sy > 0) newMax.Y = MathF.Max(Docked(ib, p.Y), min.Y + minSize);
        }
        _sketch = k0 with { A = newMin, B = newMax };
        WriteSketchSize();
        UpdateSketchPreview();
        RefreshDimension();
    }

    internal void EndResize() => _gripDrag = null;
}
