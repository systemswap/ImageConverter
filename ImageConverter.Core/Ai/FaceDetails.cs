using ImageConverter.Core.Imaging;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Ai;

/// <summary>Gesichtsbereiche (Face Parsing): je Pixel des ausgerichteten Ausschnitts eine Klasse.</summary>
public interface IFaceParser
{
    /// <param name="crop">Ausschnitt RGB planar 0…1, <paramref name="size"/> × <paramref name="size"/>.</param>
    /// <returns>Klasse je Pixel (CelebAMask-HQ, siehe <see cref="FaceDetails"/>).</returns>
    byte[] Parse(float[] crop, int size, CancellationToken ct);
}

/// <summary>BiSeNet über ONNX (FaceFusion): 512 × 512, RGB, ImageNet-normiert, 19 Klassen.</summary>
public sealed class OnnxFaceParser(InferenceSession session) : IFaceParser
{
    private const int Size = 512;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    public byte[] Parse(float[] crop, int size, CancellationToken ct)
    {
        var plane = Size * Size;
        var input = new float[3 * plane];
        for (var c = 0; c < 3; c++)
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // nächster Nachbar, falls der Ausschnitt eine andere Größe hat (alle Modelle bisher 512)
            var v = crop[c * size * size + y * size / Size * size + x * size / Size];
            input[c * plane + y * Size + x] = (v - Mean[c]) / Std[c];
        }

        ct.ThrowIfCancellationRequested();
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Size, Size]);
        using var outputs = session.Run(new RunOptions(), new Dictionary<string, OrtValue> { [session.InputNames[0]] = value },
            [session.OutputNames[0]]);
        var logits = outputs[0].GetTensorDataAsSpan<float>();
        var classes = logits.Length / plane;

        var labels = new byte[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var p = y * Size / size * Size + x * Size / size;
            var best = 0;
            for (var k = 1; k < classes; k++)
                if (logits[k * plane + p] > logits[best * plane + p]) best = k;
            labels[y * size + x] = (byte)best;
        }
        return labels;
    }
}

/// <summary>Kornprofil eines Fotos: Stärke je Helligkeit (wie AV1 Film Grain) und Korngröße.</summary>
/// <param name="SigmaByLevel">Standardabweichung des Korns (0…1) je Helligkeitsstufe, gleichmäßig über 0…1.</param>
/// <param name="GrainSigma">Korngröße als Gauß-Sigma in Bildpixeln (0 = weißes Rauschen, Pixel für Pixel).</param>
/// <param name="CoarseGain">Wie viel der Mess-Hochpass von Korn dieser Größe (Streuung 1) sieht — zum Umrechnen.</param>
public sealed record GrainProfile(float[] SigmaByLevel, double GrainSigma, double CoarseGain = 1)
{
    public static GrainProfile None { get; } = new(new float[GrainLevels], 0);

    public const int GrainLevels = 8;

    /// <summary>Kornstärke bei Helligkeit <paramref name="y"/> (0…1), linear zwischen den Stufen.</summary>
    public float SigmaAt(float y)
    {
        var t = Math.Clamp(y, 0f, 1f) * GrainLevels - 0.5f;
        var i = Math.Clamp((int)Math.Floor(t), 0, GrainLevels - 2);
        var f = Math.Clamp(t - i, 0f, 1f);
        return SigmaByLevel[i] + (SigmaByLevel[i + 1] - SigmaByLevel[i]) * f;
    }

    public float Average => SigmaByLevel.Average();
}

/// <summary>
/// Details um die KI herum: was das Original an Struktur hatte (vor der KI eingelesen), was das Modell davon
/// weggeglättet hat (danach verglichen) und wie das Korn des Fotos aussieht. Gerechnet wird einmal je Lauf;
/// die Regler „Falten &amp; Züge“ und „Körnung“ mischen danach live.
/// </summary>
/// <remarks>
/// Vorbilder: Zerlegung in Detailstufen (Laplace-Pyramide, Burt &amp; Adelson), Detail-Übertragung zwischen zwei
/// Aufnahmen (Petschnigg u. a. 2004), Korn wie AV1 Film Grain Synthesis (Norkin &amp; Birkbeck 2018: Rest aus
/// Original minus entrauschtem Bild, Stärke als Funktion der Helligkeit), Gesichtsbereiche mit BiSeNet.
/// </remarks>
public static class FaceDetails
{
    // Klassen des Face Parsings (CelebAMask-HQ)
    public const byte Skin = 1, BrowLeft = 2, BrowRight = 3, EyeLeft = 4, EyeRight = 5, Glasses = 6,
        EarLeft = 7, EarRight = 8, Earring = 9, Nose = 10, Mouth = 11, LipUpper = 12, LipLower = 13, Neck = 14;

    /// <summary>Bereiche, in die Falten aus dem Original zurückdürfen: Haut, Nase, Ohren, Hals.</summary>
    private static bool TakesFolds(byte label) => label is Skin or Nose or EarLeft or EarRight or Neck;

    /// <summary>Gröbere Detailstufe „Falten &amp; Züge“: von etwa 1 bis 4 Bildpixeln.</summary>
    internal const double FoldsFrom = 1.0, FoldsTo = 4.0;

    /// <summary>Abstand der Falten zu Augen, Lippen und Gesichtsrand (Ausschnitt-Pixel): drei Viertel der Stufenbreite.</summary>
    internal static double FoldsKeepAway(double scale) => Math.Clamp(FoldsTo * scale * 0.75, 2, 30);

    // ------------------------------------------------------------ Bereiche

    /// <summary>
    /// Wo Falten zurückdürfen (0…1, weich): Haut ja, Augen, Brauen, Lippen, Zähne, Haare nein — mit Abstand
    /// <paramref name="keepAway"/> (Ausschnitt-Pixel) zu diesen Bereichen. Ohne Face Parsing
    /// ersatzweise alles außer weichen Kreisen um Augen und Mund (aus der Normlage der fünf Punkte).
    /// </summary>
    internal static float[] FoldMask(byte[]? labels, int size, IReadOnlyList<(double X, double Y)> template, double keepAway = 0)
    {
        var mask = new float[size * size];
        if (labels is not null)
        {
            for (var i = 0; i < mask.Length; i++) mask[i] = TakesFolds(labels[i]) ? 1 : 0;
        }
        else
        {
            var mouth = ((template[3].X + template[4].X) / 2 * size, (template[3].Y + template[4].Y) / 2 * size);
            (double X, double Y, double R)[] holes =
            [
                (template[0].X * size, template[0].Y * size, 0.075 * size),
                (template[1].X * size, template[1].Y * size, 0.075 * size),
                (mouth.Item1, mouth.Item2, 0.12 * size),
            ];
            Parallel.For(0, size, y =>
            {
                for (var x = 0; x < size; x++)
                {
                    var keep = 1.0;
                    foreach (var (hx, hy, r) in holes)
                    {
                        var d = Math.Sqrt((x - hx) * (x - hx) + (y - hy) * (y - hy)) / r;
                        keep = Math.Min(keep, Math.Clamp((d - 0.8) / 0.4, 0, 1));
                    }
                    mask[y * size + x] = (float)keep;
                }
            });
        }

        // Abstand zu Augen, Lippen, Gesichtsrand: so weit, wie die Detailstufe reicht — sonst kämen deren Kanten
        // aus dem Original mit (doppelte Konturen). Weichzeichnen und hoch ansetzen schrumpft den Bereich um etwa
        // 1,9 Sigma.
        if (keepAway > 0)
        {
            var spread = (float[])mask.Clone();
            Blur.Gauss(spread, size, size, keepAway / 1.9);
            for (var i = 0; i < mask.Length; i++) mask[i] = spread[i] > 0.97f ? mask[i] : 0;
        }

        // weicher Rand, damit keine Kante an Augen und Lippen entsteht
        Blur.Gauss(mask, size, size, size / 170.0);
        return mask;
    }


    /// <summary>
    /// Für die Karte: Gesichtsteile, die vom Modell bleiben — Augen, Brauen, Brille, Lippen, Zähne (0…1).
    /// Ohne Face Parsing die Kreise um Augen und Mund.
    /// </summary>
    internal static float[] KeptMask(byte[]? labels, int size, IReadOnlyList<(double X, double Y)> template)
    {
        float[] kept;
        if (labels is not null)
        {
            kept = new float[size * size];
            for (var i = 0; i < kept.Length; i++)
                kept[i] = labels[i] is BrowLeft or BrowRight or EyeLeft or EyeRight or Glasses or Mouth or LipUpper or LipLower ? 1 : 0;
            Blur.Gauss(kept, size, size, size / 256.0);
        }
        else
        {
            kept = FoldMask(null, size, template);
            for (var i = 0; i < kept.Length; i++) kept[i] = 1 - kept[i];
        }
        return kept;
    }
    // ------------------------------------------------------------ Falten & Züge

    /// <summary>
    /// Was das Modell an Falten und Zügen weggeglättet hat: mittlere Detailstufe (Helligkeit) des Originals minus
    /// die des Modells, nur auf der Haut und nur dort, wo beide übereinanderpassen — wo das Modell eine Form
    /// verschoben hat (Augen, Mundwinkel), entstünden sonst doppelte Konturen.
    /// </summary>
    /// <param name="crop">Original im Ausschnitt (RGB planar 0…1).</param>
    /// <param name="enhanced">Ergebnis des Modells (RGB planar 0…1).</param>
    /// <param name="scale">Ausschnitt-Pixel je Bildpixel (die Stufe gilt in Bildpixeln).</param>
    /// <returns>Helligkeitsunterschied je Ausschnitt-Pixel — mit dem Regler mal 0…1 aufs Ergebnis.</returns>
    internal static float[] Folds(float[] crop, float[] enhanced, int size, double scale, float[] mask)
    {
        var s1 = Math.Clamp(FoldsFrom * scale, 0.7, 12);
        var s2 = Math.Clamp(FoldsTo * scale, 2.5, 40);
        var mo = Band(Luma(crop, size), size, s1, s2);
        var me = Band(Luma(enhanced, size), size, s1, s2);

        // Passen beide zusammen? Örtliche Energie und Korrelation der Stufe über die Breite der Stufe
        var n = size * size;
        float[] eo = new float[n], ee = new float[n], cross = new float[n];
        for (var i = 0; i < n; i++) { eo[i] = mo[i] * mo[i]; ee[i] = me[i] * me[i]; cross[i] = mo[i] * me[i]; }
        Blur.Gauss(eo, size, size, s2);
        Blur.Gauss(ee, size, size, s2);
        Blur.Gauss(cross, size, size, s2);

        var folds = new float[n];
        const float eps = 1e-7f;
        for (var i = 0; i < n; i++)
        {
            var corr = cross[i] / MathF.Sqrt(eo[i] * ee[i] + eps);
            var r = ee[i] / (ee[i] + eo[i] + eps);                  // 0 = Modell glatt, 1 = nur Modell hat Struktur
            var align = Math.Clamp(1 - r * (1 - corr) * 2, 0f, 1f);  // glatt oder gleichlaufend: ja; versetzt: nein
            folds[i] = (mo[i] - me[i]) * align * mask[i];
        }
        return folds;
    }

    /// <summary>Helligkeit (Rec. 709) aus RGB planar.</summary>
    private static float[] Luma(float[] rgb, int size)
    {
        var n = size * size;
        var y = new float[n];
        for (var i = 0; i < n; i++) y[i] = 0.2126f * rgb[i] + 0.7152f * rgb[n + i] + 0.0722f * rgb[2 * n + i];
        return y;
    }

    /// <summary>Detailstufe zwischen zwei Glättungen (Differenz zweier Gauß-Stufen).</summary>
    private static float[] Band(float[] y, int size, double s1, double s2)
    {
        var a = (float[])y.Clone();
        var b = (float[])y.Clone();
        Blur.Gauss(a, size, size, s1);
        Blur.Gauss(b, size, size, s2);
        for (var i = 0; i < a.Length; i++) a[i] -= b[i];
        return a;
    }

    // ------------------------------------------------------------ Korn

    private const double MeasureSigma = 2.0;   // Hochpass für die Stärke
    private const double FineSigma = 0.7;      // zweiter, feinerer Hochpass für die Korngröße
    private const int Block = 16;

    /// <summary>
    /// Kornprofil des Fotos (vor der KI eingelesen): je Helligkeitsstufe die Streuung in den ruhigsten Blöcken
    /// (dort ist nur Korn, kein Motiv) — wie bei AV1 als Funktion der Helligkeit; die Korngröße aus dem
    /// Verhältnis zweier Hochpässe, abgeglichen an künstlichem Korn bekannter Größe.
    /// </summary>
    /// <param name="image">Bild RGB verschachtelt 0…1.</param>
    public static GrainProfile MeasureGrain(float[] image, int w, int h)
    {
        if (w < Block * 2 || h < Block * 2) return GrainProfile.None;

        var y = new float[(long)w * h];
        Parallel.For(0, h, row =>
        {
            for (long x = 0, i = (long)row * w; x < w; x++, i++)
                y[i] = 0.2126f * image[i * 3] + 0.7152f * image[i * 3 + 1] + 0.0722f * image[i * 3 + 2];
        });
        var coarse = HighPassStd(y, w, h, MeasureSigma);
        var fine = HighPassStd(y, w, h, FineSigma);

        // je Helligkeitsstufe die ruhigsten 20 % der Blöcke
        var levels = GrainProfile.GrainLevels;
        var perLevel = Enumerable.Range(0, levels).Select(_ => new List<(float Coarse, float Fine)>()).ToArray();
        foreach (var (mean, c, f) in coarse.Blocks.Zip(fine.Blocks, (a, b) => (a.Mean, a.Std, b.Std)))
        {
            if (mean < 0.03f || mean > 0.97f) continue;   // abgeschnitten: dort ist kein Korn mehr messbar
            perLevel[Math.Min(levels - 1, (int)(mean * levels))].Add((c, f));
        }

        var raw = new float[levels];
        var ratios = new List<double>();
        for (var l = 0; l < levels; l++)
        {
            var list = perLevel[l];
            if (list.Count < 8) { raw[l] = float.NaN; continue; }
            list.Sort((a, b) => a.Coarse.CompareTo(b.Coarse));
            var quiet = list.Take(Math.Max(1, list.Count / 5)).ToList();
            raw[l] = quiet[quiet.Count / 2].Coarse;
            ratios.AddRange(quiet.Where(q => q.Coarse > 1e-5f).Select(q => (double)q.Fine / q.Coarse));
        }
        if (raw.All(float.IsNaN)) return GrainProfile.None;

        // Korngröße: welches künstliche Korn hat dasselbe Verhältnis fein/grob?
        ratios.Sort();
        var ratio = ratios.Count > 0 ? ratios[ratios.Count / 2] : Calibration[0].Ratio;
        var (grainSigma, coarseGain) = Lookup(ratio);

        // Lücken mit der nächsten gemessenen Stufe füllen, Rest-Std in Korn-Std umrechnen
        var sigma = new float[levels];
        for (var l = 0; l < levels; l++)
        {
            var near = Enumerable.Range(0, levels).Where(k => !float.IsNaN(raw[k])).OrderBy(k => Math.Abs(k - l)).First();
            sigma[l] = (float)(raw[near] / coarseGain);
        }
        return new GrainProfile(sigma, grainSigma, coarseGain);
    }


    /// <summary>
    /// Wie viel Korn ein verbessertes Gesicht schon hat (gemessen wie beim Foto, auf der Haut), umgerechnet in einen
    /// Anteil: 1 = glatt, volles Korn nötig; 0 = schon so körnig wie das Foto (manche Modelle erfinden selbst feine
    /// Struktur). „Körnung 100 %“ füllt so nur auf, was bis zum Foto fehlt.
    /// </summary>
    /// <param name="luma">Helligkeit des eingesetzten Gesichts (Bildausschnitt).</param>
    /// <param name="weight">Wo gemessen wird (Haut, 0…1).</param>
    internal static float GrainGain(float[] luma, float[] weight, int w, int h, GrainProfile profile)
    {
        var blurred = (float[])luma.Clone();
        Blur.Gauss(blurred, w, h, MeasureSigma);

        // wie beim Foto: nur die ruhigsten Stellen zählen (dort ist Korn, keine Kontur) — 8er-Blöcke, Gesichter sind klein
        const int block = 8;
        var quiet = new List<(double Std, double Mean)>();
        for (var by = 0; by + block <= h; by += block)
        for (var bx = 0; bx + block <= w; bx += block)
        {
            double sy = 0, sr = 0, sr2 = 0, sw = 0;
            for (var y = by; y < by + block; y++)
            for (var x = bx; x < bx + block; x++)
            {
                var i = y * w + x;
                var r = luma[i] - blurred[i];
                sw += weight[i]; sy += luma[i]; sr += r; sr2 += r * r;
            }
            const int n = block * block;
            if (sw < n * 0.8) continue;   // nicht ganz auf der Haut
            var mean = sr / n;
            quiet.Add((Math.Sqrt(Math.Max(0, sr2 / n - mean * mean)), sy / n));
        }
        if (quiet.Count < 4) return 1;

        quiet.Sort((a, b) => a.Std.CompareTo(b.Std));
        var calm = quiet.Take(Math.Max(1, quiet.Count / 5)).ToList();
        var (faceStd, faceMean) = calm[calm.Count / 2];
        var faceSigma = faceStd / profile.CoarseGain;
        var photoSigma = profile.SigmaAt((float)faceMean);
        if (photoSigma <= 1e-6) return 0;
        var ratio = faceSigma / photoSigma;
        return (float)Math.Sqrt(Math.Max(0, 1 - ratio * ratio));   // Streuungen addieren sich quadratisch
    }
    private sealed record BlockStats(List<(float Mean, float Std)> Blocks);

    /// <summary>Mittelwert der Helligkeit und Streuung des Hochpasses je 16er-Block.</summary>
    private static BlockStats HighPassStd(float[] y, int w, int h, double sigma)
    {
        var blurred = (float[])y.Clone();
        Blur.Gauss(blurred, w, h, sigma);
        int bw = w / Block, bh = h / Block;
        var blocks = new (float Mean, float Std)[bw * bh];
        Parallel.For(0, bh, by =>
        {
            for (var bx = 0; bx < bw; bx++)
            {
                double sum = 0, sumR = 0, sumR2 = 0;
                for (var yy = by * Block; yy < (by + 1) * Block; yy++)
                for (long xx = bx * Block, i = (long)yy * w + bx * Block; xx < (bx + 1) * Block; xx++, i++)
                {
                    var r = y[i] - blurred[i];
                    sum += y[i];
                    sumR += r;
                    sumR2 += r * r;
                }
                const int n = Block * Block;
                var mean = sumR / n;
                blocks[by * bw + bx] = ((float)(sum / n), (float)Math.Sqrt(Math.Max(0, sumR2 / n - mean * mean)));
            }
        });
        return new BlockStats(blocks.ToList());
    }

    // Abgleich: künstliches Korn (Gauß-gefiltertes weißes Rauschen, Streuung 1) — Verhältnis fein/grob und
    // wie viel davon der grobe Hochpass misst. Einmal beim ersten Gebrauch gerechnet.
    private static readonly Lazy<(double Sigma, double Ratio, double CoarseGain)[]> CalibrationTable = new(() =>
    {
        double[] sizes = [0, 0.4, 0.6, 0.8, 1.0, 1.3, 1.7, 2.2, 3.0];
        const int n = 192;
        return sizes.Select(s =>
        {
            var field = GrainField(0, 0, n, n, s, seed: 1234);
            double c = HighPassStd(field, n, n, MeasureSigma).Blocks.Average(b => b.Std);
            double f = HighPassStd(field, n, n, FineSigma).Blocks.Average(b => b.Std);
            return (s, f / c, c);
        }).ToArray();
    });

    private static (double Sigma, double Ratio, double CoarseGain)[] Calibration => CalibrationTable.Value;

    private static (double Sigma, double CoarseGain) Lookup(double ratio)
    {
        var t = Calibration;   // Verhältnis fällt mit wachsender Korngröße
        if (ratio >= t[0].Ratio) return (t[0].Sigma, t[0].CoarseGain);
        for (var i = 1; i < t.Length; i++)
        {
            if (ratio < t[i].Ratio) continue;
            var f = (ratio - t[i - 1].Ratio) / (t[i].Ratio - t[i - 1].Ratio);
            return (t[i - 1].Sigma + (t[i].Sigma - t[i - 1].Sigma) * f,
                t[i - 1].CoarseGain + (t[i].CoarseGain - t[i - 1].CoarseGain) * f);
        }
        return (t[^1].Sigma, t[^1].CoarseGain);
    }

    /// <summary>
    /// Korn mit Streuung 1 für einen Bildausschnitt (Bildkoordinaten <paramref name="x0"/>, <paramref name="y0"/>):
    /// weißes Rauschen fest je Bildpixel (gleiches Korn bei jeder Rechnung, auch wo sich Gesichter überlappen),
    /// mit <paramref name="sigma"/> weichgezeichnet.
    /// </summary>
    internal static float[] GrainField(int x0, int y0, int w, int h, double sigma, int seed = 0)
    {
        var pad = (int)Math.Ceiling(sigma * 3) + 1;
        int pw = w + 2 * pad, ph = h + 2 * pad;
        var field = new float[pw * ph];
        Parallel.For(0, ph, row =>
        {
            for (var x = 0; x < pw; x++)
                field[row * pw + x] = Gaussian(x0 - pad + x, y0 - pad + row, seed);
        });

        if (sigma >= 0.3) Blur.Gauss(field, pw, ph, sigma);

        var result = new float[w * h];
        double sum2 = 0;
        for (var row = 0; row < h; row++)
        for (var x = 0; x < w; x++)
        {
            var v = field[(row + pad) * pw + x + pad];
            result[row * w + x] = v;
            sum2 += v * v;
        }

        // auf Streuung 1 bringen — das Weichzeichnen hat sie verringert
        var k = (float)(1 / Math.Sqrt(Math.Max(1e-12, sum2 / result.Length)));
        for (var i = 0; i < result.Length; i++) result[i] *= k;
        return result;
    }

    /// <summary>Normalverteilter Wert, fest je Pixelort (Hash, Box-Muller).</summary>
    private static float Gaussian(int x, int y, int seed)
    {
        var h1 = Hash((uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)seed * 83492791u);
        var h2 = Hash(h1 ^ 0x9E3779B9u);
        var u1 = (h1 + 1.0) / 4294967297.0;
        var u2 = h2 / 4294967296.0;
        return (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }

    private static uint Hash(uint v)
    {
        v ^= v >> 16; v *= 0x7feb352du;
        v ^= v >> 15; v *= 0x846ca68bu;
        v ^= v >> 16;
        return v;
    }
}
