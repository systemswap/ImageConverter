using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ImageConverter.Imaging;

/// <summary>
/// Bridge from ImageMagick pixels to Avalonia display bitmaps. All actual image operations live
/// in the Core (ImageOps) and run on the full-resolution image.
/// </summary>
public static class BitmapOps
{
    /// <summary>
    /// Creates a displayable bitmap from raw 8-bit BGRA pixels (not premultiplied, as delivered by
    /// ImageMagick).
    /// </summary>
    /// <param name="bgra">Pixel data, 4 bytes per pixel, rows without padding (stride = <paramref name="width"/> × 4).</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>A new 96-dpi bitmap owned by the caller.</returns>
    public static Bitmap FromBgra(byte[] bgra, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using var fb = bitmap.Lock();
        var stride = width * 4;
        if (fb.RowBytes == stride)
        {
            Marshal.Copy(bgra, 0, fb.Address, bgra.Length);
        }
        else
        {
            // Rows in the target buffer may be padded.
            for (var y = 0; y < height; y++)
                Marshal.Copy(bgra, y * stride, fb.Address + y * fb.RowBytes, stride);
        }

        return bitmap;
    }
}
