using ImageMagick;
using ImageConverter.Core.Platform;

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
                   ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
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

    /// <summary>Wie <see cref="ToDisplayBgra"/>, aber als 32-Bit-Puffer mit voller Tiefe (siehe <see cref="Pixels"/>).</summary>
    public static float[] ToDisplayPixels(MagickImage image)
    {
        IColorProfile? profile;
        lock (image) profile = image.GetColorProfile();
        if (profile is null || ImageExporter.IsSrgb(profile))
            return Pixels.FromMagick(image);

        using var display = CloneOf(image);
        display.TransformColorSpace(ColorProfiles.SRGB);
        return Pixels.FromMagick(display);
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

    /// <summary>
    /// Frei drehen und zuschneiden in einem Schritt. Der Rahmen (<paramref name="x"/> … <paramref name="height"/>)
    /// gilt im ungedrehten Koordinatensystem der Vorschau um die Bildmitte (siehe <see cref="RotationGeometry"/>);
    /// er sollte ganz im gedrehten Bild liegen, sonst bleiben die Ecken außerhalb leer (transparent).
    /// Positive Winkel drehen im Uhrzeigersinn. Radius wie bei <see cref="Crop"/>.
    /// </summary>
    public static MagickImage RotateAndCrop(MagickImage source, double degrees, int x, int y, int width, int height,
        double radius)
    {
        degrees = RotationGeometry.Normalize(degrees);
        if (Math.Abs(degrees) < 1e-9) return Crop(source, x, y, width, height, radius);

        var hadAlpha = source.HasAlpha;
        using var rotated = CloneOf(source);
        // Nur die Kantenpixel außerhalb des Rahmens berühren den Hintergrund
        rotated.BackgroundColor = MagickColors.Transparent;
        rotated.Rotate(degrees);
        rotated.ResetPage();

        // Die Drehmitte bleibt die Mitte: Die Fläche wächst nach allen Seiten gleich
        var dx = (rotated.Width - (double)source.Width) / 2;
        var dy = (rotated.Height - (double)source.Height) / 2;
        var result = Crop(rotated, (int)Math.Round(x + dx), (int)Math.Round(y + dy), width, height, radius);

        // Bild ohne Transparenz: auch ohne Alphakanal zurückgeben (sonst wird z. B. ein PNG größer)
        if (!hadAlpha && radius < 0.5) result.Alpha(AlphaOption.Off);
        return result;
    }

    /// <summary>
    /// Entzerren: Das Viereck (oben links, oben rechts, unten rechts, unten links; in Bildpixeln) wird zum
    /// geraden Rechteck in der Größe von <see cref="PerspectiveGeometry.TargetSize"/> — z. B. ein schräg
    /// fotografiertes Blatt. Nur der Inhalt des Vierecks bleibt.
    /// </summary>
    public static MagickImage Straighten(MagickImage source, IReadOnlyList<(double X, double Y)> quad)
    {
        if (!PerspectiveGeometry.IsValid(quad))
            throw new ArgumentException("Die vier Ecken bilden kein brauchbares Viereck.", nameof(quad));

        var (w, h) = PerspectiveGeometry.TargetSize(quad);
        var hadAlpha = source.HasAlpha;
        var result = CloneOf(source);
        result.VirtualPixelMethod = VirtualPixelMethod.Transparent;

        // Paare Quelle → Ziel; ImageMagick rechnet für jedes Zielpixel zurück (gute Glättung, kein Treppeneffekt).
        // Pixelmitten: Die Ecke des Vierecks ist die Außenkante, im Ziel also (0,0) bis (w,h).
        double[] corners = [0, 0, w, 0, w, h, 0, h];
        var args = new double[16];
        for (var i = 0; i < 4; i++)
        {
            args[i * 4] = quad[i].X;
            args[i * 4 + 1] = quad[i].Y;
            args[i * 4 + 2] = corners[i * 2];
            args[i * 4 + 3] = corners[i * 2 + 1];
        }

        result.Distort(new DistortSettings(DistortMethod.Perspective) { Viewport = new MagickGeometry(0, 0, (uint)w, (uint)h) }, args);
        result.ResetPage();

        // Das Viereck liegt im Bild (die Griffe bleiben drin) — ohne Transparenz geladen, ohne Alphakanal zurück
        if (!hadAlpha) result.Alpha(AlphaOption.Off);
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
        Parallel.For(0, ch, ComputeLimits.Options, ly =>
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
        bool sameSize, large;
        lock (source)
        {
            sameSize = source.Width == width && source.Height == height;
            large = (long)source.Width * source.Height >= ParallelMinPixels && source.ColorSpace == ColorSpace.sRGB;
        }
        if (!sameSize && large)
            return ResizeInStrips(source, width, height, ToMagick(filter), gammaCorrect);

        var result = CloneOf(source);
        if (sameSize) return result;

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

    // ------------------------------------------------------- Paralleles Skalieren
    //
    // Magick.NET-Q16-AnyCPU ist ohne OpenMP gebaut und rechnet auf einem Kern: Ein
    // 24-Megapixel-Foto gamma-korrekt auf 2048 px zu verkleinern dauerte 2,8 s (1,6 s
    // davon die Umrechnung nach linear, 1 s das Skalieren). Skalieren ist trennbar:
    // erst waagerecht (jede Zeile für sich), dann senkrecht (jede Spalte für sich).
    // Deshalb Zeilenstreifen bzw. Spaltenstreifen parallel rechnen — ohne Überlappung,
    // denn in der jeweils anderen Richtung bleibt die Größe gleich. Die Umrechnung
    // sRGB ↔ linear übernimmt eine Tabelle mit denselben Formeln wie ImageMagick.

    /// <summary>Darunter lohnt das Aufteilen nicht; es gilt der einfache Weg.</summary>
    internal const long ParallelMinPixels = 1_000_000;

    private const int Quantum = 65535;   // Q16
    private static readonly Lazy<(ushort[] ToLinear, ushort[] ToSrgb)> GammaTables = new(() =>
    {
        var toLinear = new ushort[Quantum + 1];
        var toSrgb = new ushort[Quantum + 1];
        for (var i = 0; i <= Quantum; i++)
        {
            var x = (double)i / Quantum;
            // wie DecodePixelGamma / EncodePixelGamma in ImageMagick (gamma-private.h)
            var lin = x <= 0.0404482362771076 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            var srgb = x <= 0.0031306684425005883 ? 12.92 * x : 1.055 * Math.Pow(x, 1.0 / 2.4) - 0.055;
            toLinear[i] = (ushort)Math.Clamp(Math.Round(lin * Quantum), 0, Quantum);
            toSrgb[i] = (ushort)Math.Clamp(Math.Round(srgb * Quantum), 0, Quantum);
        }
        return (toLinear, toSrgb);
    });

    // Zerschneiden und Zusammensetzen über ImageMagick (CloneArea, Append) kostete selbst
    // rund 1 s auf einem Kern — deshalb laufen die Streifen über Pixel-Arrays.
    private static MagickImage ResizeInStrips(MagickImage image, int width, int height, FilterType filter, bool gammaCorrect)
    {
        // Die Quelle ist oft das gemeinsame Original (siehe CloneOf) — nur gesperrt auslesen
        int sw, sh;
        uint depth;
        string map;
        ushort[] source;
        lock (image)
        {
            sw = (int)image.Width;
            sh = (int)image.Height;
            depth = image.Depth;
            map = image.HasAlpha ? "RGBA" : "RGB";
            using var pixels = image.GetPixelsUnsafe();
            source = pixels.ToShortArray(map) ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
        }
        var ch = map.Length;
        var (toLinear, toSrgb) = gammaCorrect ? GammaTables.Value : default;

        // 1) Zeilenstreifen: nach linear, waagerecht auf die Zielbreite
        var wide = new ushort[(long)width * sh * ch];
        Parallel.ForEach(Strips(sh), ComputeLimits.Options, r =>
        {
            int offset = r.Start * sw * ch, count = r.Length * sw * ch;
            if (toLinear is not null) MapColor(source.AsSpan(offset, count), ch, toLinear);
            var scaled = Scale(source, offset, count, sw, r.Length, width, r.Length, map, filter);
            scaled.CopyTo(wide, (long)r.Start * width * ch);
        });

        // 2) Spaltenstreifen: senkrecht auf die Zielhöhe, zurück nach sRGB
        var result = new ushort[(long)width * height * ch];
        Parallel.ForEach(Strips(width), ComputeLimits.Options, c =>
        {
            var row = c.Length * ch;
            var column = new ushort[row * sh];
            for (var y = 0; y < sh; y++)
                Array.Copy(wide, ((long)y * width + c.Start) * ch, column, (long)y * row, row);

            var scaled = Scale(column, 0, column.Length, c.Length, sh, c.Length, height, map, filter);
            if (toSrgb is not null) MapColor(scaled, ch, toSrgb);
            for (var y = 0; y < height; y++)
                Array.Copy(scaled, (long)y * row, result, ((long)y * width + c.Start) * ch, row);
        });

        var resized = new MagickImage();
        resized.ReadPixels(result, new PixelReadSettings((uint)width, (uint)height, StorageType.Quantum, map));
        resized.Depth = depth;                     // 8-Bit-Quelle bleibt 8 Bit (z. B. als PNG)
        ImageExporter.CopyProfiles(image, resized);  // EXIF, ICC … wie beim Schärfen
        return resized;
    }

    /// <summary>Teilt eine Länge in so viele Stücke wie Kerne (mindestens 64 Pixel je Stück).</summary>
    private static List<(int Start, int Length)> Strips(int total)
    {
        var count = Math.Clamp(Math.Min(Environment.ProcessorCount, total / 64), 1, 32);
        var list = new List<(int, int)>(count);
        for (var i = 0; i < count; i++)
        {
            int start = (int)((long)total * i / count), end = (int)((long)total * (i + 1) / count);
            list.Add((start, end - start));
        }
        return list;
    }

    /// <summary>Ein Streifen als eigenes Bild skaliert; liefert seine Pixel.</summary>
    private static ushort[] Scale(ushort[] data, int offset, int count, int w, int h,
        int toWidth, int toHeight, string map, FilterType filter)
    {
        using var strip = new MagickImage();
        strip.ReadPixels(data, (uint)offset, (uint)count,
            new PixelReadSettings((uint)w, (uint)h, StorageType.Quantum, map));
        strip.FilterType = filter;
        strip.Resize(new MagickGeometry((uint)toWidth, (uint)toHeight) { IgnoreAspectRatio = true });
        using var pixels = strip.GetPixelsUnsafe();
        return pixels.ToShortArray(map) ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
    }

    /// <summary>Wendet eine Tabelle auf Rot, Grün und Blau an (Alpha bleibt).</summary>
    private static void MapColor(Span<ushort> data, int channels, ushort[] table)
    {
        for (var p = 0; p < data.Length; p += channels)
        {
            data[p] = table[data[p]];
            data[p + 1] = table[data[p + 1]];
            data[p + 2] = table[data[p + 2]];
        }
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
