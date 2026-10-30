using ImageConverter.Core.Imaging;
using ImageMagick;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Whole image processed at full model strength, not yet blended with the original (<see cref="AiEnhancer.Run"/>).
/// </summary>
/// <remarks>
/// The strength is a blend between the two, so the model does not run again when it changes. The preview
/// blends a reduced copy, applying blends at full size. Only <see cref="AiBlend.Strength"/> has an effect;
/// the alpha channel comes from the original, in the preview as in <see cref="Compose"/>.
/// </remarks>
public sealed class EnhanceRun : IAiRun
{
    private readonly ushort[] _result;     // model result at the target size
    private readonly ushort[] _baseline;   // original at the target size (Lanczos)
    private readonly int _w, _h, _channels;
    private readonly string _mapping;
    private readonly MagickImage _meta;    // profiles and density (1 × 1 pixel)

    // Preview: both versions reduced (RGB 0…1) and the alpha of the original (null without), computed once per preview size
    private (AiPreviewSize Size, float[] Result, float[] Baseline, float[]? Alpha)? _small;

    internal EnhanceRun(MagickImage source, ushort[] result, ushort[] baseline, int w, int h, string mapping, int channels)
    {
        (_result, _baseline, _w, _h, _mapping, _channels) = (result, baseline, w, h, mapping, channels);
        _meta = new MagickImage(MagickColors.Black, 1, 1);
        lock (source)
        {
            _meta.Density = source.Density;
            ImageExporter.CopyProfiles(source, _meta);
        }
    }

    /// <inheritdoc/>
    public int Width => _w;

    /// <inheritdoc/>
    public int Height => _h;

    /// <inheritdoc/>
    public bool SupportsDetails => false;

    /// <inheritdoc/>
    public AiPixels Preview(AiBlend blend, AiPreviewSize size)
    {
        var small = Small(size);
        int sw = size.Width, sh = size.Height;
        var s = (float)Math.Clamp(blend.Strength, 0, 1);
        var bgra = new float[sw * sh * 4];
        Parallel.For(0, sh, ComputeLimits.Options, y =>
        {
            for (var x = 0; x < sw; x++)
            {
                var p = y * sw + x;
                for (var c = 0; c < 3; c++)
                {
                    var v = small.Baseline[p * 3 + c] + (small.Result[p * 3 + c] - small.Baseline[p * 3 + c]) * s;
                    bgra[p * 4 + 2 - c] = Math.Clamp(v, 0f, 1f);
                }
                bgra[p * 4 + 3] = small.Alpha?[p] ?? 1f;
            }
        });
        return new AiPixels(bgra, sw, sh);
    }

    /// <inheritdoc/>
    public MagickImage Compose(AiBlend blend)
    {
        var s = (float)Math.Clamp(blend.Strength, 0, 1);
        ushort[] pixels;
        if (s >= 1) pixels = (ushort[])_result.Clone();
        else
        {
            pixels = new ushort[_result.Length];
            Parallel.For(0, _h, ComputeLimits.Options, y =>
            {
                for (long i = (long)y * _w * _channels, end = i + (long)_w * _channels; i < end; i++)
                    pixels[i] = (ushort)(_baseline[i] + (_result[i] - _baseline[i]) * s + 0.5f);
            });
        }

        var image = AiEnhancer.WritePixels(pixels, _w, _h, _mapping);
        image.Density = _meta.Density;
        ImageExporter.CopyProfiles(_meta, image);
        return image;
    }

    /// <summary>Both versions at preview size, reduced like the normal preview.</summary>
    private (AiPreviewSize Size, float[] Result, float[] Baseline, float[]? Alpha) Small(AiPreviewSize size)
    {
        if (_small is { } s && s.Size == size) return s;
        var result = (size, AiEnhancer.Shrink(_result, _w, _h, _mapping, size), AiEnhancer.Shrink(_baseline, _w, _h, _mapping, size),
            AiEnhancer.ShrinkAlpha(_baseline, _w, _h, _mapping, size));
        _small = result;
        return result;
    }
}
