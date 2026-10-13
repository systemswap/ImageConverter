using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Controls;

/// <summary>
/// Eine nachgezeichnete Linie im Werkzeug „Gewölbtes Blatt“: Punkte in Bildpixeln der Ansicht, Richtung auf dem Blatt.
/// <paramref name="KindChosen"/>: Richtung von Hand gewählt (Rechtsklick) — sonst wird sie aus dem Verlauf geraten.
/// <paramref name="Suggested"/>: automatisch vorgeschlagen (gestrichelt); jede Änderung (Punkt ziehen, weiterzeichnen,
/// Richtung, Pfeiltasten) macht ihn zur eigenen Linie, und „Vorschlagen“ ersetzt nur unberührte Vorschläge.
/// </summary>
public sealed record WarpLineShape(WarpLineKind Kind, IReadOnlyList<Point> Points, bool KindChosen = false,
    bool Suggested = false)
{
    public bool Equals(WarpLineShape? other) =>
        other is not null && Kind == other.Kind && KindChosen == other.KindChosen && Suggested == other.Suggested
        && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Kind, KindChosen, Suggested, Points.Count);

    /// <summary>Zählt für das Geradeziehen: mindestens zwei Punkte.</summary>
    public bool Counts => Points.Count >= 2;

    /// <summary>Richtung aus dem Verlauf: eher quer = waagerecht, eher hoch = senkrecht (ein Falz nur von Hand).</summary>
    public WarpLineShape WithGuessedKind()
    {
        if (KindChosen || Points.Count < 2) return this;
        var dx = Points.Max(p => p.X) - Points.Min(p => p.X);
        var dy = Points.Max(p => p.Y) - Points.Min(p => p.Y);
        return this with { Kind = dx >= dy ? WarpLineKind.Horizontal : WarpLineKind.Vertical };
    }

    /// <summary>Rechtsklick: waagerecht → senkrecht → Falz → waagerecht.</summary>
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

    /// <summary>Ein Vorschlag wird zur eigenen Linie (Richtung bleibt).</summary>
    public WarpLineShape Adopted() => this with { Suggested = false, KindChosen = true };

    /// <summary>
    /// Liegt diese Linie (fast) auf <paramref name="other"/>? Mindestens 60 % der Punkte der einen höchstens
    /// <paramref name="tolerance"/> Pixel vom Verlauf der anderen — in einer der beiden Richtungen, damit auch eine kurze
    /// eigene Linie auf einem langen Vorschlag zählt.
    /// </summary>
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
