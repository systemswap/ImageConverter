namespace ImageConverter.Core.Imaging;

/// <summary>Was eine nachgezeichnete Linie auf dem Blatt ist.</summary>
public enum WarpLineKind
{
    /// <summary>Auf dem Blatt waagerecht: Textzeile, Ober- oder Unterkante — wird durch die Wölbung zum Bogen.</summary>
    Horizontal,
    /// <summary>Auf dem Blatt senkrecht: Seitenkante, Spalte, Tabellenlinie.</summary>
    Vertical,
    /// <summary>Falz einer Doppelseite: senkrecht, dort darf das Papier einen Knick haben.</summary>
    Fold,
}

/// <summary>
/// Eine nachgezeichnete Linie, die in Wirklichkeit gerade ist. Punkte relativ zum Pixelbild (x / Breite, y / Höhe),
/// in der Reihenfolge, in der sie gesetzt wurden.
/// </summary>
public sealed record WarpLine(WarpLineKind Kind, IReadOnlyList<(double X, double Y)> Points)
{
    public bool Equals(WarpLine? other) =>
        other is not null && Kind == other.Kind && Points.SequenceEqual(other.Points);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Kind);
        foreach (var p in Points) h.Add(p);
        return h.ToHashCode();
    }
}

/// <summary>
/// Fertiges Gitter einer Begradigung (KI, <c>SyntheticDocDewarp</c>): für ein Raster von <paramref name="Nx"/> × <paramref name="Ny"/>
/// Stellen des Ergebnisses die Stelle im Pixelbild (relativ: x / Breite, y / Höhe), dazu die Größe des Ergebnisses in
/// Breiten des Pixelbilds und das geschätzte Seitenverhältnis (Höhe / Breite, null = unbekannt). Unabhängig von der
/// Bildgröße, deshalb Teil des Rezepts.
/// </summary>
public sealed record DewarpGrid(int Nx, int Ny, float[] Sx, float[] Sy, double RelWidth, double RelHeight, double? PageAspect, bool Din)
{
    public bool Equals(DewarpGrid? other) =>
        other is not null && Nx == other.Nx && Ny == other.Ny && RelWidth.Equals(other.RelWidth) && RelHeight.Equals(other.RelHeight)
        && Nullable.Equals(PageAspect, other.PageAspect) && Din == other.Din
        && Sx.AsSpan().SequenceEqual(other.Sx) && Sy.AsSpan().SequenceEqual(other.Sy);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Nx);
        h.Add(Ny);
        h.Add(RelWidth);
        h.Add(RelHeight);
        for (var i = 0; i < Sx.Length; i += Math.Max(1, Sx.Length / 16)) { h.Add(Sx[i]); h.Add(Sy[i]); }
        return h.ToHashCode();
    }
}

/// <summary>
/// Gewölbtes Blatt gerade ziehen: die nachgezeichneten Linien und die Brennweite der Aufnahme (Kleinbild-Äquivalent,
/// aus den EXIF-Daten; ohne Angabe 28 mm wie bei Handys üblich).
/// </summary>
/// <param name="GridOnly">Kein Blattmodell im Raum: nur die Linien gerade ziehen (Gitter), Seitenverhältnis wie vorher — für
/// einen zweiten Durchgang auf einem schon begradigten Bild.</param>
/// <param name="Grid">Fertiges Gitter einer KI-Begradigung — dann zählen keine Linien.</param>
public sealed record DewarpSpec(IReadOnlyList<WarpLine> Lines, double Focal35 = DewarpSpec.DefaultFocal35, bool GridOnly = false,
    DewarpGrid? Grid = null)
{
    public const double DefaultFocal35 = 28;

    /// <summary>Linien mit mindestens zwei Punkten — nur die zählen.</summary>
    public IEnumerable<WarpLine> UsableLines => Lines.Where(l => l.Points.Count >= 2);

    public bool IsUsable => Grid is not null || UsableLines.Any();

    public bool Equals(DewarpSpec? other) =>
        other is not null && Focal35.Equals(other.Focal35) && GridOnly == other.GridOnly && Equals(Grid, other.Grid)
        && Lines.SequenceEqual(other.Lines);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Focal35);
        h.Add(GridOnly);
        h.Add(Grid);
        foreach (var l in Lines) h.Add(l);
        return h.ToHashCode();
    }
}
