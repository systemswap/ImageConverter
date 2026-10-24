using ImageMagick;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>Resampling filters as offered in the user interface; see <see cref="ImageOps.ToMagick"/> for the ImageMagick
/// filter behind each one.</summary>
public enum ResampleFilter
{
    /// <summary>Lanczos with 3 lobes.</summary>
    Lanczos3,
    /// <summary>Soft cubic filter with few ringing artefacts (ImageMagick Robidoux).</summary>
    Bicubic,
    /// <summary>Catmull-Rom cubic spline.</summary>
    CatmullRom,
    /// <summary>Mitchell-Netravali cubic filter.</summary>
    Mitchell,
    /// <summary>B-spline filter.</summary>
    Spline,
    /// <summary>Bilinear (triangle) filter.</summary>
    Bilinear,
    /// <summary>Box filter.</summary>
    Box,
    /// <summary>Nearest neighbour (point sampling).</summary>
    Nearest,
}

/// <summary>
/// Image operations on ImageMagick images.
/// </summary>
/// <remarks>
/// Every operation returns a new image owned by the caller and leaves the source unchanged, so that the history can keep
/// earlier states.
/// </remarks>
public static class ImageOps
{
    // ------------------------------------------------------------- Display

    /// <summary>Returns 8-bit BGRA pixels (not premultiplied) for display.</summary>
    /// <param name="image">Source image; it is locked while being read and not modified.</param>
    /// <returns>A new array with width × height × 4 bytes.</returns>
    /// <exception cref="InvalidOperationException">ImageMagick could not provide the pixels.</exception>
    /// <remarks>
    /// Deliberately uses the pixel API instead of <c>ToByteArray(MagickFormat.Bgra)</c>: the latter modifies the source
    /// image internally, after which every resize returns only black transparent pixels (observed with Magick.NET 14.17.2).
    /// The pixel API also always returns full 8 bits, even for 1-bit files.
    /// </remarks>
    public static byte[] ToBgra(MagickImage image)
    {
        lock (image)
        {
            using var pixels = image.GetPixelsUnsafe();
            return pixels.ToByteArray("BGRA")
                   ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
        }
    }

    // ------------------------------------------------------------- Threads
    //
    // Up to three background tasks access the same image at the same time: preview, size estimate and editing/export.
    // Each works on its own copy; only copying and reading the shared source are locked against each other (the image
    // object itself is the lock). After that, each copy is used by one thread only.

    /// <summary>Creates a thread-safe copy; all operations copy via this method.</summary>
    /// <param name="source">Image to copy; it is locked during the copy.</param>
    /// <returns>A new copy owned by the caller.</returns>
    public static MagickImage CloneOf(MagickImage source)
    {
        lock (source)
            return (MagickImage)source.Clone();
    }

    /// <summary>
    /// Like <see cref="ToBgra"/>, but for the screen: images with their own profile (Adobe RGB …) are converted to sRGB so
    /// that the colours are correct. The image itself stays in its colour space.
    /// </summary>
    /// <param name="image">Source image; not modified.</param>
    /// <returns>A new array with width × height × 4 bytes in sRGB.</returns>
    public static byte[] ToDisplayBgra(MagickImage image)
    {
        IColorProfile? profile;
        lock (image) profile = image.GetColorProfile();
        if (profile is null || ImageExporter.IsSrgb(profile))
            return ToBgra(image);

        using var display = CloneOf(image);
        display.TransformColorSpace(ColorProfiles.SRGB);
        return ToBgra(display);
    }

    /// <summary>Like <see cref="ToDisplayBgra"/>, but as a 32-bit buffer at full depth (see <see cref="Pixels"/>).</summary>
    /// <param name="image">Source image; not modified.</param>
    /// <returns>A buffer from <see cref="PixelPool"/> with width × height × 4 values in sRGB, see
    /// <see cref="Pixels.FromMagick"/>.</returns>
    public static float[] ToDisplayPixels(MagickImage image)
    {
        IColorProfile? profile;
        lock (image) profile = image.GetColorProfile();
        if (profile is null || ImageExporter.IsSrgb(profile))
            return Pixels.FromMagick(image);

        using var display = CloneOf(image);
        display.TransformColorSpace(ColorProfiles.SRGB);
        return Pixels.FromMagick(display);
    }

    /// <summary>Reads 8-bit BGRA pixels back into an ImageMagick image (for example after sharpening).</summary>
    /// <param name="bgra">Pixels as B, G, R, A bytes.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>A new image owned by the caller.</returns>
    public static MagickImage FromBgra(byte[] bgra, int width, int height)
    {
        var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "BGRA"));
        return image;
    }

    // ------------------------------------------------------------- Geometry

    /// <summary>Rotates by 90 degrees.</summary>
    /// <param name="source">Source image; not modified.</param>
    /// <param name="clockwise">True for clockwise, false for counter-clockwise.</param>
    /// <returns>A new rotated image.</returns>
    public static MagickImage Rotate90(MagickImage source, bool clockwise)
    {
        var result = CloneOf(source);
        result.Rotate(clockwise ? 90 : -90);
        result.ResetPage();
        return result;
    }

    /// <summary>
    /// Rotates freely and crops in one step.
    /// </summary>
    /// <param name="source">Source image; not modified.</param>
    /// <param name="degrees">Rotation angle in degrees; positive angles rotate clockwise. At practically 0° the image is only cropped.</param>
    /// <param name="x">Left edge of the frame in pixels.</param>
    /// <param name="y">Top edge of the frame in pixels.</param>
    /// <param name="width">Width of the frame in pixels.</param>
    /// <param name="height">Height of the frame in pixels.</param>
    /// <param name="radius">Corner radius in pixels, as in <see cref="Crop"/>.</param>
    /// <returns>A new image with the size of the frame.</returns>
    /// <remarks>
    /// The frame applies in the unrotated coordinate system of the preview about the image centre (see
    /// <see cref="RotationGeometry"/>). It should lie entirely within the rotated image; otherwise the corners outside
    /// stay empty (transparent).
    /// </remarks>
    public static MagickImage RotateAndCrop(MagickImage source, double degrees, int x, int y, int width, int height,
        double radius)
    {
        degrees = RotationGeometry.Normalize(degrees);
        if (Math.Abs(degrees) < 1e-9) return Crop(source, x, y, width, height, radius);

        var hadAlpha = source.HasAlpha;
        using var rotated = CloneOf(source);
        // Only the edge pixels outside the frame touch the background.
        rotated.BackgroundColor = MagickColors.Transparent;
        rotated.Rotate(degrees);
        rotated.ResetPage();

        // The centre of rotation stays the centre: the canvas grows equally on all sides.
        var dx = (rotated.Width - (double)source.Width) / 2;
        var dy = (rotated.Height - (double)source.Height) / 2;
        var result = Crop(rotated, (int)Math.Round(x + dx), (int)Math.Round(y + dy), width, height, radius);

        // Image without transparency: return it without an alpha channel as well (otherwise a PNG, for example, becomes
        // larger).
        if (!hadAlpha && radius < 0.5) result.Alpha(AlphaOption.Off);
        return result;
    }

    /// <summary>
    /// Corrects perspective: the quadrilateral becomes an upright rectangle of the size given by
    /// <see cref="PerspectiveGeometry.TargetSize"/>, for example a sheet of paper photographed at an angle. Only the
    /// content of the quadrilateral is kept.
    /// </summary>
    /// <param name="source">Source image; not modified.</param>
    /// <param name="quad">Corners in image pixels: top left, top right, bottom right, bottom left.</param>
    /// <returns>A new image with the corrected content.</returns>
    /// <exception cref="ArgumentException">The corners do not form a usable quadrilateral (see
    /// <see cref="PerspectiveGeometry.IsValid"/>).</exception>
    public static MagickImage Straighten(MagickImage source, IReadOnlyList<(double X, double Y)> quad)
    {
        if (!PerspectiveGeometry.IsValid(quad))
            throw new ArgumentException("Die vier Ecken bilden kein brauchbares Viereck.", nameof(quad));

        var (w, h) = PerspectiveGeometry.TargetSize(quad);
        var hadAlpha = source.HasAlpha;
        var result = CloneOf(source);
        result.VirtualPixelMethod = VirtualPixelMethod.Transparent;

        // Pairs source → target; ImageMagick maps every target pixel back (good smoothing, no staircase effect).
        // Pixel centres: the corner of the quadrilateral is the outer edge, so in the target it is (0,0) to (w,h).
        double[] corners = [0, 0, w, 0, w, h, 0, h];
        var args = new double[16];
        for (var i = 0; i < 4; i++)
        {
            args[i * 4] = quad[i].X;
            args[i * 4 + 1] = quad[i].Y;
            args[i * 4 + 2] = corners[i * 2];
            args[i * 4 + 3] = corners[i * 2 + 1];
        }

        result.Distort(new DistortSettings(DistortMethod.Perspective) { Viewport = new MagickGeometry(0, 0, (uint)w, (uint)h) }, args);
        result.ResetPage();

        // The quadrilateral lies within the image (the handles stay inside), so an image loaded without transparency is
        // returned without an alpha channel.
        if (!hadAlpha) result.Alpha(AlphaOption.Off);
        return result;
    }

    /// <summary>Mirrors the image horizontally (left ↔ right).</summary>
    /// <param name="source">Source image; not modified.</param>
    /// <returns>A new mirrored image.</returns>
    public static MagickImage FlipHorizontal(MagickImage source)
    {
        var result = CloneOf(source);
        result.Flop();
        return result;
    }

    /// <summary>
    /// Crops the area. With a radius, the corners are rounded and everything outside the rounding becomes transparent;
    /// the result stays rectangular.
    /// </summary>
    /// <param name="source">Source image; not modified.</param>
    /// <param name="x">Left edge in pixels; clamped to the image.</param>
    /// <param name="y">Top edge in pixels; clamped to the image.</param>
    /// <param name="width">Width in pixels; clamped to 1 … the remaining width.</param>
    /// <param name="height">Height in pixels; clamped to 1 … the remaining height.</param>
    /// <param name="radius">Corner radius in pixels; below 0.5 the corners stay square. Rounding as in the preview:
    /// circular up to half the shorter side, then elliptical up to the full ellipse (a circle for a square).</param>
    /// <returns>A new cropped image; with rounded corners it has an alpha channel.</returns>
    public static MagickImage Crop(MagickImage source, int x, int y, int width, int height, double radius)
    {
        x = Math.Clamp(x, 0, (int)source.Width - 1);
        y = Math.Clamp(y, 0, (int)source.Height - 1);
        width = Math.Clamp(width, 1, (int)source.Width - x);
        height = Math.Clamp(height, 1, (int)source.Height - y);

        var result = CloneOf(source);
        result.Crop(new MagickGeometry(x, y, (uint)width, (uint)height));
        result.ResetPage();

        var rx = Math.Min(radius, width / 2.0);
        var ry = Math.Min(radius, height / 2.0);
        if (rx < 0.5 || ry < 0.5) return result;

        // Mask: white, alpha = coverage of the rounded shape (anti-aliased).
        var coverage = RoundedCoverage(width, height, rx, ry);
        var maskPixels = new byte[width * height * 4];
        for (var i = 0; i < coverage.Length; i++)
        {
            var o = i * 4;
            maskPixels[o] = maskPixels[o + 1] = maskPixels[o + 2] = 255;
            maskPixels[o + 3] = coverage[i];
        }

        using var mask = new MagickImage();
        mask.ReadPixels(maskPixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGBA"));

        // DstIn: existing transparency is kept and multiplied by the shape.
        result.Alpha(AlphaOption.Set);
        result.Composite(mask, CompositeOperator.DstIn);
        return result;
    }

    /// <summary>Coverage 0–255 per pixel for a rectangle with elliptical corners (4×4 supersampling).</summary>
    internal static byte[] RoundedCoverage(int width, int height, double rx, double ry)
    {
        var alpha = new byte[width * height];
        Array.Fill(alpha, (byte)255);

        var cw = (int)Math.Ceiling(rx);
        var ch = (int)Math.Ceiling(ry);

        // Compute one corner and mirror it to all four corners.
        var corner = new byte[cw * ch];
        Parallel.For(0, ch, ComputeLimits.Options, ly =>
        {
            for (var lx = 0; lx < cw; lx++)
            {
                var inside = 0;
                for (var sy = 0; sy < 4; sy++)
                for (var sx = 0; sx < 4; sx++)
                {
                    var px = lx + (sx + 0.5) / 4;
                    var py = ly + (sy + 0.5) / 4;
                    if (px >= rx || py >= ry) { inside++; continue; }
                    var dx = (px - rx) / rx;
                    var dy = (py - ry) / ry;
                    if (dx * dx + dy * dy <= 1) inside++;
                }
                corner[ly * cw + lx] = (byte)(inside * 255 / 16);
            }
        });

        for (var ly = 0; ly < ch; ly++)
        for (var lx = 0; lx < cw; lx++)
        {
            var a = corner[ly * cw + lx];
            if (a == 255) continue;
            int left = lx, right = width - 1 - lx, top = ly, bottom = height - 1 - ly;
            alpha[top * width + left] = Math.Min(alpha[top * width + left], a);
            alpha[top * width + right] = Math.Min(alpha[top * width + right], a);
            alpha[bottom * width + left] = Math.Min(alpha[bottom * width + left], a);
            alpha[bottom * width + right] = Math.Min(alpha[bottom * width + right], a);
        }

        return alpha;
    }

    // ------------------------------------------------------------- Quality

    /// <summary>Maps a resampling filter to the ImageMagick filter type.</summary>
    /// <param name="filter">Filter from the user interface.</param>
    /// <returns>The ImageMagick filter; Lanczos for unknown values.</returns>
    public static FilterType ToMagick(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Lanczos3 => FilterType.Lanczos,
        // "Bicubic" as a soft cubic filter with little ringing (Keys family).
        ResampleFilter.Bicubic => FilterType.Robidoux,
        ResampleFilter.CatmullRom => FilterType.Catrom,
        ResampleFilter.Mitchell => FilterType.Mitchell,
        ResampleFilter.Spline => FilterType.Spline,
        ResampleFilter.Bilinear => FilterType.Triangle,
        ResampleFilter.Box => FilterType.Box,
        ResampleFilter.Nearest => FilterType.Point,
        _ => FilterType.Lanczos,
    };

    /// <summary>
    /// Scales to exactly <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    /// <param name="source">Source image; not modified.</param>
    /// <param name="width">Target width in pixels.</param>
    /// <param name="height">Target height in pixels.</param>
    /// <param name="filter">Resampling filter.</param>
    /// <param name="gammaCorrect">Compute in linear light, the way the eye mixes brightness, so that fine bright structures
    /// do not become too dark when downscaling.</param>
    /// <returns>A new image; a plain copy if the size is unchanged.</returns>
    /// <remarks>sRGB images from <see cref="ParallelMinPixels"/> pixels upwards are scaled in parallel strips.</remarks>
    public static MagickImage Resize(MagickImage source, int width, int height, ResampleFilter filter, bool gammaCorrect)
    {
        bool sameSize, large;
        lock (source)
        {
            sameSize = source.Width == width && source.Height == height;
            large = (long)source.Width * source.Height >= ParallelMinPixels && source.ColorSpace == ColorSpace.sRGB;
        }
        if (!sameSize && large)
            return ResizeInStrips(source, width, height, ToMagick(filter), gammaCorrect);

        var result = CloneOf(source);
        if (sameSize) return result;

        if (gammaCorrect) result.ColorSpace = ColorSpace.RGB;   // sRGB → linear
        result.FilterType = ToMagick(filter);
        result.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        if (gammaCorrect)
        {
            result.ColorSpace = ColorSpace.sRGB;                 // back
            // The detour via linear removes the colour profile (for example Adobe RGB), but the pixels are back in the
            // original colour space, so the profile is restored.
            ImageExporter.CopyProfiles(source, result);
        }

        return result;
    }

    // ------------------------------------------------------- Parallel scaling
    //
    // This path does not depend on ImageMagick using several cores itself (which ImageMagick package is used, single-core
    // or with OpenMP, is set in Directory.Packages.props). With a single-core ImageMagick, downscaling a 24-megapixel
    // photo gamma-correctly to 2048 px took 2.8 s (1.6 s of it the conversion to linear, 1 s the scaling). Scaling is
    // separable: first horizontally (each row on its own), then vertically (each column on its own). Therefore row strips
    // and then column strips are computed in parallel, without overlap, because the size in the other direction stays
    // the same. The sRGB ↔ linear conversion uses a table with the same formulas as ImageMagick.

    /// <summary>Below this pixel count, splitting does not pay off and the simple path is used.</summary>
    internal const long ParallelMinPixels = 1_000_000;

    private const int Quantum = 65535;   // Q16
    private static readonly Lazy<(ushort[] ToLinear, ushort[] ToSrgb)> GammaTables = new(() =>
    {
        var toLinear = new ushort[Quantum + 1];
        var toSrgb = new ushort[Quantum + 1];
        for (var i = 0; i <= Quantum; i++)
        {
            var x = (double)i / Quantum;
            // as DecodePixelGamma / EncodePixelGamma in ImageMagick (gamma-private.h)
            var lin = x <= 0.0404482362771076 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            var srgb = x <= 0.0031306684425005883 ? 12.92 * x : 1.055 * Math.Pow(x, 1.0 / 2.4) - 0.055;
            toLinear[i] = (ushort)Math.Clamp(Math.Round(lin * Quantum), 0, Quantum);
            toSrgb[i] = (ushort)Math.Clamp(Math.Round(srgb * Quantum), 0, Quantum);
        }
        return (toLinear, toSrgb);
    });

    // Splitting and joining via ImageMagick (CloneArea, Append) costs about 1 s on one core by itself, so the strips are
    // handled as pixel arrays.
    private static MagickImage ResizeInStrips(MagickImage image, int width, int height, FilterType filter, bool gammaCorrect)
    {
        // The source is often the shared original (see CloneOf), so it is read only under the lock.
        int sw, sh;
        uint depth;
        string map;
        lock (image)
        {
            sw = (int)image.Width;
            sh = (int)image.Height;
            depth = image.Depth;
            map = image.HasAlpha ? "RGBA" : "RGB";
        }
        var ch = map.Length;
        var (toLinear, toSrgb) = gammaCorrect ? GammaTables.Value : default;

        // 1) Row strips: to linear, horizontally to the target width. Each strip reads only its own rows (under the lock),
        // so that no copy of the whole image is needed up front (168 MB for the panorama, at every first preview of an
        // image).
        var wide = new ushort[(long)width * sh * ch];
        Parallel.ForEach(Strips(sh), ComputeLimits.Options, r =>
        {
            ushort[] rows;
            lock (image)
            {
                using var pixels = image.GetPixelsUnsafe();
                rows = pixels.ToShortArray(0, r.Start, (uint)sw, (uint)r.Length, map)
                       ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
            }
            if (toLinear is not null) MapColor(rows.AsSpan(), ch, toLinear);
            var scaled = Scale(rows, 0, rows.Length, sw, r.Length, width, r.Length, map, filter);
            scaled.CopyTo(wide, (long)r.Start * width * ch);
        });

        // 2) Column strips: vertically to the target height, back to sRGB.
        var result = new ushort[(long)width * height * ch];
        Parallel.ForEach(Strips(width), ComputeLimits.Options, c =>
        {
            var row = c.Length * ch;
            var column = new ushort[row * sh];
            for (var y = 0; y < sh; y++)
                Array.Copy(wide, ((long)y * width + c.Start) * ch, column, (long)y * row, row);

            var scaled = Scale(column, 0, column.Length, c.Length, sh, c.Length, height, map, filter);
            if (toSrgb is not null) MapColor(scaled, ch, toSrgb);
            for (var y = 0; y < height; y++)
                Array.Copy(scaled, (long)y * row, result, ((long)y * width + c.Start) * ch, row);
        });

        var resized = new MagickImage();
        resized.ReadPixels(result, new PixelReadSettings((uint)width, (uint)height, StorageType.Quantum, map));
        resized.Depth = depth;                     // an 8-bit source stays 8 bits (for example as PNG)
        ImageExporter.CopyProfiles(image, resized);  // EXIF, ICC … as for sharpening
        return resized;
    }

    /// <summary>Splits a length into as many pieces as there are cores (at least 64 pixels per piece, at most 32
    /// pieces).</summary>
    private static List<(int Start, int Length)> Strips(int total)
    {
        var count = Math.Clamp(Math.Min(Environment.ProcessorCount, total / 64), 1, 32);
        var list = new List<(int, int)>(count);
        for (var i = 0; i < count; i++)
        {
            int start = (int)((long)total * i / count), end = (int)((long)total * (i + 1) / count);
            list.Add((start, end - start));
        }
        return list;
    }

    /// <summary>Scales one strip as a separate image and returns its pixels.</summary>
    private static ushort[] Scale(ushort[] data, int offset, int count, int w, int h,
        int toWidth, int toHeight, string map, FilterType filter)
    {
        using var strip = new MagickImage();
        strip.ReadPixels(data, (uint)offset, (uint)count,
            new PixelReadSettings((uint)w, (uint)h, StorageType.Quantum, map));
        strip.FilterType = filter;
        strip.Resize(new MagickGeometry((uint)toWidth, (uint)toHeight) { IgnoreAspectRatio = true });
        using var pixels = strip.GetPixelsUnsafe();
        return pixels.ToShortArray(map) ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
    }

    /// <summary>Applies a table to red, green and blue (alpha is unchanged).</summary>
    private static void MapColor(Span<ushort> data, int channels, ushort[] table)
    {
        for (var p = 0; p < data.Length; p += channels)
        {
            data[p] = table[data[p]];
            data[p + 1] = table[data[p + 1]];
            data[p + 2] = table[data[p + 2]];
        }
    }

    /// <summary>Reduces noise with ImageMagick's wavelet denoise.</summary>
    /// <param name="source">Source image; not modified.</param>
    /// <param name="strength">Strength 0–3 as on the slider; the threshold is <paramref name="strength"/> × 3 percent.
    /// At 0.01 or below, a plain copy is returned.</param>
    /// <returns>A new image.</returns>
    public static MagickImage Denoise(MagickImage source, double strength)
    {
        var result = CloneOf(source);
        if (strength > 0.01)
            result.WaveletDenoise(new Percentage(strength * 3), 0.0);
        return result;
    }
}
