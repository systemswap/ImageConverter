using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>Ein geladenes Bild als rohe 8-Bit-BGRA-Pixel (nicht vormultipliziert), fertig für die Anzeige.</summary>
public sealed record LoadedImage(
    int Width,
    int Height,
    byte[] Bgra,
    string FormatName,
    long FileBytes,
    bool HasAlpha);

public static class ImageLoader
{
    /// <summary>
    /// Lädt ein Bild über ImageMagick. Die Datei wird asynchron gelesen; Dekodieren,
    /// Drehen und Farbumrechnung sind CPU-Arbeit und laufen auf dem Threadpool,
    /// damit die Oberfläche bei großen Bildern nicht hängt.
    /// </summary>
    public static Task<LoadedImage> LoadAsync(string path, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            using var image = new MagickImage();
            await image.ReadAsync(path, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var formatName = image.Format.ToString().ToUpperInvariant();

            // Handyfotos: EXIF-Drehung in die Pixel übernehmen, damit nichts quer liegt
            image.AutoOrient();

            // Für die Anzeige in sRGB: eingebettetes Profil (z. B. Adobe RGB) umrechnen,
            // CMYK ohne Profil wenigstens in RGB bringen.
            if (image.GetColorProfile() is not null)
                image.TransformColorSpace(ColorProfiles.SRGB);
            else if (image.ColorSpace == ColorSpace.CMYK)
                image.ColorSpace = ColorSpace.sRGB;

            ct.ThrowIfCancellationRequested();

            var hasAlpha = image.HasAlpha;

            // Rohexport schreibt in der Bittiefe der Datei — ein einfarbiges PNG oder
            // Schwarzweiß-TIFF käme sonst mit 1 Bit pro Kanal statt 8 heraus.
            image.Depth = 8;
            var bgra = image.ToByteArray(MagickFormat.Bgra);

            return new LoadedImage(
                (int)image.Width,
                (int)image.Height,
                bgra,
                formatName,
                new FileInfo(path).Length,
                hasAlpha);
        }, ct);
}
