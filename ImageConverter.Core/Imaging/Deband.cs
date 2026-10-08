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
            var dst = new float[src.Length];
            var reach = (float)(radius * round);
            var bound = strength * MaxThreshold / round;
            var key = seed * 0x9E3779B9u + (uint)round * 0x85EBCA6Bu;
            Parallel.For(0, height, y =>
            {
                Span<int> tap = stackalloc int[4];
                for (int x = 0, p = y * width; x < width; x++, p++)
                {
                    var i = p * 4;
                    var angle = Pixels.Noise((uint)p ^ key) * MathF.PI;   // Kreuz: halber Kreis genügt
                    var dist = reach * (0.25f + 0.75f * Pixels.Noise((uint)p * 3u + key));
                    var dx = (int)MathF.Round(dist * MathF.Cos(angle));
                    var dy = (int)MathF.Round(dist * MathF.Sin(angle));
                    tap[0] = Index(x + dx, y + dy, width, height);
                    tap[1] = Index(x - dx, y - dy, width, height);
                    tap[2] = Index(x - dy, y + dx, width, height);
                    tap[3] = Index(x + dy, y - dx, width, height);

                    var worst = 0f;
                    for (var t = 0; t < 4; t++)
                    for (var c = 0; c < 3; c++)
                        worst = Math.Max(worst, MathF.Abs(src[tap[t] + c] - src[i + c]));
                    // voll bis zur halben Schwelle, an der Schwelle aus
                    var w = Math.Clamp(2 - 2 * worst / bound, 0, 1);
                    for (var c = 0; c < 3; c++)
                    {
                        var avg = (src[tap[0] + c] + src[tap[1] + c] + src[tap[2] + c] + src[tap[3] + c]) * 0.25f;
                        dst[i + c] = src[i + c] + (avg - src[i + c]) * w;
                    }
                    dst[i + 3] = src[i + 3];
                }
            });
            current = dst;
        }
        return current;
    }

    private static int Index(int x, int y, int w, int h) =>
        (Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)) * 4;
}
