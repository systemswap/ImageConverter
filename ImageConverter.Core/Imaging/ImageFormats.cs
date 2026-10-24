using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Determines which file extensions the application can open.
/// </summary>
/// <remarks>
/// The candidate list is intersected with the formats that the bundled ImageMagick build can actually
/// read, so that the file dialog never offers a format that then fails to load.
/// </remarks>
public static class ImageFormats
{
    /// <summary>
    /// Candidates: common image formats. ImageMagick supports many more (text, palettes, scientific
    /// formats), but those do not belong in an image converter.
    /// </summary>
    private static readonly string[] Candidates =
    [
        // Classic
        "jpg", "jpeg", "jpe", "jfif", "png", "bmp", "dib", "gif", "tif", "tiff", "ico", "cur", "tga",
        // Modern
        "webp", "avif", "heic", "heif", "jxl", "qoi", "jp2",
        // Other
        "svg", "psd", "exr", "hdr",
        // Camera raw
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

    /// <summary>Gets the readable extensions including the leading dot, for example ".avif".</summary>
    /// <remarks>
    /// The set compares case-insensitively. It is computed once on first access from
    /// <see cref="MagickNET.SupportedFormats"/>.
    /// </remarks>
    public static IReadOnlySet<string> ReadableExtensions => Readable.Value;

    /// <summary>Determines whether the extension of a path is one of the <see cref="ReadableExtensions"/>.</summary>
    /// <param name="path">File path or file name; only its extension is examined.</param>
    /// <returns><see langword="true"/> if the extension is readable; otherwise <see langword="false"/>.</returns>
    public static bool CanRead(string path) => ReadableExtensions.Contains(Path.GetExtension(path));
}
