using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Gesichter, mit voller Wirkung verbessert, aber noch nicht ins Bild gemischt (<see cref="AiFaces.Run"/>).
/// Mischt Stärke und Hautstruktur beliebig oft neu — für die Vorschau auf einer verkleinerten Fassung
/// (schnell genug für jede Reglerbewegung), beim Übernehmen in voller Größe.
/// </summary>
public sealed class FaceRun : IAiRun
{
    /// <summary>Ein verbessertes Gesicht: Ausschnitt vorher, Ergebnis des Modells, dessen grobe Fassung (alles RGB planar 0…1).</summary>
    internal sealed record Face(float[] Crop, float[] Enhanced, float[] Low, Similarity ToCrop, int Size);

    private readonly float[] _image;      // Bild davor, RGB verschachtelt 0…1
    private readonly ushort[] _pixels;    // Bild davor mit Alpha, wie gelesen
    private readonly int _w, _h, _channels;
    private readonly string _mapping;
    private readonly IReadOnlyList<Face> _faces;
    private readonly MagickImage _meta;   // Profile und Auflösung des Bildes davor (1 × 1 Pixel)
    private float[]? _highPass;           // feine Struktur in voller Größe — erst beim ersten Bedarf

    // Vorschau: verkleinerte Fassung und ihre feine Struktur, je Kantenlänge einmal gerechnet
    private (int MaxEdge, int W, int H, float K, float[] Image, float[] HighPass)? _small;

    internal FaceRun(MagickImage source, float[] image, ushort[] pixels, int w, int h, string mapping, int channels, IReadOnlyList<Face> faces)
    {
        (_image, _pixels, _w, _h, _mapping, _channels, _faces) = (image, pixels, w, h, mapping, channels, faces);
        _meta = new MagickImage(MagickColors.Black, 1, 1);
        lock (source)
        {
            _meta.Density = source.Density;
            ImageExporter.CopyProfiles(source, _meta);
        }
    }

    public bool SupportsTexture => true;

    public int FaceCount => _faces.Count;

    public AiPreviewFrame Preview(double strength, double texture, int maxEdge = 2048)
    {
        var small = Small(maxEdge);
        var image = (float[])small.Image.Clone();
        foreach (var face in _faces)
        {
            // Bild → Ausschnitt auf dem Maßstab der Vorschau: erst auf volle Größe zurück (÷ K)
            var t = face.ToCrop;
            var toCrop = new Similarity(t.A / small.K, t.B / small.K, t.Tx, t.Ty);
            AiFaces.PasteBack(image, small.W, small.H, face, small.HighPass, toCrop, (float)strength, (float)texture);
        }

        var bgra = new byte[small.W * small.H * 4];
        Parallel.For(0, small.H, y =>
        {
            for (var x = 0; x < small.W; x++)
            {
                var p = y * small.W + x;
                bgra[p * 4] = ToByte(image[p * 3 + 2]);
                bgra[p * 4 + 1] = ToByte(image[p * 3 + 1]);
                bgra[p * 4 + 2] = ToByte(image[p * 3]);
                bgra[p * 4 + 3] = 255;
            }
        });
        return new AiPreviewFrame(bgra, small.W, small.H);
    }

    public MagickImage Compose(double strength, double texture)
    {
        var image = (float[])_image.Clone();
        if (texture > 0) _highPass ??= AiFaces.HighPass(_image, _w, _h);
        foreach (var face in _faces)
            AiFaces.PasteBack(image, _w, _h, face, texture > 0 ? _highPass : null, face.ToCrop, (float)strength, (float)texture);

        var pixels = (ushort[])_pixels.Clone();
        for (long i = 0, j = 0; i < image.Length; i += 3, j += _channels)
        {
            pixels[j] = (ushort)(Math.Clamp(image[i], 0f, 1f) * 65535f + 0.5f);
            pixels[j + 1] = (ushort)(Math.Clamp(image[i + 1], 0f, 1f) * 65535f + 0.5f);
            pixels[j + 2] = (ushort)(Math.Clamp(image[i + 2], 0f, 1f) * 65535f + 0.5f);
        }

        var result = AiEnhancer.WritePixels(pixels, _w, _h, _mapping);
        result.Density = _meta.Density;
        ImageExporter.CopyProfiles(_meta, result);
        return result;
    }

    /// <summary>Verkleinerte Fassung (Flächenmittel) und ihre feine Struktur für die Vorschau.</summary>
    private (int MaxEdge, int W, int H, float K, float[] Image, float[] HighPass) Small(int maxEdge)
    {
        if (_small is { } s && s.MaxEdge == maxEdge) return s;
        var k = (float)Math.Min(1.0, maxEdge / (double)Math.Max(_w, _h));
        int sw = Math.Max(1, (int)Math.Round(_w * k)), sh = Math.Max(1, (int)Math.Round(_h * k));
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
                    var i = ((long)yy * _w + xx) * 3;
                    r += _image[i]; g += _image[i + 1]; b += _image[i + 2];
                    n++;
                }
                var o = (y * sw + x) * 3;
                (small[o], small[o + 1], small[o + 2]) = (r / n, g / n, b / n);
            }
        });
        var result = (maxEdge, sw, sh, k, small, AiFaces.HighPass(small, sw, sh));
        _small = result;
        return result;
    }

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
}
