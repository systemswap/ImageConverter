using ImageConverter.Core.Platform;
namespace ImageConverter.Core.Imaging;

/// <summary>
/// Removes haze according to the physical scattering model, in linear light:
/// image = scene · t + air light · (1 − t), where t is the transmission of the air.
/// </summary>
/// <remarks>
/// Structure based on the dark channel prior (He, Sun, Tang 2009/2011), with improvements that address its known
/// weaknesses:
/// <list type="number">
/// <item>Computing in linear light: the model applies to quantities of light, not to gamma-encoded values (otherwise
///   colour shifts and wrong brightness).</item>
/// <item>Air light: among the pixels with the highest dark channel the brightest decide, without clipped ones, per
///   tile of the picture, so that neither the sun nor a single pixel determines it.</item>
/// <item>Transmission from the dark channel on a downscaled copy, refined with a guided filter with a colour guide image
///   and brought to full size edge-preservingly (Fast Guided Filter, He 2015).</item>
/// <item>Tolerance: whatever resembles the air light (sky, white walls) does not count as dense haze (otherwise colour
///   casts and noise in the sky).</item>
/// <item>Brightness compensation: matched to the original on a large scale, separately for landscape and sky, so that
///   only local contrast and colour are gained; otherwise the image becomes dark.</item>
/// </list>
/// </remarks>
internal static partial class Dehaze
{
    /// <summary>Longest edge (px) of the copy on which air light and transmission are estimated.</summary>
    private const int WorkEdge = 1024;

    /// <summary>Window of the dark channel (radius), relative to the longest edge.</summary>
    private const double PatchPerEdge = 0.012;

    /// <summary>Guided filter for the transmission (radius as Gaussian sigma), relative to the longest edge.</summary>
    private const double RefinePerEdge = 0.025;

    /// <summary>Share of the brightness compensation in haze: <see cref="CompensationFar"/> up to a transmission of
    /// <see cref="CompensationFrom"/> in the first pass, the full share from <see cref="CompensationTo"/> on (nearly clear
    /// air). Hazy distance keeps the darker, clearer look; the exposure of the near scene is kept.</summary>
    private const float CompensationFar = 0.3f, CompensationFrom = 0.35f, CompensationTo = 0.85f;

    /// <summary>Brightness compensation: smoothing of the light quantities (Gaussian sigma), relative to the longest edge.
    /// Measured: 0.0125 keeps the hue of vegetation under thin haze better than 0.05 (4.3° instead of 6.1° at dehaze
    /// 300) and gains slightly more local contrast in the valley, without fringes at trees.</summary>
    private const double BrightnessPerEdge = 0.0125;

    // Defaults of the fine-tuning sliders in percent (sliders in the Tools tile); Adjustments uses them as its
    // defaults.
    /// <summary>Share of the brightness compensation (100 = fully matched to the original).</summary>
    internal const double DefaultBrightness = 85;
    /// <summary>Clarity (local contrast, large radius) at slider 100.</summary>
    internal const double DefaultClarity = 35;
    /// <summary>Fine sharpening (3×3 unsharp mask) at slider 100.</summary>
    internal const double DefaultSharpen = 60;
    /// <summary>S-curve on the lightness at slider 100 (slope in the middle: 1 + value).</summary>
    internal const double DefaultCurve = 12;
    /// <summary>Vibrance at slider 100: pale colours gain up to this much more chroma.</summary>
    internal const double DefaultVibrance = 25;

    /// <summary>Maximum number of passes.</summary>
    internal const int MaxPasses = 10;

    /// <summary>Default number of passes: one pass is deliberately gentle; only several passes produce the effect.</summary>
    internal const int DefaultPasses = 3;

    // One gentle step per pass: dehaze 40 with contrast +8 and vibrance +8 per round is far too strong.
    /// <summary>Dehaze amount per pass at slider 100.</summary>
    internal const double PassAmount = 0.3;

    // Look per pass (DehazeLook): strength per unit of the fine slider at dehaze 100. Each value combines the effects
    // that would otherwise act in two places (inside the dehaze filter and through the Tools chain), see
    // DehazeLook.cs.
    /// <summary>Clarity per pass (applied to the already dehazed image, and therefore lower than a clarity applied inside
    /// the dehaze filter).</summary>
    internal const double LookClarity = 0.2;
    /// <summary>S-curve on the lightness per pass (sine curve and Tools contrast combined).</summary>
    internal const double LookCurve = 0.6;
    /// <summary>Vibrance per pass in the landscape and in the sky.</summary>
    internal const double LookVibrance = 0.42, LookVibranceSky = 0.12;
    /// <summary>Fine sharpening per pass (3×3 in the dehaze filter and unsharp mask combined).</summary>
    internal const double LookSharpen = 0.9;

    /// <summary>Default debanding in percent.</summary>
    internal const double DefaultDeband = 50;

    /// <summary>Default "haze colour" in percent: how much of the air-light colour is subtracted (0 = hue as in the
    /// original). Half: smog loses its yellow cast, evening light keeps part of its warmth.</summary>
    internal const double DefaultCast = 50;
    /// <summary>Share of the debanding applied to the source (before the first pass): its steps are only one 8-bit level
    /// high, so a smaller threshold suffices and spares fine structure.</summary>
    internal const float SourceDebandShare = 0.6f;
    /// <summary>Gaussian sigma of the fine unsharp mask in the loop.</summary>
    internal const double FineSigma = 0.7;
    /// <summary>Share of the edge slider per pass.</summary>
    internal const double LoopEdgeShare = 0.5;

    /// <summary>Edge sharpening (unsharp mask after all passes): Gaussian sigma in pixels.</summary>
    internal const double EdgeSigma = 2.0;

    /// <summary>Perceptual shares as factors (percent / 100); see the Default constants.</summary>
    internal sealed record Settings(float Clarity, float Sharpen, float Curve, float Vibrance, float BrightnessMatch, float CastRemoval = (float)(DefaultCast / 100))
    {
        public static readonly Settings Default = new(
            (float)(DefaultClarity / 100), (float)(DefaultSharpen / 100), (float)(DefaultCurve / 100),
            (float)(DefaultVibrance / 100), (float)(DefaultBrightness / 100));
    }

    /// <summary>Tolerance (linear light): colours closer to the air light do not count as dense haze.</summary>
    private const float Tolerance = 0.08f;

    /// <summary>
    /// Chroma: at most this much more than in the original, per slider unit, in addition to what the haze took away
    /// (1/t). The model itself would give much more with thin haze: it drives the darkest channel towards zero, which for
    /// green is blue, and fields turn ochre and brown.
    /// </summary>
    private const float ChromaPerUnit = 0.15f;

    /// <summary>Share of the colour cast per unit of dehaze amount at "haze colour" 100 %.</summary>
    private const float HueRecovery = 1.0f;

    /// <summary>At most this much of the air-light colour counts as mixed in (above it, the pixel is almost only haze).</summary>
    private const float MaxCastShare = 0.8f;

    /// <summary>Radius of the clarity (Gaussian sigma of the guided filter), relative to the longest edge.</summary>
    private const double ClarityPerEdge = 0.02;

    /// <summary>From this Oklab chroma on, vibrance has no effect (strong colours stay).</summary>
    private const float VibranceFull = 0.12f;

    /// <summary>Smallest transmission; limits the amplification.</summary>
    private const float MinTransmission = 0.1f;

    /// <summary>Dehazes 8-bit BGRA pixels with the default settings in a single pass.</summary>
    /// <param name="bgra">Pixels, 4 bytes per pixel (B, G, R, A); not modified.</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="amount">−1 … +3: positive values remove haze (above 1 amplified), negative values add haze.</param>
    /// <returns>A new array with the result.</returns>
    public static byte[] Apply(byte[] bgra, int width, int height, float amount) =>
        Apply(bgra, width, height, amount, null, Settings.Default, share: 1f);

    /// <summary>What one pass hands on to the next: the air-light field and the scene map (sky, halo).</summary>
    internal sealed class Pass
    {
        public float[] Air = [];      // B, G, R per pixel of the small copy
        public SceneMap? Scene;       // Computed once in the first pass, read by all steps
        public int W, H;
    }

    /// <summary>Dehazes 8-bit BGRA pixels; converts to floating point and back (without dithering).</summary>
    /// <param name="pass">Passes: reuse the air light and sky of the first pass. The haze amount is estimated anew on the
    /// intermediate result each time: where the haze is gone it finds nothing more, and the distance keeps clearing. In
    /// the sky only the first pass acts; otherwise the estimate keeps finding "haze" there (noise, gradient) and the sky
    /// becomes grainy and blotchy.</param>
    /// <param name="s">Perceptual shares and brightness compensation.</param>
    /// <param name="share">Share of the perceptual effects (clarity, sharpening, curve, vibrance) in this pass. The loop
    /// uses 1 in every round, as the eye works: contrast and colour after each round, and the next round estimates the
    /// higher-contrast state anew. Applied only at the end, the passes would achieve almost nothing.</param>
    internal static byte[] Apply(byte[] bgra, int width, int height, float amount, Pass? pass, Settings s, float share) =>
        Pixels.ToBytes(Apply(Pixels.FromBytes(bgra), width, height, amount, pass, s, share), dither: false);

    /// <summary>As above in floating point (B, G, R, A per pixel, sRGB-encoded 0–1), so that the passes do not round to
    /// 8 bits after every step; that rounding produces banding in the sky.</summary>
    /// <param name="workEdge">Longest edge of the estimation copy in px; 0 = <see cref="WorkEdge"/>.</param>
    /// <returns>A new buffer: a plain copy if <paramref name="amount"/> is 0, otherwise rented from
    /// <see cref="PixelPool"/>.</returns>
    internal static float[] Apply(float[] bgra, int width, int height, float amount, Pass? pass, Settings s, float share, int workEdge = 0)
    {
        if (amount == 0) return (float[])bgra.Clone();
        var strength = Math.Abs(amount);
        // Slider −100 … +300: up to 100 as the physical model (mix original → result); above that, the estimated haze
        // amount is amplified continuously up to threefold, because the model alone is very subtle with backlight and
        // light haze.
        var mix = Math.Min(1, strength);
        var boost = amount > 0 ? Math.Max(1, strength) : 1;

        // All estimates on a downscaled copy (area mean in linear light); the large image is then traversed only once.
        // Panoramas have 80 MP, and a floating-point copy would need gigabytes.
        var f = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)(workEdge > 0 ? workEdge : WorkEdge)));
        var (small, sw, sh) = DownscaleLinear(bgra, width, height, f);
        var fresh = pass is null || pass.Air.Length == 0;
        var scene = pass?.Scene ?? SceneMap.Of(small, sw, sh, f);
        var sky = scene.Sky;
        var air = fresh ? AirLightField(small, sw, sh, sky) : pass!.Air;   // B, G, R per pixel of the small copy
        if (pass is not null && fresh)
        {
            pass.Air = air; pass.Scene = scene; pass.W = sw; pass.H = sh;
            // For the coring of the sharpening: the noise of the source and, from pass to pass, its amplification
            scene.Noise = EstimateNoise(bgra, width, height);
            scene.Gain = new float[sw * sh];
            Array.Fill(scene.Gain, 1f);
        }
        var t = Transmission(small, sw, sh, air, 0.95f);
        if (!fresh)
            for (var p = 0; p < t.Length; p++) t[p] += (1 - t[p]) * Math.Min(1, sky[p]);   // Sky: only in the first pass
        var boosted = ProtectSky(sky, t, boost);
        if (pass is not null && fresh) scene.Depth = (float[])t.Clone();   // depth for the brightness compensation
        if (boosted != null) LimitBoost(small, sw, sh, air, t, boosted);
        // Colour of the air light per cell (linear luma, Oklab a/b), for subtracting the colour cast
        var airLab = new float[sw * sh * 3];
        Parallel.For(0, sh, ComputeLimits.Options, row =>
        {
            for (int x = 0, p = row * sw; x < sw; x++, p++)
            {
                var (_, la, lb) = Oklab.FromLinear(air[p * 3 + 2], air[p * 3 + 1], air[p * 3]);
                (airLab[p * 3], airLab[p * 3 + 1], airLab[p * 3 + 2]) = (Luma(air[p * 3], air[p * 3 + 1], air[p * 3 + 2]), la, lb);
            }
        });
        var guide = Encode(small);
        var refine = Math.Max(1.0, RefinePerEdge * Math.Max(sw, sh));
        var plain = GuidedColor.Coefficients(guide, t, sw, sh, refine, eps: 1e-3f);
        var strong = boosted == null ? default : GuidedColor.Coefficients(guide, boosted, sw, sh, refine, eps: 1e-3f);

        // Air light at a position of the large image: bilinear from the field of the small copy
        void AirAt(in Cell cell, float[] into)
        {
            for (var c = 0; c < 3; c++) into[c] = cell.Of(air, c);
        }

        // Transmission at a position (coordinates of the small copy), guide image gamma-encoded
        float TransmitAt(in Cell cell, float skyAt, float gB, float gG, float gR, float b, float g, float r, float[] a)
        {
            var landHere = 1 - Math.Clamp(skyAt, 0, 1);   // Above 1 = halo, counts as sky
            var haloHere = Math.Clamp(skyAt - 1, 0, 1);
            var tt = Tolerate(Math.Clamp(cell.Sample(plain, gB, gG, gR), 0, 1), b, g, r, a, 0, landHere);
            if (boosted != null)
                tt = Math.Min(tt, Tolerate(Math.Clamp(cell.Sample(strong, gB, gG, gR), 0, 1), b, g, r, a, 0, landHere));
            // Halo of the sun: light, not haze, so nothing is recovered there. Without this, the recovery brightens the
            // halo and the rays run into white (fluctuation at r 120: ±8.5 instead of ±11.3 in the lab reference).
            return tt + (1 - tt) * haloHere;
        }

        // Brightness compensation: ratio original/result on the small copy
        (float[] A, float[] B) gain = default;
        if (amount > 0)
        {
            var after = new float[small.Length];
            Parallel.For(0, sh, ComputeLimits.Options, row =>
            {
                var a3 = new float[3];
                for (int x = 0, p = row * sw; x < sw; x++, p++)
                {
                    var o = p * 3;
                    a3[0] = air[o]; a3[1] = air[o + 1]; a3[2] = air[o + 2];
                    var cell = new Cell(sw, sh, x, row);
                    var tt = TransmitAt(cell, sky[p], guide[p * 3], guide[p * 3 + 1], guide[p * 3 + 2],
                        small[p * 3], small[p * 3 + 1], small[p * 3 + 2], a3);
                    for (var c = 0; c < 3; c++) after[p * 3 + c] = Recover(small[p * 3 + c], air[o + c], tt, mix);
                    // The recovery scales the noise by (1 − mix + mix / t); the passes multiply up
                    if (scene.Gain.Length == after.Length / 3) scene.Gain[p] *= 1 - mix + mix / Math.Max(tt, 0.05f);
                }
            });
            gain = BrightnessGain(small, after, sky, sw, sh);
        }
        // The compensation share is scaled with the dehaze amount on purpose (the darkening in "after" already grows with
        // mix, so at dehaze 36 about a third of the set share remains): a gentle dehaze keeps a gently darker, clearer
        // picture, a strong one is matched more. Users have tuned their pictures to this behaviour.
        var k = s.BrightnessMatch * mix;
        // Chroma: haze reduces it by the factor t, so restoring up to 1/t is justified, plus a small remainder; thin haze
        // (fields at medium distance) thus gains hardly any colour

        // Perception: the model alone leaves smog and thin haze almost unchanged (the dark channel always
        // finds something black between houses and trees). Added are local contrast (clarity), fine sharpening, a gentle
        // S-curve and vibrance, growing with the slider, more slowly above 100. Clarity and sharpening not in the sky
        // (noise).
        var over = Math.Max(0, strength - 1);
        var perceptual = share;
        var clarity = perceptual * (s.Clarity * mix + s.Clarity * 0.4f * over);
        var sharpen = perceptual * (s.Sharpen * mix + s.Sharpen * 0.4f * over);
        var curve = perceptual * (s.Curve * mix + s.Curve * 0.5f * over);
        var vibrance = perceptual * (s.Vibrance * mix + s.Vibrance * 0.4f * over);
        (float[] A, float[] B) baseL = default;
        if (amount > 0 && clarity > 0)   // Without clarity (physics only, look afterwards) the base lightness is not needed
        {
            // Base lightness for the clarity: Oklab L of the original, smoothed edge-preservingly; a plain blur would
            // produce halos at the horizon and at building edges
            var l = new float[sw * sh];
            Parallel.For(0, sh, ComputeLimits.Options, row =>
            {
                for (int x = 0, p = row * sw; x < sw; x++, p++) l[p] = Oklab.FromLinear(small[p * 3 + 2], small[p * 3 + 1], small[p * 3]).L;
            });
            baseL = GuidedColor.Coefficients(guide, l, sw, sh, Math.Max(1.0, ClarityPerEdge * Math.Max(sw, sh)), eps: 1e-3f);
        }

        // GPU: the pixel loop of the model (physics only, as the passes of the chain compute it)
        if (amount > 0 && clarity == 0 && sharpen == 0 && curve == 0 && vibrance == 0 && scene.W == sw && scene.H == sh
            && ModelOnGpu(bgra, width, height, f, sw, sh, air, sky, plain, strong, gain, airLab, scene.PackedForGpu(), mix, k,
                s.CastRemoval, strength) is { } onGpu)
            return onGpu;

        var luma = amount > 0 && sharpen > 0 ? LumaPlane(bgra, width, height) : null;
        var result = PixelPool.Rent(bgra.Length);   // Every pixel is fully written below
        Parallel.For(0, height, ComputeLimits.Options, y =>
        {
            var sy = (y + 0.5f) / f - 0.5f;
            Span<float> lin = stackalloc float[3];
            var airHere = new float[3];
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var sx = (x + 0.5f) / f - 0.5f;
                var i = p * 4;
                var cell = new Cell(sw, sh, sx, sy);   // Position in the small copy, once for all maps
                AirAt(cell, airHere);
                float gB = bgra[i], gG = bgra[i + 1], gR = bgra[i + 2];   // Guide image = original
                for (var c = 0; c < 3; c++) lin[c] = Srgb.Decode(bgra[i + c]);

                var skyHere = cell.Of(sky, 0, 1);
                var tt = TransmitAt(cell, skyHere, gB, gG, gR, lin[0], lin[1], lin[2], airHere);
                var land = 1 - Math.Clamp(skyHere, 0, 1);
                var halo = Math.Clamp(skyHere - 1, 0, 1);   // Halo of the sun (sky above 1)

                float oB, oG, oR;
                if (amount > 0)
                {
                    oB = Recover(lin[0], airHere[0], tt, mix);
                    oG = Recover(lin[1], airHere[1], tt, mix);
                    oR = Recover(lin[2], airHere[2], tt, mix);

                    // Brightness matched to the original on a large scale, but never brighter than the original
                    // (otherwise a bright band appears above the horizon)
                    var g = MathF.Exp(Math.Clamp(cell.Sample(gain, gB, gG, gR), -0.7f, 1.1f) * k);
                    if (g > 1)
                    {
                        var before = Luma(lin[0], lin[1], lin[2]);
                        g = Math.Min(g, Math.Max(1, before / Math.Max(Luma(oB, oG, oR), 1e-4f)));
                    }
                    // Only in the landscape: the linear model of the compensation extrapolates onto the sun (brighter
                    // than any haze), making the centre darker and the halo brighter, and the rays blur.
                    // Distant haze gets only a share of the compensation: matching its brightness to the hazy original
                    // would give the milky distance straight back (measured: 1,3 × the true lightness however well the
                    // air light is estimated); nearby, where the haze is thin, the compensation keeps the exposure.
                    var depthShare = CompensationFar + (1 - CompensationFar) * SmoothStep(CompensationFrom, CompensationTo, scene.DepthAt(x, y));
                    g = 1 + (g - 1) * land * depthShare;
                    oB *= g; oG *= g; oR *= g;

                    // Colour: lightness from the model, hue from the original, chroma limited (Oklab). The model drives
                    // the darkest channel towards zero (for green, the blue), and fields turn ochre and brown.
                    var was = Oklab.FromLinear(lin[2], lin[1], lin[0]);
                    var now = Oklab.FromLinear(Math.Max(0, oR), Math.Max(0, oG), Math.Max(0, oB));
                    // Sky by colour: next to a silhouette the structure map is transitional; a pixel in the colour of the
                    // sky nearby stays sky for cast, chroma, clarity, sharpening and vibrance (no band along the edge)
                    land = Math.Min(land, SceneMap.Land(scene.SkyAt(x, y, was.L, was.A, was.B)));
                    // Subtract the colour cast of the haze: the haze mixes in the colour of the air light with the share
                    // (1 − t) · luma(air light) / luma(pixel). This share of its colour (Oklab a/b) is subtracted, gently
                    // (per pass, with the dehaze amount) and only in the landscape. The full hue of the model would turn
                    // fields brown (it drives the darkest channel towards zero); this removes only the haze colour: grey
                    // haze changes nothing, bluish haze loses its blue.
                    var castShare = Math.Clamp((1 - tt) * cell.Of(airLab, 0) / Math.Max(Luma(lin[0], lin[1], lin[2]), 1e-4f), 0, MaxCastShare);
                    var cast = s.CastRemoval * HueRecovery * mix * land * castShare;
                    var baseA = was.A - cast * cell.Of(airLab, 1);
                    var baseB = was.B - cast * cell.Of(airLab, 2);
                    var c0 = MathF.Sqrt(baseA * baseA + baseB * baseB);
                    var c1 = MathF.Sqrt(now.A * now.A + now.B * now.B);
                    // No additional chroma in the sky: the recovery would make the sky bluer, and the halo of the sun
                    // (haze-coloured, left unchanged) would then appear as a pink ring.
                    var chromaCap = 1 + land * (mix * (1 / tt - 1) + strength * ChromaPerUnit);   // Above 100, tt is already amplified
                    var ratio = c0 > 1e-3f ? Math.Min(c1 / c0, chromaCap) : 1;

                    // Clarity, sharpening, curve (on the lightness), vibrance (on the chroma)
                    var L = now.L
                          + (clarity > 0 ? clarity * land * (was.L - cell.Sample(baseL, gB, gG, gR)) : 0)
                          + (luma is null ? 0 : sharpen * land * FineDetail(luma, width, height, x, y));
                    L = Math.Clamp(L, 0, 1);
                    // No curve in the halo: its shoulder compresses the rays (measured −11 % fluctuation)
                    L -= curve * (1 - halo) * MathF.Sin(2 * MathF.PI * L) / (2 * MathF.PI);
                    var chroma = c0 * ratio;
                    ratio *= 1 + vibrance * land * (1 - Math.Min(1, chroma / VibranceFull));   // Not in the sky, nor in the halo

                    var (sR, sG, sB) = Oklab.ToSrgb(L, baseA * ratio, baseB * ratio);
                    result[i] = sB;
                    result[i + 1] = sG;
                    result[i + 2] = sR;
                    result[i + 3] = bgra[i + 3];
                    continue;
                }
                else
                {
                    // Add haze: a base veil plus more where it is already hazy or distant
                    var add = strength * (0.3f + 0.5f * (1 - tt));
                    oB = lin[0] + (airHere[0] - lin[0]) * add;
                    oG = lin[1] + (airHere[1] - lin[1]) * add;
                    oR = lin[2] + (airHere[2] - lin[2]) * add;
                }

                result[i] = Srgb.Encode(oB);
                result[i + 1] = Srgb.Encode(oG);
                result[i + 2] = Srgb.Encode(oR);
                result[i + 3] = bgra[i + 3];
            }
        });
        PixelPool.Return(luma);
        return result;
    }

    /// <summary>
    /// Fine structure at a pixel: luma (gamma-encoded) minus the 3×3 binomial mean, which is what a light unsharp mask
    /// amplifies. Read from the precomputed luma plane, so that each pixel reads nine single-channel values instead of
    /// nine neighbours with three channels each.
    /// </summary>
    private static float FineDetail(float[] luma, int w, int h, int x, int y)
    {
        float Y(int xx, int yy) => luma[Math.Clamp(yy, 0, h - 1) * w + Math.Clamp(xx, 0, w - 1)];
        var blur = (4 * Y(x, y)
                    + 2 * (Y(x - 1, y) + Y(x + 1, y) + Y(x, y - 1) + Y(x, y + 1))
                    + Y(x - 1, y - 1) + Y(x + 1, y - 1) + Y(x - 1, y + 1) + Y(x + 1, y + 1)) / 16;
        return Y(x, y) - blur;
    }

    /// <summary>
    /// Noise of a picture: standard deviation of the gamma-encoded luma, estimated with the 3 × 3 Laplacian mask of
    /// Immerkær (Computer Vision and Image Understanding 64(2), 1996): the mask cancels constant and linear content, the
    /// mean of its absolute response is √(π/2) · σ. Every third row is enough.
    /// </summary>
    internal static float EstimateNoise(float[] bgra, int w, int h)
    {
        if (w < 3 || h < 3) return 0;
        float Y(int x, int y) { var i = (y * w + x) * 4; return 0.0722f * bgra[i] + 0.7152f * bgra[i + 1] + 0.2126f * bgra[i + 2]; }
        double sum = 0; long n = 0;
        for (var y = 1; y < h - 1; y += 3)
        for (var x = 1; x < w - 1; x++)
        {
            var v = Y(x - 1, y - 1) - 2 * Y(x, y - 1) + Y(x + 1, y - 1)
                    - 2 * Y(x - 1, y) + 4 * Y(x, y) - 2 * Y(x + 1, y)
                    + Y(x - 1, y + 1) - 2 * Y(x, y + 1) + Y(x + 1, y + 1);
            sum += Math.Abs(v); n++;
        }
        return n == 0 ? 0 : (float)(Math.Sqrt(Math.PI / 2) / 6 * sum / n);
    }

    /// <summary>Luma (gamma-encoded, ITU-R BT.709 weights) per pixel for <see cref="FineDetail"/>; buffer from the pool.</summary>
    private static float[] LumaPlane(float[] bgra, int w, int h)
    {
        var luma = PixelPool.Rent(w * h);
        Parallel.For(0, h, ComputeLimits.Options, y =>
        {
            for (int x = 0, p = y * w; x < w; x++, p++)
                luma[p] = 0.0722f * bgra[p * 4] + 0.7152f * bgra[p * 4 + 1] + 0.2126f * bgra[p * 4 + 2];
        });
        return luma;
    }

    /// <summary>Recovers the scene, blended by the strength between the original and the full effect.</summary>
    private static float Recover(float v, float air, float t, float strength) =>
        v + ((v - air) / t + air - v) * strength;

    /// <summary>
    /// Position of a point in the grid of the small copy: the four neighbours and their weights (bilinear).
    /// Determined once per pixel and used for all maps; recomputing the position (clamping, rounding, indices) for each
    /// of the roughly 24 reads per pixel takes a large part of the dehaze time.
    /// </summary>
    private readonly struct Cell
    {
        private readonly int _i00, _i01, _i10, _i11;
        private readonly float _fx, _fy;

        public Cell(int w, int h, float x, float y)
        {
            x = Math.Clamp(x, 0, w - 1);
            y = Math.Clamp(y, 0, h - 1);
            int x0 = (int)x, y0 = (int)y;
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            (_fx, _fy) = (x - x0, y - y0);
            (_i00, _i01, _i10, _i11) = (y0 * w + x0, y0 * w + x1, y1 * w + x0, y1 * w + x1);
        }

        /// <summary>Channel <paramref name="c"/> of a plane with <paramref name="ch"/> channels.</summary>
        public float Of(float[] data, int c, int ch = 3)
        {
            var top = data[_i00 * ch + c] * (1 - _fx) + data[_i01 * ch + c] * _fx;
            var bottom = data[_i10 * ch + c] * (1 - _fx) + data[_i11 * ch + c] * _fx;
            return top * (1 - _fy) + bottom * _fy;
        }

        /// <summary>Linear model of the guided filter at this position: aᵀ·I + b.</summary>
        public float Sample((float[] A, float[] B) m, float gB, float gG, float gR) =>
            Of(m.A, 0) * gB + Of(m.A, 1) * gG + Of(m.A, 2) * gR + Of(m.B, 0, 1);
    }

    /// <summary>
    /// Tolerance: whatever closely resembles the air light (sky at the horizon, white walls) does not count as dense haze;
    /// otherwise colour casts and noise appear. In addition, the limits per channel.
    /// </summary>
    /// <param name="air">Air light (B, G, R); <paramref name="o"/> = start of the triple.</param>
    /// <param name="land">1 = landscape, 0 = sky. In the sky only the limits apply, not the pixel rules: the halo of the
    /// sun has the colour of the haze and would be spared while the sky next to it becomes bluer, giving a tinted ring
    /// around the sun. The sky has no edges, so the smooth map suffices.</param>
    private static float Tolerate(float t, float b, float g, float r, float[] air, int o, float land = 1)
    {
        float aB = air[o], aG = air[o + 1], aR = air[o + 2];
        var smooth = Math.Max(t, Math.Max(Keep(b, aB, 0), Math.Max(Keep(g, aG, 0), Keep(r, aR, 0))));
        if (land <= 0) return Math.Clamp(smooth, MinTransmission, 1);

        var dist = Math.Max(Math.Abs(b - aB), Math.Max(Math.Abs(g - aG), Math.Abs(r - aR)));
        if (dist < Tolerance) t = Math.Min(1, t * Tolerance / Math.Max(dist, 1e-4f));
        t = Math.Max(t, Math.Max(Keep(b, aB, 0), Math.Max(Keep(g, aG, 0), Keep(r, aR, 0))));

        // Clearly brighter than the air light of this direction, per channel: a light source (sun, its halo, a lamp),
        // not haze. Only from 5 % above: with one air light for the whole image and the limit just below it, the bright
        // haze under the sun would count as a light source, and exactly the densest haze would remain.
        var over = Math.Max(b / aB, Math.Max(g / aG, r / aR));
        t += (1 - t) * SmoothStep(1.05f, 1.2f, over);
        // Almost white (at the limit of the capture): sun, lights, specular highlights; never haze
        t += (1 - t) * SmoothStep(0.85f, 0.97f, Luma(b, g, r));
        return Math.Clamp(smooth + (t - smooth) * land, MinTransmission, 1);
    }

    /// <summary>Minimum share that each colour channel keeps with the amplified slider (above 100).</summary>
    private const float KeepShare = 0.3f;

    /// <summary>
    /// Smallest transmission at which a channel stays within the permitted range: below the air light it keeps at least
    /// <paramref name="share"/> of its value (0: not below black); above it, it does not exceed white. Without this
    /// limit a strong slider setting overshoots: channels are clipped, red and black bands appear at the horizon and
    /// colour fringes at the sun.
    /// </summary>
    private static float Keep(float v, float air, float share) => v < air
        ? (air - v) / Math.Max(air - share * v, 1e-4f)
        : air < 1 ? (v - air) / (1 - air) : 0;

    /// <summary>
    /// Slider above 100: the amplification approaches softly the limit at which, on average over the neighbourhood
    /// (window of the dark channel), each channel still keeps <see cref="KeepShare"/>; never below the effect at 100.
    /// </summary>
    /// <remarks>
    /// A limit per pixel would hold the dark spots while the brighter ones next to them are lowered further, and the
    /// contrast would decrease again. The maximum over the neighbourhood is never below the effect at 100 (the dark
    /// channel already brings the darkest pixel there to about 5 %), so the slider would hardly act above 100. A hard
    /// limit would make it stop at about 150.
    /// </remarks>
    private static void LimitBoost(float[] lin, int w, int h, float[] air, float[] t, float[] boosted)
    {
        var floor = new float[w * h];
        for (var p = 0; p < floor.Length; p++)
        {
            var o = p * 3;
            floor[p] = Math.Max(Keep(lin[p * 3], air[o], KeepShare),
                Math.Max(Keep(lin[p * 3 + 1], air[o + 1], KeepShare), Keep(lin[p * 3 + 2], air[o + 2], KeepShare)));
        }
        Blur.Gauss(floor, w, h, Math.Max(1.0, PatchPerEdge * Math.Max(w, h)));
        for (var p = 0; p < floor.Length; p++)
        {
            // Softly towards the limit: full at first, then ever slower; no step, no standstill
            var room = t[p] - floor[p];
            boosted[p] = room <= 0 ? t[p] : t[p] - room * (1 - MathF.Exp(-Math.Max(0, t[p] - boosted[p]) / room));
        }
    }

    /// <summary>
    /// Transmission t = 1 − ω · dark channel of the image normalised to the air light.
    /// </summary>
    /// <param name="air">Air light per pixel (B, G, R), same layout as <paramref name="lin"/>.</param>
    internal static float[] Transmission(float[] lin, int w, int h, float[] air, float omega)
    {
        var n = w * h;
        var minRgb = new float[n];
        for (var p = 0; p < n; p++)
        {
            var o = p * 3;
            minRgb[p] = Math.Min(lin[p * 3] / air[o], Math.Min(lin[p * 3 + 1] / air[o + 1], lin[p * 3 + 2] / air[o + 2]));
        }
        var dark = MinFilter(minRgb, w, h, Math.Max(1, (int)Math.Round(PatchPerEdge * Math.Max(w, h))));
        var t = new float[n];
        for (var p = 0; p < n; p++) t[p] = 1 - omega * dark[p];
        return t;
    }

    /// <summary>
    /// Air light (B, G, R, linear) for the whole image, chosen as in <see cref="AirLightOf"/>. At least 0.25 (linear), so
    /// that dark images without haze are not ruined.
    /// </summary>
    internal static float[] AirLight(float[] lin, int w, int h) =>
        AirLightOf(lin, DarkChannel(lin, w, h), Enumerable.Range(0, w * h).ToArray());

    /// <summary>Vertical strips for the air light per direction.</summary>
    private const int AirStrips = 8;

    /// <summary>
    /// Air light per column (B, G, R per column): as <see cref="AirLight"/>, but for each of <see cref="AirStrips"/>
    /// vertical strips, interpolated between the strip centres. The colour of the haze depends on the direction to the
    /// sun: bright and warm towards the sun, blue and darker away from it. Measured at the horizon of a panorama:
    /// 0.33 … 0.97 (linear); a single value from the area of the sun underestimates the haze away from it by half.
    /// </summary>
    /// <remarks>
    /// Strips without sky and haze (only trees: dark channel 0.01) are unsuitable; there the value for the whole image
    /// applies, with a soft transition. Each channel stays between 30 % and 125 % of the overall value.
    /// </remarks>
    internal static float[] AirLightColumns(float[] lin, int w, int h, float[]? sky = null)
    {
        var dark = DarkChannel(lin, w, h);
        // Only from the landscape, where it disappears into the haze, not from the sky: the sky is often brighter than
        // the haze in front of the landscape (overexposed, glow around the sun), and measured against such "white",
        // dense smog over a city appears thin (1–12 % detected).
        int[] Landscape(int[] pixels)
        {
            if (sky is null) return pixels;
            var land = pixels.Where(p => sky[p] < 0.5f).ToArray();
            return land.Length >= pixels.Length / 20 ? land : pixels;
        }

        var global = AirLightOf(lin, dark, Landscape(Enumerable.Range(0, w * h).ToArray()));
        var globalDark = Math.Min(global[0], Math.Min(global[1], global[2]));

        var strips = new float[AirStrips][];
        for (var k = 0; k < AirStrips; k++)
        {
            int x0 = k * w / AirStrips, x1 = Math.Max(x0 + 1, (k + 1) * w / AirStrips);
            var pixels = new int[(x1 - x0) * h];
            for (int y = 0, i = 0; y < h; y++)
                for (var x = x0; x < x1; x++) pixels[i++] = y * w + x;
            pixels = Landscape(pixels);
            var a = AirLightOf(lin, dark, pixels);

            // How reliable: dark channel of the candidates compared with the whole image
            var candidates = pixels.OrderByDescending(p => dark[p]).Take(Math.Max(1, pixels.Length / 100)).Average(p => dark[p]);
            var trust = SmoothStep(0.1f, 0.25f, (float)candidates / globalDark);
            for (var c = 0; c < 3; c++)
                a[c] = global[c] + (Math.Clamp(a[c], 0.3f * global[c], 1.25f * global[c]) - global[c]) * trust;
            strips[k] = a;
        }

        var air = new float[w * 3];
        for (var x = 0; x < w; x++)
        {
            var pos = Math.Clamp((x + 0.5f) / w * AirStrips - 0.5f, 0, AirStrips - 1);
            int k0 = (int)pos, k1 = Math.Min(k0 + 1, AirStrips - 1);
            for (var c = 0; c < 3; c++)
                air[x * 3 + c] = strips[k0][c] + (strips[k1][c] - strips[k0][c]) * (pos - k0);
        }
        return air;
    }


    /// <summary>Tiles for the air-light field (columns × rows).</summary>
    private const int AirCols = 6, AirRows = 4;

    /// <summary>
    /// Air light as a field (B, G, R per pixel of the small copy): as <see cref="AirLightColumns"/>, but per tile instead
    /// of per strip, bilinear between the tile centres. The colour of the veil shifts in different directions: yellowish
    /// white around the sun, bluish away from it, grey in the valley. One value per column would subtract the same colour
    /// everywhere: a ring around the sun, a pink distance.
    /// </summary>
    internal static float[] AirLightField(float[] lin, int w, int h, float[]? sky = null)
    {
        var dark = DarkChannel(lin, w, h);
        int[] Landscape(int[] pixels)
        {
            if (sky is null) return pixels;
            var land = pixels.Where(p => sky[p] < 0.5f).ToArray();
            return land.Length >= pixels.Length / 20 ? land : pixels;
        }
        // Deviate vertically only where the tile contains haze as dense as its column at the horizon: the air light is the
        // colour at infinite distance. A tile in the valley mainly sees scene colour, and its "air light" would be too
        // dark (measured: half the colour recovery).
        var columns = AirLightColumns(lin, w, h, sky);
        var columnDark = new float[AirCols];
        for (var tx = 0; tx < AirCols; tx++)
        {
            int x0 = tx * w / AirCols, x1 = Math.Max(x0 + 1, (tx + 1) * w / AirCols);
            var pixels = new int[(x1 - x0) * h];
            for (int y = 0, i = 0; y < h; y++)
                for (var x = x0; x < x1; x++) pixels[i++] = y * w + x;
            pixels = Landscape(pixels);
            columnDark[tx] = (float)pixels.OrderByDescending(p => dark[p]).Take(Math.Max(1, pixels.Length / 100)).Average(p => dark[p]);
        }

        var tiles = new float[AirCols * AirRows][];
        for (var ty = 0; ty < AirRows; ty++)
        for (var tx = 0; tx < AirCols; tx++)
        {
            int x0 = tx * w / AirCols, x1 = Math.Max(x0 + 1, (tx + 1) * w / AirCols);
            int y0 = ty * h / AirRows, y1 = Math.Max(y0 + 1, (ty + 1) * h / AirRows);
            var cx = Math.Clamp((x0 + x1) / 2, 0, w - 1) * 3;   // Centre of the tile (before widening)
            // Overlapping windows: half a tile in each direction for more candidates and a calmer estimate
            int ex = (x1 - x0) / 2, ey = (y1 - y0) / 2;
            (x0, x1, y0, y1) = (Math.Max(0, x0 - ex), Math.Min(w, x1 + ex), Math.Max(0, y0 - ey), Math.Min(h, y1 + ey));
            var pixels = new int[(x1 - x0) * (y1 - y0)];
            for (int y = y0, i = 0; y < y1; y++)
                for (var x = x0; x < x1; x++) pixels[i++] = y * w + x;
            pixels = Landscape(pixels);
            var a = AirLightOf(lin, dark, pixels);
            var candidates = pixels.OrderByDescending(p => dark[p]).Take(Math.Max(1, pixels.Length / 100)).Average(p => dark[p]);
            var trust = SmoothStep(0.7f, 0.95f, (float)candidates / Math.Max(1e-3f, columnDark[tx]));
            for (var c = 0; c < 3; c++)
            {
                var col = columns[cx + c];
                a[c] = col + (Math.Clamp(a[c], 0.5f * col, 1.25f * col) - col) * trust;
            }
            tiles[ty * AirCols + tx] = a;
        }

        var air = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            var py = Math.Clamp((y + 0.5f) / h * AirRows - 0.5f, 0, AirRows - 1);
            int r0 = (int)py, r1 = Math.Min(r0 + 1, AirRows - 1); var fy = py - r0;
            for (var x = 0; x < w; x++)
            {
                var px = Math.Clamp((x + 0.5f) / w * AirCols - 0.5f, 0, AirCols - 1);
                int c0 = (int)px, c1 = Math.Min(c0 + 1, AirCols - 1); var fx = px - c0;
                for (var c = 0; c < 3; c++)
                {
                    var top = tiles[r0 * AirCols + c0][c] * (1 - fx) + tiles[r0 * AirCols + c1][c] * fx;
                    var bottom = tiles[r1 * AirCols + c0][c] * (1 - fx) + tiles[r1 * AirCols + c1][c] * fx;
                    air[(y * w + x) * 3 + c] = top * (1 - fy) + bottom * fy;
                }
            }
        }
        return air;
    }

    /// <summary>Dark channel (smallest colour channel, minimum over the window), not normalised.</summary>
    private static float[] DarkChannel(float[] lin, int w, int h)
    {
        var minRgb = new float[w * h];
        for (var p = 0; p < minRgb.Length; p++) minRgb[p] = Math.Min(lin[p * 3], Math.Min(lin[p * 3 + 1], lin[p * 3 + 2]));
        return MinFilter(minRgb, w, h, Math.Max(1, (int)Math.Round(PatchPerEdge * Math.Max(w, h))));
    }

    /// <summary>
    /// Air light from a selection of pixels, following the dark channel prior (He, Sun, Tang 2009): the candidates are
    /// the 0.1 % with the highest dark channel (at least ten), and among them the brightest decide. Clipped pixels (a
    /// channel at 0.95 or above: sun, lamps, glare) are left out unless nothing else remains, and the brightest tenth of
    /// the candidates is averaged, so that a single pixel does not decide.
    /// </summary>
    private static float[] AirLightOf(float[] lin, float[] dark, int[] pixels)
    {
        var take = Math.Min(pixels.Length, Math.Max(10, pixels.Length / 1000));
        var top = pixels.OrderByDescending(p => dark[p]).Take(take).ToArray();
        var unclipped = top.Where(p => Math.Max(lin[p * 3], Math.Max(lin[p * 3 + 1], lin[p * 3 + 2])) < ClipLevel).ToArray();
        if (unclipped.Length > 0) top = unclipped;
        var brightest = top.OrderByDescending(p => Luma(lin[p * 3], lin[p * 3 + 1], lin[p * 3 + 2]))
            .Take(Math.Max(1, top.Length / 10)).ToArray();
        var air = new float[3];
        foreach (var p in brightest)
            for (var c = 0; c < 3; c++) air[c] += lin[p * 3 + c];
        for (var c = 0; c < 3; c++) air[c] = Math.Max(0.25f, air[c] / brightest.Length);
        return air;
    }

    /// <summary>Linear value from which a channel counts as clipped (no longer usable as the colour of the haze).</summary>
    private const float ClipLevel = 0.95f;

    /// <summary>
    /// Protects the sky: areas without structure that are not dark are sky, not haze in front of a landscape. Without
    /// protection the dark channel removes the sky as "haze": it shifts to turquoise or gets a violet cast next to the
    /// sun. A remainder of the effect stays (the sky becomes slightly bluer).
    /// </summary>
    /// <remarks>
    /// Structure is the difference of two copies smoothed to different degrees (band pass): even gradients (sky towards
    /// the sun) cancel out in it, meadows, trees and fields do not, not even in dense fog. Measured (relative to the
    /// mean): sky 0.002–0.005, landscape 0.04–0.38, dense test fog 0.03. Simple brightness variance is unsuitable: a sky
    /// with a gradient varies too, and fog suppresses the variance of the landscape, so dense fog would count as sky.
    /// The structure measure is computed in <see cref="SkyMap"/>; this method modifies <paramref name="t"/> in place.
    /// </remarks>
    /// <returns>The amplified transmission for the slider above 100; otherwise null.</returns>
    private static float[]? ProtectSky(float[] sky, float[] t, float boost)
    {
        var boosted = boost > 1 ? new float[t.Length] : null;
        for (var i = 0; i < t.Length; i++)
        {
            // Slider above 100: amplify the haze amount, only in the landscape. The amplification fades towards the sky
            // much faster than the protection takes effect; otherwise bands (blue, white-pink) and a dark arc form in the
            // transition between sky and horizon at 200–300.
            if (boosted != null)
            {
                var skyHere = Math.Min(1, sky[i]);   // Above 1 = halo, counts as sky
                var landscape = (1 - skyHere) * (1 - skyHere) * (1 - skyHere) * (1 - skyHere);
                var tb = Math.Max(0, 1 - (1 - t[i]) * (1 + (boost - 1) * landscape));
                boosted[i] = tb + (1 - tb) * SkyProtection * skyHere;
            }
            t[i] += (1 - t[i]) * SkyProtection * Math.Min(1, sky[i]);
        }
        return boosted;
    }

    /// <summary>Sky per pixel: bright and without structure (0 … 1), plus the halo of a light source added on top (up to
    /// 2); see <see cref="ProtectSky"/>.</summary>
    internal static float[] SkyMap(float[] lin, int w, int h)
    {
        var l = Luma(lin);
        var edge = Math.Max(w, h);
        var fine = (float[])l.Clone();
        var coarse = (float[])l.Clone();
        Blur.Gauss(fine, w, h, Math.Max(0.6, 0.002 * edge));
        Blur.Gauss(coarse, w, h, Math.Max(1.5, 0.006 * edge));
        var band = new float[l.Length];
        for (var i = 0; i < band.Length; i++) band[i] = Math.Abs(fine[i] - coarse[i]);
        var mean = (float[])l.Clone();
        var sigma = Math.Max(1.0, PatchPerEdge * edge);
        // The structure and the mean are smoothed with the colour of the picture as the guide, not isotropically: a plain
        // Gaussian smears the structure of a tree edge about 200 px (full size) into the sky next to it, which then loses
        // the protection and is dehazed in full – a saturated blue band along every silhouette. The guided filter gives
        // sky pixels next to the edge the values of the sky side (Fehlergedächtnis #129).
        Blur.Gauss(band, w, h, Math.Max(1.0, sigma / 4));
        Blur.Gauss(mean, w, h, Math.Max(1.0, sigma / 4));
        var guide = Encode(lin);
        band = GuidedColor.Apply(guide, GuidedColor.Coefficients(guide, band, w, h, sigma, eps: 1e-3f), w * h);
        mean = GuidedColor.Apply(guide, GuidedColor.Coefficients(guide, mean, w, h, sigma, eps: 1e-3f), w * h);
        for (var i = 0; i < band.Length; i++) { band[i] = Math.Max(0, band[i]); mean[i] = Math.Max(1e-3f, mean[i]); }

        // Light source with halo: whatever lies around clipped pixels (sun, lamp) is light, not haze. Otherwise the rays
        // count as structure and the halo as landscape: the pixel rules would double the chroma there (measured
        // 0.023 → 0.045), a pink ring.
        var hot = new float[l.Length];
        for (var i = 0; i < hot.Length; i++) hot[i] = l[i] > HotLuma ? 1 : 0;
        Blur.Gauss(hot, w, h, Math.Max(2.0, HaloPerEdge * edge));
        // Normalised to the peak, so that the halo reaches equally far (into the rays) at every image size; without
        // normalisation it would be almost empty for small images and too weak towards the outside, and the recovery
        // would brighten the rays. A single hot pixel does not count (gate).
        var peak = hot.Max();
        var haloScale = peak > 0.001f ? 1 / peak : 0;   // Gate: a clipped disc, not a single pixel

        var plain = new float[l.Length];
        for (var i = 0; i < plain.Length; i++)
        {
            var structure = band[i] / Math.Max(mean[i], 1e-3f);
            var smooth = 1 - SmoothStep(0.008f, 0.02f, structure);
            var bright = SmoothStep(0.03f, 0.1f, l[i]);   // Dark smooth shadows are not sky
            plain[i] = bright * smooth * SkyColour(lin[i * 3 + 2], lin[i * 3 + 1], lin[i * 3]);
        }
        DropIsolatedSky(plain, w, h);

        var sky = new float[l.Length];
        for (var i = 0; i < sky.Length; i++)
        {
            // Halo: near the sun and bright. The boundary follows the brightness of the sky, not a radius; a pure radius
            // gives a visibly brighter disc around the sun (unchanged inside, dehazed outside). This spares pixel by
            // pixel, like the rule "brighter than the air light" in Tolerate.
            var halo = SmoothStep(0.02f, 0.3f, hot[i] * haloScale) * SmoothStep(0.45f, 0.75f, l[i]);
            // Values above 1 mark the halo (sky = 1 + halo): fine sharpening stays at full strength there, because the
            // rays of the sun are structure that should be visible (treated as sky they appear soft, with normal
            // sharpening crisp); only hazy sky is spared.
            sky[i] = Math.Max(plain[i], halo) + halo;
        }
        return sky;
    }

    /// <summary>
    /// How far a colour can be sky at all (0 … 1): a sky is blue, grey, white or warm (yellow, orange, red, pink at
    /// sunset), never green – a smooth green meadow is not sky. Brown is deliberately not excluded: a brown field under
    /// haze and the warm, hazy sky just above the horizon have the same colour and similar brightness, and the rule
    /// would leave speckles in that sky.
    /// </summary>
    private static float SkyColour(float r, float g, float b)
    {
        var (_, a, ob) = Oklab.FromLinear(r, g, b);
        var chroma = MathF.Sqrt(a * a + ob * ob);
        var hue = Oklab.Hue(a, ob);   // degrees, 0 … 360
        var green = hue is > 95 and < 175 ? SmoothStep(0.025f, 0.06f, chroma) : 0;
        return 1 - green;
    }

    /// <summary>Smallest area of a sky region that touches neither the top edge nor the large sky, as a share of the
    /// picture.</summary>
    private const float IsolatedSkyShare = 0.01f;

    /// <summary>
    /// Removes small, isolated "sky" spots: a smooth, bright patch in a field is bright and without structure like sky,
    /// and as sky it would keep its haze (protected) while the landscape around it is cleared – a visible spot, a darker
    /// one once the brightness compensation acts. A region (connected above 0.05) stays sky if it touches the top edge or
    /// covers at least <see cref="IsolatedSkyShare"/> of the picture (a lake mirroring the sky, a large sky behind a tree);
    /// Fehlergedächtnis #128.
    /// </summary>
    private static void DropIsolatedSky(float[] plain, int w, int h)
    {
        var label = new int[plain.Length];
        var stack = new Stack<int>();
        var region = new List<int>();
        var minArea = (int)(IsolatedSkyShare * plain.Length);
        for (var start = 0; start < plain.Length; start++)
        {
            if (label[start] != 0 || plain[start] <= 0.05f) continue;
            region.Clear();
            var touchesTop = false;
            label[start] = 1;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                region.Add(p);
                int x = p % w, y = p / w;
                if (y == 0) touchesTop = true;
                if (x > 0) Visit(p - 1);
                if (x < w - 1) Visit(p + 1);
                if (y > 0) Visit(p - w);
                if (y < h - 1) Visit(p + w);
            }
            if (!touchesTop && region.Count < minArea)
                foreach (var p in region) plain[p] = 0;
        }

        void Visit(int q)
        {
            if (label[q] != 0 || plain[q] <= 0.05f) return;
            label[q] = 1;
            stack.Push(q);
        }
    }

    /// <summary>From this luma (linear) on, a pixel is a light source (clipped).</summary>
    private const float HotLuma = 0.95f;

    /// <summary>Reach of the halo around a light source (Gaussian sigma), relative to the longest edge.</summary>
    private const double HaloPerEdge = 0.05;

    /// <summary>Share by which the sky is protected (1 = not changed at all).</summary>
    private const float SkyProtection = 0.85f;

    /// <summary>
    /// Brightness compensation: the large-scale brightness of the result is matched to that of the original; local
    /// contrast and colour remain gained. Compared are the smoothed light quantities (linear luma) before and after,
    /// separately for the landscape and for the sky, each weighted by the sky map (normalised convolution): a plain blur
    /// would carry the brightening from the land across the horizon into the sky (bright band). Factor limited to
    /// 0.5 … 3.
    /// </summary>
    /// <returns>Coefficients in the form of <see cref="GuidedColor.Coefficients"/> with slope 0: only the smoothed
    /// log(factor), so that the application (CPU and GPU) stays the same.</returns>
    private static (float[] A, float[] B) BrightnessGain(float[] before, float[] after, float[] sky, int sw, int sh)
    {
        var n = sw * sh;
        // Weighted light quantities (linear luma) before and after, separately for landscape and sky
        var landBefore = new float[n]; var landAfter = new float[n]; var land = new float[n];
        var skyBefore = new float[n]; var skyAfter = new float[n]; var skyWeight = new float[n];
        Parallel.For(0, sh, ComputeLimits.Options, row =>
        {
            for (int x = 0, p = row * sw; x < sw; x++, p++)
            {
                var lb = Luma(before[p * 3], before[p * 3 + 1], before[p * 3 + 2]);
                var la = Luma(after[p * 3], after[p * 3 + 1], after[p * 3 + 2]);
                var wl = 1 - Math.Clamp(sky[p], 0, 1);
                (land[p], landBefore[p], landAfter[p]) = (wl, lb * wl, la * wl);
                (skyWeight[p], skyBefore[p], skyAfter[p]) = (1 - wl, lb * (1 - wl), la * (1 - wl));
            }
        });
        var sigma = Math.Max(1.0, BrightnessPerEdge * Math.Max(sw, sh));
        foreach (var plane in new[] { landBefore, landAfter, land, skyBefore, skyAfter, skyWeight }) Blur.Gauss(plane, sw, sh, sigma);
        var b = new float[n];
        Parallel.For(0, sh, ComputeLimits.Options, row =>
        {
            for (int x = 0, p = row * sw; x < sw; x++, p++)
            {
                var gl = MathF.Log(Math.Clamp(landBefore[p] / Math.Max(landAfter[p], 1e-6f), 0.5f, 3f));
                var gs = MathF.Log(Math.Clamp(skyBefore[p] / Math.Max(skyAfter[p], 1e-6f), 0.5f, 3f));
                // An estimate counts only where its side really occurs in the neighbourhood. A few pixels the sky map
                // takes partly for sky (a smooth spot in a field) would otherwise get the sky's gain, a dark spot in the
                // brightened landscape; the same for a few "land" pixels in the sky.
                gs = gl + (gs - gl) * SmoothStep(0.05f, 0.3f, skyWeight[p]);
                gl = gs + (gl - gs) * SmoothStep(0.05f, 0.3f, land[p]);
                var wl = 1 - Math.Clamp(sky[p], 0, 1);
                b[p] = wl * gl + (1 - wl) * gs;
            }
        });
        return (new float[n * 3], b);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float[] Luma(float[] lin)
    {
        var n = lin.Length / 3;
        var l = new float[n];
        for (var p = 0; p < n; p++) l[p] = 0.0722f * lin[p * 3] + 0.7152f * lin[p * 3 + 1] + 0.2126f * lin[p * 3 + 2];
        return l;
    }

    private static float[] Encode(float[] lin)
    {
        var g = new float[lin.Length];
        for (var i = 0; i < g.Length; i++) g[i] = Srgb.Encode(lin[i]);
        return g;
    }

    /// <summary>
    /// Downscales directly from the sRGB-encoded floating-point pixels (B, G, R, A): area mean over f × f blocks in linear
    /// light (physically correct). Result B, G, R per pixel, linear.
    /// </summary>
    private static (float[] Data, int W, int H) DownscaleLinear(float[] bgra, int w, int h, int f)
    {
        int sw = (w + f - 1) / f, sh = (h + f - 1) / f;
        var dst = new float[sw * sh * 3];
        Parallel.For(0, sh, ComputeLimits.Options, sy =>
        {
            for (var sx = 0; sx < sw; sx++)
            {
                float b = 0, g = 0, r = 0;
                var count = 0;
                for (var y = sy * f; y < Math.Min(h, sy * f + f); y++)
                for (var x = sx * f; x < Math.Min(w, sx * f + f); x++)
                {
                    var i = (y * w + x) * 4;
                    b += Srgb.Decode(bgra[i]); g += Srgb.Decode(bgra[i + 1]); r += Srgb.Decode(bgra[i + 2]);
                    count++;
                }
                var o = (sy * sw + sx) * 3;
                (dst[o], dst[o + 1], dst[o + 2]) = (b / count, g / count, r / count);
            }
        });
        return (dst, sw, sh);
    }

    private static float Luma(float b, float g, float r) => 0.0722f * b + 0.7152f * g + 0.2126f * r;

    /// <summary>Minimum over a square of radius r, separated into rows and columns, O(n).</summary>
    internal static float[] MinFilter(float[] src, int width, int height, int r)
    {
        var rows = new float[src.Length];
        Parallel.For(0, height, ComputeLimits.Options, y => SlidingMin(src, rows, y * width, 1, width, r));
        var dst = new float[src.Length];
        Parallel.For(0, width, ComputeLimits.Options, x => SlidingMin(rows, dst, x, width, height, r));
        return dst;
    }

    /// <summary>Sliding minimum over a row or column (monotonic queue).</summary>
    private static void SlidingMin(float[] src, float[] dst, int start, int stride, int length, int r)
    {
        var queue = new int[length];
        int head = 0, tail = 0;
        for (int i = 0, added = 0; i < length; i++)
        {
            // Window [i − r, i + r]: push in from the right
            for (; added <= Math.Min(length - 1, i + r); added++)
            {
                var v = src[start + added * stride];
                while (tail > head && src[start + queue[tail - 1] * stride] >= v) tail--;
                queue[tail++] = added;
            }
            while (queue[head] < i - r) head++;
            dst[start + i * stride] = src[start + queue[head] * stride];
        }
    }
}

/// <summary>sRGB ↔ linear (IEC 61966-2-1 transfer function) via lookup tables: one for 8-bit values, a fine one for
/// floating-point values, and one for the reverse direction with the exact formula at the steep low end.</summary>
internal static class Srgb
{
    private static readonly float[] ToLinearTable = Enumerable.Range(0, 256).Select(i =>
    {
        var v = i / 255.0;
        return (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
    }).ToArray();

    public static float ToLinear(byte v) => ToLinearTable[v];

    /// <summary>Table of <see cref="Decode"/>, for the GPU (Dehaze.Gpu).</summary>
    internal static float[] DecodeTableForGpu => DecodeTable;

    // For the floating-point chain (values between the 8-bit levels): fine table, linearly interpolated
    private const int DecodeSize = 4096;
    private static readonly float[] DecodeTable = Enumerable.Range(0, DecodeSize + 1).Select(i =>
    {
        var v = (double)i / DecodeSize;
        return (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
    }).ToArray();

    /// <summary>sRGB-encoded (0–1, floating point) → linear.</summary>
    public static float Decode(float v)
    {
        var x = Math.Clamp(v, 0, 1) * DecodeSize;
        var i = (int)x;
        if (i >= DecodeSize) return DecodeTable[DecodeSize];
        return DecodeTable[i] + (DecodeTable[i + 1] - DecodeTable[i]) * (x - i);
    }

    // Reverse direction as a table with linear interpolation, because panoramas have 80 MP
    private const int EncodeSize = 8192;
    private static readonly float[] EncodeTable = Enumerable.Range(0, EncodeSize + 1).Select(i =>
    {
        var v = (double)i / EncodeSize;
        return (float)(v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);
    }).ToArray();

    public static float Encode(float lin)
    {
        var x = Math.Clamp(lin, 0, 1) * EncodeSize;
        var i = (int)x;
        if (i >= EncodeSize) return EncodeTable[EncodeSize];
        // At the very bottom the curve is steep, so it is computed exactly there
        if (i < 8) return Math.Clamp(lin, 0, 1) <= 0.0031308f ? 12.92f * Math.Clamp(lin, 0, 1) : 1.055f * MathF.Pow(lin, 1 / 2.4f) - 0.055f;
        return EncodeTable[i] + (EncodeTable[i + 1] - EncodeTable[i]) * (x - i);
    }

    public static byte ToByte(float lin) => (byte)(Encode(lin) * 255 + 0.5f);
}

/// <summary>
/// Guided filter with a colour guide image (He, Sun, Tang 2010): per neighbourhood the linear model q = aᵀ·I + b with
/// I = (B, G, R). More edge-preserving than with a grey guide image: colour edges (green forest in front of blue sky of
/// the same brightness) are preserved. Returns the smoothed coefficients so that they can be applied to a larger copy
/// (Fast Guided Filter). Windows are Gaussian-weighted (<see cref="Blur.Gauss"/>) rather than box windows.
/// </summary>
internal static class GuidedColor
{
    /// <returns>a (3 channels per pixel) and b (1 channel).</returns>
    /// <summary>Applies coefficients on the same grid as the guide: q = aᵀ·I + b per pixel.</summary>
    /// <param name="guide">Guide image, B, G, R per pixel.</param>
    /// <param name="m">Coefficients from <see cref="Coefficients"/> for this guide.</param>
    /// <param name="n">Number of pixels.</param>
    /// <returns>The filtered values, one per pixel.</returns>
    public static float[] Apply(float[] guide, (float[] A, float[] B) m, int n)
    {
        var q = new float[n];
        Parallel.For(0, n, ComputeLimits.Options, p =>
            q[p] = m.A[3 * p] * guide[3 * p] + m.A[3 * p + 1] * guide[3 * p + 1] + m.A[3 * p + 2] * guide[3 * p + 2] + m.B[p]);
        return q;
    }

    public static (float[] A, float[] B) Coefficients(float[] guide, float[] input, int w, int h, double sigma, float eps)
    {
        var n = w * h;
        float[] Plane(Func<int, float> f)
        {
            var a = new float[n];
            Parallel.For(0, h, ComputeLimits.Options, row => { for (int x = 0, i = row * w; x < w; x++, i++) a[i] = f(i); });
            Blur.Gauss(a, w, h, sigma);
            return a;
        }

        var mB = Plane(i => guide[i * 3]);
        var mG = Plane(i => guide[i * 3 + 1]);
        var mR = Plane(i => guide[i * 3 + 2]);
        var mP = Plane(i => input[i]);
        var bb = Plane(i => guide[i * 3] * guide[i * 3]);
        var bg = Plane(i => guide[i * 3] * guide[i * 3 + 1]);
        var br = Plane(i => guide[i * 3] * guide[i * 3 + 2]);
        var gg = Plane(i => guide[i * 3 + 1] * guide[i * 3 + 1]);
        var gr = Plane(i => guide[i * 3 + 1] * guide[i * 3 + 2]);
        var rr = Plane(i => guide[i * 3 + 2] * guide[i * 3 + 2]);
        var pB = Plane(i => guide[i * 3] * input[i]);
        var pG = Plane(i => guide[i * 3 + 1] * input[i]);
        var pR = Plane(i => guide[i * 3 + 2] * input[i]);

        var a = new float[n * 3];
        var b = new float[n];
        Parallel.For(0, n, ComputeLimits.Options, i =>
        {
            // Covariance Σ (+ ε·E) and covariance with the input
            double s11 = bb[i] - mB[i] * mB[i] + eps, s12 = bg[i] - mB[i] * mG[i], s13 = br[i] - mB[i] * mR[i];
            double s22 = gg[i] - mG[i] * mG[i] + eps, s23 = gr[i] - mG[i] * mR[i];
            double s33 = rr[i] - mR[i] * mR[i] + eps;
            double c1 = pB[i] - mB[i] * mP[i], c2 = pG[i] - mG[i] * mP[i], c3 = pR[i] - mR[i] * mP[i];

            // a = Σ⁻¹ · c (symmetric 3×3 matrix, adjugate)
            double i11 = s22 * s33 - s23 * s23, i12 = s13 * s23 - s12 * s33, i13 = s12 * s23 - s13 * s22;
            double i22 = s11 * s33 - s13 * s13, i23 = s13 * s12 - s11 * s23, i33 = s11 * s22 - s12 * s12;
            var det = s11 * i11 + s12 * i12 + s13 * i13;
            if (Math.Abs(det) < 1e-12) det = 1e-12;
            var a1 = (i11 * c1 + i12 * c2 + i13 * c3) / det;
            var a2 = (i12 * c1 + i22 * c2 + i23 * c3) / det;
            var a3 = (i13 * c1 + i23 * c2 + i33 * c3) / det;
            a[i * 3] = (float)a1; a[i * 3 + 1] = (float)a2; a[i * 3 + 2] = (float)a3;
            b[i] = (float)(mP[i] - a1 * mB[i] - a2 * mG[i] - a3 * mR[i]);
        });

        // Smooth the coefficients (each pixel lies in many windows)
        var aB = new float[n]; var aG = new float[n]; var aR = new float[n];
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++) { aB[i] = a[i * 3]; aG[i] = a[i * 3 + 1]; aR[i] = a[i * 3 + 2]; }
        });
        foreach (var plane in new[] { aB, aG, aR, b }) Blur.Gauss(plane, w, h, sigma);
        Parallel.For(0, h, ComputeLimits.Options, row =>
        {
            for (int x = 0, i = row * w; x < w; x++, i++) { a[i * 3] = aB[i]; a[i * 3 + 1] = aG[i]; a[i * 3 + 2] = aR[i]; }
        });
        return (a, b);
    }
}
