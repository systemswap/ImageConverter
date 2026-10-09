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

    // Vorschau: beide Fassungen verkleinert (RGB 0…1), je Kantenlänge einmal gerechnet
    private (int MaxEdge, int W, int H, float[] Result, float[] Baseline)? _small;

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

    public bool SupportsTexture => false;

    public AiPreviewFrame Preview(double strength, double texture, int maxEdge = 2048)
    {
        var small = Small(maxEdge);
        var s = (float)Math.Clamp(strength, 0, 1);
        var bgra = new byte[small.W * small.H * 4];
        Parallel.For(0, small.H, y =>
        {
            for (var x = 0; x < small.W; x++)
            {
                var p = y * small.W + x;
                for (var c = 0; c < 3; c++)
                {
                    var v = small.Baseline[p * 3 + c] + (small.Result[p * 3 + c] - small.Baseline[p * 3 + c]) * s;
                    bgra[p * 4 + 2 - c] = (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
                }
                bgra[p * 4 + 3] = 255;
            }
        });
        return new AiPreviewFrame(bgra, small.W, small.H);
    }

    public MagickImage Compose(double strength, double texture)
    {
        var s = (float)Math.Clamp(strength, 0, 1);
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

    private (int MaxEdge, int W, int H, float[] Result, float[] Baseline) Small(int maxEdge)
    {
        if (_small is { } s && s.MaxEdge == maxEdge) return s;
        var k = Math.Min(1.0, maxEdge / (double)Math.Max(_w, _h));
        int sw = Math.Max(1, (int)Math.Round(_w * k)), sh = Math.Max(1, (int)Math.Round(_h * k));
        var result = (maxEdge, sw, sh, Shrink(_result, sw, sh, k), Shrink(_baseline, sw, sh, k));
        _small = result;
        return result;
    }

    /// <summary>Flächenmittel auf die Vorschaugröße, RGB 0…1.</summary>
    private float[] Shrink(ushort[] data, int sw, int sh, double k)
    {
        var small = new float[sw * sh * 3];
        Parallel.For(0, sh, y =>
        {
            int ys = (int)(y / k), ye = Math.Min(_h, Math.Max(ys + 1, (int)((y + 1) / k)));
            for (var x = 0; x < sw; x++)
            {
                int xs = (int)(x / k), xe = Math.Min(_w, Math.Max(xs + 1, (int)((x + 1) / k)));
                float r = 0, g = 0, b = 0;
                var n = 0;
                for (var yy = ys; yy < ye; yy++)
                for (var xx = xs; xx < xe; xx++)
                {
                    var i = ((long)yy * _w + xx) * _channels;
                    r += data[i]; g += data[i + 1]; b += data[i + 2];
                    n++;
                }
                var o = (y * sw + x) * 3;
                var f = 1f / (n * 65535f);
                (small[o], small[o + 1], small[o + 2]) = (r * f, g * f, b * f);
            }
        });
        return small;
    }
}
