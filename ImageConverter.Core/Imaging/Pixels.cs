using ImageMagick;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Conversions for the pixel buffer of the image pipeline: 32-bit floating point per channel (B, G, R, A), values 0–1,
/// sRGB-encoded.
/// </summary>
/// <remarks>
/// Values are not rounded between steps; they are reduced to 8 bits only for display and when saving to 8-bit formats,
/// once and with dither, so that no new banding appears.
/// The buffer is deliberately sRGB-encoded rather than linear: curves, sharpening, shadows/highlights and vibrance are
/// tuned to encoded values; the physical steps (dehaze, scaling) work in linear light internally. In 32 bits both are
/// equally precise, so storing linear values would only mean converting twice.
/// Values from Magick (Q16) are taken directly at 16 bits, without a detour via 8 bits.
/// </remarks>
public static class Pixels
{
    private const float Q16 = 65535f;

    /// <summary>Converts 8-bit BGRA bytes into a new floating-point buffer.</summary>
    /// <param name="bgra">Pixels as B, G, R, A bytes.</param>
    /// <returns>A new buffer of the same length with values 0–1.</returns>
    public static float[] FromBytes(byte[] bgra)
    {
        var px = new float[bgra.Length];
        Parallel.For(0, bgra.Length / 4, ComputeLimits.Options, p =>
        {
            var i = p * 4;
            px[i] = bgra[i] / 255f;
            px[i + 1] = bgra[i + 1] / 255f;
            px[i + 2] = bgra[i + 2] / 255f;
            px[i + 3] = bgra[i + 3] / 255f;
        });
        return px;
    }

    /// <summary>Reads the pixels of a Magick image at full depth (Q16). The image is not modified.</summary>
    /// <param name="image">Source image; it is locked while being read.</param>
    /// <returns>A buffer from <see cref="PixelPool"/> with width × height × 4 values (B, G, R, A); the caller may return
    /// it to the pool after use.</returns>
    /// <exception cref="InvalidOperationException">ImageMagick could not provide the pixels.</exception>
    public static float[] FromMagick(MagickImage image)
    {
        lock (image)
        {
            int w = (int)image.Width, h = (int)image.Height;
            var px = PixelPool.Rent(w * h * 4);
            using var pixels = image.GetPixelsUnsafe();
            // Read in strips: a 16-bit intermediate buffer of the whole image would take another 230 MB at 28 MP.
            for (var y0 = 0; y0 < h; y0 += StripRows)
            {
                var rows = Math.Min(StripRows, h - y0);
                var data = pixels.ToShortArray(0, y0, (uint)w, (uint)rows, "BGRA")
                           ?? throw new InvalidOperationException("Pixel konnten nicht gelesen werden.");
                var offset = y0 * w * 4;
                Parallel.For(0, rows, ComputeLimits.Options, r =>
                {
                    for (int i = r * w * 4, end = i + w * 4; i < end; i++) px[offset + i] = data[i] / Q16;
                });
            }
            return px;
        }
    }

    /// <summary>Rows per strip when reading from and writing to Magick.</summary>
    private const int StripRows = 256;

    /// <summary>Creates a new Magick image with 16 bits per channel from the buffer. It is written in strips, without a
    /// 16-bit copy of the whole image.</summary>
    /// <param name="px">Pixel buffer (B, G, R, A, values 0–1); values outside are clamped.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="alpha">Whether the result has an alpha channel; otherwise the alpha values are discarded.</param>
    /// <returns>A new image owned by the caller.</returns>
    public static MagickImage ToMagick16(float[] px, int width, int height, bool alpha)
    {
        var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height);
        if (alpha) image.Alpha(AlphaOption.Set);
        image.Depth = 16;
        using var pixels = image.GetPixelsUnsafe();
        var channels = (int)pixels.Channels;
        // Channel order in the image (usually R, G, B[, A]) → source in the buffer (B, G, R, A).
        var from = new int[channels];
        for (var c = 0; c < channels; c++) from[c] = 3;
        foreach (var (channel, source) in new[] { (PixelChannel.Blue, 0), (PixelChannel.Green, 1), (PixelChannel.Red, 2), (PixelChannel.Alpha, 3) })
            if (pixels.GetChannelIndex(channel) is uint index && index < channels) from[index] = source;
        var strip = new ushort[StripRows * width * channels];
        for (var y0 = 0; y0 < height; y0 += StripRows)
        {
            var rows = Math.Min(StripRows, height - y0);
            Parallel.For(0, rows, ComputeLimits.Options, r =>
            {
                for (var x = 0; x < width; x++)
                {
                    var i = ((y0 + r) * width + x) * 4;
                    var o = (r * width + x) * channels;
                    for (var c = 0; c < channels; c++)
                    {
                        var v = px[i + from[c]] * Q16;
                        strip[o + c] = (ushort)(v <= 0 ? 0 : v >= Q16 ? 65535 : v + 0.5f);
                    }
                }
            });
            pixels.SetArea(0, y0, (uint)width, (uint)rows, rows == StripRows ? strip : strip[..(rows * width * channels)]);
        }
        return image;
    }

    /// <summary>Converts the buffer to 8-bit BGRA bytes.</summary>
    /// <param name="px">Pixel buffer (B, G, R, A, values 0–1).</param>
    /// <param name="dither">Adds triangular noise of ±1 level before rounding (TPDF) to the colour channels, so that a
    /// smooth gradient between two 8-bit levels becomes fine grain instead of a visible edge. The noise is fixed per
    /// pixel position, so the preview does not flicker. Alpha is only rounded.</param>
    /// <returns>A new byte array of the same length.</returns>
    public static byte[] ToBytes(float[] px, bool dither)
    {
        var bgra = new byte[px.Length];
        Parallel.For(0, px.Length / 4, ComputeLimits.Options, p =>
        {
            var i = p * 4;
            for (var c = 0; c < 3; c++)
            {
                var v = px[i + c] * 255;
                if (dither) v += Dither(v, (uint)(i + c));
                bgra[i + c] = ToByte(v);
            }
            bgra[i + 3] = ToByte(px[i + 3] * 255);
        });
        return bgra;
    }

    /// <summary>
    /// Prepares a 16-bit image for an 8-bit format: every colour channel is set to an 8-bit level with dither, in place;
    /// profiles and metadata are kept. Magick's subsequent reduction to 8 bits is then lossless.
    /// </summary>
    /// <param name="image">Image to modify; its depth is set to 8 afterwards.</param>
    /// <exception cref="InvalidOperationException">ImageMagick could not provide the pixels.</exception>
    public static void DitherTo8Bit(MagickImage image)
    {
        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.GetValues() ?? throw new InvalidOperationException("Pixel konnten nicht gelesen werden.");
        var channels = (int)pixels.Channels;
        // Colour channels: the first one for greyscale, otherwise the first three; alpha is only rounded.
        var colour = channels >= 3 ? 3 : 1;
        Parallel.For(0, data.Length / channels, ComputeLimits.Options, p =>
        {
            var i = p * channels;
            for (var c = 0; c < channels; c++)
            {
                var v = data[i + c] / 257f;   // 65535 / 255
                if (c < colour) v += Dither(v, (uint)(i + c));
                data[i + c] = (ushort)(ToByte(v) * 257);
            }
        });
        pixels.SetPixels(data);
        image.Depth = 8;
    }

    private static byte ToByte(float v) => (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v + 0.5f);

    /// <summary>
    /// TPDF noise (−1 … +1 level) for a value in 8-bit levels. Values that already lie exactly on a level stay unchanged,
    /// so an unedited 8-bit image does not change; black and white stay pure (the noise fades out towards both ends).
    /// </summary>
    private static float Dither(float v, uint key)
    {
        if (MathF.Abs(v - MathF.Round(v)) < 1f / 512) return 0;
        return (Noise(key) + Noise(key ^ 0x9E3779B9u) - 1) * Math.Clamp(Math.Min(v, 255 - v), 0, 1);
    }

    /// <summary>Uniformly distributed number in [0, 1) from an integer key (stateless hash).</summary>
    internal static float Noise(uint key)
    {
        key ^= key >> 16; key *= 0x7FEB352Du;
        key ^= key >> 15; key *= 0x846CA68Bu;
        key ^= key >> 16;
        return (key >> 8) * (1f / (1 << 24));
    }
}
