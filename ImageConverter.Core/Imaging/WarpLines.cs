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
/// Gewölbtes Blatt gerade ziehen: die nachgezeichneten Linien und die Brennweite der Aufnahme (Kleinbild-Äquivalent,
/// aus den EXIF-Daten; ohne Angabe 28 mm wie bei Handys üblich).
/// </summary>
/// <param name="GridOnly">Kein Blattmodell im Raum: nur die Linien gerade ziehen (Gitter), Seitenverhältnis wie vorher — für
/// einen zweiten Durchgang auf einem schon begradigten Bild.</param>
public sealed record DewarpSpec(IReadOnlyList<WarpLine> Lines, double Focal35 = DewarpSpec.DefaultFocal35, bool GridOnly = false)
{
    public const double DefaultFocal35 = 28;

    /// <summary>Linien mit mindestens zwei Punkten — nur die zählen.</summary>
    public IEnumerable<WarpLine> UsableLines => Lines.Where(l => l.Points.Count >= 2);

    public bool IsUsable => UsableLines.Any();

    public bool Equals(DewarpSpec? other) =>
        other is not null && Focal35.Equals(other.Focal35) && GridOnly == other.GridOnly && Lines.SequenceEqual(other.Lines);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Focal35);
        h.Add(GridOnly);
        foreach (var l in Lines) h.Add(l);
        return h.ToHashCode();
    }
}
