using ImageConverter.Core.Platform;
namespace ImageConverter.Core.Imaging;

/// <summary>
/// Treppen in weichen Verläufen glätten (Debanding), auf Fließkomma-Pixeln.
///
/// Verfahren wie flash3kyuu_deband (f3kdb) und der Deband-Filter von libplacebo (mpv, FFmpeg):
/// Je Pixel vier Proben in zufälliger Richtung und Entfernung (Kreuz aus zwei Gegenpaaren),
/// das Pixel wird durch ihr Mittel ersetzt — aber nur, wo alle vier höchstens um die Schwelle
/// abweichen. In einem Verlauf liegen die Nachbarstufen so nahe, dass sie gemittelt werden: Aus
/// der Treppe wird eine Rampe mit Zwischenwerten, die erst die Fließkomma-Kette halten kann.
/// Echte Kanten und Struktur liegen über der Schwelle und bleiben. Zwei Runden: erst kleiner
/// Radius, dann der doppelte mit halber Schwelle (wie libplacebo) — lange flache Verläufe
/// brauchen den großen Radius, steilere den kleinen.
///
/// Strenger als libplacebo (dort zählt nur der Abstand zum Mittel): Jede einzelne Probe muss
/// passen, und die Wirkung blendet vor der Schwelle aus — sonst verschwanden feine, flaue
/// Strukturen in der Ferne, die der Dunstfilter danach sichtbar machen soll.
/// </summary>
internal static class Deband
{
    /// <summary>Probenradius der ersten Runde, relativ zur längsten Kante (bei 4000 px: 32 px).</summary>
    private const double RadiusPerEdge = 0.008;

    /// <summary>Schwelle bei Stärke 1 in 8-Bit-Stufen.</summary>
    private const float MaxThreshold = 6f / 255;

    /// <param name="strength">0 … 1.</param>
    /// <param name="seed">Je Aufruf ein anderes Zufallsmuster — mehrere Runden mitteln sich so aus.</param>
    public static float[] Apply(float[] px, int width, int height, float strength, uint seed)
    {
        if (strength <= 0) return px;
        var radius = Math.Max(2, RadiusPerEdge * Math.Max(width, height));
        var current = px;
        for (var round = 1; round <= 2; round++)
        {
            var src = current;
            var dst = PixelPool.Rent(src.Length);   // wird vollständig geschrieben
            var reach = (float)(radius * round);
            var bound = strength * MaxThreshold / round;
            var key = seed * 0x9E3779B9u + (uint)round * 0x85EBCA6Bu;
            var (offX, offY) = Offsets(reach);
            // Muster je Aufruf verschoben, damit sich die Runden nicht decken
            var shiftX = (int)(Pixels.Noise(key) * Pattern);
            var shiftY = (int)(Pixels.Noise(key ^ 0x5BD1E995u) * Pattern);
            // In Kacheln (TileWidth × TileRows): Die Proben liegen bis zu 2 Radien entfernt — über ganze
            // Bildzeilen (bei 8000 px je 128 KB) passte deren Umgebung nicht in den Cache des Prozessors
            var tilesX = (width + TileWidth - 1) / TileWidth;
            var tilesY = (height + TileRows - 1) / TileRows;
            Parallel.For(0, tilesX * tilesY, ComputeLimits.Options, tile =>
            {
                Span<int> tap = stackalloc int[4];
                var tx0 = tile % tilesX * TileWidth;
                var tx1 = Math.Min(width, tx0 + TileWidth);
                var ty0 = tile / tilesX * TileRows;
                var ty1 = Math.Min(height, ty0 + TileRows);
                for (var y = ty0; y < ty1; y++)
                {
                    var row = ((y + shiftY) & (Pattern - 1)) * Pattern;
                    for (int x = tx0, p = y * width + tx0; x < tx1; x++, p++)
                    {
                        var i = p * 4;
                        var o = row + ((x + shiftX) & (Pattern - 1));
                        int dx = offX[o], dy = offY[o];
                        tap[0] = Index(x + dx, y + dy, width, height);
                        tap[1] = Index(x - dx, y - dy, width, height);
                        tap[2] = Index(x - dy, y + dx, width, height);
                        tap[3] = Index(x + dy, y - dx, width, height);

                        // Größte Abweichung einer Probe; ab der Schwelle bleibt das Pixel (Struktur, Kante) —
                        // dann gleich weiter, die übrigen Proben braucht es nicht
                        float s0 = src[i], s1 = src[i + 1], s2 = src[i + 2];
                        var worst = 0f;
                        for (var t = 0; t < 4 && worst < bound; t++)
                        {
                            var q = tap[t];
                            worst = MathF.Max(worst, MathF.Max(MathF.Abs(src[q] - s0),
                                MathF.Max(MathF.Abs(src[q + 1] - s1), MathF.Abs(src[q + 2] - s2))));
                        }
                        // voll bis zur halben Schwelle, an der Schwelle aus
                        var w = worst >= bound ? 0 : Math.Clamp(2 - 2 * worst / bound, 0, 1);
                        if (w <= 0)
                        {
                            dst[i] = s0; dst[i + 1] = s1; dst[i + 2] = s2;
                        }
                        else
                        {
                            for (var c = 0; c < 3; c++)
                            {
                                var avg = (src[tap[0] + c] + src[tap[1] + c] + src[tap[2] + c] + src[tap[3] + c]) * 0.25f;
                                dst[i + c] = src[i + c] + (avg - src[i + c]) * w;
                            }
                        }
                        dst[i + 3] = src[i + 3];
                    }
                }
            });
            if (round == 2) PixelPool.Return(src);   // Zwischenstand der ersten Runde (gehört uns)
            current = dst;
        }
        return current;
    }

    /// <summary>Kachelgröße: Breite in Pixeln, Höhe in Zeilen.</summary>
    private const int TileWidth = 256, TileRows = 64;

    /// <summary>Kantenlänge des Zufallsmusters der Probenrichtungen (Zweierpotenz).</summary>
    private const int Pattern = 512;

    /// <summary>
    /// Zufällige Richtung (halber Kreis — die Proben bilden ein Kreuz) und Entfernung (¼ … 1 der Reichweite)
    /// je Feld eines Musters von <see cref="Pattern"/>² — vorab, statt je Pixel Sinus und Kosinus zu rechnen.
    /// </summary>
    private static readonly Lazy<(float[] Cos, float[] Sin)> Directions = new(() =>
    {
        var cos = new float[Pattern * Pattern];
        var sin = new float[Pattern * Pattern];
        for (var p = 0; p < cos.Length; p++)
        {
            var angle = Pixels.Noise((uint)p * 2654435761u) * MathF.PI;
            var dist = 0.25f + 0.75f * Pixels.Noise((uint)p * 3u + 0x68E31DA4u);
            cos[p] = dist * MathF.Cos(angle);
            sin[p] = dist * MathF.Sin(angle);
        }
        return (cos, sin);
    });

    private static (int[] X, int[] Y) Offsets(float reach)
    {
        var (cos, sin) = Directions.Value;
        var x = new int[cos.Length];
        var y = new int[cos.Length];
        for (var p = 0; p < x.Length; p++)
        {
            x[p] = (int)MathF.Round(reach * cos[p]);
            y[p] = (int)MathF.Round(reach * sin[p]);
        }
        return (x, y);
    }

    private static int Index(int x, int y, int w, int h) =>
        (Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)) * 4;
}
