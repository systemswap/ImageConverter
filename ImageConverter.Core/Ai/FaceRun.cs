using ImageConverter.Core.Imaging;
using ImageMagick;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Gesichter, mit voller Wirkung verbessert, aber noch nicht ins Bild gemischt (<see cref="AiFaces.Run"/>).
/// Mischt Stärke, Hautstruktur, Falten und Korn beliebig oft neu — für die Vorschau auf einer verkleinerten
/// Fassung (schnell genug für jede Reglerbewegung), beim Übernehmen in voller Größe.
/// </summary>
public sealed class FaceRun : IAiRun
{
    /// <summary>
    /// Ein verbessertes Gesicht: Ausschnitt vorher, Ergebnis des Modells, dessen grobe Fassung (RGB planar 0…1),
    /// was das Modell an Falten geglättet hat (Helligkeit), wo Falten zurückdürfen (0…1) und welcher Anteil Korn
    /// bis zum Foto fehlt (<see cref="FaceDetails.GrainGain"/>).
    /// </summary>
    internal sealed record Face(float[] Crop, float[] Enhanced, float[] Low, Similarity ToCrop, int Size,
        float[] Folds, float[] FoldMask, float[] Kept, float GrainGain = 1);

    private readonly float[] _image;      // Bild davor, RGB verschachtelt 0…1
    private readonly ushort[] _pixels;    // Bild davor mit Alpha, wie gelesen
    private readonly int _w, _h, _channels;
    private readonly string _mapping;
    private readonly IReadOnlyList<Face> _faces;
    private readonly MagickImage _meta;   // Profile und Auflösung des Bildes davor (1 × 1 Pixel)
    private float[]? _highPass;           // feine Struktur in voller Größe — erst beim ersten Bedarf

    // Vorschau: verkleinerte Fassung und ihre feine Struktur, je Vorschaugröße einmal gerechnet
    private (AiPreviewSize Size, float K, float[] Image, float[] HighPass)? _small;

    // Korn je Gesicht, Korngröße und Maßstab (null = volle Größe) — einmal gerechnet, beim Ziehen wiederverwendet
    private readonly Dictionary<(int Face, double Size, AiPreviewSize? Preview), AiFaces.GrainPatch> _grain = [];

    internal FaceRun(MagickImage source, float[] image, ushort[] pixels, int w, int h, string mapping, int channels,
        IReadOnlyList<Face> faces, GrainProfile grain)
    {
        (_image, _pixels, _w, _h, _mapping, _channels, _faces, Grain) = (image, pixels, w, h, mapping, channels, faces, grain);
        _meta = new MagickImage(MagickColors.Black, 1, 1);
        lock (source)
        {
            _meta.Density = source.Density;
            ImageExporter.CopyProfiles(source, _meta);
        }
    }

    public int Width => _w;
    public int Height => _h;

    public bool SupportsDetails => true;

    public int FaceCount => _faces.Count;

    /// <summary>Kornprofil des Fotos, vor der KI gemessen.</summary>
    public GrainProfile Grain { get; }

    public AiPixels Preview(AiBlend blend, AiPreviewSize size)
    {
        var small = Small(size);
        return ToPixels(Mix(small.Image, small.HighPass, size.Width, size.Height, small.K, blend, size), size);
    }

    /// <summary>
    /// Karte zum Anschauen: das Ergebnis blass, darüber rot, wo Falten aus dem Original zurückkommen (je kräftiger,
    /// desto mehr), blau, was vom Modell bleibt (Augen, Brauen, Lippen, Zähne).
    /// </summary>
    public AiPixels Map(AiBlend blend, AiPreviewSize size)
    {
        var small = Small(size);
        var image = Mix(small.Image, small.HighPass, size.Width, size.Height, small.K, blend, size);
        int w = size.Width, h = size.Height;

        // blass und etwas dunkler, damit die Farben gut zu sehen sind
        for (long i = 0; i < image.Length; i += 3)
        {
            var g = 0.2126f * image[i] + 0.7152f * image[i + 1] + 0.0722f * image[i + 2];
            for (var c = 0; c < 3; c++) image[i + c] = (image[i + c] * 0.35f + g * 0.65f) * 0.85f;
        }

        foreach (var face in _faces)
        {
            var toCrop = Scaled(face.ToCrop, small.K);
            var (x0, y0, x1, y1) = AiFaces.Bounds(toCrop, face.Size, w, h);
            Parallel.For(y0, y1 + 1, ComputeLimits.Options, y =>
            {
                for (var x = x0; x <= x1; x++)
                {
                    var (u, v) = toCrop.Apply(x, y);
                    if (u < 0 || v < 0 || u > face.Size - 1 || v > face.Size - 1) continue;
                    var edge = AiFaces.EdgeWeight(u, v, face.Size);
                    if (edge <= 0) continue;

                    var fold = Math.Clamp(Math.Abs(AiFaces.SampleOne(face.Folds, face.Size, u, v)) / 0.03f, 0, 1);
                    var kept = AiFaces.SampleOne(face.Kept, face.Size, u, v) * 0.55f;
                    var i = ((long)y * w + x) * 3;
                    Tint(image, i, (0.25f, 0.5f, 1f), kept * edge);
                    Tint(image, i, (1f, 0.15f, 0.1f), fold * 0.9f * edge);
                }
            });
        }

        return ToPixels(image, size);
    }

    private static void Tint(float[] image, long i, (float R, float G, float B) color, float amount)
    {
        if (amount <= 0) return;
        image[i] += (color.R - image[i]) * amount;
        image[i + 1] += (color.G - image[i + 1]) * amount;
        image[i + 2] += (color.B - image[i + 2]) * amount;
    }

    public MagickImage Compose(AiBlend blend)
    {
        if (blend.Texture > 0) _highPass ??= AiFaces.HighPass(_image, _w, _h);
        var image = Mix(_image, blend.Texture > 0 ? _highPass : null, _w, _h, 1, blend, null);

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

    /// <summary>Alle Gesichter in eine Kopie von <paramref name="source"/> setzen (Maßstab <paramref name="k"/> zur vollen Größe).</summary>
    private float[] Mix(float[] source, float[]? highPass, int w, int h, float k, AiBlend blend, AiPreviewSize? preview)
    {
        var image = (float[])source.Clone();
        for (var f = 0; f < _faces.Count; f++)
        {
            var face = _faces[f];
            var grain = blend.Grain > 0 ? GrainFor(f, blend.GrainSize, preview, k, w, h) : null;
            AiFaces.PasteBack(image, w, h, face, highPass, Scaled(face.ToCrop, k), blend, grain);
        }
        return image;
    }

    /// <summary>Bild → Ausschnitt auf dem Maßstab der Vorschau: erst auf volle Größe zurück (÷ K).</summary>
    private static Similarity Scaled(Similarity t, float k) => k == 1 ? t : new Similarity(t.A / k, t.B / k, t.Tx, t.Ty);

    /// <summary>
    /// Korn eines Gesichts: in voller Größe aus der gemessenen Korngröße, für die Vorschau daraus verkleinert
    /// (Flächenmittel) — so wirkt es dort, wie es nach dem Übernehmen verkleinert aussähe.
    /// </summary>
    private AiFaces.GrainPatch GrainFor(int f, double sizeFactor, AiPreviewSize? preview, float k, int w, int h)
    {
        var factor = Math.Round(sizeFactor, 3);
        lock (_grain)
            if (_grain.TryGetValue((f, factor, preview), out var cached)) return cached;

        AiFaces.GrainPatch? patch;
        lock (_grain) _grain.TryGetValue((f, factor, null), out patch);
        if (patch is null)
        {
            var (x0, y0, x1, y1) = AiFaces.Bounds(_faces[f].ToCrop, _faces[f].Size, _w, _h);
            var sigma = Grain.GrainSigma * factor;
            patch = new AiFaces.GrainPatch(FaceDetails.GrainField(x0, y0, x1 - x0 + 1, y1 - y0 + 1, sigma, seed: f),
                x0, y0, x1 - x0 + 1, y1 - y0 + 1, Grain);
            lock (_grain) _grain[(f, factor, null)] = patch;
        }

        if (preview is not null)
            patch = Shrink(patch, k, w, h);

        lock (_grain)
        {
            if (_grain.Count > 24) _grain.Clear();   // Korngröße oft verstellt: nicht endlos sammeln
            _grain[(f, factor, preview)] = patch;
        }
        return patch;
    }

    /// <summary>Korn auf den Maßstab <paramref name="k"/> verkleinern: Mittel über die Bildpixel jedes Vorschaupixels.</summary>
    private static AiFaces.GrainPatch Shrink(AiFaces.GrainPatch full, float k, int w, int h)
    {
        if (k >= 1) return full;
        var x0 = (int)Math.Floor(full.X0 * k);
        var y0 = (int)Math.Floor(full.Y0 * k);
        var x1 = Math.Min(w - 1, (int)Math.Ceiling((full.X0 + full.Width) * k));
        var y1 = Math.Min(h - 1, (int)Math.Ceiling((full.Y0 + full.Height) * k));
        int sw = x1 - x0 + 1, sh = y1 - y0 + 1;
        var field = new float[sw * sh];
        Parallel.For(0, sh, ComputeLimits.Options, row =>
        {
            for (var col = 0; col < sw; col++)
            {
                var fx0 = Math.Clamp((int)((x0 + col) / k) - full.X0, 0, full.Width - 1);
                var fx1 = Math.Clamp((int)((x0 + col + 1) / k) - full.X0, fx0 + 1, full.Width);
                var fy0 = Math.Clamp((int)((y0 + row) / k) - full.Y0, 0, full.Height - 1);
                var fy1 = Math.Clamp((int)((y0 + row + 1) / k) - full.Y0, fy0 + 1, full.Height);
                float sum = 0;
                for (var yy = fy0; yy < fy1; yy++)
                for (var xx = fx0; xx < fx1; xx++)
                    sum += full.Field[yy * full.Width + xx];
                field[row * sw + col] = sum / ((fx1 - fx0) * (fy1 - fy0));
            }
        });
        return full with { Field = field, X0 = x0, Y0 = y0, Width = sw, Height = sh };
    }

    private static AiPixels ToPixels(float[] image, AiPreviewSize size)
    {
        int sw = size.Width, sh = size.Height;
        var bgra = new float[sw * sh * 4];
        Parallel.For(0, sh, ComputeLimits.Options, y =>
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
