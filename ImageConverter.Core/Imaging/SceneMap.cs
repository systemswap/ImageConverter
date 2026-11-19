namespace ImageConverter.Core.Imaging;

/// <summary>
/// Scene map: where the sky, the halo of a light source (sun) and the landscape lie. It is determined once at the start
/// from the image, on the small working copy (by default at most 1024 px), with soft transitions. All steps that protect
/// the sky or the sun read this single map instead of computing their own rules.
/// </summary>
/// <remarks>
/// Encoding: 0 landscape, 1 sky, values above 1 halo (1 + halo strength). From this,
/// <see cref="Land"/> = 1 − min(1, sky) and <see cref="Halo"/> = min(1, max(0, sky − 1)).
/// </remarks>
internal sealed class SceneMap
{
    /// <summary>Sky value per cell of the small copy (0 … 2, see the remarks).</summary>
    public float[] Sky { get; }
    public int W { get; }
    public int H { get; }

    /// <summary>One cell of the map covers <see cref="Factor"/> × <see cref="Factor"/> image pixels.</summary>
    public int Factor { get; }

    /// <summary>Colour of the sky nearby, per pixel of the small copy: coverage (smoothed sky weight), Oklab L, a, b – a
    /// normalised convolution of the sky colour over the sky, extended into the land next to it.</summary>
    private readonly float[] _skyColour;

    /// <summary>Noise of the source picture: standard deviation of the gamma-encoded luma (Immerkær 1996), measured once.</summary>
    public float Noise { get; set; }

    /// <summary>How much the passes so far have amplified the noise, per pixel of the small copy (1 − mix + mix / t per
    /// pass, multiplied up); 1 before the first pass.</summary>
    public float[] Gain { get; set; } = [];

    /// <summary>Transmission of the first pass per pixel of the small copy (1 = no haze): the depth of the scene, as far
    /// as the haze reveals it. Read by the brightness compensation, which gives distant haze only a share.</summary>
    public float[] Depth { get; set; } = [];

    /// <summary>Transmission of the first pass at an image pixel (bilinear from the small copy); 1 without a map.</summary>
    public float DepthAt(int x, int y)
    {
        if (Depth.Length == 0) return 1;
        var (sx, sy) = Point(x, y);
        int x0 = (int)sx, y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
        float fx = sx - x0, fy = sy - y0;
        var top = Depth[y0 * W + x0] * (1 - fx) + Depth[y0 * W + x1] * fx;
        var bottom = Depth[y1 * W + x0] * (1 - fx) + Depth[y1 * W + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>
    /// Sky colour and depth packed for the GPU kernels, five values per cell of the small copy: coverage, Oklab L, a, b
    /// of the sky nearby, and the transmission of the first pass (1 where no depth map exists).
    /// </summary>
    internal float[] PackedForGpu()
    {
        var n = W * H;
        var packed = new float[n * 5];
        for (var p = 0; p < n; p++)
        {
            packed[p * 5] = _skyColour[p * 4];
            packed[p * 5 + 1] = _skyColour[p * 4 + 1];
            packed[p * 5 + 2] = _skyColour[p * 4 + 2];
            packed[p * 5 + 3] = _skyColour[p * 4 + 3];
            packed[p * 5 + 4] = Depth.Length == n ? Depth[p] : 1;
        }
        return packed;
    }

    /// <summary>Noise amplification at an image pixel (bilinear from the small copy); 1 without a map.</summary>
    public float GainAt(int x, int y)
    {
        if (Gain.Length == 0) return 1;
        var (sx, sy) = Point(x, y);
        int x0 = (int)sx, y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
        float fx = sx - x0, fy = sy - y0;
        var top = Gain[y0 * W + x0] * (1 - fx) + Gain[y0 * W + x1] * fx;
        var bottom = Gain[y1 * W + x0] * (1 - fx) + Gain[y1 * W + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private SceneMap(float[] sky, float[] skyColour, int w, int h, int factor) => (Sky, _skyColour, W, H, Factor) = (sky, skyColour, w, h, factor);

    /// <summary>Builds the map from the small working copy.</summary>
    /// <param name="small">Small copy in linear light (B, G, R per cell).</param>
    /// <param name="w">Width of the small copy in cells.</param>
    /// <param name="h">Height of the small copy in cells.</param>
    /// <param name="factor">Downscaling factor from the image to the small copy.</param>
    public static SceneMap Of(float[] small, int w, int h, int factor)
    {
        var sky = Dehaze.SkyMap(small, w, h);
        return new SceneMap(sky, SkyColour(small, sky, w, h), w, h, factor);
    }

    /// <summary>Sky value (0 … 2) at an image pixel, bilinearly interpolated from the map.</summary>
    public float SkyAt(int x, int y)
    {
        var sx = Math.Clamp((x + 0.5f) / Factor - 0.5f, 0, W - 1);
        var sy = Math.Clamp((y + 0.5f) / Factor - 0.5f, 0, H - 1);
        int x0 = (int)sx, y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
        float fx = sx - x0, fy = sy - y0;
        var top = Sky[y0 * W + x0] * (1 - fx) + Sky[y0 * W + x1] * fx;
        var bottom = Sky[y1 * W + x0] * (1 - fx) + Sky[y1 * W + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>1 = landscape, 0 = sky or halo.</summary>
    public static float Land(float sky) => 1 - Math.Clamp(sky, 0, 1);

    /// <summary>1 = in the centre of the halo of a light source.</summary>
    public static float Halo(float sky) => Math.Clamp(sky - 1, 0, 1);

    /// <summary>Smoothing of the sky colour (Gaussian sigma), relative to the longest edge of the small copy.</summary>
    private const double ColourReachPerEdge = 0.012;

    private static float[] SkyColour(float[] small, float[] sky, int w, int h)
    {
        var n = w * h;
        var cover = new float[n]; var l = new float[n]; var a = new float[n]; var b = new float[n];
        for (var p = 0; p < n; p++)
        {
            var ws = Math.Clamp(sky[p], 0, 1);
            if (ws <= 0) continue;
            var (L, A, B) = Oklab.FromLinear(small[p * 3 + 2], small[p * 3 + 1], small[p * 3]);
            cover[p] = ws; l[p] = L * ws; a[p] = A * ws; b[p] = B * ws;
        }
        var sigma = Math.Max(1.0, ColourReachPerEdge * Math.Max(w, h));
        foreach (var plane in new[] { cover, l, a, b }) Blur.Gauss(plane, w, h, sigma);
        var map = new float[n * 4];
        for (var p = 0; p < n; p++)
        {
            var c = cover[p];
            map[p * 4] = c;
            if (c > 1e-4f) { map[p * 4 + 1] = l[p] / c; map[p * 4 + 2] = a[p] / c; map[p * 4 + 3] = b[p] / c; }
        }
        return map;
    }

    /// <summary>
    /// Sky share of a pixel including the colour criterion: a pixel next to the sky whose colour matches the sky nearby
    /// counts as sky even where the structure map has a transitional value (the edge of a tree, a roof line). Without
    /// this, vibrance and curve act partly in a 150 px band along every silhouette and saturate the sky there
    /// (Fehlergedächtnis #129). The halo (values above 1) is returned unchanged.
    /// </summary>
    /// <param name="x">X in image pixels.</param>
    /// <param name="y">Y in image pixels.</param>
    /// <param name="L">Oklab lightness of the original pixel.</param>
    /// <param name="a">Oklab a of the original pixel.</param>
    /// <param name="b">Oklab b of the original pixel.</param>
    /// <returns>Sky share 0 … 1, or above 1 in the halo.</returns>
    public float SkyAt(int x, int y, float L, float a, float b)
    {
        var sky = SkyAt(x, y);
        if (sky >= 1) return sky;
        var (sx, sy) = Point(x, y);
        var cover = Sample(_skyColour, sx, sy, 0);
        if (cover < CoverMin) return sky;
        float dL = L - Sample(_skyColour, sx, sy, 1), da = a - Sample(_skyColour, sx, sy, 2), db = b - Sample(_skyColour, sx, sy, 3);
        var dist = MathF.Sqrt(0.25f * dL * dL + da * da + db * db);
        var like = 1 - SmoothStep(ColourNear, ColourFar, dist);
        return Math.Max(sky, like * SmoothStep(CoverFrom, CoverTo, cover));
    }

    /// <summary>Colour criterion of <see cref="SkyAt(int, int, float, float, float)"/> (also used by the GPU kernels): up to
    /// <see cref="ColourNear"/> Oklab distance a pixel has the colour of the sky nearby, from <see cref="ColourFar"/> on a
    /// different one; the sky must cover at least <see cref="CoverMin"/> of the neighbourhood, fully counted from
    /// <see cref="CoverTo"/>.</summary>
    internal const float ColourNear = 0.02f, ColourFar = 0.05f, CoverMin = 0.02f, CoverFrom = 0.02f, CoverTo = 0.25f;

    private (float X, float Y) Point(int x, int y) =>
        (Math.Clamp((x + 0.5f) / Factor - 0.5f, 0, W - 1), Math.Clamp((y + 0.5f) / Factor - 0.5f, 0, H - 1));

    private float Sample(float[] map, float sx, float sy, int channel)
    {
        int x0 = (int)sx, y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
        float fx = sx - x0, fy = sy - y0;
        var top = map[(y0 * W + x0) * 4 + channel] * (1 - fx) + map[(y0 * W + x1) * 4 + channel] * fx;
        var bottom = map[(y1 * W + x0) * 4 + channel] * (1 - fx) + map[(y1 * W + x1) * 4 + channel] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private static float SmoothStep(float e0, float e1, float v)
    {
        var t = Math.Clamp((v - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
