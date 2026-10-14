namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// Kacheln entlang einer Achse: gleichmäßig verteilt, je mindestens <c>overlap</c> überlappend. Dazu Gewichte, die an jeder
/// Stelle zusammen genau 1 ergeben (weiche Rampen in den Überlappungen, am Bildrand keine) — so lässt sich ein Ergebnis
/// Kachel für Kachel direkt aufaddieren, ohne Summen- und Gewichtspuffer in voller Größe. Zweidimensional: Produkt der
/// Gewichte beider Achsen (ergibt ebenfalls überall 1).
/// </summary>
public sealed class TileAxis
{
    /// <param name="length">Länge der Achse.</param>
    /// <param name="tile">Kachelgröße (bei kürzerer Achse: eine Kachel über alles).</param>
    /// <param name="overlap">Mindestüberlappung; die Rampen sind so breit.</param>
    /// <param name="align">Anfänge auf Vielfache davon (VAE: 1 Latentpixel = 8 Bildpixel).</param>
    public TileAxis(int length, int tile, int overlap, int align = 1)
    {
        Length = length;
        Size = Math.Min(tile, length);
        var n = length <= tile ? 1 : (int)Math.Ceiling((length - overlap) / (double)(tile - overlap));
        Starts = new int[n];
        for (var k = 0; k < n; k++)
        {
            var s = n == 1 ? 0 : (int)Math.Round(k * (length - Size) / (double)(n - 1));
            Starts[k] = Math.Min(length - Size, s / align * align);
        }
        Starts[^1] = length - Size;

        // Rohgewichte: Rampe von den Kachelrändern nach innen (nicht am Bildrand), dann je Stelle auf Summe 1 bringen
        var ramp = Math.Max(1, overlap);
        var raw = new float[n][];
        var sum = new double[length];
        for (var k = 0; k < n; k++)
        {
            raw[k] = new float[Size];
            for (var i = 0; i < Size; i++)
            {
                var p = Starts[k] + i;
                double r = 1;
                if (Starts[k] > 0) r = Math.Min(r, (i + 0.5) / ramp);
                if (Starts[k] + Size < length) r = Math.Min(r, (Size - i - 0.5) / ramp);
                raw[k][i] = (float)r;
                sum[p] += r;
            }
        }
        Weights = new float[n][];
        for (var k = 0; k < n; k++)
        {
            Weights[k] = new float[Size];
            for (var i = 0; i < Size; i++) Weights[k][i] = (float)(raw[k][i] / sum[Starts[k] + i]);
        }

        // Für die Zwischenstände: letzte Kachel, die eine Stelle berührt
        LastCovering = new int[length];
        for (var k = 0; k < n; k++)
        for (var i = 0; i < Size; i++) LastCovering[Starts[k] + i] = k;
    }

    public int Length { get; }
    public int Size { get; }
    public int[] Starts { get; }
    public int Count => Starts.Length;

    /// <summary>Gewicht je Kachel und Stelle in der Kachel.</summary>
    public float[][] Weights { get; }

    /// <summary>Je Stelle die letzte (höchste) Kachel, die sie berührt.</summary>
    public int[] LastCovering { get; }

    /// <summary>Dieselbe Einteilung in <paramref name="factor"/>-facher Auflösung (Latent → Bild), Rampen mitskaliert.</summary>
    public TileAxis Scaled(int factor, int overlap) => new(this, factor, overlap);

    private TileAxis(TileAxis coarse, int factor, int overlap)
    {
        Length = coarse.Length * factor;
        Size = coarse.Size * factor;
        Starts = coarse.Starts.Select(s => s * factor).ToArray();
        var n = Starts.Length;
        var ramp = Math.Max(1, overlap * factor);
        var raw = new float[n][];
        var sum = new double[Length];
        for (var k = 0; k < n; k++)
        {
            raw[k] = new float[Size];
            for (var i = 0; i < Size; i++)
            {
                double r = 1;
                if (Starts[k] > 0) r = Math.Min(r, (i + 0.5) / ramp);
                if (Starts[k] + Size < Length) r = Math.Min(r, (Size - i - 0.5) / ramp);
                raw[k][i] = (float)r;
                sum[Starts[k] + i] += r;
            }
        }
        Weights = new float[n][];
        for (var k = 0; k < n; k++)
        {
            Weights[k] = new float[Size];
            for (var i = 0; i < Size; i++) Weights[k][i] = (float)(raw[k][i] / sum[Starts[k] + i]);
        }
        LastCovering = new int[Length];
        for (var k = 0; k < n; k++)
        for (var i = 0; i < Size; i++) LastCovering[Starts[k] + i] = k;
    }
}
