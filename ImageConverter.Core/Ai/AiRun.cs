using ImageMagick;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Ergebnis eines KI-Laufs, noch nicht ins Bild gemischt. Das Modell rechnet einmal mit voller Wirkung;
/// alles in <see cref="AiBlend"/> wird danach beliebig oft neu gemischt — schnell für die Vorschau (live beim
/// Ziehen am Regler), in voller Größe beim Übernehmen.
/// </summary>
public interface IAiRun
{
    /// <summary>Größe des Ergebnisses in Pixeln.</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>Gesichtsmodelle: Hautstruktur, Falten und Korn; beim ganzen Bild nur die Stärke.</summary>
    bool SupportsDetails { get; }

    /// <summary>Vorschau in <paramref name="size"/> (gleiches Seitenverhältnis wie das Ergebnis).</summary>
    AiPixels Preview(AiBlend blend, AiPreviewSize size);

    /// <summary>Endergebnis in voller Größe, mit Profilen und Auflösung des Bildes davor.</summary>
    MagickImage Compose(AiBlend blend);
}

/// <summary>Wie das Ergebnis eines KI-Laufs ins Bild kommt — alles ohne neues Rechnen des Modells.</summary>
/// <param name="Strength">0 … 1: Deckkraft der KI über dem Original (1 = nur KI).</param>
/// <param name="Texture">0 … 1: feinste Struktur (Poren, Härchen) aus dem Original statt aus dem Modell.</param>
/// <param name="Folds">0 … 1: was das Modell an Falten und Zügen weggeglättet hat, aus dem Original zurück (nur Haut).</param>
/// <param name="Grain">0 … 2: Korn im Gesicht, 1 = so stark wie im übrigen Foto gemessen.</param>
/// <param name="GrainSize">Faktor auf die gemessene Korngröße (1 = wie im Foto).</param>
public sealed record AiBlend(double Strength = 1, double Texture = 0, double Folds = 0, double Grain = 0, double GrainSize = 1);

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
