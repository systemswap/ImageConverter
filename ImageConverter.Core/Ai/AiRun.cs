using ImageMagick;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Ergebnis eines KI-Laufs, noch nicht ins Bild gemischt. Das Modell rechnet einmal mit voller Wirkung;
/// Stärke (Mischung mit dem Bild davor) und Hautstruktur werden danach beliebig oft neu gemischt —
/// schnell für die Vorschau (live beim Ziehen am Regler), in voller Größe beim Übernehmen.
/// </summary>
public interface IAiRun
{
    /// <summary>Gesichtsmodelle: Hautstruktur aus dem Bild davor zurückholen; beim ganzen Bild nicht.</summary>
    bool SupportsTexture { get; }

    /// <summary>Vorschau (8-Bit-BGRA), längste Kante höchstens <paramref name="maxEdge"/>.</summary>
    /// <param name="strength">0 … 1: Mischung mit dem Bild davor (1 = nur das Modell).</param>
    /// <param name="texture">0 … 1: feine Struktur (Poren, Härchen, Korn) aus dem Bild davor statt aus dem Modell.</param>
    AiPreviewFrame Preview(double strength, double texture, int maxEdge = 2048);

    /// <summary>Endergebnis in voller Größe, mit Profilen und Auflösung des Bildes davor.</summary>
    MagickImage Compose(double strength, double texture);
}
