using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Welche Dateiendungen die App öffnen kann. Die Liste wird mit dem abgeglichen,
/// was die eingebundene ImageMagick-Version tatsächlich lesen kann — so steht im
/// Dateidialog nie ein Format, an dem das Laden dann scheitert.
/// </summary>
public static class ImageFormats
{
    /// <summary>
    /// Kandidaten: übliche Bildformate. ImageMagick kennt noch viel mehr (Text,
    /// Paletten, wissenschaftliche Formate) — die gehören nicht in einen Bildkonverter.
    /// </summary>
    private static readonly string[] Candidates =
    [
        // Klassisch
        "jpg", "jpeg", "jpe", "jfif", "png", "bmp", "dib", "gif", "tif", "tiff", "ico", "cur", "tga",
        // Modern
        "webp", "avif", "heic", "heif", "jxl", "qoi", "jp2",
        // Sonstige
        "svg", "psd", "exr", "hdr",
        // Kamera-RAW
        "dng", "cr2", "cr3", "nef", "nrw", "arw", "orf", "rw2", "raf", "pef", "srw",
    ];

    private static readonly Lazy<HashSet<string>> Readable = new(() =>
    {
        var supported = MagickNET.SupportedFormats
            .Where(f => f.SupportsReading)
            .Select(f => f.Format)
            .ToHashSet();

        return Candidates
            .Where(ext => Enum.TryParse<MagickFormat>(ext, ignoreCase: true, out var fmt) && supported.Contains(fmt))
            .Select(ext => "." + ext)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>Lesbare Endungen mit Punkt, z. B. ".avif". Groß-/Kleinschreibung egal.</summary>
    public static IReadOnlySet<string> ReadableExtensions => Readable.Value;

    public static bool CanRead(string path) => ReadableExtensions.Contains(Path.GetExtension(path));
}
