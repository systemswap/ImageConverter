using ImageConverter.Core.Localization;
using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Ein geladenes Bild: bereits richtig herum gedreht und in sRGB.
/// Gehört dem Aufrufer — nach Gebrauch freigeben.
/// </summary>
public sealed record LoadedImage(
    MagickImage Image,
    string FormatName,
    long FileBytes,
    double Dpi) : IDisposable
{
    public int Width => (int)Image.Width;
    public int Height => (int)Image.Height;

    public void Dispose() => Image.Dispose();
}

/// <summary>Bild zu groß zum Öffnen (Maße aus den Kopfdaten, siehe <see cref="ImageLoader.MaxPixels"/>).</summary>
public sealed class ImageTooLargeException(string message) : IOException(message);

public static class ImageLoader
{
    /// <summary>DPI, wenn die Datei keine Angabe enthält (Konvention von Referenzwerkzeug und Co.).</summary>
    public const double DefaultDpi = 72;

    /// <summary>
    /// Lädt ein Bild über ImageMagick. Die Datei wird asynchron gelesen; Dekodieren,
    /// Drehen und Farbumrechnung sind CPU-Arbeit und laufen auf dem Threadpool,
    /// damit die Oberfläche bei großen Bildern nicht hängt.
    /// </summary>
    public static Task<LoadedImage> LoadAsync(string path, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            CheckSize(path);
            var image = new MagickImage();
            try
            {
                await image.ReadAsync(path, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var formatName = image.Format.ToString().ToUpperInvariant();
                var dpi = ReadDpi(image);

                // Handyfotos: EXIF-Drehung in die Pixel übernehmen, damit nichts quer liegt
                image.AutoOrient();

                // Farbraum des Originals behalten (z. B. Adobe RGB) — umgerechnet wird
                // erst für die Anzeige (ImageOps.ToDisplayBgra) bzw. beim Export, je
                // nach Einstellung. Nur CMYK muss sofort nach RGB: Web-Formate und die
                // Bearbeitung arbeiten in RGB.
                if (image.ColorSpace == ColorSpace.CMYK)
                {
                    if (image.GetColorProfile() is not null)
                        image.TransformColorSpace(ColorProfiles.SRGB);
                    else
                        image.ColorSpace = ColorSpace.sRGB;
                }

                ct.ThrowIfCancellationRequested();
                return new LoadedImage(image, formatName, new FileInfo(path).Length, dpi);
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }, ct);

    /// <summary>
    /// Nur die Kopfdaten: Größe (nach der EXIF-Drehung, wie <see cref="LoadAsync"/>) und DPI — ohne die Pixel zu
    /// entpacken. Für Listen (Export mehrerer Bilder, Drucken), die sonst jedes Bild ganz laden. Kann das Format das nicht,
    /// null — dann ganz laden.
    /// </summary>
    public static Task<(int Width, int Height, double Dpi)?> ProbeAsync(string path, CancellationToken ct = default) =>
        Task.Run<(int, int, double)?>(() =>
        {
            try
            {
                var info = new MagickImageInfo(path);
                if (info.Width == 0 || info.Height == 0) return null;
                var turned = info.Orientation is OrientationType.LeftTop or OrientationType.RightTop
                    or OrientationType.RightBottom or OrientationType.LeftBottom;
                var (w, h) = turned ? ((int)info.Height, (int)info.Width) : ((int)info.Width, (int)info.Height);
                return (w, h, ReadDpi(info.Density));
            }
            catch (MagickException)
            {
                return null;
            }
        }, ct);

    /// <summary>
    /// Vorschaubild höchstens <paramref name="edge"/> Pixel, richtig herum: JPEG wird gleich beim Entpacken verkleinert
    /// (ein Bruchteil der Arbeit und des Speichers). Nur für die Anzeige, nie für den Export.
    /// </summary>
    public static Task<MagickImage> LoadThumbnailAsync(string path, int edge, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            CheckSize(path);
            var settings = new MagickReadSettings();
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{edge * 2}x{edge * 2}");
            var image = new MagickImage(path, settings);
            try
            {
                image.AutoOrient();
                if (image.ColorSpace == ColorSpace.CMYK)
                {
                    if (image.GetColorProfile() is not null) image.TransformColorSpace(ColorProfiles.SRGB);
                    else image.ColorSpace = ColorSpace.sRGB;
                }
                image.Thumbnail((uint)edge, (uint)edge);
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }, ct);

    /// <summary>
    /// Größtes Bild, das geöffnet wird (Pixel): etwa 64 Byte je Pixel vom Arbeitsspeicher, mindestens 100, höchstens 2000
    /// Megapixel — die Bearbeitung braucht ein Vielfaches des Bildes (Gleitkomma-Puffer). 16 GB → rund 250 MP.
    /// </summary>
    public static long MaxPixels { get; } = Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 64, 100_000_000L, 2_000_000_000L);

    /// <summary>
    /// Vor dem Entpacken: Größe aus den Kopfdaten gegen <see cref="MaxPixels"/> prüfen. Eine präparierte Datei kann klein
    /// sein und riesige Maße behaupten (1,5 MB für 40000 × 40000) — ohne Prüfung belegte ImageMagick Speicher bzw.
    /// Auslagerung in dieser Größe. Liefert das Format keine Kopfdaten, entscheidet ImageMagick mit seinen Grenzen.
    /// </summary>
    public static void CheckSize(string path)
    {
        MagickImageInfo info;
        try
        {
            info = new MagickImageInfo(path);
        }
        catch (MagickException)
        {
            return;   // beim Lesen meldet ImageMagick den eigentlichen Fehler
        }
        var pixels = (long)info.Width * info.Height;
        if (pixels > MaxPixels || info.Width > Platform.ImageMagickPolicy.MaxEdge || info.Height > Platform.ImageMagickPolicy.MaxEdge)
            throw new ImageTooLargeException(Tr.T("Das Bild ist zu groß für diesen Rechner ({0} × {1} Pixel, {2:0} Megapixel; höchstens {3:0} Megapixel).",
                info.Width, info.Height, pixels / 1e6, MaxPixels / 1e6));
    }

    private static double ReadDpi(MagickImage image) => ReadDpi(image.Density);

    private static double ReadDpi(Density density)
    {
        if (density.X <= 0) return DefaultDpi;
        return density.Units switch
        {
            DensityUnit.PixelsPerCentimeter => Math.Round(density.X * 2.54),
            DensityUnit.PixelsPerInch => Math.Round(density.X),
            // Ohne Einheit ist der Wert nicht verlässlich (oft 1 oder 72)
            _ => DefaultDpi,
        };
    }
}
