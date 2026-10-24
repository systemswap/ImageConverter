using ImageConverter.Core.Platform;
namespace ImageConverter.Core.Imaging;

/// <summary>
/// Maps for "Match colour → Luminance" with threshold and target value, computed at low resolution.
/// </summary>
/// <remarks>
/// The main map is the <b>region brightness</b>: the brightness smoothed edge-preservingly (self-guided guided filter). A
/// tree is dark as a whole, the sky bright as a whole, and the edge between them stays sharp. Threshold and target value
/// apply to these regions, not per pixel, so a dark tree is lifted completely and no halo glows into the sky next to it.
/// In addition, colour and brightness are kept at low resolution for the "what lies behind" map (gaps between branches).
/// The approach was chosen on a panorama with a dark foreground: blurred masks gave clear halos in the sky, several passes
/// (rolling guidance) made the mask lumpy and brought the halos back, while the smoothed brightness kept the pixels next
/// to the trees practically unchanged (+0.003 instead of +0.31 Oklab L without a threshold).
/// The result is blended via a Laplacian pyramid (<see cref="PyramidBlend"/>).
/// Distance (from haze or a depth map) is deliberately not used as a criterion: luminance decides by brightness, and the
/// sun and reflections can also lie in the foreground, so near and far are the wrong feature. The target value keeps the
/// midtones (valley) calm.
/// </remarks>
public sealed class LuminanceRegions
{
    /// <summary>Longest edge of the maps: enough for regions, fast enough for the preview.</summary>
    private const int MapEdge = 1200;

    /// <summary>Width of the transition around the threshold (Oklab L, on each side).</summary>
    private const float Soft = 0.08f;

    private readonly float[] _base, _l, _a, _b;
    private readonly int _w, _h;
    private readonly float _scale;   // image pixels per map pixel

    private LuminanceRegions(float[] baseMap, float[] l, float[] a, float[] b, int w, int h, float scale) =>
        (_base, _l, _a, _b, _w, _h, _scale) = (baseMap, l, a, b, w, h, scale);

    /// <summary>Computes the maps for a pixel buffer.</summary>
    /// <param name="bgra">Pixel buffer (B, G, R, A; sRGB 0–1).</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>The maps, with a longest edge of at most 1200 cells.</returns>
    public static LuminanceRegions Compute(float[] bgra, int width, int height)
    {
        var f = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)MapEdge));
        int w = (width + f - 1) / f, h = (height + f - 1) / f, n = w * h;
        var small = Shrink(bgra, width, height, f, w, h);
        var l = new float[n];
        var la = new float[n];
        var lb = new float[n];
        for (var i = 0; i < n; i++) (l[i], la[i], lb[i]) = Oklab.FromSrgb(small[i * 3 + 2], small[i * 3 + 1], small[i * 3]);

        var radius = Math.Max(2, Math.Max(w, h) / 60);
        var baseMap = Guided(l, l, w, h, radius, 0.01f);   // regions: structure removed, large edges kept
        return new LuminanceRegions(baseMap, l, la, lb, w, h, f);
    }

    /// <summary>Downscales by averaging each f × f block; the result has B, G, R per pixel.</summary>
    private static float[] Shrink(float[] bgra, int width, int height, int f, int w, int h)
    {
        var small = new float[w * h * 3];
        Parallel.For(0, h, ComputeLimits.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                double b = 0, g = 0, r = 0;
                var count = 0;
                for (var yy = y * f; yy < Math.Min(height, (y + 1) * f); yy++)
                for (var xx = x * f; xx < Math.Min(width, (x + 1) * f); xx++)
                {
                    var i = (yy * width + xx) * 4;
                    b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2];
                    count++;
                }
                var o = (y * w + x) * 3;
                small[o] = (float)(b / count); small[o + 1] = (float)(g / count); small[o + 2] = (float)(r / count);
            }
        });
        return small;
    }

    /// <summary>Share of the region below the threshold (1 = below, is lifted; 0 = above).</summary>
    private static float BelowLimit(float area, float limit) => limit < 1 ? 1 - SmoothStep(limit - Soft, limit + Soft, area) : 1;

    /// <summary>Computes the gain needed to bring a region to the target value.</summary>
    /// <param name="area">Region brightness (Oklab L).</param>
    /// <param name="target">Target value as Oklab L; 0 disables the target.</param>
    /// <param name="luminance">Luminance factor; upper limit of the gain.</param>
    /// <returns>The gain needed to reach the target, clamped to 1 … <paramref name="luminance"/>; with the target disabled,
    /// <paramref name="luminance"/>.</returns>
    public static float TargetGain(float area, float target, float luminance) =>
        target > 0 ? Math.Clamp(target / Math.Max(area, 0.02f), 1, luminance) : luminance;

    /// <summary>
    /// Creates the weighting for fixed slider values (threshold, target value, luminance).
    /// </summary>
    /// <remarks>
    /// Computes the "what lies behind" map once: colour and brightness of the areas that are not (or hardly) lifted,
    /// because they are above the threshold or already at the target value (for example the sky), extended into the
    /// lifted regions (normalised convolution). This makes gaps between branches detectable.
    /// </remarks>
    /// <param name="limit">Threshold as Oklab L (1 = no threshold): brighter regions are left unchanged.</param>
    /// <param name="target">Target value as Oklab L (0 = off).</param>
    /// <param name="luminance">Luminance factor (&gt; 1).</param>
    /// <returns>The weighting; it references these maps.</returns>
    public Weighting For(float limit, float target, float luminance)
    {
        var n = _w * _h;
        var excluded = new float[n];
        var el = new float[n];
        var ea = new float[n];
        var eb = new float[n];
        for (var i = 0; i < n; i++)
        {
            var lift = BelowLimit(_base[i], limit);
            if (target > 0 && luminance > 1) lift *= (TargetGain(_base[i], target, luminance) - 1) / (luminance - 1);
            var e = 1 - lift;
            excluded[i] = e;
            el[i] = e * _l[i];
            ea[i] = e * _a[i];
            eb[i] = e * _b[i];
        }
        var r = Math.Max(4, Math.Max(_w, _h) / BehindReach);
        var cover = Box(excluded, _w, _h, r);
        var bl = Box(el, _w, _h, r);
        var ba = Box(ea, _w, _h, r);
        var bb = Box(eb, _w, _h, r);
        for (var i = 0; i < n; i++)
        {
            var c = Math.Max(cover[i], 1e-4f);
            bl[i] /= c; ba[i] /= c; bb[i] /= c;
        }
        return new Weighting(this, limit, cover, bl, ba, bb);
    }

    /// <summary>
    /// Reach of the "what lies behind" map: one fiftieth of the longest edge, because gaps occur at the edge of a tree,
    /// not deep inside a dense bush. With one twentieth, sunlit leaves in the middle of a bush count as gaps.
    /// </summary>
    private const int BehindReach = 50;

    /// <summary>Per-pixel weighting for fixed slider values (see <see cref="For"/>).</summary>
    public sealed class Weighting
    {
        private readonly LuminanceRegions _maps;
        private readonly float _limit;
        private readonly float[] _cover, _behindL, _behindA, _behindB;

        internal Weighting(LuminanceRegions maps, float limit, float[] cover, float[] l, float[] a, float[] b) =>
            (_maps, _limit, _cover, _behindL, _behindA, _behindB) = (maps, limit, cover, l, a, b);

        /// <summary>Computes how strongly a pixel is lifted, from the threshold, the protection of bright pixels and the
        /// gaps.</summary>
        /// <param name="x">X coordinate in image pixels.</param>
        /// <param name="y">Y coordinate in image pixels.</param>
        /// <param name="l">Oklab L of the pixel itself.</param>
        /// <param name="a">Oklab a of the pixel itself.</param>
        /// <param name="b">Oklab b of the pixel itself.</param>
        /// <returns>Weight 0 … 1.</returns>
        public float Weight(int x, int y, float l, float a, float b)
        {
            var m = _maps;
            var (fx, fy) = m.MapPoint(x, y);
            var area = m.Read(m._base, fx, fy);

            var w = BelowLimit(area, _limit);
            if (_limit < 1)
            {
                // Fixed protection of very bright pixels (a fallback where nothing "behind" is known).
                var protect = Math.Max(0.62f, _limit + 0.12f);
                w *= 1 - SmoothStep(protect, protect + 0.18f, l);
            }

            // Gap: brighter than the region and in the colour of what lies behind (sky, valley); only this share is left
            // unchanged. Sunlit needles are also brighter but have a different colour and are lifted.
            var cover = SmoothStep(0.03f, 0.15f, m.Read(_cover, fx, fy));
            if (cover > 0)
            {
                float bl = m.Read(_behindL, fx, fy), ba = m.Read(_behindA, fx, fy), bb = m.Read(_behindB, fx, fy);
                var share = Math.Clamp((l - area) / Math.Max(0.05f, bl - area), 0, 1);   // share of "behind" by brightness
                // A gap (a mix of dark branch and background) is never brighter than the background; anything brighter
                // is sunlit foliage. Without this limit, leaves in front of green meadows count as gaps.
                var notBrighter = 1 - SmoothStep(bl + 0.01f, bl + 0.06f, l);
                var chroma = MathF.Sqrt((a - ba) * (a - ba) + (b - bb) * (b - bb));
                w *= 1 - cover * share * notBrighter * (1 - SmoothStep(ChromaSame, ChromaOther, chroma));
            }
            return w;
        }
    }

    /// <summary>Colour distance (Oklab a/b) to "behind": up to <c>ChromaSame</c> the same colour (gap), from
    /// <c>ChromaOther</c> a different one.</summary>
    private const float ChromaSame = 0.03f, ChromaOther = 0.08f;

    /// <summary>Returns the region brightness at an image pixel.</summary>
    /// <param name="x">X coordinate in image pixels.</param>
    /// <param name="y">Y coordinate in image pixels.</param>
    /// <returns>Smoothed Oklab L, bilinearly interpolated from the map.</returns>
    public float AreaBrightness(int x, int y)
    {
        var (fx, fy) = MapPoint(x, y);
        return Read(_base, fx, fy);
    }

    private (float X, float Y) MapPoint(int x, int y) =>
        (Math.Clamp((x + 0.5f) / _scale - 0.5f, 0, _w - 1), Math.Clamp((y + 0.5f) / _scale - 0.5f, 0, _h - 1));

    private float Read(float[] m, float fx, float fy)
    {
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, _w - 1), y1 = Math.Min(y0 + 1, _h - 1);
        float tx = fx - x0, ty = fy - y0;
        return (m[y0 * _w + x0] * (1 - tx) + m[y0 * _w + x1] * tx) * (1 - ty) +
               (m[y1 * _w + x0] * (1 - tx) + m[y1 * _w + x1] * tx) * ty;
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Guided filter (He, Sun, Tang 2010) with a greyscale guide; box filter via running sums. The result is
    /// clamped to 0 … 1.</summary>
    internal static float[] Guided(float[] p, float[] guide, int w, int h, int r, float eps)
    {
        var n = w * h;
        var ii = new float[n];
        var ip = new float[n];
        for (var i = 0; i < n; i++) { ii[i] = guide[i] * guide[i]; ip[i] = guide[i] * p[i]; }
        var mI = Box(guide, w, h, r);
        var mP = Box(p, w, h, r);
        var mII = Box(ii, w, h, r);
        var mIP = Box(ip, w, h, r);
        var a = new float[n];
        var b = new float[n];
        for (var i = 0; i < n; i++)
        {
            var variance = mII[i] - mI[i] * mI[i];
            a[i] = (mIP[i] - mI[i] * mP[i]) / (variance + eps);
            b[i] = mP[i] - a[i] * mI[i];
        }
        var ma = Box(a, w, h, r);
        var mb = Box(b, w, h, r);
        var q = new float[n];
        for (var i = 0; i < n; i++) q[i] = Math.Clamp(ma[i] * guide[i] + mb[i], 0, 1);
        return q;
    }

    /// <summary>Mean over ±r (at the border only what lies inside the image).</summary>
    private static float[] Box(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, h, ComputeLimits.Options, y => Line(src, tmp, y * w, 1, w, r));
        Parallel.For(0, w, ComputeLimits.Options, x => Line(tmp, dst, x, w, h, r));
        return dst;
    }

    private static void Line(float[] src, float[] dst, int start, int step, int length, int r)
    {
        double sum = 0;
        var count = 0;
        for (var k = 0; k <= Math.Min(r, length - 1); k++) { sum += src[start + k * step]; count++; }
        for (var k = 0; k < length; k++)
        {
            dst[start + k * step] = (float)(sum / count);
            var add = k + r + 1;
            var remove = k - r;
            if (add < length) { sum += src[start + add * step]; count++; }
            if (remove >= 0) { sum -= src[start + remove * step]; count--; }
        }
    }
}
