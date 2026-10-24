using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Controls;

/// <summary>
/// A traced line in the curved-page tool: points in image pixels of the view, plus the line's direction on the page.
/// </summary>
/// <param name="Kind">Direction of the line on the page.</param>
/// <param name="Points">Traced points in image pixels of the view.</param>
/// <param name="KindChosen">The direction was chosen by hand (right-click); otherwise it is guessed from the course of
/// the line.</param>
/// <param name="Suggested">Suggested automatically (drawn dashed). Any change (dragging a point, continuing the line,
/// direction, arrow keys) turns it into a user line, and "Suggest" replaces only untouched suggestions.</param>
public sealed record WarpLineShape(WarpLineKind Kind, IReadOnlyList<Point> Points, bool KindChosen = false,
    bool Suggested = false)
{
    public bool Equals(WarpLineShape? other) =>
        other is not null && Kind == other.Kind && KindChosen == other.KindChosen && Suggested == other.Suggested
        && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Kind, KindChosen, Suggested, Points.Count);

    /// <summary>Whether the line is used for straightening, i.e. it has at least two points.</summary>
    public bool Counts => Points.Count >= 2;

    /// <summary>
    /// Guesses the direction from the course of the line: wider than tall = horizontal, otherwise vertical (a fold is
    /// only set by hand).
    /// </summary>
    /// <returns>This line unchanged if the direction was chosen by hand or it has fewer than two points; otherwise a
    /// copy with the guessed direction.</returns>
    public WarpLineShape WithGuessedKind()
    {
        if (KindChosen || Points.Count < 2) return this;
        var dx = Points.Max(p => p.X) - Points.Min(p => p.X);
        var dy = Points.Max(p => p.Y) - Points.Min(p => p.Y);
        return this with { Kind = dx >= dy ? WarpLineKind.Horizontal : WarpLineKind.Vertical };
    }

    /// <summary>Cycles the direction on right-click: horizontal → vertical → fold → horizontal.</summary>
    /// <returns>A copy with the next direction, marked as chosen by hand.</returns>
    public WarpLineShape NextKind() => this with
    {
        Kind = Kind switch
        {
            WarpLineKind.Horizontal => WarpLineKind.Vertical,
            WarpLineKind.Vertical => WarpLineKind.Fold,
            _ => WarpLineKind.Horizontal,
        },
        KindChosen = true,
    };

    /// <summary>Turns a suggestion into a user line; the direction is kept.</summary>
    /// <returns>A copy that is no longer a suggestion and whose direction counts as chosen by hand.</returns>
    public WarpLineShape Adopted() => this with { Suggested = false, KindChosen = true };

    /// <summary>
    /// Determines whether this line lies (almost) on <paramref name="other"/>: at least 60 % of the points of one line
    /// are at most <paramref name="tolerance"/> pixels from the path of the other. Checked in both directions so that a
    /// short user line on a long suggestion also counts.
    /// </summary>
    /// <param name="other">The line to compare with.</param>
    /// <param name="tolerance">Maximum distance in image pixels.</param>
    /// <returns><c>true</c> if the lines overlap; <c>false</c> if they do not or either has fewer than two points.</returns>
    public bool Overlaps(WarpLineShape other, double tolerance)
    {
        if (Points.Count < 2 || other.Points.Count < 2) return false;
        static double Near(IReadOnlyList<Point> pts, IReadOnlyList<Point> path, double tol) =>
            pts.Count(p => DistanceToPath(p, path) <= tol) / (double)pts.Count;
        return Near(Points, other.Points, tolerance) >= 0.6 || Near(other.Points, Points, tolerance) >= 0.6;
    }

    private static double DistanceToPath(Point p, IReadOnlyList<Point> path)
    {
        var best = double.MaxValue;
        for (var i = 0; i + 1 < path.Count; i++)
        {
            Point a = path[i], b = path[i + 1];
            var ab = b - a;
            var len2 = ab.X * ab.X + ab.Y * ab.Y;
            var t = len2 < 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
            best = Math.Min(best, Point.Distance(p, a + ab * t));
        }
        return best;
    }
}
