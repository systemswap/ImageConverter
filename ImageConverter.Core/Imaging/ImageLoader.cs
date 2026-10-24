using ImageConverter.Core.Localization;
using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// A loaded image, already rotated to the correct orientation.
/// </summary>
/// <param name="Image">The decoded image. It keeps the colour space of the original (for example Adobe RGB); only CMYK
/// images are converted to sRGB.</param>
/// <param name="FormatName">Name of the file format in upper case, as reported by ImageMagick.</param>
/// <param name="FileBytes">Size of the file in bytes.</param>
/// <param name="Dpi">Resolution in dots per inch; <see cref="ImageLoader.DefaultDpi"/> if the file has no usable
/// value.</param>
/// <remarks>Owned by the caller, who must dispose it after use.</remarks>
public sealed record LoadedImage(
    MagickImage Image,
    string FormatName,
    long FileBytes,
    double Dpi) : IDisposable
{
    /// <summary>Width in pixels.</summary>
    public int Width => (int)Image.Width;

    /// <summary>Height in pixels.</summary>
    public int Height => (int)Image.Height;

    /// <summary>Disposes the contained <see cref="Image"/>.</summary>
    public void Dispose() => Image.Dispose();
}

/// <summary>Thrown when an image is too large to open, judged by the dimensions in its header (see
/// <see cref="ImageLoader.MaxPixels"/>).</summary>
/// <param name="message">Localised message for the user.</param>
public sealed class ImageTooLargeException(string message) : IOException(message);

/// <summary>Loads images and image headers via ImageMagick, with a size check against crafted files.</summary>
public static class ImageLoader
{
    /// <summary>Resolution in DPI used when the file contains no usable value (the common convention of image
    /// editors).</summary>
    public const double DefaultDpi = 72;

    /// <summary>
    /// Loads an image via ImageMagick, applies the EXIF orientation and converts CMYK to sRGB.
    /// </summary>
    /// <param name="path">Path of the image file.</param>
    /// <param name="ct">Cancels loading.</param>
    /// <returns>The loaded image; the caller owns it and must dispose it.</returns>
    /// <exception cref="ImageTooLargeException">The header reports dimensions above <see cref="MaxPixels"/> or
    /// <see cref="Platform.ImageMagickPolicy.MaxEdge"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <remarks>
    /// The file is read asynchronously; decoding, rotating and colour conversion are CPU work and run on the thread pool
    /// so that the user interface does not freeze with large images.
    /// </remarks>
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

                // Phone photos: apply the EXIF orientation to the pixels so that nothing is shown sideways.
                image.AutoOrient();

                // Keep the colour space of the original (for example Adobe RGB); it is converted only for display
                // (ImageOps.ToDisplayBgra) or on export, depending on the settings. Only CMYK must be converted to RGB
                // immediately, because web formats and the editing pipeline work in RGB.
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
    /// Reads only the header: size (after the EXIF orientation, as in <see cref="LoadAsync"/>) and DPI, without decoding
    /// the pixels.
    /// </summary>
    /// <param name="path">Path of the image file.</param>
    /// <param name="ct">Cancels the probe before it starts.</param>
    /// <returns>Width and height in pixels and the resolution in DPI, or null if the format does not provide them; the
    /// caller then has to load the whole image.</returns>
    /// <remarks>For lists (exporting several images, printing) that would otherwise load every image completely.</remarks>
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
    /// Loads a thumbnail of at most <paramref name="edge"/> pixels per side, correctly oriented. JPEG files are already
    /// downscaled while decoding (a fraction of the work and memory). For display only, never for export.
    /// </summary>
    /// <param name="path">Path of the image file.</param>
    /// <param name="edge">Maximum width and height in pixels.</param>
    /// <param name="ct">Cancels loading before it starts.</param>
    /// <returns>The thumbnail; the caller owns it and must dispose it.</returns>
    /// <exception cref="ImageTooLargeException">The header reports dimensions that are too large, see
    /// <see cref="CheckSize"/>.</exception>
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
    /// Largest image that is opened, in pixels.
    /// </summary>
    /// <remarks>
    /// One pixel per 64 bytes of available memory, at least 100 and at most 2000 megapixels, because the editing pipeline
    /// needs a multiple of the image size (floating-point buffers). 16 GB give about 250 MP. Determined once at start-up.
    /// </remarks>
    public static long MaxPixels { get; } = Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 64, 100_000_000L, 2_000_000_000L);

    /// <summary>
    /// Checks the size from the header against <see cref="MaxPixels"/> and
    /// <see cref="Platform.ImageMagickPolicy.MaxEdge"/> before decoding.
    /// </summary>
    /// <param name="path">Path of the image file.</param>
    /// <exception cref="ImageTooLargeException">The image is too large.</exception>
    /// <remarks>
    /// A crafted file can be small and claim huge dimensions (1.5 MB for 40000 × 40000); without the check ImageMagick
    /// would allocate memory or swap space of that size. If the header cannot be read, the method returns without an
    /// error, and ImageMagick decides with its own limits when the image is read.
    /// </remarks>
    public static void CheckSize(string path)
    {
        MagickImageInfo info;
        try
        {
            info = new MagickImageInfo(path);
        }
        catch (MagickException)
        {
            return;   // ImageMagick reports the actual error when the image is read
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
            // Without a unit the value is not reliable (often 1 or 72).
            _ => DefaultDpi,
        };
    }
}
