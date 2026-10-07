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

                // In sRGB arbeiten: eingebettetes Profil (z. B. Adobe RGB) umrechnen,
                // CMYK ohne Profil wenigstens in RGB bringen.
                if (image.GetColorProfile() is not null)
                    image.TransformColorSpace(ColorProfiles.SRGB);
                else if (image.ColorSpace == ColorSpace.CMYK)
                    image.ColorSpace = ColorSpace.sRGB;

                ct.ThrowIfCancellationRequested();
                return new LoadedImage(image, formatName, new FileInfo(path).Length, dpi);
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }, ct);

    private static double ReadDpi(MagickImage image)
    {
        var density = image.Density;
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
