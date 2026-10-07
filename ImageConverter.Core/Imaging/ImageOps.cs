using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>Skalierungsfilter, wie sie in der Oberfläche angeboten werden.</summary>
public enum ResampleFilter
{
    Lanczos3,
    Bicubic,
    CatmullRom,
    Mitchell,
    Spline,
    Bilinear,
    Box,
    Nearest,
}

/// <summary>
/// Bildoperationen auf ImageMagick-Bildern. Jede Operation liefert ein neues Bild
/// und lässt die Quelle unverändert — so kann der Verlauf alte Stände behalten.
/// </summary>
public static class ImageOps
{
    // ------------------------------------------------------------- Anzeige

    /// <summary>8-Bit-BGRA-Pixel (nicht vormultipliziert) für die Anzeige.</summary>
    /// <remarks>
    /// Bewusst über die Pixel-API statt <c>ToByteArray(MagickFormat.Bgra)</c>:
    /// Letzteres verändert intern das Quellbild — danach liefert jedes Skalieren
    /// nur noch schwarz-transparente Pixel (gemessen mit Magick.NET 14.17.2).
    /// Die Pixel-API liefert außerdem immer volle 8 Bit, auch bei 1-Bit-Dateien.
    /// </remarks>
    public static byte[] ToBgra(MagickImage image)
    {
        lock (image)
        {
            using var pixels = image.GetPixelsUnsafe();
            return pixels.ToByteArray("BGRA")
                   ?? throw new InvalidOperationException("Pixel konnten nicht gelesen werden.");
        }
    }

    // ------------------------------------------------------------- Threads
    //
    // Auf dasselbe Bild greifen bis zu drei Hintergrundaufgaben gleichzeitig zu:
    // Vorschau, Größenschätzung und Bearbeiten/Export. Jede arbeitet auf einer
    // eigenen Kopie — nur das Kopieren und Auslesen der gemeinsamen Quelle wird
    // gegeneinander gesperrt. Danach ist jede Kopie nur noch in einem Thread.

    /// <summary>Thread-sichere Kopie: alle Operationen kopieren über diesen Weg.</summary>
    public static MagickImage CloneOf(MagickImage source)
    {
        lock (source)
            return (MagickImage)source.Clone();
    }

    /// <summary>
    /// Wie <see cref="ToBgra"/>, aber für den Bildschirm: Bilder mit eigenem Profil
    /// (Adobe RGB …) werden nach sRGB umgerechnet, damit die Farben stimmen.
    /// Das Bild selbst bleibt in seinem Farbraum.
    /// </summary>
    public static byte[] ToDisplayBgra(MagickImage image)
    {
        IColorProfile? profile;
        lock (image) profile = image.GetColorProfile();
        if (profile is null || ImageExporter.IsSrgb(profile))
            return ToBgra(image);

        using var display = CloneOf(image);
        display.TransformColorSpace(ColorProfiles.SRGB);
        return ToBgra(display);
    }

    /// <summary>Liest 8-Bit-BGRA-Pixel zurück in ein ImageMagick-Bild (z. B. nach dem Schärfen).</summary>
    public static MagickImage FromBgra(byte[] bgra, int width, int height)
    {
        var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "BGRA"));
        return image;
    }

    // ------------------------------------------------------------- Geometrie

    public static MagickImage Rotate90(MagickImage source, bool clockwise)
    {
        var result = CloneOf(source);
        result.Rotate(clockwise ? 90 : -90);
        result.ResetPage();
        return result;
    }

    public static MagickImage FlipHorizontal(MagickImage source)
    {
        var result = CloneOf(source);
        result.Flop();
        return result;
    }

    /// <summary>
    /// Schneidet den Bereich aus. Mit Radius werden die Ecken abgerundet und alles
    /// außerhalb der Rundung wird transparent; das Ergebnis bleibt rechteckig.
    /// Rundung wie in der Vorschau: kreisrund bis zur halben kürzeren Seite,
    /// danach elliptisch bis zur vollen Ellipse (bei Quadrat: Kreis).
    /// </summary>
    public static MagickImage Crop(MagickImage source, int x, int y, int width, int height, double radius)
    {
        x = Math.Clamp(x, 0, (int)source.Width - 1);
        y = Math.Clamp(y, 0, (int)source.Height - 1);
        width = Math.Clamp(width, 1, (int)source.Width - x);
        height = Math.Clamp(height, 1, (int)source.Height - y);

        var result = CloneOf(source);
        result.Crop(new MagickGeometry(x, y, (uint)width, (uint)height));
        result.ResetPage();

        var rx = Math.Min(radius, width / 2.0);
        var ry = Math.Min(radius, height / 2.0);
        if (rx < 0.5 || ry < 0.5) return result;

        // Maske: weiß, Alpha = Abdeckung der abgerundeten Form (kantengeglättet)
        var coverage = RoundedCoverage(width, height, rx, ry);
        var maskPixels = new byte[width * height * 4];
        for (var i = 0; i < coverage.Length; i++)
        {
            var o = i * 4;
            maskPixels[o] = maskPixels[o + 1] = maskPixels[o + 2] = 255;
            maskPixels[o + 3] = coverage[i];
        }

        using var mask = new MagickImage();
        mask.ReadPixels(maskPixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGBA"));

        // DstIn: vorhandene Transparenz bleibt erhalten und wird mit der Form multipliziert
        result.Alpha(AlphaOption.Set);
        result.Composite(mask, CompositeOperator.DstIn);
        return result;
    }

    /// <summary>Deckung 0–255 je Pixel für ein Rechteck mit elliptischen Ecken (4×4-Supersampling).</summary>
    internal static byte[] RoundedCoverage(int width, int height, double rx, double ry)
    {
        var alpha = new byte[width * height];
        Array.Fill(alpha, (byte)255);

        var cw = (int)Math.Ceiling(rx);
        var ch = (int)Math.Ceiling(ry);

        // Ein Viertel rechnen, auf alle vier Ecken spiegeln
        var corner = new byte[cw * ch];
        Parallel.For(0, ch, ly =>
        {
            for (var lx = 0; lx < cw; lx++)
            {
                var inside = 0;
                for (var sy = 0; sy < 4; sy++)
                for (var sx = 0; sx < 4; sx++)
                {
                    var px = lx + (sx + 0.5) / 4;
                    var py = ly + (sy + 0.5) / 4;
                    if (px >= rx || py >= ry) { inside++; continue; }
                    var dx = (px - rx) / rx;
                    var dy = (py - ry) / ry;
                    if (dx * dx + dy * dy <= 1) inside++;
                }
                corner[ly * cw + lx] = (byte)(inside * 255 / 16);
            }
        });

        for (var ly = 0; ly < ch; ly++)
        for (var lx = 0; lx < cw; lx++)
        {
            var a = corner[ly * cw + lx];
            if (a == 255) continue;
            int left = lx, right = width - 1 - lx, top = ly, bottom = height - 1 - ly;
            alpha[top * width + left] = Math.Min(alpha[top * width + left], a);
            alpha[top * width + right] = Math.Min(alpha[top * width + right], a);
            alpha[bottom * width + left] = Math.Min(alpha[bottom * width + left], a);
            alpha[bottom * width + right] = Math.Min(alpha[bottom * width + right], a);
        }

        return alpha;
    }

    // ------------------------------------------------------------- Qualität

    public static FilterType ToMagick(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Lanczos3 => FilterType.Lanczos,
        // "Bicubic" als weicher, saumarmer Kubik-Filter (Keys-Familie)
        ResampleFilter.Bicubic => FilterType.Robidoux,
        ResampleFilter.CatmullRom => FilterType.Catrom,
        ResampleFilter.Mitchell => FilterType.Mitchell,
        ResampleFilter.Spline => FilterType.Spline,
        ResampleFilter.Bilinear => FilterType.Triangle,
        ResampleFilter.Box => FilterType.Box,
        ResampleFilter.Nearest => FilterType.Point,
        _ => FilterType.Lanczos,
    };

    /// <summary>
    /// Skaliert auf genau <paramref name="width"/> × <paramref name="height"/>.
    /// Gamma-korrekt heißt: in linearem Licht rechnen, so wie das Auge Helligkeit
    /// mischt — feine helle Strukturen werden beim Verkleinern nicht zu dunkel.
    /// </summary>
    public static MagickImage Resize(MagickImage source, int width, int height, ResampleFilter filter, bool gammaCorrect)
    {
        var result = CloneOf(source);
        if (result.Width == width && result.Height == height) return result;

        if (gammaCorrect) result.ColorSpace = ColorSpace.RGB;   // sRGB → linear
        result.FilterType = ToMagick(filter);
        result.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        if (gammaCorrect)
        {
            result.ColorSpace = ColorSpace.sRGB;                 // zurück
            // Der Umweg über linear entfernt das Farbprofil (z. B. Adobe RGB) —
            // die Pixel sind aber wieder im Ausgangsraum, also Profil zurücksetzen.
            ImageExporter.CopyProfiles(source, result);
        }

        return result;
    }

    /// <summary>Rauschminderung (Wavelet), <paramref name="strength"/> 0–3 wie der Regler.</summary>
    public static MagickImage Denoise(MagickImage source, double strength)
    {
        var result = CloneOf(source);
        if (strength > 0.01)
            result.WaveletDenoise(new Percentage(strength * 3), 0.0);
        return result;
    }
}
