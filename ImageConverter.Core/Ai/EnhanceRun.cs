using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Ganzes Bild mit voller Wirkung des Modells, noch nicht mit dem Original gemischt (<see cref="AiEnhancer.Run"/>).
/// Die Stärke ist eine Mischung zwischen beiden — das Modell rechnet dafür nicht neu; die Vorschau mischt auf
/// einer verkleinerten Fassung, beim Übernehmen in voller Größe.
/// </summary>
public sealed class EnhanceRun : IAiRun
{
    private readonly ushort[] _result;     // Modell, in Zielgröße
    private readonly ushort[] _baseline;   // Original, in Zielgröße (Lanczos)
    private readonly int _w, _h, _channels;
    private readonly string _mapping;
    private readonly MagickImage _meta;    // Profile und Auflösung (1 × 1 Pixel)

    // Vorschau: beide Fassungen verkleinert (RGB 0…1), je Vorschaugröße einmal gerechnet
    private (AiPreviewSize Size, float[] Result, float[] Baseline)? _small;

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

    public int Width => _w;
    public int Height => _h;

    public bool SupportsDetails => false;

    public AiPixels Preview(AiBlend blend, AiPreviewSize size)
    {
        var small = Small(size);
        int sw = size.Width, sh = size.Height;
        var s = (float)Math.Clamp(blend.Strength, 0, 1);
        var bgra = new float[sw * sh * 4];
        Parallel.For(0, sh, y =>
        {
            for (var x = 0; x < sw; x++)
            {
                var p = y * sw + x;
                for (var c = 0; c < 3; c++)
                {
                    var v = small.Baseline[p * 3 + c] + (small.Result[p * 3 + c] - small.Baseline[p * 3 + c]) * s;
                    bgra[p * 4 + 2 - c] = Math.Clamp(v, 0f, 1f);
                }
                bgra[p * 4 + 3] = 1f;
            }
        });
        return new AiPixels(bgra, sw, sh);
    }

    public MagickImage Compose(AiBlend blend)
    {
        var s = (float)Math.Clamp(blend.Strength, 0, 1);
        ushort[] pixels;
        if (s >= 1) pixels = (ushort[])_result.Clone();
        else
        {
            pixels = new ushort[_result.Length];
            Parallel.For(0, _h, y =>
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

    /// <summary>Beide Fassungen auf Vorschaugröße, verkleinert wie die normale Vorschau.</summary>
    private (AiPreviewSize Size, float[] Result, float[] Baseline) Small(AiPreviewSize size)
    {
        if (_small is { } s && s.Size == size) return s;
        var result = (size, AiEnhancer.Shrink(_result, _w, _h, _mapping, size), AiEnhancer.Shrink(_baseline, _w, _h, _mapping, size));
        _small = result;
        return result;
    }
}
