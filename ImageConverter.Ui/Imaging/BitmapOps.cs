using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ImageConverter.Imaging;

/// <summary>
/// Bildoperationen auf der Vorschau-Bitmap. Übergangslösung, bis die eigentliche
/// Bildbibliothek (NetVips/Magick.NET) im Core steckt — dann wandern diese
/// Operationen dorthin und laufen beim Export auf dem Original.
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

    /// <summary>Um 90° drehen, im oder gegen den Uhrzeigersinn. Verlustfrei, keine Interpolation.</summary>
    public static Bitmap Rotate90(Bitmap source, bool clockwise)
    {
        var w = source.PixelSize.Width;
        var h = source.PixelSize.Height;

        // Avalonia-Matrix: x' = x·M11 + y·M21 + M31,  y' = x·M12 + y·M22 + M32
        var m = clockwise
            ? new Matrix(0, 1, -1, 0, h, 0)   // x' = h − y,  y' = x
            : new Matrix(0, -1, 1, 0, 0, w);  // x' = y,      y' = w − x

        return Transform(source, new PixelSize(h, w), m);
    }

    /// <summary>Waagerecht spiegeln (links ↔ rechts).</summary>
    public static Bitmap FlipHorizontal(Bitmap source) =>
        Transform(source, source.PixelSize, new Matrix(-1, 0, 0, 1, source.PixelSize.Width, 0));

    private static Bitmap Transform(Bitmap source, PixelSize targetSize, Matrix matrix)
    {
        var target = new RenderTargetBitmap(targetSize, new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        using (ctx.PushTransform(matrix))
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            ctx.DrawImage(source, new Rect(source.Size),
                new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height));
        }

        return target;
    }

    /// <summary>
    /// Schneidet <paramref name="selection"/> (in Bildpixeln) aus. Bei einem Radius
    /// größer 0 werden die Ecken abgerundet; alles außerhalb der Rundung ist im
    /// Ergebnis transparent. Das Ergebnis bleibt rechteckig, so groß wie die Auswahl.
    /// </summary>
    public static Bitmap Crop(Bitmap source, Rect selection, double radius)
    {
        var x = Math.Clamp((int)Math.Round(selection.X), 0, source.PixelSize.Width - 1);
        var y = Math.Clamp((int)Math.Round(selection.Y), 0, source.PixelSize.Height - 1);
        var w = Math.Clamp((int)Math.Round(selection.Width), 1, source.PixelSize.Width - x);
        var h = Math.Clamp((int)Math.Round(selection.Height), 1, source.PixelSize.Height - y);

        // Quellrechteck in den Einheiten von Bitmap.Size (DIPs) — bei Bildern mit
        // DPI-Angabe ungleich 96 weichen die von den Pixeln ab.
        var sx = source.Size.Width / source.PixelSize.Width;
        var sy = source.Size.Height / source.PixelSize.Height;
        var src = new Rect(x * sx, y * sy, w * sx, h * sy);
        var dest = new Rect(0, 0, w, h);

        // Startet vollständig transparent
        var target = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        {
            // Gleiche Rundungslogik wie in der Vorschau (CropView): kreisrund, bis
            // die kürzere Seite aufgebraucht ist, danach elliptisch bis zur Ellipse.
            var rx = Math.Min(radius, w / 2.0);
            var ry = Math.Min(radius, h / 2.0);

            using (rx > 0.5 && ry > 0.5
                       ? ctx.PushGeometryClip(new RectangleGeometry(dest) { RadiusX = rx, RadiusY = ry })
                       : default(DrawingContext.PushedState?))
            {
                ctx.DrawImage(source, src, dest);
            }
        }

        return target;
    }
}
