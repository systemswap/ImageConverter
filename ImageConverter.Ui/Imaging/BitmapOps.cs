using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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

    /// <summary>
    /// Reduces a display bitmap: each target pixel is the average of the source pixels it covers, weighted by alpha so that
    /// transparent pixels do not darken the edges.
    /// </summary>
    /// <remarks>
    /// Avalonia's <see cref="Bitmap.CreateScaledBitmap"/> rejects the bitmaps the display uses (<see cref="WriteableBitmap"/>)
    /// under Skia with "Invalid source bitmap type"; the reduced "Before" side was therefore never created and every movement
    /// of the dividing line redrew the full-size image (docs/FEHLERGEDAECHTNIS.md, No. 106).
    /// </remarks>
    /// <param name="source">Bitmap in BGRA, not premultiplied (as from <see cref="FromBgra"/>); only read.</param>
    /// <param name="target">Size of the result; at most the size of <paramref name="source"/>.</param>
    /// <returns>A new bitmap owned by the caller.</returns>
    public static Bitmap Shrink(Bitmap source, PixelSize target)
    {
        var (sw, sh) = (source.PixelSize.Width, source.PixelSize.Height);
        var (tw, th) = (Math.Clamp(target.Width, 1, sw), Math.Clamp(target.Height, 1, sh));
        var stride = sw * 4;
        var pixels = new byte[stride * sh];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            source.CopyPixels(new PixelRect(0, 0, sw, sh), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        var result = new byte[tw * th * 4];
        Parallel.For(0, th, ty =>
        {
            int y0 = (int)((long)ty * sh / th), y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * sh / th));
            for (var tx = 0; tx < tw; tx++)
            {
                int x0 = (int)((long)tx * sw / tw), x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * sw / tw));
                double b = 0, g = 0, r = 0, a = 0;
                for (var y = y0; y < y1; y++)
                for (int x = x0, i = y * stride + x0 * 4; x < x1; x++, i += 4)
                {
                    double al = pixels[i + 3];
                    b += pixels[i] * al;
                    g += pixels[i + 1] * al;
                    r += pixels[i + 2] * al;
                    a += al;
                }
                var o = (ty * tw + tx) * 4;
                var count = (y1 - y0) * (x1 - x0);
                if (a > 0)
                {
                    result[o] = (byte)Math.Round(b / a);
                    result[o + 1] = (byte)Math.Round(g / a);
                    result[o + 2] = (byte)Math.Round(r / a);
                }
                result[o + 3] = (byte)Math.Round(a / count);
            }
        });
        return FromBgra(result, tw, th);
    }
}
