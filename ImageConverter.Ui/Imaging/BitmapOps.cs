using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ImageConverter.Imaging;

/// <summary>
/// Brücke von ImageMagick-Pixeln zur Avalonia-Anzeige. Alle eigentlichen
/// Bildoperationen liegen im Core (ImageOps) und laufen auf dem echten Bild.
/// </summary>
public static class BitmapOps
{
    /// <summary>
    /// Baut aus rohen 8-Bit-BGRA-Pixeln (nicht vormultipliziert, wie ImageMagick sie
    /// liefert) eine anzeigbare Bitmap.
    /// </summary>
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
            // Zeilen können im Zielpuffer aufgefüllt sein
            for (var y = 0; y < height; y++)
                Marshal.Copy(bgra, y * stride, fb.Address + y * fb.RowBytes, stride);
        }

        return bitmap;
    }
}
