using ImageMagick;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Ergebnis eines KI-Laufs, noch nicht ins Bild gemischt. Das Modell rechnet einmal mit voller Wirkung;
/// Stärke (Mischung mit dem Bild davor) und Hautstruktur werden danach beliebig oft neu gemischt —
/// schnell für die Vorschau (live beim Ziehen am Regler), in voller Größe beim Übernehmen.
/// </summary>
public interface IAiRun
{
    /// <summary>Größe des Ergebnisses in Pixeln.</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>Gesichtsmodelle: Hautstruktur aus dem Bild davor zurückholen; beim ganzen Bild nicht.</summary>
    bool SupportsTexture { get; }

    /// <summary>Vorschau in <paramref name="size"/> (gleiches Seitenverhältnis wie das Ergebnis).</summary>
    /// <param name="strength">0 … 1: Mischung mit dem Bild davor (1 = nur das Modell).</param>
    /// <param name="texture">0 … 1: feine Struktur (Poren, Härchen, Korn) aus dem Bild davor statt aus dem Modell.</param>
    AiPixels Preview(double strength, double texture, AiPreviewSize size);

    /// <summary>Endergebnis in voller Größe, mit Profilen und Auflösung des Bildes davor.</summary>
    MagickImage Compose(double strength, double texture);
}

/// <summary>
/// Vorschau als Pixelpuffer der Bildkette (<see cref="Imaging.Pixels"/>: B, G, R, A je 0 … 1, sRGB) —
/// so können die Einstellungen (Dunst, Licht, Schärfe …) ungerundet darauf laufen.
/// </summary>
public sealed record AiPixels(float[] Bgra, int Width, int Height);

/// <summary>
/// Größe und Verkleinerung der Vorschau — dieselben wie bei der normalen Vorschau (Zielgröße des Exports,
/// höchstens 2048 px, gewählter Filter), sonst springt das Bild beim Übernehmen.
/// </summary>
public readonly record struct AiPreviewSize(int Width, int Height,
    Imaging.ResampleFilter Filter = Imaging.ResampleFilter.Lanczos3, bool GammaCorrect = true)
{
    /// <summary>Seitenverhältnis von <paramref name="width"/> × <paramref name="height"/>, längste Kante höchstens <paramref name="maxEdge"/>.</summary>
    public static AiPreviewSize Fit(int width, int height, int maxEdge)
    {
        var k = Math.Min(1.0, maxEdge / (double)Math.Max(width, height));
        return new AiPreviewSize(Math.Max(1, (int)Math.Round(width * k)), Math.Max(1, (int)Math.Round(height * k)));
    }
}
