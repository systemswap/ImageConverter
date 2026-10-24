using ImageConverter.Core.Ai;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Settings of the "Grain" group, independent of the grain in "Skin &amp; details".
/// </summary>
/// <param name="Amount">Strength 0–100 % (spread in the midtones, quadratic so that low values can be set finely).</param>
/// <param name="Size">Grain size 0–100 (fine … coarse), in output pixels; see <see cref="SigmaFor"/>. Values down to
/// <see cref="MinSize"/> give grain finer than one pixel.</param>
/// <param name="Roughness">Roughness 0 (uniform) … 100 (clumpy like film).</param>
/// <param name="Color">Colour share 0 (pure luminance grain) … 100 % (each channel on its own).</param>
/// <param name="Hardness">Hardness 0 (soft dots as produced by the Gaussian) … 100 (sharply bounded grains, grainier);
/// see <see cref="Grain.Harden"/>.</param>
public sealed record GrainSettings(double Amount, double Size = 40, double Roughness = 30, double Color = 0, double Hardness = 0)
{
    /// <summary>No grain.</summary>
    public static readonly GrainSettings None = new(0);

    /// <summary>Spread (standard deviation) in the midtones at 100 % (0.12 ≈ 31 of 255 levels, strong film grain).</summary>
    public const double MaxStd = 0.12;

    /// <summary>True if the strength is too low to have an effect (below 0.5 %).</summary>
    public bool IsNone => Amount < 0.5;

    /// <summary>Spread of the grain in the midtones (0…1).</summary>
    public double MidStd => MaxStd * Math.Pow(Math.Clamp(Amount, 0, 100) / 100, 2);

    /// <summary>Grain size as a Gaussian sigma in output pixels.</summary>
    public double Sigma => SigmaFor(Size);

    /// <summary>Lowest slider value: 0.1 px (finer than one pixel, see <see cref="Grain.Field"/>).</summary>
    public const double MinSize = -27;

    /// <summary>Finest grain in output pixels.</summary>
    public const double FinestSigma = 0.1;

    /// <summary>At this sigma every pixel has its own grain (white noise); below it the grain becomes even finer.</summary>
    public const double PixelSigma = 0.25;

    /// <summary>
    /// Converts the size slider into a sigma: 0 = 0.25 px (pixel by pixel), doubling every 20, 100 = 8 px; below 0 down to
    /// <see cref="MinSize"/> = 0.1 px (finer than one pixel).
    /// </summary>
    /// <param name="size">Slider value; clamped to <see cref="MinSize"/> … 100.</param>
    /// <returns>Sigma in output pixels, at least <see cref="FinestSigma"/>.</returns>
    public static double SigmaFor(double size) =>
        Math.Max(FinestSigma, PixelSigma * Math.Pow(2, Math.Clamp(size, MinSize, 100) / 20));

    /// <summary>Inverse of <see cref="SigmaFor"/> for the slider range 0 … 100.</summary>
    /// <param name="sigma">Sigma in output pixels.</param>
    /// <returns>Slider value 0 … 100; 0 for sigma ≤ 0.25 px.</returns>
    public static double SizeFor(double sigma) => sigma <= 0.25 ? 0 : Math.Clamp(20 * Math.Log2(sigma / 0.25), 0, 100);

    /// <summary>Returns the strength (slider value) that gives this spread in the midtones.</summary>
    /// <param name="midStd">Spread in the midtones (0…1).</param>
    /// <returns>Strength 0–100.</returns>
    public static double AmountFor(double midStd) => Math.Clamp(100 * Math.Sqrt(Math.Max(0, midStd) / MaxStd), 0, 100);
}

/// <summary>
/// Grain over the finished image, always the last step of the pipeline: after denoising and sharpening, also after AI
/// and "Skin &amp; details" (otherwise one step smooths away the grain of the other).
/// </summary>
/// <remarks>
/// Grain as in film: strength as a function of brightness (most in the midtones, almost none in black and white, like the
/// grain profile in <see cref="FaceDetails"/> and AV1 film grain), Gaussian-filtered white noise fixed per image pixel
/// (the same grain on every computation, no flicker). Roughness mixes in a coarser level and lets the strength vary over
/// large areas (clumps); the colour share mixes separate grain per channel into the shared grain.
/// Size and strength apply in output pixels. A smaller version (preview, downscaled print) receives the grain as it
/// would look after downscaling the export: blurred over a larger area and weaker (<see cref="AtScale"/>).
/// </remarks>
public static class Grain
{
    private const int StripRows = 256;

    // Roughness: coarser level (2.5 × grain size) and large-area variation of the strength (6 ×).
    private const double CoarseFactor = 2.5;
    private const double CoarseWeight = 1.2;
    private const double ClumpFactor = 6;
    private const double ClumpDepth = 0.7;

    /// <summary>Blur caused by downscaling by the factor k: a Gaussian with DownscaleBlur / k output pixels (matched to
    /// Lanczos 3, GrainTests.Preview_LooksLikeTheDownscaledExport: 1 px 1.00, 2.8 px 0.92).</summary>
    internal const double DownscaleBlur = 0.28;

    /// <summary>Spread of white noise expressed as a Gaussian sigma (1 / (2 √π)).</summary>
    private const double WhiteSigma = 0.2821;

    /// <summary>E[tanh(Z)²] for Z ~ N(0, 1): mean power of the clump variation.</summary>
    private static readonly double TanhPower = TanhSquaredMean();

    /// <summary>
    /// Applies grain to a pixel buffer.
    /// </summary>
    /// <param name="pixels">Pixel buffer (B, G, R, A, sRGB-encoded 0…1); not modified.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="grain">Grain settings; null or <see cref="GrainSettings.IsNone"/> means no grain.</param>
    /// <param name="scale">Size of this image relative to the output (preview &lt; 1); 1 = the image is the output.</param>
    /// <returns>A new buffer from the <see cref="PixelPool"/>; if there is no effect, <paramref name="pixels"/>
    /// itself.</returns>
    public static float[] Apply(float[] pixels, int width, int height, GrainSettings? grain, double scale = 1)
    {
        if (grain is null || grain.IsNone || width <= 0 || height <= 0) return pixels;

        var mid = (float)grain.MidStd;
        var rough = Math.Clamp(grain.Roughness / 100, 0, 1);
        var color = Math.Clamp(grain.Color / 100, 0, 1);
        var fineSigma = grain.Sigma;
        var hard = Hardening.For(grain.Hardness);

        // Levels in pixels of this image, with the attenuation caused by downscaling.
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
            var lumaFine = Harden(Field(y0, width, rows, fine, seed: 11), hard);
            var lumaCoarse = rough > 0 ? Harden(Field(y0, width, rows, coarse, seed: 12), hard) : null;
            var clumps = rough > 0 ? Field(y0, width, rows, clump, seed: 13) : null;
            float[]? r = null, g = null, b = null;
            if (color > 0)
            {
                b = Harden(Field(y0, width, rows, fine, seed: 21), hard);
                g = Harden(Field(y0, width, rows, fine, seed: 22), hard);
                r = Harden(Field(y0, width, rows, fine, seed: 23), hard);
            }

            Parallel.For(0, rows, ComputeLimits.Options, row =>
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

    /// <summary>
    /// Hardness as a transfer curve: k = 4 · hardness/100, grain → tanh(k · grain), spread normalised back to 1. At 0
    /// nothing changes; the larger k, the flatter the tops and the steeper the edges of the grains, so soft Gaussian dots
    /// become sharply bounded grains, at 100 almost only bright or dark.
    /// </summary>
    internal readonly record struct Hardening(float K, float Gain)
    {
        public static Hardening For(double hardness)
        {
            var k = 4 * Math.Clamp(hardness / 100, 0, 1);
            return k < 1e-3 ? default : new Hardening((float)k, (float)(1 / Math.Sqrt(TanhSquaredMean(k))));
        }
    }

    /// <summary>Hardens grain (spread 1) in place; unchanged without hardness. Returns <paramref name="field"/>.</summary>
    internal static float[] Harden(float[] field, Hardening hard)
    {
        if (hard.K <= 0) return field;
        var (k, gain) = (hard.K, hard.Gain);
        Parallel.For(0, (field.Length + 4095) / 4096, ComputeLimits.Options, block =>
        {
            var end = Math.Min(field.Length, (block + 1) * 4096);
            for (var i = block * 4096; i < end; i++) field[i] = MathF.Tanh(k * field[i]) * gain;
        });
        return field;
    }

    /// <summary>Strength per brightness as in film: 1 in the midtones, fading out towards black and white.</summary>
    internal static float Weight(float lum)
    {
        var t = 4 * lum * (1 - lum);
        return t <= 0 ? 0 : t >= 1 ? 1 : MathF.Sqrt(t);
    }

    /// <summary>
    /// Describes grain of size <paramref name="sigma"/> (output pixels) as seen in an image scaled by
    /// <paramref name="scale"/>.
    /// </summary>
    /// <param name="sigma">Grain sigma in output pixels.</param>
    /// <param name="scale">Size of the image relative to the output.</param>
    /// <returns>The sigma in pixels of the scaled image and the share of the spread that remains (1 for
    /// <paramref name="scale"/> ≥ 1).</returns>
    /// <remarks>
    /// Downscaling acts like a Gaussian of <see cref="DownscaleBlur"/>/scale; filtered noise has a spread inversely
    /// proportional to its sigma (white noise counts as sigma <see cref="WhiteSigma"/>).
    /// </remarks>
    public static (double Sigma, double Amplitude) AtScale(double sigma, double scale)
    {
        if (scale >= 1) return (sigma * scale, 1);
        var blur = DownscaleBlur / scale;
        var spread = Math.Sqrt(sigma * sigma + WhiteSigma * WhiteSigma);
        return (scale * Math.Sqrt(sigma * sigma + blur * blur), spread / Math.Sqrt(spread * spread + blur * blur));
    }

    /// <summary>
    /// Grain with spread 1 for the rows <paramref name="y0"/> … over the full image width: white noise fixed per image
    /// pixel, blurred with <paramref name="sigma"/>. Computed strip by strip with a margin, giving the same result as for
    /// the whole image at once but without planes of full image size. Returns a buffer from the <see cref="PixelPool"/>.
    /// </summary>
    internal static float[] Field(int y0, int width, int rows, double sigma, int seed)
    {
        var blur = sigma >= 0.3;
        var finer = FinerShare(sigma);   // below 0.25 px: share of the high-pass
        var pad = blur ? (int)Math.Ceiling(3.5 * sigma) + 2 : finer > 0 ? (int)Math.Ceiling(3.5 * FinerBlur) + 2 : 0;
        int pw = width + 2 * pad, ph = rows + 2 * pad;
        var field = PixelPool.Rent(pw * ph);
        Parallel.For(0, ph, ComputeLimits.Options, row =>
        {
            var y = y0 - pad + row;
            for (var x = 0; x < pw; x++) field[row * pw + x] = Noise(x - pad, y, seed);
        });

        var result = PixelPool.Rent(width * rows);
        if (finer > 0)
        {
            // Finer than one pixel: white noise minus part of its blurred version. The coarse components (random
            // clusters over several pixels that look like blotches) disappear, and neighbours become slightly
            // anti-correlated, as with grain smaller than one pixel. The spread is normalised back to 1 analytically.
            var low = PixelPool.Rent(pw * ph);
            Array.Copy(field, low, pw * ph);
            Blur.Gauss(low, pw, ph, FinerBlur);
            var a = (float)finer;
            var gain = (float)(1 / Math.Sqrt(FinerEnergy(finer)));
            Parallel.For(0, rows, ComputeLimits.Options, row =>
            {
                var from = (row + pad) * pw + pad;
                for (var x = 0; x < width; x++) result[row * width + x] = (field[from + x] - a * low[from + x]) * gain;
            });
            PixelPool.Return(low);
        }
        else if (!blur)
        {
            Array.Copy(field, result, width * rows);
        }
        else
        {
            Blur.Gauss(field, pw, ph, sigma);
            var gain = (float)(1 / KernelEnergy(sigma));   // normalise the spread back to 1 after blurring
            Parallel.For(0, rows, ComputeLimits.Options, row =>
            {
                var from = (row + pad) * pw + pad;
                for (var x = 0; x < width; x++) result[row * width + x] = field[from + x] * gain;
            });
        }
        PixelPool.Return(field);
        return result;
    }

    /// <summary>
    /// Spread of white noise (spread 1) after <see cref="Blur.Gauss"/> with this sigma: Σk² of the 1-D kernel, applied
    /// once for both directions (variance (Σk²)², spread Σk²). Uses the same kernel or the same boxes as there.
    /// </summary>
    internal static double KernelEnergy(double sigma) => Kernel1D(sigma).Sum(v => v * v);

    /// <summary>Blur for grain below one pixel: what is subtracted are clusters over about 1 px.</summary>
    private const double FinerBlur = 1.0;

    /// <summary>Share of the high-pass: 0 at 0.25 px (white noise), 1 at 0.1 px.</summary>
    internal static double FinerShare(double sigma) =>
        sigma >= GrainSettings.PixelSigma ? 0
            : Math.Clamp((GrainSettings.PixelSigma - sigma) / (GrainSettings.PixelSigma - GrainSettings.FinestSigma), 0, 1);

    /// <summary>
    /// Variance of n − a·G(n) for white noise n (variance 1) and the 2-D Gaussian G = g ⊗ g of <see cref="Blur.Gauss"/>:
    /// Σ(δ − aG)² = 1 − 2a·G(0, 0) + a²·ΣG², with G(0, 0) = g₀² and ΣG² = (Σg²)².
    /// </summary>
    internal static double FinerEnergy(double a)
    {
        var g = Kernel1D(FinerBlur);
        var g0 = g[g.Length / 2];
        var g2 = g.Sum(v => v * v);
        return 1 - 2 * a * g0 * g0 + a * a * g2 * g2;
    }

    /// <summary>The 1-D kernel of <see cref="Blur.Gauss"/> with this sigma: the same kernel or the same boxes as
    /// there.</summary>
    private static double[] Kernel1D(double sigma)
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
        return kernel;
    }

    /// <summary>
    /// Sufficiently normally distributed noise with spread 1, fixed per pixel position: the sum of four uniformly
    /// distributed 16-bit numbers from two hashes (Irwin-Hall). Cheaper than Box-Muller and indistinguishable for grain.
    /// </summary>
    private static float Noise(int x, int y, int seed)
    {
        var h1 = Hash((uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)seed * 83492791u);
        var h2 = Hash(h1 ^ 0x9E3779B9u);
        const float unit = 1f / 65536;
        var sum = (h1 & 0xFFFF) * unit + (h1 >> 16) * unit + (h2 & 0xFFFF) * unit + (h2 >> 16) * unit;
        return (sum - 2) * 1.7320508f;   // the spread of the sum of 4 × U(0, 1) is 1/√3
    }

    private static uint Hash(uint v)
    {
        v ^= v >> 16; v *= 0x7feb352du;
        v ^= v >> 15; v *= 0x846ca68bu;
        v ^= v >> 16;
        return v;
    }

    /// <summary>E[tanh(k·Z)²] for Z ~ N(0, 1).</summary>
    private static double TanhSquaredMean(double k = 1)
    {
        // ∫ tanh(k·z)² φ(z) dz, trapezoidal rule over ±8
        double sum = 0;
        const double step = 0.001;
        for (var z = -8.0; z <= 8; z += step)
        {
            var t = Math.Tanh(k * z);
            sum += t * t * Math.Exp(-z * z / 2);
        }
        return sum * step / Math.Sqrt(2 * Math.PI);
    }

    // ------------------------------------------------------------ Measuring from the photo

    /// <summary>Maximum number of pixels measured (centre of the image); grain is the same everywhere, so this saves time
    /// and memory.</summary>
    private const int MeasureMaxPixels = 12_000_000;

    private const int Block = 16;

    /// <summary>
    /// Measures the grain of the photo and converts it into slider values.
    /// </summary>
    /// <remarks>
    /// Strength and size come from the grain profile (<see cref="FaceDetails.MeasureGrain"/>, quietest areas per
    /// brightness), the colour share from the correlation of the three channels at the same places (moving together =
    /// luminance grain, independent = colour grain). Roughness cannot be measured this way; the grain of a photo is
    /// uniform, so it is 0. Hardness cannot be measured either and is 0 (soft).
    /// </remarks>
    /// <param name="pixels">Photo (B, G, R, A, 0…1), preferably before denoising and before AI.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="outputScale">Size of the output relative to this photo (smaller &lt; 1).</param>
    /// <returns>The measured settings, or null if no grain can be measured (too small, completely smooth).</returns>
    public static GrainSettings? Measure(float[] pixels, int width, int height, double outputScale)
    {
        var (rgb, w, h) = CenterRgb(pixels, width, height);
        var profile = FaceDetails.MeasureGrain(rgb, w, h);
        if (profile.SigmaByLevel.All(s => s <= 1e-5f)) return null;

        var color = MeasureColorShare(rgb, w, h);

        // Fit the Weight() curve to the measured profile (least squares over the brightness levels).
        double num = 0, den = 0;
        var levels = profile.SigmaByLevel.Length;
        for (var l = 0; l < levels; l++)
        {
            var wy = Weight((l + 0.5f) / levels);
            num += wy * profile.SigmaByLevel[l];
            den += wy * wy;
        }
        var lumaStd = den > 0 ? num / den : 0;

        // The measured value is the spread of the luminance; colour grain per channel contributes only partly to it.
        const double lumaWeights2 = 0.2126 * 0.2126 + 0.7152 * 0.7152 + 0.0722 * 0.0722;
        var channelStd = lumaStd / Math.Sqrt((1 - color) + color * lumaWeights2);

        // Convert to output pixels and output strength.
        var (sigma, amp) = AtScale(profile.GrainSigma, outputScale);
        return new GrainSettings(
            Math.Round(GrainSettings.AmountFor(channelStd * amp)),
            Math.Round(GrainSettings.SizeFor(sigma)),
            0,
            Math.Round(color * 100),
            0);   // hardness cannot be measured: soft
    }

    /// <summary>Interleaved RGB (for <see cref="FaceDetails.MeasureGrain"/>), at most <see cref="MeasureMaxPixels"/> from
    /// the centre.</summary>
    private static (float[] Rgb, int W, int H) CenterRgb(float[] bgra, int width, int height)
    {
        var f = Math.Min(1, Math.Sqrt((double)MeasureMaxPixels / ((long)width * height)));
        int w = Math.Max(1, (int)(width * f)), h = Math.Max(1, (int)(height * f));
        int x0 = (width - w) / 2, y0 = (height - h) / 2;
        var rgb = new float[(long)w * h * 3];
        Parallel.For(0, h, ComputeLimits.Options, row =>
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
    /// Colour share 0…1: 1 − the mean correlation of the channels in the high-pass, in the quietest 20 % of the blocks,
    /// where there is only grain. In the model of <see cref="Apply"/> the correlation is exactly 1 − colour share.
    /// </summary>
    internal static double MeasureColorShare(float[] rgb, int w, int h)
    {
        if (w < Block * 2 || h < Block * 2) return 0;
        var blurred = (float[])rgb.Clone();
        Blur.Gauss(blurred, w, h, 2.0, channels: 3);

        int bw = w / Block, bh = h / Block;
        var stats = new (double Luma, double[] S)[bw * bh];
        Parallel.For(0, bh, ComputeLimits.Options, by =>
        {
            for (var bx = 0; bx < bw; bx++)
            {
                // Sums: r, g, b, rr, gg, bb, rg, gb, rb, luminance
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
