using ImageMagick;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Result of an AI run that has not yet been blended into the image.
/// </summary>
/// <remarks>
/// The model runs once at full strength; everything in <see cref="AiBlend"/> can then be blended again as often
/// as needed: fast for the preview (live while a slider is dragged), at full size when applying.
/// </remarks>
public interface IAiRun
{
    /// <summary>Gets the width of the result in pixels.</summary>
    int Width { get; }

    /// <summary>Gets the height of the result in pixels.</summary>
    int Height { get; }

    /// <summary>
    /// Gets whether the detail settings of <see cref="AiBlend"/> (texture, folds, grain) have an effect.
    /// True for face models; for whole-image runs only <see cref="AiBlend.Strength"/> applies.
    /// </summary>
    bool SupportsDetails { get; }

    /// <summary>Blends a preview at <paramref name="size"/>.</summary>
    /// <param name="blend">Blend settings.</param>
    /// <param name="size">Preview size; it has the same aspect ratio as the result.</param>
    /// <returns>The preview as a pixel buffer of the image pipeline.</returns>
    AiPixels Preview(AiBlend blend, AiPreviewSize size);

    /// <summary>Blends the final result at full size.</summary>
    /// <param name="blend">Blend settings.</param>
    /// <returns>A new image owned by the caller, with the profiles and density of the image before the run.</returns>
    MagickImage Compose(AiBlend blend);
}

/// <summary>Describes how the result of an AI run is blended into the image; none of it requires running the model again.</summary>
/// <remarks>
/// This type is a class on purpose: as a <c>record struct</c>, <c>new AiBlend()</c> would call the
/// parameterless constructor and ignore the defaults, giving strength 0, so intermediate frames and results
/// would show only the original. See docs/FEHLERGEDAECHTNIS.md, No. 24.
/// </remarks>
/// <param name="Strength">0 … 1: opacity of the AI result over the original (1 = AI only).</param>
/// <param name="Texture">0 … 1: finest structure (pores, fine hairs) from the original instead of from the model.</param>
/// <param name="Folds">0 … 1: restores from the original the folds and lines the model smoothed away (skin only).</param>
/// <param name="Grain">0 … 2: grain on the face; 1 = as strong as measured in the rest of the photo.</param>
/// <param name="GrainSize">Factor applied to the measured grain size (1 = as in the photo).</param>
public sealed record AiBlend(double Strength = 1, double Texture = 0, double Folds = 0, double Grain = 0, double GrainSize = 1);

/// <summary>
/// Preview as a pixel buffer of the image pipeline (<see cref="Imaging.Pixels"/>: B, G, R, A, each 0 … 1, sRGB),
/// so that the adjustments (haze, light, sharpness …) can run on it without rounding.
/// </summary>
/// <param name="Bgra">Pixels, row by row, <paramref name="Width"/> × <paramref name="Height"/> × 4 values.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public sealed record AiPixels(float[] Bgra, int Width, int Height);

/// <summary>
/// Size and reduction method of the preview.
/// </summary>
/// <remarks>
/// These must be the same as for the normal preview (export target size, at most 2048 px, selected filter);
/// otherwise the image jumps when the result is applied.
/// </remarks>
/// <param name="Width">Preview width in pixels.</param>
/// <param name="Height">Preview height in pixels.</param>
/// <param name="Filter">Resampling filter used for the reduction.</param>
/// <param name="GammaCorrect">Whether the reduction is gamma-correct.</param>
public readonly record struct AiPreviewSize(int Width, int Height,
    Imaging.ResampleFilter Filter = Imaging.ResampleFilter.Lanczos3, bool GammaCorrect = true)
{
    /// <summary>Creates a size with the aspect ratio of <paramref name="width"/> × <paramref name="height"/> and the longest edge at most <paramref name="maxEdge"/>.</summary>
    /// <param name="width">Width of the full image in pixels.</param>
    /// <param name="height">Height of the full image in pixels.</param>
    /// <param name="maxEdge">Maximum length of the longest edge in pixels.</param>
    /// <returns>The fitted size (never enlarged, at least 1 × 1), with the default filter and gamma correction.</returns>
    public static AiPreviewSize Fit(int width, int height, int maxEdge)
    {
        var k = Math.Min(1.0, maxEdge / (double)Math.Max(width, height));
        return new AiPreviewSize(Math.Max(1, (int)Math.Round(width * k)), Math.Max(1, (int)Math.Round(height * k)));
    }
}
