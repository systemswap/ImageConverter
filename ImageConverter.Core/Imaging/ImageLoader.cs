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
