using ImageConverter.Core.Ai;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Einstellungen der Gruppe „Körnung“ — unabhängig von der Körnung bei „Haut &amp; Details“.
/// </summary>
/// <param name="Amount">Stärke 0–100 % (Streuung im Mittelton, quadratisch: unten fein einstellbar).</param>
/// <param name="Size">Korngröße 0–100 (fein … grob), in Pixeln der Ausgabe — siehe <see cref="SigmaFor"/>.</param>
/// <param name="Roughness">Rauheit 0 (gleichmäßig) … 100 (klumpig wie Film).</param>
/// <param name="Color">Farbanteil 0 (reines Helligkeitskorn) … 100 % (jeder Kanal für sich).</param>
public sealed record GrainSettings(double Amount, double Size = 40, double Roughness = 30, double Color = 0)
{
    public static readonly GrainSettings None = new(0);

    /// <summary>Streuung im Mittelton bei 100 % (0,12 ≈ 31 Stufen von 255 — kräftiges Filmkorn).</summary>
    public const double MaxStd = 0.12;

    public bool IsNone => Amount < 0.5;

    /// <summary>Streuung des Korns im Mittelton (0…1).</summary>
    public double MidStd => MaxStd * Math.Pow(Math.Clamp(Amount, 0, 100) / 100, 2);

    /// <summary>Korngröße als Gauß-Sigma in Pixeln der Ausgabe.</summary>
    public double Sigma => SigmaFor(Size);

    /// <summary>Regler 0…100 → Sigma: 0 = 0,25 px (Pixel für Pixel), je 20 doppelt so groß, 100 = 8 px.</summary>
    public static double SigmaFor(double size) => 0.25 * Math.Pow(2, Math.Clamp(size, 0, 100) / 20);

    /// <summary>Umkehrung von <see cref="SigmaFor"/>.</summary>
    public static double SizeFor(double sigma) => sigma <= 0.25 ? 0 : Math.Clamp(20 * Math.Log2(sigma / 0.25), 0, 100);

    /// <summary>Stärke (Regler), die im Mittelton diese Streuung ergibt.</summary>
    public static double AmountFor(double midStd) => Math.Clamp(100 * Math.Sqrt(Math.Max(0, midStd) / MaxStd), 0, 100);
}

/// <summary>
/// Körnung über das fertige Bild — immer der letzte Schritt der Kette: nach Entrauschen und Schärfen, auch nach KI
/// und „Haut &amp; Details“ (sonst glättet das eine das Korn des anderen wieder weg).
/// </summary>
/// <remarks>
/// Korn wie bei Film: Stärke als Funktion der Helligkeit (Mitteltöne am meisten, Schwarz und Weiß fast nichts, wie das
/// Kornprofil in <see cref="FaceDetails"/> und AV1 Film Grain), Gauß-gefiltertes weißes Rauschen fest je Bildpixel
/// (gleiches Korn bei jeder Rechnung, kein Flimmern). Rauheit mischt eine gröbere Stufe dazu und lässt die Stärke
/// großflächig schwanken (Klumpen); Farbanteil mischt je Kanal eigenes Korn unter das gemeinsame.
/// Größe und Stärke gelten in Pixeln der Ausgabe. Eine kleinere Fassung (Vorschau, verkleinerter Druck) bekommt das
/// Korn so, wie es nach dem Verkleinern des Exports aussähe: größer verwischt, schwächer (<see cref="AtScale"/>).
/// </remarks>
public static class Grain
{
    private const int StripRows = 256;

    // Rauheit: gröbere Stufe (2,5 × Korngröße) und großflächige Schwankung der Stärke (6 ×)
    private const double CoarseFactor = 2.5;
    private const double CoarseWeight = 1.2;
    private const double ClumpFactor = 6;
    private const double ClumpDepth = 0.7;

    /// <summary>Weichzeichnen beim Verkleinern um den Faktor k: Gauß mit DownscaleBlur / k Pixeln der Ausgabe (an Lanczos 3
    /// abgeglichen, GrainTests.Preview_LooksLikeTheDownscaledExport: 1 px 1,00, 2,8 px 0,92).</summary>
    internal const double DownscaleBlur = 0.28;

    /// <summary>Streuung von weißem Rauschen, ausgedrückt als Gauß-Sigma (1 / (2 √π)).</summary>
    private const double WhiteSigma = 0.2821;

    /// <summary>E[tanh(Z)²] für Z ~ N(0, 1): mittlere Leistung der Klumpen-Schwankung.</summary>
    private static readonly double TanhPower = TanhSquaredMean();

    /// <summary>
    /// Körnung auf einen Pixelpuffer (B, G, R, A, sRGB-kodiert 0…1). Liefert einen neuen Puffer aus dem
    /// <see cref="PixelPool"/>; ohne Wirkung den Puffer selbst. <paramref name="pixels"/> bleibt unverändert.
    /// </summary>
    /// <param name="scale">Größe dieses Bildes relativ zur Ausgabe (Vorschau &lt; 1); 1 = das Bild ist die Ausgabe.</param>
    public static float[] Apply(float[] pixels, int width, int height, GrainSettings? grain, double scale = 1)
    {
        if (grain is null || grain.IsNone || width <= 0 || height <= 0) return pixels;

        var mid = (float)grain.MidStd;
        var rough = Math.Clamp(grain.Roughness / 100, 0, 1);
        var color = Math.Clamp(grain.Color / 100, 0, 1);
        var fineSigma = grain.Sigma;

        // Stufen in Pixeln dieses Bildes, mit der Abschwächung durchs Verkleinern
        var (fine, fineAmp) = AtScale(fineSigma, scale);
        var (coarse, coarseAmp) = AtScale(fineSigma * CoarseFactor, scale);
        var clump = fineSigma * ClumpFactor * Math.Min(1, scale);
        var c = CoarseWeight * rough;
        var m = (float)(ClumpDepth * rough);
        var lumaNorm = (float)(1 / Math.Sqrt(1 + c * c));
        var clumpNorm = (float)(1 / Math.Sqrt(1 + m * m * TanhPower));
        var (wLuma, wOwn) = ((float)Math.Sqrt(1 - color), (float)Math.Sqrt(color));
        var (aFine, aCoarse) = ((float)fineAmp, (float)(coarseAmp * c));

        var result = PixelPool.Rent(pixels.Length);
        for (var y0 = 0; y0 < height; y0 += StripRows)
        {
            var rows = Math.Min(StripRows, height - y0);
            var lumaFine = Field(y0, width, rows, fine, seed: 11);
            var lumaCoarse = rough > 0 ? Field(y0, width, rows, coarse, seed: 12) : null;
            var clumps = rough > 0 ? Field(y0, width, rows, clump, seed: 13) : null;
            float[]? r = null, g = null, b = null;
            if (color > 0)
            {
                b = Field(y0, width, rows, fine, seed: 21);
                g = Field(y0, width, rows, fine, seed: 22);
                r = Field(y0, width, rows, fine, seed: 23);
            }

            Parallel.For(0, rows, row =>
            {
                var o = (long)(y0 + row) * width * 4;
                for (int x = 0, k = row * width; x < width; x++, k++, o += 4)
                {
                    float pb = pixels[o], pg = pixels[o + 1], pr = pixels[o + 2];
                    var lum = 0.2126f * pr + 0.7152f * pg + 0.0722f * pb;
                    var s = mid * Weight(lum);
                    if (clumps is not null) s *= (1 + m * MathF.Tanh(clumps[k])) * clumpNorm;

                    var l = aFine * lumaFine[k];
                    if (lumaCoarse is not null) l += aCoarse * lumaCoarse[k];
                    l *= lumaNorm;

                    float nb = l, ng = l, nr = l;
                    if (b is not null)
                    {
                        var own = wOwn * (float)fineAmp;
                        nb = wLuma * l + own * b[k];
                        ng = wLuma * l + own * g![k];
                        nr = wLuma * l + own * r![k];
                    }
                    result[o] = Math.Clamp(pb + s * nb, 0, 1);
                    result[o + 1] = Math.Clamp(pg + s * ng, 0, 1);
                    result[o + 2] = Math.Clamp(pr + s * nr, 0, 1);
                    result[o + 3] = pixels[o + 3];
                }
            });

            foreach (var f in new[] { lumaFine, lumaCoarse, clumps, r, g, b }) PixelPool.Return(f);
        }
        return result;
    }

    /// <summary>Stärke je Helligkeit wie bei Film: Mitteltöne 1, zu Schwarz und Weiß hin auslaufend.</summary>
    internal static float Weight(float lum)
    {
        var t = 4 * lum * (1 - lum);
        return t <= 0 ? 0 : t >= 1 ? 1 : MathF.Sqrt(t);
    }

    /// <summary>
    /// Korn der Größe <paramref name="sigma"/> (Pixel der Ausgabe), gesehen in einem um <paramref name="scale"/>
    /// verkleinerten Bild: Sigma in dessen Pixeln und wie viel von der Streuung übrig bleibt. Verkleinern wirkt wie ein
    /// Gauß von <see cref="DownscaleBlur"/>/scale; gefiltertes Rauschen hat eine Streuung umgekehrt proportional zum
    /// Sigma (weißes Rauschen zählt wie Sigma <see cref="WhiteSigma"/>).
    /// </summary>
    public static (double Sigma, double Amplitude) AtScale(double sigma, double scale)
    {
        if (scale >= 1) return (sigma * scale, 1);
        var blur = DownscaleBlur / scale;
        var spread = Math.Sqrt(sigma * sigma + WhiteSigma * WhiteSigma);
        return (scale * Math.Sqrt(sigma * sigma + blur * blur), spread / Math.Sqrt(spread * spread + blur * blur));
    }

    /// <summary>
    /// Korn mit Streuung 1 für die Zeilen <paramref name="y0"/> … der vollen Bildbreite: weißes Rauschen fest je
    /// Bildpixel, mit <paramref name="sigma"/> weichgezeichnet. Streifenweise mit Rand gerechnet — dasselbe Ergebnis wie
    /// für das ganze Bild auf einmal, aber ohne Ebenen in voller Bildgröße.
    /// </summary>
    internal static float[] Field(int y0, int width, int rows, double sigma, int seed)
    {
        var blur = sigma >= 0.3;
        var pad = blur ? (int)Math.Ceiling(3.5 * sigma) + 2 : 0;
        int pw = width + 2 * pad, ph = rows + 2 * pad;
        var field = PixelPool.Rent(pw * ph);
        Parallel.For(0, ph, row =>
        {
            var y = y0 - pad + row;
            for (var x = 0; x < pw; x++) field[row * pw + x] = Noise(x - pad, y, seed);
        });

        var result = PixelPool.Rent(width * rows);
        if (!blur)
        {
            Array.Copy(field, result, width * rows);
        }
        else
        {
            Blur.Gauss(field, pw, ph, sigma);
            var gain = (float)(1 / KernelEnergy(sigma));   // Streuung nach dem Weichzeichnen zurück auf 1
            Parallel.For(0, rows, row =>
            {
                var from = (row + pad) * pw + pad;
                for (var x = 0; x < width; x++) result[row * width + x] = field[from + x] * gain;
            });
        }
        PixelPool.Return(field);
        return result;
    }

    /// <summary>
    /// Streuung von weißem Rauschen (Streuung 1) nach <see cref="Blur.Gauss"/> mit diesem Sigma: Σk² des 1-D-Kerns,
    /// für zwei Richtungen einmal (Varianz (Σk²)², Streuung Σk²). Mit demselben Kern bzw. denselben Boxen wie dort.
    /// </summary>
    internal static double KernelEnergy(double sigma)
    {
        double[] kernel;
        if (sigma < Blur.ExactGaussBelow)
        {
            kernel = Blur.GaussKernel(sigma).Select(v => (double)v).ToArray();
        }
        else
        {
            kernel = [1.0];
            foreach (var r in Blur.BoxRadii(sigma, passes: 3))
            {
                var box = Enumerable.Repeat(1.0 / (2 * r + 1), 2 * r + 1).ToArray();
                var next = new double[kernel.Length + box.Length - 1];
                for (var i = 0; i < kernel.Length; i++)
                for (var j = 0; j < box.Length; j++)
                    next[i + j] += kernel[i] * box[j];
                kernel = next;
            }
        }
        return kernel.Sum(v => v * v);
    }

    /// <summary>
    /// Normalverteilt genug, Streuung 1, fest je Pixelort: Summe von vier gleichverteilten 16-Bit-Zahlen aus zwei
    /// Hashes (Irwin-Hall) — billiger als Box-Muller, für Korn nicht zu unterscheiden.
    /// </summary>
    private static float Noise(int x, int y, int seed)
    {
        var h1 = Hash((uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)seed * 83492791u);
        var h2 = Hash(h1 ^ 0x9E3779B9u);
        const float unit = 1f / 65536;
        var sum = (h1 & 0xFFFF) * unit + (h1 >> 16) * unit + (h2 & 0xFFFF) * unit + (h2 >> 16) * unit;
        return (sum - 2) * 1.7320508f;   // Streuung von 4 × U(0, 1) ist 1/√3
    }

    private static uint Hash(uint v)
    {
        v ^= v >> 16; v *= 0x7feb352du;
        v ^= v >> 15; v *= 0x846ca68bu;
        v ^= v >> 16;
        return v;
    }

    private static double TanhSquaredMean()
    {
        // ∫ tanh(z)² φ(z) dz, Trapez über ±8
        double sum = 0;
        const double step = 0.001;
        for (var z = -8.0; z <= 8; z += step)
        {
            var t = Math.Tanh(z);
            sum += t * t * Math.Exp(-z * z / 2);
        }
        return sum * step / Math.Sqrt(2 * Math.PI);
    }

    // ------------------------------------------------------------ Aus dem Foto ermitteln

    /// <summary>Höchstens so viele Pixel messen (Mitte des Bildes) — Korn ist überall gleich, das spart Zeit und Speicher.</summary>
    private const int MeasureMaxPixels = 12_000_000;

    private const int Block = 16;

    /// <summary>
    /// Korn des Fotos messen und in Regler umrechnen: Stärke und Größe aus dem Kornprofil
    /// (<see cref="FaceDetails.MeasureGrain"/>, ruhigste Stellen je Helligkeit), Farbanteil aus dem Zusammenhang der
    /// drei Kanäle an denselben Stellen (gleich laufend = Helligkeitskorn, unabhängig = Farbkorn). Rauheit lässt sich so
    /// nicht messen — das Korn eines Fotos ist gleichmäßig, also 0.
    /// </summary>
    /// <param name="pixels">Foto (B, G, R, A, 0…1), möglichst vor dem Entrauschen und vor der KI.</param>
    /// <param name="outputScale">Größe der Ausgabe relativ zu diesem Foto (kleiner &lt; 1).</param>
    /// <returns>null: kein Korn messbar (zu klein, ganz glatt).</returns>
    public static GrainSettings? Measure(float[] pixels, int width, int height, double outputScale)
    {
        var (rgb, w, h) = CenterRgb(pixels, width, height);
        var profile = FaceDetails.MeasureGrain(rgb, w, h);
        if (profile.SigmaByLevel.All(s => s <= 1e-5f)) return null;

        var color = MeasureColorShare(rgb, w, h);

        // Kurve Weight() an das gemessene Profil legen (kleinste Quadrate über die Helligkeitsstufen)
        double num = 0, den = 0;
        var levels = profile.SigmaByLevel.Length;
        for (var l = 0; l < levels; l++)
        {
            var wy = Weight((l + 0.5f) / levels);
            num += wy * profile.SigmaByLevel[l];
            den += wy * wy;
        }
        var lumaStd = den > 0 ? num / den : 0;

        // Gemessen ist die Streuung der Helligkeit; Farbkorn je Kanal trägt nur zum Teil dazu bei
        const double lumaWeights2 = 0.2126 * 0.2126 + 0.7152 * 0.7152 + 0.0722 * 0.0722;
        var channelStd = lumaStd / Math.Sqrt((1 - color) + color * lumaWeights2);

        // In Pixel und Stärke der Ausgabe umrechnen
        var (sigma, amp) = AtScale(profile.GrainSigma, outputScale);
        return new GrainSettings(
            Math.Round(GrainSettings.AmountFor(channelStd * amp)),
            Math.Round(GrainSettings.SizeFor(sigma)),
            0,
            Math.Round(color * 100));
    }

    /// <summary>RGB verschachtelt (für <see cref="FaceDetails.MeasureGrain"/>), höchstens <see cref="MeasureMaxPixels"/> aus der Mitte.</summary>
    private static (float[] Rgb, int W, int H) CenterRgb(float[] bgra, int width, int height)
    {
        var f = Math.Min(1, Math.Sqrt((double)MeasureMaxPixels / ((long)width * height)));
        int w = Math.Max(1, (int)(width * f)), h = Math.Max(1, (int)(height * f));
        int x0 = (width - w) / 2, y0 = (height - h) / 2;
        var rgb = new float[(long)w * h * 3];
        Parallel.For(0, h, row =>
        {
            for (int x = 0; x < w; x++)
            {
                var s = ((long)(y0 + row) * width + x0 + x) * 4;
                var d = ((long)row * w + x) * 3;
                rgb[d] = bgra[s + 2];
                rgb[d + 1] = bgra[s + 1];
                rgb[d + 2] = bgra[s];
            }
        });
        return (rgb, w, h);
    }

    /// <summary>
    /// Farbanteil 0…1: 1 − mittlerer Zusammenhang (Korrelation) der Kanäle im Hochpass, in den ruhigsten 20 % der
    /// Blöcke — dort ist nur Korn. Im Modell von <see cref="Apply"/> ist die Korrelation genau 1 − Farbanteil.
    /// </summary>
    internal static double MeasureColorShare(float[] rgb, int w, int h)
    {
        if (w < Block * 2 || h < Block * 2) return 0;
        var blurred = (float[])rgb.Clone();
        Blur.Gauss(blurred, w, h, 2.0, channels: 3);

        int bw = w / Block, bh = h / Block;
        var stats = new (double Luma, double[] S)[bw * bh];
        Parallel.For(0, bh, by =>
        {
            for (var bx = 0; bx < bw; bx++)
            {
                // Summen: r, g, b, rr, gg, bb, rg, gb, rb, Helligkeit
                var s = new double[10];
                double mean = 0;
                for (var yy = by * Block; yy < (by + 1) * Block; yy++)
                for (var xx = bx * Block; xx < (bx + 1) * Block; xx++)
                {
                    var i = ((long)yy * w + xx) * 3;
                    double r = rgb[i] - blurred[i], g = rgb[i + 1] - blurred[i + 1], b = rgb[i + 2] - blurred[i + 2];
                    s[0] += r; s[1] += g; s[2] += b;
                    s[3] += r * r; s[4] += g * g; s[5] += b * b;
                    s[6] += r * g; s[7] += g * b; s[8] += r * b;
                    var lr = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    s[9] += lr * lr;
                    mean += 0.2126 * rgb[i] + 0.7152 * rgb[i + 1] + 0.0722 * rgb[i + 2];
                }
                stats[by * bw + bx] = (mean / (Block * Block), s);
            }
        });

        var usable = stats.Where(t => t.Luma is > 0.03 and < 0.97).OrderBy(t => t.S[9]).ToList();
        var quiet = usable.Take(Math.Max(1, usable.Count / 5)).ToList();
        if (quiet.Count < 8) return 0;

        var sum = new double[9];
        foreach (var (_, s) in quiet)
            for (var k = 0; k < 9; k++) sum[k] += s[k];
        var n = (double)quiet.Count * Block * Block;
        double Cov(int a, int b, int ab) => sum[ab] / n - sum[a] / n * (sum[b] / n);
        double Var(int a, int aa) => Math.Max(1e-12, sum[aa] / n - Math.Pow(sum[a] / n, 2));
        var rg = Cov(0, 1, 6) / Math.Sqrt(Var(0, 3) * Var(1, 4));
        var gb = Cov(1, 2, 7) / Math.Sqrt(Var(1, 4) * Var(2, 5));
        var rb = Cov(0, 2, 8) / Math.Sqrt(Var(0, 3) * Var(2, 5));
        return Math.Clamp(1 - (rg + gb + rb) / 3, 0, 1);
    }
}
