using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Controls;

/// <summary>
/// Eine nachgezeichnete Linie im Werkzeug „Gewölbtes Blatt“: Punkte in Bildpixeln der Ansicht, Richtung auf dem Blatt.
/// <paramref name="KindChosen"/>: Richtung von Hand gewählt (Rechtsklick) — sonst wird sie aus dem Verlauf geraten.
/// <paramref name="Suggested"/>: automatisch vorgeschlagen (gestrichelt); ein Klick wählt ihn ab oder wieder an
/// (<paramref name="Enabled"/>), wer einen Punkt zieht, macht ihn zur normalen Linie.
/// </summary>
public sealed record WarpLineShape(WarpLineKind Kind, IReadOnlyList<Point> Points, bool KindChosen = false,
    bool Suggested = false, bool Enabled = true)
{
    public bool Equals(WarpLineShape? other) =>
        other is not null && Kind == other.Kind && KindChosen == other.KindChosen && Suggested == other.Suggested
        && Enabled == other.Enabled && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Kind, KindChosen, Suggested, Enabled, Points.Count);

    /// <summary>Zählt für das Geradeziehen: mindestens zwei Punkte und nicht abgewählt.</summary>
    public bool Counts => Enabled && Points.Count >= 2;

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

    /// <summary>Ein Vorschlag wird zur normalen Linie (angewählt, Richtung bleibt).</summary>
    public WarpLineShape Adopted() => this with { Suggested = false, Enabled = true, KindChosen = true };
}
