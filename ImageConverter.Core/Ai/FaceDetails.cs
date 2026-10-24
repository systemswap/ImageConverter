using ImageConverter.Core.Imaging;
using Microsoft.ML.OnnxRuntime;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai;

/// <summary>Face parsing: assigns a region class to every pixel of the aligned face crop.</summary>
public interface IFaceParser
{
    /// <summary>Classifies every pixel of an aligned face crop.</summary>
    /// <param name="crop">Crop as planar RGB 0…1, <paramref name="size"/> × <paramref name="size"/>.</param>
    /// <param name="size">Edge length of the crop in pixels.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <returns>One class per pixel, row by row (CelebAMask-HQ classes, see <see cref="FaceDetails"/>).</returns>
    byte[] Parse(float[] crop, int size, CancellationToken ct);
}

/// <summary>Face parser using BiSeNet through ONNX Runtime (FaceFusion export): 512 × 512, RGB, ImageNet normalisation, 19 classes.</summary>
/// <param name="session">ONNX session of the BiSeNet model; it stays owned by the caller.</param>
public sealed class OnnxFaceParser(InferenceSession session) : IFaceParser
{
    private const int Size = 512;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <inheritdoc/>
    /// <remarks>
    /// Crops of a size other than 512 are resampled to 512 and back with nearest neighbour.
    /// Cancellation is checked before the model runs and also stops a running computation.
    /// </remarks>
    public byte[] Parse(float[] crop, int size, CancellationToken ct)
    {
        var plane = Size * Size;
        var input = new float[3 * plane];
        for (var c = 0; c < 3; c++)
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // Nearest neighbour in case the crop has a different size (all supported models use 512)
            var v = crop[c * size * size + y * size / Size * size + x * size / Size];
            input[c * plane + y * Size + x] = (v - Mean[c]) / Std[c];
        }

        ct.ThrowIfCancellationRequested();
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Size, Size]);
        // Through OnnxSessions.Run, so that the run options stay alive until the computation ends. With "new RunOptions()"
        // directly in the call, the garbage collector could free them in the middle of the computation, causing a crash
        // (access violation) in ONNX Runtime. See docs/FEHLERGEDAECHTNIS.md, No. 69.
        var logits = OnnxSessions.Run(session, new Dictionary<string, OrtValue> { [session.InputNames[0]] = value }, ct);
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

/// <summary>Grain profile of a photo: strength per brightness level (as in AV1 film grain synthesis) and grain size.</summary>
/// <param name="SigmaByLevel">
/// Standard deviation of the grain (0…1) per brightness level; <see cref="GrainLevels"/> levels spread evenly over 0…1.
/// </param>
/// <param name="GrainSigma">Grain size as a Gaussian sigma in image pixels (0 = white noise, pixel by pixel).</param>
/// <param name="CoarseGain">
/// How much of grain of this size (standard deviation 1) the measuring high-pass filter sees; used to convert
/// measured values into grain strength.
/// </param>
public sealed record GrainProfile(float[] SigmaByLevel, double GrainSigma, double CoarseGain = 1)
{
    /// <summary>Gets a profile without grain (all levels 0).</summary>
    public static GrainProfile None { get; } = new(new float[GrainLevels], 0);

    /// <summary>Number of brightness levels in <see cref="SigmaByLevel"/>.</summary>
    public const int GrainLevels = 8;

    /// <summary>Returns the grain strength at brightness <paramref name="y"/>, interpolated linearly between the level centres.</summary>
    /// <param name="y">Brightness, 0…1; values outside are clamped.</param>
    /// <returns>Standard deviation of the grain, 0…1.</returns>
    public float SigmaAt(float y)
    {
        var t = Math.Clamp(y, 0f, 1f) * GrainLevels - 0.5f;
        var i = Math.Clamp((int)Math.Floor(t), 0, GrainLevels - 2);
        var f = Math.Clamp(t - i, 0f, 1f);
        return SigmaByLevel[i] + (SigmaByLevel[i + 1] - SigmaByLevel[i]) * f;
    }

    /// <summary>Gets the mean grain strength over all levels.</summary>
    public float Average => SigmaByLevel.Average();
}

/// <summary>
/// Detail analysis around the face model: the structure the original had (read before the model), what the
/// model smoothed away (compared afterwards) and what the grain of the photo looks like.
/// </summary>
/// <remarks>
/// <para>
/// The analysis runs once per AI run; the "Folds &amp; lines" and "Grain" controls then blend live.
/// </para>
/// <para>
/// Sources: decomposition into detail bands (Laplacian pyramid, Burt &amp; Adelson), detail transfer between two
/// exposures (Petschnigg et al. 2004), grain as in AV1 film grain synthesis (Norkin &amp; Birkbeck 2018: residual
/// of the original minus the denoised image, strength as a function of brightness), face regions with BiSeNet.
/// </para>
/// </remarks>
public static class FaceDetails
{
    /// <summary>Face parsing class indices (CelebAMask-HQ) as returned by <see cref="IFaceParser.Parse"/>.</summary>
    public const byte Skin = 1, BrowLeft = 2, BrowRight = 3, EyeLeft = 4, EyeRight = 5, Glasses = 6,
        EarLeft = 7, EarRight = 8, Earring = 9, Nose = 10, Mouth = 11, LipUpper = 12, LipLower = 13, Neck = 14;

    /// <summary>Regions into which folds from the original may return: skin, nose, ears, neck.</summary>
    private static bool TakesFolds(byte label) => label is Skin or Nose or EarLeft or EarRight or Neck;

    /// <summary>Coarser detail band "folds &amp; lines": from about 1 to 4 image pixels.</summary>
    internal const double FoldsFrom = 1.0, FoldsTo = 4.0;

    /// <summary>
    /// Distance of the folds from eyes, lips and face outline (crop pixels): three quarters of the band width.
    /// Without this distance the band reaches across the skin boundary and edges of eyelids, lips and face
    /// outline come back from the blurred original. See docs/FEHLERGEDAECHTNIS.md, No. 24.
    /// </summary>
    internal static double FoldsKeepAway(double scale) => Math.Clamp(FoldsTo * scale * 0.75, 2, 30);

    // ------------------------------------------------------------ Regions

    /// <summary>
    /// Where folds may return (0…1, soft): skin yes; eyes, brows, lips, teeth, hair no, with a distance of
    /// <paramref name="keepAway"/> (crop pixels) from the excluded regions. Without face parsing, everything
    /// except soft circles around eyes and mouth (from the reference positions of the five landmarks).
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
            Parallel.For(0, size, ComputeLimits.Options, y =>
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

        // Distance from eyes, lips and face outline as far as the detail band reaches; otherwise their edges would
        // come along from the original (double contours). Blurring and thresholding high shrinks the region by
        // about 1.9 sigma.
        if (keepAway > 0)
        {
            var spread = (float[])mask.Clone();
            Blur.Gauss(spread, size, size, keepAway / 1.9);
            for (var i = 0; i < mask.Length; i++) mask[i] = spread[i] > 0.97f ? mask[i] : 0;
        }

        // Soft edge so that no hard edge appears at eyes and lips
        Blur.Gauss(mask, size, size, size / 170.0);
        return mask;
    }


    /// <summary>
    /// For the map: face parts that stay from the model, i.e. eyes, brows, glasses, lips, inner mouth and teeth (0…1).
    /// Without face parsing, the circles around eyes and mouth.
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
    // ------------------------------------------------------------ Folds & lines

    /// <summary>
    /// What the model smoothed away in folds and lines: the middle detail band (brightness) of the original minus
    /// that of the model, only on the skin and only where both are aligned. Where the model has moved a shape
    /// (eyes, mouth corners), double contours would appear otherwise.
    /// </summary>
    /// <param name="crop">Original in the crop (planar RGB 0…1).</param>
    /// <param name="enhanced">Model result (planar RGB 0…1).</param>
    /// <param name="size">Edge length of the crop in pixels.</param>
    /// <param name="scale">Crop pixels per image pixel (the band is defined in image pixels).</param>
    /// <param name="mask">Where folds may return (<see cref="FoldMask"/>).</param>
    /// <returns>Brightness difference per crop pixel; multiplied by the slider value 0…1 and added to the result.</returns>
    internal static float[] Folds(float[] crop, float[] enhanced, int size, double scale, float[] mask)
    {
        var s1 = Math.Clamp(FoldsFrom * scale, 0.7, 12);
        var s2 = Math.Clamp(FoldsTo * scale, 2.5, 40);
        var mo = Band(Luma(crop, size), size, s1, s2);
        var me = Band(Luma(enhanced, size), size, s1, s2);

        // Are both aligned? Local energy and correlation of the band over the width of the band
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
            var r = ee[i] / (ee[i] + eo[i] + eps);                  // 0 = model smooth, 1 = only the model has structure
            var align = Math.Clamp(1 - r * (1 - corr) * 2, 0f, 1f);  // smooth or in phase: yes; shifted: no
            folds[i] = (mo[i] - me[i]) * align * mask[i];
        }
        return folds;
    }

    /// <summary>Luma (ITU-R BT.709 coefficients) from planar RGB.</summary>
    private static float[] Luma(float[] rgb, int size)
    {
        var n = size * size;
        var y = new float[n];
        for (var i = 0; i < n; i++) y[i] = 0.2126f * rgb[i] + 0.7152f * rgb[n + i] + 0.0722f * rgb[2 * n + i];
        return y;
    }

    /// <summary>Detail band between two blur levels (difference of Gaussians).</summary>
    private static float[] Band(float[] y, int size, double s1, double s2)
    {
        var a = (float[])y.Clone();
        var b = (float[])y.Clone();
        Blur.Gauss(a, size, size, s1);
        Blur.Gauss(b, size, size, s2);
        for (var i = 0; i < a.Length; i++) a[i] -= b[i];
        return a;
    }

    // ------------------------------------------------------------ Grain

    private const double MeasureSigma = 2.0;   // high-pass filter for the strength
    private const double FineSigma = 0.7;      // second, finer high-pass filter for the grain size
    private const int Block = 16;

    /// <summary>
    /// Measures the grain profile of a photo (before the model runs).
    /// </summary>
    /// <remarks>
    /// Per brightness level, the spread in the quietest 20 % of the 16 × 16 blocks is taken (there is only grain,
    /// no subject), as a function of brightness as in AV1. The grain size follows from the ratio of two high-pass
    /// filters, calibrated against synthetic grain of known size. Levels without enough blocks take the value of
    /// the nearest measured level.
    /// </remarks>
    /// <param name="image">Image as interleaved RGB 0…1.</param>
    /// <param name="w">Image width in pixels.</param>
    /// <param name="h">Image height in pixels.</param>
    /// <returns>
    /// The measured profile, or <see cref="GrainProfile.None"/> if the width or height is below 32 pixels or no
    /// brightness level has enough usable blocks.
    /// </returns>
    public static GrainProfile MeasureGrain(float[] image, int w, int h)
    {
        if (w < Block * 2 || h < Block * 2) return GrainProfile.None;

        var y = new float[(long)w * h];
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (long x = 0, i = (long)row * w; x < w; x++, i++)
                y[i] = 0.2126f * image[i * 3] + 0.7152f * image[i * 3 + 1] + 0.0722f * image[i * 3 + 2];
        });
        var coarse = HighPassStd(y, w, h, MeasureSigma);
        var fine = HighPassStd(y, w, h, FineSigma);

        // Per brightness level the quietest 20 % of the blocks
        var levels = GrainProfile.GrainLevels;
        var perLevel = Enumerable.Range(0, levels).Select(_ => new List<(float Coarse, float Fine)>()).ToArray();
        foreach (var (mean, c, f) in coarse.Blocks.Zip(fine.Blocks, (a, b) => (a.Mean, a.Std, b.Std)))
        {
            if (mean < 0.03f || mean > 0.97f) continue;   // clipped: no grain can be measured there
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

        // Grain size: which synthetic grain has the same fine/coarse ratio?
        ratios.Sort();
        var ratio = ratios.Count > 0 ? ratios[ratios.Count / 2] : Calibration[0].Ratio;
        var (grainSigma, coarseGain) = Lookup(ratio);

        // Fill gaps with the nearest measured level, convert residual std into grain std
        var sigma = new float[levels];
        for (var l = 0; l < levels; l++)
        {
            var near = Enumerable.Range(0, levels).Where(k => !float.IsNaN(raw[k])).OrderBy(k => Math.Abs(k - l)).First();
            sigma[l] = (float)(raw[near] / coarseGain);
        }
        return new GrainProfile(sigma, grainSigma, coarseGain);
    }


    /// <summary>
    /// How much grain a restored face already has (measured as for the photo, on the skin), converted into a
    /// share: 1 = smooth, full grain needed; 0 = already as grainy as the photo (some models invent fine
    /// structure themselves). "Grain 100 %" thus only fills up what is missing compared with the photo.
    /// </summary>
    /// <remarks>
    /// Only the quietest blocks count, as for the photo. Taking the total spread of the skin fails: the contours
    /// of nose and cheeks count as well and no grain is added at all. See docs/FEHLERGEDAECHTNIS.md, No. 24.
    /// </remarks>
    /// <param name="luma">Brightness of the pasted face (image area).</param>
    /// <param name="weight">Where to measure (skin, 0…1).</param>
    internal static float GrainGain(float[] luma, float[] weight, int w, int h, GrainProfile profile)
    {
        var blurred = (float[])luma.Clone();
        Blur.Gauss(blurred, w, h, MeasureSigma);

        // As for the photo, only the quietest areas count (grain there, no contour); 8 × 8 blocks because faces are small
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
            if (sw < n * 0.8) continue;   // not entirely on the skin
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
        return (float)Math.Sqrt(Math.Max(0, 1 - ratio * ratio));   // standard deviations add in quadrature
    }
    private sealed record BlockStats(List<(float Mean, float Std)> Blocks);

    /// <summary>Mean brightness and standard deviation of the high-pass residual per 16 × 16 block.</summary>
    private static BlockStats HighPassStd(float[] y, int w, int h, double sigma)
    {
        var blurred = (float[])y.Clone();
        Blur.Gauss(blurred, w, h, sigma);
        int bw = w / Block, bh = h / Block;
        var blocks = new (float Mean, float Std)[bw * bh];
        Parallel.For(0, bh, ComputeLimits.Options, by =>
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

    // Calibration: synthetic grain (Gaussian-filtered white noise, standard deviation 1), its fine/coarse ratio and
    // how much of it the coarse high-pass filter measures. Computed once on first use.
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
        var t = Calibration;   // the ratio falls as the grain size grows
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
    /// Grain with standard deviation 1 for an image area (image coordinates <paramref name="x0"/>, <paramref name="y0"/>):
    /// white noise fixed per image pixel (the same grain in every computation, also where faces overlap),
    /// blurred with <paramref name="sigma"/>.
    /// </summary>
    internal static float[] GrainField(int x0, int y0, int w, int h, double sigma, int seed = 0)
    {
        var pad = (int)Math.Ceiling(sigma * 3) + 1;
        int pw = w + 2 * pad, ph = h + 2 * pad;
        var field = new float[pw * ph];
        Parallel.For(0, ph, ComputeLimits.Options, row =>
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

        // Normalise to standard deviation 1, because blurring has reduced it
        var k = (float)(1 / Math.Sqrt(Math.Max(1e-12, sum2 / result.Length)));
        for (var i = 0; i < result.Length; i++) result[i] *= k;
        return result;
    }

    /// <summary>Normally distributed value, fixed per pixel position (hash, Box-Muller transform).</summary>
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
