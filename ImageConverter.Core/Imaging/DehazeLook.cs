using ImageConverter.Core.Platform;
namespace ImageConverter.Core.Imaging;

/// <summary>
/// Look per pass: clarity, contrast, vibrance, fine sharpening and edges. There is one mechanism per effect, in one place,
/// applied after the physical model (<see cref="Apply(float[], int, int, float, Pass?, Settings, float, int)"/> with
/// perception disabled). If the fine sliders acted both inside the dehaze filter and again in a separate tool chain
/// (per-channel contrast, vibrance, unsharp mask), every effect would be applied twice: vibrance becomes too strong, and the
/// tool steps also hit the sky.
/// </summary>
/// <remarks>
/// The scene map (<see cref="SceneMap"/>) determines where each effect acts: clarity and vibrance in the landscape (only a
/// small share of vibrance in the sky), the curve everywhere but reduced in the halo of a light source (its shoulder
/// compresses the rays), sharpening fully in the landscape and the halo (the rays are structure) and only a share in the
/// smooth sky.
/// </remarks>
internal static partial class Dehaze
{
    /// <summary>Strengths for one pass, already combined with the slider values and the dehaze strength.</summary>
    internal sealed record LookSettings(float Clarity, float Curve, float Vibrance, float VibranceSky, float Sharpen, float Edge)
    {
        public bool IsNone => Clarity <= 0 && Curve == 0 && Vibrance <= 0 && VibranceSky <= 0 && Sharpen <= 0 && Edge <= 0;
    }

    /// <summary>Applies clarity, curve and vibrance (before debanding) in place.</summary>
    internal static void LookTone(float[] px, int width, int height, SceneMap? scene, LookSettings s, int workEdge = 0)
    {
        if (s.Clarity <= 0 && s.Curve == 0 && s.Vibrance <= 0 && s.VibranceSky <= 0) return;

        // Base lightness for clarity: Oklab L, smoothed edge-preservingly (guided filter on the small copy, with the image
        // itself as the guide). A plain blur would produce halos at edges.
        var f = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)(workEdge > 0 ? workEdge : WorkEdge)));
        (float[] A, float[] B) baseL = default;
        int sw = 0, sh = 0;
        if (s.Clarity > 0)
        {
            var (small, w2, h2) = DownscaleLinear(px, width, height, f);
            (sw, sh) = (w2, h2);
            var l = new float[sw * sh];
            Parallel.For(0, sh, ComputeLimits.Options, row =>
            {
                for (int x = 0, p = row * sw; x < sw; x++, p++) l[p] = Oklab.FromLinear(small[p * 3 + 2], small[p * 3 + 1], small[p * 3]).L;
            });
            baseL = GuidedColor.Coefficients(Encode(small), l, sw, sh, Math.Max(1.0, ClarityPerEdge * Math.Max(sw, sh)), eps: 1e-3f);
        }

        // GPU: the same per-pixel loop.
        if (ToneOnGpu(px, width, height, f, sw, sh, baseL, scene, s)) return;

        Parallel.For(0, height, ComputeLimits.Options, y =>
        {
            var sy = (y + 0.5f) / f - 0.5f;
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var i = p * 4;
                float b = px[i], g = px[i + 1], r = px[i + 2];
                var (L, oa, ob) = Oklab.FromSrgb(r, g, b);
                var sky = scene?.SkyAt(x, y, L, oa, ob) ?? 0;
                var land = SceneMap.Land(sky);
                var halo = SceneMap.Halo(sky);
                if (s.Clarity > 0)
                {
                    var cell = new Cell(sw, sh, (x + 0.5f) / f - 0.5f, sy);
                    L += s.Clarity * land * (L - cell.Sample(baseL, b, g, r));
                }
                L = Math.Clamp(L, 0, 1);
                // In the halo the curve is reduced by HaloCurveShare (to 75 % at the centre): without any curve the rays lack
                // contrast; at full strength its shoulder compresses the rays.
                L = Curve(L, s.Curve * (1 - HaloCurveShare * halo));

                var chroma = MathF.Sqrt(oa * oa + ob * ob);
                var vibrance = s.Vibrance * land + s.VibranceSky * Math.Min(1, sky) * (1 - halo);
                var factor = (1 + vibrance * (1 - Math.Min(1, chroma / VibranceFull)))
                             * (1 + CurveChroma * s.Curve * (1 - halo));   // chroma gain of a per-channel curve, see CurveChroma
                (r, g, b) = Oklab.ToSrgb(L, oa * factor, ob * factor);
                px[i] = b; px[i + 1] = g; px[i + 2] = r;
            }
        });
    }

    /// <summary>
    /// Pivot of the S-curve in Oklab L. The value lies between the pivots of a sine curve around L 0.5 and a per-channel
    /// curve around sRGB 0.5 (L ≈ 0.6) and reproduces their combined effect. With a pivot of 0.5 alone, images with dark
    /// midtones become noticeably brighter.
    /// </summary>
    private const float CurvePivot = 0.56f;

    /// <summary>Gentle S-curve around <see cref="CurvePivot"/>: both halves are stretched to 0 … ½ and ½ … 1, then the
    /// sine curve is applied (slope at the pivot 1 + <paramref name="k"/>, end points fixed).</summary>
    private static float Curve(float L, float k)
    {
        if (k == 0) return L;
        var x = L < CurvePivot ? 0.5f * L / CurvePivot : 0.5f + 0.5f * (L - CurvePivot) / (1 - CurvePivot);
        x -= k * MathF.Sin(2 * MathF.PI * x) / (2 * MathF.PI);
        return x < 0.5f ? 2 * x * CurvePivot : CurvePivot + (2 * x - 1) * (1 - CurvePivot);
    }

    /// <summary>
    /// Chroma that rises with the curve. A per-channel curve spreads the channels and thereby raises chroma as a side
    /// effect; a curve on lightness does not. Without compensation, a smoggy test photo lost 10 % of its colour.
    /// Compensating with more vibrance in the sky instead turned pale hills turquoise.
    /// </summary>
    private const float CurveChroma = 0.3f;

    /// <summary>Share of the sharpening applied in the smooth sky.</summary>
    private const float SkySharpenShare = 0.4f;

    /// <summary>Share by which the curve is reduced in the halo of a light source.</summary>
    private const float HaloCurveShare = 0.25f;

    /// <summary>Applies fine sharpening and edges (after debanding) in place; in the smooth sky only a share.</summary>
    internal static void LookDetail(float[] px, int width, int height, SceneMap? scene, LookSettings s)
    {
        if (s.Sharpen > 0) WeightedSharpen(px, width, height, scene, s.Sharpen, FineSigma);
        if (s.Edge > 0) WeightedSharpen(px, width, height, scene, s.Edge, EdgeSigma);
    }

    /// <summary>Coring thresholds in multiples of the local noise: no sharpening below the low one, full above the high one.</summary>
    private const float CoringLow = 1f, CoringHigh = 2.5f;

    /// <summary>Largest noise amplification taken into account: beyond it the threshold would also swallow real edges
    /// in the distance (ridge lines), which are worth more than the noise they carry.</summary>
    private const float CoringGainCap = 2f;

    private static float CoringStep(float e0, float e1, float v)
    {
        var t = Math.Clamp((v - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static void WeightedSharpen(float[] px, int width, int height, SceneMap? scene, float amount, double sigma)
    {
        var sharp = Sharpen.UnsharpMask(px, width, height, amount, sigma);
        Parallel.For(0, height, ComputeLimits.Options, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var sky = scene?.SkyAt(x, y) ?? 0;
                // Full strength in the landscape and the halo (the rays are structure), a share in the sky: at full
                // strength it emphasises JPEG blocks and noise (banding in the sky 0.79 instead of 0.59); without any,
                // the rays are missing.
                var land = SceneMap.Land(sky);
                var halo = SceneMap.Halo(sky);
                var w = land + (1 - land) * SkySharpenShare + halo * (1 - SkySharpenShare);
                if (w <= 0) continue;
                var i = p * 4;
                // Coring (classic, as in analogue video and Simoncelli & Adelson 1996): a detail is sharpened only when it
                // stands out of the noise. The threshold comes from the measured noise of the source and the amplification
                // of the passes so far – not from any local variance, and nothing is fitted to the output.
                if (scene is { Noise: > 0 } sc)
                {
                    var detail = (0.0722f * (sharp[i] - px[i]) + 0.7152f * (sharp[i + 1] - px[i + 1]) + 0.2126f * (sharp[i + 2] - px[i + 2])) / amount;
                    var sigma = sc.Noise * Math.Min(CoringGainCap, sc.GainAt(x, y));
                    // not in the halo of a light source: its rays are structure, and the halo is bright, hardly noisy
                    w *= halo + (1 - halo) * CoringStep(CoringLow * sigma, CoringHigh * sigma, Math.Abs(detail));
                    if (w <= 0) continue;
                }
                for (var c = 0; c < 3; c++) px[i + c] += (sharp[i + c] - px[i + c]) * w;
            }
        });
        PixelPool.Return(sharp);
    }
}
