namespace ImageConverter.Core.Imaging;

/// <summary>
/// Local Laplacian Filter (Paris, Hasinoff, Kautz 2011; schnelle Fassung nach Aubry u. a.
/// 2014) — das Verfahren hinter Tiefen/Lichter/Klarheit in Referenzwerkzeug und Referenzwerkzeug.
///
/// Eine Tonkurve wird nicht Pixel für Pixel angewendet, sondern Schicht für Schicht einer
/// Laplace-Pyramide: Jede Detailschicht übernimmt die Kurve passend zur Helligkeit der
/// Umgebung auf ihrer Größenordnung. Feine Details bleiben erhalten (die Kurve wird um den
/// Umgebungswert herum linear angewendet), und über Kanten hinweg wirkt sie nicht — Werte,
/// die weiter als <c>sigma</c> vom Umgebungswert entfernt sind, bleiben unverändert.
/// Dadurch keine Lichthöfe und keine flauen Flächen.
///
/// Schnelle Fassung: Statt für jedes Pixel eine eigene Pyramide zu rechnen, wird die Kurve
/// für <see cref="Levels"/> feste Umgebungswerte gerechnet und zwischen den beiden
/// nächsten Ergebnissen linear gemischt.
/// </summary>
internal static class LocalLaplacian
{
    /// <summary>Anzahl der festen Umgebungswerte zwischen 0 und 1.</summary>
    private const int Levels = 8;

    /// <summary>Kleinste Kante der gröbsten Pyramidenstufe.</summary>
    private const int CoarsestEdge = 4;

    /// <param name="lum">Helligkeit 0–1 (sRGB-kodiert, also wahrnehmungsnah).</param>
    /// <param name="curve">Tonkurve für den Umgebungswert.</param>
    /// <param name="sigma">Ab diesem Abstand zum Umgebungswert gilt ein Wert als Kante.</param>
    /// <param name="guide">Umgebungswert je Pyramidenstufe aus der Gauß-Pyramide der
    /// Eingabe — z. B. um zusätzlich eine weite Umgebung zu berücksichtigen.</param>
    public static float[] Apply(float[] lum, int width, int height, Func<float, float> curve, float sigma,
        Func<Pyramid, int, float[]>? guide = null)
    {
        var gauss = Pyramid.Gaussian(lum, width, height, LevelCount(width, height));
        var top = gauss.Count - 1;
        var guides = Enumerable.Range(0, gauss.Count).Select(l => guide?.Invoke(gauss, l) ?? gauss[l]).ToArray();

        // Ausgabe-Pyramide: Laplace-Stufen 0 … top-1, Rest auf der gröbsten Stufe
        var output = gauss.EmptyLike();
        var step = 1f / (Levels - 1);
        var twoSigma2 = 2 * sigma * sigma;

        for (var k = 0; k < Levels; k++)
        {
            var gamma = k * step;
            var target = curve(gamma);
            var slope = Slope(curve, gamma);

            // Bild so umgerechnet, als läge überall die Umgebung gamma vor
            var remapped = new float[lum.Length];
            Parallel.For(0, height, y =>
            {
                for (int x = 0, i = y * width; x < width; x++, i++)
                {
                    var d = lum[i] - gamma;
                    var curved = target + slope * d;                     // Kurve, um gamma linearisiert
                    var near = MathF.Exp(-d * d / twoSigma2);            // 1 nah, 0 jenseits der Kante
                    remapped[i] = lum[i] + (curved - lum[i]) * near;
                }
            });

            var r = Pyramid.Gaussian(remapped, width, height, gauss.Count);
            for (var l = 0; l <= top; l++)
            {
                var band = l < top ? r.Laplacian(l) : r[top];
                var g = guides[l];
                var o = output[l];
                Parallel.For(0, o.Length / 4096 + 1, chunk =>
                {
                    var end = Math.Min(o.Length, (chunk + 1) * 4096);
                    for (var i = chunk * 4096; i < end; i++)
                    {
                        var w = 1 - MathF.Abs(Math.Clamp(g[i], 0, 1) - gamma) / step;
                        if (w > 0) o[i] += w * band[i];
                    }
                });
            }
        }

        return output.Collapse();
    }

    private static float Slope(Func<float, float> curve, float x)
    {
        const float e = 0.01f;
        var lo = Math.Max(0, x - e);
        var hi = Math.Min(1, x + e);
        return Math.Clamp((curve(hi) - curve(lo)) / (hi - lo), 0, 4);
    }

    private static int LevelCount(int width, int height)
    {
        var n = 1;
        for (var e = Math.Min(width, height); e / 2 >= CoarsestEdge; e = (e + 1) / 2) n++;
        return n;
    }
}

/// <summary>
/// Gauß-Pyramide einer Fließkomma-Ebene: Stufe 0 ist das Bild, jede weitere halb so groß
/// (5er-Binomialfilter 1-4-6-4-1, Ränder gespiegelt fortgesetzt).
/// </summary>
internal sealed class Pyramid
{
    private readonly List<(float[] Data, int W, int H)> _levels = [];

    public int Count => _levels.Count;
    public float[] this[int level] => _levels[level].Data;
    public int Width(int level) => _levels[level].W;
    public int Height(int level) => _levels[level].H;

    private static readonly float[] Kernel = [1 / 16f, 4 / 16f, 6 / 16f, 4 / 16f, 1 / 16f];

    public static Pyramid Gaussian(float[] image, int width, int height, int levels)
    {
        var p = new Pyramid();
        p._levels.Add((image, width, height));
        for (var l = 1; l < levels; l++)
        {
            var (src, w, h) = p._levels[l - 1];
            p._levels.Add(Down(src, w, h));
        }
        return p;
    }

    /// <summary>Leere Pyramide gleicher Größen (für die Ausgabe).</summary>
    public Pyramid EmptyLike()
    {
        var p = new Pyramid();
        foreach (var (data, w, h) in _levels) p._levels.Add((new float[data.Length], w, h));
        return p;
    }

    /// <summary>Detailschicht: Stufe minus die vergrößerte nächstgröbere Stufe.</summary>
    public float[] Laplacian(int level)
    {
        var (fine, w, h) = _levels[level];
        var (coarse, cw, ch) = _levels[level + 1];
        var up = Up(coarse, cw, ch, w, h);
        var band = new float[fine.Length];
        for (var i = 0; i < band.Length; i++) band[i] = fine[i] - up[i];
        return band;
    }

    /// <summary>Gröbste Stufe auf die Größe von <paramref name="level"/> vergrößert.</summary>
    public float[] UpsampledFrom(int coarse, int level)
    {
        var (data, w, h) = _levels[coarse];
        for (var l = coarse - 1; l >= level; l--)
        {
            data = Up(data, w, h, _levels[l].W, _levels[l].H);
            (w, h) = (_levels[l].W, _levels[l].H);
        }
        return data;
    }

    /// <summary>Laplace-Pyramide (Stufen 0 … n-2) plus Rest zurück zum Bild.</summary>
    public float[] Collapse()
    {
        var top = Count - 1;
        var (img, w, h) = _levels[top];
        for (var l = top - 1; l >= 0; l--)
        {
            var (band, fw, fh) = _levels[l];
            var up = Up(img, w, h, fw, fh);
            for (var i = 0; i < up.Length; i++) up[i] += band[i];
            (img, w, h) = (up, fw, fh);
        }
        return img;
    }

    private static int Mirror(int i, int n) => i < 0 ? Math.Min(-i, n - 1) : i >= n ? Math.Max(2 * n - 2 - i, 0) : i;

    private static (float[], int, int) Down(float[] src, int w, int h)
    {
        int cw = (w + 1) / 2, ch = (h + 1) / 2;
        var rows = new float[cw * h];
        Parallel.For(0, h, y =>
        {
            var row = y * w;
            for (var x = 0; x < cw; x++)
            {
                float acc = 0;
                for (var k = -2; k <= 2; k++) acc += Kernel[k + 2] * src[row + Mirror(2 * x + k, w)];
                rows[y * cw + x] = acc;
            }
        });
        var dst = new float[cw * ch];
        Parallel.For(0, ch, y =>
        {
            for (var x = 0; x < cw; x++)
            {
                float acc = 0;
                for (var k = -2; k <= 2; k++) acc += Kernel[k + 2] * rows[Mirror(2 * y + k, h) * cw + x];
                dst[y * cw + x] = acc;
            }
        });
        return (dst, cw, ch);
    }

    /// <summary>Vergrößern auf fw × fh: Nullen einfügen und mit doppeltem Kernel glätten.</summary>
    private static float[] Up(float[] src, int cw, int ch, int fw, int fh)
    {
        var rows = new float[fw * ch];
        Parallel.For(0, ch, y =>
        {
            for (var x = 0; x < fw; x++)
            {
                float acc = 0;
                // Beiträge der groben Pixel i mit 2i nahe x
                for (var k = -2; k <= 2; k++)
                {
                    var t = x + k;
                    if ((t & 1) != 0) continue;
                    acc += 2 * Kernel[k + 2] * src[y * cw + Math.Clamp(t / 2, 0, cw - 1)];
                }
                rows[y * fw + x] = acc;
            }
        });
        var dst = new float[fw * fh];
        Parallel.For(0, fh, y =>
        {
            for (var x = 0; x < fw; x++)
            {
                float acc = 0;
                for (var k = -2; k <= 2; k++)
                {
                    var t = y + k;
                    if ((t & 1) != 0) continue;
                    acc += 2 * Kernel[k + 2] * rows[Math.Clamp(t / 2, 0, ch - 1) * fw + x];
                }
                dst[y * fw + x] = acc;
            }
        });
        return dst;
    }
}
