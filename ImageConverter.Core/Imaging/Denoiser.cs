using ImageConverter.Core.Ai;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Removes noise separately for luminance and colour, in the way established raw editors split luminance and colour
/// noise reduction.
/// </summary>
/// <remarks>
/// Works on the 32-bit float buffer of the processing chain (<see cref="Pixels"/>) and computes in luma Y and the colour
/// differences Cb = B − Y and Cr = R − Y (Y with the ITU-R BT.709 weights).
/// <para><b>Colour noise</b>: coarse coloured blotches. The colour channels carry hardly any fine detail; the edges lie in
/// the luminance. Cb and Cr are therefore smoothed with a luminance-guided filter (guided filter, He et al. 2010) in two
/// stages (fine colour dots, larger blotches). This smooths the colour strongly but stops at luminance edges, so no colour
/// fringes appear.</para>
/// <para><b>Luminance noise</b>: Non-Local Means (Buades et al. 2005) on Y. Each pixel is averaged with pixels whose
/// neighbourhood looks similar rather than simply with its neighbours, so edges and fine structure are preserved. The
/// filter strength follows the noise measured per brightness level (as for the AI grain): dark, noisy areas are
/// filtered more, bright, clean areas less. A clean image remains almost unchanged.</para>
/// </remarks>
public static class Denoiser
{
    // Radii in pixels of the export (converted with the scale factor in the preview)
    private const double ChromaFine = 3, ChromaCoarse = 10;
    private const int PatchRadius = 2, SearchRadius = 4;

    /// <summary>Denoises an image; the input remains unchanged.</summary>
    /// <param name="px">Pixels, interleaved B, G, R, A, each 0…1.</param>
    /// <param name="w">Width in px.</param>
    /// <param name="h">Height in px.</param>
    /// <param name="luma">Luminance noise reduction strength, 0…1; values up to 0.005 switch it off.</param>
    /// <param name="chroma">Colour noise reduction strength, 0…1; values up to 0.005 switch it off.</param>
    /// <param name="scale">Width of this image ÷ width in the export, so that the radii act the same in the preview
    /// and in the export.</param>
    /// <param name="plan">Optional progress plan; <see cref="WorkPlan.Next"/> is called once at the start.</param>
    /// <returns>A new buffer of the same length rented from <see cref="PixelPool"/>; the caller owns it. If both
    /// strengths are off, it is an exact copy of the input.</returns>
    /// <remarks>Luminance noise reduction runs on the GPU if <see cref="GpuFilters.NonLocalMeans"/> is available,
    /// otherwise on the CPU; both give the same result up to rounding.</remarks>
    public static float[] Apply(float[] px, int w, int h, double luma, double chroma, double scale = 1, WorkPlan? plan = null)
    {
        plan?.Next();
        if (luma <= 0.005 && chroma <= 0.005)
        {
            var copy = PixelPool.Rent(px.Length);
            Array.Copy(px, copy, px.Length);
            return copy;   // Nothing to do: exactly the same image, without the round trip through luma and chroma
        }

        // All intermediate buffers come from the pool; allocating them fresh costs about 7 GB per call for the panorama
        // test image
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
            // The means of the guide (luma) depend only on the radius, so they are computed once for both colour
            // channels
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
    /// Mean and mean of squares of the guide per radius (guided filter), computed once for both colour channels.
    /// The guide must not change while this instance is in use; <see cref="Dispose"/> returns the buffers to the pool.
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

    // ------------------------------------------------------------ Colour

    /// <summary>
    /// Smooths a colour channel in place, guided by the luminance: two stages of guided filter. The strength determines
    /// how much of the smoothed channel is taken over and how large a difference the filter still treats as noise.
    /// </summary>
    private static void ChromaDenoise(float[] c, float[] guide, int w, int h, double strength, double scale, GuideStats stats)
    {
        var sigma = NoiseSigma(c, w, h);
        // Differences up to about (1 + 3 · strength) times the noise level count as noise: more strength, more tolerance
        var eps = (float)Math.Pow(sigma * (1 + 3 * strength), 2) + 1e-7f;
        var r1 = Math.Max(1, (int)Math.Round(ChromaFine * scale));
        var r2 = Math.Max(r1 + 1, (int)Math.Round(ChromaCoarse * scale));

        var fine = GuidedFilter(c, guide, w, h, r1, eps, stats);
        var coarse = GuidedFilter(fine, guide, w, h, r2, eps, stats);
        var s = (float)Math.Clamp(strength, 0, 1);
        // The fine stage scales linearly with the strength, the coarse one quadratically: small values remove the colour
        // dots first, large values also the blotches
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++)
                c[i] += s * (fine[i] - c[i]) + s * s * (coarse[i] - fine[i]);
        });
        PixelPool.Return(fine);
        PixelPool.Return(coarse);
    }

    /// <summary>Guided filter (He, Sun, Tang 2010): the output follows the edges of the guide and smooths in between.</summary>
    private static float[] GuidedFilter(float[] p, float[] guide, int w, int h, int r, float eps, GuideStats stats)
    {
        var n = p.Length;
        var ip = PixelPool.Rent(n);
        var tmp = PixelPool.Rent(n);   // One scratch buffer for all box means (fully overwritten each time)
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++) ip[i] = guide[i] * p[i];
        });
        var (meanI, corrII) = stats.For(r);
        var meanP = BoxMean(p, w, h, r, tmp, PixelPool.Rent(n));
        var corrIP = BoxMean(ip, w, h, r, tmp, PixelPool.Rent(n));

        float[] a = ip, b = PixelPool.Rent(n);   // a overwrites ip, which is no longer needed
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

    /// <summary>
    /// Mean over a square of radius <paramref name="r"/> (at the border only over the pixels that lie inside the image).
    /// Separable running sums, so the cost does not depend on the radius.
    /// </summary>
    internal static float[] BoxMean(float[] src, int w, int h, int r) =>
        BoxMean(src, w, h, r, new float[src.Length], new float[src.Length]);

    /// <summary>As above, into existing buffers (<paramref name="tmp"/> intermediate result, <paramref name="dst"/>
    /// result, which is also returned).</summary>
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

    /// <summary>Noise level of a channel (robust: median of the deviation from the 3 × 3 mean, converted to the standard
    /// deviation of a normal distribution).</summary>
    private static double NoiseSigma(float[] c, int w, int h)
    {
        var tmp = PixelPool.Rent(c.Length);
        var mean = BoxMean(c, w, h, 1, tmp, PixelPool.Rent(c.Length));
        var step = Math.Max(1, c.Length / 200_000);   // A sample of about 200,000 values is sufficient
        var diffs = new List<float>(c.Length / step + 1);
        for (var i = 0; i < c.Length; i += step) diffs.Add(Math.Abs(c[i] - mean[i]));
        PixelPool.Return(tmp);
        PixelPool.Return(mean);
        diffs.Sort();
        // For white noise the deviation from the 3 × 3 mean has the standard deviation σ·√(8/9); median |N(0,1)| = 0.6745
        return diffs[diffs.Count / 2] / 0.6745 / Math.Sqrt(8.0 / 9);
    }

    // ------------------------------------------------------------ Luminance

    /// <summary>
    /// Non-Local Means on the luminance: for each offset in the 9 × 9 search window, the distance of the 5 × 5
    /// neighbourhoods (box mean of the squared differences) gives the weight. The filter strength per pixel comes from
    /// the measured noise. Returns <paramref name="y"/> itself if the image shows no measurable noise.
    /// </summary>
    private static float[] LumaDenoise(float[] y, float[] px, int w, int h, double strength)
    {
        // Measure the noise per brightness level (as for the grain) on exactly this image, also in the preview
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
        var k = 0.25f + 0.75f * (float)Math.Clamp(strength, 0, 1);   // Filter width in multiples of the noise level
        var sigma2 = PixelPool.Rent(n);
        var invH2 = PixelPool.Rent(n);
        Parallel.For(0, n, ComputeLimits.Options, i =>
        {
            var s = profile.SigmaAt(y[i]);
            sigma2[i] = s * s;
            var hh = k * s;
            invH2[i] = hh > 1e-6f ? 1 / (hh * hh) : 0;
        });

        // GPU: the same computation per pixel, all offsets in one thread (same result up to rounding)
        if (GpuFilters.NonLocalMeans(y, sigma2, invH2, w, h, PatchRadius, SearchRadius, (float)Math.Min(1, 1.5 * strength)) is { } fast)
        {
            PixelPool.Return(sigma2);
            PixelPool.Return(invH2);
            return fast;
        }

        // The sums start at zero (pool buffers are not cleared); diff, boxTmp and patch are fully overwritten
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

        // The pixel itself gets the largest weight of its neighbours (the usual choice); the result is then mixed with the
        // original according to the strength
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
