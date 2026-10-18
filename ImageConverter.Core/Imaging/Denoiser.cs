using ImageConverter.Core.Ai;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Rauschen entfernen, getrennt nach Helligkeit und Farbe — so wie es gute Werkzeuge machen (Referenzwerkzeug: Luminanz und
/// Farbe). Arbeitet im 32-Bit-Puffer der Kette (<see cref="Pixels"/>), rechnet in Helligkeit Y und den Farbabständen
/// Cb = B − Y, Cr = R − Y.
/// </summary>
/// <remarks>
/// <para><b>Farbrauschen</b>: grobe bunte Flecken; in den Farbkanälen steckt kaum feines Detail, die Kanten liegen in
/// der Helligkeit. Deshalb ein helligkeitsgeführter Filter (Guided Filter, He u. a. 2010) auf Cb und Cr, in zwei Stufen
/// (feine Farbpunkte, größere Flecken): glättet die Farbe kräftig, stoppt aber an Helligkeitskanten — keine Farbsäume.</para>
/// <para><b>Helligkeitsrauschen</b>: Non-Local Means (Buades u. a. 2005) auf Y — jeder Punkt wird mit Punkten gemittelt,
/// deren Umgebung ähnlich aussieht, nicht einfach mit seinen Nachbarn; Kanten und feine Struktur bleiben. Die Stärke
/// folgt dem gemessenen Rauschen je Helligkeit (wie bei der KI-Körnung): dunkle, stark rauschende Stellen mehr, helle,
/// saubere weniger. Ein sauberes Bild bleibt fast unverändert.</para>
/// </remarks>
public static class Denoiser
{
    // Radien in Pixeln des Exports (in der Vorschau mit dem Maßstab umgerechnet)
    private const double ChromaFine = 3, ChromaCoarse = 10;
    private const int PatchRadius = 2, SearchRadius = 4;

    /// <summary>Entrauschen; die Eingabe bleibt unverändert.</summary>
    /// <param name="px">Pixel (B, G, R, A je 0…1).</param>
    /// <param name="luma">0…1: Helligkeitsrauschen.</param>
    /// <param name="chroma">0…1: Farbrauschen.</param>
    /// <param name="scale">Breite hier ÷ Breite im Export — Radien wirken in Vorschau und Export gleich.</param>
    public static float[] Apply(float[] px, int w, int h, double luma, double chroma, double scale = 1, WorkPlan? plan = null)
    {
        plan?.Next();
        if (luma <= 0.005 && chroma <= 0.005)
        {
            var copy = PixelPool.Rent(px.Length);
            Array.Copy(px, copy, px.Length);
            return copy;   // nichts zu tun: genau dasselbe Bild, ohne Umweg über Helligkeit und Farbe
        }

        // Alle Zwischenpuffer aus dem Pool (vorher je Aufruf rund 7 GB neu angelegt beim Panorama) — gleiche Rechnung
        var n = w * h;
        float[] y = PixelPool.Rent(n), cb = PixelPool.Rent(n), cr = PixelPool.Rent(n);
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++)
            {
                float b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
                var yy = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                (y[i], cb[i], cr[i]) = (yy, b - yy, r - yy);
            }
        });

        if (chroma > 0.005)
        {
            // Mittelwerte der Führung (Helligkeit) hängen nur vom Radius ab — für beide Farbkanäle einmal
            using var stats = new GuideStats(y, w, h);
            ChromaDenoise(cb, y, w, h, chroma, scale, stats);
            ChromaDenoise(cr, y, w, h, chroma, scale, stats);
        }

        if (luma > 0.005)
        {
            var denoised = LumaDenoise(y, px, w, h, luma);
            if (!ReferenceEquals(denoised, y)) PixelPool.Return(y);
            y = denoised;
        }

        var result = PixelPool.Rent(px.Length);
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++)
            {
                var r = y[i] + cr[i];
                var b = y[i] + cb[i];
                var g = (y[i] - 0.2126f * r - 0.0722f * b) / 0.7152f;
                result[i * 4] = b;
                result[i * 4 + 1] = g;
                result[i * 4 + 2] = r;
                result[i * 4 + 3] = px[i * 4 + 3];
            }
        });
        PixelPool.Return(y);
        PixelPool.Return(cb);
        PixelPool.Return(cr);
        return result;
    }

    /// <summary>
    /// Mittel und Mittel der Quadrate der Führung je Radius (Guided Filter), einmal gerechnet für beide Farbkanäle.
    /// Die Führung darf sich in der Zeit nicht ändern.
    /// </summary>
    private sealed class GuideStats(float[] guide, int w, int h) : IDisposable
    {
        private readonly Dictionary<int, (float[] MeanI, float[] CorrII)> _byRadius = [];

        public float[] Guide => guide;

        public (float[] MeanI, float[] CorrII) For(int r)
        {
            if (_byRadius.TryGetValue(r, out var known)) return known;
            var n = guide.Length;
            var ii = PixelPool.Rent(n);
            var tmp = PixelPool.Rent(n);
            Parallel.For(0, h, ComputeLimits.Options, row =>
            {
                for (int x = 0, i = row * w; x < w; x++, i++) ii[i] = guide[i] * guide[i];
            });
            var meanI = BoxMean(guide, w, h, r, tmp, PixelPool.Rent(n));
            var corrII = BoxMean(ii, w, h, r, tmp, PixelPool.Rent(n));
            PixelPool.Return(ii);
            PixelPool.Return(tmp);
            return _byRadius[r] = (meanI, corrII);
        }

        public void Dispose()
        {
            foreach (var (meanI, corrII) in _byRadius.Values)
            {
                PixelPool.Return(meanI);
                PixelPool.Return(corrII);
            }
            _byRadius.Clear();
        }
    }

    // ------------------------------------------------------------ Farbe

    /// <summary>
    /// Farbkanal glätten, geführt von der Helligkeit: zwei Stufen Guided Filter; die Stärke bestimmt, wie viel vom
    /// geglätteten Kanal übernommen wird und wie weit der Filter Unterschiede noch für Rauschen hält.
    /// </summary>
    private static void ChromaDenoise(float[] c, float[] guide, int w, int h, double strength, double scale, GuideStats stats)
    {
        var sigma = NoiseSigma(c, w, h);
        // Unterschiede bis etwa zum Dreifachen des Rauschens gelten als Rauschen — mehr Stärke, mehr Toleranz
        var eps = (float)Math.Pow(sigma * (1 + 3 * strength), 2) + 1e-7f;
        var r1 = Math.Max(1, (int)Math.Round(ChromaFine * scale));
        var r2 = Math.Max(r1 + 1, (int)Math.Round(ChromaCoarse * scale));

        var fine = GuidedFilter(c, guide, w, h, r1, eps, stats);
        var coarse = GuidedFilter(fine, guide, w, h, r2, eps, stats);
        var s = (float)Math.Clamp(strength, 0, 1);
        // feine Stufe ganz, die grobe nach und nach — kleine Werte entfernen erst die Farbpunkte, große auch Flecken
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++)
                c[i] += s * (fine[i] - c[i]) + s * s * (coarse[i] - fine[i]);
        });
        PixelPool.Return(fine);
        PixelPool.Return(coarse);
    }

    /// <summary>Guided Filter (He, Sun, Tang 2010): Ausgang folgt den Kanten der Führung, glättet dazwischen.</summary>
    private static float[] GuidedFilter(float[] p, float[] guide, int w, int h, int r, float eps, GuideStats stats)
    {
        var n = p.Length;
        var ip = PixelPool.Rent(n);
        var tmp = PixelPool.Rent(n);   // ein Zwischenpuffer für alle Kastenmittel (wird jeweils ganz überschrieben)
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++) ip[i] = guide[i] * p[i];
        });
        var (meanI, corrII) = stats.For(r);
        var meanP = BoxMean(p, w, h, r, tmp, PixelPool.Rent(n));
        var corrIP = BoxMean(ip, w, h, r, tmp, PixelPool.Rent(n));

        float[] a = ip, b = PixelPool.Rent(n);   // a überschreibt ip (wird nicht mehr gebraucht)
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++)
            {
                var varI = corrII[i] - meanI[i] * meanI[i];
                var cov = corrIP[i] - meanI[i] * meanP[i];
                a[i] = cov / (varI + eps);
                b[i] = meanP[i] - a[i] * meanI[i];
            }
        });
        var meanA = BoxMean(a, w, h, r, tmp, PixelPool.Rent(n));
        var meanB = BoxMean(b, w, h, r, tmp, PixelPool.Rent(n));
        var q = PixelPool.Rent(n);
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++) q[i] = meanA[i] * guide[i] + meanB[i];
        });
        foreach (var used in new[] { a, b, tmp, meanP, corrIP, meanA, meanB }) PixelPool.Return(used);
        return q;
    }

    /// <summary>Mittel über ein Quadrat mit Radius <paramref name="r"/> (am Rand nur über das, was im Bild liegt).</summary>
    internal static float[] BoxMean(float[] src, int w, int h, int r) =>
        BoxMean(src, w, h, r, new float[src.Length], new float[src.Length]);

    /// <summary>Wie oben, in vorhandene Puffer (<paramref name="tmp"/> Zwischenstand, <paramref name="dst"/> Ergebnis).</summary>
    internal static float[] BoxMean(float[] src, int w, int h, int r, float[] tmp, float[] dst)
    {
        Parallel.For(0, h, ComputeLimits.Options, y =>
        {
            var o = y * w;
            double sum = 0;
            var count = 0;
            for (var x = 0; x <= Math.Min(r, w - 1); x++) { sum += src[o + x]; count++; }
            for (var x = 0; x < w; x++)
            {
                tmp[o + x] = (float)(sum / count);
                var add = x + r + 1;
                var remove = x - r;
                if (add < w) { sum += src[o + add]; count++; }
                if (remove >= 0) { sum -= src[o + remove]; count--; }
            }
        });
        const int block = 64;
        Parallel.For(0, (w + block - 1) / block, ComputeLimits.Options, b =>
        {
            var x0 = b * block;
            var len = Math.Min(block, w - x0);
            Span<double> sum = stackalloc double[block];
            sum.Clear();
            var count = 0;
            for (var y = 0; y <= Math.Min(r, h - 1); y++)
            {
                var o = y * w + x0;
                for (var k = 0; k < len; k++) sum[k] += tmp[o + k];
                count++;
            }
            for (var y = 0; y < h; y++)
            {
                var o = y * w + x0;
                for (var k = 0; k < len; k++) dst[o + k] = (float)(sum[k] / count);
                var add = y + r + 1;
                var remove = y - r;
                if (add < h)
                {
                    var a = add * w + x0;
                    for (var k = 0; k < len; k++) sum[k] += tmp[a + k];
                    count++;
                }
                if (remove >= 0)
                {
                    var m = remove * w + x0;
                    for (var k = 0; k < len; k++) sum[k] -= tmp[m + k];
                    count--;
                }
            }
        });
        return dst;
    }

    /// <summary>Rauschstärke eines Kanals (robust: Median der Abweichung vom 3 × 3-Mittel, auf Normalverteilung umgerechnet).</summary>
    private static double NoiseSigma(float[] c, int w, int h)
    {
        var tmp = PixelPool.Rent(c.Length);
        var mean = BoxMean(c, w, h, 1, tmp, PixelPool.Rent(c.Length));
        var step = Math.Max(1, c.Length / 200_000);   // Stichprobe genügt
        var diffs = new List<float>(c.Length / step + 1);
        for (var i = 0; i < c.Length; i += step) diffs.Add(Math.Abs(c[i] - mean[i]));
        PixelPool.Return(tmp);
        PixelPool.Return(mean);
        diffs.Sort();
        // Abweichung vom 3 × 3-Mittel hat bei weißem Rauschen die Streuung σ·√(8/9); Median |N(0,1)| = 0,6745
        return diffs[diffs.Count / 2] / 0.6745 / Math.Sqrt(8.0 / 9);
    }

    // ------------------------------------------------------------ Helligkeit

    /// <summary>
    /// Non-Local Means auf der Helligkeit: für jede Verschiebung im Suchfenster der Abstand der 5 × 5-Umgebungen
    /// (Kastenmittel der quadrierten Differenzen), daraus das Gewicht. Filterstärke je Punkt aus dem gemessenen Rauschen.
    /// </summary>
    private static float[] LumaDenoise(float[] y, float[] px, int w, int h, double strength)
    {
        // Rauschen je Helligkeit messen (wie bei der Körnung) — auf genau diesem Bild, auch in der Vorschau
        var rgb = PixelPool.Rent(w * h * 3);
        Parallel.For(0, w * h, ComputeLimits.Options, i =>
        {
            rgb[i * 3] = px[i * 4 + 2];
            rgb[i * 3 + 1] = px[i * 4 + 1];
            rgb[i * 3 + 2] = px[i * 4];
        });
        var profile = FaceDetails.MeasureGrain(rgb, w, h);
        PixelPool.Return(rgb);
        if (profile.Average <= 1e-5) return y;

        var n = y.Length;
        var k = 0.25f + 0.75f * (float)Math.Clamp(strength, 0, 1);   // Filterbreite in Vielfachen des Rauschens
        var sigma2 = PixelPool.Rent(n);
        var invH2 = PixelPool.Rent(n);
        Parallel.For(0, n, ComputeLimits.Options, i =>
        {
            var s = profile.SigmaAt(y[i]);
            sigma2[i] = s * s;
            var hh = k * s;
            invH2[i] = hh > 1e-6f ? 1 / (hh * hh) : 0;
        });

        // Grafikkarte: dieselbe Rechnung je Punkt, alle Verschiebungen in einem Faden (gleiches Ergebnis bis auf Rundung)
        if (GpuFilters.NonLocalMeans(y, sigma2, invH2, w, h, PatchRadius, SearchRadius, (float)Math.Min(1, 1.5 * strength)) is { } fast)
        {
            PixelPool.Return(sigma2);
            PixelPool.Return(invH2);
            return fast;
        }

        // Summen beginnen bei null (Puffer aus dem Pool sind nicht geleert); diff, boxTmp, patch werden ganz überschrieben
        var sum = PixelPool.Rent(n);
        var weightSum = PixelPool.Rent(n);
        var maxWeight = PixelPool.Rent(n);
        Array.Clear(sum);
        Array.Clear(weightSum);
        Array.Clear(maxWeight);
        var diff = PixelPool.Rent(n);
        float[] boxTmp = PixelPool.Rent(n), patch = PixelPool.Rent(n);
        for (var dy = -SearchRadius; dy <= SearchRadius; dy++)
        for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var (ox, oy) = (dx, dy);
            Parallel.For(0, h, ComputeLimits.Options, row =>
            {
                var yy = Math.Clamp(row + oy, 0, h - 1);
                for (var x = 0; x < w; x++)
                {
                    var d = y[row * w + x] - y[yy * w + Math.Clamp(x + ox, 0, w - 1)];
                    diff[row * w + x] = d * d;
                }
            });
            BoxMean(diff, w, h, PatchRadius, boxTmp, patch);
            Parallel.For(0, h, ComputeLimits.Options, row =>
            {
                var yy = Math.Clamp(row + oy, 0, h - 1);
                for (int x = 0, i = row * w; x < w; x++, i++)
                {
                    var wt = MathF.Exp(-MathF.Max(patch[i] - 2 * sigma2[i], 0) * invH2[i]);
                    sum[i] += wt * y[yy * w + Math.Clamp(x + ox, 0, w - 1)];
                    weightSum[i] += wt;
                    if (wt > maxWeight[i]) maxWeight[i] = wt;
                }
            });
        }

        // der Punkt selbst mit dem größten Gewicht seiner Nachbarn (übliche Wahl), danach mit der Stärke mischen
        var mix = (float)Math.Min(1, 1.5 * strength);
        var result = PixelPool.Rent(n);
        Parallel.For(0, n, ComputeLimits.Options, i =>
        {
            var self = Math.Max(maxWeight[i], 1e-3f);
            var filtered = (sum[i] + self * y[i]) / (weightSum[i] + self);
            result[i] = y[i] + mix * (filtered - y[i]);
        });
        foreach (var used in new[] { sum, weightSum, maxWeight, diff, boxTmp, patch, sigma2, invH2 }) PixelPool.Return(used);
        return result;
    }
}
