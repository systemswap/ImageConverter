using ImageConverter.Core.Platform;
namespace ImageConverter.Core.Imaging;

/// <summary>
/// Local Laplacian filter (Paris, Hasinoff, Kautz 2011; fast variant after Aubry et al. 2014), the technique behind
/// shadows/highlights/clarity in Referenzwerkzeug and Referenzwerkzeug.
/// </summary>
/// <remarks>
/// A tone curve is not applied pixel by pixel but layer by layer of a Laplacian pyramid: each detail layer takes on the
/// curve according to the brightness of its surroundings at its own scale. Fine details are preserved (the curve is
/// applied linearly around the surrounding value), and the curve does not act across edges: values further than
/// <c>sigma</c> from the surrounding value stay unchanged. This avoids halos and flat areas.
/// Fast variant: instead of computing a separate pyramid for every pixel, the curve is computed for <see cref="Levels"/>
/// fixed surrounding values, and the two nearest results are blended linearly.
/// </remarks>
internal static class LocalLaplacian
{
    /// <summary>Number of fixed surrounding values between 0 and 1.</summary>
    private const int Levels = 8;

    /// <summary>Minimum short edge of the coarsest pyramid level.</summary>
    private const int CoarsestEdge = 4;

    /// <summary>Applies <paramref name="curve"/> locally to a brightness plane.</summary>
    /// <param name="lum">Brightness 0–1 (sRGB-encoded, hence close to perception). Not modified.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="curve">Tone curve for the surrounding value, defined on 0 … 1.</param>
    /// <param name="sigma">From this distance to the surrounding value onwards a value counts as an edge.</param>
    /// <param name="guide">Surrounding value per pyramid level, derived from the Gaussian pyramid of the input, for
    /// example to also take wider surroundings into account. Without it, the Gaussian level itself is used.</param>
    /// <returns>A new buffer with the filtered brightness plane.</returns>
    public static float[] Apply(float[] lum, int width, int height, Func<float, float> curve, float sigma,
        Func<Pyramid, int, float[]>? guide = null)
    {
        var gauss = Pyramid.Gaussian(lum, width, height, LevelCount(width, height));
        var top = gauss.Count - 1;
        var guides = Enumerable.Range(0, gauss.Count).Select(l => guide?.Invoke(gauss, l) ?? gauss[l]).ToArray();

        // Output pyramid: Laplacian levels 0 … top-1, residual on the coarsest level.
        var output = gauss.EmptyLike();
        var step = 1f / (Levels - 1);
        // Weight "close to the surrounding value" exp(−d²/2σ²) as a table over d ∈ [−1, 1]; otherwise every pixel and
        // surrounding value needs one exponential function (over 200 million per filter at 28 MP).
        var nearTable = NearTable(sigma);
        float[]? remapped = null;

        for (var k = 0; k < Levels; k++)
        {
            var gamma = k * step;
            var target = curve(gamma);
            var slope = Slope(curve, gamma);

            // Image remapped as if the surrounding value were gamma everywhere.
            remapped ??= PixelPool.Rent(lum.Length);   // once, for all surrounding values (completely written)
            Parallel.For(0, height, ComputeLimits.Options, y =>
            {
                for (int x = 0, i = y * width; x < width; x++, i++)
                {
                    var d = lum[i] - gamma;
                    var curved = target + slope * d;                     // curve, linearised around gamma
                    var near = Near(nearTable, d);                         // 1 close, 0 beyond the edge
                    remapped[i] = lum[i] + (curved - lum[i]) * near;
                }
            });

            var r = Pyramid.Gaussian(remapped, width, height, gauss.Count);
            for (var l = 0; l <= top; l++)
            {
                var band = l < top ? r.Laplacian(l) : r[top];   // Laplacian level from the pool, returned below
                var g = guides[l];
                var o = output[l];
                Parallel.For(0, o.Length / 4096 + 1, ComputeLimits.Options, chunk =>
                {
                    var end = Math.Min(o.Length, (chunk + 1) * 4096);
                    for (var i = chunk * 4096; i < end; i++)
                    {
                        var w = 1 - MathF.Abs(Math.Clamp(g[i], 0, 1) - gamma) / step;
                        if (w > 0) o[i] += w * band[i];
                    }
                });
                if (l < top) PixelPool.Return(band);
            }
            r.ReturnLevels(from: 1);   // level 0 is remapped and is reused
        }

        PixelPool.Return(remapped);
        gauss.ReturnLevels(from: 1);   // level 0 is the input
        return output.Collapse();
    }

    private const int NearSize = 4096;

    private static float[] NearTable(float sigma)
    {
        var table = new float[NearSize + 1];
        var twoSigma2 = 2 * sigma * sigma;
        for (var i = 0; i <= NearSize; i++)
        {
            var d = 2f * i / NearSize - 1;
            table[i] = MathF.Exp(-d * d / twoSigma2);
        }
        return table;
    }

    /// <summary>Reads the table with linear interpolation; d lies between −1 and 1 (brightness values 0–1).</summary>
    private static float Near(float[] table, float d)
    {
        var x = (Math.Clamp(d, -1, 1) + 1) * (NearSize / 2f);
        var i = Math.Min((int)x, NearSize - 1);
        return table[i] + (table[i + 1] - table[i]) * (x - i);
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
/// Gaussian pyramid of a floating-point plane: level 0 is the image, each further level is half the size (5-tap binomial
/// filter 1-4-6-4-1; when downsampling, the borders are extended by mirroring).
/// </summary>
/// <remarks>Levels above 0 are rented from <see cref="PixelPool"/>; level 0 is the caller's buffer.</remarks>
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

    /// <summary>Creates an empty pyramid with the same level sizes (for the output).</summary>
    public Pyramid EmptyLike()
    {
        var p = new Pyramid();
        foreach (var (data, w, h) in _levels) p._levels.Add((new float[data.Length], w, h));
        return p;
    }

    /// <summary>Detail layer: the level minus the upsampled next coarser level (buffer from the pool).</summary>
    public float[] Laplacian(int level)
    {
        var (fine, w, h) = _levels[level];
        var (coarse, cw, ch) = _levels[level + 1];
        var band = Up(coarse, cw, ch, w, h);
        for (var i = 0; i < band.Length; i++) band[i] = fine[i] - band[i];
        return band;
    }

    /// <summary>Returns the levels from <paramref name="from"/> onwards to the pool; the pyramid is unusable
    /// afterwards.</summary>
    public void ReturnLevels(int from)
    {
        for (var l = from; l < _levels.Count; l++) PixelPool.Return(_levels[l].Data);
    }

    /// <summary>Level <paramref name="coarse"/> upsampled step by step to the size of <paramref name="level"/>.</summary>
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

    /// <summary>Collapses a Laplacian pyramid (levels 0 … n-2) plus residual back into the image.</summary>
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
        var rows = PixelPool.Rent(cw * h);
        Parallel.For(0, h, ComputeLimits.Options, y =>
        {
            var row = y * w;
            for (var x = 0; x < cw; x++)
            {
                float acc = 0;
                for (var k = -2; k <= 2; k++) acc += Kernel[k + 2] * src[row + Mirror(2 * x + k, w)];
                rows[y * cw + x] = acc;
            }
        });
        var dst = PixelPool.Rent(cw * ch);
        Parallel.For(0, ch, ComputeLimits.Options, y =>
        {
            for (var x = 0; x < cw; x++)
            {
                float acc = 0;
                for (var k = -2; k <= 2; k++) acc += Kernel[k + 2] * rows[Mirror(2 * y + k, h) * cw + x];
                dst[y * cw + x] = acc;
            }
        });
        PixelPool.Return(rows);
        return (dst, cw, ch);
    }

    /// <summary>Upsamples to fw × fh: inserts zeros and smooths with the doubled kernel (borders clamped).</summary>
    private static float[] Up(float[] src, int cw, int ch, int fw, int fh)
    {
        var rows = PixelPool.Rent(fw * ch);
        Parallel.For(0, ch, ComputeLimits.Options, y =>
        {
            for (var x = 0; x < fw; x++)
            {
                float acc = 0;
                // Contributions of the coarse pixels i with 2i close to x.
                for (var k = -2; k <= 2; k++)
                {
                    var t = x + k;
                    if ((t & 1) != 0) continue;
                    acc += 2 * Kernel[k + 2] * src[y * cw + Math.Clamp(t / 2, 0, cw - 1)];
                }
                rows[y * fw + x] = acc;
            }
        });
        var dst = PixelPool.Rent(fw * fh);
        Parallel.For(0, fh, ComputeLimits.Options, y =>
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
        PixelPool.Return(rows);
        return dst;
    }
}
