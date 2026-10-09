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

    // Vorschau: verkleinerte Fassung und ihre feine Struktur, je Vorschaugröße einmal gerechnet
    private (AiPreviewSize Size, float K, float[] Image, float[] HighPass)? _small;

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

    public int Width => _w;
    public int Height => _h;

    public bool SupportsTexture => true;

    public int FaceCount => _faces.Count;

    public AiPixels Preview(double strength, double texture, AiPreviewSize size)
    {
        var small = Small(size);
        int sw = size.Width, sh = size.Height;
        var image = (float[])small.Image.Clone();
        foreach (var face in _faces)
        {
            // Bild → Ausschnitt auf dem Maßstab der Vorschau: erst auf volle Größe zurück (÷ K)
            var t = face.ToCrop;
            var toCrop = new Similarity(t.A / small.K, t.B / small.K, t.Tx, t.Ty);
            AiFaces.PasteBack(image, sw, sh, face, small.HighPass, toCrop, (float)strength, (float)texture);
        }

        var bgra = new float[sw * sh * 4];
        Parallel.For(0, sh, y =>
        {
            for (var x = 0; x < sw; x++)
            {
                var p = y * sw + x;
                bgra[p * 4] = Math.Clamp(image[p * 3 + 2], 0f, 1f);
                bgra[p * 4 + 1] = Math.Clamp(image[p * 3 + 1], 0f, 1f);
                bgra[p * 4 + 2] = Math.Clamp(image[p * 3], 0f, 1f);
                bgra[p * 4 + 3] = 1f;
            }
        });
        return new AiPixels(bgra, sw, sh);
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

    /// <summary>Verkleinerte Fassung (wie die normale Vorschau) und ihre feine Struktur.</summary>
    private (AiPreviewSize Size, float K, float[] Image, float[] HighPass) Small(AiPreviewSize size)
    {
        if (_small is { } s && s.Size == size) return s;
        var small = AiEnhancer.Shrink(_pixels, _w, _h, _mapping, size);
        var result = (size, (float)size.Width / _w, small, AiFaces.HighPass(small, size.Width, size.Height));
        _small = result;
        return result;
    }
}
