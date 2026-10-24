using System;
using System.Linq;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

// The image look as a whole, for extensions (AI) that bake it into the image beforehand or
// apply it over their own intermediate result.
public partial class MainViewModel
{
    /// <summary>
    /// What the chain applies to the image apart from size and format: denoise → adjustments (light, colour, dehaze …) → sharpen.
    /// </summary>
    /// <param name="Noise">Noise reduction strength; 0 for none.</param>
    /// <param name="Adjust">Tonal and colour adjustments.</param>
    /// <param name="SharpenAmount">Sharpening amount; 0 for none.</param>
    /// <param name="SharpenRadius">Sharpening radius at <paramref name="TargetWidth"/>.</param>
    /// <param name="TargetWidth">Width of the export in px; the sharpening radius is relative to it.</param>
    /// <param name="ColorNoise">Colour noise reduction strength; 0 for none.</param>
    internal sealed record Look(double Noise, Adjustments Adjust, double SharpenAmount, double SharpenRadius, int TargetWidth,
        double ColorNoise = 0)
    {
        public bool IsNone => Noise <= 0 && ColorNoise <= 0 && SharpenAmount <= 0 && Adjust.IsNone;

        /// <summary>Sharpening radius for an image of this width, scaled so that it has the same effect as in the export.</summary>
        public double SharpenSigma(int width) => SharpenRadius * width / Math.Max(1, TargetWidth);

        /// <summary>Steps for the progress display (denoise, dehaze per pass, light and colour, sharpen).</summary>
        public WorkPlan Plan(int width, int height, IProgress<WorkProgress>? progress, System.Threading.CancellationToken cancel) =>
            ImageExporter.PlanLook((double)width * height / 1e6, Noise, ColorNoise, Adjust, SharpenAmount, progress, cancel);

        /// <summary>The same look without effect: sliders that have an effect go to zero; fine settings stay.</summary>
        public Look Neutral() => this with
        {
            Noise = 0,
            ColorNoise = 0,
            SharpenAmount = 0,
            Adjust = Adjust with
            {
                Brightness = 0, Contrast = 0, Shadows = 0, Highlights = 0, Saturation = 0, Tint = Tint.None,
                Vibrance = 0, Dehaze = 0, MatchLuminance = 0, MatchLimit = 100, MatchTarget = 0, MatchGlobal = 0, MaskAmount = 0, Neutralize = 0, Warmth = 0,
                WbNeutral = null, WbBlack = null, WbWhite = null,
            },
        };
    }

    /// <summary>Size and filter of the normal preview, for intermediate results shown in its place.</summary>
    internal AiPreviewSize PreviewSize()
    {
        var (tw, th) = EffectiveTarget();
        return AiPreviewSize.Fit(tw, th, PreviewMaxEdge) with { Filter = SelectedFilter.Filter, GammaCorrect = GammaCorrect };
    }

    /// <summary>
    /// Determines whether the export target is as large as the image. In that case the normal preview without a look
    /// shows the image itself at full size, so an intermediate result should also appear at full size; otherwise it
    /// jumps when it is applied.
    /// </summary>
    internal bool TargetIsImageSize
    {
        get
        {
            var (tw, th) = EffectiveTarget();
            // Only without geometry: otherwise the intermediate result would have to be rotated/straightened
            // at full size.
            return _history is { } h && h.Recipe.IsNone && tw == SourceWidth && th == SourceHeight;
        }
    }

    internal Look CurrentLook() => new(
        EffectiveNoise,
        CurrentAdjustments(),
        EffectiveSharpen,
        SharpenRadius,
        EffectiveTarget().Width,
        EffectiveColorNoise);

    /// <summary>
    /// Determines whether several images share the settings ("for all"). In that case the look cannot be baked into
    /// a single image and reset.
    /// </summary>
    internal bool SettingsShared => ApplyToAll && _files.Count > 1;

    /// <summary>Bakes the look into the image at full size (background thread). Without effect, returns the image itself.</summary>
    internal static MagickImage RenderLook(MagickImage image, Look look, WorkPlan? plan = null)
    {
        if (look.IsNone) return image;
        int width;
        lock (image) width = (int)image.Width;
        return ImageExporter.RenderLook(image, look.Noise, look.ColorNoise, look.Adjust, look.SharpenAmount, look.SharpenSigma(width), plan);
    }

    /// <summary>Grain for preview and export; not part of the look, because it is never baked into the image before the AI.</summary>
    internal GrainSettings? CurrentGrain() => EffectiveGrain;

    /// <summary>
    /// Applies the look to a preview (pixel buffer of the chain) and rounds it for display (background thread). Grain
    /// is applied afterwards, even without a look, always last.
    /// </summary>
    /// <param name="bgra">Pixels of the chain (BGRA, float).</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="look">The look; null for none.</param>
    /// <param name="plan">Progress plan; null for none.</param>
    /// <param name="grain">Grain settings; null for none.</param>
    /// <param name="grainScale">Size of this preview relative to the output.</param>
    /// <returns>Dithered 8-bit BGRA pixels.</returns>
    internal static byte[] RenderLookPreview(float[] bgra, int width, int height, Look? look, WorkPlan? plan = null,
        GrainSettings? grain = null, double grainScale = 1)
    {
        var hasLook = look is { IsNone: false };
        if (!hasLook && grain is null) return Pixels.ToBytes(bgra, dither: true);

        var pixels = bgra;
        if (hasLook && (look!.Noise > 0 || look.ColorNoise > 0))
            pixels = Denoiser.Apply(pixels, width, height, look.Noise, look.ColorNoise, (double)width / Math.Max(1, look.TargetWidth), plan);
        if (hasLook && !look!.Adjust.IsNone) pixels = Adjust.Apply(pixels, width, height, look.Adjust, plan);
        if (hasLook && look!.SharpenAmount > 0)
        {
            plan?.Next();
            pixels = Sharpen.UnsharpMask(pixels, width, height, look.SharpenAmount, look.SharpenSigma(width));
        }
        if (grain is not null) pixels = Grain.Apply(pixels, width, height, grain, Math.Min(1, grainScale));

        var result = Pixels.ToBytes(pixels, dither: true);
        PixelPool.Trim();
        return result;
    }

    /// <summary>Returns the settings with another look (everything else stays).</summary>
    private static SettingsState WithLook(SettingsState s, Look look)
    {
        var a = look.Adjust;
        return s with
        {
            NoiseReduction = look.Noise,
            ColorNoiseReduction = look.ColorNoise,
            SharpenAmount = look.SharpenAmount,
            SharpenRadius = look.SharpenRadius,
            Brightness = a.Brightness, Contrast = a.Contrast, Shadows = a.Shadows, Highlights = a.Highlights,
            Saturation = a.Saturation, Vibrance = a.Vibrance, Dehaze = a.Dehaze,
            SelectedTint = TintLabels.First(t => t.Tint == a.Tint).Label, TintHue = a.TintHue,
            MatchLuminance = a.MatchLuminance, MatchLimit = a.MatchLimit, MatchTarget = a.MatchTarget, MatchGlobal = a.MatchGlobal, MaskAmount = a.MaskAmount, MaskEnd = a.MaskEnd, MaskCurve = a.MaskCurve, Neutralize = a.Neutralize, Warmth = a.Warmth,
            DehazePasses = a.DehazePasses, DehazeClarity = a.DehazeClarity, DehazeContrast = a.DehazeContrast,
            DehazeVibrance = a.DehazeVibrance, DehazeSharpen = a.DehazeSharpen, DehazeEdge = a.DehazeEdge,
            DehazeBrightness = a.DehazeBrightness, DehazeDeband = a.DehazeDeband, DehazeCast = a.DehazeCast,
            WbNeutral = a.WbNeutral, WbBlack = a.WbBlack, WbWhite = a.WbWhite,
        };
    }

}
