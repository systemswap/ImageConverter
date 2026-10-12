using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Controls;

/// <summary>
/// Eine nachgezeichnete Linie im Werkzeug „Gewölbtes Blatt“: Punkte in Bildpixeln der Ansicht, Richtung auf dem Blatt.
/// <paramref name="KindChosen"/>: Richtung von Hand gewählt (Rechtsklick) — sonst wird sie aus dem Verlauf geraten.
/// </summary>
public sealed record WarpLineShape(WarpLineKind Kind, IReadOnlyList<Point> Points, bool KindChosen = false)
{
    public bool Equals(WarpLineShape? other) =>
        other is not null && Kind == other.Kind && KindChosen == other.KindChosen && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Kind, KindChosen, Points.Count);

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
}
