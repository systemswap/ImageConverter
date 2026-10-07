namespace ImageConverter.Core.Imaging;

/// <summary>
/// Rechnet Zielmaße aus. Haelt das Seitenverhaeltnis als einen festen Wert, der beim
/// Laden des Bildes (bzw. nach einem Zuschnitt) gesetzt wird — nicht nach jedem
/// Tastendruck neu aus den gerundeten Feldern, weil das Verhaeltnis sonst wegdriftet.
/// </summary>
public sealed class ResizeCalculator
{
    /// <summary>Seitenverhaeltnis Breite/Hoehe der Quelle.</summary>
    public double AspectRatio { get; }

    public int SourceWidth { get; }
    public int SourceHeight { get; }

    public ResizeCalculator(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceHeight));

        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        AspectRatio = (double)sourceWidth / sourceHeight;
    }

    /// <summary>
    /// Breite wurde eingegeben — liefert die passende Hoehe. Die eingegebene Breite
    /// bleibt unangetastet, der Rundungsfehler landet in der Hoehe.
    /// </summary>
    public int HeightFor(int width) => Clamp((int)Math.Round(width / AspectRatio));

    /// <summary>Hoehe wurde eingegeben — liefert die passende Breite.</summary>
    public int WidthFor(int height) => Clamp((int)Math.Round(height * AspectRatio));

    /// <summary>Beide Kanten aus einem Prozentwert (100 = Originalgroesse).</summary>
    public (int Width, int Height) FromPercent(double percent) =>
        (Clamp((int)Math.Round(SourceWidth * percent / 100.0)),
         Clamp((int)Math.Round(SourceHeight * percent / 100.0)));

    /// <summary>Beide Kanten so, dass die laengste Kante <paramref name="longestEdge"/> ergibt.</summary>
    public (int Width, int Height) FromLongestEdge(int longestEdge) =>
        SourceWidth >= SourceHeight
            ? (Clamp(longestEdge), HeightFor(longestEdge))
            : (WidthFor(longestEdge), Clamp(longestEdge));

    /// <summary>Groesstes Ergebnis, das vollstaendig in die Box passt (Fit, mit Rand).</summary>
    public (int Width, int Height) Fit(int boxWidth, int boxHeight)
    {
        var scale = Math.Min((double)boxWidth / SourceWidth, (double)boxHeight / SourceHeight);
        return (Clamp((int)Math.Round(SourceWidth * scale)),
                Clamp((int)Math.Round(SourceHeight * scale)));
    }

    /// <summary>Kleinstes Ergebnis, das die Box vollstaendig fuellt (Cover, wird beschnitten).</summary>
    public (int Width, int Height) Fill(int boxWidth, int boxHeight)
    {
        var scale = Math.Max((double)boxWidth / SourceWidth, (double)boxHeight / SourceHeight);
        return (Clamp((int)Math.Round(SourceWidth * scale)),
                Clamp((int)Math.Round(SourceHeight * scale)));
    }

    /// <summary>
    /// Pixelmaße aus physischer Zielgroesse und DPI (DPI-Modus "neu berechnen").
    /// </summary>
    public static (int Width, int Height) FromPhysicalSize(
        double widthInches, double heightInches, double dpi) =>
        (Clamp((int)Math.Round(widthInches * dpi)),
         Clamp((int)Math.Round(heightInches * dpi)));

    private static int Clamp(int value) => value < 1 ? 1 : value;
}
