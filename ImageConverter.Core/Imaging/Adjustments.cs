using ImageConverter.Core.Localization;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>Tinting of the whole image ("Tools" tile).</summary>
public enum Tint
{
    /// <summary>No tinting.</summary>
    None,
    /// <summary>Black and white.</summary>
    BlackWhite,
    /// <summary>Monochrome in the hue <see cref="Adjustments.TintHue"/>.</summary>
    Color,
}

/// <summary>
/// Light and colour settings from the "Tools" tile.
/// </summary>
/// <remarks>
/// For every slider 0 means unchanged. Values outside the stated ranges are clamped when applied.
/// </remarks>
/// <param name="Brightness">Brightness −100 … +100 (gamma on the midtones).</param>
/// <param name="Contrast">Contrast −100 … +100 (S-curve around the midpoint).</param>
/// <param name="Shadows">Shadows −100 … +100 (local, see <see cref="Adjust"/>).</param>
/// <param name="Highlights">Highlights −100 … +100 (local, see <see cref="Adjust"/>).</param>
/// <param name="Saturation">Saturation −100 … +100.</param>
/// <param name="Tint">Tinting mode.</param>
/// <param name="Vibrance">Vibrance −100 … +100.</param>
/// <param name="TintHue">Hue for <see cref="Imaging.Tint.Color"/> in degrees 0–360; only relevant with that mode.</param>
/// <param name="Dehaze">Dehaze −100 … +300; negative values add haze.</param>
/// <param name="MatchLuminance">"Match colour", luminance −100 … +200 (factor 1–300 %).</param>
/// <param name="Neutralize">"Match colour", neutralise colour cast 0–100 %.</param>
/// <param name="Warmth">"Match colour", colour −100 (cool) … +100 (warm).</param>
/// <param name="DehazePasses">Dehaze fine-tuning: number of passes (1 … 10).</param>
/// <param name="DehazeClarity">Dehaze fine-tuning: clarity share in percent.</param>
/// <param name="DehazeContrast">Dehaze fine-tuning: contrast (curve) share in percent.</param>
/// <param name="DehazeVibrance">Dehaze fine-tuning: vibrance share in percent.</param>
/// <param name="DehazeSharpen">Dehaze fine-tuning: fine sharpening share in percent.</param>
/// <param name="DehazeEdge">Dehaze fine-tuning: edge sharpening 0–100 %.</param>
/// <param name="DehazeBrightness">Dehaze fine-tuning: brightness compensation in percent.</param>
/// <param name="DehazeDeband">Dehaze fine-tuning: debanding 0–100 %.</param>
/// <param name="DehazeCast">Dehaze fine-tuning: removal of the haze colour cast 0–100 %.</param>
/// <param name="WbNeutral">White balance eyedropper for neutral grey (averaged measurements); null = not used.</param>
/// <param name="WbBlack">White balance eyedropper for black; null = not used.</param>
/// <param name="WbWhite">White balance eyedropper for white; null = not used.</param>
/// <param name="MatchLimit">"Match colour", luminance threshold (percent brightness of the regions, 5–100): brighter
/// regions stay unchanged; 100 = no threshold. Only when brightening.</param>
/// <param name="MatchTarget">"Match colour", luminance target (percent brightness, 5–100): dark regions are lifted only
/// up to this value (per region, at most by the luminance factor); 0 = off, everything in the same
/// ratio.</param>
/// <param name="MatchGlobal">"Match colour", luminance "Overall" −100 … +100: makes the whole image brighter or darker
/// once more (1–200 %), without masks, after the region luminance. The region luminance evens out regions
/// but does not make the image brighter as a whole.</param>
/// <param name="MaskAmount">Luminosity mask (own group): strength 0–200 (% more
/// light in the darkest tones).</param>
/// <param name="MaskEnd">Luminosity mask: fade-out point (percent brightness, 5–100); nothing above it.</param>
/// <param name="MaskCurve">Luminosity mask: gradient 0–100 (soft … steep), see <see cref="LumMask.Power"/>.</param>
/// <param name="MaskPreview">Preview only: shows the mask as greyscale instead of the image; never used for export.</param>
public sealed record Adjustments(
    double Brightness = 0,
    double Contrast = 0,
    double Shadows = 0,
    double Highlights = 0,
    double Saturation = 0,
    Tint Tint = Tint.None,
    double Vibrance = 0,
    double TintHue = Adjustments.SepiaHue,
    double Dehaze = 0,
    double MatchLuminance = 0,
    double Neutralize = 0,
    double Warmth = 0,
    // Dehaze fine-tuning: passes and the perception shares in percent.
    int DehazePasses = Imaging.Dehaze.DefaultPasses,
    double DehazeClarity = Imaging.Dehaze.DefaultClarity,
    double DehazeContrast = Imaging.Dehaze.DefaultCurve,
    double DehazeVibrance = Imaging.Dehaze.DefaultVibrance,
    double DehazeSharpen = Imaging.Dehaze.DefaultSharpen,
    double DehazeEdge = 0,
    double DehazeBrightness = Imaging.Dehaze.DefaultBrightness,
    double DehazeDeband = Imaging.Dehaze.DefaultDeband,
    double DehazeCast = Imaging.Dehaze.DefaultCast,
    // White balance with eyedroppers ("White balance" group): averaged measurements; null = eyedropper not used.
    WbSample? WbNeutral = null,
    WbSample? WbBlack = null,
    WbSample? WbWhite = null,
    // "Match colour", luminance: threshold, target and "Overall" (see the parameter documentation above). Luminance
    // decides by brightness only, not by distance.
    double MatchLimit = 100,
    double MatchTarget = 0,
    double MatchGlobal = 0,
    // Luminosity mask; MaskPreview is for the preview only and never applies to the export.
    double MaskAmount = 0,
    double MaskEnd = 75,
    double MaskCurve = 30,
    bool MaskPreview = false)
{
    /// <summary>Default hue for tinting: warm brown like an old photographic print.</summary>
    public const double SepiaHue = 30;

    /// <summary>No adjustments.</summary>
    public static readonly Adjustments None = new();

    /// <summary>True if there is nothing to do. The hue counts only when tinting is active, the dehaze fine-tuning only
    /// with dehaze, and threshold, target and mask settings only when they have an effect.</summary>
    public bool IsNone => Normalized == None;

    /// <summary>The settings with all values that currently have no effect reset to their defaults (hue without tinting,
    /// dehaze fine-tuning without dehaze, threshold/target without luminance, mask settings without mask).</summary>
    private Adjustments Normalized
    {
        get
        {
            var n = Tint == Tint.Color ? this : this with { TintHue = SepiaHue };
            if (MatchLuminance <= 0 || MatchLimit >= 100) n = n with { MatchLimit = 100 };   // only has an effect when brightening
            if (MatchLuminance <= 0 || MatchTarget <= 0) n = n with { MatchTarget = 0 };
            if (MaskAmount <= 0 && !MaskPreview) n = n with { MaskAmount = 0, MaskEnd = None.MaskEnd, MaskCurve = None.MaskCurve };
            return Dehaze == 0 ? n with { DehazePasses = None.DehazePasses, DehazeClarity = None.DehazeClarity, DehazeContrast = None.DehazeContrast,
                DehazeVibrance = None.DehazeVibrance, DehazeSharpen = None.DehazeSharpen, DehazeEdge = 0, DehazeBrightness = None.DehazeBrightness, DehazeDeband = None.DehazeDeband, DehazeCast = None.DehazeCast } : n;
        }
    }

    /// <summary>Only the parts that <see cref="Adjust.Prepare"/> depends on; used as a cache key for the preview.</summary>
    public Adjustments PrepareKey => None with
    {
        Shadows = Shadows, Highlights = Highlights, Dehaze = Normalized.Dehaze,
        DehazePasses = Normalized.DehazePasses, DehazeClarity = Normalized.DehazeClarity, DehazeContrast = Normalized.DehazeContrast,
        DehazeVibrance = Normalized.DehazeVibrance, DehazeSharpen = Normalized.DehazeSharpen, DehazeEdge = Normalized.DehazeEdge,
        DehazeBrightness = Normalized.DehazeBrightness, DehazeDeband = Normalized.DehazeDeband, DehazeCast = Normalized.DehazeCast,
        WbNeutral = WbNeutral, WbBlack = WbBlack, WbWhite = WbWhite,
        // Only whether the region maps are needed, not the slider values; those take effect only in Apply (fast).
        MatchLimit = NeedsRegions ? 0 : 100,
    };

    /// <summary>
    /// Only the parts that <see cref="Adjust.Dehazed"/> depends on (white balance and dehaze); the cache key of the most
    /// expensive stage. Shadows/highlights and the region maps come afterwards and do not require dehaze to be recomputed.
    /// </summary>
    public Adjustments DehazeKey => PrepareKey with { Shadows = 0, Highlights = 0, MatchLimit = 100 };

    /// <summary>True if luminance (threshold or target) or the luminosity mask needs the region maps
    /// (<see cref="LuminanceRegions"/>).</summary>
    public bool NeedsRegions => Normalized is var n
        && ((n.MatchLuminance > 0 && (n.MatchLimit < 100 || n.MatchTarget > 0)) || n.MaskAmount > 0 || n.MaskPreview);

    /// <summary>True if the luminosity mask has an effect or is shown.</summary>
    public bool HasLumMask => Normalized is { } n && (n.MaskAmount > 0 || n.MaskPreview);

    /// <summary>
    /// True for strong tonal editing that spreads smooth gradients, where 16 bits per channel are worthwhile
    /// (recommendation when saving).
    /// </summary>
    /// <remarks>
    /// Thresholds: dehaze from 20, contrast, shadows or highlights from ±25, brightness from ±30, luminance or luminosity
    /// mask from 20, luminance "Overall" from ±25. Below that, 8 bits with dither are sufficient and the file stays half
    /// the size.
    /// </remarks>
    public bool SpreadsTones => Normalized is var n
        && (n.Dehaze >= 20 || Math.Abs(n.Contrast) >= 25 || Math.Abs(n.Shadows) >= 25 || Math.Abs(n.Highlights) >= 25
            || Math.Abs(n.Brightness) >= 30 || n.MatchLuminance >= 20 || n.MaskAmount >= 20 || Math.Abs(n.MatchGlobal) >= 25);

    /// <summary>True if a white balance is computed.</summary>
    public bool HasWhiteBalance => WhiteBalance.IsActive(WbNeutral, WbBlack, WbWhite);
}

/// <summary>
/// Applies <see cref="Adjustments"/> to pixel buffers. Like <see cref="Sharpen"/>, preview and export use the same
/// function, so that the export matches what was shown.
/// </summary>
/// <remarks>
/// Order: white balance (eyedroppers) → dehaze → shadows/highlights → "Match colour" (neutralise, colour, luminance,
/// overall) → luminosity mask → brightness and contrast → vibrance → saturation → tint.
/// Shadows and highlights act locally: whether an area counts as dark or bright is decided by its
/// surroundings at every scale (local Laplacian filter), not by the single pixel. A dark detail in a
/// bright area therefore stays dark, and the image does not become flat.
/// </remarks>
public static class Adjust
{
    /// <summary>Wide surroundings relative to the longest edge: an area counts as dark (or bright) only if these
    /// surroundings are as well. Small dark spots in bright areas (pupils, nostrils, strands of hair in a face) therefore
    /// stay unchanged. Relative, so that it has the same effect in the small preview and in the export.</summary>
    private const double WideNeighbourhoodPerEdge = 0.03;

    /// <summary>From this brightness difference to the surroundings onwards, a transition counts as an edge across which
    /// shadows/highlights do not act.</summary>
    private const float EdgeContrast = 0.15f;

    // Luminance weights (ITU-R BT.709) applied to the sRGB values.
    private const float Kr = 0.2126f, Kg = 0.7152f, Kb = 0.0722f;

    /// <summary>8-bit entry point (tests, simple callers): computes in 32 bits and rounds with dither at the end.</summary>
    /// <param name="bgra">Pixels as B, G, R, A bytes; not modified.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="a">Adjustments to apply.</param>
    /// <param name="plan">Progress and cancellation; null = none.</param>
    /// <returns>A new byte array with the result.</returns>
    public static byte[] Apply(byte[] bgra, int width, int height, Adjustments a, WorkPlan? plan = null) =>
        Pixels.ToBytes(Apply(Pixels.FromBytes(bgra), width, height, a, plan), dither: true);

    /// <summary>Applies the adjustments: <see cref="Prepare"/> followed by
    /// <see cref="Apply(Prepared, int, int, Adjustments, WorkPlan?)"/>.</summary>
    /// <param name="px">Pixel buffer (B, G, R, A, sRGB-encoded 0–1, see <see cref="Pixels"/>); not modified.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="a">Adjustments to apply.</param>
    /// <param name="plan">Progress and cancellation; null = none.</param>
    /// <returns>A new buffer from the <see cref="PixelPool"/>.</returns>
    public static float[] Apply(float[] px, int width, int height, Adjustments a, WorkPlan? plan = null)
    {
        var prepared = Prepare(px, width, height, a, plan);
        var result = Apply(prepared, width, height, a, plan);
        if (!ReferenceEquals(prepared.Source, px)) PixelPool.Return(prepared.Source);   // intermediate result owned by this method
        return result;
    }

    /// <summary>
    /// Registers the steps of <see cref="Prepare"/> and <see cref="Apply(Prepared, int, int, Adjustments, WorkPlan?)"/> in
    /// the progress plan, in the same order in which they start there.
    /// </summary>
    /// <param name="a">Adjustments that will be applied.</param>
    /// <param name="plan">Plan to add the steps to.</param>
    /// <param name="megapixels">Image size in megapixels, for the estimated effort.</param>
    /// <param name="withDehaze">False if the dehazed image is already available (cache) and only the rest is
    /// computed.</param>
    public static void PlanSteps(Adjustments a, WorkPlan plan, double megapixels, bool withDehaze = true)
    {
        if (a.IsNone) return;
        var dehaze = withDehaze ? Math.Clamp(a.Dehaze / 100, -1, 3) : 0;
        if (dehaze < 0)
            plan.Add(Tr.T("Dunst hinzufügen"), WorkPlan.DehazePassCost * megapixels);
        else if (dehaze > 0)
        {
            var passes = Math.Clamp(a.DehazePasses, 1, Dehaze.MaxPasses);
            for (var i = 1; i <= passes; i++)
                plan.Add(Tr.T("Dunst entfernen – Durchlauf {0} von {1}", i, passes), WorkPlan.DehazePassCost * megapixels);
        }
        if (a.Shadows != 0 || a.Highlights != 0)
            plan.Add(Tr.T("Tiefen und Lichter"), WorkPlan.ToneMapCost * megapixels);
        plan.Add(Tr.T("Licht und Farbe"), WorkPlan.ToolsCost * megapixels);
    }

    /// <summary>Result of the expensive part of the adjustments.</summary>
    /// <param name="Source">Image after white balance and dehaze.</param>
    /// <param name="Toned">Target brightness from shadows/highlights; null if both are 0.</param>
    /// <param name="Regions">Region maps for luminance with threshold or target and for the luminosity mask; null = not
    /// needed.</param>
    public sealed record Prepared(float[] Source, float[]? Toned, LuminanceRegions? Regions = null);

    /// <summary>
    /// Computes the expensive part: white balance, dehaze and shadows/highlights via the local Laplacian filter.
    /// </summary>
    /// <param name="px">Pixel buffer (B, G, R, A, sRGB-encoded 0–1); not modified.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="a">Adjustments; only the parts in <see cref="Adjustments.PrepareKey"/> are used.</param>
    /// <param name="plan">Progress and cancellation; null = none.</param>
    /// <returns>The intermediate result for <see cref="Apply(Prepared, int, int, Adjustments, WorkPlan?)"/>.</returns>
    /// <remarks>
    /// Depends only on the image, white balance, dehaze, shadows and highlights (and on whether region maps are needed);
    /// the preview keeps the result so that the other sliders stay responsive. Two stages: <see cref="Dehazed"/>, then
    /// <see cref="Finish"/>.
    /// </remarks>
    public static Prepared Prepare(float[] px, int width, int height, Adjustments a, WorkPlan? plan = null) =>
        Finish(Dehazed(px, width, height, a, plan), width, height, a, plan);

    /// <summary>
    /// Second stage of <see cref="Prepare"/>: shadows/highlights and the region maps on the result of
    /// <see cref="Dehazed"/>.
    /// </summary>
    /// <param name="dehazed">Result of <see cref="Dehazed"/>; it becomes <see cref="Prepared.Source"/>.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="a">Adjustments.</param>
    /// <param name="plan">Progress and cancellation; null = none.</param>
    /// <returns>The intermediate result.</returns>
    /// <remarks>
    /// Depends on dehaze only through this image; callers that keep it (preview, size estimate) recompute only this stage
    /// while shadows or highlights are being dragged.
    /// </remarks>
    public static Prepared Finish(float[] dehazed, int width, int height, Adjustments a, WorkPlan? plan = null)
    {
        if (a.Shadows != 0 || a.Highlights != 0) plan?.Next();
        return new Prepared(dehazed, ToneMap(dehazed, width, height, a),
            a.NeedsRegions ? LuminanceRegions.Compute(dehazed, width, height) : null);
    }

    /// <summary>
    /// First stage of <see cref="Prepare"/>: white balance and dehaze, the most expensive part. Depends only on
    /// <see cref="Adjustments.DehazeKey"/>.
    /// </summary>
    /// <param name="px">Pixel buffer (B, G, R, A, sRGB-encoded 0–1); not modified.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="a">Adjustments.</param>
    /// <param name="plan">Progress and cancellation; null = none.</param>
    /// <param name="workEdge">Edge of the small copy on which the haze is estimated; 0 = default (1024). Smaller only for
    /// the coarse preview while dragging: a quarter of the work, with a slightly different result.</param>
    /// <returns><paramref name="px"/> itself if neither white balance nor dehaze is active; otherwise a new buffer.</returns>
    public static float[] Dehazed(float[] px, int width, int height, Adjustments a, WorkPlan? plan = null, int workEdge = 0)
    {
        var dehaze = (float)Math.Clamp(a.Dehaze / 100, -1, 3);   // −100 … +300
        var source = px;

        // White balance first: it acts on the same image on which the eyedroppers measured, and everything after it
        // (dehaze, light, colour) already works with the correct colours.
        if (a.HasWhiteBalance)
            source = WhiteBalance.Apply(px, width, height, a.WbNeutral, a.WbBlack, a.WbWhite);
        if (dehaze != 0)
        {
            // Passes like the eye: remove haze, take the result as the new input and estimate again. Where the haze is
            // gone, the estimate finds nothing more, while the distance keeps getting clearer. Air light and sky come
            // from the first pass; the sky stays unchanged afterwards (otherwise it becomes grainy).
            //
            // Everything in 32-bit floating point, rounded once at the end (with dither): with 8 bits between the steps,
            // every pass loses intermediate levels and the contrast pulls the remaining ones apart, giving banding in
            // the sky with 3 passes.
            //
            // Order: first deband the source (the 8-bit levels of the JPEG become intermediate values before the
            // contrast spreads them), then per pass dehaze → contrast/vibrance → deband → sharpening. Sharpening comes
            // last because before debanding it would emphasise the band edges.
            var settings = new Dehaze.Settings(
                (float)(a.DehazeClarity / 100), (float)(a.DehazeSharpen / 100), (float)(a.DehazeContrast / 100),
                (float)(a.DehazeVibrance / 100), (float)(a.DehazeBrightness / 100), (float)Math.Clamp(a.DehazeCast / 100, 0, 1));
            var pass = new Dehaze.Pass();
            var deband = (float)Math.Clamp(a.DehazeDeband / 100, 0, 1);
            // Intermediate results come from the pool and go back as soon as the next one exists, but never the input
            // (it belongs to the caller; in the preview it lives in the cache).
            float[] Replace(float[] old, float[] next)
            {
                if (!ReferenceEquals(old, next) && !ReferenceEquals(old, px)) PixelPool.Return(old);
                return next;
            }
            var work = Deband.Apply(source, width, height, deband * Dehaze.SourceDebandShare, seed: 0);
            // The white balance result (owned by this method) is no longer read from here on; return it now, otherwise
            // it stays in memory until the next full garbage collection (450 MB for the panorama).
            if (!ReferenceEquals(work, source) && !ReferenceEquals(source, px)) PixelPool.Return(source);
            plan?.Next();   // debanding the source counts towards the first step
            if (dehaze < 0)
            {
                work = Replace(work, Dehaze.Apply(work, width, height, dehaze, pass, settings, 1f, workEdge));   // add haze: one step
            }
            else
            {
                // A gentle step per pass: remove haze, then contrast, vibrance and sharpening on the result; the next
                // pass estimates the remainder anew. More passes build on this. With the full strength in every pass,
                // two passes at dehaze 60 are already the limit and vibrance becomes far too strong.
                var passes = Math.Clamp(a.DehazePasses, 1, Dehaze.MaxPasses);
                var t = dehaze;   // 0 … 3: slider value / 100
                // Physics and look are separate: the dehaze filter computes only the model (air light, transmission,
                // recovery, brightness compensation, colour); the look follows in one place, with one mechanism per
                // fine slider, and the scene map protects the sky and the halo of light sources.
                var physics = settings with { Clarity = 0, Sharpen = 0, Curve = 0, Vibrance = 0 };
                var look = new Dehaze.LookSettings(
                    Clarity: (float)(a.DehazeClarity / 100 * Dehaze.LookClarity * t),
                    Curve: (float)(a.DehazeContrast / 100 * Dehaze.LookCurve * t),
                    Vibrance: (float)(a.DehazeVibrance / 100 * Dehaze.LookVibrance * t),
                    VibranceSky: (float)(a.DehazeVibrance / 100 * Dehaze.LookVibranceSky * t),
                    Sharpen: (float)(a.DehazeSharpen / 100 * Dehaze.LookSharpen * t),
                    Edge: (float)(Math.Clamp(a.DehazeEdge / 100, 0, 1) * Dehaze.LoopEdgeShare * t));
                for (var i = 0; i < passes; i++)
                {
                    if (i > 0) plan?.Next();
                    work = Replace(work, Dehaze.Apply(work, width, height, (float)(Dehaze.PassAmount * t), pass, physics, 1f, workEdge));
                    Dehaze.LookTone(work, width, height, pass.Scene, look, workEdge);
                    work = Replace(work, Deband.Apply(work, width, height, deband, seed: (uint)i + 1));
                    Dehaze.LookDetail(work, width, height, pass.Scene, look);
                }
            }
            source = work;   // stays in 32 bits; rounded only for display or file
        }
        return source;
    }

    /// <summary>Reads the curve with linear interpolation between its nodes, without rounding to a level.</summary>
    private static float Lookup(float[] curve, float v)
    {
        var x = Math.Clamp(v, 0, 1) * (CurveSize - 1);
        var i = (int)x;
        if (i >= CurveSize - 1) return curve[CurveSize - 1];
        return curve[i] + (curve[i + 1] - curve[i]) * (x - i);
    }

    private static float[]? ToneMap(float[] bgra, int width, int height, Adjustments a)
    {
        var shadows = (float)Math.Clamp(a.Shadows / 100, -1, 1);
        var highlights = (float)Math.Clamp(a.Highlights / 100, -1, 1);
        return shadows != 0 || highlights != 0
            ? ShadowsHighlights(bgra, width, height, shadows, highlights)
            : null;
    }

    /// <summary>Applies the fast part of the adjustments to a prepared image.</summary>
    /// <param name="prepared">Result of <see cref="Prepare"/> for the same image.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="a">Adjustments to apply.</param>
    /// <param name="plan">Progress and cancellation; null = none.</param>
    /// <returns>A new buffer from the <see cref="PixelPool"/>; <paramref name="prepared"/> is not modified.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size of <paramref name="prepared"/> does not match the
    /// dimensions.</exception>
    public static float[] Apply(Prepared prepared, int width, int height, Adjustments a, WorkPlan? plan = null)
    {
        var (bgra, toned, regions) = (prepared.Source, prepared.Toned, prepared.Regions);
        ArgumentOutOfRangeException.ThrowIfNotEqual(bgra.Length, width * height * 4, nameof(prepared));

        var result = PixelPool.Rent(bgra.Length);
        Array.Copy(bgra, result, bgra.Length);
        if (a.IsNone) return result;
        plan?.Next();

        var saturation = (float)Math.Clamp(a.Saturation / 100, -1, 1);
        var vibrance = (float)Math.Clamp(a.Vibrance / 100, -1, 1);
        var curve = ToneCurve(a.Brightness / 100, a.Contrast / 100);
        // 1–200 %, extended up to 300 %: useful with a target value, where the factor is only the upper
        // limit for deep shadows. Brightening the image as a whole is the job of "Overall" (MatchGlobal).
        var luminance = Math.Max(0.01f, 1 + (float)Math.Clamp(a.MatchLuminance / 100, -1, 2));
        var limit = (float)Math.Clamp(a.MatchLimit / 100, 0.05, 1);
        var target = a.MatchTarget > 0 ? (float)Math.Clamp(a.MatchTarget / 100, 0.05, 1) : 0f;
        if (luminance <= 1 || !a.NeedsRegions) regions = null;   // threshold and target only when brightening
        // "Match colour", neutralise (0–100 %) and colour (cool − … + warm): as a shift in Oklab.
        var neutralize = (float)Math.Clamp(a.Neutralize / 100, 0, 1);
        var (castA, castB) = neutralize > 0 ? ColourCast(bgra, width, height) : (0f, 0f);
        var warmth = (float)Math.Clamp(a.Warmth / 100, -1, 1);
        var shiftA = -castA * neutralize + warmth * WarmthA;
        var shiftB = -castB * neutralize + warmth * WarmthB;
        // Order: first white balance and exposure ("Match colour"), then the contrast curve, then
        // vibrance/saturation, so that a colour cast is gone before contrast and chroma amplify it.
        var global = Math.Max(0.01f, 1 + (float)Math.Clamp(a.MatchGlobal / 100, -1, 1));
        var balance = luminance != 1 || global != 1 || shiftA != 0 || shiftB != 0;
        // Luminosity mask: after "Match colour", pixel-accurate; the region map contributes 30 % structure.
        var lumMask = a.HasLumMask;
        var areaMap = lumMask ? prepared.Regions : null;
        var maskGain = 1 + (float)Math.Clamp(a.MaskAmount / 100, 0, 2);
        var maskEnd = (float)Math.Clamp(a.MaskEnd / 100, 0.05, 1);
        var maskPower = LumMask.Power(a.MaskCurve);
        var chroma = vibrance != 0 || saturation != 0;

        // Pixel after shadows/highlights (target brightness from Prepare, hue kept).
        (float R, float G, float B) Toned(int p)
        {
            var i = p * 4;
            float b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            if (toned is null) return (r, g, b);
            var lum = Kr * r + Kg * g + Kb * b;
            var target = Math.Clamp(toned[p], 0, 1);
            if (lum <= 1f / 512) return (target, target, target);   // almost black: no hue to preserve
            var f = Math.Min(target / lum, 8f);                       // keep the hue: all channels in the same ratio
            return (r * f, g * f, b * f);
        }

        // Luminance with threshold or target: original and brightened version blended per scale (Laplacian pyramid,
        // like exposure fusion after Mertens), coarse brightness over large areas, edges over a
        // few pixels. Blending per pixel leaves a bright fringe at soft mask edges.
        var lifted = regions is not null ? LiftByRegions(Toned, width, height, luminance, regions, limit, target) : null;

        Parallel.For(0, height, ComputeLimits.Options, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var i = p * 4;
                var (r, g, b) = Toned(p);

                if (balance)
                {
                    // "Match colour" in Oklab (before the contrast curve, like the white balance).
                    var (L, oa, ob) = Oklab.FromSrgb(r, g, b);
                    if (shiftA != 0 || shiftB != 0)
                    {
                        // Neutralise + colour. Very dark tones stay neutral, otherwise black would become coloured.
                        var w = Math.Min(1, L / 0.3f);
                        oa += shiftA * w;
                        ob += shiftB * w;
                    }
                    var factor = 1f;
                    if (luminance != 1 && L > 1e-4f)
                    {
                        // Luminance: like more light, lightness and chroma in the same ratio (in Oklab exactly a
                        // stronger illumination). Raising only the lightness makes the colours look pale.
                        var target = lifted is not null ? Math.Clamp(lifted[p], 0, 1) : Shoulder(L * luminance);
                        factor = target / L;
                        L = target;
                    }
                    if (global != 1 && L > 1e-4f)
                    {
                        // "Overall": applied to the result of the region luminance, the same for the whole image.
                        var all = Shoulder(L * global);
                        factor *= all / L;
                        L = all;
                    }
                    (r, g, b) = Oklab.ScaleChroma(L, oa, ob, factor);
                }

                if (lumMask)
                {
                    var (L, oa, ob) = Oklab.FromSrgb(r, g, b);
                    var m = LumMask.Weight(L, areaMap?.AreaBrightness(x, y) ?? L, maskEnd, maskPower);
                    if (a.MaskPreview)
                    {
                        // Show the mask: white = receives everything, black = nothing.
                        result[i] = result[i + 1] = result[i + 2] = m;
                        continue;
                    }
                    if (m > 0 && L > 1e-4f)
                    {
                        var lifted = L + (Shoulder(L * maskGain) - L) * m;
                        (r, g, b) = Oklab.ScaleChroma(lifted, oa, ob, lifted / L);
                    }
                }

                r = Lookup(curve, r); g = Lookup(curve, g); b = Lookup(curve, b);

                if (chroma)
                {
                    // In Oklab, changing chroma leaves hue and lightness unchanged.
                    var (L, oa, ob) = Oklab.FromSrgb(r, g, b);
                    var factor = 1f;
                    if (vibrance != 0) factor *= VibranceFactor(L, oa, ob, vibrance);
                    if (saturation != 0) factor *= 1 + saturation;
                    (r, g, b) = Oklab.ScaleChroma(L, oa, ob, factor);
                }
                if (a.Tint != Tint.None)
                    (r, g, b) = Colorize(Math.Clamp(Kr * r + Kg * g + Kb * b, 0, 1), a.Tint, (float)a.TintHue);

                result[i] = Math.Clamp(b, 0, 1);
                result[i + 1] = Math.Clamp(g, 0, 1);
                result[i + 2] = Math.Clamp(r, 0, 1);
            }
        });

        if (lifted is not null) PixelPool.Return(lifted);
        return result;
    }

    /// <summary>
    /// Target brightness (Oklab L) for luminance with regions: original and fully brightened version, blended per scale
    /// of a Laplacian pyramid with the weighting map (Burt/Adelson, as in exposure fusion).
    /// </summary>
    /// <param name="target">Target as Oklab L (0 = off): the gain per region is chosen so that the region is lifted up to
    /// this value, at most <paramref name="luminance"/> and never below 1.</param>
    private static float[] LiftByRegions(Func<int, (float R, float G, float B)> pixel, int width, int height,
        float luminance, LuminanceRegions regions, float limit, float target)
    {
        var n = width * height;
        var plain = PixelPool.Rent(n);
        var bright = PixelPool.Rent(n);
        var weight = PixelPool.Rent(n);
        var cap = PixelPool.Rent(n);
        var weighting = regions.For(limit, target, luminance);
        Parallel.For(0, height, ComputeLimits.Options, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var (r, g, b) = pixel(p);
                var (l, oa, ob) = Oklab.FromSrgb(r, g, b);
                // Target: per region (not per pixel, so the structure in the tree is kept) only as strong as needed to
                // reach the target; a dark tree receives a lot, a meadow in the midtones hardly anything.
                var gain = LuminanceRegions.TargetGain(regions.AreaBrightness(x, y), target, luminance);
                var lift = l > 1e-4f ? Shoulder(l * gain) : l;
                var weightHere = weighting.Weight(x, y, l, oa, ob);
                plain[p] = l;
                bright[p] = lift;
                weight[p] = weightHere;
                cap[p] = l + (lift - l) * weightHere;
            }
        });
        // The pyramid blends coarse brightness over large areas, also across real edges (the sky next to the tree would
        // become brighter: the well-known large-area halo of exposure fusion). The per-pixel weighting is therefore the
        // upper limit; the pyramid only smooths downwards (soft transitions without a fringe).
        var blended = PyramidBlend.Blend(plain, bright, weight, width, height);
        Parallel.For(0, n, ComputeLimits.Options, p => blended[p] = Math.Clamp(blended[p], plain[p], Math.Max(plain[p], cap[p])));
        PixelPool.Return(plain);
        PixelPool.Return(bright);
        PixelPool.Return(weight);
        PixelPool.Return(cap);
        return blended;
    }

    /// <summary>Effect at ±100 with negative shadows or positive highlights: two stops.</summary>
    private const float MaxStops = 2f;

    /// <summary>Reach of shadows (from black) and highlights (from white).</summary>
    private const float ToneReach = 0.4f;

    /// <summary>Strength of the lift; ≤ 3 keeps the curve monotonic (slope ≥ 1 − Lift/3).</summary>
    private const float Lift = 2.5f;

    /// <summary>
    /// Target brightness of a pixel for the shadows slider (and, in <see cref="HighlightCurve"/>, for highlights).
    /// Shadows raise (or lower) where the surroundings are dark, highlights where they are bright. Raising shadows and
    /// lowering highlights use <see cref="Raise"/> (mirrored from white for highlights); lowering shadows and raising
    /// highlights act as an exposure in stops, for highlights computed from white so that nothing clips.
    /// </summary>
    /// <remarks>
    /// The ranges are deliberately narrow: shadows act fully in truly dark areas and end at a surrounding brightness of
    /// 0.4 (hardly any effect from 0.35), highlights start only at 0.6. Faces indoors typically lie at 0.4–0.6 and thus
    /// stay untouched (measured on a family photo: hallway 0.08, door frame 0.15, hair/eye area 0.36–0.40, cheeks 0.6).
    /// If shadows reach up to 0.6, they noticeably brighten faces as well.
    /// </remarks>
    internal static float ShadowCurve(float x, float shadows) => shadows >= 0
        ? Math.Clamp(x + shadows * Raise(x), 0, 1)
        : Math.Clamp(x * MathF.Pow(2, MaxStops * shadows * (1 - SmoothStep(0.02f, ToneReach, x))), 0, 1);

    internal static float HighlightCurve(float x, float highlights) => highlights <= 0
        ? Math.Clamp(x - (-highlights) * Raise(1 - x), 0, 1)
        : Math.Clamp(1 - (1 - x) * MathF.Pow(2, -MaxStops * highlights * SmoothStep(1 - ToneReach, 0.98f, x)), 0, 1);

    /// <summary>
    /// Lift of the shadows: x·(1 − x/reach)²·Lift, zero from the reach onwards. Deliberately not an exposure (x·2^…):
    /// with strong values that inverts, 0.25 becomes brighter than 0.30 and the tonal order flips.
    /// </summary>
    private static float Raise(float x)
    {
        if (x <= 0 || x >= ToneReach) return 0;
        var u = 1 - x / ToneReach;
        return Lift * x * u * u;
    }

    /// <summary>
    /// Target brightness per pixel: shadows and highlights curve via the local Laplacian filter, each with the wide
    /// surroundings as a second scale (shadows: the brighter, highlights: the darker of the two surrounding values).
    /// </summary>
    private static float[] ShadowsHighlights(float[] bgra, int width, int height, float shadows, float highlights)
    {
        var lum = new float[width * height];
        Parallel.For(0, height, ComputeLimits.Options, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
                lum[p] = Kb * bgra[p * 4] + Kg * bgra[p * 4 + 1] + Kr * bgra[p * 4 + 2];
        });

        // Pyramid level whose smoothing roughly corresponds to the wide surroundings (level l ≈ 2^l px).
        var wideLevel = Math.Max(1, (int)Math.Round(Math.Log2(Math.Max(2, WideNeighbourhoodPerEdge * Math.Max(width, height)))));

        Func<Pyramid, int, float[]> Guide(Func<float, float, float> pick) => (p, l) =>
        {
            if (l >= wideLevel || wideLevel >= p.Count) return p[l];
            var near = p[l];
            var wide = p.UpsampledFrom(wideLevel, l);
            var g = new float[near.Length];
            for (var i = 0; i < g.Length; i++) g[i] = pick(near[i], wide[i]);
            return g;
        };

        if (shadows != 0)
        {
            var input = lum;
            lum = LocalLaplacian.Apply(input, width, height, s => ShadowCurve(s, shadows), EdgeContrast, Guide(Math.Max));
            // Shadows do not change what is bright itself more than the curve would for this value (that is, not at
            // all); otherwise a bright fringe remains at hard edges.
            Bound(input, lum, s => ShadowCurve(s, shadows), v => SmoothStep(0.35f, 0.6f, v));
        }
        if (highlights != 0)
        {
            var input = lum;
            lum = LocalLaplacian.Apply(input, width, height, s => HighlightCurve(s, highlights), EdgeContrast, Guide(Math.Min));
            Bound(input, lum, s => HighlightCurve(s, highlights), v => 1 - SmoothStep(0.4f, 0.65f, v));
        }
        return lum;
    }

    /// <summary>
    /// Limits the change per pixel to that of the curve for the pixel's own value, with the share
    /// <paramref name="strength"/> (0 = filter unrestricted, 1 = fully limited).
    /// </summary>
    private static void Bound(float[] input, float[] output, Func<float, float> curve, Func<float, float> strength)
    {
        Parallel.For(0, (input.Length + 4095) / 4096, ComputeLimits.Options, chunk =>
        {
            var end = Math.Min(input.Length, (chunk + 1) * 4096);
            for (var i = chunk * 4096; i < end; i++)
            {
                var s = strength(input[i]);
                if (s <= 0) continue;
                var point = curve(input[i]);
                var bounded = point >= input[i] ? Math.Min(output[i], point) : Math.Max(output[i], point);
                output[i] += (bounded - output[i]) * s;
            }
        });
    }

    /// <summary>
    /// "Match colour" (without source image), luminance slider: the brightness is <em>multiplied</em>
    /// (Lab L, 1–200 %), so the contrast ratios are kept and the image looks more luminous than with a gamma
    /// that only raises the midtones. Above 0.8 it rolls off smoothly towards white so that nothing clips.
    /// </summary>
    private static float Shoulder(float l)
    {
        const float knee = 0.8f;
        return l <= knee ? Math.Max(0, l) : knee + (1 - knee) * (1 - MathF.Exp(-(l - knee) / (1 - knee)));
    }

    /// <summary>
    /// "Match colour", neutralise: the colour cast as the mean Oklab colour direction of the image parts that are not
    /// strongly coloured and not too dark or bright (grey-world assumption). Strong colours (a red T-shirt, blue sky) do
    /// not count, otherwise they would be "neutralised". Uses every 4th pixel of every 4th row, which is enough for a mean.
    /// </summary>
    internal static (float A, float B) ColourCast(float[] bgra, int width, int height)
    {
        double sa = 0, sb = 0, n = 0;
        for (var y = 0; y < height; y += 4)
        for (var x = 0; x < width; x += 4)
        {
            var i = (y * width + x) * 4;
            var (L, a, b) = Oklab.FromSrgb(bgra[i + 2], bgra[i + 1], bgra[i]);
            if (L < 0.15f || L > 0.97f || a * a + b * b > 0.1f * 0.1f) continue;
            sa += a; sb += b; n++;
        }
        return n > 0 ? ((float)(sa / n), (float)(sb / n)) : (0, 0);
    }

    /// <summary>
    /// Colour slider at ±100: shift in Oklab along the colour temperature, b towards yellow (warm) or blue (cool), a
    /// slightly along with it (artificial light is yellowish red).
    /// </summary>
    private const float WarmthB = 0.04f, WarmthA = 0.008f;

    /// <summary>Chroma from which a colour counts as strong (Oklab C; sRGB primaries ≈ 0.26–0.32).</summary>
    private const float StrongChroma = 0.2f;

    /// <summary>
    /// Vibrance: strongly boosts pale colours, strong colours hardly at all, and protects skin tones, so
    /// that images become livelier without faces turning orange. Towards the left, pale colours turn grey first, strong
    /// colours last longer.
    /// </summary>
    /// <returns>Factor for the chroma in Oklab (1 = unchanged).</returns>
    internal static float VibranceFactor(float L, float a, float b, float vibrance)
    {
        var chroma = MathF.Sqrt(a * a + b * b);
        var sat = Math.Clamp(chroma / StrongChroma, 0, 1);

        if (vibrance < 0)
            return Math.Max(0, 1 + vibrance * (1 - 0.6f * sat));

        var room = MathF.Pow(1 - sat, 1.5f);                  // pale: much headroom
        return 1 + 1.5f * vibrance * room * (1 - 0.75f * SkinTone(L, a, b, chroma));
    }

    /// <summary>
    /// How much a colour looks like skin (0–1): Oklab hue around 55° (orange-reddish), not too dark, moderately coloured;
    /// all skin types lie on this axis.
    /// </summary>
    private static float SkinTone(float L, float a, float b, float chroma)
    {
        if (chroma < 1e-3f) return 0;
        var onHue = Math.Clamp(1 - MathF.Abs(Oklab.Hue(a, b) - 55) / 30, 0, 1);
        var onChroma = 1 - SmoothStep(0.12f, 0.18f, chroma);    // very strong = not skin
        var onLight = SmoothStep(0.25f, 0.4f, L);
        return onHue * onChroma * onLight;
    }

    private const int CurveSize = 4096;

    /// <summary>
    /// Brightness as a gamma on the midtones (black and white stay fixed), contrast as an S-curve around the midpoint. As
    /// a table, so that exporting large images stays fast.
    /// </summary>
    internal static float[] ToneCurve(double brightness, double contrast)
    {
        var gamma = Math.Pow(2, -0.8 * Math.Clamp(brightness, -1, 1));
        var p = Math.Pow(2, 1.2 * Math.Clamp(contrast, -1, 1));
        var curve = new float[CurveSize];
        for (var i = 0; i < CurveSize; i++)
        {
            var x = Math.Pow((double)i / (CurveSize - 1), gamma);
            x = x < 0.5 ? 0.5 * Math.Pow(2 * x, p) : 1 - 0.5 * Math.Pow(2 - 2 * x, p);
            curve[i] = (float)x;
        }
        return curve;
    }

    /// <summary>
    /// Gradient black → tint → white. The tint is a muted colour in the chosen hue (HSL with S 0.38, L 0.48; at 30° the
    /// classic sepia, at 210° a cool blue).
    /// </summary>
    private static (float R, float G, float B) Colorize(float lum, Tint tint, float hue)
    {
        if (tint == Tint.BlackWhite) return (lum, lum, lum);
        var (mr, mg, mb) = Hsl(hue, 0.38f, 0.48f);

        // Shift the midpoint so that the tint lies at its own brightness.
        var mid = Kr * mr + Kg * mg + Kb * mb;
        if (lum <= mid)
        {
            var t = lum / mid;
            return (mr * t, mg * t, mb * t);
        }
        else
        {
            var t = (lum - mid) / (1 - mid);
            return (mr + (1 - mr) * t, mg + (1 - mg) * t, mb + (1 - mb) * t);
        }
    }

    /// <summary>HSL → RGB, hue in degrees, S and L 0–1.</summary>
    internal static (float R, float G, float B) Hsl(float hue, float s, float l)
    {
        var h = ((hue % 360) + 360) % 360 / 60f;
        var c = (1 - MathF.Abs(2 * l - 1)) * s;
        var x = c * (1 - MathF.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        var m = l - c / 2;
        return (r + m, g + m, b + m);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
